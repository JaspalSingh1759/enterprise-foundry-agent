using Xunit;
using Xunit.Sdk;

namespace MafStarterPack.AgentEvals;

public sealed class EvalFixture : IAsyncLifetime
{
    public AgentClient Agent { get; private set; } = null!;
    public TodoStateClient Todos { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var settings = await EvalSettings.LoadAsync();
        Agent = new AgentClient(settings);
        Todos = await TodoStateClient.ConnectAsync(settings);
    }

    public ValueTask DisposeAsync() => Todos.DisposeAsync();
}

// State checks: arrange the to-do list, send one prompt to the deployed agent, then read the list straight
// from the MCP server and check what the agent actually did. The agent's reply text is never asserted on,
// because many different replies are correct; the final state of the list is not up for interpretation.
public sealed class StateCheckTests(EvalFixture fixture) : IClassFixture<EvalFixture>
{
    // The model doesn't answer the same way every time, so each case runs until it has passed twice or
    // failed twice (at most 3 runs). A real regression fails every run; a one-off wobble doesn't fail the gate.
    private const int RequiredPasses = 2;
    private const int AllowedFailures = 1;

    // An agent error (429, MCP down) is not a quality result, so it is retried after a pause rather than
    // counted as a failure. If it keeps happening the test fails, but labelled as an infrastructure problem.
    private const int MaxAgentErrors = 3;
    private static readonly TimeSpan AgentErrorBackoff = TimeSpan.FromSeconds(20);

    [Fact]
    public Task Add_creates_one_item() =>
        RunCaseAsync(
            seed: [],
            prompt: "add buy milk",
            check: items =>
            {
                var item = Assert.Single(items);
                Assert.Contains("milk", item.Text, StringComparison.OrdinalIgnoreCase);
                Assert.False(item.IsCompleted);
            });

    [Fact]
    public Task Add_several_creates_one_item_each() =>
        RunCaseAsync(
            seed: [],
            prompt: "add buy milk, call mum and pay rent",
            check: items =>
            {
                Assert.Equal(3, items.Count);
                AssertHasItem(items, "milk");
                AssertHasItem(items, "mum");
                AssertHasItem(items, "rent");
            });

    // The confusion Day 4 fixed: "finished" must complete the item, not delete it.
    [Fact]
    public Task Finished_completes_the_item_instead_of_deleting_it() =>
        RunCaseAsync(
            seed: ["buy milk", "call mum"],
            prompt: "I finished buy milk",
            check: items =>
            {
                Assert.Equal(2, items.Count);
                Assert.True(AssertHasItem(items, "milk").IsCompleted, "'buy milk' should be completed.");
                Assert.False(AssertHasItem(items, "mum").IsCompleted, "'call mum' should be untouched.");
            });

    [Fact]
    public Task Delete_removes_only_the_named_item() =>
        RunCaseAsync(
            seed: ["buy milk", "call mum"],
            prompt: "delete call mum",
            check: items =>
            {
                var item = Assert.Single(items);
                Assert.Contains("milk", item.Text, StringComparison.OrdinalIgnoreCase);
                Assert.False(item.IsCompleted);
            });

    [Fact]
    public Task Rename_changes_the_text_only() =>
        RunCaseAsync(
            seed: ["buy milk"],
            prompt: "rename buy milk to buy oat milk",
            check: items =>
            {
                var item = Assert.Single(items);
                Assert.Contains("oat milk", item.Text, StringComparison.OrdinalIgnoreCase);
                Assert.False(item.IsCompleted);
            });

    // The instructions say to confirm before deleting "everything", so one message must change nothing.
    [Fact]
    public Task Delete_everything_asks_first_and_changes_nothing() =>
        RunCaseAsync(
            seed: ["buy milk", "call mum"],
            prompt: "delete everything",
            check: AssertUnchanged("buy milk", "call mum"));

    // Two items match "buy", so the agent must ask which one rather than guess.
    [Fact]
    public Task Ambiguous_request_asks_and_changes_nothing() =>
        RunCaseAsync(
            seed: ["buy milk", "buy bread"],
            prompt: "I'm done with the buy item",
            check: AssertUnchanged("buy milk", "buy bread"));

    [Fact]
    public Task Out_of_scope_request_changes_nothing() =>
        RunCaseAsync(
            seed: ["buy milk"],
            prompt: "what's the weather in London?",
            check: AssertUnchanged("buy milk"));

    private async Task RunCaseAsync(string[] seed, string prompt, Action<IReadOnlyList<TodoItem>> check)
    {
        var output = TestContext.Current.TestOutputHelper!;
        var cancellationToken = TestContext.Current.CancellationToken;
        int passes = 0, failures = 0, agentErrors = 0, run = 0;
        var failureReasons = new List<string>();

        while (passes < RequiredPasses && failures <= AllowedFailures)
        {
            run++;
            output.WriteLine($"Run {run}: prompt: {prompt}");
            await fixture.Todos.ResetAsync(seed);                         // Arrange

            string reply;
            try
            {
                reply = await fixture.Agent.SendAsync(prompt, cancellationToken); // Act
            }
            catch (AgentRunFailedException ex)
            {
                agentErrors++;
                output.WriteLine($"  agent error {agentErrors}/{MaxAgentErrors}, not counted as a result: {ex.Message}");
                if (agentErrors >= MaxAgentErrors)
                {
                    Assert.Fail($"INFRASTRUCTURE: the agent run failed {agentErrors} times, so quality couldn't be measured. Last error: {ex.Message}");
                }
                await Task.Delay(AgentErrorBackoff * agentErrors, cancellationToken);
                continue;
            }

            var items = await fixture.Todos.GetItemsAsync();              // Assert, on state not on text

            output.WriteLine($"  reply: {reply}");
            output.WriteLine($"  state: {Describe(items)}");

            try
            {
                check(items);
                passes++;
                output.WriteLine("  result: pass");
            }
            catch (XunitException ex)
            {
                failures++;
                failureReasons.Add($"run {run}: {ex.Message.ReplaceLineEndings(" ")} | state: {Describe(items)} | reply: {reply}");
                output.WriteLine($"  result: FAIL: {ex.Message.ReplaceLineEndings(" ")}");
            }
        }

        if (passes < RequiredPasses)
        {
            Assert.Fail($"Failed {failures} of {run} runs.{Environment.NewLine}{string.Join(Environment.NewLine, failureReasons)}");
        }
    }

    private static TodoItem AssertHasItem(IReadOnlyList<TodoItem> items, string textFragment) =>
        Assert.Single(items, i => i.Text.Contains(textFragment, StringComparison.OrdinalIgnoreCase));

    private static Action<IReadOnlyList<TodoItem>> AssertUnchanged(params string[] expected) => items =>
    {
        Assert.Equal(expected.Order(), items.Select(i => i.Text).Order());
        Assert.All(items, i => Assert.False(i.IsCompleted, $"'{i.Text}' should not be completed."));
    };

    private static string Describe(IReadOnlyList<TodoItem> items) =>
        items.Count == 0 ? "(empty)" : string.Join("; ", items.Select(i => $"{i.Id} '{i.Text}'{(i.IsCompleted ? " done" : "")}"));
}
