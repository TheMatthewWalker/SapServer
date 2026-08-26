using System.Text;
using System.Web.Hosting;
using System.Web.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Web.Cors;
using Microsoft.Owin;
using Microsoft.Owin.Cors;
using Microsoft.Owin.Security.Jwt;
using Owin;
using Serilog;
using SapServer.Configuration;
using SapServer.Middleware;
using SapServer.Services;
using SapServer.Services.Interfaces;
using SapServer.Services.Nco;

[assembly: OwinStartup(typeof(SapServer.Startup))]

namespace SapServer;

/// <summary>
/// OWIN composition root — the .NET Framework 4.8 + IIS-hosted replacement
/// for Program.cs's ASP.NET Core minimal-hosting-model pipeline. Same
/// responsibilities (config binding, DI registration, auth, CORS, exception
/// handling, routing), different hosting APIs:
///   - Kestrel's generic host              -> Microsoft.Owin.Host.SystemWeb (IIS)
///   - builder.Services (IServiceCollection) -> a ServiceCollection built and
///     resolved manually, bridged into Web API 2 via ServiceProviderDependencyResolver
///   - app.UseMiddleware&lt;T&gt;()              -> app.Use&lt;T&gt;() (OWIN)
///   - AddAuthentication().AddJwtBearer()   -> app.UseJwtBearerAuthentication() (Katana)
///   - IHostedService/BackgroundService     -> SapSessionMonitor.Start()/Stop(), wired
///     to OWIN's "host.OnAppDisposing" token explicitly (no generic host lifecycle)
///
/// UNVERIFIED: this file cannot be executed in this sandbox (no Windows, no
/// IIS, no Mono) — the OWIN/System.Web.Http package wiring below is written
/// from documented Katana/Web-API-2 patterns, not exercised against a real
/// IIS site. Validate end to end (auth, CORS, routing, Swagger) on a real
/// Windows dev machine before trusting this in production.
/// </summary>
public class Startup
{
    public void Configuration(IAppBuilder app) => ConfigurePipeline(app, BuildConfiguration());

    /// <summary>
    /// The real pipeline-building logic, factored out of Configuration(IAppBuilder)
    /// so SapServer.Tests can drive the exact same OWIN pipeline (JWT auth, CORS,
    /// exception handling, Web API routing) through Microsoft.Owin.Testing.TestServer
    /// instead of duplicating it — see SapServerTestFactory. <paramref name="overrideServices"/>
    /// runs after the normal DI registrations, immediately before BuildServiceProvider(),
    /// so a test's Mock&lt;ISapConnectionPool&gt;.Object registered there wins over the
    /// real NcoConnectionPool for that resolution (last registration wins in
    /// Microsoft.Extensions.DependencyInjection) — mirrors the old
    /// WebApplicationFactory&lt;Program&gt;.ConfigureWebHost + RemoveAll&lt;T&gt;() pattern's
    /// intent without needing an equivalent RemoveAll (there is none here).
    /// </summary>
    public static void ConfigurePipeline(
        IAppBuilder app, IConfiguration configuration, Action<IServiceCollection>? overrideServices = null,
        bool startSessionMonitor = true)
    {
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger();

        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddSerilog(dispose: true));

        services.Configure<SapNcoOptions>(configuration.GetSection(SapNcoOptions.SectionName));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));

        var authOpts = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>()
            ?? throw new InvalidOperationException("Auth configuration section is missing.");
        var ncoOpts = configuration.GetSection(SapNcoOptions.SectionName).Get<SapNcoOptions>()
            ?? throw new InvalidOperationException("SapNco configuration section is missing.");

        bool isDevelopment = string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development",
            StringComparison.OrdinalIgnoreCase);
        bool devBypass = isDevelopment && authOpts.DevBypassAuth;

        services.AddSingleton<ISapConnectionPool, NcoConnectionPool>();
        services.AddSingleton(ncoOpts);
        services.AddSingleton<SapSessionMonitor>();
        services.AddMemoryCache();

        if (devBypass || authOpts.BypassPermissions)
            services.AddScoped<IPermissionService, NullPermissionService>();
        else
            services.AddScoped<IPermissionService, PermissionService>();

        overrideServices?.Invoke(services);

        var provider = services.BuildServiceProvider();

        // Exception handling first — must wrap everything below it, same
        // ordering ExceptionHandlingMiddleware had as the first entry in
        // Program.cs's pipeline.
        var exceptionLogger = provider.GetRequiredService<ILogger<ExceptionHandlingMiddleware>>();
        app.Use<ExceptionHandlingMiddleware>(exceptionLogger);

        ConfigureCors(app, configuration);

        if (devBypass)
        {
            Console.WriteLine("*** DEV BYPASS AUTH IS ACTIVE — all requests are auto-authenticated ***");
            app.Use<DevAuthMiddleware>();
        }
        else
        {
            app.UseJwtBearerAuthentication(new JwtBearerAuthenticationOptions
            {
                AuthenticationMode = Microsoft.Owin.Security.AuthenticationMode.Active,
                AllowedAudiences = new[] { authOpts.JwtAudience },
                IssuerSecurityKeyProviders = new IIssuerSecurityKeyProvider[]
                {
                    new SymmetricKeyIssuerSecurityKeyProvider(
                        authOpts.JwtIssuer, Encoding.UTF8.GetBytes(authOpts.JwtSecret))
                }
            });
        }

        var httpConfig = new HttpConfiguration
        {
            DependencyResolver = new ServiceProviderDependencyResolver(provider)
        };
        httpConfig.MapHttpAttributeRoutes();

        var monitor = provider.GetRequiredService<SapSessionMonitor>();
        if (startSessionMonitor)
            monitor.Start();

        var onAppDisposing = app.Properties.TryGetValue("host.OnAppDisposing", out var raw) && raw is CancellationToken token
            ? token
            : CancellationToken.None;
        onAppDisposing.Register(() =>
        {
            if (startSessionMonitor)
                monitor.Stop();
            Log.CloseAndFlush();
        });

        app.UseWebApi(httpConfig);
    }

    private static void ConfigureCors(IAppBuilder app, IConfiguration configuration)
    {
        var allowedOrigins = configuration.GetSection("AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();

        var policy = new CorsPolicy
        {
            AllowAnyMethod      = true,
            AllowAnyHeader      = true,
            SupportsCredentials = true,
        };
        foreach (var origin in allowedOrigins)
            policy.Origins.Add(origin);

        app.UseCors(new CorsOptions
        {
            PolicyProvider = new CorsPolicyProvider { PolicyResolver = _ => Task.FromResult(policy) }
        });
    }

    /// <summary>
    /// appsettings.json + appsettings.{ASPNETCORE_ENVIRONMENT}.json + env vars —
    /// the same layering WebApplicationBuilder.CreateBuilder(args) did
    /// automatically; IIS hosting means there's no args[]/content-root
    /// auto-detection, so the base path is resolved via HostingEnvironment
    /// explicitly instead.
    /// </summary>
    private static IConfiguration BuildConfiguration()
    {
        string basePath = HostingEnvironment.IsHosted
            ? HostingEnvironment.MapPath("~/")!
            : AppDomain.CurrentDomain.BaseDirectory;

        string environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

        return new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
    }
}
