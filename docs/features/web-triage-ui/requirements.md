# Requirements: web-triage-ui

**Date**: 2026-09-24
**Branch**: `feat/web-triage-ui` · **Modus**: `/orchestrate … fast` (≤ 4 h, max. 3 Slices)
**Bezug**: [docs/requirements.md](../../requirements.md) FR-18, FR-20–FR-29, FR-34, NFR-10/11 · [architecture.md](../../architecture.md) §4, §5 · [ADR-0002](../../adr/0002-background-analysis-worker.md)

## Problem

Die Web-UI zeigt heute eine rohe Liste der Tickets mit AI-Änderungen und eine Review-Seite, die direkt auf DB-Spalten arbeitet. Für die Demo fehlt der Human-in-the-Loop-Ablauf aus FR-20 bis FR-29: Tickets hochladen, live sehen, wie der Agent sie verarbeitet (Ampel), Vorschläge prüfen, mit Accept, Speichern oder Reject entscheiden und die Metriken zeigen. Ohne diese UI sieht die Jury weder den HITL-Teil noch die Business-Metriken von Swiss Life (Acceptance-Rate, Durchlaufzeit).

## Users & Context

- **Analyst** (einzige Rolle, keine Anmeldung) in `TicketTriage.Web` (Blazor Server, global Interactive Server, MudBlazor).
- **Jury-Demo**: challenge.json hochladen → Ampeln wechseln live → Review → Dashboard.
- `TicketTriage.Batch` (challenge.json → result.json) ist nicht betroffen.
- UI-Sprache **Englisch** (wie bisher, passend zu Daten und Enum-Werten).

## Functional Requirements

### Upload (FR-27, angepasst)

- FR1: Seite `/upload`: JSON-Datei wählen oder per Drag & Drop ablegen. Sie wird in Core-`Ticket` geparst (Top-Level-Array, JSON-Namen aus `Ticket`). Grenzen: 1 MB, 200 Tickets.
- FR2: Vorschau-Tabelle pro Eintrag: Issue key, Summary, gültig/ungültig mit Grund, Hinweise (gekürzt, unbekannte Werte werden leer gespeichert, FR-10) und Treffer in der DB (siehe FR4). Vor der Bestätigung wird nichts gespeichert.
- FR3: «Save & analyse» speichert alle gültigen Einträge in **einer** Transaktion als `TicketEntity` mit `StatusId` = New. Das Mapping entspricht dem `TrainingDataImporter`: Namen → Lookup-IDs, Impact übersetzt, erster Service und erstes Team, Texte auf Spaltenlänge gekürzt, Kommentare als `CommentEntity`. Danach kommen die Tickets in die Web-Queue. Issue key und das geparste `Ticket` bleiben im RAM.
- FR4: Duplikate gegenüber der DB (gleiche Summary + Description, Created falls vorhanden): Ein Treffer ohne Vorschlag wird nicht neu angelegt, sondern neu eingereiht (Fall nach einem Neustart). Ein Treffer mit Vorschlag oder Entscheid wird übersprungen und in der Vorschau als «already in triage» angezeigt.
- FR5: Import-Falle: Enthält die DB kein Ticket mit Status `Finished`, sind die Training-Daten nicht importiert. Die Seite warnt dann, und gespeichert wird nur nach expliziter Bestätigung. Grund: `TrainingDataImporter` überspringt den Import, sobald irgendein Ticket existiert.

### Agent-Verarbeitung (FR-28, FR-34, Worker im Web laut ADR-0002)

- FR6: Ein `BackgroundService` im Web arbeitet die RAM-Queue ab, ein Ticket nach dem anderen. Ablauf: Queued → Analysing → `ITriagePipeline.TriageAsync` (aus DI, heute Stub) → Vorschlag in die `*Changed`-Spalten schreiben → Pending. Verarbeitet werden **nur** eingereihte Tickets, nie andere New-Tickets der DB.
- FR7: Mapping Vorschlag → `*Changed`: WorkType, erster AffectedService, erstes ServiceTeam, Assignee, Urgency, Impact (übersetzt), Priority = `PriorityMatrix.Resolve` (nie aus dem Modell), ResolutionStatus → `ResolutionChanged`. **Alle** Werte werden geschrieben, auch wenn sie dem Original gleichen, damit «hat Vorschlag» immer «analysiert» bedeutet. Ein Wert, der nicht in der Lookup-Tabelle steht, bleibt `null`, und das Feld wird im Review als «not in catalog» markiert. Im RAM liegen: DraftComment, vollständige Service- und Team-Listen, SimilarTicketKeys und Confidence.
- FR8: Bei einer Exception oder einem Timeout (60 s pro Ticket) werden die Versuche um 1 erhöht und das Ticket wieder eingereiht. Nach 3 Versuchen ist es `Failed` (RAM). Ein Teilergebnis wird nie gespeichert. Ein Fehler blockiert weder die UI noch die Queue.
- FR9: `Failed`-Tickets haben in Liste und Review den Button «Re-queue»: Die Versuche werden zurückgesetzt, und das Ticket kommt wieder in die Queue.
- FR10 (FR-29): Beim Öffnen des Reviews ohne Vorschlag:
  - Ticket steht in der Queue: Es wird nach vorne gezogen, die Seite zeigt «Agent is analysing…» und aktualisiert sich selbst.
  - New-Ticket der DB, das nicht in der Queue ist (z.B. nach einem Neustart): Button «Analyse now».

  Das Öffnen ruft nie selbst Pipeline oder LLM auf (NFR-10).

### Ampel & Liste (FR-20)

- FR11: Ein Web-eigenes Anzeige-Enum wird aus DB und RAM abgeleitet. Die Core-Enums bleiben unverändert. Die drei Review-Farben entsprechen 1:1 `ReviewDecision`:

  | Zustand | Quelle | Ampel |
  |---|---|---|
  | Queued | RAM-Queue, kein Vorschlag | ○ grau |
  | Analysing | RAM | ◌ blau, pulsierend |
  | Failed | RAM | ⚠ rot + «Re-queue» |
  | Pending (`ReviewDecision.Pending`) | DB: New + `*Changed` gesetzt | ● gelb |
  | Approved (`ReviewDecision.Approved`) | DB: `HumanApproved` | ● grün |
  | Rejected (`ReviewDecision.Rejected`) | DB: `HumanRejected` | ● rot |

- FR12: Wiederverwendbare Komponente `TrafficLight`: In der Liste ein Punkt mit Label, im Review-Kopf eine große 3-Licht-Ampel. Der Zustand wird immer mit Farbe, Icon **und** Text gezeigt.
- FR13: Seite `/tickets` als `MudDataGrid` mit den Spalten Ampel, #Id, Issue key (sonst «—»), Summary, Work type, Priority und Zustand. Filter-Chips pro Zustand mit Anzahl, Zeilenklick öffnet das Review. Angezeigt werden nur Triage-Tickets: die Uploads der Sitzung und alle DB-Tickets mit Vorschlag oder Entscheid. Training-Tickets erscheinen nicht.
- FR14: Liste, Ampel und Review aktualisieren sich live, wenn der Worker einen Zustand ändert (Event → `InvokeAsync(StateHasChanged)`), ohne Reload. Ein Fortschrittsbalken zeigt «x of n analysed» für den laufenden Upload.

### Review (FR-21 bis FR-24)

- FR15: Seite `/review/{id}`: links das Original read-only (Summary, Description, Comments, ursprüngliche Feldwerte), rechts der Vorschlag als Formular. Felder, die vom Original abweichen, sind hervorgehoben.
- FR16: Editierbar sind Work type, Affected service, Service team, Assignee, Urgency, Impact, Resolution (Status) und Comment (Entwurf). Priority ist nicht editierbar und wird bei jeder Änderung von Urgency oder Impact sofort per Core-`PriorityMatrix` neu berechnet (FR-23).
- FR17: Jedes Feld hat ein Badge in den Farben aus architecture.md §4, das zeigt, wer es bestimmt:
  - Code (blau): Service team, Assignee, Priority
  - LLM (amber): Work type, Affected service
  - LLM + Code (violett): Urgency, Impact, Resolution, Comment
- FR18: Buttons:
  - **Accept** übernimmt den Vorschlag unverändert (Approved, 0 Edits) und ist nur aktiv ohne Änderungen.
  - **Save** übernimmt die bearbeiteten Werte (Approved) und zählt die Edits pro Feld im RAM. Nur aktiv, wenn mindestens ein Feld vom Vorschlag abweicht.
  - **Reset** setzt das Formular auf den Vorschlag zurück.
  - **Reject** öffnet einen Dialog mit Pflicht-Grund (Rejected). Der Grund bleibt im RAM.
- FR19: Der Entscheid wird im Web über `IDbContextFactory` persistiert, in einer Transaktion und nur, wenn das Ticket noch Pending ist:
  - Approve: Die Original-Spalten erhalten die finalen Werte, `PriorityId` = Matrix, `*Changed` → `null`, `StatusId` = `HumanApproved`. Ein nicht leerer Kommentar wird als neue `CommentEntity` gespeichert.
  - Reject: `*Changed` → `null`, `StatusId` = `HumanRejected`, die Original-Spalten bleiben unverändert.
  - Ist das Ticket nicht mehr Pending, wird nichts geschrieben: Meldung «Already decided» und Reload.
  - Danach eine Snackbar und weiter zum nächsten Pending-Ticket, sonst zurück zur Liste.
- FR20: Entschiedene Tickets öffnen read-only, Entscheide sind endgültig (architecture.md §5.1). Training-Tickets öffnen read-only mit dem Hinweis «Not part of triage».
- FR21 (FR-24, FR-18): Ein Panel «References & confidence» zeigt SimilarTicketKeys und die Confidence. Leer (Stubs) oder nach einem Neustart zeigt es «No references» bzw. «—».

### Dashboard (FR-25)

- FR22: `/` wird erweitert um:
  - Zähler pro Ampel-Zustand; ein Klick öffnet die gefilterte Liste
  - **Approval rate** = Approved / (Approved + Rejected) aus der DB, persistent
  - **Acceptance rate** = unverändert freigegeben / alle freigegebenen, für die Sitzung
  - Edits pro Feld, für die Sitzung
  - Ø Zeit Upload → erstes Öffnen → Entscheid, für die Sitzung

  Der bestehende Health-Block bleibt.

### Styles

- FR23: Ein `MudTheme` mit den Farben aus architecture.md §4: Primary `#2563eb` (Code), Secondary `#7c3aed` (LLM+Code), Tertiary `#d97706` (LLM), helle Flächen `#dbeafe`, `#ede9fe` und `#fef3c7` für Badges. Die Ampel-Farben (grün, gelb, rot, grau, blau) müssen sich klar vom LLM-Amber unterscheiden. Aus docs/ werden nur die Styles übernommen, keine Logik.

### Manuelles Ticket (FR-26), Could

- FR24: Ein Formular «New ticket» (Summary Pflicht, Description, optional Work type und Service) nutzt denselben Speicher- und Queue-Weg wie FR3. Nur wenn nach Slice 3 noch Zeit bleibt, sonst Out of Scope.

## Non-Functional Requirements

- NFR1 «nur Web»: Änderungen nur in `src/TicketTriage.Web/**`. Ausnahmen:
  - neues `tests/TicketTriage.Web.Tests/**`
  - je 1 Zeile in `TicketTriage.slnx` und `Directory.Packages.props` (bUnit)
  - `docs/features/web-triage-ui/**`

  Keine Änderungen an Core, Infrastructure, Agents, Batch oder AppHost, keine Schema-Änderung, keine Migration.
- NFR2: Kein Pipeline- oder LLM-Aufruf beim Prerender oder beim Öffnen einer Seite, nur im Worker (NFR-10).
- NFR3: SQLite hat einen einzigen Writer. Transaktionen sind kurz (Upload speichern, Vorschlag speichern, Entscheid speichern) und laufen nie über einen Pipeline-Aufruf hinweg (NFR-11).
- NFR4: Vorschau von 20 Tickets < 1 s. Eine Zustandsänderung erscheint in der Liste < 1 s.
- NFR5: Ticket-Text und Modell-Output werden nur als Plain Text gerendert (kein `MarkupString`), wegen Prompt-Injection und XSS.
- NFR6: Keine Ticket-Inhalte in Logs auf Information-Level.
- NFR7: Der RAM-Store ist ein Singleton für alle Circuits und daher thread-safe. Komponenten melden sich im `Dispose` von Events ab.
- NFR8: Build ohne Warnungen (`TreatWarningsAsErrors`), `dotnet format --verify-no-changes` sauber, alle Tests grün.

## Technical Constraints

- **Daten**: bestehender `TriageDbContext` via `IDbContextFactory`. Tabellen `Ticket` (Original + `*Changed`), `Comments` und Lookups. Status: `New`, `HumanRejected`, `HumanApproved`, `Finished`. Keine neuen Spalten.
- **Lookup-Mapping nur über Namen**, nie über Ordinalwerte. Die Reihenfolgen weichen ab:

  | Lookup | DB | Core-Enum |
  |---|---|---|
  | Impact | `Lowest` … `Highest` | `Major` → `Highest`, `Significant` → `High`, `Moderate` → `Medium`, `Minor` → `Low`, `NoImpact` → `Lowest` (wie `TrainingDataImporter.ImpactNameTranslation`) |
  | Priority | `Lowest` = 0 | `Highest` = 0 |

  Die Übersetzung wird im Web dupliziert und muss synchron bleiben.
- **Priority** ist immer `PriorityMatrix.Resolve` (Core). `PriorityMapping` in der DB ist nur ein Spiegel davon.
- **Pipeline**: `ITriagePipeline` ist scoped, der Worker öffnet einen Scope pro Ticket. Heute läuft `StubTriagePipeline` und liefert Platzhalter: Kommentar «TODO: …», Confidence 0, keine Services und Teams.
- **RAM-Store** (Singleton), geht beim Neustart verloren: Issue key, geparstes Ticket, Queue-Zustand, Versuche, DraftComment, Service- und Team-Listen, SimilarTicketKeys, Confidence, Reject-Grund, Zeitstempel und Edit-Zähler.
- **Umbau**: `Tickets.razor` und `Review.razor` werden ersetzt, `Home.razor` wird erweitert, `MainLayout` bekommt die Navigation Dashboard · Upload · Tickets.
- **Seiten bleiben dünn**: DB-Zugriff und Mapping liegen in Web-Services, nicht in `.razor` (Skill `blazor-server`). Die Web-Interfaces sind die Nahtstelle, um später `ITicketIngestor` und `IReviewService` des Teams einzusetzen.
- **Tests**: xUnit v3 + FluentAssertions + bUnit, SQLite in-memory für die DB-Tests.
- **Zeit**: ≤ 4 h, orchestrate fast mit max. 3 Slices.

## Acceptance Criteria

- [ ] AC1: Upload einer gültigen challenge.json (20 Tickets) → Vorschau mit 20 gültigen Zeilen. In der DB ist noch nichts gespeichert.
- [ ] AC2: Nach «Save & analyse» stehen alle 20 in `/tickets`. Die Ampel wechselt ohne Reload von grau über blau zu gelb, mit der Stub-Pipeline sind alle 20 innerhalb von 10 s gelb.
- [ ] AC3: Ungültiges JSON, falsche Wurzel oder eine Datei > 1 MB → Fehlermeldung, nichts gespeichert. Ein Eintrag ohne Summary ist in der Vorschau ungültig und wird nicht gespeichert.
- [ ] AC4: Dieselbe Datei nach einem Neustart erneut hochgeladen → keine doppelten Zeilen. Nicht analysierte Tickets werden neu eingereiht, entschiedene als «already in triage» angezeigt.
- [ ] AC5: Ohne Training-Daten (kein `Finished`-Ticket) warnt der Upload, gespeichert wird nur nach Bestätigung.
- [ ] AC6: Das Review zeigt Original und Vorschlag nebeneinander. Eine Änderung von Urgency oder Impact aktualisiert Priority sofort laut Matrix (z.B. High × Major = Highest).
- [ ] AC7: Accept ohne Edits → grün. In der DB: `HumanApproved`, Original = Vorschlag, `*Changed` = `null`, `PriorityId` = Matrix, Kommentar als `Comments`-Zeile.
- [ ] AC8: Save ist erst nach einem Edit aktiv → grün, und die Edits pro Feld auf dem Dashboard steigen um 1.
- [ ] AC9: Reject ohne Grund ist nicht möglich. Mit Grund → rot. In der DB: `HumanRejected`, Original unverändert, `*Changed` = `null`.
- [ ] AC10: Entscheiden zwei Tabs dasselbe Ticket, bekommt der zweite «Already decided», und nichts wird überschrieben.
- [ ] AC11: Wirft die Pipeline 3-mal einen Fehler → Failed (rot ⚠). Nach «Re-queue» ist das Ticket wieder grau und wird verarbeitet.
- [ ] AC12: Ein Queued-Ticket geöffnet → «Agent is analysing…», dann wechselt die Seite selbst zum Vorschlag. Die Seite ruft die Pipeline nie auf (per Fake-Pipeline mit Aufrufzähler getestet).
- [ ] AC13: Das Dashboard zeigt die Zähler pro Zustand, Approval rate, Acceptance rate, Edits pro Feld und die Ø-Zeiten. Ein Klick auf einen Zähler filtert die Liste.
- [ ] AC14: Theme und Badges nutzen `#2563eb`, `#d97706` und `#7c3aed`. Die Ampel zeigt Farbe, Icon und Text.
- [ ] AC15: Build ohne Warnungen, format sauber, Tests grün. `git diff main --name-only` enthält nur Pfade aus NFR1.

## Edge Cases & Failure Modes

- Ungültiges JSON, Objekt statt Array oder leeres Array → Meldung, nichts gespeichert.
- Datei > 1 MB (z.B. aus Versehen training.json) → wird abgelehnt, bevor sie geparst wird.
- Summary fehlt oder Issue key kommt in der Datei doppelt vor → ungültig, mit Grund in der Vorschau.
- Text zu lang (Summary 250, Description 1000, Resolution 500, Comment 500, Assignee 50 Zeichen) → gekürzt, mit Hinweis in der Vorschau.
- Unbekannte Werte für Work type, Service, Team, Urgency, Impact oder Priority → Spalte bleibt leer (Work type wird wie im Importer `Incident`), mit Hinweis in der Vorschau.
- Upload ohne Training-Daten → Warnung und Bestätigung (FR5).
- Exception oder Timeout in der Pipeline → bis zu 3 Versuche, dann Failed. Kein Teilergebnis in der DB.
- Der Vorschlag enthält einen Service oder ein Team, das es in den Lookups nicht gibt → `*Changed` bleibt `null`, das Feld ist als «not in catalog» markiert, der Analyst wählt.
- Der Vorschlag gleicht dem Original → trotzdem Pending, das Review zeigt «No changes suggested».
- Zwei Analysten entscheiden gleichzeitig → bedingtes Update, der zweite bekommt eine Meldung und einen Reload.
- Neustart der App → der RAM ist leer, damit verschwinden Queue, Analysing und Failed. Pending- und entschiedene Tickets bleiben (DB), Issue key, Entwurf und Confidence zeigen «—». Nicht analysierte Uploads kommen per erneutem Upload oder «Analyse now» zurück.
- Circuit bricht während Upload oder Review ab → nichts ist halb gespeichert (eine Transaktion pro Aktion). Der Worker läuft weiter.
- Ticket-Text mit HTML, Script oder Prompt-Injection → wird nur als Plain Text angezeigt.
- LLM nicht konfiguriert → spielt für den Stub keine Rolle. Später führen LLM-Fehler auf den Failed-Pfad, die Ursache zeigt der Health-Block.

## Out of Scope

- Änderungen an Core, Infrastructure, Agents, Batch und AppHost, neue DB-Spalten, Migrationen. Den Fix des Importer-Checks macht das Team.
- RAM-Daten über Neustarts hinweg persistieren (Issue key, Entwurf, Confidence, Reject-Grund, Zeitstempel).
- Die Qualität von Pipeline und LLM: Die Stubs bleiben, wie sie sind.
- JSON-Export der Entscheide (result.json kommt aus dem Batch, FR-30).
- Auth und Rollen, Identität des Analysten, entschiedene Tickets wieder öffnen oder neu analysieren, parallele Edits zusammenführen.
- Mehrere Services oder Teams persistent speichern (die DB hat nur einen FK, Listen gibt es nur im RAM und in der Anzeige).
- Confidence pro Feld (FR-18 nur als Gesamtwert).
- Business Entity, ResolutionDate oder Ticket-Status von Hand bearbeiten, Dark Mode, deutsche Lokalisierung.

## Open Questions

- **Extras**: Du hast die Auswahl mir überlassen. Meine Wahl: FR-24 mit FR-18 (Anzeige), FR-25 und FR-26 als Could (letzter Slice, streichbar). Den JSON-Export habe ich nicht genommen, weil es dafür keine FR gibt.
- **Upload-Grenzen** 1 MB / 200 Tickets: Annahme. Die challenge.json hat 20 Tickets.
- **Worker**: seriell, 3 Versuche, 60 s Timeout pro Ticket. Annahme aus architecture.md §5.2.1 und requirements §7 Nr. 6.
- **Format der challenge.json** (Array oder Wrapper, Feldnamen) ist nicht verifiziert, weil die Datei nicht im Repo liegt. Der Parser erwartet ein Top-Level-Array, ein Wrapper-Objekt führt zu einer Fehlermeldung. Anpassen, sobald die Datei da ist.
- **Bedeutung der Spalte `Resolution`**: Annahme: `ResolutionChanged` enthält das Status-Vokabular (Done, Cancelled, Clarification, Cannot Reproduce), der Kommentar wird beim Approve zur `Comments`-Zeile. Das bisherige `Review.razor` behandelt `Resolution` als Freitext. Mit dem DB-Owner klären.
- **Abgleich mit dem Team**: architecture.md sieht `TriageSuggestion`-Persistenz, `IReviewService`, `rowVersion` und Analysing/Failed in der DB vor. Dieses Feature baut bewusst auf dem heutigen Schema (`*Changed`) auf. Die Web-Interfaces sind die Nahtstelle für den späteren Wechsel.
- **Import-Falle**: `TrainingDataImporter` überspringt den Import, sobald irgendein Ticket existiert. Der Fix liegt beim Team, z.B. nur auf `Finished`-Tickets prüfen.
