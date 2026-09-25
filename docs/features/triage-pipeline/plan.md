# Plan: triage-pipeline

Requirements: [requirements.md](requirements.md). No git commits. Agents, Razor frontend and DB code untouched apart from the permitted failure-handling change (Retries column, failure table, EnsureCreated, no migrations).

## Architecture overview

- **Core**: new ports `ITicketSource` (`IAsyncEnumerable<Ticket> GetTicketsAsync`), `ISimilarTicketSource` (replaces `ISimilarTicketRetriever`, same shape, excludes the ticket itself), `ITriageFailureStore` (`Task<int?> RecordFailureAsync(TriageFailure, ct)` returns persisted `Retries` or null); record `TriageFailure`; `ITriagePipeline` gains a stream overload `IAsyncEnumerable<TriageSuggestion> TriageAsync(IAsyncEnumerable<Ticket>, ct)`; `Ticket` gets `[JsonIgnore] int? Id`.
- **Infrastructure/Pipeline** (no EF, only Core ports + Options/Logging/Hosting.Abstractions): `TriageOptions` (section `Triage`: RetryCount 3, StopSystemOnFailure false, TicketTimeoutSeconds 60, SimilarTicketCount 10, validated on start), `TicketNormalizer`, `SuggestionValidator`, `FallbackSuggestionFactory`, `TriagePipeline` (sequential, input order; per-attempt timeout; failures recorded through the store with the outer token; stop via `IHostApplicationLifetime`; whole-stream cancellation propagates).
- **Infrastructure/Persistence**: `TicketEntity.Retries`, `TriageFailureEntity` (table `TriageFailure`, no FK on TicketId), `TriageDbContext` DbSet, `EfTriageFailureStore` (short transaction per call).
- **DI**: stub retriever renamed to `StubSimilarTicketSource`, `StubTriagePipeline` deleted, real pipeline and store registered, `TriageOptions` bound. Delete `data/triage.db` once after the schema change.
- Reasons in the failure log never contain ticket text or `ex.Message`; the stack trace is stored in its own column.

## Slices

| # | Slice | Goal | Complexity | AC |
|---|---|---|---|---|
| 1 | Core contracts + happy-path stream pipeline | New ports, stub rename, `TriagePipeline` (FR3 order, normalization, no retries), `Triage` settings in Web/Batch appsettings, new `tests/TicketTriage.Infrastructure.Tests` project (+ slnx line, InternalsVisibleTo) | M | AC1, AC2, AC3 |
| 2 | Failure persistence | `ITriageFailureStore`, `Retries` column, failure table, EF store, SQLite in-memory tests | S/M | AC9 |
| 3 | Retry, timeout, validation, fallback | `SuggestionValidator`, `FallbackSuggestionFactory`, retry loop, failure recording | L | AC4, AC5, AC7, AC8 |
| 4 | StopSystemOnFailure + wiring | Stop branch, `Microsoft.Extensions.Hosting.Abstractions` 10.0.12 in `Directory.Packages.props`, full gate | S | AC6, AC10 |

Dependencies: 1 → 2 → 3 → 4 (1 and 2 both edit `TriageAbstractions.cs` and the DI extension, so sequential).

## Cross-project consequences

- Removing `ISimilarTicketRetriever` only breaks Core/Infrastructure (grep-verified); Agents, Web, Batch and tests do not reference it. Doc text mentions it in CLAUDE.md, architecture.md and the agent-framework skill (text only, after your approval).
- The new stream overload has no callers yet (`BatchRunner` is a TODO).
- `Ticket.Id` is additive, so existing tests still compile.
- An old `data/triage.db` fails with "no such column: Retries" until deleted.

## Commands

```bash
dotnet build TicketTriage.slnx
dotnet test --solution TicketTriage.slnx --filter "Category!=Integration"
dotnet format TicketTriage.slnx --verify-no-changes
```

## Decisions (plan assumes the first option)

- D1 retry bound: stop when `max(attempts this run, persisted Retries) >= RetryCount`. Alternative: read `Retries` first and return the fallback with no LLM call if already exhausted.
- D2 stop: stream ends quietly after `StopApplication`; the single-ticket overload throws `OperationCanceledException`.
- D3 fallback services: the single most frequent service of the similar tickets.
- D4 failure table has no FK on `TicketId` so file tickets are still logged.
- D5 no `ITicketSource` stub registered (others implement it). Alternative: empty `TryAdd` stub.
- D6 `StopSystemOnFailure` false everywhere. Alternative: true in `appsettings.Development.json`.
- D7 services are not validated against `ServiceCatalog` (still placeholders).
