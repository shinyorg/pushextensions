using System.Text.Json;

namespace Shiny.Extensions.Push.Fcm;


/// <summary>
/// Configuration for the FCM (HTTP v1) provider. Supply a Google service-account key — either the JSON
/// contents (<see cref="ServiceAccountJson"/>) or a path to it (<see cref="ServiceAccountJsonPath"/>).
/// One set of options corresponds to one Firebase project. For multiple apps, register multiple keyed
/// providers.
/// </summary>
public class FcmOptions
{
    /// <summary>The service-account JSON contents.</summary>
    public string? ServiceAccountJson { get; set; }

    /// <summary>Path to the service-account JSON file.</summary>
    public string? ServiceAccountJsonPath { get; set; }

    /// <summary>Overrides the project id parsed from the service account (rarely needed).</summary>
    public string? ProjectId { get; set; }


    internal FcmServiceAccount ResolveServiceAccount()
    {
        var json = this.ServiceAccountJson;
        if (string.IsNullOrWhiteSpace(json))
        {
            if (string.IsNullOrWhiteSpace(this.ServiceAccountJsonPath))
                throw new InvalidOperationException("FCM requires ServiceAccountJson or ServiceAccountJsonPath.");
            json = File.ReadAllText(this.ServiceAccountJsonPath!);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string Get(string name) => root.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

        var projectId = this.ProjectId ?? Get("project_id");
        var clientEmail = Get("client_email");
        var privateKey = Get("private_key");
        var tokenUri = Get("token_uri");
        if (string.IsNullOrWhiteSpace(tokenUri))
            tokenUri = "https://oauth2.googleapis.com/token";

        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(clientEmail) || string.IsNullOrWhiteSpace(privateKey))
            throw new InvalidOperationException("FCM service account is missing project_id, client_email, or private_key.");

        return new FcmServiceAccount(projectId, clientEmail, privateKey, tokenUri);
    }
}


/// <summary>Parsed Google service-account fields needed to mint access tokens.</summary>
internal sealed record FcmServiceAccount(string ProjectId, string ClientEmail, string PrivateKeyPem, string TokenUri);
