---
name: blazor-server
description: Blazor Web App with global Interactive Server render mode on .NET 10, with MudBlazor, as used in TicketTriage.Web — component lifecycle and prerendering, circuits and DI lifetimes, DbContext usage, streaming AI output, JS interop, forms/validation, .NET 10 features (PersistentState, ReconnectModal, NotFound), and bUnit testing. Use whenever .razor components, pages, layouts or Web Program.cs are written or reviewed.
---

# Blazor Server (.NET 10) — TicketTriage.Web

Setup in this repo: `App.razor` sets `<Routes @rendermode="InteractiveServer" />` and `<HeadOutlet @rendermode="InteractiveServer" />` → **every page is interactive over a SignalR circuit**. Don't add per-component `@rendermode`. UI library: **MudBlazor**.

## Structure

```
Components/
  App.razor · Routes.razor · _Imports.razor
  Layout/        MainLayout (MudBlazor providers), ReconnectModal (.NET 10 template)
  Pages/         routable pages: Home, Tickets, Review, Error, NotFound
  Shared/        reusable child components (create when needed)
```

- Pages stay thin: inject Core ports (`ITriagePipeline`, …) or small query services, render state. No EF queries, prompts or business rules in `.razor`.
- Code-behind (`Page.razor.cs`, `partial class`) once `@code` exceeds ~30 lines.
- Human-in-the-loop review (`Review.razor`) persists `ReviewDecision` (Approved/Edited/Rejected) — the analyst's decision is the source of truth, not the suggestion.

## MudBlazor setup (once)

```csharp
builder.Services.AddMudServices();
```

`App.razor`: MudBlazor CSS/JS (`_content/MudBlazor/MudBlazor.min.css`, `MudBlazor.min.js`) and in `MainLayout.razor`: `<MudThemeProvider/> <MudPopoverProvider/> <MudDialogProvider/> <MudSnackbarProvider/>`. Prefer `MudDataGrid` (server-side paging via `ServerData`) for ticket lists — never load 20k rows into the circuit.

## Lifecycle & prerendering (the #1 source of bugs)

Prerendering is on by default: `OnInitialized{Async}` runs **twice** — once on the static prerender, once when the circuit connects.

- Never call an LLM, a long query, or anything with side effects in `OnInitializedAsync` unguarded.
- Options: (a) load in `OnAfterRenderAsync(firstRender)` and show a skeleton; (b) persist prerendered state with .NET 10's declarative attribute:

```csharp
[PersistentState]
public TicketSummary[]? Tickets { get; set; }

protected override async Task OnInitializedAsync()
    => Tickets ??= await queries.GetSummariesAsync(ct);
```

(c) disable prerendering for a heavy page: `@rendermode @(new InteractiveServerRenderMode(prerender: false))` — only if (a)/(b) don't fit.
- JS interop is unavailable during prerender → only in `OnAfterRenderAsync`.

## Threading & state

- Event handlers already run on the renderer's sync context → `StateHasChanged()` is fine there.
- Callbacks from timers, background services, `IProgress`, events from singletons → `await InvokeAsync(StateHasChanged)`.
- Every component that starts async work owns a `CancellationTokenSource`, cancels it in `Dispose`/`DisposeAsync` (`@implements IAsyncDisposable`). Circuits disconnect all the time.
- Unhandled exceptions kill the circuit → wrap risky UI areas in `<ErrorBoundary>` and show a MudAlert.

## DI lifetimes in Blazor Server

| Lifetime | Means here |
|---|---|
| Singleton | shared by **all users** — never per-user state (sessions, selections) |
| Scoped | **per circuit** (per browser tab), lives for minutes/hours |
| Transient | new per injection |

- **DbContext**: never inject it into components (a scoped DbContext lives as long as the circuit → stale data, concurrency exceptions). Use `IDbContextFactory<TriageDbContext>`:

```csharp
@inject IDbContextFactory<TriageDbContext> DbFactory
...
await using var db = await DbFactory.CreateDbContextAsync(ct);
```

- `AgentSession` for a chat UI: a field in the component (or a scoped service), never a singleton.
- `HttpContext` is not available in interactive components — don't use `IHttpContextAccessor` there.

## Streaming AI output

See the `agent-framework` skill for the loop. UI rules: show a MudProgressLinear while streaming, disable the submit button, offer a Cancel button bound to the CTS, throttle re-renders if updates are tiny (e.g. every 50 ms).

**Never** render model output as `(MarkupString)` unsanitised (XSS). Plain text, or markdown via Markdig with HTML disabled (`.DisableHtml()`), optionally plus HtmlSanitizer.

## Forms

`<EditForm Model="_model" OnValidSubmit="SaveAsync">` + `<DataAnnotationsValidator />` (or MudForm + validation). Keep the form model a separate class from the entity. Validation rules that are *business* rules belong in Core as well — UI validation is convenience only.

## .NET 10 features worth using

- `[PersistentState]` — declarative prerender state (above).
- `ReconnectModal` component (already in `Layout/`) — customise text/CSS there.
- `NavigationManager.NotFound()` + `Router NotFoundPage` (already wired, plus `BlazorDisableThrowNavigationException=true` in the csproj → `NavigateTo` during static render doesn't throw).
- `MapStaticAssets()` + `@Assets["..."]` fingerprinting (already in `App.razor`).
- QuickGrid `RowClass`, improved JS interop (`InvokeConstructorAsync`, `GetValueAsync`) if needed.

## Configuration

- Circuit options for dev debugging: `AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment())`.
- Long LLM calls: they don't block the circuit if awaited properly, but increase SignalR `MaximumReceiveMessageSize` only if large payloads really come *from* the client.

## Testing with bUnit 2

- Base class `BunitContext` (v1's `TestContext` is gone), `Render<T>(p => p.Add(x => x.Param, value))`.
- MudBlazor: `Services.AddMudServices(); JSInterop.Mode = JSRuntimeMode.Loose;`.
- Replace `IDbContextFactory` / agent interfaces with fakes; async data → `cut.WaitForAssertion(() => ...)`.
- Test states (loading/empty/data/error) and interactions, not business logic.

See `test-writer` agent for the full pattern.
