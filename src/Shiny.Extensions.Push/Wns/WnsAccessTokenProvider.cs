using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Shiny.Extensions.Push.Wns;


/// <summary>
/// Mints and caches an OAuth2 access token for WNS via the Entra (Azure AD) client-credentials flow, and
/// reuses it until shortly before it expires. No JWT signing — the app's client secret is exchanged
/// directly. AOT-safe (form-encoded request, <see cref="JsonDocument"/> parsing).
/// </summary>
public sealed class WnsAccessTokenProvider : IDisposable
{
    const string Scope = "https://wns.windows.com/.default";
    static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    readonly string tokenEndpoint;
    readonly string clientId;
    readonly string clientSecret;
    readonly IHttpClientFactory httpClientFactory;
    readonly SemaphoreSlim gate = new(1, 1);

    string? cachedToken;
    DateTimeOffset expiresAt;


    public WnsAccessTokenProvider(string appKey, IOptionsMonitor<WnsOptions> options, IHttpClientFactory httpClientFactory)
    {
        var o = options.Get(appKey);
        this.tokenEndpoint = o.ResolveTokenEndpoint();
        this.clientId = o.ClientId ?? "";
        this.clientSecret = o.ClientSecret ?? "";
        this.httpClientFactory = httpClientFactory;
    }


    public async Task<string> GetAccessToken(CancellationToken cancellationToken)
    {
        if (this.cachedToken != null && DateTimeOffset.UtcNow < this.expiresAt - Skew)
            return this.cachedToken;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.cachedToken != null && DateTimeOffset.UtcNow < this.expiresAt - Skew)
                return this.cachedToken;

            var (token, lifetime) = await this.Fetch(cancellationToken).ConfigureAwait(false);
            this.cachedToken = token;
            this.expiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime);
            return token;
        }
        finally
        {
            this.gate.Release();
        }
    }


    /// <summary>Drops the cached token so the next send re-mints one (used after a 401 from WNS).</summary>
    public void Invalidate() => this.cachedToken = null;


    async Task<(string token, int expiresIn)> Fetch(CancellationToken ct)
    {
        var client = this.httpClientFactory.CreateClient(WnsProvider.HttpClientName);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = this.clientId,
            ["client_secret"] = this.clientSecret,
            ["scope"] = Scope
        });

        using var response = await client.PostAsync(this.tokenEndpoint, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"WNS token exchange failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var token = root.GetProperty("access_token").GetString()!;
        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        return (token, expiresIn);
    }


    public void Dispose() => this.gate.Dispose();
}
