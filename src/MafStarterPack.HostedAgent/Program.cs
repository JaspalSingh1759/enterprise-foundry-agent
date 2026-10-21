using Azure.AI.OpenAI;
using Azure.Identity;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;

var builder = AgentHost.CreateBuilder(args);

Console.WriteLine("Starting Hosted Agent...");

var config = builder.Configuration;

var endpoint = config["FOUNDRY_PROJECT_ENDPOINT"] 
            ?? config["AZURE_AI_PROJECT_ENDPOINT"]
            ?? "https://ai-account-6a3q2sputpvay.cognitiveservices.azure.com/";
if (endpoint.Contains(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
{
    endpoint = endpoint.Replace(".services.ai.azure.com", ".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase);
}
var deploymentName = config["AZURE_AI_MODEL_DEPLOYMENT_NAME"] ?? "gpt-5-mini";
var tenantId = config["AZURE_TENANT_ID"] ?? "a1460d0d-1371-4bcb-9d86-b3f2c535e319";
var mcpTodoFqdn = config["AZURE_RESOURCE_MCP_TODO_FQDN"] ?? "mcp-todo.bluepond-0c1b28fa.eastus2.azurecontainerapps.io";

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

// Use the base authority endpoint for AzureOpenAIClient (e.g. https://ai-account-6a3q2sputpvay.cognitiveservices.azure.com/)
var baseUri = new Uri(endpoint);
var openAiEndpoint = new Uri($"{baseUri.Scheme}://{baseUri.Authority}/");
Console.WriteLine($"Resolved OpenAI Endpoint: {openAiEndpoint}");

var chatClient = new AzureOpenAIClient(openAiEndpoint, credential)
                     .GetResponsesClient()
                     .AsIChatClient(deploymentName)
                     .AsBuilder()
                     .UseFunctionInvocation()
                     .Build();

var agentOptions = new ChatClientAgentOptions
{
    Name = "todo-agent",
    Description = "A hosted agent that manages to-do list items.",
    Instructions = """
        You are a helpful assistant for managing a todo list.
        You can add, remove, and list tasks in the todo list.
        """,
    ChatOptions = new ChatOptions
    {
        Tools = [ serverTool ]
    }
};

AIAgent agent = new ChatClientAgent(chatClient, agentOptions);

builder.Services.AddFoundryResponses(agent);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
await app.RunAsync();
