namespace Shiny.Extensions.Push.Wns;


/// <summary>
/// Configuration for the WNS (Windows Notification Service) provider, using the modern Windows App SDK /
/// Microsoft Entra (Azure AD) authentication model. Register an Entra app, grant it the WNS permission, and
/// supply its <see cref="TenantId"/>, <see cref="ClientId"/> and <see cref="ClientSecret"/>. One set of
/// options corresponds to one app; for multiple apps register multiple keyed providers.
/// </summary>
public class WnsOptions
{
    /// <summary>The Entra (Azure AD) directory (tenant) id the app registration lives in.</summary>
    public string? TenantId { get; set; }

    /// <summary>The Entra application (client) id.</summary>
    public string? ClientId { get; set; }

    /// <summary>A client secret for the app registration.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Overrides the OAuth2 token endpoint. Defaults to the public cloud
    /// (<c>https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token</c>); set this for a national/
    /// sovereign cloud (e.g. <c>login.microsoftonline.us</c>).
    /// </summary>
    public string? TokenEndpoint { get; set; }


    internal string ResolveTokenEndpoint() =>
        string.IsNullOrWhiteSpace(this.TokenEndpoint)
            ? $"https://login.microsoftonline.com/{this.TenantId}/oauth2/v2.0/token"
            : this.TokenEndpoint!;
}
