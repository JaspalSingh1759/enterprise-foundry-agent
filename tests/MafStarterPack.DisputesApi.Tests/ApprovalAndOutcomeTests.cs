using System.Text.Json.Nodes;

using Azure.Storage.Blobs;

using MafStarterPack.DisputesApi;

using Xunit;

namespace MafStarterPack.DisputesApi.Tests;

// The rules that decide who may close a case and move money, and what reaches the customer. All in code, all
// testable without Azure.
public class ApprovalAndOutcomeTests
{
    private static readonly BankReference Bank = new(AppContext.BaseDirectory);

    private static readonly CurrentUser Analyst = new("analyst@maple.test", new HashSet<string> { Roles.Analyst });
    private static readonly CurrentUser Senior = new("senior@maple.test", new HashSet<string> { Roles.SeniorAnalyst });
    private static readonly CurrentUser NoRole = new("someone@maple.test", new HashSet<string>());

    // CASE-0007: duplicate charge of 1,298.37 CAD (over the 1,000 senior threshold). CASE-0004: 170.91 CAD.
    private static CaseEntity Case(string id, string card, string status = CaseStatus.AwaitingApproval, string submittedBy = "customer:CUST-044") =>
        new() { RowKey = id, CardId = card, CustomerId = "CUST-044", Status = status, SubmittedBy = submittedBy, Language = "en" };

    private static JsonObject Draft(string action, decimal amount, params string[] transactionIds) => new()
    {
        ["action"] = action,
        ["amount"] = amount,
        ["transaction_ids"] = new JsonArray(transactionIds.Select(t => (JsonNode)t).ToArray()),
        // An agent claiming no senior approval is needed must not lower the bar.
        ["needs_senior_approval"] = false,
    };

    [Fact]
    public void An_analyst_can_approve_a_credit_up_to_the_threshold()
    {
        var refusal = ApprovalRules.CanApprove(Analyst, Case("CASE-0004", "CARD-0040"), Draft("recommend_credit", 170.91m, "TXN-104832"), Bank);

        Assert.Null(refusal);
    }

    [Fact]
    public void An_analyst_cannot_approve_a_credit_over_the_threshold_whatever_the_agent_said()
    {
        var refusal = ApprovalRules.CanApprove(Analyst, Case("CASE-0007", "CARD-0044"),
                                               Draft("recommend_credit", 1298.37m, "TXN-105932", "TXN-105933"), Bank);

        Assert.Contains("needs a senior analyst", refusal);
    }

    [Fact]
    public void A_senior_analyst_can_approve_a_credit_over_the_threshold()
    {
        var refusal = ApprovalRules.CanApprove(Senior, Case("CASE-0007", "CARD-0044"),
                                               Draft("recommend_credit", 1298.37m, "TXN-105932", "TXN-105933"), Bank);

        Assert.Null(refusal);
    }

    [Fact]
    public void Nobody_can_decide_a_case_they_submitted()
    {
        var own = Case("CASE-0004", "CARD-0040", submittedBy: Senior.Id);

        Assert.Contains("segregation of duties", ApprovalRules.CanApprove(Senior, own, Draft("recommend_credit", 170.91m, "TXN-104832"), Bank));
        Assert.Contains("segregation of duties", ApprovalRules.CanReject(Senior, own, "not eligible"));
    }

    [Fact]
    public void Only_analysts_can_decide()
    {
        Assert.Contains("only analysts", ApprovalRules.CanApprove(NoRole, Case("CASE-0004", "CARD-0040"),
                                                                   Draft("deny", 0, "TXN-104832"), Bank));
    }

    [Theory]
    [InlineData(CaseStatus.New)]
    [InlineData(CaseStatus.Investigating)]
    [InlineData(CaseStatus.Closed)]
    public void A_case_can_only_be_decided_while_awaiting_approval(string status)
    {
        var refusal = ApprovalRules.CanApprove(Senior, Case("CASE-0004", "CARD-0040", status), Draft("deny", 0, "TXN-104832"), Bank);

        Assert.Contains("not AwaitingApproval", refusal);
    }

    [Fact]
    public void Rejecting_needs_a_reason()
    {
        Assert.Contains("reason is required", ApprovalRules.CanReject(Analyst, Case("CASE-0004", "CARD-0040"), " "));
    }

    [Fact]
    public void Output_checks_reject_another_cards_transactions_and_a_credit_without_the_rules_engine()
    {
        var output = new JsonObject
        {
            ["case_id"] = "CASE-0004", ["action"] = "recommend_credit", ["eligible"] = true, ["amount"] = 170.91m,
            ["transaction_ids"] = new JsonArray("TXN-105932"), ["policy_sections"] = new JsonArray("4.3"),
        };

        var problems = OutputChecks.Policy(output, Case("CASE-0004", "CARD-0040"), Bank, toolsCalled: ["search_policy"]);

        Assert.Contains(problems, p => p.Contains("not a transaction on this case's card"));
        Assert.Contains(problems, p => p.Contains("call evaluate_dispute_rules"));
    }

    [Fact]
    public void Output_checks_reject_an_amount_that_does_not_fit_the_action()
    {
        var output = new JsonObject
        {
            ["case_id"] = "CASE-0004", ["action"] = "deny", ["eligible"] = false, ["amount"] = 25m,
            ["transaction_ids"] = new JsonArray("TXN-104832"), ["policy_sections"] = new JsonArray("4.3"),
        };

        var problems = OutputChecks.Policy(output, Case("CASE-0004", "CARD-0040"), Bank, toolsCalled: ["evaluate_dispute_rules"]);

        Assert.Contains(problems, p => p.Contains("amount must be"));
    }

    [Theory]
    [InlineData("credit-approved", "en")]
    [InlineData("credit-approved", "fr")]
    [InlineData("dispute-denied", "en")]
    [InlineData("dispute-denied", "fr")]
    [InlineData("information-needed", "en")]
    [InlineData("information-needed", "fr")]
    [InlineData("referred-to-specialist-team", "en")]
    [InlineData("referred-to-specialist-team", "fr")]
    public void Every_letter_fills_every_placeholder(string kind, string language)
    {
        var letters = new Letters(new BlobServiceClient("UseDevelopmentStorage=true"), Bank);
        var dispute = Case("CASE-0007", "CARD-0044");
        dispute.Language = language;

        var letter = letters.Render(dispute, Draft("recommend_credit", 1298.37m, "TXN-105932", "TXN-105933"), kind,
                                    creditAmount: 1298.37m, reason: "The charge is genuine.", informationNeeded: "A receipt.");

        Assert.DoesNotContain("{{", letter);
        Assert.Contains("CASE-0007", letter);
    }

    [Fact]
    public void A_letter_with_a_missing_value_is_refused()
    {
        var letters = new Letters(new BlobServiceClient("UseDevelopmentStorage=true"), Bank);

        // No credit amount for a credit letter: refused rather than sent with "{{amount}}" in it.
        var error = Assert.Throws<InvalidOperationException>(() =>
            letters.Render(Case("CASE-0007", "CARD-0044"), Draft("recommend_credit", 1298.37m, "TXN-105932"), "credit-approved"));

        Assert.Contains("{{amount}}", error.Message);
    }
}
