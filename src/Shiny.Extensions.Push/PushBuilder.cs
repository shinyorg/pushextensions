using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.Extensions.Push;


sealed class PushBuilder : IPushBuilder
{
    public PushBuilder(IServiceCollection services) => this.Services = services;

    public IServiceCollection Services { get; }
    public bool RepositorySet { get; private set; }
    public bool ManagerSet { get; private set; }


    public IPushBuilder UseRepository<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IPushRepository
    {
        this.Services.RemoveAll<IPushRepository>();
        this.Services.AddSingleton<IPushRepository, T>();
        this.RepositorySet = true;
        return this;
    }


    public IPushBuilder AddProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IPushProvider
    {
        this.Services.AddSingleton<IPushProvider, T>();
        return this;
    }


    public IPushBuilder AddInterceptor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IPushInterceptor
    {
        this.Services.AddSingleton<IPushInterceptor, T>();
        return this;
    }


    public IPushBuilder UseManager<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IPushManager
    {
        this.Services.RemoveAll<IPushManager>();
        this.Services.AddSingleton<IPushManager, T>();
        this.ManagerSet = true;
        return this;
    }


    public IPushBuilder Configure(Action<PushManagerOptions> configure)
    {
        this.Services.Configure(configure);
        return this;
    }
}
