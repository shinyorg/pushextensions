using Microsoft.Extensions.Options;

namespace Shiny.Extensions.Push.Fcm;


/// <summary>
/// Mints and caches an OAuth2 access token for FCM for one keyed app: exchanges an RS256 assertion built from
/// the service account for a bearer token and reuses it until shortly before it expires. The signing/exchange
/// itself lives in <see cref="FcmToken"/>, shared with the multi-tenant path.
/// </summary>
public sealed class FcmAccessTokenProvider : IDisposable
{
    static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    readonly IHttpClientFactory httpClientFactory;
    readonly FcmServiceAccount account;
    readonly SemaphoreSlim gate = new(1, 1);

    string? cachedToken;
    DateTimeOffset expiresAt;


    public FcmAccessTokenProvider(string appKey, IOptionsMonitor<FcmOptions> options, IHttpClientFactory httpClientFactory)
    {
        this.httpClientFactory = httpClientFactory;
        this.account = options.Get(appKey).ResolveServiceAccount();
    }


    public string ProjectId => this.account.ProjectId;


    public async Task<string> GetAccessToken(CancellationToken cancellationToken)
    {
        if (this.cachedToken != null && DateTimeOffset.UtcNow < this.expiresAt - Skew)
            return this.cachedToken;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.cachedToken != null && DateTimeOffset.UtcNow < this.expiresAt - Skew)
                return this.cachedToken;

            var client = this.httpClientFactory.CreateClient(FcmProvider.HttpClientName);
            var (token, lifetime) = await FcmToken.Exchange(client, this.account, cancellationToken).ConfigureAwait(false);
            this.cachedToken = token;
            this.expiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime);
            return token;
        }
        finally
        {
            this.gate.Release();
        }
    }


    public void Dispose() => this.gate.Dispose();
}
