using System.Reflection;

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
    otel.UseAzureMonitor();
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

app.MapMcp("/mcp");

await app.RunAsync();
