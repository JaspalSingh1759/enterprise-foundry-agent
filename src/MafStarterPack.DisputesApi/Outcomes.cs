using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Azure.Data.Tables;
using Azure.Storage.Blobs;

namespace MafStarterPack.DisputesApi;

/// <summary>
/// Where money moves: the provisional credit. It's only called from the approve endpoint, after the approval
/// rules pass. No agent, prompt or MCP tool can reach it, whatever a claim or receipt says. In a real bank this
/// would call the card platform; here it records the posting in a "ledger" table.
/// </summary>
public sealed class Ledger(TableServiceClient tables)
{
    private readonly TableClient table = tables.GetTableClient("ledger");

    public Task EnsureTableAsync(CancellationToken ct) => table.CreateIfNotExistsAsync(ct);

    public async Task<string> PostProvisionalCreditAsync(CaseEntity dispute, decimal amount, string approvedBy, CancellationToken ct)
    {
        var postingId = $"CR-{dispute.Id}";
        var entry = new TableEntity(dispute.CardId, postingId)
        {
            ["CaseId"] = dispute.Id,
            ["AmountCad"] = (double)amount,
            ["Type"] = "provisional_credit",
            ["ApprovedBy"] = approvedBy,
            ["PostedAt"] = DateTimeOffset.UtcNow,
        };
        // Add, not upsert: posting the same case twice fails instead of crediting twice.
        await table.AddEntityAsync(entry, ct);
        return postingId;
    }
}

/// <summary>
/// Customer letters from the approved templates (docs/maple-bank/templates), in the customer's language. A
/// letter is refused if any {{placeholder}} would be left unfilled: a half-filled letter never reaches a customer.
/// </summary>
public sealed partial class Letters(BlobServiceClient blobs, BankReference bank)
{
    private readonly BlobContainerClient container = blobs.GetBlobContainerClient("letters");

    public Task EnsureContainerAsync(CancellationToken ct) => container.CreateIfNotExistsAsync(cancellationToken: ct);

    public async Task<string> SendAsync(CaseEntity dispute, JsonObject? draft, string kind, CancellationToken ct,
                                        decimal? creditAmount = null, string? reason = null, string? informationNeeded = null)
    {
        var text = Render(dispute, draft, kind, creditAmount, reason, informationNeeded);
        var name = $"{dispute.Id}/{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{kind}.txt";
        await container.GetBlobClient(name).UploadAsync(BinaryData.FromString(text), overwrite: false, ct);
        return name;
    }

    public string Render(CaseEntity dispute, JsonObject? draft, string kind, decimal? creditAmount = null,
                         string? reason = null, string? informationNeeded = null)
    {
        var language = dispute.Language == "fr" ? "fr" : "en";
        var path = Path.Combine(AppContext.BaseDirectory, "templates", $"letter-{kind}-{language}.md");
        var template = TextBlock().Match(File.ReadAllText(path)).Groups[1].Value;
        var culture = CultureInfo.GetCultureInfo(language == "fr" ? "fr-CA" : "en-CA");

        // The disputed charge: the latest of the draft's transactions on this card.
        var transaction = OutputChecks.Strings(draft?["transaction_ids"])
                                      .Where(bank.Transactions.ContainsKey).Select(id => bank.Transactions[id])
                                      .Where(t => t.CardId == dispute.CardId).OrderBy(t => t.TxnDate).LastOrDefault();
        var values = new Dictionary<string, string?>
        {
            ["customer_first_name"] = bank.Customers.GetValueOrDefault(dispute.CustomerId)?.FirstName,
            ["case_id"] = dispute.Id,
            ["card_last4"] = bank.Cards.GetValueOrDefault(dispute.CardId)?.Last4,
            ["merchant_name"] = transaction is null ? null : bank.Merchants.GetValueOrDefault(transaction.MerchantId)?.Name,
            ["transaction_date"] = transaction?.TxnDate.ToString("d MMMM yyyy", culture),
            ["disputed_amount"] = transaction?.Amount.ToString("C", culture),
            ["amount"] = creditAmount?.ToString("C", culture),
            ["reason"] = reason,
            ["information_needed"] = informationNeeded,
        };

        var letter = Placeholder().Replace(template, m => values.GetValueOrDefault(m.Groups[1].Value) ?? m.Value);
        var missing = Placeholder().Matches(letter).Select(m => m.Value).Distinct().ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"letter {kind} for {dispute.Id} would leave {string.Join(", ", missing)} unfilled");
        }
        return letter.Trim() + "\n";
    }

    [GeneratedRegex(@"```text\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex TextBlock();

    [GeneratedRegex(@"\{\{([a-z_0-9]+)\}\}")]
    private static partial Regex Placeholder();
}
