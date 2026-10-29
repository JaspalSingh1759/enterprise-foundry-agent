using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using Azure.Monitor.OpenTelemetry.AspNetCore;

using MafStarterPack.McpTodo;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var connection = new SqliteConnection("Filename=:memory:");
connection.Open();

builder.Services.AddSingleton(connection);

builder.Services.AddDbContext<TodoDbContext>(options => options.UseSqlite(connection));

builder.Services.AddScoped<ITodoRepository, TodoRepository>();

// The Docker build context is this folder only, so wire OpenTelemetry here instead of via ServiceDefaults.
var otel = builder.Services.AddOpenTelemetry()
                           .WithTracing(tracing => tracing.AddSource("Experimental.ModelContextProtocol", "ModelContextProtocol"));
if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_SERVICE_NAME"]))
{
    otel.ConfigureResource(resource => resource.AddService("mcp-todo"));
}
if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    // Keep every trace. The default rate-limited sampler (a few traces per second) dropped whole agent
    // turns during evaluation bursts, including the bulk delete the quality queries exist to catch
    // (docs/kql/agent-quality.kql). Traffic here is small; revisit if volume grows.
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

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
    dbContext.Database.EnsureCreated();
}

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
