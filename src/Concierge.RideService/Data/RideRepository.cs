using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using Concierge.Contracts;
using Npgsql;

namespace Concierge.RideService.Data;

public sealed class RideRepository(NpgsqlDataSource dataSource, TimeZoneInfo hotelTimeZone)
{
    public async Task<AdmissionTransaction> BeginAdmissionAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var lockKey = GetLockKey(idempotencyKey);
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock($1);", connection, transaction);
            command.Parameters.AddWithValue(lockKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new AdmissionTransaction(this, connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<RideStoredBooking?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, idempotency_key, guest_id, stay_id, checkout_date,
                   pickup_name, pickup_latitude, pickup_longitude,
                   dropoff_name, dropoff_latitude, dropoff_longitude, is_immediate,
                   pickup_at_utc, booked_at_utc, status, route_duration_seconds,
                   stay_workflow_id, driver_name, driver_car, driver_plate,
                   picked_up_at_utc, completed_at_utc, cancelled_at_utc
            FROM rides
            WHERE idempotency_key = $1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadStoredBooking(reader) : null;
    }

    /// <summary>
    /// Reads the ride an earlier, unconfirmed store attempt may have committed for this command.
    /// Returns null when nothing was stored or the stored intent belongs to a different request.
    /// </summary>
    public async Task<RideResponse?> FindAdmittedBookingAsync(
        RideBookingCommand booking,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await FindByIdempotencyKeyAsync(
            booking.IdempotencyKey,
            connection,
            transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return existing is not null && Matches(existing.Intent, booking) ? existing.Response : null;
    }

    public async Task<RideResponse> PersistBookingAsync(
        RideBookingCommand booking,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO rides (
                id, idempotency_key, guest_id, stay_id, checkout_date,
                pickup_name, pickup_latitude, pickup_longitude,
                dropoff_name, dropoff_latitude, dropoff_longitude,
                is_immediate, pickup_at_utc, booked_at_utc, route_duration_seconds,
                stay_workflow_id, status, created_at_utc)
            VALUES (
                $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14,
                $15, $16, 'Booked', $14)
            ON CONFLICT (idempotency_key) DO NOTHING
            RETURNING id;
            """,
            connection,
            transaction);
        AddBookingParameters(insert, booking);
        var inserted = await insert.ExecuteScalarAsync(cancellationToken);
        RideResponse result;
        if (inserted is null)
        {
            var existing = await FindByIdempotencyKeyAsync(
                booking.IdempotencyKey,
                connection,
                transaction,
                cancellationToken);
            if (existing is null)
            {
                throw new InvalidOperationException(
                    "Ride idempotency uniqueness conflicted without a persisted ride.");
            }

            if (!Matches(existing.Intent, booking))
            {
                throw RideErrors.IdempotencyConflict();
            }

            result = existing.Response;
        }
        else
        {
            await using var eventInsert = new NpgsqlCommand(
                """
                INSERT INTO ride_events (ride_id, guest_id, sequence, status, details, occurred_at_utc)
                VALUES ($1, $2, 1, 'Booked', '{}'::jsonb, $3);
                """,
                connection,
                transaction);
            eventInsert.Parameters.AddWithValue(booking.Id);
            eventInsert.Parameters.AddWithValue(booking.GuestId);
            eventInsert.Parameters.AddWithValue(booking.BookedAtUtc);
            await eventInsert.ExecuteNonQueryAsync(cancellationToken);

            result = ToResponse(booking, "Booked");
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<RideTransitionResult> TransitionAsync(
        RideTransitionCommand transition,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var ride = await ReadTransitionStateAsync(
            transition.RideId, connection, transaction, cancellationToken);
        if (ride is null)
        {
            throw new InvalidOperationException($"Ride '{transition.RideId}' was not found.");
        }

        var priorEvent = await ReadEventAsync(
            transition.RideId, transition.Sequence, connection, transaction, cancellationToken);
        if (priorEvent is not null)
        {
            if (!string.Equals(priorEvent.Status, transition.TargetStatus, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Ride event sequence {transition.Sequence} conflicts with the requested transition.");
            }

            await transaction.CommitAsync(cancellationToken);
            return new RideTransitionResult(
                true,
                ride.Status,
                ToActivityEntry(
                    ride, transition.Sequence, transition.TargetStatus, priorEvent.OccurredAtUtc));
        }

        if (!string.Equals(ride.Status, transition.ExpectedStatus, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return new RideTransitionResult(false, ride.Status, null);
        }

        await using (var update = new NpgsqlCommand(
            """
            UPDATE rides
            SET status = $2,
                driver_name = COALESCE($3, driver_name),
                driver_car = COALESCE($4, driver_car),
                driver_plate = COALESCE($5, driver_plate),
                picked_up_at_utc = CASE WHEN $2 = 'PickedUp' THEN $6 ELSE picked_up_at_utc END,
                completed_at_utc = CASE WHEN $2 = 'Completed' THEN $6 ELSE completed_at_utc END,
                cancelled_at_utc = CASE WHEN $2 = 'Cancelled' THEN $6 ELSE cancelled_at_utc END
            WHERE id = $1;
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue(transition.RideId);
            update.Parameters.AddWithValue(transition.TargetStatus);
            update.Parameters.AddWithValue((object?)transition.DriverName ?? DBNull.Value);
            update.Parameters.AddWithValue((object?)transition.Car ?? DBNull.Value);
            update.Parameters.AddWithValue((object?)transition.Plate ?? DBNull.Value);
            update.Parameters.AddWithValue(transition.ChangedAtUtc.ToUniversalTime());
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        var entry = ToActivityEntry(
            ride with
            {
                DriverName = transition.DriverName ?? ride.DriverName,
                Car = transition.Car ?? ride.Car,
                Plate = transition.Plate ?? ride.Plate,
            },
            transition.Sequence,
            transition.TargetStatus,
            transition.ChangedAtUtc);
        await InsertEventAsync(
            transition.RideId,
            ride.GuestId,
            transition.Sequence,
            transition.TargetStatus,
            transition.ChangedAtUtc,
            entry,
            connection,
            transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RideTransitionResult(true, transition.TargetStatus, entry);
    }

    public async Task<RideCancellationResult> CancelAsync(
        Guid rideId,
        DateTimeOffset cancelledAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var ride = await ReadTransitionStateAsync(rideId, connection, transaction, cancellationToken);
        if (ride is null)
        {
            throw new InvalidOperationException($"Ride '{rideId}' was not found.");
        }

        if (string.Equals(ride.Status, "Cancelled", StringComparison.Ordinal))
        {
            await using var priorCancel = new NpgsqlCommand(
                """
                SELECT sequence, occurred_at_utc
                FROM ride_events
                WHERE ride_id = $1 AND status = 'Cancelled'
                ORDER BY sequence DESC
                LIMIT 1;
                """,
                connection,
                transaction);
            priorCancel.Parameters.AddWithValue(rideId);
            await using var reader = await priorCancel.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Cancelled ride has no cancellation event.");
            }

            var priorSequence = reader.GetInt32(0);
            var occurredAt = reader.GetFieldValue<DateTimeOffset>(1);
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return new RideCancellationResult(
                true,
                ride.Status,
                ToActivityEntry(ride, priorSequence, "Cancelled", occurredAt),
                ride.StayWorkflowId);
        }

        if (ride.Status is not ("Booked" or "DriverAssigned"))
        {
            await transaction.CommitAsync(cancellationToken);
            return new RideCancellationResult(false, ride.Status, null);
        }

        var sequence = await GetNextSequenceAsync(rideId, connection, transaction, cancellationToken);
        await using (var update = new NpgsqlCommand(
            """
            UPDATE rides
            SET status = 'Cancelled', cancelled_at_utc = $2
            WHERE id = $1;
            """,
            connection,
            transaction))
        {
            update.Parameters.AddWithValue(rideId);
            update.Parameters.AddWithValue(cancelledAtUtc.ToUniversalTime());
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        var entry = ToActivityEntry(ride, sequence, "Cancelled", cancelledAtUtc);
        await InsertEventAsync(
            rideId,
            ride.GuestId,
            sequence,
            "Cancelled",
            cancelledAtUtc,
            entry,
            connection,
            transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RideCancellationResult(true, "Cancelled", entry, ride.StayWorkflowId);
    }

    public async Task<RideResponse?> GetOwnedAsync(
        Guid id,
        Guid guestId,
        Guid stayId,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, guest_id, stay_id, pickup_name, pickup_latitude, pickup_longitude,
                   dropoff_name, dropoff_latitude, dropoff_longitude, is_immediate,
                   pickup_at_utc, booked_at_utc, status, route_duration_seconds,
                   stay_workflow_id, driver_name, driver_car, driver_plate,
                   picked_up_at_utc, completed_at_utc, cancelled_at_utc
            FROM rides
            WHERE id = $1 AND guest_id = $2 AND stay_id = $3;
            """);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(guestId);
        command.Parameters.AddWithValue(stayId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResponse(reader) : null;
    }

    public async Task<IReadOnlyList<RideResponse>> ListOwnedAsync(
        Guid guestId,
        Guid stayId,
        bool upcoming,
        CancellationToken cancellationToken)
    {
        // Lifecycle status, not pickup time, decides the bucket: an immediate ride's pickup time
        // is its booking time, yet it stays active until the booking workflow completes it.
        await using var command = dataSource.CreateCommand(
            upcoming
                ? """
                  SELECT id, guest_id, stay_id, pickup_name, pickup_latitude, pickup_longitude,
                         dropoff_name, dropoff_latitude, dropoff_longitude, is_immediate,
                         pickup_at_utc, booked_at_utc, status, route_duration_seconds,
                         stay_workflow_id, driver_name, driver_car, driver_plate,
                         picked_up_at_utc, completed_at_utc, cancelled_at_utc
                  FROM rides
                  WHERE guest_id = $1 AND stay_id = $2
                    AND status NOT IN ('Completed', 'Cancelled')
                  ORDER BY pickup_at_utc;
                  """
                : """
                  SELECT id, guest_id, stay_id, pickup_name, pickup_latitude, pickup_longitude,
                         dropoff_name, dropoff_latitude, dropoff_longitude, is_immediate,
                         pickup_at_utc, booked_at_utc, status, route_duration_seconds,
                         stay_workflow_id, driver_name, driver_car, driver_plate,
                         picked_up_at_utc, completed_at_utc, cancelled_at_utc
                  FROM rides
                  WHERE guest_id = $1 AND stay_id = $2
                    AND status IN ('Completed', 'Cancelled')
                  ORDER BY pickup_at_utc DESC;
                  """);
        command.Parameters.AddWithValue(guestId);
        command.Parameters.AddWithValue(stayId);
        var results = new List<RideResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadResponse(reader));
        }

        return results;
    }

    /// <summary>Every ride of one stay in any status; the booking workflow id is the ride id.</summary>
    public async Task<IReadOnlyList<Guid>> ListStayRideIdsAsync(
        Guid guestId,
        Guid stayId,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id FROM rides WHERE guest_id = $1 AND stay_id = $2;");
        command.Parameters.AddWithValue(guestId);
        command.Parameters.AddWithValue(stayId);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>Hard-deletes one stay's rides and their events. Idempotent.</summary>
    public async Task DeleteStayRidesAsync(
        Guid guestId,
        Guid stayId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Lock the rides first: a transition that already holds a ride row would otherwise
        // insert an event after the event delete and fail the ride delete's foreign key.
        await using (var lockRides = new NpgsqlCommand(
            "SELECT id FROM rides WHERE guest_id = $1 AND stay_id = $2 FOR UPDATE;",
            connection,
            transaction))
        {
            lockRides.Parameters.AddWithValue(guestId);
            lockRides.Parameters.AddWithValue(stayId);
            await lockRides.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteEvents = new NpgsqlCommand(
            """
            DELETE FROM ride_events
            WHERE ride_id IN (SELECT id FROM rides WHERE guest_id = $1 AND stay_id = $2);
            """,
            connection,
            transaction))
        {
            deleteEvents.Parameters.AddWithValue(guestId);
            deleteEvents.Parameters.AddWithValue(stayId);
            await deleteEvents.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteRides = new NpgsqlCommand(
            "DELETE FROM rides WHERE guest_id = $1 AND stay_id = $2;",
            connection,
            transaction))
        {
            deleteRides.Parameters.AddWithValue(guestId);
            deleteRides.Parameters.AddWithValue(stayId);
            await deleteRides.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public DateTimeOffset ToHotelTime(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, hotelTimeZone);

    public DateTimeOffset CheckoutCutoffUtc(DateOnly checkoutDate)
    {
        var nextLocalMidnight = checkoutDate.AddDays(1)
            .ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(nextLocalMidnight, hotelTimeZone));
    }

    public static Guid GetBookingId(string idempotencyKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        return new Guid(hash.AsSpan(0, 16));
    }

    public static DateTimeOffset ToUtcMicrosecondPrecision(DateTimeOffset value)
    {
        var utcTicks = value.ToUniversalTime().Ticks;
        return new DateTimeOffset(utcTicks - utcTicks % 10, TimeSpan.Zero);
    }

    public static bool Matches(RideBookingIntent existing, RideBookingRequest requested)
    {
        if (requested.Pickup is null || requested.Dropoff is null)
        {
            return false;
        }

        if (requested.Immediate == requested.PickupAt.HasValue)
        {
            return false;
        }

        DateTimeOffset? requestedPickup = requested.Immediate
            ? null
            : requested.PickupAt is { } pickupAt
                ? ToUtcMicrosecondPrecision(pickupAt)
                : null;
        return existing.GuestId == requested.GuestId
            && existing.StayId == requested.StayId
            && existing.CheckoutDate == requested.CheckoutDate
            && string.Equals(existing.PickupName, requested.Pickup.Name, StringComparison.Ordinal)
            && existing.PickupLatitude.Equals(requested.Pickup.Latitude)
            && existing.PickupLongitude.Equals(requested.Pickup.Longitude)
            && string.Equals(existing.DropoffName, requested.Dropoff.Name, StringComparison.Ordinal)
            && existing.DropoffLatitude.Equals(requested.Dropoff.Latitude)
            && existing.DropoffLongitude.Equals(requested.Dropoff.Longitude)
            && existing.Immediate == requested.Immediate
            && existing.PickupAtUtc == requestedPickup
            && existing.RouteDurationSeconds.Equals(requested.RouteDurationSeconds);
    }

    private static bool Matches(RideBookingIntent existing, RideBookingCommand requested) =>
        existing.GuestId == requested.GuestId
        && existing.StayId == requested.StayId
        && existing.CheckoutDate == requested.CheckoutDate
        && string.Equals(existing.PickupName, requested.PickupName, StringComparison.Ordinal)
        && existing.PickupLatitude.Equals(requested.PickupLatitude)
        && existing.PickupLongitude.Equals(requested.PickupLongitude)
        && string.Equals(existing.DropoffName, requested.DropoffName, StringComparison.Ordinal)
        && existing.DropoffLatitude.Equals(requested.DropoffLatitude)
        && existing.DropoffLongitude.Equals(requested.DropoffLongitude)
        && existing.Immediate == requested.Immediate
        && existing.PickupAtUtc == (requested.Immediate ? null : requested.PickupAtUtc)
        && existing.RouteDurationSeconds.Equals(requested.RouteDurationSeconds);

    private static async Task<RideTransitionState?> ReadTransitionStateAsync(
        Guid rideId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, guest_id, pickup_name, dropoff_name, pickup_at_utc,
                   status, driver_name, driver_car, driver_plate, stay_workflow_id
            FROM rides
            WHERE id = $1
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(rideId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RideTransitionState(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4).ToUniversalTime(),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9));
    }

    private static async Task<RideEvent?> ReadEventAsync(
        Guid rideId,
        int sequence,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT status, occurred_at_utc
            FROM ride_events
            WHERE ride_id = $1 AND sequence = $2;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(rideId);
        command.Parameters.AddWithValue(sequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RideEvent(
                reader.GetString(0),
                reader.GetFieldValue<DateTimeOffset>(1).ToUniversalTime())
            : null;
    }

    private static async Task<int> GetNextSequenceAsync(
        Guid rideId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(MAX(sequence), 0) + 1 FROM ride_events WHERE ride_id = $1;",
            connection,
            transaction);
        command.Parameters.AddWithValue(rideId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertEventAsync(
        Guid rideId,
        Guid guestId,
        int sequence,
        string status,
        DateTimeOffset occurredAtUtc,
        RideActivityEntry entry,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(new
        {
            entry.DriverName,
            entry.Car,
            entry.Plate,
        });
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ride_events (
                ride_id, guest_id, sequence, status, details, occurred_at_utc)
            VALUES ($1, $2, $3, $4, $5::jsonb, $6);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(rideId);
        command.Parameters.AddWithValue(guestId);
        command.Parameters.AddWithValue(sequence);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(details);
        command.Parameters.AddWithValue(occurredAtUtc.ToUniversalTime());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static RideActivityEntry ToActivityEntry(
        RideTransitionState ride,
        int sequence,
        string status,
        DateTimeOffset changedAtUtc) =>
        new(
            ride.Id,
            sequence,
            status,
            changedAtUtc.ToUniversalTime(),
            ride.PickupName,
            ride.DropoffName,
            ride.PickupAtUtc,
            ride.DriverName,
            ride.Car,
            ride.Plate);

    private sealed record RideTransitionState(
        Guid Id,
        Guid GuestId,
        string PickupName,
        string DropoffName,
        DateTimeOffset PickupAtUtc,
        string Status,
        string? DriverName,
        string? Car,
        string? Plate,
        string StayWorkflowId);

    private sealed record RideEvent(string Status, DateTimeOffset OccurredAtUtc);

    private RideResponse ReadResponse(NpgsqlDataReader reader)
    {
        var pickupAt = reader.GetFieldValue<DateTimeOffset>(10);
        var bookedAt = reader.GetFieldValue<DateTimeOffset>(11);
        return new RideResponse(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetDouble(4),
            reader.GetDouble(5),
            reader.GetString(6),
            reader.GetDouble(7),
            reader.GetDouble(8),
            reader.GetBoolean(9),
            ToHotelTime(pickupAt),
            ToHotelTime(bookedAt),
            reader.GetString(12),
            reader.GetDouble(13),
            reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            ReadNullableTimestamp(reader, 18),
            ReadNullableTimestamp(reader, 19),
            ReadNullableTimestamp(reader, 20));
    }

    private RideStoredBooking ReadStoredBooking(NpgsqlDataReader reader)
    {
        var response = new RideResponse(
            reader.GetGuid(0),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetString(5),
            reader.GetDouble(6),
            reader.GetDouble(7),
            reader.GetString(8),
            reader.GetDouble(9),
            reader.GetDouble(10),
            reader.GetBoolean(11),
            ToHotelTime(reader.GetFieldValue<DateTimeOffset>(12)),
            ToHotelTime(reader.GetFieldValue<DateTimeOffset>(13)),
            reader.GetString(14),
            reader.GetDouble(15),
            reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            ReadNullableTimestamp(reader, 20),
            ReadNullableTimestamp(reader, 21),
            ReadNullableTimestamp(reader, 22));
        DateTimeOffset? pickupAt = response.Immediate
            ? null
            : reader.GetFieldValue<DateTimeOffset>(12).ToUniversalTime();
        return new RideStoredBooking(
            reader.GetString(1),
            new RideBookingIntent(
                response.GuestId,
                response.StayId,
                reader.GetFieldValue<DateOnly>(4),
                response.PickupName,
                response.PickupLatitude,
                response.PickupLongitude,
                response.DropoffName,
                response.DropoffLatitude,
                response.DropoffLongitude,
                response.Immediate,
                pickupAt,
                response.RouteDurationSeconds),
            response);
    }

    private RideResponse ToResponse(RideBookingCommand booking, string status) =>
        new(
            booking.Id,
            booking.GuestId,
            booking.StayId,
            booking.PickupName,
            booking.PickupLatitude,
            booking.PickupLongitude,
            booking.DropoffName,
            booking.DropoffLatitude,
            booking.DropoffLongitude,
            booking.Immediate,
            ToHotelTime(booking.PickupAtUtc),
            ToHotelTime(booking.BookedAtUtc),
            status,
            booking.RouteDurationSeconds,
            booking.StayWorkflowId);

    private static void AddBookingParameters(NpgsqlCommand command, RideBookingCommand booking)
    {
        command.Parameters.AddWithValue(booking.Id);
        command.Parameters.AddWithValue(booking.IdempotencyKey);
        command.Parameters.AddWithValue(booking.GuestId);
        command.Parameters.AddWithValue(booking.StayId);
        command.Parameters.AddWithValue(booking.CheckoutDate);
        command.Parameters.AddWithValue(booking.PickupName);
        command.Parameters.AddWithValue(booking.PickupLatitude);
        command.Parameters.AddWithValue(booking.PickupLongitude);
        command.Parameters.AddWithValue(booking.DropoffName);
        command.Parameters.AddWithValue(booking.DropoffLatitude);
        command.Parameters.AddWithValue(booking.DropoffLongitude);
        command.Parameters.AddWithValue(booking.Immediate);
        command.Parameters.AddWithValue(booking.PickupAtUtc.ToUniversalTime());
        command.Parameters.AddWithValue(booking.BookedAtUtc.ToUniversalTime());
        command.Parameters.AddWithValue(booking.RouteDurationSeconds);
        command.Parameters.AddWithValue(booking.StayWorkflowId);
    }

    private static DateTimeOffset? ReadNullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(ordinal).ToUniversalTime();

    private static long GetLockKey(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }

    public sealed class AdmissionTransaction(
        RideRepository repository,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async Task<RideStoredBooking?> FindAsync(
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            await repository.FindByIdempotencyKeyAsync(
                idempotencyKey, connection, transaction, cancellationToken);

        public Task CommitAsync(CancellationToken cancellationToken) =>
            transaction.CommitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    public sealed record RideStoredBooking(string IdempotencyKey, RideBookingIntent Intent, RideResponse Response);
}
