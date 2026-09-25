# Requirements: upload-frontend

**Date**: 2026-09-25

## Problem
Triage results can only be produced via the Batch CLI on a fixed file path. The user wants a quick web page to upload a Jira-export JSON, have the agent work through it, and download the triaged JSON, without touching the command line.

## Users & Context
Single analyst / demo presenter using `TicketTriage.Web` (Blazor Server, MudBlazor). Not a full review UI; the existing Tickets/Review pages stay as they are.

## Functional Requirements
- FR1: New page `/upload` (nav link) with a file picker for a `.json` file (challenge envelope with `records`, or plain array; keyless records allowed, same as Batch).
- FR2: On submit the tickets are ingested via `ITicketIngestor` with `TicketOrigin.Challenge`; the existing `AnalysisWorker` analyses them (the page never calls the LLM itself).
- FR3: The page shows live progress by polling `IAnalysisMonitor`: analysed n of N, pending, fallback count; it also warns if the worker heartbeat is stale.
- FR4: When nothing is pending (or the user stops waiting), the page offers "Download result.json": identical shape to Batch output (input mirrored, predicted fields filled, no `Issue key` added), built via `ChallengeDocument.ToOutput` + `TriageResult.From`; unanalysed tickets use `IFallbackSuggestionProvider`.
- FR5: A preview table of the results (row #/key, summary, work type, services, priority, team, assignee, status; fallbacks marked). Model text rendered as plain text only.
- FR6: Uploaded tickets also show up in the existing Tickets/Review UI (consequence of ingest).

## Non-Functional Requirements
- NFR1: Reuse the Batch logic (`ChallengeDocument`, result mapping, fallback handling) rather than duplicating it; no change to the `result.json` contract.
- NFR2: Upload size cap 10 MB and 500 records (same limits as Batch).
- NFR3: No ticket bodies logged at Information.

## Technical Constraints
- Projects: Web (page + small service), plus moving/sharing `ChallengeDocument` so Web can use it (currently in Batch, path-based only; needs a stream/`JsonNode` entry point). No schema change, no new entities.
- Uses `IBrowserFile.OpenReadStream(maxAllowedSize)` (MudFileUpload or InputFile); download via JS interop / stream reference.
- No LLM during prerender; poll with a timer and `InvokeAsync(StateHasChanged)`.

## Acceptance Criteria
- [ ] AC1: Uploading the challenge file (envelope) on `/upload` ingests all 20 tickets and progress reaches 20/20.
- [ ] AC2: The downloaded file has the same envelope/records structure as the input with all 7 predicted fields filled and no `Issue key` added; matches what Batch would write for the same input.
- [ ] AC3: Plain-array input works; invalid JSON, empty array, oversized file show a readable error on the page (no crash, no data values in the message).
- [ ] AC4: Worker not running -> the page says so instead of waiting forever.
- [ ] AC5: Unit tests for the shared document/result-building code and for the upload service (fake ingestor/monitor); `dotnet build` and `dotnet test --filter "Category!=Integration"` green.

## Edge Cases & Failure Modes
- Malformed / non-array JSON / empty -> error message with position or type only.
- Re-upload of the same file -> ingestor outcome Unchanged/Updated/Locked; page shows counts and proceeds.
- Ticket analysis fails -> fallback suggestion, marked in the preview.
- Prompt injection in ticket text -> unchanged pipeline handling; preview renders plain text (no `MarkupString`).
- User navigates away mid-analysis -> worker keeps going; results stay in DB, no cleanup needed.

## Out of Scope
- Editing/approving suggestions on this page, authentication, multi-user job history, CSV input, cancelling analysis, styling polish, removing Batch.

## Open Questions
- Where `ChallengeDocument` lives (Infrastructure vs. Core vs. new shared file): decided in the plan; assumption: move to Infrastructure, Batch references it from there.
- Wait timeout for the page: assumption: reuse `BatchOptions`-like defaults (poll 2 s, stop-waiting after configurable timeout, user can download partial results with fallback).
