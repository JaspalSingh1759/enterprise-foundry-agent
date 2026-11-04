using System.Globalization;
using System.Text.Json;

using Azure;
using Azure.Data.Tables;

namespace MafStarterPack.McpBanking;

/// <summary>
/// Finds a case. disputes-api owns cases (the "cases" table, Day 9), so new claims and current statuses come
/// from there; the cases built into this server's data are the fallback, for local runs and the unit tests.
/// Read-only: the server's identity has Storage Table Data Reader on that one table.
/// </summary>
public sealed class CaseLookup(BankData bank, TableClient? table, ILogger<CaseLookup> logger)
{
    public DisputeCase? Find(string caseId)
    {
        if (table is not null)
        {
            try
            {
                var result = table.GetEntityIfExists<TableEntity>("cases", caseId);
                if (result.HasValue)
                {
                    var e = result.Value!;
                    return new DisputeCase(
                        Id: caseId,
                        CustomerId: e.GetString("CustomerId"),
                        CardId: e.GetString("CardId"),
                        Submitted: DateOnly.Parse(e.GetString("Submitted"), CultureInfo.InvariantCulture),
                        Channel: e.GetString("Channel") ?? "web",
                        Language: e.GetString("Language") ?? "en",
                        Claim: e.GetString("Claim"),
                        AttachmentIds: JsonSerializer.Deserialize<string[]>(e.GetString("AttachmentIds") ?? "[]") ?? [],
                        Status: e.GetString("Status") ?? "New");
                }
            }
            catch (RequestFailedException ex)
            {
                // The case system being unreachable shouldn't hide the built-in cases; log it and fall back.
                logger.LogError(ex, "Reading case {CaseId} from the cases table failed.", caseId);
            }
        }
        return bank.Cases.GetValueOrDefault(caseId);
    }
}
