using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Shiny.Extensions.Push.Fcm;


/// <summary>
/// Mints and caches an OAuth2 access token for FCM. Builds an RS256-signed JWT from the service account,
/// exchanges it at the token endpoint for a bearer token, and reuses that token until shortly before it
/// expires. AOT-safe: manual JWT, <see cref="RSA.ImportFromPem"/>, <see cref="JsonDocument"/> parsing.
/// </summary>
public sealed class FcmAccessTokenProvider : IDisposable
{
    const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    readonly string appKey;
    readonly IHttpClientFactory httpClientFactory;
    readonly FcmServiceAccount account;
    readonly RSA key;
    readonly SemaphoreSlim gate = new(1, 1);

    string? cachedToken;
    DateTimeOffset expiresAt;


    public FcmAccessTokenProvider(string appKey, IOptionsMonitor<FcmOptions> options, IHttpClientFactory httpClientFactory)
    {
        this.appKey = appKey;
        this.httpClientFactory = httpClientFactory;
        this.account = options.Get(appKey).ResolveServiceAccount();
        this.key = RSA.Create();
        this.key.ImportFromPem(this.account.PrivateKeyPem);
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


    async Task<(string token, int expiresIn)> Fetch(CancellationToken ct)
    {
        var assertion = this.BuildAssertion();
        var client = this.httpClientFactory.CreateClient(FcmProvider.HttpClientName);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion
        });

        using var response = await client.PostAsync(this.account.TokenUri, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"FCM token exchange failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var token = root.GetProperty("access_token").GetString()!;
        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        return (token, expiresIn);
    }


    string BuildAssertion()
    {
        var now = DateTimeOffset.UtcNow;
        var header = """{"alg":"RS256","typ":"JWT"}""";
        var payload =
            $"{{\"iss\":\"{this.account.ClientEmail}\",\"scope\":\"{Scope}\"," +
            $"\"aud\":\"{this.account.TokenUri}\",\"iat\":{now.ToUnixTimeSeconds()},\"exp\":{now.AddHours(1).ToUnixTimeSeconds()}}}";

        var signingInput = $"{Encode(Encoding.UTF8.GetBytes(header))}.{Encode(Encoding.UTF8.GetBytes(payload))}";
        var signature = this.key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Encode(signature)}";
    }


    static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    public void Dispose()
    {
        this.key.Dispose();
        this.gate.Dispose();
    }
}
