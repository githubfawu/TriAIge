# Feature: backend-completion (DB, backend, agent; UI excluded)

Finishes the backend so challenge tickets go through the same path as any ticket: ingest, worker analysis, stored suggestion, review persistence, `result.json` exported from the DB. Decided in [ADR-0002](../../adr/0002-background-analysis-worker.md); this file fixes the scope and the answers to the open questions. The Blazor UI is out of scope here.

## Decisions (2026-09-25)

| Topic | Decision |
|---|---|
| Priority | Score first (result.json quality), then the worker path. |
| Embeddings, similarity filter | **FR-03 and FR-05 dropped.** TF-IDF retrieval stays. Reason: retrieval is only LLM context; service/team/priority come from LLM + code; status and assignee are at chance; the training data has 173 distinct descriptions, which TF-IDF already matches. |
| Resolution cleaning | **FR-02 deferred** (only 21 distinct real resolution texts). |
| Batch export | Batch ingests the challenge tickets, polls until all left `New`, exports `result.json` from stored suggestions in challenge order. Still-`New` or failed tickets export the deterministic fallback. Export = AI suggestion, not the analyst's edit. Web with the worker must be running. |
| Review persistence | Full: decision, reject reason, per-field edits, timestamps (ingested, first opened, decided), row version. |

## In scope

| # | Item | Requirements |
|---|---|---|
| A | Agent quality: drafter prompt stops printing similar tickets' status; real `TriageAgent` instructions (TODO in `TriageAgent.cs`); remove leftover stubs and their TODOs if unused | score-completeness follow-ups |
| B | Origin marker on `Ticket` (challenge vs. training history) plus `ClaimedAt` lease column; schema change | ADR-0002, FR-28 |
| C | Persist `TriageSuggestion` (table + mapping) | FR-28 |
| D | `ITicketIngestor` (Core port, Infrastructure impl): upsert, status `New`, marker | FR-27 |
| E | `AnalysisWorker` in Web: data-ready gate, atomic claim, lease sweep, save with status `Reviewing`, failure isolation, config (timer 30 s, batch 5) | FR-28, NFR-10/11 |
| F | `IReviewService` + row version: approve / edit / reject | FR-22 |
| G | Batch: ingest, poll, export from DB; drop the direct pipeline call; exit codes and atomic write kept | FR-30, FR-31 |
| H | Manual re-queue of a failed ticket (reset `Retries`, status `New`) as a service method | architecture §5.2.2 |
| I | Metrics query service (acceptance rate, edits per field, time to decision) over the F data | FR-25 (data side only) |

## Out of scope

UI (Review page, list, dashboard), embeddings, FR-02, re-analysis after a decision, auth, multi-instance workers.

## Acceptance criteria

1. `dotnet build` and `dotnet test --filter "Category!=Integration"` are green; `dotnet format --verify-no-changes` on changed files.
2. Ingesting the challenge file twice creates no duplicates and marks every ticket as challenge.
3. The worker claims only ingested tickets (never training history), never claims a ticket twice, releases stale leases, and stores a suggestion with status `Reviewing`; a failing ticket does not stop the batch.
4. Opening or reading never calls the LLM.
5. A review decision persists status, per-field edits, reason and timestamps; a stale row version is rejected.
6. Batch with Web running produces `result.json` with one entry per challenge ticket in challenge order, identical in shape to today's output; fallback tickets are counted in the console summary.
7. Drafter prompt no longer contains similar tickets' resolution status.
8. Docs updated: architecture status tables, requirements status, ADR-0002 status, FR-03/05/02 marked dropped/deferred, CLAUDE.md (gotcha: delete `data/triage.db*` after the schema change).

## Risks

- SQLite single writer: Batch only ingests and reads; analysis runs in Web.
- Schema change: everyone deletes `data/triage.db*` and re-imports.
- The worker must start only after import finishes (data-ready gate), or retrieval runs on partial data.
