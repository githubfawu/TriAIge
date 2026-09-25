# ADR-0001: Five-step hybrid triage pipeline (LLM suggests, code decides)

- **Status:** Proposed (implemented in the pipeline: steps 1–5 exist, routing statistics and embeddings are still planned). The team confirms or amends it at the hackathon.
- **Date:** 2026-09-24
- **Requirements:** FR-10 to FR-16, FR-31, FR-33 ([requirements.md](../requirements.md))

## Context

Each challenge ticket needs seven fields. The training data is deliberately noisy: some affected services are wrong, resolutions are often templates, and priority, urgency and impact are **random**. Scoring rewards a priority that is mathematically consistent with the matrix, and routing to real people. A single "LLM, fill in all fields" prompt would hallucinate names, learn the random fields, and produce priorities that don't match the matrix.

## Decision

Triage runs as a fixed sequence of **five steps**. Each field has exactly one owner: code or LLM. The LLM is used only where the call needs judgement on free text. Anything that can be computed or looked up is done in code.

| # | Step | LLM does | Code does |
|---|---|---|---|
| 1 | **Normalize & retrieve** | — | Normalize fields, flag empty or suspicious values, find top-k similar tickets by cosine similarity (today TF-IDF over the description, embeddings planned) |
| 2 | **Classify** | Work type + affected service (structured output), using similar tickets as context and the given values only as a hint | Validate against `WorkType` / `ServiceCatalog`, fall back to the majority of similar tickets |
| 3 | **Route** | — | Team and assignee from routing statistics (majority vote). Names are never generated |
| 4 | **Assess & prioritize** | Urgency + impact, aware of the critical-service list | `Priority = PriorityMatrix.Resolve(urgency, impact)` |
| 5 | **Draft & validate** | Resolution status + comment in the assignee's voice, based on cleaned templates | Check all 7 output fields (vocabulary, matrix consistency, required fields) before output (FR-33; resolution status not implemented yet) |

The suggestion carries the reference tickets (FR-17); there is no per-decision reasoning and no confidence value. Web and Batch call the same `ITriagePipeline` (FR-31). A human reviews every suggestion in the Web UI.

## Consequences

**Good**
- Priority is correct by construction and routing never hallucinates people. This is where the most points are.
- Each step can be tested and measured on its own, e.g. accuracy per field on a held-out training slice.
- Any step can be replaced by a deterministic fallback when the LLM is down or unconfigured.
- LLM calls are few, small and structured, which keeps a ticket under 15 s (NFR-06) and costs low.

**Trade-offs**
- Several LLM calls per ticket instead of one. Steps 2 and 4 may be merged into one structured call if latency requires it. That doesn't change ownership.
- Routing quality depends on how good the statistics are. Rare services need a fallback, such as the team of the nearest neighbour.
- More code to write up front than a single prompt.

## Alternatives considered

- **Single end-to-end prompt:** fastest to build, but it hallucinates assignees, produces inconsistent priorities and is hard to debug. Rejected.
- **Autonomous agent with tools** (the agent decides which lookups to run): flexible but non-deterministic and hard to reproduce (FR-32). Could be kept for the optional free-text ticket intake (FR-26).
- **Pure ML/kNN without an LLM:** deterministic and cheap, but weak on misleading titles and can't draft comments. It stays as the fallback path.
