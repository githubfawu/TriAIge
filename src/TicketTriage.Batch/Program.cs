using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
builder.Services.AddTransient<BatchRunner>();

using var host = builder.Build();
await host.StartAsync();

var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
int exitCode;
try
{
    if (host.Services.GetRequiredService<IHostEnvironment>().IsDevelopment())
    {
        await host.Services.InitializeTriageDatabaseAsync(stopping);
    }

    await host.Services.GetRequiredService<BatchRunner>().RunAsync(stopping);
    exitCode = 0;
}
catch (OptionsValidationException ex)
{
    await Console.Error.WriteLineAsync(string.Join(Environment.NewLine, ex.Failures));
    await Console.Error.WriteLineAsync("Usage: TicketTriage.Batch --input <challenge.json> --output <result.json>");
    exitCode = 2;
}

await host.StopAsync();
return exitCode;
