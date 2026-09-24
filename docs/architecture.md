# Architecture

How TicketTriage is built and how a ticket flows through it. Requirements: [requirements.md](requirements.md). Why it's built this way: [adr/](adr/).

> Status legend: **implemented** = in code today, **planned** = required but still a stub (`TODO: implement`). Update this file when a stub is replaced.

## 1. System context

```mermaid
flowchart LR
    analyst([Service desk analyst])
    jury([Challenge scoring])

    subgraph aspire["Aspire AppHost (one command: aspire run)"]
        web["TicketTriage.Web<br/>Blazor Server + MudBlazor<br/>+ analysis worker (BackgroundService)"]
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

Web (UI and analysis worker) and Batch share **one** pipeline implementation (FR-31). The AppHost injects the connection string (`triage-db`) and the LLM configuration (`Llm__*`) from its user secrets.

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

Core has no references, and all dependencies point inward. Pipeline steps are Core interfaces (`ISimilarTicketRetriever`, `ITicketClassifier`, `IRoutingResolver`, `IResolutionDrafter`, `ITriagePipeline`), implemented in Infrastructure (deterministic) or Agents (LLM-backed). `ITriagePipeline` only **analyses** a ticket. Two further Core ports keep the other concerns out of it: `ITicketIngestor` (saves incoming tickets as `New`) and `IReviewService` (persists the analyst's decision, edits and timestamps). The analysis worker is a `BackgroundService` in Web that calls the pipeline, see [ADR-0002](adr/0002-background-analysis-worker.md).

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

**Embedding throttle (proposal).** Embedding 20k tickets is a one-off bulk job and must not hit provider rate limits (429):

- Send the texts in batches (e.g. 64 inputs per request), with at most 2 requests in flight.
- Retry on 429 and 5xx with exponential backoff and jitter, and honour `Retry-After`.
- Save the vectors after every batch and embed only tickets that have no vector yet (keyed by a hash of the text). An interrupted or repeated import resumes where it stopped and costs nothing extra.
- Set the "data ready" marker only when every training ticket has a vector.
- The cost is small (a few million tokens with a small embedding model). Ollama is the free fallback for local runs.

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
| Orchestration | `ITriagePipeline` (analysis only) | code | stub |
| Ingest | `ITicketIngestor` | code, saves tickets as `New` | planned |
| Analysis worker | `BackgroundService` in Web | code, claims `New` tickets and calls the pipeline (ADR-0002) | planned |
| Review persistence | `IReviewService` | code, decision + per-field edits + timestamps | planned |

### 4.1 Pipeline rules

| Topic | Rule |
|---|---|
| **No self-retrieval** | `ISimilarTicketRetriever` takes the id of the ticket under analysis and excludes it from the kNN result. This matters when a challenge ticket also appears in `training.json`, and for tickets that are re-analysed. |
| **Confidence** | If the pipeline cannot complete a ticket with high confidence (empty or garbage text, unclear classification, no usable references), the suggestion is flagged `LowConfidence` with a reason. The UI shows the flag and the reason to the analyst, who decides. The LLM is not skipped silently and nothing is auto-routed. |
| **Language** | The language is detected once per ticket. The drafted resolution and comment use that language, and the validator checks against it. Service, team and enum names stay in the fixed English vocabulary of the catalog. |
| **Multiple services** | Open: it is not yet confirmed that a ticket can have more than one service (requirements §7 no. 5). The field is a list in the export, so the model keeps a list. Urgency, impact and priority are assessed once per ticket. Until the data is checked, routing uses the first service the classifier lists, and all listed services are shown as affected. If several services do occur and map to different teams, a proper rule is needed. The rule "service with the highest ticket priority" was considered and dropped for now, because it needs urgency and impact per service. |
| **Cold start and ties** | With few or no similar tickets, or a tie in the majority vote, the suggestion is flagged `LowConfidence` and the analyst chooses. No further tie-break logic for now. |
| **Text length** | Ticket text is limited by the database column (`varchar(max)`). No truncation or chunking in the pipeline. |
| **Reference tickets** | Similar tickets and their resolutions come from our own cleaned and controlled training data (FR-02, FR-05), so they are trusted. Only the incoming ticket text is treated as untrusted input. |

## 5. Ticket lifecycle and human in the loop

Suggestions are **pre-computed** by a background worker. Opening a ticket only reads the stored result ([ADR-0002](adr/0002-background-analysis-worker.md)).

### 5.1 Ticket states

```mermaid
stateDiagram-v2
    [*] --> New: ingest (ITicketIngestor)
    New --> Analysing: worker claims ticket
    Analysing --> Suggested: suggestion saved
    Analysing --> New: call incomplete or timeout, attempts left
    Analysing --> New: stale claim swept (ClaimedAt too old)
    Analysing --> Failed: attempt limit reached
    Failed --> New: manual re-queue
    Suggested --> New: source ticket edited, no decision yet
    Suggested --> Approved: analyst approves (with or without edits)
    Suggested --> Rejected: analyst rejects (reason)
```

`New`, `Analysing` and `Failed` show in the ticket list as "analysing" / "failed" (FR-20). A `Failed` ticket still shows deterministic fallback values where there are any (FR-34).

`Approved` and `Rejected` are final. There is no `Edited` state: the analyst either approves or rejects, and any changes made before approving are stored as per-field edits on the decision (FR-25), so "approved unchanged" and "approved with edits" are told apart by those edits. Re-opening or un-rejecting a decided ticket, and re-analysis after a decision, are out of scope (see §7).

### 5.2 Ingest and analysis (worker)

```mermaid
sequenceDiagram
    participant S as Source (UI / file)
    participant I as ITicketIngestor
    participant W as AnalysisWorker
    participant P as ITriagePipeline
    participant L as LLM
    participant D as SQLite

    S->>I: new tickets
    I->>D: save tickets (status New)
    loop timer / startup / manual trigger / queue signal
        W->>D: claim next New tickets, batch (status Analysing)
        D-->>W: tickets
        W->>P: AnalyzeAsync(ticket)
        P->>L: embed summary + description
        L-->>P: vector
        P->>D: kNN similar tickets, routing statistics
        D-->>P: similar tickets, team / assignee votes
        P->>L: classify (structured output)
        L-->>P: work type, services
        P->>L: assess urgency + impact (structured output)
        L-->>P: urgency, impact
        Note over P: route from statistics, priority = PriorityMatrix
        P->>L: draft resolution + comment
        L-->>P: draft
        Note over P: validate vocabulary, matrix, required fields (FR-33)
        P-->>W: TriageSuggestion
        W->>D: save suggestion (status Suggested)
    end
    Note over W,D: call incomplete: discard partial result, status New, attempts + 1.<br/>Attempt limit reached: deterministic fallback, status Failed (FR-34)
```

### 5.2.1 Worker rules

These rules close the gaps of the diagram above. All of them apply to the worker. Batch follows the per-ticket rules of §5.4.

| Rule | Definition |
|---|---|
| **Start gate** | The worker starts claiming only after data preparation (import, embeddings, routing statistics, §3) has completed. A persisted "data ready" marker tells it. Until then ingest still saves tickets as `New`, they wait. Without this gate, kNN would run on a half-imported or half-embedded set. |
| **Atomic claim** | A claim is one conditional statement, `UPDATE Tickets SET Status='Analysing', ClaimedAt=@now, Attempts=Attempts+1 WHERE Id IN (…) AND Status='New'`, followed by a read of the rows that were changed. Two overlapping ticks or two instances can never claim the same ticket, because SQLite has a single writer. The transaction covers only the claim and is never held across an LLM call. |
| **Lease and sweep** | `ClaimedAt` is the lease. On startup and on every tick, tickets in `Analysing` with `ClaimedAt` older than the lease (assumption: 5 min, above the per-ticket timeout) are reset to `New`. This recovers tickets after a crash or a restart. |
| **Per-ticket timeout** | Each ticket runs under its own `CancellationTokenSource` (assumption: 60 s in total, on top of the resilience timeouts per LLM call). A slow ticket is cancelled and handled like any incomplete call. |
| **Failure isolation** | Tickets of a batch are processed independently. An exception or timeout affects only its own ticket and never the rest of the batch or the worker loop. |
| **Incomplete call** | If any pipeline step does not complete (LLM error, timeout, cancellation, validation failure), the partial result is **discarded**, not stored. The ticket goes back to `New` so the next load tries it again. There are no partial suggestions. `Attempts` is kept, so a ticket that keeps failing ends as `Failed` at the limit (FR-34) and does not retry forever. |
| **Suggested is closed to the worker** | The worker only claims `New`. A ticket in `Suggested` is never re-analysed and its suggestion is never overwritten, so it cannot change while an analyst edits it. The `rowVersion` check of §5.3 still protects against two analysts. |
| **Source edit** | If a ticket is changed in the source (a re-ingest with the same key) and it has no decision yet, its suggestion is dropped and the status goes back to `New`. A decided ticket (`Approved`, `Rejected`) is not changed. Edits made directly in the database are out of scope. |
| **Ingest = upsert** | `ITicketIngestor` upserts on the Jira key. It never creates a duplicate row. An unchanged re-send is a no-op. |

### 5.3 Open and review

```mermaid
sequenceDiagram
    actor A as Analyst
    participant W as Web (Review page)
    participant Q as AnalysisWorker
    participant R as IReviewService
    participant D as SQLite

    A->>W: open ticket
    W->>D: read ticket + TriageSuggestion
    alt suggestion exists
        D-->>W: ticket, suggestion, reference tickets
        Note over W,D: first-opened timestamp saved once
    else not analysed yet
        W->>Q: enqueue with priority (FR-29)
        W-->>A: show "analysing", refresh when done
    end
    A->>W: edit urgency / impact
    Note over W: PriorityMatrix.Resolve in Core, no round trip (FR-23)
    W-->>A: priority recalculated
    A->>W: approve / edit / reject (+ reason)
    W->>R: SubmitDecision(ticketId, decision, edits, rowVersion)
    R->>D: persist decision, per-field edits, timestamps
    Note over D: basis for acceptance rate, edits per field,<br/>time-to-resolution = ingested → first opened → decided (FR-25)
```

If the ticket changed in the meantime (another analyst decided, or the worker re-analysed it), `rowVersion` no longer matches, the write is rejected and the UI reloads.

### 5.4 Batch (challenge submission)

Batch does not use the worker. It calls `ITriagePipeline` directly for each challenge ticket, runs the FR-33 validation and writes `result.json`. The pipeline code is the same as in 5.2.

- **Every ticket is evaluated.** `result.json` always contains one entry per challenge ticket. The status is kept per ticket, not per run. A ticket that fails does not stop the run.
- **Per-ticket retry, then fallback.** Batch has no worker, so it retries an incomplete ticket itself (same attempt limit and per-ticket timeout as §5.2.1). If it still fails, the deterministic fallback of FR-34 fills the entry and the ticket is reported as failed in the console summary. The file is never partial.
- **No self-retrieval.** kNN excludes the ticket itself (see §4.1).
- **Data ready.** Batch checks the same "data ready" marker as the worker and stops with a clear message if data preparation is not complete.

## 6. Cross-cutting

| Concern | Approach |
|---|---|
| Configuration | `Llm` section (`AzureOpenAI` \| `Ollama`), AppHost parameters → env vars, secrets in user secrets only |
| Observability | OpenTelemetry via ServiceDefaults. LLM calls (`Experimental.Microsoft.Extensions.AI`) show in the dashboard with token usage |
| Health | `/health`: `sqlite` + `agent-framework` (ready), `/alive` (live) |
| Reproducibility | temperature 0, fixed model deployment, prompt version logged (FR-32) |
| Security | ticket text is untrusted input (prompt injection), model output is validated and never rendered as raw HTML |

## 7. Known limitations (out of scope)

Decided as out of scope for the hackathon. Recorded so they are not mistaken for oversights.

| Topic | Current behaviour |
|---|---|
| Reopen, un-reject, re-analyse after a decision | Not supported. Decisions are final. |
| Embedding model change | No model/version key on vectors. A model change means a full re-import. |
| Rare services, ties in routing | Treated as `LowConfidence`, the analyst decides. No tie-break. |
| Inactive team or assignee | Routing statistics come from history and may name someone who has left. Not checked. |
| Duplicate tickets | Similar tickets are shown as references. No duplicate detection or linking. |
| Direct database edits | A ticket edited in the database (not through ingest) does not invalidate its suggestion. |
| Personal data | No PII redaction, retention or region rules beyond the log hygiene of CLAUDE.md. |
| Non-determinism | Temperature 0 does not guarantee identical output on Azure OpenAI. Not handled. |
| UI and review details | Merge of concurrent edits, first-opened timing bias, circuit reconnect and analyst identity are not designed yet. |
