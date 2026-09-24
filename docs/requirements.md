# Requirements: AI Ticket Triage (Swiss Life Challenge)

**Hackathon:** Swiss {ai} Weeks – AI Support Agent for Operational Service Desks
**Ziel:** Eingehende Service-Desk-Tickets automatisch so triagieren und abarbeiten, wie es ein erfahrener L2-Agent tun würde. Der Mensch bleibt in Kontrolle.

---

## 1. Ausgangslage

Service Desks verlieren viel Zeit mit manueller Triage, der Suche in verstreuter Dokumentation und dem Neuschreiben ähnlicher Antworten. Qualität und Geschwindigkeit hängen stark vom einzelnen Analysten ab.

**Datenbasis:**

| Datei | Inhalt | Verwendung |
|---|---|---|
| `jira_first_20000_requested_fields_synthetic.json` | 20'000 historische Tickets, bewusst verrauscht | Wissensbasis für Routing, Retrieval und Resolution-Vorlagen |
| `jira_hackathon_20_new_tickets_challenge.json` | 20 neue Tickets, Felder teils leer oder falsch | wird triagiert und als Output bewertet |

**Bekannte Datenqualitätsprobleme (Trainingsset):**
- Affected Service teilweise falsch oder generisch
- Resolutions teilweise leer, nur «Problem fixed» oder inhaltlich unpassend zum Ticket
- Kommentare grösstenteils Templates ohne Informationsgehalt
- Priority, Urgency und Impact sind **zufällig** und dürfen nicht gelernt werden

---

## 2. Ziel & Erfolgskriterien

Pro Challenge-Ticket müssen folgende Felder korrekt bestimmt werden:

| # | Feld | Bewertung |
|---|---|---|
| 1 | Work type (`Incident` / `Service Request`) | korrekt trotz irreführender Titel |
| 2 | Affected Business or IT Services | tatsächlich betroffener Service, nicht der mitgelieferte Wert |
| 3 | Service Team(s) | Team, das den Service besitzt |
| 4 | Assignee | tatsächliche Routing-Person |
| 5 | Priority | **mathematisch konsistent** mit Urgency × Impact gemäss Matrix |
| 6 | Resolution (`done`, `cancelled`, `clarification`, `cannot reproduce`) | Vokabular der Trainingsdaten |
| 7 | Resolution-Kommentar | konkret, plausibel, in der Stimme des Assignees, keine Floskeln |

Urgency und Impact dürfen korrigiert werden (Teilpunkte). Volle Punkte gibt es nur, wenn die Priority zu den gewählten Werten passt.

**Business-Metriken für die Demo** (von Swiss Life genannt):
- Klassifikationsgenauigkeit
- Draft-Acceptance-Rate (Anteil unverändert freigegebener Vorschläge)
- Time-to-Resolution (Zeit vom Ingest bis zum Entscheid)

---

## 3. Funktionale Anforderungen

Priorisierung nach MoSCoW: **M** = Must, **S** = Should, **C** = Could.

### 3.1 Datenaufbereitung (einmalig beim Start)

| ID | Anforderung | Prio |
|---|---|---|
| FR-01 | Trainingsdaten werden idempotent in SQLite importiert. | M |
| FR-02 | Resolution-Kommentare werden bereinigt: Templates, «Problem fixed» und kurze «Resolution recorded»-Einträge werden verworfen. Nur substanzielle «Resolution:»-Texte bleiben. | M |
| FR-03 | Für Summary + Description werden Embeddings erzeugt, vorher dedupliziert und anschliessend persistent gecacht. | M |
| FR-04 | Routing-Statistiken werden per Mehrheitsentscheid berechnet: Service → Team, (Service, Team) → Assignee. Ob Business Entity oder Work type das Routing beeinflussen, wird in der Exploration geprüft. | M |
| FR-05 | Resolutions, die semantisch nicht zum Ticket passen, werden über einen Similarity-Schwellenwert herausgefiltert. | S |

### 3.2 Triage-Pipeline (pro Ticket)

| ID | Anforderung | Prio |
|---|---|---|
| FR-10 | Ticket einlesen und normalisieren. Leere oder verdächtige Felder werden markiert und nicht blind übernommen. | M |
| FR-11 | Die Top-k ähnlichen historischen Tickets werden per kNN (Cosine Similarity) ermittelt. | M |
| FR-12 | Das LLM bestimmt Work type und Affected Service mit Structured Output. Die ähnlichen Tickets dienen als Kontext, die mitgelieferten Werte nur als Hinweis. | M |
| FR-13 | Team und Assignee werden aus den Routing-Statistiken abgeleitet, nicht frei vom LLM erfunden. | M |
| FR-14 | Das LLM schätzt Urgency und Impact unter Berücksichtigung der Critical-Service-Liste. | M |
| FR-15 | Die Priority wird **deterministisch im Code** aus der Matrix berechnet, niemals durch das LLM. | M |
| FR-16 | Resolution-Status und Resolution-Kommentar werden auf Basis der besten bereinigten Vorlagen gedraftet, in der Stimme des Assignees. | M |
| FR-17 | Jede Entscheidung liefert eine kurze Begründung und die verwendeten Referenz-Tickets (Nachvollziehbarkeit). | S |
| FR-18 | Pro Feld wird ein Confidence-Wert ausgegeben. Bei tiefer Confidence wird das Feld in der UI hervorgehoben. | C |

### 3.3 Human-in-the-Loop (Web-UI)

| ID | Anforderung | Prio |
|---|---|---|
| FR-20 | Liste aller Tickets mit Status (Pending, Approved, Edited, Rejected). | M |
| FR-21 | Review-Ansicht: Originalticket neben dem Vorschlag, alle Felder editierbar. | M |
| FR-22 | Der Analyst kann Approve, Edit oder Reject wählen. Der Entscheid wird inkl. Änderungen persistiert. | M |
| FR-23 | Bei Änderung von Urgency oder Impact wird die Priority automatisch neu berechnet. | M |
| FR-24 | Die Referenz-Tickets und die Begründung sind in der Review-Ansicht sichtbar. | S |
| FR-25 | Dashboard mit Metriken: Acceptance-Rate, Anzahl Edits pro Feld, Durchlaufzeit. | S |
| FR-26 | Neues Ticket manuell erfassen oder als E-Mail-Text einfügen und triagieren lassen. | C |

### 3.4 Batch-Verarbeitung (Submission)

| ID | Anforderung | Prio |
|---|---|---|
| FR-30 | Console-App liest die Challenge-Datei und schreibt eine Result-JSON-Datei im selben Schema. | M |
| FR-31 | Batch und Web verwenden dieselbe Pipeline (keine doppelte Logik). | M |
| FR-32 | Der Output ist reproduzierbar (Temperature 0, fixe Seeds wo möglich). | S |
| FR-33 | Eine Validierung prüft vor dem Export: gültiges Vokabular, Priority konsistent mit der Matrix, keine leeren Pflichtfelder. | M |

---

## 4. Nicht-funktionale Anforderungen

| ID | Anforderung | Prio |
|---|---|---|
| NFR-01 | Stack: .NET 10, Blazor Server, SQLite, Microsoft Agent Framework, .NET Aspire. | M |
| NFR-02 | Die Lösung startet mit einem Befehl über den Aspire AppHost. | M |
| NFR-03 | Health Checks für SQLite und Agent Framework sind vorhanden (`/health`). | M |
| NFR-04 | LLM-Provider ist per Konfiguration umschaltbar (Azure OpenAI / Ollama). | S |
| NFR-05 | Keine Secrets im Repository, nur user-secrets bzw. Aspire-Parameter. | M |
| NFR-06 | Triage eines Tickets dauert unter 15 Sekunden. | S |
| NFR-07 | Traces aller LLM-Aufrufe sind im Aspire Dashboard sichtbar (OpenTelemetry). | S |
| NFR-08 | Die Priority-Matrix ist vollständig durch Unit Tests abgedeckt (25 Kombinationen). | M |
| NFR-09 | Das Repository ist public-fähig: README mit Setup, Architektur und bekannten Limitationen. | S |

---

## 5. Regeln der Challenge

- **Kein Hardcoding** der 20 Challenge-Tickets, weder manuell noch über fremde Outputs oder frühere Runs.
- Modell, Framework und Retrieval-Ansatz sind frei wählbar.
- Die Lösung muss auf beliebige neue Tickets generalisieren.

---

## 6. Referenz: Priority-Matrix

| Urgency \ Impact | Major / Widespread | Significant / Large | Moderate / Limited | Minor / Localized | No direct impact |
|---|---|---|---|---|---|
| **Critical** | Highest | Highest | High | Medium | Medium |
| **High** | Highest | High | High | Medium | Low |
| **Medium** | High | High | Medium | Low | Low |
| **Low** | Medium | Medium | Low | Low | Lowest |
| **Lowest** | Medium | Low | Low | Lowest | Lowest |

**Critical Services:** Trading Platform, Order Management, Trade Matching, Securities Settlement, Corporate Actions, Fund Pricing, NAV Calculation, Portfolio Accounting, Cash Management, Risk & Compliance Monitoring, Regulatory Reporting, SimCorp Dimension, Rimes Data Feed, Client Reporting

**Non-Critical Services:** Tax Reporting, CRM & Client Portal, Identity & Access Management, SharePoint & File Storage, Outlook & Email, Emailed Support Tickets

---

## 7. Offene Fragen (am Hackathon klären)

1. **Vokabular-Mapping Urgency/Impact:** In den Daten stehen Werte wie `highest`, `high`, `medium`, `low`, `lowest`, die Matrix verwendet für Impact aber Major, Significant, Moderate, Minor und No impact. Annahme bis zur Klärung: `highest`=Major, `high`=Significant, `medium`=Moderate, `low`=Minor, `lowest`=No impact. Gilt dasselbe für `critical` bei Urgency?
2. **Output-Format:** Exaktes Submission-Schema (Feldnamen, Gross-/Kleinschreibung, Resolution-Kommentar als eigenes Feld oder angehängt an «All Comments»)?
3. **Azure-Zugang:** Stellt Swiss Life Azure-OpenAI-Keys zur Verfügung oder braucht es eigene?
4. **Routing-Logik:** Hängt der Assignee von Business Entity oder Work type ab? Das klären die Exploration und eine Rückfrage beim Domain Owner.
5. **Mehrere Services pro Ticket:** Sind Listen mit mehr als einem Service im Referenz-Set zu erwarten?

---

## 8. Out of Scope

- Echte Anbindung an Jira oder E-Mail-Postfächer (Ingest erfolgt über Datei oder UI)
- Fine-Tuning von Modellen
- Authentifizierung und Rollenkonzept in der UI
- Produktives Deployment
