using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Shiny.Extensions.Push.Apns;


/// <summary>
/// Produces and caches the APNs provider authentication token (an ES256 JWT). Apple requires the same
/// token be reused and only refreshed periodically (it rejects tokens regenerated too frequently), so
/// we mint at most one token per <see cref="RefreshAfter"/> window and reuse it across all sends and
/// both environments.
/// </summary>
public sealed class ApnsJwtProvider(ApnsOptions options) : IDisposable
{
    static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(50); // Apple allows up to 60 min

    readonly ECDsa key = ImportKey(options.ResolvePrivateKeyPem());
    readonly Lock gate = new();
    string? cachedToken;
    DateTimeOffset issuedAt;


    static ECDsa ImportKey(string pem)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }


    /// <summary>Returns a valid bearer token, regenerating only when the cached one is stale.</summary>
    public string GetToken()
    {
        lock (gate)
        {
            if (cachedToken == null || (DateTimeOffset.UtcNow - issuedAt) >= RefreshAfter)
                cachedToken = Generate();

            return cachedToken;
        }
    }


    /// <summary>Drop the cached token so the next <see cref="GetToken"/> mints a fresh one (e.g. after a 403).</summary>
    public void Invalidate()
    {
        lock (gate)
            cachedToken = null;
    }


    string Generate()
    {
        var now = DateTimeOffset.UtcNow;
        issuedAt = now;

        var header = $"{{\"alg\":\"ES256\",\"kid\":\"{options.KeyId}\"}}";
        var payload = $"{{\"iss\":\"{options.TeamId}\",\"iat\":{now.ToUnixTimeSeconds()}}}";

        var signingInput = $"{Encode(Encoding.UTF8.GetBytes(header))}.{Encode(Encoding.UTF8.GetBytes(payload))}";

        // JWS ES256 requires the raw r||s concatenation (IEEE P1363), NOT a DER-encoded signature.
        var signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );

        return $"{signingInput}.{Encode(signature)}";
    }


    static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    public void Dispose() => key.Dispose();
}
