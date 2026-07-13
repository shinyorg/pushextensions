using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Shiny.Extensions.Push;

namespace Shiny.Extensions.Push.Infrastructure;


sealed class PushBuilder(IServiceCollection services) : IPushBuilder
{
    public IServiceCollection Services { get; } = services;
    public bool RepositorySet { get; private set; }
    public bool ManagerSet { get; private set; }


    public IPushBuilder UseRepository<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IPushRepository
    {
        Services.RemoveAll<IPushRepository>();
        Services.AddSingleton<IPushRepository, T>();
        RepositorySet = true;
        return this;
    }


    public IPushBuilder AddProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IPushProvider
    {
        Services.AddSingleton<IPushProvider, T>();
        return this;
    }


    public IPushBuilder AddInterceptor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IPushInterceptor
    {
        Services.AddSingleton<IPushInterceptor, T>();
        return this;
    }


    public IPushBuilder AddEventReceiver<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IPushEventReceiver
    {
        Services.AddSingleton<IPushEventReceiver, T>();
        return this;
    }


    public IPushBuilder UseManager<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IPushManager
    {
        Services.RemoveAll<IPushManager>();
        Services.AddSingleton<IPushManager, T>();
        ManagerSet = true;
        return this;
    }


    public IPushBuilder Configure(Action<PushManagerOptions> configure)
    {
        Services.Configure(configure);
        return this;
    }
}
