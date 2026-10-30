using System.Text.Json;

namespace MafStarterPack.McpBanking;

// Maple Bank is fictional. These records mirror the JSON Lines files written by
// tools/synthetic-bank/generate.py; the files use snake_case names.
public sealed record Customer(string Id, string FirstName, string LastName, string City, string Province, string Language, string CustomerSince);

public sealed record Card(string Id, string CustomerId, string Product, string Last4, string Status, string Opened);

public sealed record Merchant(string Id, string Name, string Descriptor, string Category, string City, string Region,
                              string Currency, string Channel, decimal? RecurringPrice);

public sealed record Transaction(string Id, string CardId, DateOnly TxnDate, DateOnly PostedDate, string Time, string MerchantId,
                                 string Descriptor, string Type, string Channel, decimal Amount, string Currency,
                                 decimal? OriginalAmount, string? OriginalCurrency, decimal? FxRate, decimal? FxFeePct);

public sealed record PastDispute(string Id, string CustomerId, string CardId, string MerchantId, string DisputeType,
                                 decimal Amount, DateOnly Opened, DateOnly Closed, string Outcome);

public sealed record DisputeCase(string Id, string CustomerId, string CardId, DateOnly Submitted, string Channel,
                                 string Language, string Claim, string[] AttachmentIds, string Status);

public sealed record Attachment(string Id, string CaseId, string Kind, string Quality, JsonElement Fields);

/// <summary>
/// The bank's reference data, loaded once at start-up. It's read-only: the generator's output is the
/// source of truth, so every start serves exactly the same data.
/// </summary>
public sealed class BankData
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public IReadOnlyDictionary<string, Customer> Customers { get; }
    public IReadOnlyDictionary<string, Card> Cards { get; }
    public IReadOnlyDictionary<string, Merchant> Merchants { get; }
    public IReadOnlyDictionary<string, Transaction> Transactions { get; }
    public ILookup<string, Transaction> TransactionsByCard { get; }
    public ILookup<string, PastDispute> PastDisputesByCustomer { get; }
    public IReadOnlyDictionary<string, DisputeCase> Cases { get; }
    public IReadOnlyDictionary<string, Attachment> Attachments { get; }

    public BankData(string dataDirectory)
    {
        Customers = Load<Customer>(dataDirectory, "customers.jsonl").ToDictionary(c => c.Id);
        Cards = Load<Card>(dataDirectory, "cards.jsonl").ToDictionary(c => c.Id);
        Merchants = Load<Merchant>(dataDirectory, "merchants.jsonl").ToDictionary(m => m.Id);
        var transactions = Load<Transaction>(dataDirectory, "transactions.jsonl");
        Transactions = transactions.ToDictionary(t => t.Id);
        TransactionsByCard = transactions.ToLookup(t => t.CardId);
        PastDisputesByCustomer = Load<PastDispute>(dataDirectory, "past_disputes.jsonl").ToLookup(d => d.CustomerId);
        Cases = Load<DisputeCase>(dataDirectory, "cases.jsonl").ToDictionary(c => c.Id);
        Attachments = Load<Attachment>(dataDirectory, "attachments.jsonl").ToDictionary(a => a.Id);
    }

    private static List<T> Load<T>(string directory, string file) =>
        File.ReadLines(Path.Combine(directory, file))
            .Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize<T>(line, Json)
                            ?? throw new InvalidDataException($"Empty record in {file}"))
            .ToList();
}
