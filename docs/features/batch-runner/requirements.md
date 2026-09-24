# Requirements: batch-runner

**Date**: 2026-09-25

## Problem

`TicketTriage.Batch` is the only path that produces the file the challenge scores (`result.json`). Today `BatchRunner` is a TODO: it echoes the input back. Every port behind it (retrieval, classifier, drafter, pipeline) exists, but nothing calls the pipeline, so the project scores zero. This cycle wires the thinnest end-to-end path. It also shows which gaps (routing, resolution status, service catalog, validator) cost the most score, so later cycles can be ordered by evidence.

Planned order of later cycles (one small feature each, each its own `/orchestrate` run): real `ServiceCatalog` names → routing statistics (FR-04/13) → resolution status (FR-16) → full FR-33 validator.

## Users & Context

- Primary: the challenge scoring (batch run, `challenge.json` → `result.json`), started explicitly by a developer.
- Secondary: the developer reading the console summary to see what failed.
- No Web UI work in this cycle. Time budget for the whole cycle: under 4 hours.

## Functional Requirements

- FR1: `BatchRunner` reads `--input` (challenge tickets, JSON array in the Jira export shape, same as `training.json`) and deserializes it into Core `Ticket` records using the Core enum converters.
- FR2: The tickets are streamed through `ITriagePipeline.TriageAsync(IAsyncEnumerable<Ticket>, ct)`. Batch adds no retry, timeout or fallback logic of its own (the pipeline owns it, FR-34).
- FR3: Each suggestion is mapped with `TriageResult.From(...)` and all results are written to `--output` as one JSON array, in input order, exactly one entry per challenge ticket.
- FR4: The file is written only after all tickets are processed (never partial). It is written atomically (temp file, then move) so a crash cannot leave a half-written `result.json`.
- FR5: After the run the console prints a summary: number of tickets, number of fallback/failed tickets (`Retries >= Triage:RetryCount` or fallback suggestion), duration. It never prints ticket text.
- FR6: Exit code 0 when `result.json` was written (including fallback tickets), 2 for invalid arguments (existing behaviour), 1 for an unrecoverable error (unreadable input, invalid JSON, cancellation).
- FR7: With `Triage:StopSystemOnFailure=true` (current Batch appsettings default) the run aborts on the first failed ticket and writes no file. This stays as is for development. The scoring run uses `false`.

## Non-Functional Requirements

- NFR1: All 20 tickets finish in under 10 minutes sequentially (pipeline target NFR-06: under 15 s per ticket).
- NFR2: Deterministic wiring: same input and provider config produce the same structure. Temperature/seed handling stays as in the agents (FR-32, not extended here).
- NFR3: No ticket bodies or prompts logged at Information (CLAUDE.md convention).
- NFR4: Async with `CancellationToken` everywhere. Warnings are errors.

## Technical Constraints

- Projects touched: `TicketTriage.Batch` (runner, options), tests. `TicketTriage.Core` only if `TriageResult` needs a small change. Infrastructure/Agents are used through existing ports and not changed.
- No schema change, no new packages. `Triage` DB is still required (retrieval reads training tickets), so `training.json` must be imported first. Batch already initializes the DB in Development.
- Routing stays the stub (`StubRoutingResolver`), resolution status stays null. The output therefore has empty/default team and assignee and no status field. This is accepted for cycle 1.
- `TriageResult` keeps its current shape (Jira-style field names, comment in `All Comments`).

## Acceptance Criteria

- [ ] AC1: `dotnet run --project src/TicketTriage.Batch -- --input data/challenge.json --output data/result.json` writes a JSON array with exactly as many entries as the input has tickets, in input order, each with the ticket's `Issue key`.
- [ ] AC2: For every entry, `Priority` equals `PriorityMatrix.Resolve(urgency, impact)` of the suggestion (guaranteed by Core, asserted in a test with a fake pipeline).
- [ ] AC3: A ticket whose pipeline result is a fallback still appears in the file, and the summary counts it as failed/fallback.
- [ ] AC4: Unreadable input file, empty array or invalid JSON → clear error message on stderr, exit code 1, no output file created or overwritten.
- [ ] AC5: No partial output: if the run is cancelled or aborted (`StopSystemOnFailure`), an existing `result.json` is left untouched.
- [ ] AC6: Unit tests use a fake `ITriagePipeline` (no LLM, no DB) and cover AC1 to AC5. No Integration-category test is required.
- [ ] AC7: A smoke run against the real provider on the 20 challenge tickets (manual, by the developer) completes within NFR1 and produces a well-formed file. `docs/features/batch-runner/README.md` records the command and result.

## Edge Cases & Failure Modes

- Empty or garbage ticket fields → normalised by the pipeline (FR-10), still one entry.
- Duplicate `Issue key` in the input → processed independently, both entries kept (no dedupe), warning in the log.
- LLM unreachable / rate limit → pipeline retries then falls back per ticket (FR-34). File is still written.
- `UnconfiguredChatClient` (no provider configured) → every ticket falls back. Summary makes this visible (all tickets failed) and exit code is still 0 but a warning line says the provider is not configured.
- Prompt injection in ticket text → handled by the classifier/drafter (user message only). Batch never interpolates ticket text into anything.
- Output directory missing → created (existing behaviour).

## Out of Scope

- Routing statistics, resolution status field, real `ServiceCatalog` names, full FR-33 validator, embeddings, cleaning of resolution templates.
- Running Batch through the analysis worker (open question §7 no. 7).
- Web UI, ingest, review persistence.
- Accuracy evaluation on a held-out training slice. Success is smoke and consistency checks only.
- Changing the result schema. It is dictated by the organizers and is still unknown.

## Open Questions

- Official `result.json` schema (requirements §7 no. 2). Assumption: keep `TriageResult` as is, a JSON array with the current field names. Revisit once the spec or a sample output is available.
- `data/challenge.json` and `data/training.json` are not present in this checkout (only `triage.db`). The developer must place them before AC7. Assumption: the challenge file has the same shape as `training.json` (top-level array).
- Is a top-level array also what the organizers expect for the output (vs. a wrapper object)? Assumption: array.
