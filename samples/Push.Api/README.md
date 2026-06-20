# Push.Api sample

A minimal ASP.NET Core API demonstrating `Shiny.Extensions.Push`, with a [Scalar](https://scalar.com)
UI for easy testing. It uses the in-memory repository and the **debug provider** (logs instead of
sending), so it runs with no credentials.

```bash
dotnet run --project samples/Push.Api
```

Then open **http://localhost:5xxx/scalar** (the root redirects there) and try the endpoints:

- `POST /devices` — register a device
- `POST /devices/unregister`
- `GET  /devices` — list registrations (debug)
- `POST /topics/{topic}/subscribe` · `POST /topics/{topic}/unsubscribe`
- `POST /send/user/{userId}` · `POST /send/tags` · `POST /send/topic/{topic}` · `POST /send/broadcast`

Sends are logged by the debug provider. To deliver for real, uncomment the `AddApns` / `AddFcm` /
`AddWebPush` / `UseDocumentDb` lines in `Program.cs` and supply credentials.
