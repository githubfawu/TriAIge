using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TicketTriage.Agents;
using TicketTriage.Batch;
using TicketTriage.Infrastructure;

// Usage: TicketTriage.Batch --input <challenge.json> --output <result.json>
var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddCommandLine(args, new Dictionary<string, string>
{
    ["--input"] = $"{BatchOptions.SectionName}:{nameof(BatchOptions.Input)}",
    ["--output"] = $"{BatchOptions.SectionName}:{nameof(BatchOptions.Output)}",
});

builder.AddServiceDefaults();

builder.Services.AddTriageInfrastructure(builder.Configuration);
builder.Services.AddTriageAgents(builder.Configuration);

builder.Services.AddOptions<BatchOptions>()
    .Bind(builder.Configuration.GetSection(BatchOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Input), "Missing --input <path>.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Output), "Missing --output <path>.");
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddTransient<BatchRunner>();

using var host = builder.Build();
await host.StartAsync();

var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
int exitCode;
try
{
    var isDevelopment = host.Services.GetRequiredService<IHostEnvironment>().IsDevelopment();

    // The pipeline and its ports are scoped, so the runner cannot be resolved from the root provider.
    await using var scope = host.Services.CreateAsyncScope();
    exitCode = await BatchCommand.ExecuteAsync(
        scope.ServiceProvider.GetRequiredService<BatchRunner>(),
        Console.Out,
        Console.Error,
        stopping,
        isDevelopment ? ct => host.Services.InitializeTriageDatabaseAsync(ct) : null);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // Startup work outside the command (database initialisation); the type name only, no ticket data.
    await Console.Error.WriteLineAsync($"Startup failed ({ex.GetType().Name}); no output written.");
    exitCode = 1;
}
catch (OperationCanceledException)
{
    await Console.Error.WriteLineAsync("Startup was cancelled; no output written.");
    exitCode = 1;
}

await host.StopAsync();
return exitCode;
