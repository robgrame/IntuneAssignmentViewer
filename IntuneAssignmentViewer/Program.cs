using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Microsoft.Graph;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Azure.Identity;
using IntuneAssignmentViewer.Components;
using IntuneAssignmentViewer.Models;
using IntuneAssignmentViewer.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure forwarded headers (required when behind a reverse proxy: Azure Linux App Service, IIS, nginx, etc.)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Authentication: App Registration only for user sign-in + role check
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

// Configure cookie - "Always" for HTTPS-only deployments, configurable for on-prem
var cookieSecurePolicy = builder.Configuration.GetValue<string>("CookiePolicy:Secure") ?? "Always";
builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.Secure = Enum.TryParse<CookieSecurePolicy>(cookieSecurePolicy, true, out var p) ? p : CookieSecurePolicy.Always;
});

// Role-based authorization
var requiredRole = builder.Configuration.GetValue<string>("Authorization:RequiredRole") ?? "IntuneReader";
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireIntuneRole", policy =>
        policy.RequireRole(requiredRole));
    // No FallbackPolicy: it would force authentication on every endpoint
    // including static assets (lib/bootstrap, app.css, scoped CSS files),
    // causing 302 redirects to OIDC for stylesheets. Auth is enforced per page
    // via [Authorize] / [AllowAnonymous] attributes instead.
});

builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

// Microsoft Graph client - supports both Managed Identity (Azure) and Client Secret (on-prem)
builder.Services.AddSingleton<Azure.Core.TokenCredential>(sp =>
{
    var graphConfig = builder.Configuration.GetSection("Graph");
    var tenantId = graphConfig.GetValue<string>("TenantId") ?? builder.Configuration.GetValue<string>("AzureAd:TenantId");
    var clientId = graphConfig.GetValue<string>("ClientId");
    var clientSecret = graphConfig.GetValue<string>("ClientSecret");

    // 1) Explicit client credentials always win (on-prem or hybrid scenario)
    if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret) && !string.IsNullOrEmpty(tenantId))
    {
        return new ClientSecretCredential(tenantId, clientId, clientSecret);
    }

    // 2) Development: allow CLI / Visual Studio fallback for local dev convenience
    if (builder.Environment.IsDevelopment())
    {
        return new ChainedTokenCredential(
            new ManagedIdentityCredential(new ManagedIdentityCredentialOptions()),
            new AzureCliCredential(),
            new VisualStudioCredential());
    }

    // 3) Production (Azure): require Managed Identity. No silent CLI/VS fallback so
    //    configuration errors fail loudly instead of accidentally using a developer's
    //    interactive credentials.
    return new ManagedIdentityCredential(new ManagedIdentityCredentialOptions());
});

builder.Services.AddSingleton(sp =>
{
    var credential = sp.GetRequiredService<Azure.Core.TokenCredential>();
    return new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
});

// Dedicated HttpClient for raw Graph beta calls (avoids Kiota URL templating issues)
builder.Services.AddHttpClient("GraphBeta", c =>
{
    c.BaseAddress = new Uri("https://graph.microsoft.com/");
    c.Timeout = TimeSpan.FromMinutes(2);
});

// Global in-memory cache (used by IntuneService for group display names only;
// GraphResponseCache has its own dedicated, size-capped MemoryCache).
builder.Services.AddMemoryCache();

// Performance & cache options from configuration
builder.Services.Configure<IntuneAssignmentViewer.Models.CacheOptions>(
    builder.Configuration.GetSection("Cache"));
builder.Services.Configure<IntuneAssignmentViewer.Models.PerformanceOptions>(
    builder.Configuration.GetSection("Performance"));
builder.Services.Configure<IntuneAssignmentViewer.Models.WarmupOptions>(
    builder.Configuration.GetSection("Warmup"));

builder.Services.AddSingleton<IntuneAssignmentViewer.Services.GraphResponseCache>();

// Optional background pre-warming of the cache (off by default for on-prem safety)
var warmupEnabled = builder.Configuration.GetValue<bool>("Warmup:Enabled");
if (warmupEnabled)
{
    builder.Services.AddHostedService<IntuneAssignmentViewer.Services.WarmupHostedService>();
}

// Branding configuration
builder.Services.Configure<BrandingSettings>(builder.Configuration.GetSection("Branding"));

// Application services
builder.Services.AddScoped<IIntuneService, IntuneService>();

// Add Razor components
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

// Must be first middleware - handles X-Forwarded-* headers from Azure App Service proxy
app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseCookiePolicy();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapControllers();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
