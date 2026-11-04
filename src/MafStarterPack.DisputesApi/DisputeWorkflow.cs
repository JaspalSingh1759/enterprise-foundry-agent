using System.Text.Json.Nodes;

using Microsoft.Agents.AI.Workflows;

namespace MafStarterPack.DisputesApi;

/// <summary>What flows through the workflow: the case and each agent's run so far.</summary>
public sealed record DisputeState(CaseEntity Case, AgentRun? Intake = null, AgentRun? Evidence = null, AgentRun? Policy = null,
                                  string? Failure = null);

/// <summary>
/// The investigation, as a fixed Agent Framework workflow: intake → evidence → policy. Code decides the order,
/// never a model, so every case takes the same steps (easy to audit, test and cost). Between steps, code checks
/// each agent's JSON (<see cref="OutputChecks"/>); a bad answer is retried once with the reason, and a second
/// bad answer stops the workflow so the case goes to manual handling instead of reaching an analyst as a draft.
/// </summary>
public sealed class DisputeWorkflow(FoundryAgents agents, AuditLog audit, BankReference bank, IConfiguration config,
                                    ILogger<DisputeWorkflow> logger)
{
    private readonly string intakeAgent = config["Agents:Intake"] ?? "intake-agent";
    private readonly string evidenceAgent = config["Agents:Evidence"] ?? "evidence-agent";
    private readonly string policyAgent = config["Agents:Policy"] ?? "policy-agent";

    public async Task<DisputeState> RunAsync(CaseEntity dispute, CancellationToken ct)
    {
        var intake = Step("intake", async (state, token) =>
        {
            var run = await RunCheckedAsync(state.Case, intakeAgent, $"Case ID: {state.Case.Id}",
                                            output => OutputChecks.Intake(output, state.Case.Id), token);
            return state with { Intake = run };
        });

        var evidence = Step("evidence", async (state, token) =>
        {
            var input = $"Case ID: {state.Case.Id}\n\nIntake report:\n{state.Intake!.Output.ToJsonString()}";
            var run = await RunCheckedAsync(state.Case, evidenceAgent, input,
                                            output => OutputChecks.Evidence(output, state.Case, bank), token);
            return state with { Evidence = run };
        });

        var policy = Step("policy", async (state, token) =>
        {
            var input = $"Case ID: {state.Case.Id}\n\nIntake report:\n{state.Intake!.Output.ToJsonString()}" +
                        $"\n\nEvidence report:\n{state.Evidence!.Output.ToJsonString()}";
            AgentRun? latest = null;
            var run = await RunCheckedAsync(state.Case, policyAgent, input, output =>
                OutputChecks.Policy(output, state.Case, bank, latest?.Tools ?? []), token, r => latest = r);
            return state with { Policy = run };
        });

        var workflow = new WorkflowBuilder(intake)
            .AddEdge(intake, evidence)
            .AddEdge(evidence, policy)
            .WithOutputFrom(policy)
            .Build();

        await using var run = await InProcessExecution.RunAsync(workflow, new DisputeState(dispute), cancellationToken: ct);
        foreach (var evt in run.OutgoingEvents)
        {
            switch (evt)
            {
                case WorkflowOutputEvent output when output.Is<DisputeState>(out var result):
                    return result;
                case ExecutorFailedEvent failed:
                    return new DisputeState(dispute, Failure: $"step {failed.ExecutorId} failed: {failed.Data}");
                case WorkflowErrorEvent error:
                    return new DisputeState(dispute, Failure: $"workflow error: {error.Data}");
            }
        }
        return new DisputeState(dispute, Failure: "the workflow ended without a result");
    }

    /// <summary>
    /// A workflow step. Once a step has failed, later steps pass the state through untouched, so the failure
    /// reaches the end of the workflow with the reason.
    /// </summary>
    private static ExecutorBinding Step(string id, Func<DisputeState, CancellationToken, Task<DisputeState>> work) =>
        new Func<DisputeState, IWorkflowContext, CancellationToken, ValueTask<DisputeState>>(async (state, context, ct) =>
        {
            var next = state.Failure is not null ? state : await RunStepAsync(id, state, work, ct);
            if (id == "policy")
            {
                await context.YieldOutputAsync(next, ct);
            }
            return next;
        }).BindAsExecutor(id);

    private static async Task<DisputeState> RunStepAsync(string id, DisputeState state,
                                                          Func<DisputeState, CancellationToken, Task<DisputeState>> work,
                                                          CancellationToken ct)
    {
        try
        {
            return await work(state, ct);
        }
        catch (AgentRunFailedException e)
        {
            return state with { Failure = $"{id}: {e.Message}" };
        }
    }

    /// <summary>
    /// Runs an agent and checks its output. On problems, retries once and tells the agent what was wrong; if the
    /// retry has problems too, throws so the case goes to manual handling. Every attempt is audited.
    /// </summary>
    private async Task<AgentRun> RunCheckedAsync(CaseEntity dispute, string agent, string input,
                                                 Func<JsonObject, IReadOnlyList<string>> check, CancellationToken ct,
                                                 Action<AgentRun>? onRun = null)
    {
        var run = await agents.RunAsync(agent, input, ct);
        onRun?.Invoke(run);
        var problems = check(run.Output);
        await audit.AgentStepAsync(dispute.Id, run, ct, problems.Count > 0 ? $"rejected by checks: {string.Join("; ", problems)}" : null);
        if (problems.Count == 0)
        {
            return run;
        }

        logger.LogWarning("Case {CaseId}: {Agent} output rejected ({Problems}); retrying once.", dispute.Id, agent, string.Join("; ", problems));
        var retryInput = $"{input}\n\nYour previous answer was rejected by the bank's checks: {string.Join("; ", problems)}. " +
                         "Fix these and answer again.";
        run = await agents.RunAsync(agent, retryInput, ct);
        onRun?.Invoke(run);
        problems = check(run.Output);
        await audit.AgentStepAsync(dispute.Id, run, ct, problems.Count > 0 ? $"retry rejected by checks: {string.Join("; ", problems)}" : "retry accepted");
        if (problems.Count > 0)
        {
            throw new AgentRunFailedException($"{agent} output failed the checks twice: {string.Join("; ", problems)}");
        }
        return run;
    }
}

/// <summary>
/// Code checks on each agent's output, on top of the JSON schema Foundry already enforces. They catch answers
/// that are well-formed but wrong in ways code can see: another card's transactions, a credit with no amount,
/// a recommendation made without calling the rules engine.
/// </summary>
public static class OutputChecks
{
    public static readonly string[] DisputeTypes =
        ["duplicate_charge", "incorrect_amount", "credit_not_processed", "cancelled_recurring", "not_received", "unauthorised"];

    public static readonly string[] Actions = ["recommend_credit", "deny", "request_info", "route_to_fraud"];

    public static IReadOnlyList<string> Intake(JsonObject output, string caseId)
    {
        var problems = new List<string>();
        if ((string?)output["case_id"] != caseId)
        {
            problems.Add($"case_id must be {caseId}");
        }
        if (DisputeTypes.Contains((string?)output["dispute_type"]) == false)
        {
            problems.Add("dispute_type is not one of the six types");
        }
        return problems;
    }

    public static IReadOnlyList<string> Evidence(JsonObject output, CaseEntity dispute, BankReference bank)
    {
        var problems = new List<string>();
        var ids = Strings(output["transaction_ids"]);
        if (ids.Count == 0)
        {
            problems.Add("transaction_ids is empty; list the transactions the outcome relies on");
        }
        problems.AddRange(ForeignTransactions(ids, dispute, bank));
        return problems;
    }

    public static IReadOnlyList<string> Policy(JsonObject output, CaseEntity dispute, BankReference bank, IReadOnlyList<string> toolsCalled)
    {
        var problems = new List<string>();
        var action = (string?)output["action"];
        var amount = (decimal?)output["amount"] ?? 0;
        if ((string?)output["case_id"] != dispute.Id)
        {
            problems.Add($"case_id must be {dispute.Id}");
        }
        if (Actions.Contains(action) == false)
        {
            problems.Add("action is not one of recommend_credit, deny, request_info, route_to_fraud");
        }
        if (action == "recommend_credit" ? amount <= 0 : amount != 0)
        {
            problems.Add("amount must be above 0 for recommend_credit and exactly 0 otherwise");
        }
        if ((bool?)output["eligible"] != (action == "recommend_credit"))
        {
            problems.Add("eligible must be true for recommend_credit and false otherwise");
        }
        var ids = Strings(output["transaction_ids"]);
        if (ids.Count == 0)
        {
            problems.Add("cite the transaction IDs the recommendation relies on");
        }
        problems.AddRange(ForeignTransactions(ids, dispute, bank));
        if (Strings(output["policy_sections"]).Count == 0)
        {
            problems.Add("cite at least one policy section");
        }
        if (toolsCalled.Contains("evaluate_dispute_rules") == false)
        {
            problems.Add("call evaluate_dispute_rules before recommending; its results are final");
        }
        return problems;
    }

    private static IEnumerable<string> ForeignTransactions(IEnumerable<string> ids, CaseEntity dispute, BankReference bank) =>
        ids.Where(id => bank.Transactions.TryGetValue(id, out var t) == false || t.CardId != dispute.CardId)
           .Select(id => $"{id} is not a transaction on this case's card");

    public static List<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(n => (string?)n).Where(s => s is not null).Select(s => s!).ToList() : [];
}
