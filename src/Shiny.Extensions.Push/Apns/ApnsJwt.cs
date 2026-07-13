using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Shiny.Extensions.Push.Apns;


/// <summary>
/// Shared ES256 minting for the APNs provider token, used by both the keyed <see cref="ApnsJwtProvider"/>
/// (which caches one key for one app) and the multi-tenant path (which mints transiently per tenant). AOT-safe:
/// hand-built JWT, P1363 signature — no <c>System.IdentityModel</c>.
/// </summary>
static class ApnsJwt
{
    /// <summary>Signs a provider JWT for <paramref name="options"/> with an already-imported <paramref name="key"/>.</summary>
    public static string Sign(ApnsOptions options, ECDsa key, DateTimeOffset now)
    {
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


    /// <summary>Imports the key from <paramref name="options"/>, signs a fresh token, and disposes the key.</summary>
    public static string Generate(ApnsOptions options)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(options.ResolvePrivateKeyPem());
        return Sign(options, key, DateTimeOffset.UtcNow);
    }


    static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);
}
