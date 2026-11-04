using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;

using Azure.Core;
using Azure.Identity;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Identity.Web;

namespace MafStarterPack.WebUI.Disputes;

/// <summary>The result of a call to disputes-api: the JSON body, or the error it returned.</summary>
public sealed record ApiResult(bool Ok, int Status, JsonNode? Body, string? Error);

/// <summary>Raised when the signed-in user's token can't be refreshed silently; the page sends them to sign in again.</summary>
public sealed class SignInRequiredException(Exception inner) : Exception("sign in again", inner);

/// <summary>
/// Gets a token for disputes-api AS THE SIGNED-IN ANALYST, so the API applies its approval rules to the real
/// person: their name for segregation of duties, their app roles for the senior threshold.
/// - In Azure: Microsoft.Identity.Web, with the token from the analyst's own sign-in. The console's app
///   registration has no secret; it trusts the web UI's managed identity (a federated identity credential).
/// - Locally: the developer's own Azure CLI sign-in (the Azure CLI is pre-authorized on the API's scope).
/// </summary>
public sealed class DisputesTokenSource(IServiceProvider services, IConfiguration config)
{
    private static readonly AzureCliCredential Cli = new();

    public bool SignInEnabled { get; } = string.IsNullOrWhiteSpace(config["AzureAd:ClientId"]) == false;

    public string Scope { get; } = config["DisputesApi:Scope"] ?? throw new InvalidOperationException("DisputesApi:Scope is not configured");

    public async Task<string> GetTokenAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (SignInEnabled == false)
        {
            return (await Cli.GetTokenAsync(new TokenRequestContext([Scope]), ct)).Token;
        }

        try
        {
            var tokens = services.GetRequiredService<ITokenAcquisition>();
            return await tokens.GetAccessTokenForUserAsync([Scope], user: user);
        }
        catch (Exception e) when (e is MicrosoftIdentityWebChallengeUserException or Microsoft.Identity.Client.MsalUiRequiredException)
        {
            // No cached token for this user (e.g. the web UI restarted): they need to sign in again.
            throw new SignInRequiredException(e);
        }
    }
}

/// <summary>Typed client for disputes-api. Every call carries the current analyst's token.</summary>
public sealed class DisputesClient(HttpClient http, DisputesTokenSource tokens, AuthenticationStateProvider auth)
{
    public bool SignInEnabled => tokens.SignInEnabled;

    public Task<ApiResult> ListAsync(string? status, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, status is null ? "cases" : $"cases?status={Uri.EscapeDataString(status)}", null, ct);

    public Task<ApiResult> GetAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Get, $"cases/{Uri.EscapeDataString(id)}", null, ct);

    public Task<ApiResult> CardsAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Get, "reference/cards", null, ct);

    public Task<ApiResult> SeedAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "admin/seed", null, ct);

    public Task<ApiResult> CreateAsync(string cardId, string claim, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "cases", new { cardId, claim }, ct);

    public Task<ApiResult> InvestigateAsync(string id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"cases/{Uri.EscapeDataString(id)}/investigate", null, ct);

    public Task<ApiResult> ApproveAsync(string id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"cases/{Uri.EscapeDataString(id)}/approve", null, ct);

    public Task<ApiResult> RejectAsync(string id, string reason, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"cases/{Uri.EscapeDataString(id)}/reject", new { reason }, ct);

    public Task<ApiResult> RequestInfoAsync(string id, string informationNeeded, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"cases/{Uri.EscapeDataString(id)}/request-info", new { reason = informationNeeded }, ct);

    private async Task<ApiResult> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var user = (await auth.GetAuthenticationStateAsync()).User;
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(user, ct));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try
        {
            json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
        }

        if (response.IsSuccessStatusCode)
        {
            return new ApiResult(true, (int)response.StatusCode, json, null);
        }
        // disputes-api answers refusals as problem details; show the reason the API gave.
        var error = (string?)json?["detail"] ?? (string?)json?["title"] ?? $"HTTP {(int)response.StatusCode}";
        return new ApiResult(false, (int)response.StatusCode, json, error);
    }
}
