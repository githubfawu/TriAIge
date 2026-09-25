# Feature: score-completeness

Makes the batch run produce a scoreable `result.json` on the **real** organizer files: all seven predicted fields per record, in the data's vocabulary, mirroring the input records. Sources: [requirements.md](requirements.md), [plan.md](plan.md).

## What / how

| Concern | Behaviour |
|---|---|
| Input | The real challenge file (envelope with `records`, or a plain array). Records carry no `Issue key`; Batch assigns positional identities `#1..#n` internally (never written to the output). |
| Output | The input container is mirrored: each record is cloned, predicted fields are overwritten, all other fields stay. Predicted fields are defined once in Core `TriageResult` (Jira vocabulary via `JiraVocabulary`). |
| Service | Chosen by the LLM, validated against the 20 real names in `ServiceCatalog`. |
| Team / assignee | `StatisticsRoutingResolver`: majority per service (and per service+team for the assignee), computed once per process from SQLite; ties resolve alphabetically (case-insensitive). The first classified service wins. |
| Priority | Always `PriorityMatrix.Resolve(urgency, impact)`. |
| Resolution status / comment | LLM drafter, validated; fallback = similar-ticket status majority, else `done` (slice 3). |

## Configuration

`TrainingData:Path` (default `../../data/jira_first_20000_requested_fields_synthetic.json`), AppHost `Data:TrainingFile` / `Data:ChallengeFile`, Batch `--input` / `--output`. Run details: plan section 6. No schema change; **delete `data/triage.db*` once** so the real 20 000 rows are imported (the importer skips when any ticket exists).

## Tests

- Core / Batch / Infrastructure unit tests per slice (`ChallengeDocumentTests`, `JiraVocabularyTests`, `RoutingStatisticsTests`, `StatisticsRoutingResolverTests`, ...).
- `Category=Integration` (needs the real training file; skipped when missing; `TRIAGE_TRAINING_FILE` overrides the path):
  `dotnet test --project tests/TicketTriage.Infrastructure.Tests --filter "Category=Integration" --output Detailed`
  - `TrainingDataImporterTests.Import_RealTrainingFile_...` (AC6)
  - `SignalMeasurementTests` (FR9 / AC7, numbers below)

## FR9: is the resolution status (or the assignee) predictable from the ticket text?

Method (`SignalMeasurementTests`): resolved tickets with a description (16 969) in file order; every 5th (`index % 5 == 0`) is held out (n = 3 394), the rest (13 575) is the train set. A `TfIdfIndex` over the train descriptions (the same engine the pipeline uses) returns the top 10 neighbours for each held-out description; the label is the similarity-weighted majority (ties ordinal). 95% CI = Wilson interval.

| Prediction | Accuracy | 95% CI | Baseline | Verdict |
|---|---|---|---|---|
| Status, kNN top-10 majority | 25.3% (858/3394) | 23.8% - 26.8% | 25.0% uniform (4 classes) | no signal |
| Status, majority class (`cannot reproduce`) | 26.2% (888/3394) | 24.7% - 27.7% | 25.0% | no signal |
| Status, majority per work type | 26.2% (888/3394) | 24.7% - 27.7% | 25.0% | no signal |
| Assignee, service majority (what routing does) | 3.5% (118/3394) | 2.9% - 4.1% | 3.3% (1/30) | chance |
| Assignee, kNN top-10 majority | 3.5% (120/3394) | 3.0% - 4.2% | 3.3% (1/30) | chance |
| Service -> team determinism (all 20 000) | 100% (20 000/20 000) | - | - | deterministic |

**Conclusion:** no learnable signal for status or assignee in the training file. Status must come from LLM semantics of the ticket text (the drafter must not copy similar tickets' statuses as evidence); the assignee is a majority vote and will score about chance whatever the pipeline does. Team is fully determined by service, so team accuracy equals service accuracy.

## Data facts (plan F4-F7)

- **F4 vocabulary**: training `Resolution` is `done / cancelled / clarification / cannot reproduce` (lowercase) or null (3 031); urgency and impact use `highest ... lowest` (challenge capitalised); Core `Urgency` / `Impact` names differ, mapped for output by `JiraVocabulary` (assumption: Critical = Highest, Major = Highest, ... No Impact = Lowest).
- **F5**: `Resolution` is the status, not text; real resolution notes sit in `All Comments` as `"<email>: Resolution: ..."` (5 814 comments, 21 distinct texts) beside templates.
- **F6 routing**: service -> team is 1:1 in all 20 services; the assignee is near-random (every service has all 30 assignees, top one about 5% in-sample). Business Entity / Work type do not help.
- **F7 status**: classes near-uniform (4 168-4 322 each), independent of work type; only 173 distinct descriptions (groups of 15-4 212), so the top-10 neighbours are often copies of one template.

## Limitations

- The output schema (container, casing, `All Comments` semantics) is an assumption pending organizer confirmation (plan C3, C6, C7).
- Fields 4 (assignee) and 6 (status) cannot exceed chance by learning from training data.

### Known limitations from review (not fixed)

- The drafter prompt still prints similar tickets' `Resolution status:` although the instructions tell the model to ignore it (FR9: no signal). Removing it from the drafter prompt (keeping it in the classifier) is a small follow-up.
- `FallbackSuggestionFactory` derives the fallback status through a JSON round-trip and relies on `Done` being enum value 0 as default. Works, but could be simplified.
- The positional key `#n` is recognised by string form; a real Jira key `#3` at index 3 would be treated as positional (very unlikely).
- `result.json` uses the default JSON encoder, so `&` and non-ASCII characters are written as `\uXXXX` escapes. Valid JSON, but a byte-wise diff against the input differs.
- `TriageFailure` stores stack frames (no messages, no ticket text); do not expose that column in the Web UI without access control.
- Output schema (envelope mirrored, `All Comments` = drafted comment, Urgency/Impact in Jira vocabulary) is an assumption until the organizers confirm it.
