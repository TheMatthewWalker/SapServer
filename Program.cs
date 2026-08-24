using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using SapServer.Configuration;
using SapServer.Middleware;
using SapServer.Services;
using SapServer.Services.Interfaces;
using SapServer.Services.Nco;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Logging — Serilog reads its configuration from appsettings.json "Serilog" section
// ---------------------------------------------------------------------------
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));

// ---------------------------------------------------------------------------
// Configuration binding
// ---------------------------------------------------------------------------
builder.Services.Configure<SapPoolOptions>(
    builder.Configuration.GetSection(SapPoolOptions.SectionName));

builder.Services.Configure<AuthOptions>(
    builder.Configuration.GetSection(AuthOptions.SectionName));

builder.Services.Configure<SapNcoOptions>(
    builder.Configuration.GetSection(SapNcoOptions.SectionName));

// Eagerly read auth options so we can configure JWT validation below
var authOpts = builder.Configuration
    .GetSection(AuthOptions.SectionName)
    .Get<AuthOptions>()
    ?? throw new InvalidOperationException("Auth configuration section is missing.");

// ---------------------------------------------------------------------------
// Authentication — JWT Bearer tokens issued by normanton-nexus.
// In Development with Auth:DevBypassAuth=true, a passthrough scheme is used
// instead so the API can be exercised without normanton-nexus.
// ---------------------------------------------------------------------------
bool devBypass = builder.Environment.IsDevelopment() && authOpts.DevBypassAuth;

if (devBypass)
{
    Console.WriteLine("*** DEV BYPASS AUTH IS ACTIVE — all requests are auto-authenticated ***");

    builder.Services
        .AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);

    builder.Services.AddScoped<IPermissionService, NullPermissionService>();
}
else
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(opts =>
        {
            opts.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey         = new SymmetricSecurityKey(
                                               Encoding.UTF8.GetBytes(authOpts.JwtSecret)),
                ValidateIssuer   = true,
                ValidIssuer      = authOpts.JwtIssuer,
                ValidateAudience = true,
                ValidAudience    = authOpts.JwtAudience,
                ValidateLifetime = true,
                ClockSkew        = TimeSpan.FromSeconds(30)
            };
        });

    // BypassPermissions skips the SQL lookup but still requires a valid JWT.
    // Use this until dbo.SapDepartmentPermissions is provisioned.
    if (authOpts.BypassPermissions)
        builder.Services.AddScoped<IPermissionService, NullPermissionService>();
    else
        builder.Services.AddScoped<IPermissionService, PermissionService>();
}

builder.Services.AddAuthorization();

// ---------------------------------------------------------------------------
// CORS — allow requests only from the normanton-nexus frontend origin(s)
// ---------------------------------------------------------------------------
builder.Services.AddCors(opts =>
    opts.AddDefaultPolicy(policy =>
        policy
            .WithOrigins(
                builder.Configuration
                    .GetSection("AllowedOrigins")
                    .Get<string[]>() ?? [])
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()));

// ---------------------------------------------------------------------------
// SAP Connection Pool — singleton so the STA threads live for the app lifetime
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<ISapConnectionPool, SapConnectionPool>();

// Background service that sends keep-alive pings and logs disconnected workers
builder.Services.AddHostedService<SapSessionMonitor>();

// ---------------------------------------------------------------------------
// SAP NCo rebuild spike — parallel to the COM pool above, not a replacement.
// Singleton so the registered destination configuration + NCo connection
// pool live for the app's lifetime. See CLAUDE.md's "SAP NCo Spike" section.
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<INcoRfcService, NcoRfcService>();

// ---------------------------------------------------------------------------
// Permission service cache
// ---------------------------------------------------------------------------
builder.Services.AddMemoryCache();

// ---------------------------------------------------------------------------
// ASP.NET Core infrastructure
// ---------------------------------------------------------------------------
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "SapServer API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new()
    {
        Name         = "Authorization",
        Type         = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme       = "bearer",
        BearerFormat = "JWT",
        In           = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description  = "Paste the JWT issued by sql2005-bridge."
    });
    c.AddSecurityRequirement(new()
    {
        {
            new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } },
            []
        }
    });
});

// ---------------------------------------------------------------------------
// Build & configure pipeline
// ---------------------------------------------------------------------------
var app = builder.Build();

// Global exception → JSON response mapping (must be first)
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Required for WebApplicationFactory<Program> (SapServer.Tests) to work with
// top-level statements — no behavior change.
public partial class Program { }
