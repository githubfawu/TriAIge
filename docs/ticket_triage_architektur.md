# Architektur: AI Ticket Triage (Swiss AI Weeks Hackathon)

Referenz-Architektur für die Challenge "AI-Powered Ticket Triage", aufbauend auf dem im RunningBot-Capstone validierten Stack (LangGraph, Qdrant, bge-m3, Hybrid-Retrieval).

## 1. Ingestion & Indexing Pipeline (einmalig, auf den 20k Trainingsdaten)

```mermaid
flowchart TD
    A["jira_first_20000\n_requested_fields_synthetic.json"] --> B["Parser / Loader\n(pydantic-Schema pro Ticket)"]
    B --> C{"Pro Ticket:\nFelder normalisieren"}
    C --> D["Ticket-Dokument bauen\n(Title + Description + Service +\nTeam + Assignee als Metadata-Header)"]
    C --> E["Kommentar-Thread bauen\n(chronologisch verkettet,\nseparates Dokument)"]

    D --> F["Embedding-Modell: bge-m3\n(1 Ticket = 1 Chunk, kein Split)"]
    E --> G["Embedding-Modell: bge-m3\n(1 Kommentar-Thread = 1 Chunk)"]

    F --> H[("Qdrant Collection\n'tickets_dense'")]
    G --> I[("Qdrant Collection\n'resolutions_dense'")]

    D --> J["BM25-Index (sparse)\nüber Title+Description"]
    J --> K[("BM25 Store\n'tickets_sparse'")]

    C --> L["Statistik-Tabellen extrahieren\n(Service→Team→Assignee Mapping,\nhäufigste Resolution-Patterns pro Service)"]
    L --> M[("Lookup-Tabelle\nservice_team_assignee.parquet")]

    N["Priority-Matrix (Urgency×Impact)\n+ Critical-Service-Liste"] --> O[("Statische Regel-KB\npriority_matrix.json")]
```

**Wichtige Design-Entscheidungen:**

| Aspekt | Entscheidung | Begründung |
|---|---|---|
| Chunking | **Kein** klassisches Chunk-Splitting pro Ticket | Tickets sind kurz (Title+Description meist < 500 Tokens) → ein Ticket = ein Chunk vermeidet Kontextverlust an künstlichen Chunk-Grenzen |
| Zwei Collections | `tickets_dense` (Problem) getrennt von `resolutions_dense` (Lösung) | Beim Triage brauchst du zuerst "ähnliches Problem", danach separat "wie wurde es gelöst" — unterschiedliche Query-Absicht |
| Embedding-Modell | `bge-m3` | Im Capstone bereits gegen `nomic-embed-text` validiert, mehrsprachig (CH/FR/DE/LUX-Tickets), gute Cross-Lingual-Performance |
| Hybrid Retrieval | Dense (bge-m3) + Sparse (BM25) via EnsembleRetriever | Tickets enthalten viele exakte Fachbegriffe (Fehlercodes, Systemnamen wie "SimCorp Dimension", "Rimes") — BM25 fängt das ab, was Dense-Embeddings "verwässern" |
| Priority | **Kein RAG/LLM**, sondern deterministische Lookup-Tabelle | Ist laut Challenge exakt matrixbasiert — LLM-Einsatz würde nur Fehlerquelle einbauen |
| Service→Team→Assignee | Statistik-Tabelle aus 20k Tickets (Mehrheitsentscheid) + LLM-Fallback bei Unschärfe | Deterministisch wo möglich, LLM nur wenn Mapping in Trainingsdaten uneindeutig ist |

## 2. Triage-Workflow pro neuem Ticket (LangGraph StateGraph)

```mermaid
flowchart TD
    Start(["Neues Challenge-Ticket\n(JSON, teils lückenhaft)"]) --> P["Node: parse_ticket\nRohfelder ins TicketState laden"]

    P --> WT["Node: classify_work_type\nLLM: Incident vs. Service Request?\n(Titel bewusst irreführend → auf\nDescription/Comments fokussieren)"]

    WT --> RS["Node: retrieve_similar_tickets\nHybrid-Retrieval gegen 'tickets_dense'\n+ BM25, Filter optional auf\nvermutete Service-Kategorie"]

    RS --> SV["Node: verify_service\nLLM vergleicht gemeldeten Service\nmit Top-k ähnlichen historischen Tickets\n→ korrigiert 'Affected Service' falls nötig"]

    SV --> TA["Node: lookup_team_assignee\nDeterministisch aus\nservice_team_assignee.parquet\n(Fallback: LLM bei Mehrdeutigkeit)"]

    TA --> UI["Node: assess_urgency_impact\nLLM bewertet Urgency + Impact\nanhand Ticket-Inhalt +\nCritical-Service-Liste"]

    UI --> PR["Node: compute_priority\nReiner Lookup in priority_matrix.json\n(kein LLM — deterministisch)"]

    PR --> RR["Node: retrieve_resolution_pattern\nHybrid-Retrieval gegen\n'resolutions_dense', gefiltert auf\nkorrigierten Service + ähnliches Problem"]

    RR --> RD["Node: decide_resolution_status\nLLM: done / cancelled /\nclarification / cannot reproduce\n(gestützt auf Retrieval-Kontext)"]

    RD --> RC["Node: generate_resolution_comment\nLLM schreibt Kommentar im Ton\ndes zugewiesenen Agenten,\ngrounded auf Retrieval-Beispiele"]

    RC --> VAL{"Node: validate_output\nPydantic-Schema-Check\nalle 7 Felder vollständig?"}

    VAL -- "nein / Fehler" --> HITL["Node: human_review\ninterrupt() → manuelle Korrektur\n(analog HITL-Pattern Kap. 11)"]
    HITL --> VAL

    VAL -- "ok" --> Out(["Output: triaged_ticket.json\n(Work Type, Service, Team,\nAssignee, Priority, Resolution,\nResolution-Kommentar)"])
```

## 3. State-Objekt (Vorschlag)

```python
class TicketState(TypedDict):
    raw_ticket: dict
    work_type: str | None
    reported_service: str
    verified_service: str | None
    service_team: str | None
    assignee: str | None
    urgency: str | None
    impact: str | None
    priority: str | None          # via Matrix-Lookup, nicht LLM
    retrieved_similar_tickets: list[dict]
    retrieved_resolutions: list[dict]
    resolution_status: str | None  # done | cancelled | clarification | cannot reproduce
    resolution_comment: str | None
    validation_errors: list[str]
```

## 4. Modell-Einsatz (Hackathon-pragmatisch)

| Node | Modell-Empfehlung | Grund |
|---|---|---|
| `classify_work_type`, `verify_service`, `assess_urgency_impact` | lokal via Ollama, z. B. `qwen2.5:7b` | schnell, kostenlos, ausreichend für Klassifikation |
| `generate_resolution_comment` | grösseres Modell (z. B. `gpt-4o-mini` via API) | Textqualität zählt direkt ins Scoring ("spezifisch statt generisch") |
| `compute_priority`, `lookup_team_assignee` | **kein LLM** | deterministische Funktionen |

## 5. Kritischer Punkt für die Bewertung

> "Resolution comments that are specific and plausible, not generic filler."

→ Der `retrieve_resolution_pattern`-Node ist der Hebel: ohne konkret abgerufenes, ähnliches historisches Ticket generiert das LLM zwangsläufig generische Floskeln ("issue fixed"). Retrieval-Qualität hier ist wichtiger als Prompt-Tuning.
