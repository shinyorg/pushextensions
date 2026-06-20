using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shiny.Extensions.Push.Apns;

namespace Shiny.Extensions.Push.Tests;


public class ApnsJwtTests
{
    static (ApnsJwtProvider jwt, ECDsa verifyKey) Build()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportPkcs8PrivateKeyPem();

        var options = new ApnsOptions
        {
            TeamId = "TEAM123456",
            KeyId = "KEYABCDEFG",
            BundleId = "com.example.app",
            PrivateKey = pem
        };

        // Independent verification key from the same PEM.
        var verify = ECDsa.Create();
        verify.ImportFromPem(pem);

        return (new ApnsJwtProvider(options), verify);
    }


    [Fact]
    public void Token_HasThreeParts_AndValidClaims()
    {
        var (jwt, verify) = Build();
        using (verify)
        {
            var token = jwt.GetToken();
            var parts = token.Split('.');
            Assert.Equal(3, parts.Length);

            var header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])).RootElement;
            Assert.Equal("ES256", header.GetProperty("alg").GetString());
            Assert.Equal("KEYABCDEFG", header.GetProperty("kid").GetString());

            var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement;
            Assert.Equal("TEAM123456", payload.GetProperty("iss").GetString());
            Assert.True(payload.GetProperty("iat").GetInt64() > 0);
        }
    }


    [Fact]
    public void Signature_VerifiesWithPublicKey()
    {
        var (jwt, verify) = Build();
        using (verify)
        {
            var token = jwt.GetToken();
            var parts = token.Split('.');

            var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            var signature = Base64Url.DecodeFromChars(parts[2]);

            var valid = verify.VerifyData(
                signingInput,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation
            );
            Assert.True(valid);
        }
    }


    [Fact]
    public void Token_IsCached_BetweenCalls()
    {
        var (jwt, verify) = Build();
        using (verify)
            Assert.Same(jwt.GetToken(), jwt.GetToken());
    }
}
