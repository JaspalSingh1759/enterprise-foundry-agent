using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MafStarterPack.DisputesApi;

public static class Roles
{
    public const string Analyst = "Analyst";
    public const string SeniorAnalyst = "SeniorAnalyst";
}

/// <summary>The signed-in person acting on a case.</summary>
public sealed record CurrentUser(string Id, IReadOnlySet<string> Roles)
{
    public bool IsAnalyst => Roles.Contains(DisputesApi.Roles.Analyst) || Roles.Contains(DisputesApi.Roles.SeniorAnalyst);
    public bool IsSenior => Roles.Contains(DisputesApi.Roles.SeniorAnalyst);

    /// <summary>
    /// In Azure, Container Apps' built-in authentication signs the user in with Entra ID and passes the result in
    /// the X-MS-CLIENT-PRINCIPAL header (base64 JSON, including app roles); the app writes no login code, and
    /// callers can't forge the header because the platform strips it from incoming requests. In local
    /// development only, X-Dev-User and X-Dev-Roles stand in for it.
    /// </summary>
    public static CurrentUser? From(HttpRequest request, bool isDevelopment)
    {
        if (request.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var principal) && principal.Count > 0)
        {
            var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(principal.ToString())))!;
            var claims = json["claims"]?.AsArray().Select(c => ((string?)c?["typ"], (string?)c?["val"])).ToList() ?? [];
            var name = claims.FirstOrDefault(c => c.Item1 is "preferred_username" or ClaimTypes.Upn or ClaimTypes.Email).Item2
                       ?? claims.FirstOrDefault(c => c.Item1 is "http://schemas.microsoft.com/identity/claims/objectidentifier").Item2;
            var roles = claims.Where(c => c.Item1 is "roles" or ClaimTypes.Role).Select(c => c.Item2!).ToHashSet();
            return name is null ? null : new CurrentUser(name, roles);
        }

        if (isDevelopment && request.Headers.TryGetValue("X-Dev-User", out var devUser))
        {
            var roles = request.Headers["X-Dev-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new CurrentUser(devUser.ToString(), roles.ToHashSet());
        }

        return null;
    }
}

/// <summary>
/// The approval rules, in code. They're checked against the case and the bank's own rules file, never against
/// what an agent said: an agent's "needs_senior_approval: false" can't lower the bar.
/// </summary>
public static class ApprovalRules
{
    public static string? CanApprove(CurrentUser user, CaseEntity dispute, JsonObject? draft, BankReference bank)
    {
        if (CanDecide(user, dispute) is { } error)
        {
            return error;
        }
        if (draft is null)
        {
            return "the case has no draft to approve";
        }

        var action = (string?)draft["action"];
        var amount = (decimal?)draft["amount"] ?? 0;
        var threshold = bank.SeniorApprovalOver(OutputChecks.Strings(draft["transaction_ids"]));
        if (action == "recommend_credit" && amount > threshold && user.IsSenior == false)
        {
            return $"a credit of {amount:0.00} CAD is over {threshold:0.00} CAD and needs a senior analyst";
        }
        return null;
    }

    public static string? CanReject(CurrentUser user, CaseEntity dispute, string? reason) =>
        CanDecide(user, dispute) ?? (string.IsNullOrWhiteSpace(reason) ? "a reason is required to reject" : null);

    public static string? CanRequestInfo(CurrentUser user, CaseEntity dispute, string? informationNeeded) =>
        CanDecide(user, dispute) ?? (string.IsNullOrWhiteSpace(informationNeeded) ? "say what information is needed" : null);

    private static string? CanDecide(CurrentUser user, CaseEntity dispute)
    {
        if (user.IsAnalyst == false)
        {
            return "only analysts can decide cases";
        }
        if (dispute.Status != CaseStatus.AwaitingApproval)
        {
            return $"the case is {dispute.Status}, not AwaitingApproval";
        }
        if (string.Equals(user.Id, dispute.SubmittedBy, StringComparison.OrdinalIgnoreCase))
        {
            return "you submitted this case, so someone else must decide it (segregation of duties)";
        }
        return null;
    }
}
