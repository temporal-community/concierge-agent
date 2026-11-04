# Changelog

All notable project changes are documented here.

## [Unreleased]

### Added

- Independent Foundry/Ollama chat and embedding configuration, plus a 768-dimensional knowledge-embedding migration.
- Temporal startup setup for fictional guest/stay data and Hotel ZGY Markdown ingestion from `knowledge/hotel-zgy/`.
- Hotel ZGY evaluation package (`tests/Concierge.Evaluation/`) with a validator, negative tests, and a hashed corpus snapshot.

### Changed

- Default TTS voice is Flux `flux-hannah-en` (provider `v2`); Aura remains configurable via `Deepgram:SpeakModel`. STT stays on Flux `flux-general-en`.
- ADR 0007 and related implementation status now record steps 3–4 as implemented, with step 5 retrieval pending.
- README and architecture brief identify `tests/Concierge.Evaluation/` as the evaluation package, distinguish validated local Ollama calls from unverified Foundry live calls, and retain the setup run's persisted-state evidence limits.
- AppHost passes its selected environment name to WorkflowServer and Web; the environment handoff is not hard-coded to Development.
- Final direct `dotnet test Concierge.slnx --no-restore` passed 52/52 tests, including PostgreSQL and Temporal integration coverage. The evaluation validator and its negative tests passed.

### Fixed

- Setup seeding rejects a configured stay whose `stay_id`/`external_stay_key` pair differs from the persisted pair. It fails with a non-retryable `StayIdentityMismatch` and rolls back instead of silently keeping the old UUID or retrying a primary-key violation.
- Corrected stale setup/provider/schema implementation status. The live setup evidence is an idempotent re-run against persisted state, not a clean initial ingest.
- Kept evaluation-package human review, corpus-absence confirmation, activation, Foundry live-call validation, retrieval comparisons, and retrieval-quality results explicitly pending.
