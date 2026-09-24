# ADR-0002: Pre-computed suggestions from a background analysis worker

- **Status:** Proposed, amended 2026-09-25 to the DB status model and the pipeline's retry model. Not implemented yet (no ingestor, worker or review service in code). The team confirms or amends it at the hackathon.
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
- **Batch stays direct.** `TicketTriage.Batch` calls the same pipeline without the worker and writes `result.json`.

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

## Alternatives considered

- **Analyse on open only:** simplest, but the analyst waits on every ticket and it invites LLM calls during prerender. Rejected.
- **Both triggers as separate paths (first draft):** double analysis risk and two code paths. Rejected.
- **Separate Worker project:** cleanest isolation, but more Aspire wiring and a second SQLite writer process. Not needed for the hackathon.
- **Worker inside Batch:** makes Batch long-running and adds the same second writer. Rejected.
- **Review decision through `ITriagePipeline`:** fewer types, but it mixes analysis with review. Rejected.
