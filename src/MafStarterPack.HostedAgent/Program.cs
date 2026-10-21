using Azure.AI.Projects;
using Azure.Identity;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;

var builder = AgentHost.CreateBuilder(args);

Console.WriteLine("Starting Hosted Agent...");

var config = builder.Configuration;

var endpoint = config["FOUNDRY_PROJECT_ENDPOINT"] ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT environment variable is not set.");
if (endpoint.Contains(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
{
    endpoint = endpoint.Replace(".services.ai.azure.com", ".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase);
}
var deploymentName = config["AZURE_AI_MODEL_DEPLOYMENT_NAME"] ?? throw new InvalidOperationException("AZURE_AI_MODEL_DEPLOYMENT_NAME environment variable is not set.");
var tenantId = config["AZURE_TENANT_ID"] ?? throw new InvalidOperationException("AZURE_TENANT_ID environment variable is not set.");
var mcpTodoFqdn = config["AZURE_RESOURCE_MCP_TODO_FQDN"] ?? throw new InvalidOperationException("AZURE_RESOURCE_MCP_TODO_FQDN is not configured");

Console.WriteLine($"Using Azure OpenAI Endpoint: {endpoint}");
Console.WriteLine($"Using Azure OpenAI Deployment Name: {deploymentName}");
Console.WriteLine($"Using Azure Tenant ID: {tenantId}");
Console.WriteLine($"Using MCP Todo FQDN: {mcpTodoFqdn}");

AITool serverTool = new HostedMcpServerTool(
    serverName: "mcp-todo",
    serverAddress: $"https://{mcpTodoFqdn.TrimEnd('/')}/mcp")
{
    ApprovalMode = HostedMcpServerToolApprovalMode.NeverRequire
};

var credentialOptions = new DefaultAzureCredentialOptions() { TenantId = tenantId };
var clientId = config["AZURE_CLIENT_ID"];
if (!string.IsNullOrWhiteSpace(clientId))
{
    Console.WriteLine($"Using ManagedIdentityClientId: {clientId}");
    credentialOptions.ManagedIdentityClientId = clientId;
}
var credential = new DefaultAzureCredential(credentialOptions);
AIAgent agent = new AIProjectClient(new Uri(endpoint), credential)
                    .AsAIAgent(
                        model: deploymentName,
                        instructions: """
                            You are a helpful assistant for managing a todo list.
                            You can add, remove, and list tasks in the todo list.
                            """,
                        name: "todo-agent",
                        description: "A hosted agent that manages to-do list items.",
                        tools: [ serverTool ]);

builder.Services.AddFoundryResponses(agent);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
await app.RunAsync();
