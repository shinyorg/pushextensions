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
public sealed class ApnsJwtProvider : IDisposable
{
    static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(50); // Apple allows up to 60 min

    readonly ApnsOptions options;
    readonly ECDsa key;
    readonly Lock gate = new();
    string? cachedToken;
    DateTimeOffset issuedAt;


    public ApnsJwtProvider(ApnsOptions options)
    {
        this.options = options;
        this.key = ECDsa.Create();
        this.key.ImportFromPem(this.options.ResolvePrivateKeyPem());
    }


    /// <summary>Returns a valid bearer token, regenerating only when the cached one is stale.</summary>
    public string GetToken()
    {
        lock (this.gate)
        {
            if (this.cachedToken == null || (DateTimeOffset.UtcNow - this.issuedAt) >= RefreshAfter)
                this.cachedToken = this.Generate();

            return this.cachedToken;
        }
    }


    /// <summary>Drop the cached token so the next <see cref="GetToken"/> mints a fresh one (e.g. after a 403).</summary>
    public void Invalidate()
    {
        lock (this.gate)
            this.cachedToken = null;
    }


    string Generate()
    {
        var now = DateTimeOffset.UtcNow;
        this.issuedAt = now;

        var header = $"{{\"alg\":\"ES256\",\"kid\":\"{this.options.KeyId}\"}}";
        var payload = $"{{\"iss\":\"{this.options.TeamId}\",\"iat\":{now.ToUnixTimeSeconds()}}}";

        var signingInput = $"{Encode(Encoding.UTF8.GetBytes(header))}.{Encode(Encoding.UTF8.GetBytes(payload))}";

        // JWS ES256 requires the raw r||s concatenation (IEEE P1363), NOT a DER-encoded signature.
        var signature = this.key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );

        return $"{signingInput}.{Encode(signature)}";
    }


    static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    public void Dispose() => this.key.Dispose();
}
