using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Shiny.Extensions.Push.WebPush;


/// <summary>
/// Web Push message encryption — RFC 8291 (Message Encryption for Web Push) over the RFC 8188
/// <c>aes128gcm</c> content encoding. Uses only BCL crypto (ECDH P-256, HKDF-SHA256, AES-128-GCM), so it
/// is fully AOT/trim-safe with no third-party dependency. Verified against the RFC 8291 §5 test vector.
/// </summary>
static class WebPushCrypto
{
    const int RecordSize = 4096;

    static readonly byte[] KeyInfoPrefix = "WebPush: info\0"u8.ToArray();
    static readonly byte[] CekInfo = "Content-Encoding: aes128gcm\0"u8.ToArray();
    static readonly byte[] NonceInfo = "Content-Encoding: nonce\0"u8.ToArray();


    /// <summary>
    /// Encrypts <paramref name="payload"/> for a subscription, generating a fresh ephemeral key and salt.
    /// Returns the full <c>aes128gcm</c> body (header + ciphertext) ready to POST.
    /// </summary>
    public static byte[] Encrypt(byte[] payload, byte[] uaPublicKey, byte[] authSecret)
    {
        using var asKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var asPublic = ExportUncompressed(asKey);
        var salt = RandomNumberGenerator.GetBytes(16);
        return EncryptCore(payload, uaPublicKey, authSecret, asKey, asPublic, salt);
    }


    /// <summary>
    /// Deterministic core used by <see cref="Encrypt"/> and by tests (injecting the RFC 8291 example
    /// ephemeral key + salt to verify the exact ciphertext).
    /// </summary>
    internal static byte[] EncryptCore(
        byte[] payload,
        byte[] uaPublicKey,
        byte[] authSecret,
        ECDiffieHellman asKey,
        byte[] asPublic,
        byte[] salt)
    {
        // ECDH shared secret with the subscription's public key.
        using var ua = ImportPublic(uaPublicKey);
        var ecdhSecret = asKey.DeriveRawSecretAgreement(ua.PublicKey);

        // RFC 8291 §3.4 — derive the IKM for the content encoding.
        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, ecdhSecret, authSecret);
        var keyInfo = Concat(KeyInfoPrefix, uaPublicKey, asPublic);
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);

        // RFC 8188 — aes128gcm CEK + nonce.
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, CekInfo);
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, NonceInfo);

        // Single record: payload || 0x02 (last-record delimiter), no padding.
        var record = new byte[payload.Length + 1];
        payload.CopyTo(record, 0);
        record[^1] = 0x02;

        var ciphertext = new byte[record.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16))
            aes.Encrypt(nonce, record, ciphertext, tag);

        // Header: salt(16) || rs(uint32 BE) || idlen(1) || keyid(as_public).
        var header = new byte[16 + 4 + 1 + asPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), RecordSize);
        header[20] = (byte)asPublic.Length;
        asPublic.CopyTo(header, 21);

        var body = new byte[header.Length + ciphertext.Length + tag.Length];
        header.CopyTo(body, 0);
        ciphertext.CopyTo(body, header.Length);
        tag.CopyTo(body, header.Length + ciphertext.Length);
        return body;
    }


    /// <summary>Builds an ECDH key from a 65-byte uncompressed P-256 public point (the subscription's p256dh).</summary>
    internal static ECDiffieHellman ImportPublic(byte[] uncompressedPoint)
    {
        var p = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = uncompressedPoint[1..33],
                Y = uncompressedPoint[33..65]
            }
        };
        return ECDiffieHellman.Create(p);
    }


    /// <summary>Builds an ECDH key from raw private scalar + uncompressed public point (for the RFC test vector).</summary>
    internal static ECDiffieHellman ImportPrivate(byte[] privateScalar, byte[] uncompressedPoint)
    {
        var p = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateScalar,
            Q = new ECPoint { X = uncompressedPoint[1..33], Y = uncompressedPoint[33..65] }
        };
        return ECDiffieHellman.Create(p);
    }


    internal static byte[] ExportUncompressed(ECDiffieHellman key)
    {
        var p = key.ExportParameters(false);
        var bytes = new byte[65];
        bytes[0] = 0x04;
        p.Q.X!.CopyTo(bytes, 1);
        p.Q.Y!.CopyTo(bytes, 33);
        return bytes;
    }


    static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        var offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }
        return result;
    }
}
