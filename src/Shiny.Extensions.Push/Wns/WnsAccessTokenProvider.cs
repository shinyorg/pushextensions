using Microsoft.Extensions.Options;

namespace Shiny.Extensions.Push.Wns;


/// <summary>
/// Mints and caches an OAuth2 access token for WNS via the Entra (Azure AD) client-credentials flow, and
/// reuses it until shortly before it expires. The exchange itself lives in <see cref="WnsToken"/>, shared
/// with the multi-tenant path.
/// </summary>
public sealed class WnsAccessTokenProvider : IDisposable
{
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


    ValueTask<(string Token, int ExpiresIn)> Fetch(CancellationToken ct)
        => WnsToken.Exchange(
            this.httpClientFactory.CreateClient(WnsProvider.HttpClientName),
            this.tokenEndpoint,
            this.clientId,
            this.clientSecret,
            ct
        );


    public void Dispose() => this.gate.Dispose();
}
