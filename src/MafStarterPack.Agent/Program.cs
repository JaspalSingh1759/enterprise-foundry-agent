using Azure.AI.Extensions.OpenAI;
using Azure.Identity;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

var config = builder.Configuration;

var endpoint = config["Foundry:Project:Endpoint"] ?? throw new InvalidOperationException("Missing Foundry Endpoint");
var model = config["Foundry:Project:Model"] ?? throw new InvalidOperationException("Missing Foundry Model");
var agentName = config["Foundry:Project:Agent:Name"] ?? "todo-agent";
var agentVersion = config["Foundry:Project:Agent:Version"] ?? "1";

var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("MafStarterPack.Agent.Program");
logger.LogInformation("Parsed connection string values: Endpoint={endpoint}, Model={model}, AgentName={agentName}, AgentVersion={agentVersion}", endpoint, model, agentName, agentVersion);

builder.AddServiceDefaults();

var credentialOptions = new DefaultAzureCredentialOptions
{
    TenantId = config["AZURE_TENANT_ID"]
};

var clientId = config["AZURE_CLIENT_ID"];
if (!string.IsNullOrWhiteSpace(clientId))
{
    logger.LogInformation("Using ManagedIdentityClientId: {clientId}", clientId);
    credentialOptions.ManagedIdentityClientId = clientId;
}

var credential = new DefaultAzureCredential(credentialOptions);
var projectClientOptions = new ProjectOpenAIClientOptions { AgentName = agentName };
var projectClient = new ProjectOpenAIClient(
    projectEndpoint: new Uri(endpoint),
    tokenProvider: credential,
    options: projectClientOptions);

var rawChatClient = projectClient.GetResponsesClient()
                                 .AsIChatClient(model);
var chatClient = new ExceptionLoggingChatClient(rawChatClient);
var agentOptions = new ChatClientAgentOptions { Name = agentName };
var agent = new ChatClientAgent(
    chatClient: chatClient,
    options: agentOptions);

builder.Services.AddKeyedSingleton<AIAgent>(agentName, agent);

builder.Services.AddOpenAIResponses();
builder.Services.AddOpenAIConversations();

builder.Services.AddAGUIServer();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/health-agent", async () =>
{
    try
    {
        var response = await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")]);
        return Results.Ok(new { status = "ok", response = response.Text });
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "Agent Invocation Error", detail: ex.ToString());
    }
});

app.MapOpenAIResponses();
app.MapOpenAIConversations();

app.MapAGUIServer(
    "ag-ui",
    app.Services.GetRequiredKeyedService<AIAgent>(agentName)
);

if (builder.Environment.IsDevelopment() == true)
{
    app.MapDevUI();
}
else
{
    app.UseHttpsRedirection();
}

app.Run();

public class ExceptionLoggingChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Console.WriteLine($"[ChatClient] GetResponseAsync invoked with {chatMessages.Count()} messages.");
            return await base.GetResponseAsync(chatMessages, options, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChatClient ERROR] GetResponseAsync failed: {ex}");
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[ChatClient] GetStreamingResponseAsync invoked with {chatMessages.Count()} messages.");
        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        try
        {
            enumerator = base.GetStreamingResponseAsync(chatMessages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChatClient ERROR] Failed initializing stream: {ex}");
            throw;
        }

        while (true)
        {
            ChatResponseUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }
                update = enumerator.Current;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChatClient ERROR] Failed during streaming update: {ex}");
                throw;
            }

            yield return update;
        }
    }
}
