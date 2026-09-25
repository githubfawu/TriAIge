# Feature: score-completeness

Makes the batch run produce a scoreable `result.json` on the **real** organizer files: all seven predicted fields per record, in the data's vocabulary, mirroring the input records. Sources: [requirements.md](requirements.md), [plan.md](plan.md).

## What / how

| Concern | Behaviour |
|---|---|
| Input | The real challenge file (envelope with `records`, or a plain array). Records carry no `Issue key`; Batch assigns positional identities `#1..#n` internally (never written to the output). |
| Output | The input container is mirrored: each record is cloned, predicted fields are overwritten, all other fields stay. Predicted fields are defined once in Core `TriageResult` (Jira vocabulary via `JiraVocabulary`). |
| Service | Chosen by the LLM, validated against the 20 real names in `ServiceCatalog`. |
| Team | `StatisticsRoutingResolver`: majority per service, computed once per process from SQLite; ties resolve alphabetically (case-insensitive). The first classified service wins. |
| Assignee | Person with the fewest tickets (`AssigneeWorkloadProvider`), see [Assignee decision](#assignee-decision-2026-09-25). |
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

**Conclusion:** no learnable signal for status or assignee in the training file. Status must come from LLM semantics of the ticket text (the drafter must not copy similar tickets' statuses as evidence); the assignee will score about chance whatever the pipeline does (see the assignee decision below). Team is fully determined by service, so team accuracy equals service accuracy.

## Assignee decision (2026-09-25)

`Notebooks/ticket_data_analysis.ipynb` (section "Assignee analysis") checks the assignee against every field and against the ticket order, and fits models with 5-fold cross-validation (30 assignees, chance 3.3% top-1 / 10% top-3):

- Workload is even (about 1/30 per person), and every team has all 30 assignees.
- No field (service, team, entity, work type, reporter, priority / urgency / impact, status, resolution, creation hour / weekday / month, resolution time, comments, text) is associated with the assignee after Bonferroni correction (bias-corrected Cramer's V at most 0.023). No round-robin, no "same person again", assignee = reporter at chance level (0.62%).
- Every model scores at chance: most common overall 3.6% top-1, per-service majority (the old rule) 3.6%, logistic regression on routing fields 3.6%, on text 3.6%, on everything 3.5% (shuffled-label baseline 3.4%, permutation p = 0.27). A positive control (team from service) reaches 100%, so the setup can find a pattern.

**Decision:** the majority vote per service is dropped. The suggested assignee is the person with the **fewest tickets assigned**. It spreads the work evenly and costs no accuracy (every rule is at chance).

- **Counts:** training tickets per assignee plus stored suggestions (`TriageSuggestion.Assignee`), loaded once per process (`AssigneeWorkloadProvider`). Only assignees of training tickets are candidates. Ties resolve alphabetically (case-insensitive).
- **Running counts:** every final suggestion (fallback included) is reserved in memory right away, so consecutive tickets go to different people. The resolver only peeks, so a retried ticket counts once.
- **Restart:** in-memory reservations are lost; stored suggestions are counted again on the next load, so the Web worker continues where it stopped. A Batch run starts from the training counts every time and is therefore reproducible.
- **Not checked:** absence, shifts, skills or the real current workload of a person; the export has no data for that. If the Domain Owner names a real rule, replace `IAssigneeWorkload`.
- **UI:** the review page shows the hint "Suggestion: the person with the fewest tickets assigned so far. Not derived from the ticket content." under the assignee field (`#review-assignee-hint`).
- **Validator:** the assignee is no longer compared with the statistics (`MissingAssignee` / `InconsistentAssignee` were removed).

## Data facts (plan F4-F7)

- **F4 vocabulary**: training `Resolution` is `done / cancelled / clarification / cannot reproduce` (lowercase) or null (3 031); urgency and impact use `highest ... lowest` (challenge capitalised); Core `Urgency` / `Impact` names differ, mapped for output by `JiraVocabulary` (assumption: Critical = Highest, Major = Highest, ... No Impact = Lowest).
- **F5**: `Resolution` is the status, not text; real resolution notes sit in `All Comments` as `"<email>: Resolution: ..."` (5 814 comments, 21 distinct texts) beside templates.
- **F6 routing**: service -> team is 1:1 in all 20 services; the assignee is near-random (every service has all 30 assignees, top one about 5% in-sample). Business Entity / Work type do not help.
- **F7 status**: classes near-uniform (4 168-4 322 each), independent of work type; only 173 distinct descriptions (groups of 15-4 212), so the top-10 neighbours are often copies of one template.

## Limitations

- The output schema (container, casing, `All Comments` semantics) is an assumption pending organizer confirmation (plan C3, C6, C7).
- Fields 4 (assignee) and 6 (status) cannot exceed chance by learning from training data. The assignee rule (fewest tickets) is a fairness rule, not a prediction.

### Known limitations from review (not fixed)

- The drafter prompt still prints similar tickets' `Resolution status:` although the instructions tell the model to ignore it (FR9: no signal). Removing it from the drafter prompt (keeping it in the classifier) is a small follow-up.
- `FallbackSuggestionFactory` derives the fallback status through a JSON round-trip and relies on `Done` being enum value 0 as default. Works, but could be simplified.
- The positional key `#n` is recognised by string form; a real Jira key `#3` at index 3 would be treated as positional (very unlikely).
- `result.json` uses the default JSON encoder, so `&` and non-ASCII characters are written as `\uXXXX` escapes. Valid JSON, but a byte-wise diff against the input differs.
- `TriageFailure` stores stack frames (no messages, no ticket text); do not expose that column in the Web UI without access control.
- Output schema (envelope mirrored, `All Comments` = drafted comment, Urgency/Impact in Jira vocabulary) is an assumption until the organizers confirm it.
