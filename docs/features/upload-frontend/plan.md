# Plan: upload-frontend

**Date**: 2026-09-25 · **Mode**: fast (3 slices) · **Requirements**: [requirements.md](requirements.md)

## Architecture overview

Same path Batch uses: **parse → ingest (`TicketOrigin.Challenge`) → in-process `AnalysisWorker` analyses → poll `IAnalysisMonitor` → build results (fallback for unanalysed) → mirror input into `result.json`**. The page never calls the LLM.

### Key decisions

1. **`ChallengeDocument` moves to `Infrastructure/Challenge/`** (namespace `TicketTriage.Infrastructure.Challenge`). Batch and Web (via Agents) both reference Infrastructure. New entry points: `ReadAsync(Stream, ct)`, `Parse(JsonNode?)`, `WriteAsync(JsonNode, Stream, ct)` (single indented serializer, so Batch and Web output is byte-identical). Throws new `ChallengeFormatException` (positions/type names only, never values or paths). `MaxFileBytes` / `MaxRecords` become public.
2. **Batch keeps a path wrapper** `Batch/ChallengeFile.cs` (size check, open file, maps exceptions to `BatchInputException` with the path). `BatchCommand`, exit codes unchanged.
3. **Result loop extracted** to `Infrastructure/Challenge/ChallengeResults.cs`: static `BuildAsync(ids, states, fallbackProvider, ct)` → `ChallengeResultSet(Rows, Fallbacks, NotAnalysed)` with `ChallengeResultRow(Position, Ticket, TriageResult, IsFallback, WasAnalysed)`. Same semantics as today (fallback cache per id, blank `DraftComment` = fallback).
4. **`WorkerLiveness.IsAlive(heartbeat, now, maxAge)`** in `Infrastructure/Analysis/`, shared by Batch and Web.
5. **`ChallengeUploadService`** (Web, scoped, no Blazor types) resolves `ITicketIngestor` from a new `IServiceScopeFactory` scope per upload (scoped DbContext must not live for a circuit).
6. **Page drives waiting** with a `PeriodicTimer` calling `GetProgressAsync`; new `Upload` options (`PollIntervalSeconds`=2, `WaitTimeoutSeconds`=1200, `WorkerHeartbeatMaxAgeSeconds`=90, `WorkerStartGraceSeconds`=120; defaults in code).
7. **Download** via `DotNetStreamReference` + `wwwroot/js/download.js`.

### Service contract

```csharp
UploadSession(ChallengeDocument Document, IReadOnlyList<int> TicketIds, IngestCounts Counts, IReadOnlyList<string> DuplicateKeys, DateTimeOffset StartedAt)
IngestCounts(int Created, int Updated, int Unchanged, int Locked)
enum WorkerStatus { Alive, Starting, NotRunning }
UploadProgress(int Total, int Analysed, int Pending, int Fallbacks, WorkerStatus Worker, bool TimedOut) { bool IsComplete => Pending == 0; }
UploadExport(IReadOnlyList<ChallengeResultRow> Rows, byte[] Json, int Fallbacks, int NotAnalysed)

Task<UploadSession> UploadAsync(Stream json, CancellationToken ct);   // throws ChallengeFormatException (also for oversized IOException)
Task<UploadProgress> GetProgressAsync(UploadSession s, CancellationToken ct);
Task<UploadExport> ExportAsync(UploadSession s, CancellationToken ct); // pending -> fallback
```

## Slices

| # | Slice | Projects | Complexity | AC |
|---|---|---|---|---|
| 1 | Shared challenge document + result building, Batch refactor | Infrastructure, Batch, tests | M | AC5, NFR1, groundwork AC2/AC3 |
| 2 | `ChallengeUploadService` + new `Web.Tests` project | Web, tests | M | AC1–AC4 (service), AC5 |
| 3 | `/upload` page, nav link, download JS | Web | M | AC1–AC4 e2e, FR1/3/5/6 |

### Slice 1 files
- Infrastructure: `Challenge/ChallengeDocument.cs` (moved), `Challenge/ChallengeFormatException.cs`, `Challenge/ChallengeResults.cs`, `Analysis/WorkerLiveness.cs`.
- Batch: delete `ChallengeDocument.cs`; add `ChallengeFile.cs`; `BatchRunner.cs` uses `ChallengeFile`, `ChallengeResults.BuildAsync`, `WorkerLiveness.IsAlive`, `ChallengeDocument.WriteAsync` (static, so ctor unchanged; keep `internal static IsFallback`).
- Tests: mechanical edit of `Batch.Tests/ChallengeDocumentTests.cs` (call `ChallengeFile.ReadAsync`); new `Infrastructure.Tests/ChallengeDocumentStreamTests.cs`, `ChallengeResultBuilderTests.cs`, `WorkerLivenessTests`. Existing Batch assertions must stay green.

### Slice 2 files
- Web: `Upload/UploadOptions.cs`, `Upload/UploadModels.cs`, `Upload/ChallengeUploadService.cs`; `Program.cs` (options + `AddScoped`); csproj `InternalsVisibleTo TicketTriage.Web.Tests`.
- Tests: new `tests/TicketTriage.Web.Tests` (csproj, in `TicketTriage.slnx`), `TestSupport.cs` (fakes adapted from Batch.Tests), `ChallengeUploadServiceTests.cs`: 20 keyless records ingest as Challenge `#1..#20`; array input; invalid/empty/oversized -> `ChallengeFormatException` without values; re-upload counts; Starting/NotRunning/TimedOut; export with a pending ticket uses fallback once; export bytes equal Batch-style output, no `Issue key`, envelope kept; ingestor resolved per scope.

### Slice 3 files
- Web: `Components/Pages/Upload.razor` (states Idle/Uploading/Waiting/Ready/Error; `MudFileUpload` `.json`; ingest summary + Locked warning; `MudProgressLinear`; worker-not-running warning; "Stop waiting"; "Download result.json"; preview `MudTable` with fallback chips, plain `@` binding, no `MarkupString`; link to `/tickets`), `Layout/MainLayout.razor` nav link, `wwwroot/js/download.js`, `Components/App.razor` script tag, `_Imports.razor` usings.
- Tests: no bUnit (fast mode); manual check with `aspire run`: upload the challenge file (20/20, diff against Batch output), array file, broken JSON, `[]`, >10 MB, worker disabled.

## Schema changes
None (no need to delete `data/triage.db*`).

## Build commands
```bash
dotnet build TicketTriage.slnx
dotnet test --solution TicketTriage.slnx --filter "Category!=Integration"
dotnet format TicketTriage.slnx --include <changed files> --verify-no-changes   # never solution-wide
aspire run                                                                        # Slice 3 manual check
```

## Dependencies
Slice 1 → Slice 2 → Slice 3 (sequential). Slice 1 is independently shippable.

## Risks
- **Positional-key collision** (existing Batch behaviour): a different keyless file overwrites `#1..#n` from an earlier upload/Batch run; if such a row is Locked, export uses the old stored suggestion. Mitigation: Locked warning on the page; real fix (per-file key namespace) out of scope; document in README and architecture §7.
- Worker cadence (`Analysis:IntervalSeconds`=30, batch 5): 20 tickets take a few minutes; "Stop waiting" exists.
- `TreatWarningsAsErrors` applies to the new test project.

## Docs to update
`docs/features/upload-frontend/README.md`; `docs/architecture.md` (§2 layout, §5.4 second entry point, §7 key collision); `CLAUDE.md` (layout: `Infrastructure/Challenge/`, Web `Upload`, `Web.Tests`; note ingestor/worker/monitor exist).
