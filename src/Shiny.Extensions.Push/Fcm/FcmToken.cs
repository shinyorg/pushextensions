using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shiny.Extensions.Push.Fcm;


/// <summary>
/// Shared OAuth2 access-token minting for FCM, used by both the keyed <see cref="FcmAccessTokenProvider"/> and
/// the multi-tenant path. Builds an RS256 JWT assertion from the service account and exchanges it for a bearer
/// token. AOT-safe: hand-built JWT, <see cref="RSA.ImportFromPem"/>, <see cref="JsonDocument"/> parsing.
/// </summary>
static class FcmToken
{
    const string Scope = "https://www.googleapis.com/auth/firebase.messaging";


    /// <summary>Exchanges a fresh assertion for a bearer token, returning it and its lifetime in seconds.</summary>
    public static async ValueTask<(string Token, int ExpiresIn)> Exchange(HttpClient client, FcmServiceAccount account, CancellationToken ct)
    {
        var assertion = BuildAssertion(account);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion
        });

        using var response = await client.PostAsync(account.TokenUri, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"FCM token exchange failed ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var token = root.GetProperty("access_token").GetString()!;
        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        return (token, expiresIn);
    }


    static string BuildAssertion(FcmServiceAccount account)
    {
        using var key = RSA.Create();
        key.ImportFromPem(account.PrivateKeyPem);

        var now = DateTimeOffset.UtcNow;
        var header = """{"alg":"RS256","typ":"JWT"}""";
        var payload =
            $"{{\"iss\":\"{account.ClientEmail}\",\"scope\":\"{Scope}\"," +
            $"\"aud\":\"{account.TokenUri}\",\"iat\":{now.ToUnixTimeSeconds()},\"exp\":{now.AddHours(1).ToUnixTimeSeconds()}}}";

        var signingInput = $"{Encode(Encoding.UTF8.GetBytes(header))}.{Encode(Encoding.UTF8.GetBytes(payload))}";
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Encode(signature)}";
    }


    static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);
}
