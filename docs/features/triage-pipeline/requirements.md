# Requirements: triage-pipeline

**Date**: 2026-09-25

## Problem

`ITriagePipeline` is served by `StubTriagePipeline`, which chains four ports for one ticket and does no validation, retry or failure handling. The pipeline must (a) take its input and its history from two injectable data sources, (b) follow FR-10…FR-16 and FR-31…FR-34, and (c) handle failures with a retry count, a failure log and an optional stop switch so no tokens are wasted during development.

## Users & Context

- Callers: Batch (challenge file) and the Web analysis worker. Both consume the same pipeline (FR-31).
- Data sources are implemented by other team members; this feature defines the interfaces and the pipeline that consumes them.
- The agent component (LLM classifier/drafter), the frontend and the DB code are not changed, except the explicitly permitted failure-handling change below.

## Functional Requirements

- FR1: Core gets two data-source interfaces:
  - `ITicketSource`: yields the tickets to evaluate as `IAsyncEnumerable<Ticket>`.
  - `ISimilarTicketSource`: `FindSimilarAsync(Ticket ticket, int top, CancellationToken)` returning `IReadOnlyList<SimilarTicket>`; it excludes the ticket itself (no self-retrieval). It replaces `ISimilarTicketRetriever`, which is removed from Core.
- FR2: `ITriagePipeline` processes a stream: `TriageAsync(IAsyncEnumerable<Ticket>, CancellationToken)` returns `IAsyncEnumerable<TriageSuggestion>`. The per-ticket `TriageAsync(Ticket)` stays for single use. Tickets are processed **sequentially, in input order**, one suggestion per input ticket. The stream never ends early because of one ticket (unless FR9).
- FR3: Per ticket the steps run in this order (ADR-0001): normalize (FR-10) → similar tickets (`ISimilarTicketSource`) → classify (`ITicketClassifier`) → route (`IRoutingResolver`, team/assignee never from the LLM, FR-13) → priority `PriorityMatrix.Resolve(urgency, impact)` (FR-15, computed by `TriageSuggestion`) → draft (`IResolutionDrafter`, FR-16).
- FR4: Normalization (FR-10) flags empty or suspicious ticket fields (empty summary/description, missing values) and does not blindly adopt supplied classification values; the flags are logged without ticket text.
- FR5: Validation (FR-33) runs before a suggestion is yielded: valid enums, non-empty affected services, non-empty comment, priority consistent with the matrix. A failed validation counts as a failed attempt.
- FR6: A per-ticket timeout (default 60 s) bounds each attempt; the ticket is cancelled and counts as a failed attempt.
- FR7: On a failed attempt the pipeline increments `Retries` for that ticket in the database and writes one row to a failure-log table (reason, exception type, stack trace, ticket id, attempt number, timestamp). The ticket is retried until `Retries` reaches the configured **RetryCount** (default 3).
- FR8: When the retries are exhausted (and `StopSystemOnFailure` is false) a deterministic fallback suggestion is yielded (FR-34): work type and services from the majority of the similar tickets, Urgency Medium / Impact Moderate, routing as far as it resolves, no comment. The stream continues.
- FR9: `StopSystemOnFailure` (default false): when true the application is stopped (`IHostApplicationLifetime.StopApplication`) on the **first** failure, after the failure has been logged to the failure table and before any retry, so no further tokens are spent.
- FR10: New settings section `Triage` in the appsettings files: `RetryCount`, `StopSystemOnFailure`, `TicketTimeoutSeconds`, `SimilarTicketCount` (default 10).
- FR11: Persistence for FR7 sits behind a new Core port (e.g. `ITriageFailureStore`), implemented in Infrastructure. The pipeline itself does not reference EF Core.
- FR12: `Ticket` (Core) gets an optional `Id` so the stream ticket can be mapped to its database row. `TicketEntity` gets a `Retries` column (int, default 0); a new failure-log table is added. Schema is created by `EnsureCreated` (no migrations): the developer deletes `data/triage.db` once.
- FR13: The stub pipeline is replaced by the real one in `TicketTriage.Infrastructure/Pipeline` and registered in DI; the other stubs stay. The `ISimilarTicketRetriever` stub is renamed to the new interface with a minimal change (still returns an empty list).
- FR14: `BatchRunner` is out of scope unless you ask; the pipeline is usable from it through DI.

## Non-Functional Requirements

- NFR1: Deterministic order and behaviour; no LLM call is made by the pipeline itself (only via the ports).
- NFR2: No ticket body, summary or prompt is logged at Information or in the failure table's message field; the stack trace may contain exception text only.
- NFR3: Async with `CancellationToken` everywhere; no `.Result` / `.Wait()`.
- NFR4: Short database transactions; failure logging never holds a transaction across an LLM call (NFR-11).
- NFR5: Warnings are errors; `dotnet build` and tests pass.

## Technical Constraints

- Allowed to change: Core (interfaces, `Ticket.Id`), Infrastructure (`Pipeline/`, the failure store, `Retries` column, failure table, stub rename, DI registration, `Triage` options), appsettings for Web/Batch (settings section only).
- Not changed: Agents, frontend (Razor components), the importer beyond what compile requires, migrations (none exist).
- Dependency direction stays `Core ← Infrastructure ← Agents ← {Web, Batch}`; the pipeline uses Core ports only. The Agents-only resolution status is not available to it, so `TriageSuggestion.ResolutionStatus` stays null.

## Acceptance Criteria

- [ ] AC1: With fake sources and fake ports, a stream of N tickets yields N suggestions in input order.
- [ ] AC2: The similar-ticket source receives the ticket under analysis and the configured `top`.
- [ ] AC3: Team and assignee in the suggestion come from `IRoutingResolver`; priority equals `PriorityMatrix.Resolve` of the classified urgency and impact.
- [ ] AC4: A ticket failing once and then succeeding yields its suggestion; `Retries` and one failure-log row reflect the failed attempt.
- [ ] AC5: A ticket that always fails yields a fallback suggestion after RetryCount attempts; failure rows equal RetryCount; the stream continues with the next ticket.
- [ ] AC6: With `StopSystemOnFailure = true` the first failure is logged and the application stop is requested before any retry.
- [ ] AC7: A timeout counts as a failure; cancellation of the whole stream propagates (not swallowed as a ticket failure).
- [ ] AC8: Validation rejects an empty comment / no services and counts it as a failed attempt.
- [ ] AC9: The failure store persists the column and table (SQLite in-memory test); the pipeline has no EF reference.
- [ ] AC10: `dotnet build TicketTriage.slnx` and `dotnet test --solution TicketTriage.slnx --filter "Category!=Integration"` pass.

## Edge Cases & Failure Modes

- Empty stream → empty result, no error.
- Ticket without `Id` (e.g. from a file) → failures are still logged; `Retries` cannot be persisted, so the in-memory attempt counter is used.
- Failure store itself fails → logged, the attempt is still counted in memory; never masks the original error.
- Whole-stream cancellation → stops promptly, no fallback yielded.
- Ticket text with prompt injection → the pipeline forwards it unchanged to the ports; it does not interpret it.

## Out of Scope

- The LLM agents, the data-source implementations, routing statistics, embeddings, the analysis worker, ingest, the review service, the Web UI.
- Confidence and reasoning (dropped earlier), `LowConfidence` flag, resolution status through the pipeline.
- Parallel processing (sequential by decision), state transitions of the ticket status.
- Migrations (EnsureCreated by decision).

## Requirements changes to agree with you (nothing edited yet)

1. `docs/requirements.md` FR-11 / FR-31 and `architecture.md` §2/§4: `ISimilarTicketRetriever` becomes `ISimilarTicketSource`; add `ITicketSource`; pipeline is stream-based (`TriageAsync` vs docs' `AnalyzeAsync`).
2. FR-34 / ADR-0002 / architecture §5.2.1: the worker's `Attempts`, `ClaimedAt`, `Failed` model conflicts with the new `Retries` column and failure log; both retry mechanisms must not both apply.
3. New requirements for `Triage` settings (RetryCount, StopSystemOnFailure) and the failure-log table.
4. CLAUDE.md says migrations; code uses `EnsureCreated` (also the sqlite-efcore skill).

## Incongruencies (open, yours to decide)

- Docs vs DB statuses (`Analysing/Suggested/Failed` vs `Reviewing/Reviewed/HumanRejected/HumanApproved`), `TrainingDataImporter` looks up status `"Finished"`, and the DB has no Jira key column.
- `IResolutionDrafter` returns only a string; the Agents-side status cannot reach the suggestion.
- `EnsureCreated` never alters an existing database.

## Open Questions

- Assumption: `Retries` counts failed attempts and is reset by nothing in this feature.
- Assumption: with `StopSystemOnFailure = true` the fallback is never yielded (the host stops first).
- Assumption: `ITicketSource` has no filtering parameters.
