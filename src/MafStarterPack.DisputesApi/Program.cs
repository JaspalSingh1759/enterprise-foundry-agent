using System.Text.Json.Nodes;

using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;

using MafStarterPack.DisputesApi;

// disputes-api: Maple Bank's case system for card disputes (fictional bank, synthetic data). It owns the cases,
// runs the investigation workflow (three Foundry agents), and is the only place a credit can be posted, after
// an analyst approves. See docs/notes/day-06-11-maple-bank-disputes-plan.md, Day 9.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var config = builder.Configuration;

// In Azure: the app's user-assigned identity (AZURE_CLIENT_ID). Locally: the developer's own sign-in.
var clientId = config["AZURE_CLIENT_ID"];
TokenCredential credential = string.IsNullOrWhiteSpace(clientId)
    ? new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeManagedIdentityCredential = true })
    : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
builder.Services.AddSingleton(credential);

// Storage: Entra ID in Azure (shared keys are off on the account); a connection string only for the local emulator.
if (config["Storage:ConnectionString"] is { Length: > 0 } connectionString)
{
    builder.Services.AddSingleton(new TableServiceClient(connectionString));
    builder.Services.AddSingleton(new BlobServiceClient(connectionString));
}
else
{
    var account = config["Storage:AccountName"] ?? throw new InvalidOperationException("Storage:AccountName is not configured");
    builder.Services.AddSingleton(new TableServiceClient(new Uri($"https://{account}.table.core.windows.net"), credential));
    builder.Services.AddSingleton(new BlobServiceClient(new Uri($"https://{account}.blob.core.windows.net"), credential));
}

builder.Services.AddSingleton(new BankReference(AppContext.BaseDirectory));
builder.Services.AddSingleton<CaseRepository>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<Ledger>();
builder.Services.AddSingleton<Letters>();
builder.Services.AddSingleton<DisputeWorkflow>();
// An investigation runs three agents, each with several model calls: allow minutes, not the default 100 s.
builder.Services.AddHttpClient<FoundryAgents>(c => c.Timeout = TimeSpan.FromMinutes(5));

var app = builder.Build();
app.MapDefaultEndpoints();

using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    await sp.GetRequiredService<CaseRepository>().EnsureTableAsync(default);
    await sp.GetRequiredService<Ledger>().EnsureTableAsync(default);
    await sp.GetRequiredService<AuditLog>().EnsureContainerAsync(default);
    await sp.GetRequiredService<Letters>().EnsureContainerAsync(default);
}

var isDevelopment = app.Environment.IsDevelopment();

// Every endpoint knows who is calling. In Azure, Container Apps' built-in authentication rejects anonymous
// requests before they reach the app; this is the second line.
IResult? RequireAnalyst(HttpRequest request, out CurrentUser user)
{
    user = CurrentUser.From(request, isDevelopment)!;
    if (user is null)
    {
        return Results.Problem("sign in first", statusCode: StatusCodes.Status401Unauthorized);
    }
    return user.IsAnalyst ? null : Results.Problem("only analysts can do this", statusCode: StatusCodes.Status403Forbidden);
}

// ── Cases ──────────────────────────────────────────────────────────────────────────────────────────────────

// Loads the 40 synthetic cases (tools/synthetic-bank) as New cases, for demos and the end-to-end tests.
app.MapPost("/admin/seed", async (HttpRequest request, CaseRepository cases, BankReference bank, AuditLog audit, CancellationToken ct) =>
{
    if (RequireAnalyst(request, out var user) is { } denied)
    {
        return denied;
    }
    var added = 0;
    foreach (var seed in bank.SeedCases)
    {
        var entity = new CaseEntity
        {
            RowKey = seed.Id, CustomerId = seed.CustomerId, CardId = seed.CardId,
            Submitted = seed.Submitted.ToString("yyyy-MM-dd"), Channel = seed.Channel, Language = seed.Language,
            Claim = seed.Claim, AttachmentIds = System.Text.Json.JsonSerializer.Serialize(seed.AttachmentIds),
            SubmittedBy = $"customer:{seed.CustomerId}",
        };
        if (await cases.AddIfMissingAsync(entity, ct))
        {
            added++;
            await audit.EventAsync(seed.Id, entity.SubmittedBy, "case_created", ct, new Dictionary<string, object?> { ["seeded_by"] = user.Id });
        }
    }
    return Results.Ok(new { added, total = bank.SeedCases.Count });
});

// A new claim (the customer simulator). The case is visible to the agents at once: mcp-banking reads the same
// "cases" table. Whoever submits it can't later decide it (segregation of duties).
app.MapPost("/cases", async (HttpRequest request, NewCaseRequest body, CaseRepository cases, BankReference bank, AuditLog audit,
                             CancellationToken ct) =>
{
    var user = CurrentUser.From(request, isDevelopment);
    if (user is null)
    {
        return Results.Problem("sign in first", statusCode: StatusCodes.Status401Unauthorized);
    }
    if (bank.Cards.TryGetValue(body.CardId ?? "", out var card) == false)
    {
        return Results.Problem("unknown card", statusCode: 400);
    }
    if (string.IsNullOrWhiteSpace(body.Claim) || body.Claim.Length > 2000)
    {
        return Results.Problem("the claim must be 1 to 2000 characters", statusCode: 400);
    }

    var customer = bank.Customers[card.CustomerId];
    var existing = await cases.ListAsync(null, ct);
    for (var number = existing.Count + 1; ; number++)
    {
        var entity = new CaseEntity
        {
            RowKey = $"CASE-{number:0000}", CustomerId = customer.Id, CardId = card.Id,
            Submitted = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), Channel = "web",
            Language = customer.Language, Claim = body.Claim.Trim(), SubmittedBy = user.Id,
        };
        if (await cases.AddIfMissingAsync(entity, ct))
        {
            await audit.EventAsync(entity.Id, $"user:{user.Id}", "case_created", ct, new Dictionary<string, object?> { ["card_id"] = card.Id });
            return Results.Created($"/cases/{entity.Id}", new { id = entity.Id, status = entity.Status });
        }
    }
});

app.MapGet("/cases", async (HttpRequest request, string? status, CaseRepository cases, CancellationToken ct) =>
{
    if (RequireAnalyst(request, out _) is { } denied)
    {
        return denied;
    }
    if (status is not null && CaseStatus.All.Contains(status) == false)
    {
        return Results.Problem($"status must be one of {string.Join(", ", CaseStatus.All)}", statusCode: 400);
    }
    var list = await cases.ListAsync(status, ct);
    return Results.Ok(list.Select(c =>
    {
        var draft = ParseObject(c.DraftJson);
        return new
        {
            id = c.Id, status = c.Status, submitted = c.Submitted, language = c.Language,
            dispute_type = (string?)draft?["dispute_type"], action = (string?)draft?["action"], amount = (decimal?)draft?["amount"],
            needs_senior_approval = (bool?)draft?["needs_senior_approval"], updated_at = c.UpdatedAt,
        };
    }));
});

app.MapGet("/cases/{id}", async (HttpRequest request, string id, CaseRepository cases, AuditLog audit, CancellationToken ct) =>
{
    if (RequireAnalyst(request, out _) is { } denied)
    {
        return denied;
    }
    var c = await cases.GetAsync(id, ct);
    if (c is null)
    {
        return Results.NotFound();
    }
    return Results.Ok(new
    {
        id = c.Id, status = c.Status, submitted = c.Submitted, submitted_by = c.SubmittedBy, customer_id = c.CustomerId,
        card_id = c.CardId, language = c.Language, claim = c.Claim, attachment_ids = Parse(c.AttachmentIds),
        intake = Parse(c.IntakeJson), evidence = Parse(c.EvidenceJson), draft = Parse(c.DraftJson),
        manual_handling_reason = c.ManualHandlingReason,
        decision = c.Decision, decided_by = c.DecidedBy, decision_reason = c.DecisionReason, decided_at = c.DecidedAt,
        audit = await audit.ReadAsync(c.Id, ct),
    });
});

// ── Investigation: the agents run here, and only here ──────────────────────────────────────────────────────

app.MapPost("/cases/{id}/investigate", async (HttpRequest request, string id, CaseRepository cases, DisputeWorkflow workflow,
                                              AuditLog audit, CancellationToken ct) =>
{
    if (RequireAnalyst(request, out var user) is { } denied)
    {
        return denied;
    }
    var c = await cases.GetAsync(id, ct);
    if (c is null)
    {
        return Results.NotFound();
    }
    if (CaseStatus.CanInvestigate(c.Status) == false)
    {
        return Results.Problem($"a {c.Status} case can't be investigated", statusCode: StatusCodes.Status409Conflict);
    }

    // Claim the case first: if two requests race, the second one's ETag no longer matches and it stops here.
    c.Status = CaseStatus.Investigating;
    c.ManualHandlingReason = null;
    if (await cases.TryUpdateAsync(c, ct) == false)
    {
        return Results.Problem("the case changed meanwhile; try again", statusCode: StatusCodes.Status409Conflict);
    }
    await audit.EventAsync(c.Id, $"user:{user.Id}", "investigation_started", ct);

    var result = await workflow.RunAsync(c, ct);

    c.IntakeJson = result.Intake?.Output.ToJsonString();
    c.EvidenceJson = result.Evidence?.Output.ToJsonString();
    c.DraftJson = result.Policy?.Output.ToJsonString();
    c.Status = result.Failure is null ? CaseStatus.AwaitingApproval : CaseStatus.NeedsManualHandling;
    c.ManualHandlingReason = result.Failure;
    await cases.TryUpdateAsync(c, ct);
    await audit.EventAsync(c.Id, "workflow", "investigation_finished", ct, new Dictionary<string, object?>
    {
        ["status"] = c.Status, ["failure"] = result.Failure,
    });

    return Results.Ok(new { id = c.Id, status = c.Status, draft = Parse(c.DraftJson), failure = result.Failure });
});

// ── Human decisions: the only way a case is closed or money moves ──────────────────────────────────────────

app.MapPost("/cases/{id}/approve", async (HttpRequest request, string id, CaseRepository cases, BankReference bank,
                                          Ledger ledger, Letters letters, AuditLog audit, CancellationToken ct) =>
{
    if (RequireAnalyst(request, out var user) is { } denied)
    {
        return denied;
    }
    var c = await cases.GetAsync(id, ct);
    if (c is null)
    {
        return Results.NotFound();
    }
    var draft = ParseObject(c.DraftJson);
    if (ApprovalRules.CanApprove(user, c, draft, bank) is { } refusal)
    {
        await audit.EventAsync(c.Id, $"user:{user.Id}", "approve_refused", ct, new Dictionary<string, object?> { ["reason"] = refusal });
        return Results.Problem(refusal, statusCode: StatusCodes.Status403Forbidden);
    }

    var action = (string)draft!["action"]!;
    var amount = (decimal?)draft["amount"] ?? 0;

    // Record the decision first (with the ETag): of two analysts approving at once, only one gets past here.
    c.Status = action == "request_info" ? CaseStatus.InfoRequested : CaseStatus.Closed;
    c.Decision = "approved";
    c.DecidedBy = user.Id;
    c.DecidedAt = DateTimeOffset.UtcNow;
    if (await cases.TryUpdateAsync(c, ct) == false)
    {
        return Results.Problem("the case changed meanwhile; reload it", statusCode: StatusCodes.Status409Conflict);
    }
    await audit.EventAsync(c.Id, $"user:{user.Id}", "approve", ct, new Dictionary<string, object?>
    {
        ["draft_action"] = action, ["amount"] = amount, ["roles"] = user.Roles.Order().ToArray(),
    });

    string? posting = null;
    if (action == "recommend_credit")
    {
        posting = await ledger.PostProvisionalCreditAsync(c, amount, user.Id, ct);
        await audit.EventAsync(c.Id, "disputes-api", "provisional_credit_posted", ct, new Dictionary<string, object?>
        {
            ["posting_id"] = posting, ["amount"] = amount, ["card_id"] = c.CardId,
        });
    }

    var explanation = (string?)draft["explanation"];
    var letter = action switch
    {
        "recommend_credit" => await letters.SendAsync(c, draft, "credit-approved", ct, creditAmount: amount),
        "deny" => await letters.SendAsync(c, draft, "dispute-denied", ct, reason: explanation),
        "request_info" => await letters.SendAsync(c, draft, "information-needed", ct, informationNeeded: explanation),
        _ => await letters.SendAsync(c, draft, "referred-to-specialist-team", ct),
    };
    await audit.EventAsync(c.Id, "disputes-api", "letter_sent", ct, new Dictionary<string, object?> { ["letter"] = letter });

    return Results.Ok(new { id = c.Id, status = c.Status, posting, letter });
});

app.MapPost("/cases/{id}/reject", async (HttpRequest request, string id, DecisionRequest body, CaseRepository cases,
                                         Letters letters, AuditLog audit, CancellationToken ct) =>
{
    if (RequireAnalyst(request, out var user) is { } denied)
    {
        return denied;
    }
    var c = await cases.GetAsync(id, ct);
    if (c is null)
    {
        return Results.NotFound();
    }
    if (ApprovalRules.CanReject(user, c, body.Reason) is { } refusal)
    {
        return Results.Problem(refusal, statusCode: StatusCodes.Status403Forbidden);
    }

    // The analyst overrules the draft and declines the dispute. The reason feeds the override-rate signal (Day 11).
    c.Status = CaseStatus.Closed;
    c.Decision = "rejected";
    c.DecidedBy = user.Id;
    c.DecisionReason = body.Reason;
    c.DecidedAt = DateTimeOffset.UtcNow;
    if (await cases.TryUpdateAsync(c, ct) == false)
    {
        return Results.Problem("the case changed meanwhile; reload it", statusCode: StatusCodes.Status409Conflict);
    }
    var draft = ParseObject(c.DraftJson);
    await audit.EventAsync(c.Id, $"user:{user.Id}", "reject", ct, new Dictionary<string, object?>
    {
        ["reason"] = body.Reason, ["draft_action"] = (string?)draft?["action"],
    });
    var letter = await letters.SendAsync(c, draft, "dispute-denied", ct, reason: body.Reason);
    await audit.EventAsync(c.Id, "disputes-api", "letter_sent", ct, new Dictionary<string, object?> { ["letter"] = letter });
    return Results.Ok(new { id = c.Id, status = c.Status, letter });
});

app.MapPost("/cases/{id}/request-info", async (HttpRequest request, string id, DecisionRequest body, CaseRepository cases,
                                               Letters letters, AuditLog audit, CancellationToken ct) =>
{
    if (RequireAnalyst(request, out var user) is { } denied)
    {
        return denied;
    }
    var c = await cases.GetAsync(id, ct);
    if (c is null)
    {
        return Results.NotFound();
    }
    if (ApprovalRules.CanRequestInfo(user, c, body.Reason) is { } refusal)
    {
        return Results.Problem(refusal, statusCode: StatusCodes.Status403Forbidden);
    }

    c.Status = CaseStatus.InfoRequested;
    c.Decision = "info_requested";
    c.DecidedBy = user.Id;
    c.DecisionReason = body.Reason;
    c.DecidedAt = DateTimeOffset.UtcNow;
    if (await cases.TryUpdateAsync(c, ct) == false)
    {
        return Results.Problem("the case changed meanwhile; reload it", statusCode: StatusCodes.Status409Conflict);
    }
    var draft = ParseObject(c.DraftJson);
    await audit.EventAsync(c.Id, $"user:{user.Id}", "request_info", ct, new Dictionary<string, object?> { ["information_needed"] = body.Reason });
    var letter = await letters.SendAsync(c, draft, "information-needed", ct, informationNeeded: body.Reason);
    await audit.EventAsync(c.Id, "disputes-api", "letter_sent", ct, new Dictionary<string, object?> { ["letter"] = letter });
    return Results.Ok(new { id = c.Id, status = c.Status, letter });
});

await app.RunAsync();

static JsonNode? Parse(string? json) => string.IsNullOrEmpty(json) ? null : JsonNode.Parse(json);
static JsonObject? ParseObject(string? json) => Parse(json) as JsonObject;

/// <summary>A new claim: the card it's about and the customer's words.</summary>
public sealed record NewCaseRequest(string? CardId, string? Claim);

/// <summary>Body of reject (the reason) and request-info (what's needed).</summary>
public sealed record DecisionRequest(string? Reason);

public partial class Program;
