---
name: grill-me
description: Interviews the user relentlessly about a feature, plan or idea until requirements are unambiguous, then writes them to docs/requirements.md. Use when the user says "grill me", asks to clarify or stress-test an idea before building it, or when a request is vague enough that building now would mean guessing. Runs inline in the main conversation (not in a subagent) because it needs AskUserQuestion.
argument-hint: "[feature or idea]"
---

# Grill Me

Interview the user about `$ARGUMENTS` (or the feature under discussion) until every branch of the decision tree is resolved. Don't accept vague answers — if a reply is still ambiguous, ask a sharper follow-up instead of moving on.

## Why inline, not a subagent

Subagents can't call `AskUserQuestion` and can't pause mid-task for a reply. An interview only works turn-by-turn in the main conversation, so you (the main agent) follow these instructions directly.

## Process

1. Before asking, read `CLAUDE.md`, `data/README.md` and relevant code so you don't ask what the repo already answers.
2. Ask with `AskUserQuestion`, **max 2 questions per round**, until all 7 areas are covered. Offer concrete options (with a recommended one) rather than open questions where possible.
3. Push back on hand-wavy answers: "it should be accurate" → which metric, what target on which data? "handle errors gracefully" → which errors, and what does the user/jury see?
4. When all areas are resolved, write `docs/requirements.md` (create `docs/` if missing).
5. Show the file and ask for explicit approval before anything downstream starts. Silence or a topic change is not approval.

## The 7 areas

1. **Problem & motivation** — what's solved, why it matters for the challenge/demo, what happens if we skip it.
2. **Users & context** — jury, support agent, batch run? UI (Web) or batch (`challenge.json` → `result.json`) or both?
3. **Inputs & outputs** — exact input fields, exact output shape (for `result.json`: the organizers' contract).
4. **Technical constraints** — which projects (Core / Infrastructure / Agents / Web / Batch), new entities or migrations, which model/provider, latency/cost budget, time budget in the hackathon.
5. **Success criteria** — measurable: accuracy on a held-out slice of `training.json`, demo flow works end-to-end, runtime < N min for the batch.
6. **Edge cases & failure modes** — empty/garbage tickets, unknown categories from the model, rate limits/timeouts, prompt injection in ticket text, what must never happen.
7. **Out of scope** — what we explicitly won't do (as important as the rest).

## docs/requirements.md

```markdown
# Requirements: <feature>

**Date**: YYYY-MM-DD

## Problem
## Users & Context
## Functional Requirements
- FR1: ...
## Non-Functional Requirements
- NFR1: ... (latency, cost, accuracy, determinism)
## Technical Constraints
(projects touched, entities/migrations, model/provider, dependencies)
## Acceptance Criteria
- [ ] AC1: ...
## Edge Cases & Failure Modes
- <case> → <expected behaviour>
## Out of Scope
## Open Questions
- (deferred decisions and the assumption taken)
```

## Rules

- Don't write the file until the interview is done.
- If the user defers ("you decide"), record your assumption under **Open Questions** rather than silently picking.
- Hackathon reality: if the user says time is short, compress to the areas that change the implementation (3, 4, 5, 6) and note the rest as assumptions.
