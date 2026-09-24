# Architecture

How TicketTriage is built and how a ticket flows through it. Requirements: [requirements.md](requirements.md). Why it's built this way: [adr/](adr/).

> Status legend: **implemented** = in code today, **planned** = required but still a stub (`TODO: implement`). Update this file when a stub is replaced.

## 1. System context

```mermaid
flowchart LR
    analyst([Service desk analyst])
    jury([Challenge scoring])

    subgraph aspire["Aspire AppHost (one command: aspire run)"]
        web["TicketTriage.Web<br/>Blazor Server + MudBlazor"]
        batch["TicketTriage.Batch<br/>console, explicit start"]
        db[("triage-db<br/>SQLite: data/triage.db")]
        dash["Aspire dashboard<br/>logs · traces · health"]
    end

    llm["LLM provider<br/>Azure OpenAI or Ollama"]
    files[/"data/*.json<br/>training · challenge · result"/]

    analyst -- "review: approve / edit / reject" --> web
    web --> db
    batch --> db
    web -- "IChatClient" --> llm
    batch -- "IChatClient" --> llm
    files -- "training import" --> web
    files -- "challenge.json" --> batch
    batch -- "result.json" --> jury
    web -. OTel .-> dash
    batch -. OTel .-> dash
```

Web and Batch share **one** pipeline implementation (FR-31). The AppHost injects the connection string (`triage-db`) and the LLM configuration (`Llm__*`) from its user secrets.

## 2. Projects and dependencies

```mermaid
flowchart BT
    core["Core<br/>domain records, enums, PriorityMatrix,<br/>ServiceCatalog, pipeline ports"]
    infra["Infrastructure<br/>EF Core/SQLite, import, stubs"]
    agents["Agents<br/>IChatClient factory, agents, prompts"]
    web["Web<br/>Blazor UI, HITL"]
    batch["Batch<br/>challenge → result"]
    sd["ServiceDefaults<br/>OTel, health, resilience"]
    host["AppHost"]

    infra --> core
    agents --> infra
    web --> agents
    batch --> agents
    batch --> infra
    web --> sd
    batch --> sd
    host -. orchestrates .-> web
    host -. orchestrates .-> batch
```

Core has no references, and all dependencies point inward. Pipeline steps are Core interfaces (`ISimilarTicketRetriever`, `ITicketClassifier`, `IRoutingResolver`, `IResolutionDrafter`, `ITriagePipeline`), implemented in Infrastructure (deterministic) or Agents (LLM-backed).

## 3. Data preparation (once, on startup — FR-01…05)

```mermaid
flowchart LR
    json[/training.json<br/>20k noisy tickets/] --> imp["Import<br/>idempotent (FR-01)"]
    imp --> clean["Clean resolutions<br/>drop templates, 'Problem fixed' (FR-02)"]
    clean --> emb["Embed summary + description<br/>dedupe, cache (FR-03)"]
    clean --> stats["Routing statistics<br/>service→team, (service,team)→assignee (FR-04)"]
    emb --> filt["Filter mismatching resolutions<br/>similarity threshold (FR-05)"]
    emb & stats & filt --> db[(SQLite)]
```

| Step | Status |
|---|---|
| Import | implemented (`TrainingDataImporter`) |
| Clean, embed, routing stats, filter | planned |

The training set's **Priority, Urgency and Impact are random**. They are never used as labels, as few-shot examples or in statistics.

## 4. Triage pipeline (per ticket)

The five steps are decided in [ADR-0001](adr/0001-hybrid-triage-pipeline.md). The colour shows who decides each value.

```mermaid
flowchart LR
    t[/Ticket/] --> s1
    s1["1 · Normalize & retrieve<br/>flag empty/suspicious fields,<br/>top-k kNN (cosine)"]
    s2["2 · Classify<br/>work type, affected service<br/>structured output"]
    s3["3 · Route<br/>team + assignee<br/>from routing statistics"]
    s4["4 · Assess & prioritize<br/>LLM: urgency + impact<br/>code: PriorityMatrix"]
    s5["5 · Draft & validate<br/>resolution + comment (assignee voice),<br/>vocabulary & consistency checks"]
    out[/TriageSuggestion<br/>+ reasoning + reference tickets/]

    s1 --> s2 --> s3 --> s4 --> s5 --> out

    classDef det fill:#dbeafe,stroke:#2563eb,color:#1e3a8a
    classDef llm fill:#fef3c7,stroke:#d97706,color:#78350f
    classDef mixed fill:#ede9fe,stroke:#7c3aed,color:#3b0764
    class s1,s3 det
    class s2 llm
    class s4,s5 mixed
```

🟦 deterministic code · 🟨 LLM · 🟪 LLM proposes, code validates/decides

| Step | Core port | Decided by | Status |
|---|---|---|---|
| 1 Normalize & retrieve | `ISimilarTicketRetriever` | code (embeddings + cosine) | stub |
| 2 Classify | `ITicketClassifier` | LLM, validated against `ServiceCatalog` / enums | stub |
| 3 Route | `IRoutingResolver` | code (majority vote), LLM never invents names | stub |
| 4 Assess & prioritize | `ITicketClassifier` + `PriorityMatrix` | LLM (urgency, impact) → **code** (priority) | matrix implemented |
| 5 Draft & validate | `IResolutionDrafter` + validator | LLM draft from cleaned templates → code validation (FR-33) | stub |
| Orchestration | `ITriagePipeline` | code | stub |

## 5. Human in the loop

```mermaid
sequenceDiagram
    actor A as Analyst
    participant W as Web (Review page)
    participant P as ITriagePipeline
    participant L as LLM
    participant D as SQLite

    A->>W: open ticket
    W->>P: TriageAsync(ticket)
    P->>D: similar tickets, routing stats
    P->>L: classify / assess / draft (structured output)
    L-->>P: JSON
    P-->>W: TriageSuggestion (priority from matrix)
    W->>D: save suggestion (Pending)
    A->>W: edit urgency / impact
    W-->>A: priority recalculated (FR-23)
    A->>W: approve / edit / reject
    W->>D: persist decision + edits + timestamps
    Note over D: basis for acceptance rate,<br/>edits per field, time-to-resolution (FR-25)
```

## 6. Cross-cutting

| Concern | Approach |
|---|---|
| Configuration | `Llm` section (`AzureOpenAI` \| `Ollama`), AppHost parameters → env vars, secrets in user secrets only |
| Observability | OpenTelemetry via ServiceDefaults. LLM calls (`Experimental.Microsoft.Extensions.AI`) show in the dashboard with token usage |
| Health | `/health`: `sqlite` + `agent-framework` (ready), `/alive` (live) |
| Reproducibility | temperature 0, fixed model deployment, prompt version logged (FR-32) |
| Security | ticket text is untrusted input (prompt injection), model output is validated and never rendered as raw HTML |
