using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Extensions.Push;


/// <summary>
/// Registers the <see cref="IPushConfigurationProvider"/> that supplies per-app credentials at send time to
/// the configuration-driven transport overloads (<c>AddApns()</c>, <c>AddFcm()</c>, <c>AddWebPush()</c>,
/// <c>AddWns()</c>). Pair this with those no-argument overloads; the static <c>AddApns(o => …)</c> overloads
/// remain the baked-in-config path and don't use this.
/// </summary>
public static class PushConfigurationRegistration
{
    /// <summary>
    /// Registers <typeparamref name="TProvider"/> as a <b>scoped</b> <see cref="IPushConfigurationProvider"/>.
    /// Scoped is the recommended lifetime so the provider can depend on scoped services (e.g. an EF
    /// <c>DbContext</c>); the singleton delivery providers resolve it inside a fresh scope per send. Call this
    /// once — the configuration-driven transport overloads share the single registration.
    /// </summary>
    public static IPushBuilder UsePushConfiguration<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>(this IPushBuilder builder)
        where TProvider : class, IPushConfigurationProvider
    {
        builder.Services.AddScoped<IPushConfigurationProvider, TProvider>();
        return builder;
    }
}
