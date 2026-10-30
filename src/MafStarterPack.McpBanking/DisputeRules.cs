using System.Text.Json;

namespace MafStarterPack.McpBanking;

public sealed record PolicyVersion(string Version, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
                                   Dictionary<string, int> TimeLimitDays, decimal SeniorApprovalOverCad);

public sealed record DisputeTypeRule(string PolicySection, string[] RequiredEvidence, string? Notes);

public sealed record RulesFile(PolicyVersion[] PolicyVersions, Dictionary<string, DisputeTypeRule> DisputeTypes);

public sealed record TransactionWindow(string Id, DateOnly TxnDate, DateOnly PostedDate, string PolicyVersion,
                                       int TimeLimitDays, int DaysSincePosting, bool WithinWindow);

public sealed record RulesResult(string CaseId, string DisputeType, string PolicySection, string PolicyVersion,
                                 IReadOnlyList<TransactionWindow> Transactions, bool AllWithinWindow,
                                 decimal CreditAmount, decimal SeniorApprovalOverCad, bool NeedsSeniorApproval,
                                 string[] RequiredEvidence, string? Notes);

/// <summary>
/// The hard limits of the dispute policy, applied the same way every time. The model never computes
/// dates, time limits or thresholds; it calls this and explains the result.
/// </summary>
public sealed class DisputeRules
{
    private readonly RulesFile rules;

    public DisputeRules(string rulesFile)
    {
        rules = JsonSerializer.Deserialize<RulesFile>(File.ReadAllText(rulesFile), BankData.Json)
                ?? throw new InvalidDataException($"Empty rules file: {rulesFile}");
    }

    public IReadOnlyCollection<string> DisputeTypes => rules.DisputeTypes.Keys;

    /// <summary>The policy version in force on a date: effective_from inclusive, effective_to exclusive.</summary>
    public PolicyVersion VersionOn(DateOnly date) =>
        rules.PolicyVersions.SingleOrDefault(v => v.EffectiveFrom <= date && (v.EffectiveTo is null || date < v.EffectiveTo))
        ?? throw new InvalidOperationException($"No dispute policy version is in force on {date:yyyy-MM-dd}.");

    /// <summary>
    /// Time limits are counted in days from each transaction's posting date to the case's submission date,
    /// using the policy version in force on that transaction's date. The senior-approval threshold comes from
    /// the version in force on the earliest transaction.
    /// </summary>
    public RulesResult Evaluate(DisputeCase dispute, IReadOnlyList<Transaction> transactions, string disputeType, decimal creditAmount)
    {
        if (rules.DisputeTypes.TryGetValue(disputeType, out var typeRule) == false)
        {
            throw new ArgumentException($"Unknown dispute type '{disputeType}'. Use one of: {string.Join(", ", DisputeTypes)}.");
        }
        if (transactions.Count == 0)
        {
            throw new ArgumentException("At least one transaction is needed.");
        }
        if (creditAmount < 0)
        {
            throw new ArgumentException("The credit amount can't be negative. Use 0 when no credit is proposed.");
        }

        var windows = transactions
            .OrderBy(t => t.TxnDate).ThenBy(t => t.Id)
            .Select(t =>
            {
                var version = VersionOn(t.TxnDate);
                var limit = version.TimeLimitDays[disputeType];
                var days = dispute.Submitted.DayNumber - t.PostedDate.DayNumber;
                return new TransactionWindow(t.Id, t.TxnDate, t.PostedDate, version.Version, limit, days, days <= limit);
            })
            .ToList();

        var governing = VersionOn(windows[0].TxnDate);

        return new RulesResult(
            dispute.Id, disputeType, typeRule.PolicySection, governing.Version, windows,
            AllWithinWindow: windows.All(w => w.WithinWindow),
            CreditAmount: creditAmount,
            SeniorApprovalOverCad: governing.SeniorApprovalOverCad,
            NeedsSeniorApproval: creditAmount > governing.SeniorApprovalOverCad,
            typeRule.RequiredEvidence, typeRule.Notes);
    }
}
