# Requirements: score-completeness

**Date**: 2026-09-25

## Problem

Of the seven scored output fields ([requirements.md §2](../../requirements.md)), only three are produced today (work type, affected service, priority via the matrix plus the comment text). Team and assignee come from a stub, the resolution status is not implemented at all, and the validator covers only some fields. In addition, the real challenge file does not match what `BatchRunner` reads: it is an envelope (`{runId, requestedColumns, …, records:[…]}`), and neither data file has an `Issue key`. Without this cycle a real scoring run would fail or score zero on fields 3, 4 and 6.

Decision (2026-09-25): **score first**. The Web/worker/review work (FR-20 to FR-29) comes in a later cycle.

## Users & Context

- Jury / scoring run: `TicketTriage.Batch` reads the challenge file and writes `result.json`. This cycle keeps the transitional direct pipeline call (ADR-0002 target stays planned).
- Data (gitignored, now present in `data/`): `jira_first_20000_requested_fields_synthetic.json` (JSON array of 20 000 tickets), `jira_hackathon_blind_eval_challenge_20260923083915-1141.json` (envelope, 20 records). The file names differ from `training.json` / `challenge.json` in `README.md` and the AppHost. Config or the AppHost must point at the real names (or the files are renamed by the user).

## Functional Requirements

- **FR1 – Batch reads the real challenge file.** Accept the envelope shape (`records` array) and also a plain array. A record without `Issue key` gets a positional identity (its 1-based index in the file) used internally.
- **FR2 – result.json mirrors the input records.** Output is the same records in the same order with the same field names, with the predicted fields filled: `Work type`, `Affected Business or IT Services`, `Service Team(s)`, `Assignee`, `Priority`, `Urgency`, `Impact`, `Resolution`, `All Comments` (the drafted comment). No key is invented in the output. Written atomically as today (exit codes 0/1/2).
- **FR3 – Training import matches the real file.** The importer works on the real 20 000-ticket array (no `Issue key`), idempotently (FR-01). Schema changes → delete `data/triage.db*`.
- **FR4 – Routing statistics (FR-04).** Computed from the imported training tickets by majority vote: service → team, (service, team) → assignee. Persisted or computed once per process and cached. Ticket text is not used.
- **FR5 – Real routing (FR-13).** `IRoutingResolver` implementation using the statistics, replaces `StubRoutingResolver`. The LLM never proposes team or assignee names. Ties and rare services: pick deterministically (highest count, then alphabetical), no flag ([architecture §7](../../architecture.md)). Unknown service → empty team, null assignee.
- **FR6 – Resolution status.** The LLM predicts the status through the agent (structured output `{status, comment}`), one of `done`, `cancelled`, `clarification`, `cannot reproduce`. Carried through Core (`TriageSuggestion.ResolutionStatus`, port `IResolutionDrafter` returns status + comment), validated against the 4-value vocabulary, written to `Resolution` in `result.json`. Fallback when the model fails: the most frequent status among the similar tickets, or `done` if there are none.
- **FR7 – Real ServiceCatalog.** Replace the `TODO` placeholder names with the 20 real service names ([requirements §6](../../requirements.md)); services are validated against it (case-insensitive canonicalisation).
- **FR8 – Validator covers all seven fields (FR-33).** Work type, at least one catalog service, team(s) consistent with the routing resolver output, assignee, priority equals `PriorityMatrix.Resolve(urgency, impact)`, resolution status in vocabulary, non-blank comment. Team and assignee may be empty only when the service is unknown to the statistics.
- **FR9 – Signal check for resolution status (measurement, not a feature).** A one-off script or test in the test project (or notes in the feature README) reports whether status is predictable from training text (e.g. accuracy of kNN majority on a held-out slice vs. the 25% baseline). The result is recorded in the docs; if there is no signal, CLAUDE.md says so, like it does for priority.

## Non-Functional Requirements

- NFR1: No Ticket text in logs at Information; no model output rendered as markup (unchanged).
- NFR2: Batch of 20 tickets finishes in a reasonable time (target < 15 s per ticket, NFR-06).
- NFR3: Stats computation on 20k tickets completes in seconds and never runs per ticket.
- NFR4: Warnings-as-errors build stays green; new code has unit tests (fake `IChatClient`, in-memory SQLite, temp files).

## Technical Constraints

- Projects touched: Core (`TriageSuggestion`, `TriageResult` or a new output record, ports, `ServiceCatalog`, validator inputs), Infrastructure (importer, routing statistics + resolver, validator, retrieval reuse), Agents (drafter + prompt for status), Batch (reader, writer).
- Schema: possibly a routing-statistics table or none (in-memory cache, preferred). Delete `data/triage.db*` after schema changes.
- Provider: whatever `Llm` is configured (Azure OpenAI default). Temperature 0.
- Do not use training priority/urgency/impact as labels (ADR-0001).
- `result.json` shape follows the input records (FR2). Not yet confirmed with organizers, see Open Questions.

## Acceptance Criteria

- [ ] AC1: `dotnet run --project src/TicketTriage.Batch -- --input <real challenge file> --output data/result.json` reads the 20 envelope records and writes 20 entries in input order, exit 0, no fabricated key.
- [ ] AC2: Each output record carries all seven scored fields; `Resolution` is one of the four values; `Priority` equals the matrix result for the emitted `Urgency`/`Impact` in every record (test over all 25 combinations exists already, plus a batch-level consistency test).
- [ ] AC3: For a ticket whose service appears in training, `Service Team(s)` and `Assignee` equal the majority values from the statistics; a test with a crafted DB proves it, including the tie rule.
- [ ] AC4: A fake `IChatClient` returning an invalid status triggers the validator/retry/fallback path; the fallback status is the similar-ticket majority.
- [ ] AC5: `ServiceCatalog` contains exactly the 20 names from requirements §6; a model-returned name with different casing is canonicalised, an unknown name is rejected.
- [ ] AC6: The importer imports the real training array (20 000 rows) idempotently; re-running adds nothing.
- [ ] AC7: FR9 report exists in the feature docs with the measured numbers.
- [ ] AC8: `dotnet build` and `dotnet test --solution TicketTriage.slnx --filter "Category!=Integration"` are green.

## Edge Cases & Failure Modes

- Challenge record with empty/misleading service → classifier decides, catalog validates (already the design).
- Record with `Assignee`/`Priority`/`Urgency`/`Impact` prefilled (wrong on purpose) → treated as hints only, output is our prediction.
- Envelope missing `records` or empty → exit 1, nothing written, clear message.
- LLM unreachable → per-ticket fallback (existing), fallback also fills routing and the status fallback; Batch still writes 20 entries.
- Prompt injection in ticket text → user message only, output validated (unchanged).
- Service with no training tickets → empty team, null assignee, analyst decides later.

## Out of Scope

- Ingestor, analysis worker, review persistence, Web UI (FR-20 to FR-29), batch through the worker (ADR-0002 target).
- Embeddings (FR-03) and similarity-threshold filtering (FR-05).
- Resolution-comment cleaning (FR-02) beyond what the drafter needs: tracked as a possible follow-up slice if the FR9 numbers show template noise hurts.
- Re-analysis, tie-break rules, active-team checks ([architecture §7](../../architecture.md)).

## Open Questions

- **Result schema** (requirements §7 no. 2): assumption = mirror input records with predicted fields filled (user-approved 2026-09-25). If the organizers publish a different schema, only the writer changes.
- **Urgency/Impact in output:** assumption = included as predicted values, since partial points are awarded for them.
- **Resolution status signal** (§7): user decided the LLM predicts it; FR9 measures whether it can beat the 25% baseline.
- **Multiple services per ticket** (§7 no. 5): routing uses the first listed service, as documented in architecture §4.1.
- **Business Entity / Work type in routing** (§7 no. 4): assumption = routing depends on service only; FR9-style check may revisit.
