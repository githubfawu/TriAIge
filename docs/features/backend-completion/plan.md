# Plan: backend-completion

Source: [requirements.md](requirements.md). Backend only (Core, Infrastructure, Agents, Web worker, Batch, tests). No commits or branches by the agent; the user commits.

## 1. Findings that shape the plan

| # | Finding | Consequence |
|---|---|---|
| F1 | TF-IDF corpus, routing statistics and `DbTicketSource` read **all** `Ticket` rows | Challenge tickets would pollute retrieval and votes, and the worker would re-triage history. All three filter on origin (slice B). |
| F2 | `TrainingDataImporter` skips when **any** ticket exists | Ingest before import would block the import forever. Skip by a persisted `TrainingDataReady` marker, which is also the worker's data-ready gate. |
| F3 | Timestamps are `DateTime` UTC; status ids are fixed seeds (New=0, Reviewing=1, Reviewed=2, HumanRejected=3, HumanApproved=4) | New timestamp columns are `DateTime` UTC. Constants class pinned to the seed by a test. |
| F4 | Challenge records have no key; `WorkTypeId` non-null (importer defaults Incident); description truncated to 1000 on import | Ingest stores the received `Ticket` as JSON (`SourcePayload`); the worker triages that, so scoring matches today's direct path. |
| F5 | Pipeline needs classifier and drafter; Infrastructure stubs fill them via `TryAdd*` | Remove stubs; every host keeps `AddTriageAgents`; tests register fakes. |
| F6 | `FormatSimilarTickets` always prints `Resolution status:` | Add a flag; drafter omits it, classifier unchanged. |
| F7 | `FallbackSuggestionFactory` is internal | Still-`New` tickets get their fallback from a new Infrastructure provider. |
| F8 | No Web test project | Worker logic lives in Infrastructure `Analysis/`; Web `BackgroundService` is a thin timer loop. |
| F9 | Lease must exceed the worst case of a batch (5 × 3 × 60.5 s ≈ 15 min) | `LeaseMinutes` default 20, validated against `Triage` options. |

## 2. Architecture

```
Batch (no LLM)
  read challenge ─► ITicketIngestor.IngestAsync(Challenge) ─► ids (challenge order)
  poll IAnalysisMonitor until none pending | timeout | worker dead
  stored suggestion | IFallbackSuggestionProvider (still New) ─► TriageResult ─► atomic write

Web
  InitializeTriageDatabase (import + marker) ─► AnalysisWorker (PeriodicTimer 30 s)
    └─ AnalysisCycle.RunOnceAsync: heartbeat ─► gate ─► sweep stale leases ─► atomic claim ≤5
       ─► per ticket (own scope, isolated): ITriagePipeline ─► save (New→Reviewing if lease still ours)
```

## 3. Design decisions

| ID | Topic | Decision |
|---|---|---|
| D1 | Origin marker | Core `TicketOrigin { Training=0, Challenge=1, Intake=2 }`, int column `Origin`. Ingest columns: `SourceKey`, `SourceHash` (SHA-256), `SourcePayload` (JSON), `IngestedAt`. Unique `(Origin, SourceKey)`. Worker and `DbTicketSource` read `Origin != Training`; corpus and statistics read `Origin == Training`. |
| D2 | Lease | `DateTime? ClaimedAt` stored as INTEGER ticks (exact comparison), unique per process. Status stays `New` while claimed. |
| D3 | Atomic claim | One `ExecuteUpdateAsync` with an id sub-query (`OrderBy(IngestedAt).Take(BatchSize)`), conditions `StatusId==New && Origin!=Training && ClaimedAt==null`; read back by `ClaimedAt==claim`. No transaction across LLM calls. |
| D4 | Save | One short transaction: `UPDATE … WHERE Id AND StatusId=New AND ClaimedAt=@claim` sets Reviewing, clears claim, `Version+1`; if 1 row, insert suggestion; else roll back ("lease lost"). |
| D5 | Sweep | Every tick: release `New` tickets with `ClaimedAt < now − Lease`. On shutdown the worker releases its own claims. |
| D6 | Suggestion tables | `TriageSuggestion` (1:1 by `TicketId`, cascade): Core enum ints, JSON lists, `Priority`, `IsFallback`, `CreatedAtUtc`, review columns (`Decision`, `RejectReason`, `FirstOpenedAtUtc`, `DecidedAtUtc`). `SuggestionEdit` (`TicketId`, `Field`, `AiValue`, `FinalValue`, `EditedAtUtc`, unique `(TicketId, Field)`). |
| D7 | Row version | `long Version` concurrency token, incremented by ingest update, worker save, review, requeue. Not by claim, sweep, `Retries`, first-open. Conflict → `ReviewOutcome.Conflict`. |
| D8 | Data-ready gate | Table `SystemMarker`. Importer writes `TrainingDataReady` in the same `SaveChanges` as the tickets. No marker: worker claims nothing. |
| D9 | Heartbeat | Worker upserts `AnalysisWorkerHeartbeat` every tick (before the gate). |
| D10 | Batch wait | Poll every 5 s. All analysed → export (even if Web is down). Pending + heartbeat stale past 120 s grace → exit 1, no file written, message "start TicketTriage.Web". Worker alive but 1200 s timeout → export with fallbacks, warning, exit 0. |
| D11 | Batch DB init | `EnsureTriageDatabaseCreatedAsync` only; Batch no longer imports (Web does, behind the gate). |
| D12 | Export | Stored AI suggestion (not analyst edits). Still-`New` tickets → deterministic fallback. Summary reports `fallback/failed` and `not analysed`. |
| D13 | Fallback rule | Core `TriageSuggestion.IsFallback` (`IsNullOrWhiteSpace(DraftComment)`) used by Batch and the DB column. |
| D14 | Ingest upsert | Key `(Origin, SourceKey)`. New → insert; same hash → `Unchanged`; changed and not decided → update, drop suggestion and edits, back to `New`, `Retries=0`, `Version+1`; decided → `Locked`; `Training` origin rejected. |
| D15 | Review | `OpenAsync` (sets `FirstOpenedAtUtc` once, no LLM dependencies), `SaveEditsAsync`, `ApproveAsync`, `RejectAsync` (reason required). Priority not editable (matrix). Only differing fields stored. Results as `ReviewResult(Outcome, Version)`. |
| D16 | Requeue | Only failed (`Retries >= RetryCount`) `Reviewing` non-training tickets with matching version: delete suggestion and edits, `Retries=0`, `New`. |
| D17 | Stubs | Delete `Infrastructure/Stubs/*` and their registration. |

## 4. Schema changes (no migrations)

`EnsureCreatedAsync` never alters a DB: **delete `data/triage.db*` after slices B and C** (once if they land together), restart Web to re-import. Optional `PRAGMA journal_mode=WAL` in E1.

| Table | Change | Slice |
|---|---|---|
| `Ticket` | + `Origin`, `SourceKey`, `SourceHash`, `SourcePayload`, `IngestedAt`, `ClaimedAt`, `Version`; unique `(Origin, SourceKey)`; index `(StatusId, Origin, ClaimedAt)` | B |
| `SystemMarker` | new | B |
| `TriageSuggestion`, `SuggestionEdit` | new | C |

## 5. Slices

| Slice | Name | Size | Depends on | Delivers (main files) | Tests | AC |
|---|---|---|---|---|---|---|
| A | Agent quality | S | none | `TicketPromptFormatter` flag, drafter prompt v3 without status, real `TriageAgent` instructions, delete stubs | drafter prompt has no `Resolution status:`; formatter flag; fakes registered in `RegistrationTests`, `FallbackContractTests` | 1, 7 |
| B | Origin marker, lease columns, ready marker | M | A | `TicketOrigin`, `TicketEntity`/`TriageDbContext` columns, `SystemMarkerEntity`, `TicketStatusIds`, importer marker, corpus/statistics/`DbTicketSource` filters | schema and unique index, status-id pin, importer skip by marker, filters, `ClaimedAt` round-trip | 3, 8 |
| C | Suggestion persistence | M | B | `TriageSuggestionEntity`, `SuggestionEditEntity`, `SuggestionMapper`, `SuggestionField`, `TriageSuggestion.IsFallback` | round-trip, priority = matrix, cascade, `IsFallback` theory | 3 |
| D | Ticket ingestor | M | C | `ITicketIngestor`, `IngestResult`, `EfTicketIngestor`, `TicketEntityFactory` | idempotent ingest, positional keys, update resets, locked, training rejected, payload round-trip | 2 |
| E1 | Claim store, gate, options | M | D | `AnalysisOptions`, `TicketClaimStore` | disjoint claims, batch size/order, sweep, save after lost lease, gate, heartbeat, options validation | 3 |
| E2 | Analysis cycle and Web worker | M | E1 | `AnalysisCycle`, Web `AnalysisWorker`, `Program.cs`, `appsettings.json` | cycle with fake pipeline: 3 saved; one failing isolated; gate closed; fallback stored; second cycle idle; cancellation releases | 3 |
| F | Review service | M | C | `IReviewService`, `Review` records, `EfReviewService` | first-open once, no LLM deps (throwing `IChatClient`), approve/edit/reject, matrix priority, conflict, invalid state | 4, 5 |
| G | Batch through the worker | L | E2 | `IAnalysisMonitor`, `IFallbackSuggestionProvider`, `EfAnalysisMonitor`, `DeterministicFallbackProvider`, `BatchRunner` rewrite, `BatchOptions`, summary | order preserved, timeout fallback, worker-down exit 1 without file, all-done without heartbeat exports, cancellation, end-to-end on temp SQLite | 6 |
| H | Requeue failed ticket | S | F | `RequeueFailedAsync` | failed → New and re-claimed; other states rejected | §5.2.2 |
| I | Metrics query service | S | F | `ITriageMetricsService`, `TriageMetrics`, `EfTriageMetricsService` | empty DB, acceptance rate, edits per field, medians, training ignored | FR-25 |
| J | Docs | S | all | architecture, requirements (FR-03/05 dropped, FR-02 deferred), ADR-0002 accepted, CLAUDE.md, feature README | – | 8 |

**Order:** A → B → C → D → E1 → E2 → G → F → H → I → J. G goes right after E2 so the scoring path is restored early; F does not block it.

Slice dependencies: A; B→C→D→E1→E2→G; C→F→H; F→I; all→J.

## 6. Build and verify

```bash
dotnet build TicketTriage.slnx
dotnet test --solution TicketTriage.slnx --filter "Category!=Integration"
dotnet format TicketTriage.slnx --include <changed files> --verify-no-changes   # never solution-wide (CRLF gotcha)
# manual after G: rm data/triage.db*; aspire run (Web imports, worker starts); start batch from the dashboard
```

## 7. Risks

- Two processes on SQLite (Batch ingest and poll, Web worker): short transactions, built-in busy retry, optional WAL. Batch never analyses.
- A reordered challenge file re-ingests as `Updated` and is re-analysed (acceptable, fixed file).
- Re-scoring after a prompt change: only failed tickets can be requeued (H). A `--reanalyse` option is open, out of scope.
- FR-29 (priority enqueue on open) is UI-driven, out of scope.
- A missing training file means the worker never passes the gate (intended, logged, documented).
