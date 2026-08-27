using System.IO;
using System.Text;
using System.Web.Hosting;
using System.Web.Http;
using System.Web.Http.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Web.Cors;
using Microsoft.Owin;
using Microsoft.Owin.Cors;
using Microsoft.Owin.Extensions;
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
        // The File sink's path is deliberately set here in code rather than
        // via the "Serilog:WriteTo" JSON section (see appsettings.example.json,
        // which only configures Console there) - Serilog.Sinks.File resolves a
        // relative path against Environment.CurrentDirectory, which is not
        // reliably the site root under IIS (see ResolveBasePath()'s own
        // comment). Gated on HostingEnvironment.IsHosted, same as
        // BuildConfiguration()'s basePath, so SapServer.Tests (which runs
        // ConfigurePipeline directly via TestServer, never IIS-hosted) never
        // writes a log file into its own bin output on every test run.
        var loggerConfig = new LoggerConfiguration().ReadFrom.Configuration(configuration);
        if (HostingEnvironment.IsHosted)
        {
            string logsPath = Path.Combine(ResolveBasePath(), "logs", "sapserver-.log");
            loggerConfig = loggerConfig.WriteTo.File(
                logsPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }
        Log.Logger = loggerConfig.CreateLogger();

        // First line in every log file/session on purpose - the whole point
        // is to make it obvious at a glance (not just from appsettings.json,
        // which can be easy to lose track of on a dev machine juggling a
        // local test instance alongside production) which environment this
        // process actually launched under, and where its site root resolved
        // to under IIS hosting.
        Log.Information(
            "SapServer starting - ASPNETCORE_ENVIRONMENT={Environment}, IIS-hosted={IsHosted}, BasePath={BasePath}",
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            HostingEnvironment.IsHosted,
            ResolveBasePath());

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

        // Web API 2's DefaultHttpControllerActivator calls
        // IDependencyResolver.GetService(controllerType) and only falls back
        // to Activator.CreateInstance(controllerType) — a PARAMETERLESS
        // constructor — when that returns null. None of our controllers have
        // one (they all take ISapConnectionPool/IPermissionService/ILogger<T>
        // via constructor injection), so without an explicit registration
        // here every single controller construction failed with
        // "Type '...' does not have a default constructor", surfaced as a
        // generic Web-API-internal 500 for every request regardless of route
        // or auth outcome. Registering every IHttpController-implementing
        // type found in this assembly (rather than hand-listing each of the
        // ~15 domain controllers) means a newly added controller is covered
        // automatically.
        foreach (var controllerType in typeof(Startup).Assembly.GetTypes()
                     .Where(t => typeof(IHttpController).IsAssignableFrom(t) && !t.IsAbstract))
        {
            services.AddTransient(controllerType);
        }

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

        // Katana's own guidance for Microsoft.Owin.Host.SystemWeb: mark the
        // end of authentication middleware with the IIS-native pipeline
        // stage it corresponds to, so IntegratedPipelineContext knows where
        // this segment of the OWIN chain maps onto IIS's own pipeline
        // instead of treating the whole app as one undifferentiated block.
        // Missing this is a documented cause of IntegratedPipelineContext
        // completion-bookkeeping bugs - confirmed for real against a live
        // Production deploy under real JWT auth: intermittent
        // System.InvalidOperationException ("Operation is not valid due to
        // the current state of the object") from
        // IntegratedPipelineContext.PushLastObjects, with
        // AuthenticationMiddleware`1 directly in the stack, on requests that
        // otherwise completed normally through Web API. The earlier
        // <httpErrors existingResponse="PassThrough"/> fix (web.config) was
        // necessary but not sufficient - that stops IIS from overwriting an
        // already-written error response; this stops the underlying
        // exception from being thrown in the first place.
        app.UseStageMarker(PipelineStage.Authenticate);

        var httpConfig = new HttpConfiguration
        {
            DependencyResolver = new ServiceProviderDependencyResolver(provider)
        };
        httpConfig.MapHttpAttributeRoutes();

        // Catch-all conventional route, checked only after every attribute
        // route above has already failed to match — guarantees literally
        // every request Web API's routing sees matches SOMETHING. Without
        // this, a request that matches zero attribute routes at all crashes
        // the OWIN pipeline under real IIS hosting instead of cleanly
        // 404ing (System.InvalidOperationException from
        // IntegratedPipelineContext.PushLastObjects) — confirmed for real,
        // more than once, for different unmatched URLs (GET /health before
        // that route existed, then POST /api/production/label, a route this
        // app has never implemented). See NotFoundController's doc comment.
        httpConfig.Routes.MapHttpRoute(
            name: "Catchall",
            routeTemplate: "{*url}",
            defaults: new { controller = "NotFound" });

        // Web API 2's built-in JsonMediaTypeFormatter (System.Net.Http.Formatting,
        // wrapping Newtonsoft.Json) defaults to PascalCase property names -
        // confirmed for real against a live deploy: GET /api/rfc/status
        // returned {"Success":true,"Data":[],"Error":null} instead of the
        // documented {success,data,error} envelope every caller (Normanton-
        // Nexus's routes/*.js included) actually checks. This is a separate
        // serializer from SapExceptionMapper's manually-built error envelope
        // (System.Text.Json with explicit camelCase JsonOptions - see
        // WebApiExceptionHandler), which is why only the error path ever came
        // out lowercase: every ordinary Ok(...)/Content(...) success response
        // across every controller was silently treated as a failure by any
        // consumer checking response.success. Invisible to every test in this
        // suite too - none of them deserialize the raw JSON body; they all
        // assert on the C# ApiResponse<T> object directly via reflection
        // (ControllerTestHelpers.AssertOk/etc.), never the actual wire format.
        // ProcessDictionaryKeys must be forced off - CamelCasePropertyNamesContractResolver
        // defaults it on, which would also camelCase Dictionary<string,object?>
        // keys. RfcResponse.Parameters/Tables are exactly that shape, keyed by
        // literal SAP field/table names (e.g. "STATUS", "MATNR") that callers
        // depend on verbatim - only declared C# property names (Success/Data/
        // Error, etc.) should be camelCased, confirmed necessary by a real
        // test failure (a dictionary lookup on "STATUS" started missing once
        // the resolver's default silently lowercased that key too).
        httpConfig.Formatters.JsonFormatter.SerializerSettings.ContractResolver =
            new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver
            {
                NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy
                {
                    ProcessDictionaryKeys = false,
                    OverrideSpecifiedNames = true
                }
            };

        // Web API 2's HttpServer catches any exception raised during
        // controller construction/filters/action execution ITSELF and
        // converts it to a response before it can ever reach
        // ExceptionHandlingMiddleware above — see WebApiExceptionHandler's
        // doc comment. Without this, every SapPermissionException/
        // SapConnectionException/etc. thrown from inside a controller (i.e.
        // almost all of them) was silently replaced by Web API's own generic
        // '{"Message":"An error has occurred."}' 500.
        httpConfig.Services.Replace(
            typeof(System.Web.Http.ExceptionHandling.IExceptionHandler),
            new WebApiExceptionHandler(provider.GetRequiredService<ILogger<WebApiExceptionHandler>>()));

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
        string basePath = ResolveBasePath();

        string environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

        return new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
    }

    /// <summary>
    /// The site's physical root under real IIS hosting, or the test/dev
    /// AppDomain's own output directory otherwise — shared by
    /// BuildConfiguration() (above) and ConfigurePipeline()'s Serilog file
    /// sink (below), both of which need an absolute path rather than trusting
    /// Environment.CurrentDirectory: under IIS, a process's actual working
    /// directory is whatever w3wp.exe/WAS started it with (commonly
    /// %windir%\System32\inetsrv), NOT the site root, so any relative path
    /// resolved the ordinary way would land in the wrong place - or somewhere
    /// the app pool identity has no write permission at all.
    /// </summary>
    private static string ResolveBasePath() =>
        HostingEnvironment.IsHosted
            ? HostingEnvironment.MapPath("~/")!
            : AppDomain.CurrentDomain.BaseDirectory;
}
