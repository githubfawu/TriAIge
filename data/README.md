# data/

Local-only folder. Everything here except this README is gitignored.

Expected files (not committed — obtain from the hackathon organizers):

| File | Purpose |
|---|---|
| `training.json` | ~20k historical tickets, imported into SQLite by `TrainingDataImporter` |
| `challenge.json` | 20 challenge tickets, input for `TicketTriage.Batch` |
| `result.json` | Output written by `TicketTriage.Batch` (submitted for scoring) |
| `triage.db` | SQLite database created by the AppHost / Web app on first start |
