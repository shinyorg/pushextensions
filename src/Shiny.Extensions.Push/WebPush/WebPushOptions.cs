namespace Shiny.Extensions.Push.WebPush;


/// <summary>
/// Configuration for the Web Push provider. The VAPID keys are the standard web-push base64url-encoded
/// raw P-256 keys (e.g. produced by <c>web-push generate-vapid-keys</c>): the public key is the 65-byte
/// uncompressed point, the private key the 32-byte scalar.
/// </summary>
public class WebPushOptions
{
    /// <summary>VAPID public key (base64url, 65-byte uncompressed P-256 point).</summary>
    public string PublicKey { get; set; } = null!;

    /// <summary>VAPID private key (base64url, 32-byte P-256 scalar).</summary>
    public string PrivateKey { get; set; } = null!;

    /// <summary>VAPID contact, a <c>mailto:</c> or <c>https:</c> URI (the JWT <c>sub</c> claim).</summary>
    public string Subject { get; set; } = null!;

    /// <summary>Default time the push service should retain an undelivered message.</summary>
    public TimeSpan DefaultTimeToLive { get; set; } = TimeSpan.FromDays(28);
}
