# Web triage UI

Human-in-the-loop UI in `TicketTriage.Web`: upload tickets, watch the analysis worker, review suggestions, see metrics.
Requirements: [requirements.md](requirements.md) · Plan: [plan.md](plan.md).

## Pages

| Route | What it does |
|---|---|
| `/` | Dashboard: ticket counts per state (click → filtered list), approval rate (DB), acceptance rate / edits per field / average durations (current session), health checks |
| `/upload` | Drop a JSON file in `challenge.json` format (top-level array of Jira tickets, max 1 MB / 200 entries). Preview shows valid/invalid entries and DB matches; **Save & analyse** stores them and queues them for the agent |
| `/tickets` | Traffic-light list of triage tickets, live updates, filter chips (`?state=Pending` etc.), Re-queue for failed tickets |
| `/review/{id}` | Original next to the suggestion. **Accept** (unchanged), **Save** (with edits), **Reset**, **Reject** (reason required). Priority is always recomputed from `PriorityMatrix` |

## Traffic light

| Light | State | Source |
|---|---|---|
| grey | Queued | in-memory queue |
| blue (pulsing) | Analysing | in-memory worker |
| red + warning | Failed (3 attempts) | in-memory, **Re-queue** button |
| yellow | Pending (`ReviewDecision.Pending`) | DB: suggestion in `*Changed` columns, status not decided |
| green | Approved (`ReviewDecision.Approved`) | DB status `HumanApproved` |
| red | Rejected (`ReviewDecision.Rejected`) | DB status `HumanRejected` |

Field badges show who decides a value, in the pipeline colours of `docs/architecture.md` §4: **Code** (blue), **LLM** (amber), **LLM + Code** (violet).

## How it works

- `Triage/` holds the Web-local services; pages never query EF directly.
- Upload → one transaction inserts tickets (status `New`) → `TriageSessionStore` queues them → `TriageWorker` (BackgroundService) calls `ITriagePipeline` per ticket (scope + 60 s timeout, 3 attempts) → `SuggestionWriter` writes all `*Changed` columns and sets status `Reviewed`.
- Decisions use one conditional `ExecuteUpdateAsync` (only if the ticket is still undecided) → a second tab gets "Already decided".
- Issue key, draft comment, confidence, reject reason, timestamps and edit counts live **in memory** and are lost on restart; tickets, suggestions and decisions are in SQLite.

## Known limitations

- Suggestions come from the team's `Stub*` pipeline until it is replaced (placeholder values, no references).
- `TrainingDataImporter` (Infrastructure) still looks up the removed `Finished` status and throws when `training.json` exists — team fix needed. It also skips the import if the DB has any ticket; the upload page warns when the DB is empty.
- Only the first affected service / service team is persisted (single FK column).
- FR24 (manual ticket entry) not implemented. No authentication (out of scope).
- `dotnet format --verify-no-changes` on the whole solution fails on Windows checkouts (`core.autocrlf=true` vs. `end_of_line = lf`, no `.gitattributes`); check with `--include src/TicketTriage.Web/ tests/TicketTriage.Web.Tests/`.

## Test

```bash
dotnet test --project tests/TicketTriage.Web.Tests/TicketTriage.Web.Tests.csproj
```

Manual demo: start `aspire run`, open `/upload`, drop `tests/TicketTriage.Web.Tests/Fixtures/challenge-sample.json` (5 tickets).
