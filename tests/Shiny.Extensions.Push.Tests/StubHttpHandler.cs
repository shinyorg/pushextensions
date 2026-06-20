namespace Shiny.Extensions.Push.Tests;


/// <summary>A test message handler that returns canned responses based on the request URI.</summary>
public sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        this.Requests.Add(request);
        return Task.FromResult(responder(request));
    }

    public static HttpResponseMessage Json(System.Net.HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
