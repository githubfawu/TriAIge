# Architecture

How TicketTriage is built and how a ticket flows through it. Requirements: [requirements.md](requirements.md). Why it's built this way: [adr/](adr/).

> Status legend: **implemented** = in code today, **planned** = required but still a stub (`TODO: implement`). Update this file when a stub is replaced. Pipeline orchestration, normalization, validation, retry and fallback are implemented ([features/triage-pipeline](features/triage-pipeline/README.md)); of the ports behind it, similar tickets ([features/similar-ticket-retrieval](features/similar-ticket-retrieval/README.md)), the ticket source and the LLM classifier and drafter ([features/triage-agent](features/triage-agent/README.md)) are implemented; routing is still a stub. Ingest, analysis worker and review persistence are planned (§5).

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

    llm["LLM provider<br/>Azure OpenAI, OpenAI, Apertus or Ollama"]
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
    infra["Infrastructure<br/>EF Core/SQLite, import, pipeline, similar-ticket retrieval, stubs"]
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

Core has no references, and all dependencies point inward. Pipeline steps are Core interfaces (`ITicketSource`, `ISimilarTicketSource`, `ITicketClassifier`, `IRoutingResolver`, `IResolutionDrafter`, `ITriageFailureStore`, `ITriagePipeline`), implemented in Infrastructure (deterministic) or Agents (LLM-backed). `ITriagePipeline` only **analyses** tickets, as a stream (`TriageAsync(IAsyncEnumerable<Ticket>)`, one suggestion per ticket, in order) or for a single ticket. `ITicketSource` supplies the input stream (`DbTicketSource`, no caller yet). Two further Core ports keep the other concerns out of it: `ITicketIngestor` (saves incoming tickets as `New`) and `IReviewService` (persists the analyst's decision, edits and timestamps). The analysis worker is a `BackgroundService` in Web that calls the pipeline, see [ADR-0002](adr/0002-background-analysis-worker.md).

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
    out[/TriageSuggestion<br/>+ reference ticket keys/]

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
| 1 Normalize & retrieve | `TicketNormalizer` (implemented) + `ISimilarTicketSource` | code (TF-IDF + cosine kNN over `Description`, in memory) | normalize and `DbSimilarTicketSource` implemented ([features/similar-ticket-retrieval](features/similar-ticket-retrieval/README.md)); no embeddings / BM25 |
| 2 Classify | `ITicketClassifier` | LLM, validated against the service catalog (`IServiceCatalogProvider`) / enums | implemented (`LlmTicketClassifier`); `ServiceCatalog` in Core still has placeholder names |
| 3 Route | `IRoutingResolver` | code (majority vote), LLM never invents names | stub |
| 4 Assess & prioritize | `ITicketClassifier` + `PriorityMatrix` | LLM (urgency, impact) → **code** (priority) | implemented (urgency + impact come from the same LLM call as step 2; matrix in Core) |
| 5 Draft & validate | `IResolutionDrafter` + `SuggestionValidator` | LLM draft from cleaned templates → code validation (FR-33) | drafter implemented for the **comment** (`LlmResolutionDrafter`); **resolution status not implemented** (produced Agents-side only, `TriageSuggestion.ResolutionStatus` stays null, later cycle). Validator **partial** (enums, at least one service, comment; still missing: team, assignee, priority consistency, resolution status, services against `ServiceCatalog`), target is all 7 fields (FR-33) |
| Orchestration | `ITriagePipeline` (analysis only, stream) | code: sequential, timeout, retry, failure log, fallback (FR-34…36) | implemented |
| Input stream | `ITicketSource` | code | `DbTicketSource` implemented (streams `New` tickets from SQLite), registered in DI, no caller yet |
| Failure log | `ITriageFailureStore` (`EfTriageFailureStore`) | code, `Ticket.Retries` + table `TriageFailure` | implemented |
| Ingest | `ITicketIngestor` | code, saves tickets as `New` | planned |
| Analysis worker | `BackgroundService` in Web | code, claims `New` tickets (lease `ClaimedAt`) and calls the pipeline (ADR-0002, §5) | planned |
| Review persistence | `IReviewService` | code, decision + per-field edits + timestamps | planned |

### 4.1 Pipeline rules

> There are no confidence values, no `LowConfidence` flag and no per-decision reasoning (decision: not needed, FR-18 dropped). The suggestion only carries the keys of the reference tickets (FR-17).
>
> **Seven output fields.** The scored fields are work type, affected service, service team(s), assignee, priority, resolution status and resolution comment (requirements §2). Validation (FR-33) has to cover all seven. **Resolution status is not implemented**: `TriageSuggestion.ResolutionStatus` exists but is always null, `TriageResult` has no field for it, and only `IResolutionDraftAgent` in Agents produces it. It is planned for a later cycle. Today's `SuggestionValidator` checks work type, urgency, impact, at least one service and the comment.

| Topic | Rule |
|---|---|
| **No self-retrieval** | `ISimilarTicketSource.FindSimilarAsync(ticket, top, ct)` receives the ticket under analysis and excludes it from the kNN result. This matters when a challenge ticket also appears in `training.json`, and for tickets that are re-analysed. |
| **Language** | The language is detected once per ticket. The drafted resolution and comment use that language, and the validator checks against it. Service, team and enum names stay in the fixed English vocabulary of the catalog. |
| **Multiple services** | Open: it is not yet confirmed that a ticket can have more than one service (requirements §7 no. 5). The field is a list in the export, so the model keeps a list. Urgency, impact and priority are assessed once per ticket. Until the data is checked, routing uses the first service the classifier lists, and all listed services are shown as affected. If several services do occur and map to different teams, a proper rule is needed. The rule "service with the highest ticket priority" was considered and dropped for now, because it needs urgency and impact per service. |
| **Cold start and ties** | With few or no similar tickets, or a tie in the majority vote, the suggestion is still produced (fallback values where nothing better exists) and the analyst decides. No flag and no further tie-break logic for now. |
| **Text length** | The importer truncates imported text (summary 250, description 1000, assignee 50, resolution 500, comment 500 characters). The retrieval tokenizer caps text at 20 000 characters. No truncation or chunking in the pipeline. |
| **Reference tickets** | Similar tickets and their resolutions come from our own cleaned and controlled training data (FR-02, FR-05), so they are trusted. Only the incoming ticket text is treated as untrusted input. |

## 5. Ticket lifecycle and human in the loop

Suggestions are **pre-computed** by a background worker. Opening a ticket only reads the stored result ([ADR-0002](adr/0002-background-analysis-worker.md)).

> **Status: planned.** `ITicketIngestor`, the analysis worker and `IReviewService` do not exist in code yet. What exists: the pipeline (§4), `DbTicketSource` (streams `New` tickets, no caller), the `Status` lookup and the `*Changed` columns on `Ticket`. This section describes the intended behaviour on top of the **DB status model**.

### 5.1 Ticket states

The states are the rows of the `Status` lookup table (seeded in `TriageDbContext`): `New`, `Reviewing`, `Reviewed`, `HumanRejected`, `HumanApproved`. There is no `Analysing` and no `Failed` status; "in analysis" and "failed" are derived, see below.

```mermaid
stateDiagram-v2
    [*] --> New: ingest (planned) / training import without resolution
    [*] --> HumanApproved: training import with resolution
    New --> New: attempt failed, Retries < RetryCount
    New --> Reviewing: worker stores the suggestion (AI values in the *Changed columns)
    Reviewing --> New: source ticket edited, no decision yet
    Reviewing --> Reviewed: analyst saved edits (proposal)
    Reviewing --> HumanApproved: analyst approves
    Reviewing --> HumanRejected: analyst rejects (reason)
    Reviewed --> HumanApproved: analyst approves
    Reviewed --> HumanRejected: analyst rejects (reason)
```

| Status | Meaning | Implementation |
|---|---|---|
| `New` | Ticket has no stored suggestion yet. Input of the worker and of `DbTicketSource`. While a worker holds it, a planned nullable `ClaimedAt` lease marks it as "in analysis". | import and source implemented, claim planned |
| `Reviewing` | A suggestion is stored (also a fallback suggestion, see §5.2.2) and waits for the analyst. | planned |
| `Reviewed` | **Semantics still open.** Proposal: the analyst saved edits but has not decided yet. If the team does not need it, the state can stay unused. | open |
| `HumanApproved` / `HumanRejected` | Final decision (FR-22). Approved with or without edits, told apart by the per-field edits. Imported training tickets that have a resolution are `HumanApproved`. | decisions planned, import implemented |

The ticket list (FR-20) shows `New` as "analysing" (or "queued"), and a `New` ticket whose `Retries` reached `Triage:RetryCount` as "failed". A failed ticket still shows the deterministic fallback values (FR-34). `HumanApproved` and `HumanRejected` are final. Re-opening, un-rejecting and re-analysis after a decision are out of scope (§7).

> **Known risk.** Imported training tickets without a resolution also have status `New`. Once a worker reads `New` tickets through `DbTicketSource`, it would triage historical data. A source/status filter is needed before the worker is wired.

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
        W->>D: claim next New tickets, batch (ClaimedAt = now)
        D-->>W: tickets
        W->>P: TriageAsync(ticket)
        P->>D: similar tickets (TF-IDF), routing statistics
        D-->>P: similar tickets, team / assignee votes
        P->>L: classify + assess urgency + impact (structured output)
        L-->>P: work type, services, urgency, impact
        Note over P: route from statistics, priority = PriorityMatrix
        P->>L: draft resolution + comment
        L-->>P: draft
        Note over P: validate vocabulary, matrix, required fields (FR-33)
        P-->>W: TriageSuggestion
        W->>D: save suggestion (status Reviewing, ClaimedAt cleared)
    end
    Note over W,D: attempt failed: pipeline retries, Ticket.Retries + 1, TriageFailure row.<br/>Retries exhausted: deterministic fallback suggestion (FR-34)
```

### 5.2.1 Worker rules

These rules close the gaps of the diagram above. All of them apply to the worker (planned). Batch follows the per-ticket rules of §5.4.

| Rule | Definition |
|---|---|
| **Start gate** | The worker starts claiming only after data preparation (import, embeddings, routing statistics, §3) has completed. A persisted "data ready" marker tells it. Until then ingest still saves tickets as `New`, they wait. Without this gate, retrieval would run on a half-imported set. |
| **Atomic claim** | A claim is one conditional statement, `UPDATE Tickets SET ClaimedAt=@now WHERE Id IN (…) AND StatusId=<New> AND ClaimedAt IS NULL`, followed by a read of the rows that were changed. Two overlapping ticks or two instances can never claim the same ticket, because SQLite has a single writer. The transaction covers only the claim and is never held across an LLM call. |
| **Lease and sweep** | `ClaimedAt` is the lease. On startup and on every tick, `New` tickets with `ClaimedAt` older than the lease (assumption: 5 min, above the per-ticket timeout) are released (`ClaimedAt = NULL`). This recovers tickets after a crash or a restart. |
| **Per-ticket timeout** | Done by the pipeline: each attempt runs under `Triage:TicketTimeoutSeconds` (60 s), on top of the resilience timeouts per LLM call. |
| **Failure isolation** | Tickets of a batch are processed independently. An exception or timeout affects only its own ticket and never the rest of the batch or the worker loop. |
| **Incomplete call** | Handled by the pipeline: a failed attempt (LLM error, timeout, validation failure) is retried up to `Triage:RetryCount` and logged (`TriageFailure`, `Ticket.Retries`). There are no partial suggestions. |
| **Reviewing is closed to the worker** | The worker only claims `New`. A ticket in `Reviewing` or later is never re-analysed and its suggestion is never overwritten, so it cannot change while an analyst edits it. The row-version check of §5.3 still protects against two analysts. |
| **Source edit** | If a ticket is changed in the source (a re-ingest with the same key) and it has no final decision yet, its suggestion is dropped and the status goes back to `New`. A decided ticket (`HumanApproved`, `HumanRejected`) is not changed. Edits made directly in the database are out of scope. |
| **Ingest = upsert** | `ITicketIngestor` upserts on the Jira key. It never creates a duplicate row. An unchanged re-send is a no-op. (The DB has no Jira key column yet.) |

### 5.2.2 One retry model (decision)

There is a single retry mechanism: the **pipeline's**. The worker adds no counter of its own.

| | Pipeline (implemented) | Worker (planned) |
|---|---|---|
| Counter | `Ticket.Retries`, reset to 0 on success | none (the worker does not count attempts) |
| Limit | `Triage:RetryCount` | – |
| On exhaustion | deterministic fallback suggestion is returned | worker stores it like any suggestion (`Reviewing`). The ticket is shown as "failed" because `Retries >= RetryCount` |
| Failure record | table `TriageFailure` (reason, frames) | – |
| Timeout | `Triage:TicketTimeoutSeconds` per attempt | lease `ClaimedAt` only guards against crashes |

Consequences: no `Attempts` column and no `Failed` status. A manual re-queue of a failed ticket means resetting `Retries` to 0 and the status to `New` (planned, not designed). Details of the pipeline side: [features/triage-pipeline](features/triage-pipeline/README.md).

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
    else not analysed yet (status New)
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

If the ticket changed in the meantime (another analyst decided, or the worker re-analysed it), `rowVersion` no longer matches, the write is rejected and the UI reloads. (No row-version column exists yet.)

### 5.4 Batch (challenge submission)

Batch does not use the worker. It calls `ITriagePipeline` directly for each challenge ticket, runs the FR-33 validation and writes `result.json`. The pipeline code is the same as in 5.2.

- **Every ticket is evaluated.** `result.json` always contains one entry per challenge ticket. The status is kept per ticket, not per run. A ticket that fails does not stop the run.
- **Per-ticket retry, then fallback.** Retry, per-ticket timeout and fallback are done by the pipeline itself (`Triage:RetryCount`, `Triage:TicketTimeoutSeconds`, FR-34), so Batch gets one suggestion per ticket from the stream. The console summary that reports failed tickets and the call of the stream from `BatchRunner` are not implemented yet. The file is never partial. With `Triage:StopSystemOnFailure` the run is aborted on the first failure (development only).
- **No self-retrieval.** kNN excludes the ticket itself (see §4.1).
- **Data ready.** Batch checks the same "data ready" marker as the worker and stops with a clear message if data preparation is not complete.

## 6. Cross-cutting

| Concern | Approach |
|---|---|
| Configuration | `Llm` section (`AzureOpenAI` \| `OpenAI` \| `Apertus` \| `Ollama`), AppHost parameters → env vars, secrets in user secrets only. `Triage` section (retry, timeout, stop switch) in appsettings |
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
| Rare services, ties in routing | The analyst decides. No tie-break, no flag. |
| Inactive team or assignee | Routing statistics come from history and may name someone who has left. Not checked. |
| Duplicate tickets | Similar tickets are shown as references. No duplicate detection or linking. |
| Direct database edits | A ticket edited in the database (not through ingest) does not invalidate its suggestion. |
| Personal data | No PII redaction, retention or region rules beyond the log hygiene of CLAUDE.md. |
| Non-determinism | Temperature 0 does not guarantee identical output on Azure OpenAI. Not handled. |
| UI and review details | Merge of concurrent edits, first-opened timing bias, circuit reconnect and analyst identity are not designed yet. |
