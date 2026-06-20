using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.Extensions.Push;


public static class Registration
{
    /// <summary>
    /// Registers the push notification service. Configure providers, repository and interceptors via
    /// the builder. If no repository is supplied an in-memory store is used; if no manager is supplied
    /// the built-in <see cref="PushManager"/> is used.
    /// </summary>
    public static IServiceCollection AddPushNotifications(this IServiceCollection services, Action<IPushBuilder> configure)
    {
        var builder = new PushBuilder(services);
        configure(builder);

        // Sensible defaults so a bare configuration still works (great for tests/local dev).
        if (!builder.RepositorySet)
            services.TryAddSingleton<IPushRepository, InMemoryPushRepository>();

        if (!builder.ManagerSet)
            services.TryAddSingleton<IPushManager, PushManager>();

        services.AddMetrics();                          // ensures IMeterFactory is available
        services.TryAddSingleton<PushMetrics>();
        services.AddOptions<PushManagerOptions>();
        return services;
    }
}
