---
name: sqlite-efcore
description: SQLite with EF Core 10 in TicketTriage — TriageDbContext in TicketTriage.Infrastructure (normalized Ticket + lookup tables, Comments, PriorityMapping), IDbContextFactory for Blazor Server, EnsureCreated-on-startup (no migrations), Aspire wiring of the triage-db resource, the training.json import, SQLite type limitations (DateTimeOffset!), full-text search (FTS5) for similar-ticket retrieval, and testing with in-memory SQLite. Use whenever entities, DbContext, queries, importers or database configuration are touched.
---

# SQLite + EF Core 10 — TicketTriage

Packages: `Microsoft.EntityFrameworkCore.Sqlite` + `.Design` (Infrastructure), `CommunityToolkit.Aspire.Hosting.Sqlite` (AppHost), `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` (Web). `dotnet-ef` is a **local tool** (`dotnet-tools.json`) → run `dotnet tool restore` once.

## What exists

```
TicketTriage.Infrastructure/
  InfrastructureServiceCollectionExtensions.cs  AddTriageInfrastructure(config); ConnectionStringName = "triage-db"
  DatabaseInitializer.cs                        InitializeTriageDatabaseAsync(): EnsureCreatedAsync + training import (Development, called from Web)
  Import/TrainingDataImporter.cs                idempotent import of training.json (skips if Tickets has rows), resolves raw strings to lookup FK ids
  Persistence/TriageDbContext.cs                Ticket, Comments, PriorityMapping + 8 lookup tables (WorkType, Priority, Urgency, Impact, ServiceTeams,
                                                 AffectedBusinessOrITServices, BusinessEntity, Status), all seeded via HasData
  Persistence/TicketEntity.cs                   Ticket: each classification field has an original FK + a nullable "*ChangedId"/"*Changed" column
                                                 holding the AI's pending re-classification (cleared once an analyst approves/edits/rejects it)
  Persistence/Lookups.cs                        ILookupEntity + the 8 plain Id/Name lookup entities (independent of Core.Domain enums — different
                                                 ordinals/labels on purpose, e.g. Impact here is Lowest..Highest, not Major..NoImpact)
  Persistence/CommentEntity.cs                  Comments, FK TicketId (one-to-many on Ticket)
  Persistence/PriorityMappingEntity.cs          plain UrgencyId x ImpactId -> PriorityId rows, mirrors Core.Domain.PriorityMatrix (kept in sync manually)
  Persistence/DesignTimeDbContextFactory.cs     kept for ad-hoc `dotnet ef` inspection, not used for migrations (see below)
```

Core stays persistence-ignorant: `TrainingDataImporter` maps `Core.Domain.Ticket` (raw JSON strings) onto the FK lookup ids. Since the new `Impact` lookup uses different labels than the Core `Impact` enum, the importer translates by severity rank (`Major`→`Highest`, … , `No Impact`→`Lowest`).

## Registration (already done — keep the pattern)

```csharp
services.AddDbContextFactory<TriageDbContext>(options => options.UseSqlite(connectionString));
```

- The factory is required for Blazor Server (short-lived contexts per operation). It also registers a scoped `TriageDbContext` for Batch / initializer code.
- In components/services used by components: `await using var db = await dbFactory.CreateDbContextAsync(ct);`.
- `"triage-db"` must match the AppHost resource name (`builder.AddSqlite("triage-db", ...)`), which injects `ConnectionStrings__triage-db`. Standalone Development fallback: `appsettings.Development.json` → `Data Source=triage.db`.

## Schema creation (no migrations)

- This project intentionally uses `Database.EnsureCreatedAsync()` on Web startup (`InitializeTriageDatabaseAsync`, Development only), **not** EF migrations — a deliberate deviation from the earlier migrations-based setup, chosen so lookup-table seed data (`HasData`) and the schema are always in sync with the current model.
- Consequence: `EnsureCreated` does **not** support incremental schema changes. To change the model, delete the local `data/triage.db*` and restart (Development re-creates + re-seeds + re-imports). There is no upgrade path for a database that already has data — that's an accepted trade-off here, not a bug.
- Schema additions of the triage pipeline: `TicketEntity.Retries` (int, default 0) and table `TriageFailure` (`TriageFailureEntity`, no FK on `TicketId`). Written by `EfTriageFailureStore` with `ExecuteUpdateAsync` in one short transaction; delete `data/triage.db*` once after pulling them.
- Lookup entity ids are fixed business values starting at 0, so their `Id` property needs `ValueGeneratedNever()` in `OnModelCreating` (`HasData` rejects `0`/default as an auto-generated key).
- If migrations are ever reintroduced, remove `EnsureCreatedAsync()`, add back `dotnet ef migrations add ... --output-dir Persistence/Migrations`, and switch `InitializeTriageDatabaseAsync` to `MigrateAsync`.

## SQLite limitations that bite

- **`DateTimeOffset`**: EF Core can't translate `OrderBy`/`<`/`>` on it for SQLite → runtime exception. The existing columns are already stored as sortable 64-bit integers via a value converter (see `TriageDbContext`) — apply the same converter to every new `DateTimeOffset` property.
- **`decimal`**: same limitation → use `double` or a converter.
- **Single writer**: keep write transactions short; consider WAL (`PRAGMA journal_mode=WAL;`) and `Default Timeout=30` in the connection string if Batch and Web write at the same time.
- `Like` is case-insensitive only for ASCII; use `.UseCollation("NOCASE")` on columns that need it.
- Enums: prefer `HasConversion<string>()` when a property is stored as a genuine C# enum — readable and robust against reordering. `Ticket`'s classification fields use plain lookup-table FKs (int ids) instead, not enum conversions. Note Core enum *JSON* names (`"Service Request"`, `"No Impact"`) differ from C# names.

## Import performance (~20k tickets)

The current importer deserializes the whole file and saves once — fine at this size. If it gets slow or memory-heavy: stream with `JsonSerializer.DeserializeAsyncEnumerable<Ticket>(...)`, `ChangeTracker.AutoDetectChangesEnabled = false`, `AddRange` + `SaveChangesAsync` + `ChangeTracker.Clear()` per 1,000 rows. Keep it idempotent.

## Similar-ticket retrieval (port `ISimilarTicketSource`)

Implemented as `DbSimilarTicketSource` in `Infrastructure/Retrieval`: **in-memory TF-IDF + cosine kNN over `Description`**, no schema change and no model call (see `docs/features/similar-ticket-retrieval/README.md`). `SimilarTicketIndexProvider` (singleton) builds the index once per process from SQLite; a cancelled or failed build is not cached. The Jira key is not stored, so keys are `DB-{Id}`.

The rest of this section describes **future options** if quality is not enough: **embeddings** through `IEmbeddingGenerator<string, Embedding<float>>` (M.E.AI), cached in SQLite (e.g. `float[]` as a BLOB) and compared in memory (FR-03 asks for them), and optional FTS5 as a keyword/hybrid add-on. Both would need a schema change.

Optional **SQLite FTS5** over summary + description: EF tables with a string PK still have an implicit `rowid`:

```csharp
// in a migration (Up); Down: DROP TABLE TrainingTicketsFts
migrationBuilder.Sql("CREATE VIRTUAL TABLE TrainingTicketsFts USING fts5(Summary, Description, content='TrainingTickets');");
migrationBuilder.Sql("INSERT INTO TrainingTicketsFts(TrainingTicketsFts) VALUES('rebuild');");
```

The import runs after migrations → run `'rebuild'` again at the end of the import (or add triggers) so the index isn't empty.

```csharp
var rows = await db.Database
    .SqlQuery<SimilarRow>($"""
        SELECT t.Key, bm25(TrainingTicketsFts) AS Rank
        FROM TrainingTicketsFts f JOIN TrainingTickets t ON t.rowid = f.rowid
        WHERE TrainingTicketsFts MATCH {ftsQuery}
        ORDER BY Rank LIMIT {top}
        """)
    .ToListAsync(ct);
```

- Always `SqlQuery($"...")` / `FromSql($"...")` (parameterised). Never `FromSqlRaw` with concatenated ticket text.
- Build `ftsQuery` from sanitised tokens (quote each term, join with `OR`) — raw user text causes FTS syntax errors.
- bm25 is lower = better; convert to the `SimilarTicket.Score` convention (higher = more similar).
- Embeddings/vector search only if FTS accuracy isn't good enough — measure first.

## Query rules

- Read-only → `AsNoTracking()` and `Select` into what you need.
- No N+1, no queries inside loops; page large lists server-side (`Skip/Take`, MudDataGrid `ServerData`).
- Return materialised collections, never `IQueryable`, across layers.
- Always pass `CancellationToken`.

## Testing

```csharp
await using var connection = new SqliteConnection("DataSource=:memory:");
await connection.OpenAsync(ct);                       // db lives as long as the connection
var options = new DbContextOptionsBuilder<TriageDbContext>().UseSqlite(connection).Options;
await using (var db = new TriageDbContext(options)) await db.Database.EnsureCreatedAsync(ct);
```

Real SQLite semantics (catches the `DateTimeOffset` problem; the EF InMemory provider wouldn't). For code using `IDbContextFactory`, a tiny test factory returning `new TriageDbContext(options)` is enough. Raw-SQL/FTS features need `MigrateAsync` instead of `EnsureCreated`.
