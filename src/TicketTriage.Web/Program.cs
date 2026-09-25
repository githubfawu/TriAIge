using MudBlazor.Services;
using TicketTriage.Agents;
using TicketTriage.Infrastructure;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Web.Analysis;
using TicketTriage.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddTriageInfrastructure(builder.Configuration);
builder.Services.AddTriageAgents(builder.Configuration);
builder.Services.AddHostedService<AnalysisWorker>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<TriageDbContext>("sqlite", tags: [Extensions.ReadyTag])
    .AddAgentFrameworkCheck();

builder.Services.AddMudServices();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Schema + WAL in every environment (Batch may start concurrently); the training import stays Development-only as before.
if (app.Environment.IsDevelopment())
{
    await app.Services.InitializeTriageDatabaseAsync(app.Lifetime.ApplicationStopping);
}
else
{
    await app.Services.EnsureTriageDatabaseCreatedAsync(app.Lifetime.ApplicationStopping);
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

await app.RunAsync();
