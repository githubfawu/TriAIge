---
name: dotnet10
description: Modern .NET 10 / C# 14 conventions for TicketTriage — solution setup (slnx, Central Package Management, TreatWarningsAsErrors, editorconfig), C# 14 features (extension members, field keyword, null-conditional assignment), async/cancellation, DI and options patterns, logging, JSON, records, xUnit v3 testing, and dotnet CLI commands. Use for general C# code in any TicketTriage project, when adding packages or projects, or when fixing compiler/analyzer errors.
---

# .NET 10 / C# 14 — TicketTriage conventions

## Solution setup (already in place — keep it that way)

| File | Rule |
|---|---|
| `TicketTriage.slnx` | XML solution format (default in .NET 10). Add projects with `dotnet sln TicketTriage.slnx add <path>`. |
| `Directory.Build.props` | `net10.0`, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors`, `AnalysisLevel latest`. Don't repeat these in `.csproj` files. |
| `Directory.Packages.props` | **Central Package Management** — every version lives here. `.csproj`: `<PackageReference Include="X" />` without `Version`. Transitive pinning is on. |
| `.editorconfig` | file-scoped namespaces, `_camelCase` private fields, LF line endings, 4 spaces. The `format-csharp` hook applies whitespace rules after each edit. |

Adding a package: add `<PackageVersion>` in `Directory.Packages.props` (right `Label` group), then `<PackageReference>` in the project. `dotnet add package` also works with CPM but double-check where it wrote the version.

New project checklist: create under `src/` or `tests/`, remove any `TargetFramework`/`Nullable` duplicates, add to `TicketTriage.slnx`, respect the dependency direction `Core ← Infrastructure ← Agents ← {Web, Batch}`.

## C# 14 features to use

```csharp
// Extension members (static + instance extension properties/methods in one block)
public static class TicketExtensions
{
    extension(Ticket ticket)
    {
        public bool IsUrgent => ticket.Priority is Priority.P1 or Priority.P2;
    }
}

// field keyword — validation without a manual backing field
public string Subject
{
    get;
    set => field = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Subject required") : value.Trim();
}

// null-conditional assignment
ticket?.Tags = [..tags];
```

Also: `nameof(List<>)` with unbound generics, implicit span conversions, lambda parameter modifiers without types (`(ref x) => ...`), partial constructors/events.

Keep using: primary constructors for DI, collection expressions `[]`, `required` members, `record` / `record struct` for DTOs & value objects, pattern matching, raw string literals `"""` for prompts/JSON.

## Async & cancellation

- Every I/O method: `async Task`/`ValueTask`, suffix `Async`, last parameter `CancellationToken cancellationToken` (no default in library code).
- Never `.Result`, `.Wait()`, `async void` (except UI event handlers), or `Task.Run` to fake async I/O.
- `ConfigureAwait(false)` not required (CA2007 disabled); no sync context issues outside Blazor components.
- Bounded parallelism: `Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, ...)`.
- `TimeProvider` instead of `DateTime.UtcNow` in logic you want to test.

## DI, options, configuration

- Each project exposes one `Add<Project>(this IServiceCollection, IConfiguration)` extension; Web/Batch compose them.
- Options pattern bound to a section constant (see `LlmOptions.SectionName`, `TrainingDataOptions.SectionName`):

```csharp
services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
// add .ValidateDataAnnotations().ValidateOnStart() via AddOptions<T>() only where missing config
// should fail startup — LLM config deliberately does NOT (health check reports it instead)
```

- `TryAdd*` for defaults/stubs, plain `Add*` for real implementations registered later (last registration wins for single resolves).

- Keyed services (`AddKeyedSingleton<IChatClient>("fast", ...)`) when two models are needed side by side.

## Logging

- Source-generated logging for hot paths:

```csharp
public static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Triaged ticket {TicketId} as {Category} in {ElapsedMs} ms")]
    public static partial void Triaged(ILogger logger, string ticketId, string category, long elapsedMs);
}
```

- Structured placeholders, never string interpolation in log calls. Never log full ticket bodies / prompts at Information (PII).

## JSON

- `System.Text.Json` only. `JsonSerializerOptions.Web` (camelCase, case-insensitive) as default.
- Stream large files: `JsonSerializer.DeserializeAsyncEnumerable<T>(stream, options, ct)`.
- `result.json` must match the organizers' contract exactly — model it as explicit records with `[JsonPropertyName]` so renames don't break it.
- Source-generated `JsonSerializerContext` is optional; worth it only for hot paths.

## Errors

- Domain errors: specific exception types in Core (`DomainException`) or a `Result<T>` — pick one and stay consistent.
- Don't catch `Exception` except at boundaries (batch per-ticket loop, UI `ErrorBoundary`, background services) — and log it there.
- `ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrWhiteSpace` for guard clauses.

## Testing (xUnit v3 + FluentAssertions)

- Test projects are executables (`OutputType Exe`, Microsoft Testing Platform-capable) — `dotnet test` works as usual.
- `TestContext.Current.CancellationToken` for async tests.
- `[Theory]` + `[InlineData]` / `[MemberData]` for label-mapping tables.
- Traits: `[Trait("Category", "Smoke")]`, `[Trait("Category", "Integration")]` → filter with `--filter "Category!=Integration"`.
- FluentAssertions: `result.Should().BeEquivalentTo(expected)`, `await act.Should().ThrowAsync<T>()`.

## CLI cheat sheet

```bash
dotnet build TicketTriage.slnx
dotnet test --solution TicketTriage.slnx --filter "FullyQualifiedName~Triage"
dotnet format TicketTriage.slnx                 # apply style
dotnet list TicketTriage.slnx package --outdated
dotnet list TicketTriage.slnx package --vulnerable
dotnet run app.cs                               # .NET 10 file-based apps — handy for quick data exploration scripts
```

File-based apps (`#:package Foo@1.2.3` at the top of a single `.cs` file) are great for throwaway analysis of `training.json` — keep them out of the solution (e.g. `scripts/`).
