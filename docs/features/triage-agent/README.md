# Triage agent (LLM classifier and drafter)

**Date**: 2026-09-25 · **Projects**: Agents (+ `TicketTriage.Agents.Tests`) · Requirements: [requirements.md](requirements.md)

## Summary

Replaces the stubs `StubTicketClassifier` and `StubResolutionDrafter` with LLM-backed implementations in `TicketTriage.Agents`. Core, Infrastructure, Web, Batch, AppHost and the database are unchanged. The pipeline ([triage-pipeline](../triage-pipeline/README.md)) calls them through the existing Core ports.

## How it works

| Type | Role |
|---|---|
| `LlmTicketClassifier` (`ITicketClassifier`) | One structured-output call (`RunAsync<T>`) returns work type, affected services, urgency and impact (`ClassificationDto` → `TicketClassification`). Similar tickets are context, the ticket's own values only a hint. Temperature 0. |
| `LlmResolutionDrafter` (`IResolutionDrafter` and `IResolutionDraftAgent`) | One call returns resolution status, comment and language (`ResolutionDraft`). The Core port returns only the comment; the status is available through the Agents-side `IResolutionDraftAgent` and is **not yet wired into `TriageSuggestion`**. The comment uses the ticket's language and the voice of the assignee taken from the similar tickets (no persona when there are none). |
| `IServiceCatalogProvider` | Injected source of valid services and their criticality. Default `CoreServiceCatalogProvider` reads Core's `ServiceCatalog`. |
| `ClassifierPrompts`, `DraftPrompts` | Versioned prompt constants (`classifier-v1`, `drafter-v1`). |
| `TicketPromptFormatter`, `EnumNames` | Ticket and similar-ticket formatting for the user message, enum name parsing. |

Rules: ticket text goes only into the user message, never into the instructions. The model never produces the priority (`PriorityMatrix`). Invalid work type, urgency, impact, an unknown service, no service, an empty comment or a failing / cancelled `IChatClient` **throw** (`InvalidOperationException` or the client's exception); there is no repair retry and no fallback inside the agent. The pipeline counts the failure and retries or falls back.

Registration: `AddTriageAgents` registers the implementations with `AddScoped` after `AddTriageInfrastructure`, so they replace the stubs' `TryAdd*` defaults. The keyed `TriageAgent` stays resolvable.

## Configuration

No new option. Providers and keys: `Llm` section, see the [README](../../../README.md). Small Ollama models are weak at strict JSON; expect more failed attempts.

## Tests

`tests/TicketTriage.Agents.Tests` (xUnit v3, fake `IChatClient`): classifier mapping, invalid output, failing / cancelled client, injection text never in instructions; drafter comment + status, assignee voice, no persona without similar tickets; DI registration. `LiveSmokeTests` (`Category=Integration`, needs keys) runs one ticket against a real provider and is excluded from the default run.

## Known limitations and status of the requirements' incongruencies

Resolved since the requirements were written: the importer status (`"Finished"` → `HumanApproved`), the doc method name (`AnalyzeAsync` vs `TriageAsync`), the provider list in the docs, the DB status vs docs mismatch (docs now follow the DB, [architecture §5.1](../../architecture.md)).

Still open:

- **Resolution status is not implemented** (later cycle): `TriageSuggestion.ResolutionStatus` exists but stays null, `TriageResult` has no field for it, and the validator does not check it. Confidence and reasoning were dropped as not needed (FR-18 removed, `Confidence` deleted from `TriageSuggestion` and `TicketClassification`).
- `TriageResult` (batch output) has no resolution, urgency or impact field; the output schema is an open question (requirements §7 no. 2).
- Core `ServiceCatalog` still holds `TODO …` placeholders, so the default catalog rejects every real service. The real names are in `docs/requirements.md` §6 and the DB seed.
- DB `Impact` lookup (`Lowest…Highest`) differs from the Core `Impact` enum (`Major…NoImpact`); the importer translates by rank.
- DB `ServiceTeams` contains a team named "Affected Business or IT Services".
- The importer truncates summary, description, assignee and resolution and keeps only the first service and team.
- No accuracy measurement on held-out tickets (data files are not in the repository).

## AI assistance

Parts of this feature were developed with Claude Code (Anthropic). All code was reviewed and understood by the team.
