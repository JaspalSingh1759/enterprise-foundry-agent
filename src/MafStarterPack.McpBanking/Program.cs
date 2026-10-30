using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using Azure.Monitor.OpenTelemetry.AspNetCore;

using MafStarterPack.McpBanking;

using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Maple Bank's synthetic data and dispute rules ship with the app (see the .csproj) and are read once.
builder.Services.AddSingleton(new BankData(Path.Combine(AppContext.BaseDirectory, "data")));
builder.Services.AddSingleton(new DisputeRules(Path.Combine(AppContext.BaseDirectory, "rules", "dispute-rules.json")));
builder.Services.AddSingleton<CaseStore>();
builder.Services.AddSingleton<AttachmentReader>();

// The Docker build context is this folder only, so wire OpenTelemetry here instead of via ServiceDefaults.
var otel = builder.Services.AddOpenTelemetry()
                           .WithTracing(tracing => tracing.AddSource("Experimental.ModelContextProtocol", "ModelContextProtocol"));
if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_SERVICE_NAME"]))
{
    otel.ConfigureResource(resource => resource.AddService("mcp-banking"));
}
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    // Keep every trace, as in mcp-todo: the default rate-limited sampler drops whole agent turns in bursts.
    otel.UseAzureMonitor(o =>
    {
        o.TracesPerSecond = null;
        o.SamplingRatio = 1.0F;
    });
}

builder.Services.AddMcpServer()
                .WithHttpTransport(o => o.Stateless = true)
                .WithToolsFromAssembly(Assembly.GetEntryAssembly());

var app = builder.Build();

if (app.Environment.IsDevelopment() == false)
{
    app.UseHttpsRedirection();
}

// Callers must send the API key from Key Vault (Mcp__ApiKey) in the x-api-key header.
// Outside Development a missing key fails closed: every /mcp request is rejected.
var apiKey = app.Configuration["Mcp:ApiKey"];
if (string.IsNullOrEmpty(apiKey))
{
    app.Logger.LogWarning("Mcp:ApiKey is not set. {Behaviour}", app.Environment.IsDevelopment()
        ? "Development: /mcp accepts requests without a key."
        : "All /mcp requests will be rejected.");
}
var apiKeyBytes = Encoding.UTF8.GetBytes(apiKey ?? string.Empty);

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp"))
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            if (app.Environment.IsDevelopment() == false)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
        }
        else
        {
            var presented = Encoding.UTF8.GetBytes(context.Request.Headers["x-api-key"].ToString());
            if (CryptographicOperations.FixedTimeEquals(presented, apiKeyBytes) == false)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
    }

    await next(context);
});

app.MapMcp("/mcp");

await app.RunAsync();
