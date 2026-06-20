using System.Buffers.Text;
using System.Text;
using Shiny.Extensions.Push.WebPush;

namespace Shiny.Extensions.Push.Tests;


public class WebPushCryptoTests
{
    // RFC 8291 §5 worked example — proves the full ECDH + HKDF + AES128GCM + header assembly.
    [Fact]
    public void Encrypt_MatchesRfc8291Vector()
    {
        var plaintext = Encoding.UTF8.GetBytes("When I grow up, I want to be a watermelon");
        var auth = Base64Url.DecodeFromChars("BTBZMqHH6r4Tts7J_aSIgg");
        var uaPublic = Base64Url.DecodeFromChars("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");
        var asPrivate = Base64Url.DecodeFromChars("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw");
        var asPublic = Base64Url.DecodeFromChars("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        var salt = Base64Url.DecodeFromChars("DGv6ra1nlYgDCS1FRnbzlw");

        const string expected =
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

        using var asKey = WebPushCrypto.ImportPrivate(asPrivate, asPublic);
        var body = WebPushCrypto.EncryptCore(plaintext, uaPublic, auth, asKey, asPublic, salt);

        Assert.Equal(expected, Base64Url.EncodeToString(body));
    }


    [Fact]
    public void Encrypt_RandomKey_ProducesWellFormedHeader()
    {
        var auth = Base64Url.DecodeFromChars("BTBZMqHH6r4Tts7J_aSIgg");
        var uaPublic = Base64Url.DecodeFromChars("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");

        var body = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes("hello"), uaPublic, auth);

        // header = salt(16) + rs(4) + idlen(1) + as_public(65) = 86 bytes, then ciphertext + 16-byte tag.
        Assert.True(body.Length > 86 + 16);
        Assert.Equal(65, body[20]);                 // idlen
        Assert.Equal(0x04, body[21]);               // uncompressed point marker of the key id
    }
}
