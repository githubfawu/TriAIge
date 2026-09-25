# Plan: batch-runner

**Date**: 2026-09-25 · **Source of truth**: [requirements.md](requirements.md) · **Projects**: Batch and a new Batch test project. Core, Infrastructure and Agents are not changed (see D3). No git commits are made by the workflow (the user commits manually).

## 1. Findings that shape the plan

| # | Finding | Consequence |
|---|---|---|
| F1 | `ITriagePipeline` (and classifier, routing, drafter) is registered **scoped**. `Program.cs` resolves the transient `BatchRunner` from the root provider, which throws with scope validation in Development. | `Program.cs` creates an `AsyncServiceScope` and resolves `BatchRunner` from it. |
| F2 | `Ticket` has no enum properties (work type, urgency, impact, priority are raw strings) and every property has `[JsonPropertyName]`. `TrainingDataImporter` uses a private `JsonSerializerDefaults.Web` options instance. `Key`/`Summary` are `required`. | Batch uses its own `static readonly` options with `JsonSerializerDefaults.Web`. Enum converters only matter on output (attributes on the Core enums). |
| F3 | Neither `TriageSuggestion` nor the pipeline flags a fallback. Challenge tickets have `Id == null`, so no `Retries` are persisted. But `SuggestionValidator` rejects a blank `DraftComment` on every successful path and `FallbackSuggestionFactory` always sets `DraftComment = null` (pinned by existing tests). | A fallback is `string.IsNullOrWhiteSpace(suggestion.DraftComment)`. No Core change. |
| F4 | With `StopSystemOnFailure` the pipeline calls `StopApplication()` and throws `OperationCanceledException`. `Program.cs` only catches `OptionsValidationException`. Options are validated lazily inside `RunAsync`. | Exit-code mapping moves into a small, testable `BatchCommand` (slice 2). |
| F5 | `RunWorkingDirectory` is the Batch project folder, so `--input data/challenge.json` resolves to `src/TicketTriage.Batch/data/`. | Keep path resolution. README and AC7 use `../../data/...` or the launch profile. |
| F6 | `UnconfiguredChatClient` is internal to Agents, so Batch cannot detect an unconfigured provider directly. | Warn when **every** ticket fell back (count > 0). |

## 2. Architecture overview

```
Program.cs (host, DI, DB init in Development)
  └─ scope → BatchCommand.ExecuteAsync(runner, stdout, stderr, ct) : int     ← exit codes, summary print
       └─ BatchRunner.RunAsync(ct) : BatchSummary
            1. read + deserialize input → List<Ticket> (empty/null/invalid → BatchInputException)
            2. warn on duplicate keys (key only, no text)
            3. ITriagePipeline.TriageAsync(tickets as IAsyncEnumerable) → TriageResult.From, count fallbacks
            4. assert results.Count == tickets.Count
            5. ct.ThrowIfCancellationRequested(); write to temp file in output dir → File.Move(overwrite: true)
            6. return BatchSummary(Total, Fallbacks, Duration, OutputPath)
```

- `BatchRunner(IOptions<BatchOptions>, ITriagePipeline, TimeProvider, ILogger<BatchRunner>)`. Nothing is written until the stream has finished, so cancellation or the StopSystemOnFailure exception leaves an existing `result.json` untouched.
- Output: indented JSON, top-level array of `TriageResult`, input order. Logging: paths, counts, keys only (NFR3).

## 3. Design decisions

| ID | Decision | Reason |
|---|---|---|
| D1 | New test project `tests/TicketTriage.Batch.Tests` (copy of Infrastructure.Tests csproj + reference to Batch; one line in `TicketTriage.slnx`; no `Directory.Packages.props` change) | Batch is an Exe without a test home; Infrastructure.Tests must not reference Batch. |
| D2 | `BatchCommand` (static) maps exceptions to exit codes and prints to injected `TextWriter`s | AC4/AC5 become unit-testable without a host. |
| D3 | Fallback = blank `DraftComment`, one helper `BatchRunner.IsFallback` with a *why* comment | Avoids Core/Infrastructure changes. Alternative: explicit `IsFallback` on `TriageSuggestion` (touches Infrastructure). |
| D4 | `BatchInputException` wraps IO and JSON errors, empty array, null root, null elements | One clear stderr message per cause, JSON line/path but never values. |
| D5 | Temp file in the **same directory** as the output | `File.Move` is a same-volume rename; a crash leaves at most a stray temp file. |
| D6 | No change to `TriageResult`, `appsettings.json` or path resolution | Explicit requirement. Scoring runs set `Triage__StopSystemOnFailure=false`. |

## 4. Slices

| # | Slice | Goal | ACs | Complexity |
|---|---|---|---|---|
| 1 | Thin end-to-end path | Valid input → pipeline → atomic `result.json`, replaces the echo TODO | AC1, AC2, AC3 (file), AC6 (part) | M |
| 2 | Failure paths and exit codes | Bad input / cancel / StopSystemOnFailure → exit 1, stderr message, output untouched | AC4, AC5, AC6 | S-M |
| 3 | Summary, warnings, docs, smoke run | Console summary, all-fallback warning, README, doc status updates, manual AC7 | AC3 (summary), AC7 | S |

Dependencies: strictly sequential 1 → 2 → 3. Build green and tests passing after each slice.

### Slice 1: Thin end-to-end path

| File | Project | New/Mod | Change |
|---|---|---|---|
| `src/TicketTriage.Batch/BatchRunner.cs` | Batch | Mod | Deserialize `List<Ticket>`, stream through `ITriagePipeline`, map `TriageResult.From`, count check, atomic write, return `BatchSummary`. Minimal null-root guard. |
| `src/TicketTriage.Batch/BatchSummary.cs` | Batch | New | `record BatchSummary(int Total, int Fallbacks, TimeSpan Duration, string OutputPath)` |
| `src/TicketTriage.Batch/Program.cs` | Batch | Mod | Resolve `BatchRunner` from an `AsyncServiceScope` (F1) |
| `tests/TicketTriage.Batch.Tests/TicketTriage.Batch.Tests.csproj` | Tests | New | Copy of Infrastructure.Tests csproj, reference to Batch |
| `tests/TicketTriage.Batch.Tests/TestSupport.cs` | Tests | New | `FakeTriagePipeline` (per-key suggestion, fallback keys, OCE at index N), temp-dir helper |
| `tests/TicketTriage.Batch.Tests/BatchRunnerTests.cs` | Tests | New | see below |
| `TicketTriage.slnx` | root | Mod | Register the test project |

Tests: one entry per ticket in input order with `Issue key` (AC1); Priority equals the matrix result (AC2); fallback ticket still written with empty `All Comments` (AC3); existing output replaced and no temp file left; missing output directory created; duplicate keys both kept; Jira enum names (`"Service Request"`) written.

### Slice 2: Failure paths, no partial output, exit codes

| File | Project | New/Mod | Change |
|---|---|---|---|
| `src/TicketTriage.Batch/BatchInputException.cs` | Batch | New | Input error type |
| `src/TicketTriage.Batch/BatchRunner.cs` | Batch | Mod | Wrap read/deserialize errors, validate before pipeline or output IO, temp cleanup in `finally` |
| `src/TicketTriage.Batch/BatchCommand.cs` | Batch | New | `ExecuteAsync(runner, stdout, stderr, ct)`: options validation → 2, `BatchInputException` → 1, `OperationCanceledException` → 1 ("no output written"), unexpected → 1 (type name only), success → 0 |
| `src/TicketTriage.Batch/Program.cs` | Batch | Mod | Thin: call `BatchCommand.ExecuteAsync`; DB init stays inside the protected block |
| `tests/TicketTriage.Batch.Tests/BatchCommandTests.cs` | Tests | New | see below |

Tests: missing file, invalid JSON, `[]`, `null`, `[null]` → exit 1, stderr non-empty, no output created and a pre-existing file unchanged (AC4); OCE at ticket 2 and mid-stream cancellation → exit 1, `result.json` byte-identical (AC5); missing `--input` → exit 2; stderr/stdout never contain ticket text (marker test).

### Slice 3: Summary, warnings, docs, smoke run

| File | Project | New/Mod | Change |
|---|---|---|---|
| `src/TicketTriage.Batch/BatchCommand.cs` | Batch | Mod | Print `Tickets: N · fallback/failed: F · duration · output`; warning line when `F == N && N > 0` |
| `src/TicketTriage.Batch/BatchRunner.cs` | Batch | Mod | `LogWarning` on duplicate keys (key only), one `LogInformation` with counts |
| `tests/TicketTriage.Batch.Tests/BatchCommandTests.cs` | Tests | Mod | Summary counts, all-fallback warning, no warning for mixed results, no ticket text in stdout |
| `docs/features/batch-runner/README.md` | docs | New | Command, prerequisites, exit codes, fallback rule, AC7 result |
| `CLAUDE.md`, `docs/architecture.md`, `docs/requirements.md` (FR-30), `docs/features/triage-pipeline/README.md`, `README.md` | docs | Mod | Remove "BatchRunner is a TODO", describe the new state |

## 5. Schema changes

None. No EF entity or seed change, no `data/triage.db*` deletion. Batch still needs the imported training DB (Development init).

## 6. Build and test

```bash
dotnet build TicketTriage.slnx
dotnet test --solution TicketTriage.slnx --filter "Category!=Integration"
dotnet format TicketTriage.slnx --verify-no-changes
# AC7 (manual, real provider, data/challenge.json present), scoring run with StopSystemOnFailure off:
#   PowerShell: $env:Triage__StopSystemOnFailure="false"
dotnet run --project src/TicketTriage.Batch -- --input ../../data/challenge.json --output ../../data/result.json
```

## 7. Risks

| ID | Risk | Mitigation |
|---|---|---|
| R1 | The real `challenge.json` shape is unverified (not in the checkout). Non-array list fields, non-ISO `Created` or a missing `Summary` fail the whole run (`required`). | Clear `BatchInputException` with line/path. Verify during AC7. A fix belongs in `Ticket`/converters (follow-up). |
| R2 | AC1's literal `data/challenge.json` resolves against `src/TicketTriage.Batch`. | Document the `../../data/...` form or the launch profile. |
| R3 | Fallback heuristic (blank `DraftComment`) breaks if the validator allows empty comments later. | *Why* comment; existing tests pin both sides; switch to an explicit `IsFallback` flag if FR-16/FR-33 change this. |
| R4 | Scoped pipeline resolved from root fails in Development. | Scope in `Program.cs` (slice 1); verify once with a manual run. |
| R5 | `StopSystemOnFailure=true` (Batch default) aborts the scoring run on the first flaky LLM call. | Intended for dev; README says to set `false` for scoring; exit-1 message names the setting. |
| R6 | NFR1: worst case per ticket is `RetryCount x TicketTimeoutSeconds` = 180 s. | Record the duration in the AC7 smoke run. Tuning is out of scope. |
| R7 | Default JSON escaping writes umlauts as `ü`. | Valid JSON; leave unless the organizers' schema says otherwise. |
| R8 | `JsonException.Message` could include input fragments. | Build stderr from line, byte position and path only. |
