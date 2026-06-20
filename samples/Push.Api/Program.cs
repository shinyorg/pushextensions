using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using Shiny.Extensions.Push;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Accept/emit enums (DevicePlatform, PushEnvironment) as strings in JSON.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddPushNotifications(push =>
{
    // The debug provider logs instead of sending, so the sample runs with no credentials and claims
    // every platform. Swap in the real providers below once you have keys.
    push.AddProvider<DebugPushProvider>();

    // push.AddApns(o =>
    // {
    //     o.TeamId = "ABCDE12345";
    //     o.KeyId = "KEY1234567";
    //     o.BundleId = "com.example.app";
    //     o.PrivateKeyPath = "AuthKey_KEY1234567.p8";
    // });
    // push.AddFcm(o => o.ServiceAccountJsonPath = "firebase-service-account.json");
    // push.AddWebPush(o => { o.PublicKey = "<vapid-public>"; o.PrivateKey = "<vapid-private>"; o.Subject = "mailto:you@example.com"; });

    // Persistence defaults to in-memory; swap in DocumentDb for production:
    // push.UseDocumentDb(o => o.DatabaseProvider = new SqliteDatabaseProvider("Data Source=push.db"));
});

var app = builder.Build();

// OpenAPI document at /openapi/v1.json and the Scalar UI at /scalar — open the latter to try the API.
app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("Shiny.Extensions.Push Sample"));
app.MapGet("/", () => Results.Redirect("/scalar")).ExcludeFromDescription();

var devices = app.MapGroup("/devices").WithTags("Devices");

devices.MapPost("/", async (RegisterRequest req, IPushManager mgr) =>
{
    await mgr.RegisterDevice(new DeviceRegistration
    {
        DeviceToken = req.DeviceToken,
        Platform = req.Platform,
        UserIdentifier = req.UserIdentifier,
        AppId = req.AppId,
        Tags = req.Tags ?? [],
        Environment = req.Environment
    });
    return Results.NoContent();
})
.WithSummary("Register (or update) a device.");

devices.MapPost("/unregister", async (TokenRequest req, IPushManager mgr) =>
{
    await mgr.UnregisterDevice(req.DeviceToken, req.Platform);
    return Results.NoContent();
})
.WithSummary("Unregister a device.");

devices.MapGet("/", async (IPushRepository repo) =>
    Results.Ok(await repo.GetRegistrations(PushFilter.Broadcast)))
.WithSummary("List all registered devices (debug).");

var topics = app.MapGroup("/topics").WithTags("Topics");

topics.MapPost("/{topic}/subscribe", async (string topic, TokenRequest req, IPushManager mgr) =>
{
    await mgr.SubscribeToTopic(req.DeviceToken, req.Platform, topic);
    return Results.NoContent();
})
.WithSummary("Subscribe a device to a topic.");

topics.MapPost("/{topic}/unsubscribe", async (string topic, TokenRequest req, IPushManager mgr) =>
{
    await mgr.UnsubscribeFromTopic(req.DeviceToken, req.Platform, topic);
    return Results.NoContent();
})
.WithSummary("Unsubscribe a device from a topic.");

var send = app.MapGroup("/send").WithTags("Send");

send.MapPost("/user/{userId}", (string userId, SendRequest req, IPushManager mgr) =>
    mgr.SendToUser(userId, req.ToNotification()))
.WithSummary("Send to all of a user's devices.");

send.MapPost("/tags", (TagSendRequest req, IPushManager mgr) =>
    mgr.SendToTags(req.Tags, req.ToNotification(), req.MatchAll ? TagMatch.All : TagMatch.Any))
.WithSummary("Send to devices matching tags.");

send.MapPost("/topic/{topic}", (string topic, SendRequest req, IPushManager mgr) =>
    mgr.SendToTopic(topic, req.ToNotification()))
.WithSummary("Send to a topic.");

send.MapPost("/broadcast", (SendRequest req, IPushManager mgr) =>
    mgr.Broadcast(req.ToNotification()))
.WithSummary("Broadcast to every device.");

app.Run();


// ----- request/response contracts -----

record RegisterRequest(
    string DeviceToken,
    DevicePlatform Platform,
    string? UserIdentifier = null,
    string[]? Tags = null,
    string? AppId = null,
    PushEnvironment Environment = PushEnvironment.Production);

record TokenRequest(string DeviceToken, DevicePlatform Platform);

record SendRequest(
    string? Title = null,
    string? Message = null,
    string? DeepLink = null,
    int? Badge = null,
    Dictionary<string, string>? Data = null)
{
    public PushNotification ToNotification() => new()
    {
        Title = this.Title,
        Message = this.Message,
        DeepLink = this.DeepLink,
        Badge = this.Badge,
        Data = this.Data ?? new Dictionary<string, string>()
    };
}

record TagSendRequest(
    string[] Tags,
    bool MatchAll = false,
    string? Title = null,
    string? Message = null,
    string? DeepLink = null)
{
    public PushNotification ToNotification() => new()
    {
        Title = this.Title,
        Message = this.Message,
        DeepLink = this.DeepLink
    };
}

public partial class Program;
