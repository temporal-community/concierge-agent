# Changelog

All notable project changes are documented here.

## [Unreleased]

### Added

- Brand themes. The theme picker is one grouped list: Temporal and Deepgram, each with Light, Dark and System.
  - Temporal (UV #444CE7 on Space Black or Off White) replaces the generic purple and is the default. A Light, Dark or System choice saved before brands existed keeps its mode under Temporal.
  - Deepgram uses Glass buttons, Fuchsia (dark) or LED (light) accents, Work Sans and Azeret Mono, loaded from Google Fonts.
  - `js/theme.js` applies the saved theme from `<head>` before the first paint. That removes the flash of the light theme on load and also themes the error and 404 pages.
  - Colors are `light-dark()` tokens, one block per brand. A test checks AA contrast for every brand in both modes.
- Hotel ZGY photo background with tinted glass panels.
  - The photo shows behind the chat and the verify, start-over and end-of-chat cards, under a wash of the brand's page color. Phones keep the chat on a solid color and show the photo behind the cards.
  - The header, chat panel, focus cards and activity drawer are frosted glass: the surface color at 75% (light) or 60% (dark), a 6% brand-accent tint and a 60px blur. Bubbles, inputs and buttons stay solid. With reduced transparency turned on, the panels are solid again.
  - Light-mode muted text is darker (Temporal #4e596b, Deepgram #585858) so timestamps and hints keep AA on the glass.
  - `tests/glass-contrast.test.mjs` renders the real photo and the page's stylesheets for every brand and mode at several screen sizes, and checks each text color on the glass against the darkest and lightest pixel behind it.
- The browser layout and glass-contrast tests now render the AG-UI chat page instead of the removed Home page. `tests/agentic-page.mjs` holds the page's markup, copied from its rendered DOM, and loads the same stylesheets in the same order, including the Components.AI kit's `ai-chat.css`, located through the build's static web assets manifest. The layout test covers paused voice, the "chat ending soon" bar and live voice, with the activity drawer open and closed.
- Removed Home's leftover styles from `app.css` (about 400 lines): the suggestion accordion, the old transcript and composer, retry and sign-in-again buttons, the header voice status and its `voice-pulse` animation. No rendered markup used them. Code comments no longer describe the chat page in terms of Home, call it a prototype, or name `/agentic` as its address.
- Removed the wave icon from the focus cards and the "How can we help?" heading.
- The "Your stay conversation" page updates live when another session adds a turn to the same stay, for example a room device using `POST /api/concierge/turns` or a second browser tab, and empties when another session uses Start over. `ConciergeService` raises an in-process `StayTranscriptNotifier` event after the stay workflow accepts a turn, interrupt or reset. Pages then re-read history in the background, without touching the busy state, a visible error, suggestions or scroll position unless the transcript changed.
- Opt-in `RemoteAccess:PublicTunnel` AppHost setting. It adds Web's HTTPS endpoint as an anonymous port on the `think-tunnel` dev tunnel, so off-machine clients can reach the guest HTTP API. The setting defaults to off, and the remote-client contract is documented in `docs/configuration.md` under "Remote API clients".
- **Start over** button in the chat header. After a confirmation step it deletes the signed-in stay's transcript, conversation memory, ride offers, and rides, and keeps the guest signed in. It is backed by `POST /api/concierge/reset`, the `ResetStay` stay-workflow update, and a RideService `POST /stays/{stayId}/rides/purge` endpoint, which terminates the stay's booking workflows and deletes their rows.
- Independent Foundry/Ollama chat and embedding configuration, plus a 768-dimensional knowledge-embedding migration.
- Temporal startup setup for fictional guest/stay data and Hotel ZGY Markdown ingestion from `knowledge/hotel-zgy/`.
- Hotel ZGY evaluation package (`tests/Concierge.Evaluation/`) with a validator, negative tests, and a hashed corpus snapshot.

### Changed

- Default TTS voice is Flux `flux-hannah-en` (provider `v2`); Aura remains configurable via `Deepgram:SpeakModel`. STT stays on Flux `flux-general-en`.
- ADR 0007 and related implementation status now record steps 3–4 as implemented, with step 5 retrieval pending.
- README and architecture brief identify `tests/Concierge.Evaluation/` as the evaluation package, distinguish validated local Ollama calls from unverified Foundry live calls, and retain the setup run's persisted-state evidence limits.
- AppHost passes its selected environment name to WorkflowServer and Web; the environment handoff is not hard-coded to Development.
- Final direct `dotnet test Concierge.slnx --no-restore` passed 52/52 tests, including PostgreSQL and Temporal integration coverage. The evaluation validator and its negative tests passed.
- Retargeted all projects from `net10.0` to `net11.0`. A new `global.json` pins SDK 11.0.100-rc.3.26522.148, with `rollForward: latestFeature` so the GA 11.0.1xx SDK also satisfies it. `Microsoft.AspNetCore.Mvc.Testing` moved to 11.0.0-rc.3.26522.148; the other packages ship out of band and are unchanged. Removed the CS9057 suppressions, which SDK 11's compiler no longer needs for OllamaSharp's generator. `dotnet test Concierge.slnx` passed 1101/1101 on net11.0.

### Fixed

- Voice no longer drops the final "Y" of "Hotel ZGY." at the end of a sentence. Aura-2 TTS (`aura-2-thalia-en` and `aura-2-andromeda-en`) cuts off the last phoneme when its text ends in "ZGY", and the Voice Agent synthesizes speech a sentence at a time. Flux `flux-hannah-en`, now the default (see Changed), renders it completely. Setting `Deepgram:SpeakModel` back to Aura brings the clipping back.
- Long voice replies no longer turn jittery or make the voice state flip between speaking and listening. The relay sent only one 20 ms frame per browser playback acknowledgement, but the worklet acknowledges every 512 samples. So once Deepgram had delivered a reply, the 380 ms playback lead shrank by 32 samples per acknowledgement and ran dry after about 6 s of speech. The sender now sends every frame the lead allows before it waits.
- Re-offering a ride to a destination chosen from nearby places no longer fails with "I couldn't identify the destination" once the six-turn nearby window has lapsed. If no retained place matches, `ProposeRide` now reuses the route-verified drop-off coordinates of the open or last confirmed ride offer when the destination text exactly matches that offer's label. Places-only labels such as "Street Art, North Miami Avenue" cannot be geocoded.
- Losing the Blazor Server circuit during a browser call (tab closed, network drop) no longer faults the circuit. When `conciergeVoice.stop` hits a lost circuit during End chat, the chat still ends server-side. Calls whose only consumer was the browser stop early. Cleanup and persistence calls let their callers finish. `JSException` and interop timeouts still propagate. Prompted by .NET 11's new BL0016 analyzer.
- Setup seeding rejects a configured stay whose `stay_id`/`external_stay_key` pair differs from the persisted pair. It fails with a non-retryable `StayIdentityMismatch` and rolls back instead of silently keeping the old UUID or retrying a primary-key violation.
- Corrected stale setup/provider/schema implementation status. The live setup evidence is an idempotent re-run against persisted state, not a clean initial ingest.
- Kept evaluation-package human review, corpus-absence confirmation, activation, Foundry live-call validation, retrieval comparisons, and retrieval-quality results explicitly pending.
