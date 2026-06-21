using System.ComponentModel.DataAnnotations;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Apns;


/// <summary>
/// Configuration for the direct APNs provider using token-based authentication (a .p8 key). One set of
/// options corresponds to one app/bundle id. For multiple apps, register multiple providers (see CLAUDE.md).
/// </summary>
public class ApnsOptions
{
    /// <summary>Your 10-character Apple Developer Team ID (the JWT "iss").</summary>
    [Required]
    public string TeamId { get; set; } = null!;

    /// <summary>The 10-character Key ID of the .p8 auth key (the JWT "kid").</summary>
    [Required]
    public string KeyId { get; set; } = null!;

    /// <summary>The app bundle id, used as the default <c>apns-topic</c>.</summary>
    [Required]
    public string BundleId { get; set; } = null!;

    /// <summary>The PEM contents of the .p8 private key. Provide this OR <see cref="PrivateKeyPath"/>.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>Path to the .p8 private key file. Provide this OR <see cref="PrivateKey"/>.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>
    /// When set, forces every push through this environment regardless of the registration's
    /// <see cref="DeviceRegistration.Environment"/>. Leave null to honour each registration (recommended).
    /// </summary>
    public PushEnvironment? ForceEnvironment { get; set; }


    internal string ResolvePrivateKeyPem()
    {
        if (!string.IsNullOrWhiteSpace(this.PrivateKey))
            return this.PrivateKey!;

        if (!string.IsNullOrWhiteSpace(this.PrivateKeyPath))
            return File.ReadAllText(this.PrivateKeyPath!);

        throw new InvalidOperationException("APNs requires either PrivateKey (PEM) or PrivateKeyPath to be set.");
    }
}
