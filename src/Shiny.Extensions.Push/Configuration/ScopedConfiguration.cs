using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Extensions.Push.Infrastructure;


/// <summary>
/// Resolves the <see cref="IPushConfigurationProvider"/> inside a fresh DI scope for a single send. The
/// provider is registered <b>scoped</b> (so it may depend on scoped services like an EF <c>DbContext</c>),
/// while the delivery providers that call this are singletons — this bridges the two. Only the config lookup
/// runs in the scope; the returned <see cref="PushConfiguration"/> is an immutable record used afterwards.
/// </summary>
static class ScopedConfiguration
{
    public static async ValueTask<PushConfiguration?> Resolve(IServiceScopeFactory scopeFactory, string appId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IPushConfigurationProvider>();
        return await provider.GetConfiguration(appId, cancellationToken).ConfigureAwait(false);
    }
}
