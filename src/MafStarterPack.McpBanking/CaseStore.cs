using System.Collections.Concurrent;
using System.Text.Json;

namespace MafStarterPack.McpBanking;

public sealed record Draft(string CaseId, string DisputeType, string Action, decimal Amount, string[] TransactionIds,
                           string[] PolicySections, string Explanation, DateTimeOffset SavedAt);

public sealed record AttachmentReading(string Id, string Kind, bool Readable, double Confidence, JsonElement? Fields, string? Message);

/// <summary>
/// Drafts saved by the agents. In memory for now: Day 9 moves cases and drafts to disputes-api with
/// Table Storage, which is where approval happens.
/// </summary>
public sealed class CaseStore
{
    private readonly ConcurrentDictionary<string, Draft> drafts = new();

    public Draft? GetDraft(string caseId) => drafts.TryGetValue(caseId, out var draft) ? draft : null;

    public void SaveDraft(Draft draft) => drafts[draft.CaseId] = draft;

    public string StatusOf(DisputeCase dispute) => drafts.ContainsKey(dispute.Id) ? "AwaitingApproval" : dispute.Status;
}

/// <summary>
/// Stand-in for reading an attachment. It returns the fields the generator put on each receipt, with a
/// confidence that depends on the image quality, so the agents can be built and tested against a
/// perfect reader first. Day 10 replaces it with Document Intelligence and a vision model, and the
/// same tests then show how much accuracy the real reader loses.
/// </summary>
public sealed class AttachmentReader
{
    private static readonly Dictionary<string, double> ConfidenceByQuality = new()
    {
        ["clean"] = 0.98, ["pdf"] = 0.96, ["screenshot"] = 0.95, ["injection"] = 0.95, ["photo"] = 0.88, ["blurry"] = 0.22,
    };

    public const double ReadableThreshold = 0.6;

    public AttachmentReading Read(Attachment attachment)
    {
        var confidence = ConfidenceByQuality.GetValueOrDefault(attachment.Quality, 0.5);
        return confidence < ReadableThreshold
            ? new AttachmentReading(attachment.Id, attachment.Kind, false, confidence, null,
                                    "The image is too unclear to read. Ask the customer for a clearer copy.")
            : new AttachmentReading(attachment.Id, attachment.Kind, true, confidence, attachment.Fields, null);
    }
}
