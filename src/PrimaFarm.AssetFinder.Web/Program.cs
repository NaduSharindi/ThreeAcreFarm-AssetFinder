using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrimaFarm.AssetFinder.Web.Data;
using PrimaFarm.AssetFinder.Web.Infrastructure;
using PrimaFarm.AssetFinder.Web.Models;
using PrimaFarm.AssetFinder.Web.Services;
using PrimaFarm.AssetFinder.Web.Services.Assets;
using PrimaFarm.AssetFinder.Web.Services.Import;
using PrimaFarm.AssetFinder.Web.Services.Integration;
using PrimaFarm.AssetFinder.Web.Services.Security;
using PrimaFarm.AssetFinder.Web.Services.Soap;
using Serilog;
using SoapCore;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    var config = builder.Configuration;
    var isDev = builder.Environment.IsDevelopment();

    // ---------------- Logging & monitoring ----------------
    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.WebHost.ConfigureKestrel(o =>
    {
        o.AddServerHeader = false;                       // do not advertise the server stack
        o.Limits.MaxRequestBodySize = 25 * 1024 * 1024;  // largest upload is a 20 MB register workbook
    });

    // ---------------- Options ----------------
    builder.Services.Configure<SecurityOptions>(config.GetSection("Security"));
    builder.Services.Configure<StorageOptions>(config.GetSection("Storage"));
    builder.Services.Configure<ApiClientOptions>(config.GetSection("ApiClients"));
    builder.Services.Configure<FinanceOptions>(config.GetSection("Integration:Finance"));

    // ---------------- Data ----------------
    var connectionString = config.GetConnectionString("AssetDb")
        ?? throw new InvalidOperationException("Connection string 'AssetDb' is not configured.");
    builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString, sql =>
    {
        sql.UseCompatibilityLevel(130);      // SQL Server 2016+
        sql.EnableRetryOnFailure(3);         // transient network / failover errors
        sql.CommandTimeout(60);
    }));

    // ---------------- Security ----------------
    var sessionMinutes = config.GetValue("Security:SessionTimeoutMinutes", 30);
    var cookieSecure = isDev ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

    // Script and API callers get a status code, not a 302 to the HTML login page.
    static bool IsApiCall(HttpRequest r) =>
        r.Path.StartsWithSegments("/api") || r.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);

    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.LoginPath = "/Account/Login";
            o.LogoutPath = "/Account/Logout";
            o.AccessDeniedPath = "/Account/AccessDenied";
            o.Cookie.Name = "PrimaAssets.Auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = cookieSecure;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(sessionMinutes);
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx =>
            {
                if (IsApiCall(ctx.Request)) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                else ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = ctx =>
            {
                if (IsApiCall(ctx.Request)) ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                else ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            };
        })
        .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

    builder.Services.AddAuthorizationBuilder()
        // Deny by default: every endpoint needs a signed-in user unless it opts out with [AllowAnonymous].
        .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
        .AddPolicy(Policies.CanEdit, p => p.RequireRole(Roles.Admin, Roles.Staff))
        .AddPolicy(Policies.CanImport, p => p.RequireRole(Roles.Admin))
        .AddPolicy(Policies.CanViewAudit, p => p.RequireRole(Roles.Admin, Roles.Auditor))
        .AddPolicy(Policies.ApiRead, p => p
            .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, ApiKeyAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser());

    builder.Services.AddAntiforgery(o =>
    {
        o.Cookie.Name = "PrimaAssets.Csrf";
        o.Cookie.SecurePolicy = cookieSecure;
        o.Cookie.SameSite = SameSiteMode.Strict;
    });

    var dpKeyPath = config["DataProtection:KeyPath"];
    var dp = builder.Services.AddDataProtection().SetApplicationName("PrimaFarm.AssetFinder");
    if (!string.IsNullOrWhiteSpace(dpKeyPath))
        dp.PersistKeysToFileSystem(new DirectoryInfo(dpKeyPath));   // keeps sessions valid across IIS recycles / web farm

    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        o.AddPolicy("api", ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1) }));
    });

    if (!isDev)
        builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains = true; });

    // ---------------- MVC, Web API (JSON + XML), OpenAPI ----------------
    builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
        .AddXmlSerializerFormatters();
    builder.Services.AddProblemDetails();
    builder.Services.AddOpenApi();
    builder.Services.AddHttpContextAccessor();

    // ---------------- Application services ----------------
    builder.Services.AddScoped<AuditService>();
    builder.Services.AddScoped<AuthService>();
    builder.Services.AddScoped<AssetService>();
    builder.Services.AddScoped<AssetImportService>();
    builder.Services.AddSingleton<ApiKeyValidator>();

    // ---------------- Integration ----------------
    builder.Services.AddHttpClient<FinanceRegisterClient>((sp, c) =>
        {
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<FinanceOptions>>().Value.BaseUrl);
            c.Timeout = Timeout.InfiniteTimeSpan;            // timeouts are owned by the resilience pipeline
        })
        .AddStandardResilienceHandler(o =>
        {
            o.Retry.MaxRetryAttempts = 3;                     // safe for POST: requests carry an Idempotency-Key
            o.Retry.Delay = TimeSpan.FromMilliseconds(500);
            o.Retry.UseJitter = true;
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(config.GetValue("Integration:Finance:TimeoutSeconds", 30));
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
        });

    builder.Services.AddSoapCore();
    builder.Services.AddScoped<IAssetLookupSoapService, AssetLookupSoapService>();

    builder.Services.AddHealthChecks()
        .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

    // =====================================================================================
    var app = builder.Build();

    if (config.GetValue<bool>("Database:AutoDeploy"))
        await DbInitializer.ApplyScriptsAsync(connectionString, Path.Combine(AppContext.BaseDirectory, "Database"), app.Logger);

    if (isDev)
    {
        using var scope = app.Services.CreateScope();
        await DbInitializer.SeedDevUsersAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            config.GetSection("DevSeed:Users").Get<List<DevSeedUser>>() ?? [], app.Logger);
    }

    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
        ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
        : ctx.Request.Path.StartsWithSegments("/health") ? Serilog.Events.LogEventLevel.Verbose
        : Serilog.Events.LogEventLevel.Information);
    app.UseExceptionHandler("/error");   // generic message + correlation id; no stack traces to users

    if (!isDev) app.UseHsts();
    app.UseHttpsRedirection();
    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseStaticFiles();

    app.UseRouting();
    app.UseAuthentication();
    app.UseRateLimiter();

    // SOAP endpoint is middleware (not MVC), so guard it with the API-key scheme explicitly.
    // It must sit before UseAuthorization, otherwise the cookie fallback policy challenges first (302 to login).
    app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/soap"), soap => soap.Use(async (ctx, next) =>
    {
        var auth = await ctx.AuthenticateAsync(ApiKeyAuthenticationHandler.SchemeName);
        if (!auth.Succeeded) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        ctx.User = auth.Principal;
        await next();
    }));
    ((IApplicationBuilder)app).UseSoapEndpoint<IAssetLookupSoapService>("/soap/AssetLookup.asmx",
        new SoapEncoderOptions(), SoapSerializer.DataContractSerializer);

    app.UseAuthorization();

    app.MapControllerRoute("default", "{controller=Assets}/{action=Index}/{id?}");

    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = c => c.Tags.Contains("ready"),
        ResponseWriter = (ctx, report) =>
        {
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), ms = e.Value.Duration.TotalMilliseconds })
            }));
        }
    }).AllowAnonymous();

    if (isDev) app.MapOpenApi().AllowAnonymous();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

public static class Policies
{
    public const string CanEdit = "CanEdit";
    public const string CanImport = "CanImport";
    public const string CanViewAudit = "CanViewAudit";
    public const string ApiRead = "ApiRead";
}

// Exposed for integration tests (WebApplicationFactory).
public partial class Program;
