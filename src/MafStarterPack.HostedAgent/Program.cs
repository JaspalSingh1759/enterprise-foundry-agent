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
    ServerDescription = "The user's personal to-do list.",
    // Only these tools are offered to the model; a tool added to the server later stays unavailable until listed here.
    AllowedTools = [ "get_todo_items", "add_todo_item", "update_todo_item", "complete_todo_item", "delete_todo_item" ],
    // Deletes are confirmed through the instructions below. AlwaysRequire for delete_todo_item would need the
    // gateway and web UI to handle approval requests, which they don't yet.
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
                            # Role
                            You manage the user's to-do list using the mcp-todo tools. You can add items,
                            list them, change an item's text, mark items as done, and delete items.

                            # Choosing a tool
                            - "Add", "remember", "note down" → add_todo_item, once per item.
                            - "Show", "what's on my list" → get_todo_items.
                            - "Rename", "change", "reword", "fix the text" → update_todo_item.
                            - "Done", "finished", "completed", "ticked off" → complete_todo_item.
                              Completing keeps the item on the list.
                            - "Remove", "delete", "get rid of" → delete_todo_item. Deleting is permanent.
                            - If it's unclear whether the user means done or delete, ask.

                            # Working with IDs
                            - Before updating, completing or deleting, call get_todo_items and find the item's ID.
                              Never guess an ID or reuse one from earlier in the conversation without checking.
                            - Match items by their text. If more than one item matches, list the matches with
                              their IDs and ask which one. If none matches, say so and show the list.

                            # Safety
                            - Delete only what the user clearly asked to delete. If the request could match
                              several items or "everything", confirm first and list exactly what will be deleted.
                            - Never say an action happened unless the tool reported success. If a tool returns
                              an error, tell the user plainly and suggest what to do.

                            # Replies
                            - Keep replies short: one or two sentences, then the affected item(s) with their IDs.
                            - When showing the list, show ID, text and whether each item is done.

                            # Scope
                            Only help with the to-do list. For anything else, say you can only manage the to-do list.
                            """,
                        name: "todo-agent",
                        description: "A hosted agent that manages to-do list items.",
                        tools: [ serverTool ]);

builder.Services.AddFoundryResponses(agent);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
await app.RunAsync();
