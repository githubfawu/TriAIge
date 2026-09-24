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

**Umsetzungsstand** (Stand 2026-09-25, Details je Komponente in [architecture.md](architecture.md)):

| Stand | Anforderungen |
|---|---|
| umgesetzt | FR-01, FR-10, FR-11 (TF-IDF), FR-12, FR-14, FR-15, FR-31 (Pipeline stream-basiert), FR-34, FR-35, FR-36 |
| teilweise | FR-13 (Routing ist noch ein Stub, keine Statistik), FR-16 (nur der Kommentar, **Resolution-Status nicht umgesetzt**), FR-17 (`SimilarTicketKeys` im Vorschlag), FR-30 (`BatchRunner` liest die Challenge-Datei und schreibt `result.json` per direktem Pipeline-Aufruf, übergangsweise; Ziel ist Ingest, Worker und Export aus der DB, das ist geplant; Resolution-Status fehlt noch, Routing ist ein Stub), FR-33 (Validator prüft Enums, Service und Kommentar, nicht alle 7 Felder), NFR-03, NFR-04, NFR-07 |
| geplant | FR-02, FR-03, FR-04, FR-05, FR-20 bis FR-29, FR-32 (Temperature 0 im Agent, Seeds offen), NFR-10 und NFR-11 (erst mit Worker relevant) |
| entfallen | FR-18 (Confidence). Ebenso entfällt die Begründung aus FR-17 und FR-24: es gibt keine Confidence-Werte, keine `LowConfidence`-Markierung und keine Begründung pro Entscheidung |

**Resolution-Status:** Feld 6 der Erfolgskriterien (§2) ist im Code **nicht umgesetzt**. `TriageSuggestion.ResolutionStatus` existiert, bleibt aber immer `null` (der Core-Port `IResolutionDrafter` liefert nur einen String), `TriageResult` hat kein Feld dafür, und der Drafter liefert den Status nur Agents-seitig (`IResolutionDraftAgent`). Er wird in einem späteren Umsetzungszyklus ergänzt (Core-Port, `TriageSuggestion`, Validierung, `result.json`).

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
| FR-11 | Die Top-k ähnlichen historischen Tickets werden per kNN (Cosine Similarity, aktuell TF-IDF über die Description im Speicher, Embeddings geplant, siehe FR-03) über `ISimilarTicketSource.FindSimilarAsync(ticket, top, ct)` ermittelt. Das Ticket selbst ist nie im Ergebnis. k ist konfigurierbar (FR-35). | M |
| FR-12 | Das LLM bestimmt Work type und Affected Service mit Structured Output. Die ähnlichen Tickets dienen als Kontext, die mitgelieferten Werte nur als Hinweis. | M |
| FR-13 | Team und Assignee werden aus den Routing-Statistiken abgeleitet, nicht frei vom LLM erfunden. | M |
| FR-14 | Das LLM schätzt Urgency und Impact unter Berücksichtigung der Critical-Service-Liste. | M |
| FR-15 | Die Priority wird **deterministisch im Code** aus der Matrix berechnet, niemals durch das LLM. | M |
| FR-16 | Resolution-Status und Resolution-Kommentar werden auf Basis der besten bereinigten Vorlagen gedraftet, in der Stimme des Assignees. **Stand:** nur der Kommentar ist umgesetzt, der Resolution-Status folgt in einem späteren Zyklus. | M |
| FR-17 | Der Vorschlag führt die verwendeten Referenz-Tickets mit (Nachvollziehbarkeit). Es gibt keine Begründung pro Entscheidung. | S |
| FR-18 | *entfällt* (Confidence-Werte werden nicht erzeugt und nicht angezeigt). | – |

### 3.3 Human-in-the-Loop (Web-UI)

| ID | Anforderung | Prio |
|---|---|---|
| FR-20 | Liste aller Tickets mit Status (DB-Status: `New`, `Reviewing`, `Reviewed`, `HumanApproved`, `HumanRejected`; «in Analyse» und «fehlgeschlagen» werden aus `New` und `Ticket.Retries` abgeleitet, siehe [architecture.md §5.1](architecture.md)). | M |
| FR-21 | Review-Ansicht: Originalticket neben dem Vorschlag, alle Felder editierbar. | M |
| FR-22 | Der Analyst kann Approve, Edit oder Reject wählen. Der Entscheid wird über `IReviewService` (nicht über die Pipeline) persistiert, inkl. Änderungen pro Feld, Reject-Begründung und Zeitstempeln (Ingest, erstes Öffnen, Entscheid). Parallele Änderungen werden per Row-Version erkannt. | M |
| FR-23 | Bei Änderung von Urgency oder Impact wird die Priority automatisch neu berechnet. | M |
| FR-24 | Die Referenz-Tickets sind in der Review-Ansicht sichtbar. | S |
| FR-25 | Dashboard mit Metriken: Acceptance-Rate, Anzahl Edits pro Feld, Durchlaufzeit (Ingest → erstes Öffnen → Entscheid, aus den Zeitstempeln von FR-22). | S |
| FR-26 | Neues Ticket manuell erfassen oder als E-Mail-Text einfügen und triagieren lassen. Es wird wie jedes andere Ticket über FR-27 aufgenommen. | C |
| FR-27 | Neue Tickets werden über `ITicketIngestor` gespeichert und erhalten den Status `New`. Die Pipeline nimmt keine Tickets auf. | M |
| FR-28 | Ein Analyse-Worker (`BackgroundService` im Web) verarbeitet `New`-Tickets vorab: Ticket claimen (Lease `ClaimedAt`, Status bleibt `New`), Pipeline ausführen, Vorschlag speichern (Status `Reviewing`). Auslöser: Start, Timer, manuell oder Queue-Signal. Batchgrösse und Retry-Limit sind konfigurierbar. Ein Claim verhindert doppelte Analyse. | M |
| FR-29 | Öffnet der Analyst ein Ticket ohne Vorschlag, wird es mit Priorität in die Queue gestellt und die UI zeigt «wird analysiert». Das Öffnen ruft das LLM nie direkt auf. | M |

### 3.4 Batch-Verarbeitung (Submission)

| ID | Anforderung | Prio |
|---|---|---|
| FR-30 | Console-App liest die Challenge-Datei und schreibt eine Result-JSON-Datei im selben Schema. **Ziel (entschieden 2026-09-25):** Batch nimmt die Challenge-Tickets über `ITicketIngestor` auf (Status `New`, mit Herkunftsmarker) und exportiert `result.json` aus den vom Worker gespeicherten Vorschlägen (Challenge-Reihenfolge, ein Eintrag pro Ticket). So erscheinen die Tickets auch in der Web-UI und ein Mensch kann sie prüfen und finalisieren. **Aktuell:** übergangsweise direkter Pipeline-Aufruf, es wird nur `result.json` geschrieben. | M |
| FR-31 | Batch, Analyse-Worker und Web verwenden dieselbe `ITriagePipeline` (keine doppelte Logik). Ziel: Batch nutzt sie über den Worker (Entscheidung 2026-09-25, §7 Frage 7); übergangsweise ruft Batch sie noch direkt auf. Die Pipeline ist stream-basiert: `TriageAsync(IAsyncEnumerable<Ticket>, ct)` liefert pro Eingabeticket genau einen Vorschlag, sequenziell in Eingabereihenfolge (daneben `TriageAsync(Ticket, ct)` für ein einzelnes Ticket). Die Eingabe liefert `ITicketSource` (`IAsyncEnumerable<Ticket>`). | M |
| FR-32 | Der Output ist reproduzierbar (Temperature 0, fixe Seeds wo möglich). | S |
| FR-33 | Eine Validierung prüft vor dem Export **alle 7 Felder** aus §2 (Work type, Affected Service, Service Team(s), Assignee, Priority, Resolution-Status, Resolution-Kommentar): gültiges Vokabular (Enums, Service-Katalog, Team-Liste, Resolution-Vokabular), Priority konsistent mit der Matrix, keine leeren Pflichtfelder. **Stand:** der `SuggestionValidator` prüft erst Work type, Urgency, Impact, mindestens einen Service und einen nicht leeren Kommentar. Team, Assignee, Priority-Konsistenz und Resolution-Status fehlen noch (der Status, weil er nicht umgesetzt ist). | M |
| FR-34 | Fällt ein Analyseschritt aus (LLM nicht erreichbar, Timeout, Validierung schlägt fehl), zählt das als fehlgeschlagener Versuch. Die Pipeline wiederholt das Ticket bis `RetryCount` (FR-35) und liefert danach einen deterministischen Fallback (Work type und Service aus den ähnlichen Tickets, Urgency Medium, Impact Moderate, kein Kommentar). Jeder Fehlversuch wird im Fehlerprotokoll festgehalten (FR-36). Mit `StopSystemOnFailure` (FR-35) wird stattdessen beim ersten Fehler die Anwendung gestoppt. Umgesetzt in der Pipeline; es gibt keinen Status `Failed`, ein Ticket mit `Retries >= RetryCount` gilt als fehlgeschlagen. Das manuelle Neu-Einreihen (Retries zurücksetzen, Status `New`) ist noch offen ([architecture.md §5.2.2](architecture.md)). Ein Fehler blockiert die UI nie. | M |
| FR-35 | Einstellungen der Sektion `Triage` (appsettings): `RetryCount` (Default 3), `StopSystemOnFailure` (Default false, nur Entwicklung und Batch), `TicketTimeoutSeconds` (Default 60), `SimilarTicketCount` (Default 10), `RetryDelayMilliseconds` (Default 500). Ungültige Werte verhindern den Start. | M |
| FR-36 | Fehlerprotokoll: jeder fehlgeschlagene Versuch schreibt eine Zeile in die Tabelle `TriageFailure` (Ticket-Id, Key, Versuch, Grund, Exception-Typ, Stack-Frames, Zeitpunkt) und erhöht `Ticket.Retries`. `Retries` wird bei Erfolg auf 0 zurückgesetzt. Grund und Stack enthalten keine Exception-Meldung und keinen Ticket-Text. Ist `Retries` beim Start bereits am Limit, stoppt `StopSystemOnFailure` nicht erneut (Fallback statt Stopp). | M |

---

## 4. Nicht-funktionale Anforderungen

| ID | Anforderung | Prio |
|---|---|---|
| NFR-01 | Stack: .NET 10, Blazor Server, SQLite, Microsoft Agent Framework, .NET Aspire. | M |
| NFR-02 | Die Lösung startet mit einem Befehl über den Aspire AppHost. | M |
| NFR-03 | Health Checks für SQLite und Agent Framework sind vorhanden (`/health`). | M |
| NFR-04 | LLM-Provider ist per Konfiguration umschaltbar (Azure OpenAI, OpenAI, Apertus oder Ollama). | S |
| NFR-05 | Keine Secrets im Repository, nur user-secrets bzw. Aspire-Parameter. | M |
| NFR-06 | Triage eines Tickets dauert unter 15 Sekunden. | S |
| NFR-07 | Traces aller LLM-Aufrufe sind im Aspire Dashboard sichtbar (OpenTelemetry). | S |
| NFR-08 | Die Priority-Matrix ist vollständig durch Unit Tests abgedeckt (25 Kombinationen). | M |
| NFR-09 | Das Repository ist public-fähig: README mit Setup, Architektur und bekannten Limitationen. | S |
| NFR-10 | Kein LLM-Aufruf beim Prerender oder Öffnen einer Seite. Analysen laufen nur im Worker (übergangsweise auch direkt im Batch, siehe FR-30). | M |
| NFR-11 | SQLite hat einen einzigen Writer: Transaktionen des Workers sind kurz (Claim und Speichern getrennt vom LLM-Aufruf), der Worker arbeitet in kleinen Batches. | M |

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
6. **Analyse-Worker:** Wie oft läuft der Timer, wie gross ist der Batch, wie hoch ist das Retry-Limit? Annahme bis zur Klärung: Timer 30 s, Batch 5, 3 Versuche.
7. **Batch und Worker:** *Entschieden am 2026-09-25: ja.* Die 20 Challenge-Tickets laufen über denselben Pfad wie jedes Ticket (Ingest, Analyse-Worker, gespeicherter Vorschlag), damit sie in der Web-UI erscheinen und ein Mensch sie prüfen und finalisieren kann. Batch exportiert `result.json` aus den gespeicherten Vorschlägen (siehe FR-30, [architecture.md §5.4](architecture.md), [ADR-0002](adr/0002-background-analysis-worker.md)). Bis Ingest, Worker und Review-Persistenz existieren, ruft Batch die Pipeline übergangsweise direkt auf.
8. **Re-Analyse:** Darf ein Analyst einen Vorschlag neu berechnen lassen, und was passiert mit einer bereits erfassten Entscheidung?
9. **Batch-Export, Auslöser:** Wer startet den Worker und wer wartet auf ihn? Annahme: Batch wartet (Polling), bis alle Challenge-Tickets `New` verlassen haben, oder der Export wird bei Bedarf aus der Web-UI ausgelöst.
10. **Batch-Export, unfertige Tickets:** Was steht in `result.json` für ein Ticket, das noch `New` oder fehlgeschlagen ist? Annahme: der deterministische Fallback-Vorschlag wie heute (FR-34).
11. **Batch-Export, Werte:** KI-Vorschlag oder finale Werte des Analysten? Empfehlung: standardmässig der KI-Vorschlag, mit Schalter für die finalen menschlichen Werte.
12. **Herkunftsmarker und Writer:** Form des Markers auf `Ticket` (Challenge vs. Trainingshistorie, Filter für den Worker) und wie sichergestellt wird, dass Batch nicht parallel zum Web-Worker analysiert (SQLite hat einen Writer). Schema-Änderung, `data/triage.db*` danach löschen.

---

## 8. Out of Scope

- Echte Anbindung an Jira oder E-Mail-Postfächer (Ingest erfolgt über Datei oder UI)
- Fine-Tuning von Modellen
- Authentifizierung und Rollenkonzept in der UI
- Produktives Deployment
- Wiedereröffnen, Zurücknehmen oder Neuanalyse eines entschiedenen Tickets
- Wechsel des Embedding-Modells (kein Versionsschlüssel auf Vektoren)
- Tie-Break im Routing und Kaltstart bei seltenen Services (der Analyst entscheidet, keine Markierung)
- Prüfung, ob Team oder Assignee noch aktiv sind
- Duplikaterkennung und Verknüpfung von Tickets
- Direkte Änderungen in der Datenbank (nur Ingest invalidiert einen Vorschlag)
- PII-Maskierung, Aufbewahrung und Regionsvorgaben für Personendaten
- Garantierte Determinismus-Reproduzierbarkeit bei Azure OpenAI
- UI-Details des Reviews (Merge paralleler Edits, Circuit-Reconnect, Analyst-Identität)

Details und aktuelles Verhalten: [architecture.md §7](architecture.md).
