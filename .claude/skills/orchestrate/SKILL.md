---
name: orchestrate
description: >
  Full development workflow for a TicketTriage feature in 8 gated phases:
  Clarify (grill-me) → Plan (Opus, vertical slices) → Implement (Sonnet, per
  slice, code + tests) → Build (Haiku, loop to green) → Review (quality +
  security in parallel, user picks fixes) → Document → Update project memory
  (CLAUDE.md / skills) → Size/SRP audit. Human approval gates after Clarify and
  Plan. Invoke with /orchestrate [feature]; add "fast" for the hackathon-compressed variant.
argument-hint: "[feature description] [fast]"
disable-model-invocation: true
---

# Orchestrator: full feature workflow

Feature: $ARGUMENTS

Run the phases strictly in order. Phases delegated to a subagent run in an isolated context — this protects the main context and pins the model regardless of the session model. Non-delegated phases (1, 7, 8, all gates) run in the session model.

| Phase | Name | Model | How |
|---|---|---|---|
| 1 | Clarify | session | `grill-me` skill inline (needs AskUserQuestion) |
| — | **Gate** | — | AskUserQuestion: approve `docs/features/<feature>/requirements.md`? |
| 2 | Plan | **Opus** | Agent `Plan`, `model: opus` |
| — | **Gate** | — | AskUserQuestion: approve `docs/features/<feature>/plan.md`, which slice first? |
| 3+4 | Implement + Build (per slice) | Sonnet / Haiku | Agent `implementer` → Agent `build-fixer` |
| 5 | Review | Sonnet | Agents `code-reviewer` + `security-reviewer` **in parallel** |
| 6 | Documentation | Sonnet | Agent `docs-writer` |
| 7 | Project memory | session | inline — update `CLAUDE.md` / `.claude/skills` |
| 8 | Size/SRP audit | session | inline |

Before starting: make sure the working tree is clean (`git status`) and create a feature branch (`git switch -c feat/<short-name>`) — on a shared hackathon repo, never work directly on `main`.

**Fast mode** (`fast` in the arguments): Phase 1 = one round of max 2 questions on inputs/outputs + acceptance criteria; Phase 2 = max 3 slices; Phase 5 = one round, MUST/🔴 only; Phase 6 = README section only; Phases 7–8 unchanged but brief. Gates still apply.

---

## Phase 1 — Clarify

Follow the `grill-me` skill for `$ARGUMENTS`. Output: `docs/features/<feature>/requirements.md`.

**STOP** — show it and ask via `AskUserQuestion`: approve and continue, or change? No explicit approval → no Phase 2.

## Phase 2 — Plan (vertical slices)

Dispatch Agent `subagent_type: "Plan"`, `model: "opus"` with `docs/features/<feature>/requirements.md`, the project-level `docs/requirements.md` + `docs/architecture.md`, `CLAUDE.md`, and the list of existing projects.

A good slice cuts through all touched layers (Core → Infrastructure → Agents → Web/Batch), is testable end-to-end without the next slice, and fits one session (≈ ≤ 3 new files + tests). First slice is usually the thinnest end-to-end path (e.g. one ticket → agent → decision shown/written).

Output `docs/features/<feature>/plan.md`: architecture overview, per slice (name, goal, files with project, tests, acceptance criteria covered, complexity S/M/L), schema changes (no migrations: `EnsureCreated`, note "delete data/triage.db*"), build commands, slice dependencies.

**STOP** — show the slice table, ask via `AskUserQuestion`: approve, and which slice first?

## Phase 3+4 — Implement → Build (loop per slice)

For each slice in the approved order:

1. **Implement**: Agent `implementer` with the slice definition from `plan.md`, the relevant `requirements.md` section, and the touched files.
2. **Build**: Agent `build-fixer` with the changed files (loops to green, max 3 attempts).
3. Still red after 3 attempts → **STOP**, show the error, ask the user. Don't keep guessing.
4. Commit: `git add <changed files> && git commit -m "feat(<slice>): <what the slice delivers>"`.
5. Note "Slice X done, next: Y" and continue.

Only after all slices: Phase 5.

## Phase 5 — Review (max 3 rounds)

1. Dispatch **in parallel** (one message, two Agent calls): `code-reviewer` and `security-reviewer` on all files changed in Phases 3+4 (`git diff main...HEAD --name-only`).
2. Merge both lists, sorted by severity ([MUST]/🔴 first).
3. `AskUserQuestion` (multiSelect): each finding as an option (severity + one-liner); the user picks what to fix now.
4. Apply the selected fixes (small ones inline, larger ones via `implementer`).
5. Back to **Phase 4** (`build-fixer`) on the changed files, then commit `fix(review): ...`.
6. Remaining findings → carry into Phase 6 as known limitations. Never more than 3 rounds.

## Phase 6 — Documentation

Agent `docs-writer` with `docs/features/<feature>/plan.md`, all implemented files, and unaddressed findings.

## Phase 7 — Project memory (team-shared `.claude/`, not personal memory)

1. Compare what was learned in this feature with `CLAUDE.md` and `.claude/skills/*`.
2. Update only what actually changed: new gotchas, conventions, commands, projects, packages, prompt strategies that worked/failed.
3. Don't add what's derivable from code or git history.

## Phase 8 — Size/SRP audit

For every `.claude/` file touched in Phase 7:
- `SKILL.md` body < 500 lines; above → split into `references/*.md`, linked one level deep; reference files > 100 lines get a table of contents.
- `CLAUDE.md` ≈ 200 lines target, 300 hard cap.
- One topic per file — split by domain if a file mixes independent concerns.

Finish with a short summary: branch, commits, tests added, open findings, and the suggested PR title.

## Rules

- Never start a slice while the previous build is red.
- Never skip tests (Phase 3) or review (Phase 5).
- Gates are turn-based: after STOP the response ends; continue only on a new user message.
- After 2 failed attempts at the same problem: stop guessing, involve the user.
- Never commit secrets, `data/*` files, or `*.db` files.
