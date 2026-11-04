using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Azure.Core;

namespace MafStarterPack.DisputesApi;

/// <summary>One agent turn: its JSON output and what's needed to audit it.</summary>
public sealed record AgentRun(string Agent, string AgentVersion, string Model, string ResponseId, JsonObject Output,
                              IReadOnlyList<string> Tools, int InputTokens, int OutputTokens, double Seconds);

public sealed class AgentRunFailedException(string message) : Exception(message);

/// <summary>
/// Runs a Foundry prompt agent (resources-foundry/agents) through the project's Responses API. Foundry runs the
/// agent's tool loop against mcp-banking; this returns the final structured output plus the agent version,
/// model and tool calls, which go into the audit trail. Signs in with the app's managed identity (Azure AI User).
/// </summary>
public sealed class FoundryAgents(HttpClient http, TokenCredential credential, IConfiguration config, ILogger<FoundryAgents> logger)
{
    private static readonly TokenRequestContext Scope = new(["https://ai.azure.com/.default"]);
    private readonly string endpoint = (config["Foundry:ProjectEndpoint"]
                                        ?? throw new InvalidOperationException("Foundry:ProjectEndpoint is not configured")).TrimEnd('/');

    public async Task<AgentRun> RunAsync(string agent, string input, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            agent_reference = new { type = "agent_reference", name = agent },
            input,
        });

        // A rate limit is the model deployment's tokens-per-minute quota; wait as asked, else back off.
        for (var attempt = 0; ; attempt++)
        {
            var started = DateTimeOffset.UtcNow;
            var token = await credential.GetTokenAsync(Scope, ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/openai/v1/responses")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError && attempt < 5)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(10 * Math.Pow(2, attempt), 120));
                logger.LogWarning("{Agent}: HTTP {Status}, retrying in {Wait}", agent, (int)response.StatusCode, wait);
                await Task.Delay(wait, ct);
                continue;
            }
            if (response.IsSuccessStatusCode == false)
            {
                throw new AgentRunFailedException($"{agent}: HTTP {(int)response.StatusCode} {Truncate(text)}");
            }

            return Parse(agent, JsonNode.Parse(text)!.AsObject(), (DateTimeOffset.UtcNow - started).TotalSeconds);
        }
    }

    private static AgentRun Parse(string agent, JsonObject result, double seconds)
    {
        if (result["error"] is JsonObject error)
        {
            throw new AgentRunFailedException($"{agent}: {error.ToJsonString()}");
        }

        var output = result["output"]?.AsArray() ?? [];
        var tools = output.Where(o => (string?)o?["type"] == "mcp_call").Select(o => (string?)o!["name"] ?? "?").ToList();
        var message = output.LastOrDefault(o => (string?)o?["type"] == "message")?["content"]?.AsArray()
                            .Select(c => (string?)c?["text"]).LastOrDefault(t => t is not null);
        if (message is null)
        {
            throw new AgentRunFailedException($"{agent}: no message in the response (status {(string?)result["status"]})");
        }

        JsonObject parsed;
        try
        {
            parsed = JsonNode.Parse(message)!.AsObject();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new AgentRunFailedException($"{agent}: output is not a JSON object: {Truncate(message)}");
        }

        return new AgentRun(
            Agent: agent,
            AgentVersion: (string?)result["agent_reference"]?["version"] ?? "?",
            Model: (string?)result["model"] ?? "?",
            ResponseId: (string?)result["id"] ?? "?",
            Output: parsed,
            Tools: tools,
            InputTokens: (int?)result["usage"]?["input_tokens"] ?? 0,
            OutputTokens: (int?)result["usage"]?["output_tokens"] ?? 0,
            Seconds: Math.Round(seconds, 1));
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];
}
