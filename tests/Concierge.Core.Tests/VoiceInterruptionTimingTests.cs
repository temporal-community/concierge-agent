using System.Text;
using Concierge.Web.Voice;
using Microsoft.Extensions.Time.Testing;

namespace Concierge.Core.Tests;

public sealed class VoiceInterruptionTimingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CompletesEachInterruptionAgainstItsOwnStartAndOnlyOnce()
    {
        var time = new FakeTimeProvider(Now);
        var timings = new VoiceInterruptionTimings(time);
        timings.Begin(1);

        time.Advance(TimeSpan.FromMilliseconds(250));
        timings.Begin(2);
        time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.True(timings.TryComplete(2, out var secondDuration));
        Assert.Equal(100, secondDuration);
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(timings.TryComplete(1, out var firstDuration));
        Assert.Equal(550, firstDuration);
        Assert.False(timings.TryComplete(1, out _));
        Assert.False(timings.TryComplete(3, out _));
    }

    [Fact]
    public void ReadsOnlyPositiveInterruptionSilenceAcknowledgements()
    {
        Assert.True(VoiceRelay.TryReadInterruptionSilenced(
            Encoding.UTF8.GetBytes("""{"type":"InterruptionSilenced","interruptionNumber":7,"bargeIn":true,"playbackToken":"turn-7"}"""),
            out var number, out var bargeIn, out var playbackToken));
        Assert.Equal(7, number);
        Assert.True(bargeIn);
        Assert.Equal("turn-7", playbackToken);
        Assert.True(VoiceRelay.TryReadInterruptionSilenced(
            Encoding.UTF8.GetBytes("""{"type":"InterruptionSilenced","interruptionNumber":8,"bargeIn":false,"playbackToken":null}"""),
            out _, out bargeIn, out playbackToken));
        Assert.False(bargeIn);
        Assert.Null(playbackToken);

        foreach (var message in new[]
        {
            """{"type":"PlaybackAck","interruptionNumber":7}""",
            """{"type":"InterruptionSilenced","interruptionNumber":0}""",
            """{"type":"InterruptionSilenced","interruptionNumber":"7"}""",
            """{"type":"InterruptionSilenced","interruptionNumber":7,"bargeIn":true}""",
            """{"type":"InterruptionSilenced","interruptionNumber":7,"bargeIn":"true","playbackToken":null}""",
            """{"type":"InterruptionSilenced","interruptionNumber":7,"bargeIn":1,"playbackToken":null}""",
            """{"type":"InterruptionSilenced","interruptionNumber":7,"bargeIn":null,"playbackToken":null}""",
            """{"type":"InterruptionSilenced","interruptionNumber":7,"bargeIn":true,"playbackToken":""}""",
            """{"type":"InterruptionSilenced","interruptionNumber":7,"bargeIn":true,"playbackToken":42}""",
            """{"type":"InterruptionSilenced","interruptionNumber":0,"bargeIn":true,"playbackToken":null}""",
            "[]",
            "{not json",
        })
        {
            Assert.False(VoiceRelay.TryReadInterruptionSilenced(
                Encoding.UTF8.GetBytes(message), out _, out _, out _));
        }
    }

    [Fact]
    public void RepeatedStartForOneInterruptionDoesNotResetItsTimer()
    {
        var time = new FakeTimeProvider(Now);
        var timings = new VoiceInterruptionTimings(time);
        timings.Begin(1);
        time.Advance(TimeSpan.FromMilliseconds(250));

        timings.Begin(1);
        time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.True(timings.TryComplete(1, out var duration));
        Assert.Equal(350, duration);
    }

    [Fact]
    public async Task FlushAudioReportsDiscardedAudioAndKeepsEvents()
    {
        var queue = new PlaybackQueue(10);
        queue.TryEnqueue(new PlaybackItem(false, new byte[960]));
        queue.TryEnqueue(new PlaybackItem(true, "{}"u8.ToArray()));
        queue.TryEnqueue(new PlaybackItem(false, new byte[960]));

        Assert.Equal(2, queue.FlushAudio());
        Assert.Equal(0, queue.FlushAudio());
        var remaining = await queue.DequeueAsync(new PlaybackPacer(9120), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(remaining.IsText);
        Assert.Equal("{}", Encoding.UTF8.GetString(remaining.Data));
    }

    // Regression: the worklet acknowledges every 512 rendered samples (four 128-sample quanta) and
    // each ack used to release exactly one 480-sample frame. Once Deepgram had delivered a long
    // reply, only acks woke the sender, so the browser lead shrank 32 samples per ack until playback
    // starved (~6 s in): jittery audio and rapid speaking/listening toggles.
    [Fact]
    public async Task EachAckRefillsTheFullPlaybackLeadForAlreadyDeliveredSpeech()
    {
        const int frameSamples = 480;
        const int ackSamples = 512;
        const int leadSamples = 24_000 * 380 / 1000;
        const int totalFrames = 500; // 10 s of 24 kHz speech, all delivered before playback starts.
        var queue = new PlaybackQueue(totalFrames + 1);
        var pacer = new PlaybackPacer(leadSamples);
        for (var frame = 0; frame < totalFrames; frame++)
        {
            Assert.True(queue.TryEnqueue(new PlaybackItem(false, new byte[frameSamples * 2])));
        }

        long sent = 0;
        using var stop = new CancellationTokenSource();
        var sender = Task.Run(async () =>
        {
            while (Interlocked.Read(ref sent) < (long)totalFrames * frameSamples)
            {
                var item = await queue.DequeueAsync(pacer, stop.Token);
                Interlocked.Add(ref sent, item.Data.Length / 2);
            }
        });

        Assert.True(SpinWait.SpinUntil(
            () => Interlocked.Read(ref sent) == leadSamples / frameSamples * frameSamples,
            TimeSpan.FromSeconds(1)));
        long played = 0;
        while (played + ackSamples <= Interlocked.Read(ref sent))
        {
            played += ackSamples;
            Assert.True(pacer.Acknowledge(played));
            queue.Signal();
            var refilled = SpinWait.SpinUntil(
                () => Interlocked.Read(ref sent) - played > leadSamples - frameSamples
                    || Interlocked.Read(ref sent) == (long)totalFrames * frameSamples,
                TimeSpan.FromSeconds(1));
            Assert.True(refilled,
                $"After {played} played samples the browser held only {Interlocked.Read(ref sent) - played} unplayed samples.");
        }

        Assert.Equal((long)totalFrames * frameSamples, Interlocked.Read(ref sent));
        await sender.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void PlaybackLeadIsBoundedAndOnlyValidCumulativeAcksFreeCapacity()
    {
        var pacer = new PlaybackPacer(24_000 * 380 / 1000);
        for (var frame = 0; frame < 19; frame++)
        {
            Assert.True(pacer.TryReserve(480));
        }
        Assert.False(pacer.TryReserve(480));
        Assert.False(pacer.Acknowledge(9121));
        Assert.False(pacer.TryReserve(480));
        Assert.True(pacer.Acknowledge(480));
        Assert.True(pacer.TryReserve(480));
        Assert.False(pacer.Acknowledge(479));
        Assert.True(pacer.Acknowledge(9600)); // The flush can retire played and discarded samples.
        Assert.True(pacer.TryReserve(480));
    }
}
