using Azure.AI.Projects;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;

var builder = AgentHost.CreateBuilder(args);

Console.WriteLine("Starting Hosted Agent...");

var config = builder.Configuration;

// Must be the project endpoint (https://<account>.services.ai.azure.com/api/projects/<project>).
// The hosted agent's identity is authorized through the project, not the account's raw /openai/v1 endpoint.
var endpoint = config["FOUNDRY_PROJECT_ENDPOINT"]
            ?? config["AZURE_AI_PROJECT_ENDPOINT"]
            ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT environment variable is not set.");
var deploymentName = config["AZURE_AI_MODEL_DEPLOYMENT_NAME"] ?? "gpt-5-mini";
var tenantId = config["AZURE_TENANT_ID"];
var mcpTodoFqdn = config["AZURE_RESOURCE_MCP_TODO_FQDN"] ?? throw new InvalidOperationException("AZURE_RESOURCE_MCP_TODO_FQDN is not configured");

Console.WriteLine($"Using Foundry Project Endpoint: {endpoint}");
Console.WriteLine($"Using Model Deployment Name: {deploymentName}");
Console.WriteLine($"Using Azure Tenant ID: {tenantId}");
Console.WriteLine($"Using MCP Todo FQDN: {mcpTodoFqdn}");

var serverTool = new HostedMcpServerTool(
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

// The MCP server requires its API key in the x-api-key header. The key lives in Key Vault and is read
// with the agent's own identity. On a first deploy that identity is granted Key Vault access only after
// the agent starts (resources-foundry post-deploy hook), so load the key in the background and keep
// refreshing it, which also picks up a rotated key without a restart.
var keyVaultEndpoint = config["AZURE_KEY_VAULT_ENDPOINT"];
if (string.IsNullOrWhiteSpace(keyVaultEndpoint))
{
    Console.WriteLine("AZURE_KEY_VAULT_ENDPOINT is not set; calling the MCP server without an API key.");
}
else
{
    Console.WriteLine($"Using Key Vault: {keyVaultEndpoint}");
    var secretClient = new SecretClient(new Uri(keyVaultEndpoint), credential);
    _ = Task.Run(async () =>
    {
        while (true)
        {
            var delay = TimeSpan.FromMinutes(10);
            try
            {
                var secret = await secretClient.GetSecretAsync("mcp-api-key");
                serverTool.Headers = new Dictionary<string, string> { ["x-api-key"] = secret.Value.Value };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not read mcp-api-key from Key Vault, retrying in 30s: {ex.Message}");
                delay = TimeSpan.FromSeconds(30);
            }
            await Task.Delay(delay);
        }
    });
}

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
