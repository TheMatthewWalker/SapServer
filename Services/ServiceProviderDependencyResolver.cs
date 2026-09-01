using System.Web.Http.Dependencies;
using Microsoft.Extensions.DependencyInjection;

namespace SapServer.Services;

/// <summary>
/// Bridges Microsoft.Extensions.DependencyInjection (used everywhere else in
/// this app — the same IServiceCollection/IOptions/ILogger&lt;T&gt; model
/// Program.cs used under ASP.NET Core) into System.Web.Http's own
/// IDependencyResolver, which Web API 2 has no built-in DI container to
/// satisfy on its own. Controllers' constructor-injection patterns are
/// otherwise unchanged from the ASP.NET Core version.
/// </summary>
public sealed class ServiceProviderDependencyResolver : IDependencyResolver
{
    private readonly IServiceProvider _provider;

    public ServiceProviderDependencyResolver(IServiceProvider provider) => _provider = provider;

    public IDependencyScope BeginScope() =>
        new ServiceProviderDependencyResolver(_provider.CreateScope().ServiceProvider);

    public object? GetService(Type serviceType) => _provider.GetService(serviceType);

    public IEnumerable<object> GetServices(Type serviceType) =>
        _provider.GetServices(serviceType)!;

    public void Dispose() { }
}
