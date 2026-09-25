# Web triage UI

Human-in-the-loop UI in `TicketTriage.Web`: upload tickets, watch the analysis worker, review suggestions, see metrics.
Requirements: [requirements.md](requirements.md) · Plan: [plan.md](plan.md).

Built on top of the team's real backend (`TicketTriage.Core`/`Infrastructure` ingestion, analysis worker and review
service) - this project no longer has its own pipeline plumbing (see "How it works" below).

## Pages

| Route | What it does |
|---|---|
| `/` | Dashboard: ticket counts per state (click → filtered list), review metrics from `ITriageMetricsService` (acceptance rate, edits per field, median durations), health checks |
| `/upload` | Drop a challenge JSON file (envelope with `records` or a plain array). Ingests via `ChallengeUploadService`, polls the analysis worker, offers **Download result.json** |
| `/tickets` | Traffic-light list of triage tickets, refreshed every 2 s, filter chips (`?state=Pending` etc.), Re-queue for failed tickets |
| `/review/{id}` | Original next to the suggestion. **Accept** (unchanged), **Save** (with edits), **Reset**, **Reject** (reason required). Priority is always recomputed from `PriorityMatrix` |

## Traffic light

| Light | State | Source |
|---|---|---|
| grey | Queued | Ticket status `New`, not claimed, retries below the limit |
| blue (pulsing) | Analysing | Ticket status `New`, claimed by the analysis worker |
| red + warning | Failed | Ticket status `New`, retries exhausted (`TriageOptions.RetryCount`) - **Re-queue** button (`IReviewService.RequeueFailedAsync`) |
| yellow | Pending (`ReviewDecision.Pending`) | Ticket status `Reviewing`/`Reviewed` - a suggestion is stored, awaiting a human decision |
| green | Approved (`ReviewDecision.Approved`) | Ticket status `HumanApproved` |
| red | Rejected (`ReviewDecision.Rejected`) | Ticket status `HumanRejected` |

Field badges show who decides a value, in the pipeline colours of `docs/architecture.md` §4: **Code** (blue), **LLM** (amber), **LLM + Code** (violet).

## How it works

- `Triage/` holds only Web-local UI helpers: `ReviewFormModel` (form state + `PriorityMatrix.Resolve`), `TicketDisplayState`
  (+ `SuggestionFieldOwners` for the badges), `TriageVocabulary` (enum display names) and `TicketBoardQuery` (the one
  read-only query the board/dashboard need that main's ports don't provide in bulk - it reads `TicketEntity`/
  `TriageSuggestionEntity`/`TriageFailureEntity` directly, `AsNoTracking`, never the ~20k training rows).
  `Upload/ChallengeUploadService` and `Analysis/AnalysisWorker` are the team's.
- Upload → `ChallengeUploadService.UploadAsync` ingests via `ITicketIngestor` (status `New`) → the background
  `AnalysisWorker`/`ITriagePipeline` (Infrastructure) analyses and stores a `TriageSuggestion` → the Upload page polls
  `IAnalysisMonitor` and exports `result.json` once complete (or on demand, with fallbacks for anything still pending).
- The Review page never injects `ITriagePipeline` (opening a ticket never calls the LLM): it reads/writes only through
  `IReviewService` (`OpenAsync`/`ApproveAsync`/`RejectAsync`/`RequeueFailedAsync`), which handles optimistic concurrency
  via the ticket's row version.
- Everything (issue key, suggestion, decision, edits, timestamps) is in SQLite; nothing is session-only RAM state
  anymore. The Tickets/Review pages poll every 2 s instead of subscribing to an event, since main's backend has no
  ticket-changed notification.

## Known limitations / deviations from the original Slice 1-3 design

- No manual "Analyse now" for a ticket stuck outside the worker's queue: main's `AnalysisWorker` polls on a fixed
  interval and always picks up every `New` ticket itself, so there is nothing to trigger by hand anymore.
- No per-suggestion "confidence" score: `TriageSuggestion` doesn't have one. `IsFallback` (shown as a warning banner)
  is the closest available signal.
- The Review page can't tell "training ticket" apart from "unknown id" - `IReviewService.OpenAsync` returns `null`
  for both, so both show the same "Ticket #n was not found" message.
- Affected services are a genuine multi-select now (main's `TriageSuggestion`/`ReviewEdits` support a list); service
  team is still a single value, matching `ReviewEdits.ServiceTeams` (`string?`, not a list).
- `dotnet format --verify-no-changes` on the whole solution still fails on Windows checkouts because of a pre-existing
  CRLF/LF mismatch in `tests/TicketTriage.Web.Tests/TestSupport.cs` (`core.autocrlf=true`, no `.gitattributes`); check
  with `--include src/TicketTriage.Web/ tests/TicketTriage.Web.Tests/` and expect only that file to differ.

## Test

```bash
dotnet test --solution TicketTriage.slnx --filter "FullyQualifiedName~TicketTriage.Web.Tests"
```

Manual demo: start `aspire run`, open `/upload`, drop `tests/TicketTriage.Web.Tests/Fixtures/challenge-sample.json` (5 tickets).
