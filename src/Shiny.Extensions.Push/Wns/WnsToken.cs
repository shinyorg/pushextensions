using System.Text.Json;

namespace Shiny.Extensions.Push.Wns;


/// <summary>
/// Shared Entra (Azure AD) client-credentials token exchange for WNS, used by both the keyed
/// <see cref="WnsAccessTokenProvider"/> and the multi-tenant path. No JWT signing — the app's client secret is
/// exchanged directly. AOT-safe (form-encoded request, <see cref="JsonDocument"/> parsing).
/// </summary>
static class WnsToken
{
    const string Scope = "https://wns.windows.com/.default";


    /// <summary>Exchanges the client credentials for a bearer token, returning it and its lifetime in seconds.</summary>
    public static async ValueTask<(string Token, int ExpiresIn)> Exchange(
        HttpClient client,
        string tokenEndpoint,
        string clientId,
        string clientSecret,
        CancellationToken ct
    )
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = Scope
        });

        using var response = await client.PostAsync(tokenEndpoint, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"WNS token exchange failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var token = root.GetProperty("access_token").GetString()!;
        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        return (token, expiresIn);
    }
}
