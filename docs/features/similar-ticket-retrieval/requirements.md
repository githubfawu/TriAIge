# Requirements: similar-ticket-retrieval

**Date**: 2026-09-25

## Problem

The pipeline already runs `retrieve similar → classify` ([TriagePipeline.cs](../../../src/TicketTriage.Infrastructure/Pipeline/TriagePipeline.cs)), but both data-source ports are empty: `ISimilarTicketSource` is `StubSimilarTicketSource` (always `[]`) and `ITicketSource` has no implementation. Without similar tickets the classifier, the router, the drafter and the deterministic fallback have no historical context. [ticket_triage_architektur.md](../../ticket_triage_architektur.md) showed `classify_work_type` **before** `retrieve_similar_tickets` (fixed by the later rewrite of that document to the .NET app).

## Users & Context

- Consumer of `ISimilarTicketSource`: `TriagePipeline` (and through it `ITicketClassifier`, `IRoutingResolver`, `IResolutionDrafter`, `FallbackSuggestionFactory`).
- Consumer of `ITicketSource`: the future analysis worker in Web (FR-28). Batch keeps reading `challenge.json` itself for now.
- Data: historical tickets in SQLite, imported from `training.json` by `TrainingDataImporter`.

## Functional Requirements

- FR1 `retrieve_similar_tickets`: a real `ISimilarTicketSource` in Infrastructure replaces `StubSimilarTicketSource`.
  - Query text is **only the ticket's `Description`** (the summary is deliberately misleading; service hints are not used as filter).
  - Method: **TF-IDF + cosine kNN, in memory.** All candidate descriptions are loaded from SQLite once (lazy, thread-safe, on first call) and vectorised; each query is vectorised with the same vocabulary/IDF and ranked by cosine similarity.
  - Returns at most `top` results, sorted by `Score` descending (ties: lower row Id first), `Score` = cosine in (0, 1]. Candidates with score 0 are dropped.
  - Candidates: every ticket row with a non-blank `Description`.
  - **Self-exclusion by `Id` only**: when the query ticket has an `Id`, the row with that Id is never returned. Tickets without `Id` are not deduplicated.
  - Query ticket with blank `Description` → empty list (no error).
- FR2 Tokenisation: Unicode-aware (DE/FR/EN), lower-case invariant, split on non-letter/non-digit, tokens of length < 2 dropped, no stemming, no stop-word list (IDF down-weights common words). Sublinear TF (`1 + ln tf`), smoothed IDF, L2-normalised vectors.
- FR3 Mapping of a similar ticket (`SimilarTicket.Ticket`): `Id` = row id, `Key` = `"DB-{Id}"`, `Summary`, `Description`, `WorkType`, `AffectedServices`, `ServiceTeams`, `Assignee`, `Resolution`, `Created`, `Comments` (loaded for the top-k only) with lookup names resolved. **`Urgency`, `Impact`, `Priority` are left null** — they are random in the training data (ADR-0001) and must not reach downstream steps.
- FR4 `ITicketSource`: `DbTicketSource` in Infrastructure streams all tickets with status `New` from SQLite, ordered by `CreatedDate`, then `Id`, mapped the same way as FR3 (`Key` = `"DB-{Id}"`, `Id` set so the pipeline can persist `Retries`). It is registered in DI only; no caller is wired (Batch and Web unchanged).
- FR5 Importer fix: `TrainingDataImporter` no longer looks up the non-existent status `"Finished"`; tickets with a `Resolution` get status `HumanApproved`, tickets without stay `New`. No other importer change.
- FR6 Docs: `ticket_triage_architektur.md` §2 shows `retrieve_similar_tickets` **before** `classify_work_type` and notes the TF-IDF/description-only implementation; `docs/architecture.md` stub table and the pipeline README reflect the new implementations.

## Technical Constraints

- Allowed to change: Infrastructure (new `Retrieval/` + `Sources/` code, DI registration, removal of `StubSimilarTicketSource`, the importer status line), Infrastructure tests (incl. `RegistrationTests` expectation), docs.
- **Not changed**: schema (no new column/table, no FTS5, no embedding cache), `TriagePipeline`, Agents, Web, Batch, Core ports. Any need to touch these → ask first.
- No new NuGet package, no LLM/embedding call. Dependency direction unchanged; retrieval code uses `IDbContextFactory<TriageDbContext>`.
- Index is built once per process (singleton); training data is static during a run. Memory for ~20k descriptions (≤ 1000 chars each) is acceptable.
- Ticket text is untrusted: never logged at Information; only counts/timings are logged.

## Acceptance Criteria

- [ ] AC1: Given historical rows, a query whose description shares distinctive terms with one row returns that row first with the highest score.
- [ ] AC2: At most `top` results, strictly descending score, all scores in (0, 1].
- [ ] AC3: A query ticket with `Id = n` never gets row `n` back, even if its description is identical.
- [ ] AC4: Blank description → empty list; empty DB → empty list; no exception.
- [ ] AC5: Returned tickets have `Key = "DB-{Id}"`, resolved service/team/work-type names, comments, and null `Urgency`/`Impact`/`Priority`.
- [ ] AC6: The index is built once across concurrent first calls and reused for later calls.
- [ ] AC7: `DbTicketSource` yields exactly the `New` tickets in `CreatedDate, Id` order with `Id` and `Key` set; honours cancellation.
- [ ] AC8: DI resolves `ISimilarTicketSource` to the TF-IDF source and `ITicketSource` to `DbTicketSource`; `TriagePipeline` still resolves.
- [ ] AC9: The importer runs against the seeded lookup tables without throwing.
- [ ] AC10: Build green with warnings-as-errors; existing tests still pass.

## Edge Cases

- Description with only punctuation / one-letter tokens → no terms → empty list.
- Query terms not in the vocabulary are ignored (no out-of-vocabulary IDF).
- Mixed-language descriptions: tokens compared as-is (no translation, no stemming).
- Ticket text containing prompt-injection text: treated as plain tokens, no special handling.
- Cancellation during the first (index-building) call: the build is cancelled for that caller, a later call retries the build.

## Out of Scope

- Embeddings / hybrid retrieval, FTS5, any schema change (Jira key column, embedding cache).
- `retrieve_resolution_pattern` (separate resolution collection), FR-05 similarity threshold for resolutions.
- Wiring `ITicketSource` into Batch or a Web worker; `result.json` mapping.
- Service-category filter/boost.

## Open Questions

- Imported tickets without `Resolution` stay `New` and will therefore be streamed by `DbTicketSource` (decided: resolved → `HumanApproved`).
- Without a Jira key column, `SimilarTicketKeys` in suggestions contain `DB-{Id}` values, not Jira keys (known open point #3 in the pipeline README).
