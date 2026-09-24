---
name: sqlite-efcore
description: SQLite with EF Core 10 in TicketTriage — TriageDbContext in TicketTriage.Infrastructure (TrainingTickets, TriageSuggestions), IDbContextFactory for Blazor Server, migrations workflow with the local dotnet-ef tool, Aspire wiring of the triage-db resource, the training.json import, SQLite type limitations (DateTimeOffset!), full-text search (FTS5) for similar-ticket retrieval, and testing with in-memory SQLite. Use whenever entities, DbContext, migrations, queries, importers or database configuration are touched.
---

# SQLite + EF Core 10 — TicketTriage

Packages: `Microsoft.EntityFrameworkCore.Sqlite` + `.Design` (Infrastructure), `CommunityToolkit.Aspire.Hosting.Sqlite` (AppHost), `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` (Web). `dotnet-ef` is a **local tool** (`dotnet-tools.json`) → run `dotnet tool restore` once.

## What exists

```
TicketTriage.Infrastructure/
  InfrastructureServiceCollectionExtensions.cs  AddTriageInfrastructure(config); ConnectionStringName = "triage-db"
  DatabaseInitializer.cs                        InitializeTriageDatabaseAsync(): MigrateAsync + training import (Development, called from Web)
  Import/TrainingDataImporter.cs                idempotent import of training.json (skips if TrainingTickets has rows)
  Persistence/TriageDbContext.cs                TrainingTickets (PK string Key), TriageSuggestions (Decision as string)
  Persistence/TrainingTicketEntity.cs           persistence model + FromTicket()/ToTicket() mapping to Core.Ticket
  Persistence/DesignTimeDbContextFactory.cs     for `dotnet ef` (uses design-time.db)
  Persistence/Migrations/                       generated — never hand-edit (protect-files hook blocks Designer/Snapshot)
```

Core stays persistence-ignorant: entities in Infrastructure map to/from Core records. Lists (`AffectedServices`, `ServiceTeams`, `Comments`) are EF primitive collections → JSON text columns.

## Registration (already done — keep the pattern)

```csharp
services.AddDbContextFactory<TriageDbContext>(options => options.UseSqlite(connectionString));
```

- The factory is required for Blazor Server (short-lived contexts per operation). It also registers a scoped `TriageDbContext` for Batch / initializer code.
- In components/services used by components: `await using var db = await dbFactory.CreateDbContextAsync(ct);`.
- `"triage-db"` must match the AppHost resource name (`builder.AddSqlite("triage-db", ...)`), which injects `ConnectionStrings__triage-db`. Standalone Development fallback: `appsettings.Development.json` → `Data Source=triage.db`.

## Migrations

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/TicketTriage.Infrastructure --startup-project src/TicketTriage.Web --output-dir Persistence/Migrations
dotnet ef migrations list        --project src/TicketTriage.Infrastructure --startup-project src/TicketTriage.Web
dotnet ef migrations remove      --project src/TicketTriage.Infrastructure --startup-project src/TicketTriage.Web   # only if not pushed yet
```

- Migrations are applied on Web startup in Development (`InitializeTriageDatabaseAsync`). Don't add `EnsureCreated()` anywhere.
- Parallel migrations by two people → snapshot conflict. Rule: pull, `migrations remove` yours, `migrations add` again on top. Never hand-merge the snapshot.
- SQLite can't `ALTER` most things; EF rebuilds the table — review generated migrations for data loss.
- Reset locally: stop the app, delete `triage.db*`, restart (migrations + import rerun).

## SQLite limitations that bite

- **`DateTimeOffset`**: EF Core can't translate `OrderBy`/`<`/`>` on it for SQLite → runtime exception. The existing columns are already stored as sortable 64-bit integers via a value converter (see `TriageDbContext`) — apply the same converter to every new `DateTimeOffset` property.
- **`decimal`**: same limitation → use `double` or a converter.
- **Single writer**: keep write transactions short; consider WAL (`PRAGMA journal_mode=WAL;`) and `Default Timeout=30` in the connection string if Batch and Web write at the same time.
- `Like` is case-insensitive only for ASCII; use `.UseCollation("NOCASE")` on columns that need it.
- Enums: store as string (`HasConversion<string>()`) like `TriageSuggestions.Decision` — readable and robust against reordering. Note Core enum *JSON* names (`"Service Request"`, `"No Impact"`) differ from C# names.

## Import performance (~20k tickets)

The current importer deserializes the whole file and saves once — fine at this size. If it gets slow or memory-heavy: stream with `JsonSerializer.DeserializeAsyncEnumerable<Ticket>(...)`, `ChangeTracker.AutoDetectChangesEnabled = false`, `AddRange` + `SaveChangesAsync` + `ChangeTracker.Clear()` per 1,000 rows. Keep it idempotent.

## Similar-ticket retrieval (replacing `StubSimilarTicketRetriever`)

The requirements ask for **embeddings + kNN by cosine similarity** (FR-03, FR-11). Embeddings go through `IEmbeddingGenerator<string, Embedding<float>>` (M.E.AI), are cached in SQLite (e.g. `float[]` as a BLOB) and are compared in memory (20k vectors fits easily). The design is the team's call; FTS5 below is an optional keyword/hybrid add-on or fallback.

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
