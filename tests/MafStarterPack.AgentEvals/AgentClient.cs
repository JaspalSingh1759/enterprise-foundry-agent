using Azure.AI.Extensions.OpenAI;

using Microsoft.Extensions.AI;

namespace MafStarterPack.AgentEvals;

// The agent run itself failed (rate limit, MCP server down, auth), so its result says nothing about quality.
public sealed class AgentRunFailedException(string message) : Exception(message);

// Sends one prompt to the deployed todo-agent through the Foundry project, the same way the gateway does
// (src/MafStarterPack.Agent/Program.cs). We call Foundry directly rather than the gateway because the
// quality gate runs before stage 3 deploys the gateway.
public sealed class AgentClient(EvalSettings settings)
{
    private readonly IChatClient chatClient = new ProjectOpenAIClient(
            projectEndpoint: settings.ProjectEndpoint,
            tokenProvider: settings.Credential,
            options: new ProjectOpenAIClientOptions { AgentName = settings.AgentName })
        .GetResponsesClient()
        .AsIChatClient(settings.ModelDeploymentName);

    // Every call is a new conversation: no history is passed, so one test can't influence the next.
    public async Task<string> SendAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var response = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: cancellationToken);

        // Foundry reports a failure inside the agent's tool loop (e.g. a 429 from the model) as an error item
        // in an otherwise successful HTTP response. The loop stopped part-way, so the list may be half-changed.
        var contents = response.Messages.SelectMany(m => m.Contents).ToList();
        if (contents.OfType<ErrorContent>().FirstOrDefault() is { } error)
        {
            throw new AgentRunFailedException($"{error.ErrorCode}: {error.Message}");
        }
        if (contents.Count == 0)
        {
            throw new AgentRunFailedException("The agent returned an empty response.");
        }

        return response.Text;
    }
}
