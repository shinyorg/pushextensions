using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Shiny.Extensions.Push.WebPush;


/// <summary>
/// VAPID (RFC 8292) application-server authentication. Holds the P-256 signing key (imported from raw
/// base64url, the standard web-push key format) and produces the per-origin <c>Authorization: vapid</c>
/// header value. AOT-safe: manual ES256 JWT, no PEM/IdentityModel. The static overload mints without holding
/// a key (used by the multi-tenant path, which resolves keys per tenant).
/// </summary>
public sealed class WebPushVapid(string publicKeyBase64Url, string privateKeyBase64Url, string subject) : IDisposable
{
    readonly ECDsa key = ImportKey(publicKeyBase64Url, privateKeyBase64Url);
    readonly string publicKeyB64 = publicKeyBase64Url;


    static ECDsa ImportKey(string publicKeyBase64Url, string privateKeyBase64Url)
    {
        var pub = Base64Url.DecodeFromChars(publicKeyBase64Url);   // 65-byte uncompressed point
        var d = Base64Url.DecodeFromChars(privateKeyBase64Url);    // 32-byte scalar
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = d,
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] }
        });
    }


    /// <summary>Builds the <c>Authorization</c> header value for the given push endpoint origin.</summary>
    public string CreateAuthorizationHeader(Uri endpoint, DateTimeOffset now)
        => Build(publicKeyB64, key, subject, endpoint, now);


    /// <summary>
    /// Builds the <c>Authorization</c> header from raw options, importing (and disposing) the key transiently.
    /// Used by the multi-tenant provider, which resolves a tenant's VAPID keys per send.
    /// </summary>
    public static string CreateAuthorizationHeader(WebPushOptions options, Uri endpoint, DateTimeOffset now)
    {
        using var key = ImportKey(options.PublicKey, options.PrivateKey);
        return Build(options.PublicKey, key, options.Subject, endpoint, now);
    }


    static string Build(string publicKeyB64, ECDsa key, string subject, Uri endpoint, DateTimeOffset now)
    {
        var aud = $"{endpoint.Scheme}://{endpoint.Authority}";
        var exp = now.AddHours(12).ToUnixTimeSeconds();   // RFC 8292: <= 24h

        var header = """{"typ":"JWT","alg":"ES256"}""";
        var payload = $"{{\"aud\":\"{aud}\",\"exp\":{exp},\"sub\":\"{subject}\"}}";

        var signingInput = $"{Encode(Encoding.UTF8.GetBytes(header))}.{Encode(Encoding.UTF8.GetBytes(payload))}";
        var signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
        var jwt = $"{signingInput}.{Encode(signature)}";

        return $"vapid t={jwt}, k={publicKeyB64}";
    }


    static string Encode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    public void Dispose() => key.Dispose();
}
