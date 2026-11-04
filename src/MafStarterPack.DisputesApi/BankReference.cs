using System.Globalization;
using System.Text.Json;

namespace MafStarterPack.DisputesApi;

public sealed record CustomerInfo(string Id, string FirstName, string Language);

public sealed record MerchantInfo(string Id, string Name);

public sealed record CardInfo(string Id, string CustomerId, string Last4);

public sealed record TransactionInfo(string Id, string CardId, DateOnly TxnDate, string MerchantId, decimal Amount);

public sealed record SeedCase(string Id, string CustomerId, string CardId, DateOnly Submitted, string Channel, string Language,
                              string Claim, string[] AttachmentIds);

/// <summary>
/// Read-only reference data from the synthetic bank (Maple Bank is fictional): what letters need (first name,
/// merchant, transaction date), the 40 seed cases, and the senior-approval threshold from dispute-rules.json,
/// the same file mcp-banking's rules engine uses. In a real bank these would be calls to the core banking system.
/// </summary>
public sealed class BankReference
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly (DateOnly From, DateOnly? To, decimal SeniorOver)[] policyVersions;

    public IReadOnlyDictionary<string, CustomerInfo> Customers { get; }
    public IReadOnlyDictionary<string, MerchantInfo> Merchants { get; }
    public IReadOnlyDictionary<string, CardInfo> Cards { get; }
    public IReadOnlyDictionary<string, TransactionInfo> Transactions { get; }
    public IReadOnlyList<SeedCase> SeedCases { get; }

    public BankReference(string baseDirectory)
    {
        var data = Path.Combine(baseDirectory, "data");
        Customers = Load<CustomerInfo>(Path.Combine(data, "customers.jsonl")).ToDictionary(c => c.Id);
        Merchants = Load<MerchantInfo>(Path.Combine(data, "merchants.jsonl")).ToDictionary(m => m.Id);
        Cards = Load<CardInfo>(Path.Combine(data, "cards.jsonl")).ToDictionary(c => c.Id);
        Transactions = Load<TransactionInfo>(Path.Combine(data, "transactions.jsonl")).ToDictionary(t => t.Id);
        SeedCases = Load<SeedCase>(Path.Combine(data, "cases.jsonl"));

        using var rules = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseDirectory, "rules", "dispute-rules.json")));
        policyVersions = rules.RootElement.GetProperty("policy_versions").EnumerateArray().Select(v => (
            DateOnly.Parse(v.GetProperty("effective_from").GetString()!, CultureInfo.InvariantCulture),
            v.GetProperty("effective_to").ValueKind == JsonValueKind.Null
                ? (DateOnly?)null
                : DateOnly.Parse(v.GetProperty("effective_to").GetString()!, CultureInfo.InvariantCulture),
            v.GetProperty("senior_approval_over_cad").GetDecimal())).ToArray();
    }

    /// <summary>
    /// The senior-approval threshold of the policy version in force on the earliest transaction's date, the same
    /// way the rules engine picks it. Approval rights are checked against this, never against the agent's output.
    /// </summary>
    public decimal SeniorApprovalOver(IEnumerable<string> transactionIds)
    {
        var dates = transactionIds.Where(Transactions.ContainsKey).Select(id => Transactions[id].TxnDate).ToList();
        if (dates.Count == 0)
        {
            return policyVersions.Min(v => v.SeniorOver); // unknown transactions: apply the strictest limit
        }
        var date = dates.Min();
        return policyVersions.First(v => v.From <= date && (v.To is null || date < v.To)).SeniorOver;
    }

    private static List<T> Load<T>(string file) =>
        File.ReadLines(file).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<T>(l, Json)!).ToList();
}
