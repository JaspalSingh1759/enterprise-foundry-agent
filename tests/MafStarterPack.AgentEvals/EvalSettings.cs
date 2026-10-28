using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace MafStarterPack.AgentEvals;

// Everything comes from environment variables, so the same tests run on a laptop and in the pipeline.
// A missing value fails the run instead of skipping it: a skipped quality gate looks green but proves nothing.
public sealed class EvalSettings
{
    public required Uri ProjectEndpoint { get; init; }
    public required string AgentName { get; init; }
    public required string ModelDeploymentName { get; init; }
    public required Uri McpTodoUrl { get; init; }
    public required string McpApiKey { get; init; }
    public required DefaultAzureCredential Credential { get; init; }

    public static async Task<EvalSettings> LoadAsync()
    {
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            TenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID")
        });

        var mcpFqdn = Required("AZURE_RESOURCE_MCP_TODO_FQDN");

        // The key is read from Key Vault at test time with the caller's own identity, never stored in
        // GitHub or in a file. MCP_API_KEY overrides it for local runs without Key Vault access.
        var apiKey = Environment.GetEnvironmentVariable("MCP_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var vault = new SecretClient(new Uri(Required("AZURE_KEY_VAULT_ENDPOINT")), credential);
            apiKey = (await vault.GetSecretAsync("mcp-api-key")).Value.Value;
        }

        return new EvalSettings
        {
            ProjectEndpoint = new Uri(Required("AZURE_AI_PROJECT_ENDPOINT")),
            AgentName = Environment.GetEnvironmentVariable("AGENT_NAME") ?? "todo-agent",
            ModelDeploymentName = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME") ?? "gpt-5-mini",
            McpTodoUrl = new Uri($"https://{mcpFqdn.TrimEnd('/')}/mcp"),
            McpApiKey = apiKey,
            Credential = credential
        };
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is not set. See tests/MafStarterPack.AgentEvals/README.md.");
}
