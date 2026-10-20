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

if (builder.Environment.IsDevelopment() == true)
{
    var logger = new LoggerFactory().CreateLogger("MafStarterPack.Agent.Program");
    logger.LogInformation("Using configuration: {config}", config.GetDebugView());
    logger.LogInformation("Parsed connection string values: Endpoint={endpoint}", endpoint);
    logger.LogInformation("Parsed connection string values: Model={model}", model);
    logger.LogInformation("Parsed connection string values: AgentName={agentName}", agentName);
    logger.LogInformation("Parsed connection string values: AgentVersion={agentVersion}", agentVersion);
}

builder.AddServiceDefaults();

var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions() { TenantId = config["AZURE_TENANT_ID"] });
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
