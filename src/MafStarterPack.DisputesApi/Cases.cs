using Azure;
using Azure.Data.Tables;

namespace MafStarterPack.DisputesApi;

/// <summary>
/// A case's life:
///
///   New ──▶ Investigating ──▶ AwaitingApproval ──┬─▶ Closed (approved: credit posted / letter sent)
///              ▲     │ (agents run here only)    ├─▶ Closed (rejected: letter sent)
///              │     └─▶ NeedsManualHandling      └─▶ InfoRequested ──▶ (customer replies) ──┘
///              └───────────────────────────────────────────────┘
///
/// Approval can take hours, so nothing "waits" in memory: the state is stored, and every request loads the
/// case, checks the transition is allowed, and saves it with the ETag it read (a concurrent change fails).
/// </summary>
public static class CaseStatus
{
    public const string New = "New";
    public const string Investigating = "Investigating";
    public const string AwaitingApproval = "AwaitingApproval";
    public const string InfoRequested = "InfoRequested";
    public const string NeedsManualHandling = "NeedsManualHandling";
    public const string Closed = "Closed";

    public static readonly string[] All = [New, Investigating, AwaitingApproval, InfoRequested, NeedsManualHandling, Closed];

    /// <summary>States from which the agents may (re)investigate a case.</summary>
    public static bool CanInvestigate(string status) => status is New or InfoRequested or NeedsManualHandling;
}

public sealed class CaseEntity : ITableEntity
{
    public const string Partition = "cases";

    public string PartitionKey { get; set; } = Partition;
    public string RowKey { get; set; } = string.Empty; // the case ID
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string CustomerId { get; set; } = string.Empty;
    public string CardId { get; set; } = string.Empty;
    public string Submitted { get; set; } = string.Empty; // yyyy-MM-dd
    public string Channel { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public string Claim { get; set; } = string.Empty;
    public string AttachmentIds { get; set; } = "[]"; // JSON array
    public string SubmittedBy { get; set; } = string.Empty;

    public string Status { get; set; } = CaseStatus.New;
    public DateTimeOffset UpdatedAt { get; set; }

    // The agents' reports, as returned (JSON). The policy agent's report is the draft an analyst approves.
    public string? IntakeJson { get; set; }
    public string? EvidenceJson { get; set; }
    public string? DraftJson { get; set; }
    public string? ManualHandlingReason { get; set; }

    // The human decision.
    public string? Decision { get; set; } // approved | rejected | info_requested
    public string? DecidedBy { get; set; }
    public string? DecisionReason { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    public string Id => RowKey;
}

public sealed class CaseRepository(TableServiceClient tables)
{
    private readonly TableClient table = tables.GetTableClient("cases");

    public Task EnsureTableAsync(CancellationToken ct) => table.CreateIfNotExistsAsync(ct);

    public async Task<CaseEntity?> GetAsync(string id, CancellationToken ct)
    {
        var result = await table.GetEntityIfExistsAsync<CaseEntity>(CaseEntity.Partition, id, cancellationToken: ct);
        return result.HasValue ? result.Value : null;
    }

    public async Task<List<CaseEntity>> ListAsync(string? status, CancellationToken ct)
    {
        var filter = status is null
            ? TableClient.CreateQueryFilter($"PartitionKey eq {CaseEntity.Partition}")
            : TableClient.CreateQueryFilter($"PartitionKey eq {CaseEntity.Partition} and Status eq {status}");
        var cases = new List<CaseEntity>();
        await foreach (var entity in table.QueryAsync<CaseEntity>(filter, cancellationToken: ct))
        {
            cases.Add(entity);
        }
        return cases.OrderBy(c => c.RowKey).ToList();
    }

    public async Task<bool> AddIfMissingAsync(CaseEntity entity, CancellationToken ct)
    {
        try
        {
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            await table.AddEntityAsync(entity, ct);
            return true;
        }
        catch (RequestFailedException e) when (e.Status == 409)
        {
            return false;
        }
    }

    /// <summary>Saves the case only if nobody changed it since it was read. Returns false on a conflict.</summary>
    public async Task<bool> TryUpdateAsync(CaseEntity entity, CancellationToken ct)
    {
        try
        {
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            var response = await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct);
            entity.ETag = response.Headers.ETag ?? entity.ETag;
            return true;
        }
        catch (RequestFailedException e) when (e.Status == 412)
        {
            return false;
        }
    }
}
