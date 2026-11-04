using System.Text.Json;

using MafStarterPack.McpBanking;

using Azure.Identity;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace MafStarterPack.McpBanking.Tests;

// The generator planted each case and wrote down the right answer on its own (dispute-cases.jsonl).
// These tests check the rules engine, a separate implementation, against those answers, and check the
// tools' guard rails.
public class DisputeRulesTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly BankData Bank = new(Path.Combine(RepoRoot, "src", "MafStarterPack.McpBanking", "data"));
    private static readonly DisputeRules Rules = new(Path.Combine(RepoRoot, "src", "MafStarterPack.McpBanking", "rules", "dispute-rules.json"));

    public static TheoryData<string> CaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in Truth.Keys.Order())
        {
            data.Add(id);
        }
        return data;
    }

    private static readonly Dictionary<string, JsonElement> Truth =
        File.ReadLines(Path.Combine(RepoRoot, "resources-foundry", "evals", "datasets", "dispute-cases.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToDictionary(row => row.GetProperty("case_id").GetString()!, row => row.GetProperty("truth").Clone());

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Rules_engine_agrees_with_the_ground_truth(string caseId)
    {
        var truth = Truth[caseId];
        var dispute = Bank.Cases[caseId];
        var transactions = truth.GetProperty("transactions").EnumerateArray().Select(t => Bank.Transactions[t.GetString()!]).ToList();

        var result = Rules.Evaluate(dispute, transactions, truth.GetProperty("dispute_type").GetString()!,
                                    truth.GetProperty("amount").GetDecimal());

        var primary = result.Transactions.Single(t => t.Id == truth.GetProperty("primary_transaction").GetString());
        Assert.Equal(truth.GetProperty("policy_version").GetString(), result.PolicyVersion);
        Assert.Equal(truth.GetProperty("time_limit_days").GetInt32(), primary.TimeLimitDays);
        Assert.Equal(truth.GetProperty("days_since_posting").GetInt32(), primary.DaysSincePosting);
        Assert.Equal(truth.GetProperty("within_window").GetBoolean(), result.AllWithinWindow);
        Assert.Equal(truth.GetProperty("needs_senior_approval").GetBoolean(), result.NeedsSeniorApproval);
    }

    [Theory]
    [InlineData("2026-10-31", "3.0")]
    [InlineData("2026-11-01", "3.1")]
    [InlineData("2027-03-15", "3.1")]
    public void The_policy_version_follows_the_transaction_date(string date, string expected)
    {
        Assert.Equal(expected, Rules.VersionOn(DateOnly.Parse(date)).Version);
    }

    [Fact]
    public void A_credit_of_exactly_the_threshold_does_not_need_a_senior_analyst()
    {
        var (dispute, transaction) = AnyCaseTransaction();

        Assert.False(Rules.Evaluate(dispute, [transaction], "incorrect_amount", 1000.00m).NeedsSeniorApproval);
        Assert.True(Rules.Evaluate(dispute, [transaction], "incorrect_amount", 1000.01m).NeedsSeniorApproval);
    }

    [Fact]
    public void Tools_refuse_a_transaction_from_another_customers_card()
    {
        var (dispute, _) = AnyCaseTransaction();
        var foreign = Bank.Transactions.Values.First(t => t.CardId != dispute.CardId);

        var reply = NewTools().EvaluateDisputeRules(dispute.Id, "duplicate_charge", [foreign.Id], 0);

        Assert.StartsWith("Not done:", reply);
    }

    [Fact]
    public void Tools_never_return_customer_names()
    {
        var (dispute, _) = AnyCaseTransaction();
        var customer = Bank.Customers[dispute.CustomerId];

        var reply = NewTools().GetCase(dispute.Id);

        Assert.DoesNotContain(customer.FirstName, reply);
        Assert.DoesNotContain(customer.LastName, reply);
    }

    [Fact]
    public void An_unreadable_receipt_is_reported_as_unreadable()
    {
        var caseId = Truth.Single(t => t.Value.GetProperty("scenario").GetString() == "unreadable_receipt").Key;
        var attachmentId = Bank.Cases[caseId].AttachmentIds.Single();

        var reply = JsonDocument.Parse(NewTools().ReadAttachment(caseId, attachmentId)).RootElement;

        Assert.False(reply.GetProperty("readable").GetBoolean());
        Assert.False(reply.TryGetProperty("fields", out _));
    }

    [Theory]
    [InlineData("recommend_credit", 0)]
    [InlineData("deny", 25)]
    public void A_draft_with_an_amount_that_does_not_fit_the_action_is_rejected(string action, decimal amount)
    {
        var (dispute, transaction) = AnyCaseTransaction();

        var reply = NewTools().SaveDraft(dispute.Id, "incorrect_amount", action, amount, [transaction.Id], ["4.3"], "Test.");

        Assert.StartsWith("Not saved:", reply);
    }

    private static (DisputeCase, Transaction) AnyCaseTransaction()
    {
        var dispute = Bank.Cases["CASE-0001"];
        return (dispute, Bank.TransactionsByCard[dispute.CardId].First());
    }

    internal static BankingTools NewToolsForTests() => NewTools();

    internal static (string CaseId, string TransactionId) AnyCaseTransactionId()
    {
        var (dispute, transaction) = AnyCaseTransaction();
        return (dispute.Id, transaction.Id);
    }

    private static BankingTools NewTools() =>
        new(Bank, new CaseLookup(Bank, null, NullLogger<CaseLookup>.Instance), Rules, new CaseStore(), new AttachmentReader(),
            UnconfiguredSearch(), NullLogger<BankingTools>.Instance);

    // No Search:Endpoint, so no network call is ever made; the guard rails run before the search does.
    private static PolicySearch UnconfiguredSearch() =>
        new(new HttpClient(), new DefaultAzureCredential(), new ConfigurationBuilder().Build(), NullLogger<PolicySearch>.Instance);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && File.Exists(Path.Combine(dir.FullName, "MafStarterPack.slnx")) == false)
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (MafStarterPack.slnx) not found.");
    }
}
