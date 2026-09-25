# ADR-0002: Pre-computed suggestions from a background analysis worker

- **Status:** Proposed, amended 2026-09-25 to the DB status model and the pipeline's retry model, and amended again 2026-09-25: Batch goes through the worker (challenge tickets must be reviewable in the Web UI). Not implemented yet (no ingestor, worker or review service in code). The team confirms or amends it at the hackathon.
- **Date:** 2026-09-24
- **Requirements:** FR-20, FR-22, FR-27 to FR-29, FR-31, FR-34, NFR-10, NFR-11 ([requirements.md](../requirements.md))

## Context

The pipeline of [ADR-0001](0001-hybrid-triage-pipeline.md) makes several LLM calls per ticket and targets under 15 s (NFR-06). If the analysis ran when the analyst opens a ticket, they would wait for it on every ticket, and prerender would trigger LLM calls (forbidden, NFR-10). The first draft of the workflow also had two overlapping triggers (on open, and a cleanup sweep) and let the pipeline save tickets and review decisions.

## Decision

- **Analysis is pre-computed.** `ITicketIngestor` saves incoming tickets as `New`. An `AnalysisWorker` (`BackgroundService` in `TicketTriage.Web`) claims `New` tickets in small batches (lease column `ClaimedAt`, the status stays `New`), calls `ITriagePipeline.TriageAsync`, and stores the `TriageSuggestion` (status `Reviewing`).
- **One trigger path.** Start, timer, manual trigger and queue signal all use the same worker code. Opening a ticket without a suggestion only enqueues it with priority and shows "analysing".
- **The pipeline analyses only.** Ingest is `ITicketIngestor`. The analyst's decision, per-field edits, reject reason and timestamps are persisted by `IReviewService`, with a row version for concurrent edits.
- **Failures are explicit, one retry model.** The pipeline retries a ticket up to `Triage:RetryCount`, counts in `Ticket.Retries`, logs each failed attempt in `TriageFailure` and returns a deterministic fallback when exhausted (FR-34). The worker adds no counter and no `Failed` status; a ticket with `Retries >= RetryCount` is shown as failed. Partial results are never stored.
- **Claims are leased and atomic.** The claim is one conditional `UPDATE … WHERE Status='New' AND ClaimedAt IS NULL` that sets `ClaimedAt`. Stale claims are released. Each ticket has its own timeout (pipeline) and failure isolation. The worker starts only when data preparation is complete. Tickets in `Reviewing` or later are never re-analysed. Details: [architecture.md §5.2.1](../architecture.md).
- **Batch goes through the worker (decided 2026-09-25).** The 20 challenge tickets take the same path as any ticket: `TicketTriage.Batch` ingests them via `ITicketIngestor` (status `New`), the worker analyses them, and Batch exports `result.json` from the **stored** suggestions (challenge order, one entry per ticket, same `TriageResult` shape). Batch no longer calls `ITriagePipeline` itself. Reason: one code path, and a human can review and finalize the challenge tickets in the Web UI.
- **Transitional (true today).** The implemented `BatchRunner` still calls `ITriagePipeline` directly, writes only `result.json` and persists nothing on success. It stays until ingestor, worker and review persistence exist. It is not the target design.

Ticket states are the DB `Status` lookup: `New → Reviewing → (Reviewed) → HumanApproved | HumanRejected` ([architecture.md §5.1](../architecture.md)).

## Consequences

**Good**
- The analyst sees results instantly and never waits for the LLM. The 15 s limit applies to the worker, not to the UI.
- One code path for all triggers, tested once. The claim (`ClaimedAt`) prevents double analysis.
- Pipeline, ingest and review can be tested on their own.

**Trade-offs**
- SQLite has a single writer. The worker keeps transactions short (claim and save, never held across LLM calls) and uses small batches.
- A ticket opened right after ingest (status `New`) may show "analysing". The UI must refresh when the suggestion arrives.
- A crashed worker leaves claimed tickets behind. The sweep on startup and on every tick releases claims older than the lease (`ClaimedAt`).
- Two more Core ports (`ITicketIngestor`, `IReviewService`) to write, plus a `ClaimedAt` column and a filter that keeps imported training history out of the worker's input (unresolved training tickets are also `New`).
- Challenge tickets must be told apart from imported training history, e.g. a source/batch marker on `Ticket` (exact shape open). The worker claims only tickets from the ingest path, which also closes the "worker would re-triage history" risk. Schema change, no migrations: delete `data/triage.db*`.
- `result.json` is exported from the DB, not from the pipeline stream. Open: who triggers and waits for the worker, what a still-`New` or failed ticket exports, AI suggestion vs the analyst's final values ([architecture.md §5.4](../architecture.md), requirements §7).
- SQLite single writer: Batch must not analyse concurrently with the Web worker. The worker runs in Web only; Batch never hosts it.

## Alternatives considered

- **Analyse on open only:** simplest, but the analyst waits on every ticket and it invites LLM calls during prerender. Rejected.
- **Both triggers as separate paths (first draft):** double analysis risk and two code paths. Rejected.
- **Separate Worker project:** cleanest isolation, but more Aspire wiring and a second SQLite writer process. Not needed for the hackathon.
- **Batch calls the pipeline directly (first design):** simple and needs no worker, but challenge tickets never reach the DB or the Web UI and there are two entry paths. Superseded 2026-09-25.
- **Worker inside Batch:** makes Batch long-running and adds the same second writer. Rejected.
- **Review decision through `ITriagePipeline`:** fewer types, but it mixes analysis with review. Rejected.
