# Architecture Decision Records

Short records of decisions that are hard to reverse or that a new teammate would otherwise ask about. There's one file per decision: `NNNN-kebab-title.md`. Keep each record under about one page.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-hybrid-triage-pipeline.md) | Five-step hybrid triage pipeline (LLM suggests, code decides) | Proposed |
| [0002](0002-background-analysis-worker.md) | Pre-computed suggestions from a background analysis worker | Proposed |

## Template

```markdown
# ADR-NNNN: <decision as a short statement>

- **Status:** Proposed | Accepted | Superseded by ADR-XXXX
- **Date:** YYYY-MM-DD
- **Requirements:** FR-.., NFR-..

## Context
What forces the decision? Constraints, data, scoring.

## Decision
What we do. One table or a few bullets.

## Consequences
Good, trade-offs, follow-ups.

## Alternatives considered
Each option with the reason it was rejected.
```

Candidates for future ADRs: embedding model and vector storage in SQLite, urgency/impact vocabulary mapping (open question 1), submission output schema (open question 2).
