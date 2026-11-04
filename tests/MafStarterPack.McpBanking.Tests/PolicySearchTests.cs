using MafStarterPack.McpBanking;

using Xunit;

namespace MafStarterPack.McpBanking.Tests;

// The filter is set by code, never by the model, so these tests are the guarantee that a dispute only ever
// sees the policy version in force on its transaction date.
public class PolicySearchTests
{
    [Fact]
    public void Filter_picks_the_policy_version_in_force_on_the_date()
    {
        var filter = PolicySearch.BuildFilter(new DateOnly(2026, 10, 15));

        Assert.Contains("effective_from le 2026-10-15T00:00:00Z", filter);
        Assert.Contains("effective_to eq null or effective_to gt 2026-10-15T00:00:00Z", filter);
    }

    [Fact]
    public void Filter_applies_the_date_to_policies_only()
    {
        // SOPs, network rules and the FAQ have a single current version; filtering them by the transaction
        // date would hide them for older transactions.
        Assert.Contains("(doc_type ne 'policy' or (", PolicySearch.BuildFilter(new DateOnly(2026, 8, 1)));
    }

    [Fact]
    public void Filter_hides_senior_only_documents_and_letter_templates()
    {
        var filter = PolicySearch.BuildFilter(new DateOnly(2026, 11, 10));

        Assert.StartsWith("audience/any(a: a eq 'analyst') and doc_type ne 'template' and ", filter);
    }

    [Fact]
    public async Task Search_tool_rejects_transactions_from_another_card()
    {
        var tools = DisputeRulesTests.NewToolsForTests();

        var result = await tools.SearchPolicy("CASE-0001", "time limit", ["TXN-100000"]);

        Assert.StartsWith("Not done:", result);
    }

    [Fact]
    public async Task Search_tool_says_so_when_search_is_not_configured()
    {
        var tools = DisputeRulesTests.NewToolsForTests();
        var (caseId, transactionId) = DisputeRulesTests.AnyCaseTransactionId();

        var result = await tools.SearchPolicy(caseId, "time limit", [transactionId]);

        Assert.StartsWith("Not available:", result);
    }
}
