# Requirements: triage-agent

**Date**: 2026-09-25

## Problem

The LLM-dependent steps of the triage pipeline (ADR-0001 steps 2, 4 and 5) are still stubs (`StubTicketClassifier`, `StubResolutionDrafter`). Without them the challenge tickets get no work type, service, urgency, impact, resolution status or comment. This feature builds those LLM-backed parts as Microsoft Agent Framework agents in `TicketTriage.Agents`.

## Users & Context

- Callers: `ITriagePipeline` (Web analysis worker and Batch). The agent never runs on page open (NFR-10).
- Provider: `Llm:Provider` = **Apertus** or **OpenAI**. Azure OpenAI and Ollama are not tested in this feature.

## Functional Requirements

- FR1: An LLM-backed `ITicketClassifier` (in `TicketTriage.Agents`) returns `TicketClassification` (work type, affected services, urgency, impact) in **one** structured-output call (`RunAsync<T>`), using similar tickets as context and the ticket's own values only as a hint (FR-12, FR-14).
- FR2: The classifier validates the model output: work type and impact/urgency are parsed to the Core enums, services are checked against an injected service catalog (see FR5). Priority is never produced by the model.
- FR3: An LLM-backed `IResolutionDrafter` returns the draft comment (`string`) as required by the current Core port.
- FR4: The drafter also exposes an **Agents-only** result type (resolution status `Done` / `Cancelled` / `Clarification` / `Cannot Reproduce` + comment) via an Agents-side interface. The Core `IResolutionDrafter` implementation delegates to it and returns only the comment. Wiring the status into the pipeline is out of this feature's scope.
- FR5: The service catalog is an **injected abstraction defined in Agents** (default implementation reads Core's `ServiceCatalog`), so the source of the 20 names / 14 critical ones can change without touching agent code. The critical-service list is given to the model for the urgency/impact estimate.
- FR6: The comment language is detected by the agent from the ticket text. The comment is written in that language, in the style of the assignee. The assignee is **inferred from the similar tickets'** `Assignee` field (not from routing).
- FR7: Ticket text is untrusted: it goes in the user message only, never in instructions. Model output is treated as data.
- FR8: Prompts are versioned constants in `TicketTriage.Agents`; the prompt version is logged (FR-32). Temperature 0.
- FR9: Registered in `AddTriageAgents` (explicit registration replacing the stubs' `TryAdd*` defaults); the keyed `TriageAgent` remains resolvable.

## Non-Functional Requirements

- NFR1: Triage LLM time per ticket stays **under 15 s** (NFR-06), i.e. one classify call and one draft call per ticket.
- NFR2: No ticket body or prompt is logged at Information level (CLAUDE.md conventions).
- NFR3: All methods are async with `CancellationToken`; no `.Result` / `.Wait()`.
- NFR4: Warnings are errors; the code passes `dotnet build` and `dotnet format --verify-no-changes`.
- NFR5: Confidence values and reasoning are **not produced and not needed** (dropped by decision; FR-18 removed, the reasoning part of FR-17 dropped).

## Technical Constraints

- Touched project: `TicketTriage.Agents` only, plus a new test project or files for its tests. **No changes** to Core, Infrastructure, Web, Batch, AppHost or the database.
- Model access through `IChatClient` (Microsoft.Extensions.AI) and Microsoft.Agents.AI 1.x. Provider: Apertus (`swiss-ai/Apertus-v1.5-70B`, OpenAI-compatible) and OpenAI (default `gpt-4o-mini`).
- Uses existing Core types unchanged: `Ticket`, `SimilarTicket`, `TicketClassification`, `PriorityMatrix`, the enums.
- Training-data priority/urgency/impact are random and are never used as examples or labels (ADR-0001).

## Acceptance Criteria

- [ ] AC1: With a fake `IChatClient` returning valid JSON, the classifier returns the expected `TicketClassification`.
- [ ] AC2: With invalid work type, invalid urgency/impact or an unknown service, the classifier throws (no silent correction).
- [ ] AC3: With a failing or cancelled `IChatClient`, the classifier and drafter throw and never return a partial result.
- [ ] AC4: A ticket whose text contains an injection ("ignore previous instructions ...") never appears in the system instructions; a test asserts message placement.
- [ ] AC5: The drafter returns a non-empty comment and an Agents-side result with a valid resolution status; the Core port returns only the comment.
- [ ] AC6: The drafter's assignee voice is taken from the similar tickets' assignees; with no similar tickets it writes without a persona and does not invent a name.
- [ ] AC7: The service catalog abstraction is injected; tests use a test-local list.
- [ ] AC8: A live smoke run against Apertus and OpenAI (opt-in, `Category=Integration`, keys required) completes one ticket in under 15 s.
- [ ] AC9: `dotnet build`, `dotnet test --solution TicketTriage.slnx --filter "Category!=Integration"` and `dotnet format --verify-no-changes` pass.

## Edge Cases & Failure Modes

- LLM unreachable, timeout, cancellation → the agent throws; the pipeline's retry and fallback logic applies (arch §5.2.2).
- Invalid enum / unknown service in model output → throws (no repair retry, no fallback inside the agent).
- Empty or garbage ticket text → the agent still calls the model; the pipeline's validation and fallback apply (no confidence flag). (Assumption, see Open Questions.)
- Prompt injection in ticket text → text only in the user message, output validated, never rendered raw.
- No similar tickets → no assignee voice, no persona, generic professional tone.
- Multiple services → the classifier returns a list; routing uses the first (arch §4.1, handled outside the agent).

## Out of Scope

- Retrieval, routing, prioritization code, pipeline orchestration, worker, ingest, review service.
- Any change to Core contracts, Infrastructure, Web, Batch, database or AppHost.
- Confidence, reasoning and `LowConfidence` flag (not needed, removed from the requirements). The resolution status in the pipeline result is a later cycle.
- Azure OpenAI and Ollama testing.
- Held-out accuracy measurement (data files absent, not requested).
- Streaming into the UI, autonomous tool-using agents.

## Incongruencies reported to the user (not resolved by the agent)

1. `IResolutionDrafter` returns `string` only; FR-16 needs status + comment. Handled by an Agents-side type (decision taken).
2. `TriageSuggestion` has no reasoning / LowConfidence fields (FR-17, FR-18, arch §4.1). **Resolved:** confidence and reasoning are dropped everywhere (docs and code).
3. `LlmOptions` has `OpenAI` and `Apertus`; docs and CLAUDE.md say Azure OpenAI / Ollama.
4. `ServiceCatalog` has `TODO` placeholder names; the DB lookup and requirements §6 have the real ones.
5. Docs call `AnalyzeAsync`; the port is `TriageAsync`.
6. Resolution vocabulary: requirements `cannot reproduce` vs enum `Cannot Reproduce`.
7. `TriageResult` has no Resolution / Urgency / Impact; open question 2 (output schema) is unresolved.
8. `data/` has no `training.json` / `challenge.json`.
9. DB ticket status (`New, Reviewing, Reviewed, HumanRejected, HumanApproved`) vs docs (`New, Analysing, Suggested, Approved, Rejected, Failed`). **Resolved later:** the docs now follow the DB statuses (architecture §5.1). Incongruencies 3, 5 and 14 are also resolved, see [README.md](README.md).
10. The DB has no Resolution lookup.
11. DB Impact lookup (`Lowest…Highest`, 0 = Lowest) vs Core `Impact` (`Major…NoImpact`, 0 = Major).
12. DB `ServiceTeams` contains a team named "Affected Business or IT Services".
13. Core `ServiceCatalog` placeholders vs DB service names.
14. `TrainingDataImporter` looks up status `"Finished"`, which is not in the DB status lookup.
15. Importer truncates Summary/Description/Assignee/Resolution and keeps only the first service/team; docs say no truncation and lists.

## Open Questions

- Assumption: two LLM calls per ticket (classify+assess merged, draft). Splitting classify and assess is not planned.
- Assumption: the assignee voice uses the most frequent assignee among the top similar tickets with the same service.
- Assumption: no automatic repair retry on invalid output; the pipeline's retry handles it.
- Assumption: garbage/empty tickets are not rejected by the agent.
- Assumption: how the Agents-side status result is consumed is decided later by the owner of the pipeline.
