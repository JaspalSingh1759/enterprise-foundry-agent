using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;

namespace MafStarterPack.DisputesApi;

/// <summary>
/// Append-only audit trail: one JSON line per step, one append blob per case (audit/CASE-0007.jsonl). In Azure
/// the "audit" container has a time-based immutability (WORM) policy with protected append writes: lines can be
/// added, but nobody, admins included, can change or delete them until the retention period ends.
///
/// It answers the auditor's question ("why was this customer credited, who approved it, which policy, model and
/// agent version?") and the incident one ("a bad agent version ran for 3 hours; which cases did it touch?").
/// </summary>
public sealed class AuditLog(BlobServiceClient blobs)
{
    private readonly BlobContainerClient container = blobs.GetBlobContainerClient("audit");

    public Task EnsureContainerAsync(CancellationToken ct) => container.CreateIfNotExistsAsync(cancellationToken: ct);

    public Task AgentStepAsync(string caseId, AgentRun run, CancellationToken ct, string? note = null)
    {
        var record = Base(caseId, run.Agent, "agent_step");
        record["agent_version"] = run.AgentVersion;
        record["model"] = run.Model;
        record["response_id"] = run.ResponseId;
        record["tools"] = new JsonArray(run.Tools.Select(t => (JsonNode)t).ToArray());
        record["output_hash"] = Hash(run.Output.ToJsonString());
        record["input_tokens"] = run.InputTokens;
        record["output_tokens"] = run.OutputTokens;
        record["seconds"] = run.Seconds;
        if (run.Output["policy_sections"] is JsonArray sections)
        {
            record["policy_sections"] = sections.DeepClone();
            record["policy_version"] = run.Output["policy_version"]?.DeepClone();
            record["draft_action"] = run.Output["action"]?.DeepClone(); // "action" is the audit record's own field
            record["amount"] = run.Output["amount"]?.DeepClone();
        }
        if (note is not null)
        {
            record["note"] = note;
        }
        return AppendAsync(caseId, record, ct);
    }

    public Task EventAsync(string caseId, string actor, string action, CancellationToken ct, IDictionary<string, object?>? details = null)
    {
        var record = Base(caseId, actor, action);
        foreach (var (key, value) in details ?? new Dictionary<string, object?>())
        {
            record[key] = JsonSerializer.SerializeToNode(value);
        }
        return AppendAsync(caseId, record, ct);
    }

    public async Task<List<JsonObject>> ReadAsync(string caseId, CancellationToken ct)
    {
        var blob = container.GetAppendBlobClient($"{caseId}.jsonl");
        if (await blob.ExistsAsync(ct) == false)
        {
            return [];
        }
        var content = (await blob.DownloadContentAsync(ct)).Value.Content.ToString();
        return content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
    }

    private static JsonObject Base(string caseId, string actor, string action) => new()
    {
        ["case_id"] = caseId,
        ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
        ["actor"] = actor,
        ["action"] = action,
        ["trace_id"] = Activity.Current?.TraceId.ToString(),
    };

    private async Task AppendAsync(string caseId, JsonObject record, CancellationToken ct)
    {
        var blob = container.GetAppendBlobClient($"{caseId}.jsonl");
        await blob.CreateIfNotExistsAsync(cancellationToken: ct);
        using var line = new MemoryStream(Encoding.UTF8.GetBytes(record.ToJsonString() + "\n"));
        await blob.AppendBlockAsync(line, cancellationToken: ct);
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
