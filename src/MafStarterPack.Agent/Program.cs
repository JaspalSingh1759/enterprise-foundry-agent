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

var chatClient = projectClient.GetResponsesClient()
                              .AsIChatClient(model);
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
