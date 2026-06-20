using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Extensions.Push;
using Shiny.Extensions.Push.Apns;
using Shiny.Extensions.Push.Fcm;
using Shiny.Extensions.Push.WebPush;

// Native-AOT smoke test: wires the core + every transport with valid (generated) credentials, resolves
// the manager (which constructs all providers + their JWT/VAPID/token machinery), and runs a send. If
// this publishes and runs as a native binary with no trim/AOT warnings, the libraries are AOT-safe.

var services = new ServiceCollection();
services.AddLogging();

services.AddPushNotifications(push =>
{
    push.AddApns(o =>
    {
        o.TeamId = "TEAM123456";
        o.KeyId = "KEY1234567";
        o.BundleId = "com.example.app";
        o.PrivateKey = NewEcPem();
    });

    push.AddFcm(o => o.ServiceAccountJson = NewServiceAccountJson());

    push.AddWebPush(o =>
    {
        var (pub, priv) = NewVapidKeys();
        o.PublicKey = pub;
        o.PrivateKey = priv;
        o.Subject = "mailto:smoke@example.com";
    });
});

await using var provider = services.BuildServiceProvider();
var manager = provider.GetRequiredService<IPushManager>();

// Force construction of every IPushProvider (APNs/FCM/WebPush) — exercises key import paths.
var count = provider.GetServices<IPushProvider>().Count();
Console.WriteLine($"providers resolved: {count}");

await manager.RegisterDevice(new DeviceRegistration
{
    DeviceToken = "smoke-token",
    Platform = DevicePlatform.iOS
});

var result = await manager.Broadcast(new PushNotification { Title = "AOT", Message = "smoke" });
Console.WriteLine($"broadcast: total={result.Total} sent={result.Sent} failed={result.Failed}");
Console.WriteLine("AOT smoke test OK");
return 0;


static string NewEcPem()
{
    using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    return ec.ExportPkcs8PrivateKeyPem();
}

static string NewServiceAccountJson()
{
    using var rsa = RSA.Create(2048);
    var pem = rsa.ExportPkcs8PrivateKeyPem().ReplaceLineEndings("\\n");
    return $$"""
        {"type":"service_account","project_id":"smoke","client_email":"smoke@smoke.iam.gserviceaccount.com","private_key":"{{pem}}","token_uri":"https://oauth2.googleapis.com/token"}
        """;
}

static (string pub, string priv) NewVapidKeys()
{
    using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var p = ec.ExportParameters(true);
    var pub = new byte[65];
    pub[0] = 0x04;
    p.Q.X!.CopyTo(pub, 1);
    p.Q.Y!.CopyTo(pub, 33);
    return (Base64Url.EncodeToString(pub), Base64Url.EncodeToString(p.D!));
}
