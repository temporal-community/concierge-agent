# Configuration reference

Defaults below are for running the demo in Development. Secrets default to
empty. Outside Development, the base AppHost `appsettings.json` selects Foundry
with no endpoint or model, so AppHost fails at startup until both are configured.

AppHost forwards AI, hotel, ride-policy, and service-key settings to the
services. The [Development settings](../src/Concierge.AppHost/appsettings.Development.json)
use local Ollama models.

For AppHost-owned settings, use checked-in `appsettings*.json` for nonsecret
local defaults and the AppHost user-secrets store for local overrides:

```bash
dotnet user-secrets set "<setting>" "<value>" --project src/Concierge.AppHost
```

`aspire secret set "<setting>" "<value>" --apphost src/Concierge.AppHost`
writes to that same store. Do not put secret values in checked-in settings.
Settings owned by WorkflowServer or Web are configured in that service's
`appsettings*.json` or environment; AppHost forwards only the values called out
below. This reference covers app-owned Phase 1/2 options; framework logging and
ASP.NET Core host settings follow .NET defaults. Aspire supplies service
connection strings, Temporal endpoints, and generated ports.

## Service keys

Configure these on **AppHost** as secret parameters. It forwards them to the
indicated service; the internal service key is shown for tracing the wiring.

| AppHost parameter → service setting | Default | Units | Validation and purpose |
| --- | --- | --- | --- |
| `Parameters:geoapify-api-key` → WorkflowServer `Geoapify:ApiKey` | Empty | n/a | Nonblank at WorkflowServer startup; Geoapify key for hotel geocoding, nearby places, and routes. |
| `Parameters:deepgram-api-key` → Web `Deepgram:ApiKey` | Empty | n/a | Required to use browser voice; the key must be valid at Deepgram. Web does not validate it at startup. |

## AI providers

Configure on **AppHost**; forwarded to **WorkflowServer**. Foundry API keys use
the `Parameters:*` settings below. Provider names are case-insensitive.

| Setting | Default | Units | Validation and purpose |
| --- | --- | --- | --- |
| `AI:Chat:Provider` | `Ollama` | n/a | Case-insensitive `Foundry` or `Ollama`; selects the chat provider. |
| `AI:Chat:Endpoint` | `http://127.0.0.1:11434` | URL | Absolute HTTP(S) URL; Foundry requires HTTPS. Chat accepts a clean resource root or Responses API base (`/v1` for Ollama, `/openai/v1` for Foundry), without credentials, query, or fragment. |
| `AI:Chat:Model` | `concierge-chat` | n/a | Nonblank model name; for Foundry, use the Azure deployment name. |
| `Parameters:foundry-chat-api-key` → `AI:Chat:ApiKey` | Empty | n/a | Foundry chat key; optional when `DefaultAzureCredential` is configured, unused for Ollama. |
| `AI:Chat:RequestTimeoutSeconds` | `18` | Seconds | Integer `1–18`; bounds one chat request, including concurrency-slot wait, inside the Temporal activity attempt. |
| `AI:Chat:MaxConcurrentRequests` | `2` | Requests | Positive integer; per-WorkflowServer chat concurrency. |
| `AI:Chat:ContextWindowTokens` | `32768` | Tokens | Integer at least `21000`; must match the selected model's supported context window. |
| `AI:Chat:ReasoningEffort` | `low` | n/a | Case-insensitive `low` or `none`; the selected model must support the choice. |
| `AI:Embeddings:Provider` | `Ollama` | n/a | Case-insensitive `Foundry` or `Ollama`; selects the embedding provider. |
| `AI:Embeddings:Endpoint` | `http://127.0.0.1:11434` | URL | Absolute HTTP(S) URL; Foundry requires HTTPS. Embedding service endpoint. |
| `AI:Embeddings:Model` | `embeddinggemma-2:740m` | n/a | Nonblank model name; for Foundry, use the Azure deployment name. |
| `Parameters:foundry-embeddings-api-key` → `AI:Embeddings:ApiKey` | Empty | n/a | Foundry embeddings key; optional when `DefaultAzureCredential` is configured, unused for Ollama. |
| `AI:Embeddings:Dimensions` | `768` | Dimensions | Exactly `768`, matching the database schema and model output. |
| `AI:Embeddings:RequestTimeoutSeconds` | `8` | Seconds | Integer `1–8`; bounds retrieval embedding calls, including concurrency-slot wait. |
| `AI:Embeddings:IngestionRequestTimeoutSeconds` | `60` | Seconds | Integer `1–298`; bounds ingestion embedding calls and Ollama embedding warm-up. |
| `AI:Embeddings:MaxConcurrentRequests` | `2` | Requests | Positive integer; per-WorkflowServer embedding concurrency. |

AppHost creates the Foundry API-key parameter only when the corresponding
provider is `Foundry`. An empty key uses `DefaultAzureCredential`; in Development
the Ollama provider does not use either Foundry key.

Action requests set MEAI's `AllowMultipleToolCalls` to `false`: the model is
asked for at most one tool call per response, not per guest turn. Sequential
tool rounds (such as nearby lookup followed by a ride proposal) remain possible,
and the workflow expands supported multi-day weather requests itself. The
three-model-call/two-tool-round limit is unchanged. This is a fixed execution
contract, not another configuration setting. Unsupported mixed batches execute
no tools, even if a provider ignores the request.

## Hotel and rides

Configure on **AppHost** in settings or AppHost user-secrets. It forwards the
address to WorkflowServer, and the time zone and ride policy to both
WorkflowServer and RideService.

| Setting | Default | Units | Validation and purpose |
| --- | --- | --- | --- |
| `Hotel:Address` | `2217 NW Miami Court, Miami, FL 33127` | Address | Nonblank address that passes WorkflowServer startup geocoding; hotel anchor for weather, places, routes, and rides. |
| `Hotel:TimeZoneId` | `America/New_York` | Time-zone ID | Nonblank ID supported by the service host; defines hotel-local date and time behavior. Forwarded to WorkflowServer and RideService. |
| `Rides:MinimumAdvanceNoticeMinutes` | `5` | Minutes | Positive whole number; minimum notice for scheduled pickups, shared by WorkflowServer proposal validation and RideService booking validation. Immediate rides are unaffected. |

For example, change scheduled pickups to require 12 minutes’ notice:

```bash
dotnet user-secrets set "Rides:MinimumAdvanceNoticeMinutes" "12" --project src/Concierge.AppHost
```

AppHost forwards this value to both WorkflowServer and RideService. The pickup
must still meet the minimum when confirmed; immediate rides are unaffected.
When a guest asks for a pickup that is too soon (for example, "in five
minutes"), the concierge offers the earliest available time instead: the
minimum notice plus a two-minute confirmation grace, rounded up to the next
minute. The guest hears the adjusted time and confirms it before booking.
Pickups in the past, or at or after the end of checkout day, are refused; the
reply includes the earliest available pickup time when one exists.

Matching active rides do not prohibit an explicit new booking. The proposal
includes a bounded warning about existing rides and states that the offer is
additional, not a change; a later explicit confirmation is still required.
When a valid replacement proposal is dispatched, the previous unbooked offer
closes; it cannot be confirmed if that replacement activity fails or returns an
invalid result. A genuine destination clarification does not dispatch a proposal
and leaves the existing offer unchanged. Repeated confirmations and same-ordinal
voice revisions do not authorize another booking.

Nearby-place continuity is a fixed workflow contract, with no additional
configuration:

- Keep only the latest nearby result (up to three canonical places) and latest
  destination clarification (up to three). Nearby results last ten minutes and
  at most six subsequent accepted turns; clarifications last five minutes and
  at most two subsequent turns, whichever limit is reached first. Retries of
  an already accepted turn do not refresh either limit. Completed transcript
  sequences, including superseded completed turns, count conservatively toward
  these limits. Superseding unfinished work does not refresh expiry timestamps.
- Names, addresses, and provider IDs are capped at 120, 160, and 256 UTF-8 bytes
  per stored record. Model tool-result projections remain valid JSON within
  2,048 UTF-8 bytes, including every retained branch rather than only spoken
  addresses. The existing whole-action-context budget also covers cross-turn
  continuity. Continue-As-New carries original timestamps, sequence provenance,
  presentation metadata and focus unchanged.
- `FindNearbyPlaces.presentation` is `nearest` for one nearest result or
  `alternatives` for a list (the default). Canonical model facts are independent
  of that rendered subset and of the channel. The model interprets the guest's
  references from recent conversation and these structured facts, including
  presentation/focus and clarification context. It supplies the resolved
  destination name and street to `ProposeRide`, or asks about genuine ambiguity.
- Workflow matching compares only that model-resolved destination against
  retained names, addresses, or combined name/address labels (case-insensitive,
  ignoring surrounding whitespace). A unique match reuses activity-verified
  coordinates, never a model-supplied ID or coordinate. Multiple matching branches
  ask for a name and street without dispatching a proposal. Even a name/address
  pair cannot authorize conflicting retained coordinates.
- No guest-sentence regex, reference grammar, keyword router, or extra classifier
  or model call is used. New/changed destinations and expired records take the
  ordinary resolver path without retained coordinates. Interpreting natural
  references and deciding whether to clarify belongs to the existing model turn,
  not to a parallel parser.

An ordinary nearby request uses one model call and one tool. A known named
selection uses one model call and `ProposeRide`, without another nearby lookup
or destination geocode. A fresh nearest-ride request uses two model calls and
two tools. Every proposal still refreshes routing and the active-ride check;
non-hotel pickups still use the normal resolver. The later explicit confirmation,
consumed-offer, uncertainty, cancellation, and mixed-batch guards are unchanged.
Offers ask “Shall I book it …?” and retain material timing and additional-ride
warnings. The `ride-conversation` live-model gate and six-turn Chrome check with
synthetic microphone audio passed. These runs do not establish that every
conversational reference works.

## Browser voice

Same-ordinal speech revisions are authorized by the stay workflow, not by an
HTTP disconnect:

- The full turn ID still includes the exact spoken-text hash. Exact retries
  join the same workflow attempt, including across Web replicas. Authenticated
  voice submissions also carry session, connection, and ordinal provenance.
  Only a changed hash within that family supersedes older replaceable work.
  Each RPC uses a fresh Temporal update ID so a retry consults current workflow
  authority rather than Temporal's immutable cached reply to an old update.
- Supersession happens before turn-queue waiting. Queued fragments are skipped;
  pending retrieval, model, and read/proposal results cannot commit state or
  initiate another tool round after replacement. Already-running external
  reads/model activities may still finish; this is fencing, not a claim that
  their HTTP transport was cancelled.
- A completed reversible turn can be withdrawn from effective conversation
  context only while it is the last conversational commit. PostgreSQL keeps
  its original text with a monotonic `superseded` audit marker; effective
  history excludes it. A late revision never rolls back a later interaction.
  Earlier guest-memory/proposal effects are **not** surgically removed across
  intervening turns; a guest correction may still be needed.
- Booking and cancellation become non-replaceable when their activity is
  dispatched, before any acknowledgement. Revised speech waits for that result
  and receives the retained confirmed or uncertain outcome, without another
  model call or side-effect request. Consumed offers remain closed.
- Revision authority, the last reversible checkpoint, and bounded action
  receipts survive Continue-As-New. Stay deadlines, sequence numbers, and
  existing expiry timestamps are not reset. Fixed safety bounds are 512
  utterance families per stay, 32 text versions per utterance, and 512,000
  serialized UTF-8 bytes at revision admission. Retained reply receipts are
  limited to 2,048 UTF-8 bytes. At capacity, new admissions fail closed rather
  than evicting retry/side-effect authority; use web chat or the front desk.

A superseded Think request uses the existing HTTP 503 unavailable-error path,
not a 409, empty success, or replacement SSE reply. A caller disconnect alone
still leaves admitted Temporal work running. These server guarantees do not
establish whether Deepgram aborts speculative custom-Think requests or whether
an already-delivered obsolete reply was audible. The current code has passed
the live-model and synthetic-microphone browser checks; physical audio quality
and obsolete-reply audibility are not independently verified. Future incompatible
workflow command changes require explicit approval for the repository-mandated
Temporal/Postgres reset before starting against existing histories.

Configure on **Web** in its appsettings or service environment. AppHost
forwards `Deepgram:ApiKey` only; it does not forward the model or idle-timeout
options.

| Setting | Default | Units | Validation and purpose |
| --- | --- | --- | --- |
| `Deepgram:ApiKey` | Empty | n/a | Required for browser voice; supplied through AppHost `Parameters:deepgram-api-key`. The key must be valid at Deepgram; Web does not validate it at startup. |
| `Deepgram:ListenModel` | `flux-general-en` | n/a | Deepgram speech-to-text model; Flux uses provider version `v2`, Nova uses `v1`; configure on Web, not forwarded by AppHost. |
| `Deepgram:SpeakModel` | `flux-hannah-en` | n/a | Deepgram text-to-speech model; Flux (default) uses provider version `v2`, Aura (still configurable) uses `v1`; configure on Web, not forwarded by AppHost. |
| `Deepgram:Speed` | `1.0` | Number | TTS speed; Flux accepts `0.5–1.5` in `0.05` steps, Aura accepts `0.7–1.5`; configure on Web, not forwarded by AppHost. |
| `Deepgram:IdleTimeoutMinutes` | `2` | Minutes | Integer `1–60`; ends an idle voice chat; configure on Web, not forwarded by AppHost. |

Provider versions and speed ranges follow Deepgram's
[Voice Agent Settings documentation](https://developers.deepgram.com/docs/voice-agent-settings).

Voice chats show an accessible inactivity warning about 30 seconds before this
timeout. The server uses detected guest speech and its existing reply-in-progress
state for both the warning and timeout; microphone frames, mute/unmute, and
transport keep-alives do not reset the idle clock. Detected speech clears the
warning, replies suppress it while in progress, and reconnect starts a fresh idle
period. After an idle chat ends, the UI explicitly says it ended due to inactivity
and offers stay verification to resume. This warning has no separate configuration
and does not change the idle timeout policy.

## Remote API clients

By default, Web listens only on localhost, and the only public tunnel is the
Think callback that Deepgram uses. Clients on another device or network, such
as a phone or a hardware companion, need the opt-in API tunnel. Set this in
`src/Concierge.AppHost/appsettings.json` or AppHost user secrets:

| Setting | Default | Purpose |
| --- | --- | --- |
| `RemoteAccess:PublicTunnel` | `false` | When `true`, AppHost adds Web's HTTPS endpoint as an anonymous port on the existing `think-tunnel` dev tunnel. Its public URL is shown on the `think-tunnel-web-https` resource in the Aspire dashboard. |

The tunnel exposes the guest UI and the guest HTTP API:

| Endpoint | Auth | Body | Returns |
| --- | --- | --- | --- |
| `POST /api/concierge/sessions` | None | `{lastName, roomNumber}` | `{credential, expiresAtUtc, stayId, sessionId}`; `404 guest_not_found` when no eligible stay matches |
| `POST /api/concierge/turns` | `Bearer <credential>` | `{turnId, userMessage, channel}` | `{turnId, assistantMessage, sequence}` |
| `GET /api/concierge/history` | `Bearer <credential>` | None | `{turns: [...]}` |
| `POST /api/concierge/end` | `Bearer <credential>` | None | `204` |

- **Channel.** `channel` is `web` or `voice`, and it is case-sensitive. Use
  `voice` when the answer will be spoken: replies are worded for listening and
  contain no lists.
- **Turn IDs.** `turnId` is at most 128 characters. Retrying with the same ID
  and text joins the turn already admitted, so a client can retry after a
  timeout without booking twice.
- **Message size.** `userMessage` is at most 2,000 UTF-8 bytes.
- **Credential lifetime.** A credential lasts until the stay's eligibility
  ends, or until `POST /reset` or `POST /end`. On `401`, verify again.
- **Sessions per stay.** Each verification creates a separate session for the
  same stay workflow. A browser and another client can run side by side and
  share the stay's memory and rides.
- **Live transcript.** A turn another session adds to the stay, through the
  API, another tab or browser voice, appears on open guest pages for that stay
  without a reload. A reset from another session clears them. This is
  in-process, so it only works while every client goes through the same Web
  instance, which is how the app runs.

Errors are `{code, message}`. The codes are `invalid_request` (400),
`unauthorized` (401), `guest_not_found` (404), `turn_conflict` (409) and
`unavailable` (503).

> **Security.** The tunnel is anonymous. Anyone with the URL can try last
> name and room combinations against `/sessions`, and the endpoint has no
> rate limit. Use it only with the fictional demo stays, and turn it off
> after the demo.

## Knowledge base and demo stays

Configure on **WorkflowServer** in its appsettings or service environment;
AppHost does not read or forward these `Setup:*` settings.

| Setting | Default | Units | Validation and purpose |
| --- | --- | --- | --- |
| `Setup:KnowledgeDirectory` | `knowledge/hotel-zgy` | Directory path | Must resolve to a directory containing Markdown sources with valid `zgy-*` source IDs, nonblank titles, and nonempty sections. Relative paths resolve from the service content root, then app base directory. Discovery is recursive; the default build output copies only top-level `*.md` files from `knowledge/hotel-zgy`. The directory supplies retrieval knowledge. |
| `Setup:OverwriteExistingSeedRecords` | `false` | Boolean | `true` or `false`; when true, startup setup inserts or updates configured demo guest/stay records. Leave false unless those persistent rows are intentionally to be changed. |
| `Setup:Guests` | Built-in demo guests (Rivera, Morgan, Phillip) | List | At least one entry; `GuestId` must parse as a GUID, names must be nonblank and each at most 100 characters. Seeds the demo guest identities. |
| `Setup:Stays` | Built-in demo stays (rooms 412, 812, 112) | List | At least one entry; `StayId` and `GuestId` must parse as GUIDs, `ExternalStayKey` must be nonblank, `GuestId` must reference a configured guest, and room number must be 1–4 digits without a leading zero. Seeds demo stays. |

## Transcript persistence

Text and voice conversation transcripts are stored in the PostgreSQL
`concierge` database, in the `transcript_turns` table.

Database writes are batched using the settings below; ending a chat also
flushes pending turns.

To change them, add a `Concierge:Persistence` section to
[Web's appsettings.json](../src/Concierge.Web/appsettings.json).
Web uses the defaults below unless overridden and passes the policy when starting
a new stay workflow. Existing stay workflows retain their original policy.
**AppHost does not read or forward these settings.**

| Setting | Default | Units | Validation and purpose |
| --- | --- | --- | --- |
| `Concierge:Persistence:FlushIntervalSeconds` | `30` | Seconds | Integer `1–600`; maximum interval before a transcript batch is persisted. |
| `Concierge:Persistence:FlushBatchSize` | `5` | Turns | Integer `1–100`; transcript batch size. |
| `Concierge:Persistence:IdleRolloverSeconds` | `900` | Seconds | `0` (off) or integer `60–86400`. While a stay is idle and ride-feed polls are waiting, it continues as new once per interval. That releases polls left by closed tabs and restarted Web instances, which otherwise count against Temporal's in-flight Update limit until the stay ends. |

Web validates these at startup. AppHost does not read or forward this policy.
Ending a chat flushes pending transcript turns; the policy is passed when a new
stay workflow starts, and existing workflows retain their original values.
