# data/

Local-only folder. Everything here except this README is gitignored.

Expected files (not committed — obtain from the hackathon organizers):

| File | Purpose |
|---|---|
| `jira_first_20000_requested_fields_synthetic.json` | JSON array of 20 000 historical tickets (no `Issue key`), imported into SQLite by `TrainingDataImporter` |
| `jira_hackathon_blind_eval_challenge_20260923083915-1141.json` | Envelope `{runId, …, records:[20]}` (a plain array also works), keyless records, input for `TicketTriage.Batch` |
| `result.json` | Output of `TicketTriage.Batch`: the input mirrored (envelope metadata and all other fields kept) with the predicted fields filled; no `Issue key` is added |
| `triage.db` | SQLite database created by the AppHost / Web app on first start |

File names are configurable: `TrainingData:Path` (importer), `--input` / `--output` (Batch) and, under Aspire, `Data:TrainingFile` / `Data:ChallengeFile` in the AppHost `appsettings.json`.

**Delete `triage.db*` once** before the first run with the real training file: the importer skips as soon as any ticket exists, so a database created without training data would stay empty.
