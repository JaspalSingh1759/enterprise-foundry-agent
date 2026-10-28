using System.Text.Json;
using System.Text.RegularExpressions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MafStarterPack.AgentEvals;

public sealed record TodoItem(int Id, string Text, bool IsCompleted);

// Reads and changes the to-do list directly on the MCP server, bypassing the agent and the model.
// This is the "look in the database" half of a state check: the source of truth, not the agent's reply.
public sealed partial class TodoStateClient : IAsyncDisposable
{
    private readonly McpClient client;

    private TodoStateClient(McpClient client) => this.client = client;

    public static async Task<TodoStateClient> ConnectAsync(EvalSettings settings)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = settings.McpTodoUrl,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["x-api-key"] = settings.McpApiKey }
        });
        return new TodoStateClient(await McpClient.CreateAsync(transport));
    }

    public async Task<IReadOnlyList<TodoItem>> GetItemsAsync()
    {
        var lines = await CallAsync("get_todo_items", new Dictionary<string, object?>());
        return [.. lines.Select(ParseItem).OfType<TodoItem>()];
    }

    public async Task<TodoItem> AddAsync(string text)
    {
        var reply = string.Join(" ", await CallAsync("add_todo_item", new Dictionary<string, object?> { ["todoItemText"] = text }));
        var match = AddedPattern().Match(reply);
        return match.Success
            ? new TodoItem(int.Parse(match.Groups[1].Value), text, false)
            : throw new InvalidOperationException($"Unexpected add_todo_item result: {reply}");
    }

    // The list lives in memory on one shared MCP server, so every test starts by emptying it.
    public async Task ResetAsync(params string[] seedItems)
    {
        foreach (var item in await GetItemsAsync())
        {
            await CallAsync("delete_todo_item", new Dictionary<string, object?> { ["id"] = item.Id });
        }
        foreach (var text in seedItems)
        {
            await AddAsync(text);
        }
    }

    private async Task<IReadOnlyList<string>> CallAsync(string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        var texts = result.Content.OfType<TextContentBlock>().Select(b => b.Text).ToList();
        if (result.IsError == true)
        {
            throw new InvalidOperationException($"{tool} failed: {string.Join(" ", texts)}");
        }

        // A tool that returns a list may come back as one JSON array or as one text block per item.
        if (texts is [var single] && single.TrimStart().StartsWith('['))
        {
            return JsonSerializer.Deserialize<List<string>>(single) ?? [];
        }
        return texts;
    }

    // Matches the format written by TodoTool.GetTodoItemsAsync: "ID: 4, Text: buy milk, Completed: False".
    private static TodoItem? ParseItem(string line)
    {
        var match = ItemPattern().Match(line);
        return match.Success
            ? new TodoItem(int.Parse(match.Groups[1].Value), match.Groups[2].Value, bool.Parse(match.Groups[3].Value))
            : null;
    }

    [GeneratedRegex(@"^ID: (\d+), Text: (.*), Completed: (True|False)$")]
    private static partial Regex ItemPattern();

    [GeneratedRegex(@"Added item (\d+)")]
    private static partial Regex AddedPattern();

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
