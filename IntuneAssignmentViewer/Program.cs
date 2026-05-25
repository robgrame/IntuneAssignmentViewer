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

    // If explicit Graph client credentials are provided (on-prem scenario), use ClientSecretCredential
    if (!string.IsNullOrEmpty(clientId) && !string.IsNullOrEmpty(clientSecret) && !string.IsNullOrEmpty(tenantId))
    {
        return new ClientSecretCredential(tenantId, clientId, clientSecret);
    }
    // Cloud scenario: chain Managed Identity (Azure) -> AzureCli/VS (dev fallback)
    return new ChainedTokenCredential(
        new ManagedIdentityCredential(new ManagedIdentityCredentialOptions()),
        new AzureCliCredential(),
        new VisualStudioCredential());
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
