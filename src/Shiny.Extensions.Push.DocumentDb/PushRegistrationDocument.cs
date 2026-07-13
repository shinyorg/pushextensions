using System.Text.Json.Serialization;
using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.DocumentDb;


/// <summary>
/// Persistence shape for a <see cref="DeviceRegistration"/>. Kept separate from the domain record so the
/// store gets a proper mutable <c>Id</c> (required by Shiny.DocumentDb) without leaking storage concerns
/// into the core model. The document <c>Id</c> is <c>{Platform}|{DeviceToken}</c> so token-keyed
/// operations (remove/update on prune + rotation) are O(1) point lookups.
/// </summary>
public sealed class PushRegistrationDocument
{
    public string Id { get; set; } = "";
    public string DeviceToken { get; set; } = "";
    public DevicePlatform Platform { get; set; }
    public string? AppId { get; set; }
    public string? DeviceId { get; set; }
    public string? UserIdentifier { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<string> Topics { get; set; } = [];
    public string? Locale { get; set; }
    public string? AppVersion { get; set; }
    public PushEnvironment Environment { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public Dictionary<string, string>? Data { get; set; }


    public static string BuildId(DevicePlatform platform, string deviceToken) => $"{platform}|{deviceToken}";


    public static PushRegistrationDocument From(DeviceRegistration r) => new()
    {
        Id = BuildId(r.Platform, r.DeviceToken),
        DeviceToken = r.DeviceToken,
        Platform = r.Platform,
        AppId = r.AppId,
        DeviceId = r.DeviceId,
        UserIdentifier = r.UserIdentifier,
        Tags = [.. r.Tags],
        Topics = [.. r.Topics],
        Locale = r.Locale,
        AppVersion = r.AppVersion,
        Environment = r.Environment,
        ExpiresAt = r.ExpiresAt,
        Data = r.Data is null ? null : new Dictionary<string, string>(r.Data)
    };


    public DeviceRegistration ToRegistration() => new()
    {
        DeviceToken = this.DeviceToken,
        Platform = this.Platform,
        AppId = this.AppId,
        DeviceId = this.DeviceId,
        UserIdentifier = this.UserIdentifier,
        Tags = this.Tags,
        Topics = this.Topics,
        Locale = this.Locale,
        AppVersion = this.AppVersion,
        Environment = this.Environment,
        ExpiresAt = this.ExpiresAt,
        Data = this.Data
    };
}


/// <summary>Source-generated JSON metadata so the repository is AOT/trim-safe regardless of how the
/// host configured the document store's serializer.</summary>
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(PushRegistrationDocument))]
[JsonSerializable(typeof(List<PushRegistrationDocument>))]
public partial class PushDocumentJsonContext : JsonSerializerContext;
