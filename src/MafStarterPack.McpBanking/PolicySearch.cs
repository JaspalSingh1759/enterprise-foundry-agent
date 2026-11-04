using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Azure.Core;

namespace MafStarterPack.McpBanking;

public sealed record PolicyPassage(string Source, string Title, string Section, string DocType, string Version, double Score, string Text);

/// <summary>
/// Searches Maple Bank's knowledge index (built by resources-foundry/knowledge/apply.sh) the way the policy
/// agent needs: hybrid keyword + vector search with the semantic ranker, and filters that code sets, not the
/// model. The policy version filter in particular comes from the transaction date, because a dispute is
/// judged by the policy in force when the transaction happened. Signs in with the app's managed identity
/// (Search Index Data Reader); the search service embeds the question itself (integrated vectorization).
/// </summary>
public sealed class PolicySearch(HttpClient http, TokenCredential credential, IConfiguration config, ILogger<PolicySearch> logger)
{
    private const string ApiVersion = "2025-08-01-preview"; // querying through an index alias needs a preview version
    private const int Top = 5;
    private static readonly TokenRequestContext Scope = new(["https://search.azure.com/.default"]);

    private readonly string? endpoint = config["Search:Endpoint"]?.TrimEnd('/');
    private readonly string index = config["Search:Index"] ?? "maple-knowledge";

    public bool IsConfigured => string.IsNullOrWhiteSpace(endpoint) == false;

    /// <summary>
    /// Only what an analyst may see; no letter templates (they're for writing to the customer, not for
    /// deciding); and only the policy version in force on the given date. Other document types have one
    /// current version, so the date applies to policies only.
    /// </summary>
    public static string BuildFilter(DateOnly inForceOn)
    {
        var date = $"{inForceOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}T00:00:00Z";
        return "audience/any(a: a eq 'analyst') and doc_type ne 'template' and " +
               $"(doc_type ne 'policy' or (effective_from le {date} and (effective_to eq null or effective_to gt {date})))";
    }

    public async Task<IReadOnlyList<PolicyPassage>> SearchAsync(string question, DateOnly inForceOn, CancellationToken cancellationToken)
    {
        var body = new
        {
            search = question,
            vectorQueries = new[] { new { kind = "text", text = question, fields = "chunk_vector", k = 20 } },
            queryType = "semantic",
            filter = BuildFilter(inForceOn),
            top = Top,
            select = "parent_path,title,section,doc_type,version,chunk",
        };

        var token = await credential.GetTokenAsync(Scope, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/indexes/{index}/docs/search?api-version={ApiVersion}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode == false)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("Policy search failed: HTTP {Status} {Detail}", (int)response.StatusCode, detail);
            throw new HttpRequestException($"search returned HTTP {(int)response.StatusCode}");
        }

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("value").EnumerateArray().Select(hit => new PolicyPassage(
            Source: hit.GetProperty("parent_path").GetString()!,
            Title: hit.GetProperty("title").GetString()!,
            Section: hit.GetProperty("section").GetString()!,
            DocType: hit.GetProperty("doc_type").GetString()!,
            Version: hit.GetProperty("version").GetString()!,
            Score: Math.Round(hit.TryGetProperty("@search.rerankerScore", out var score) ? score.GetDouble() : 0, 2),
            Text: hit.GetProperty("chunk").GetString()!)).ToList();
    }
}
