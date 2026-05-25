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

// Configure forwarded headers (required for Linux App Service behind reverse proxy)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Authentication: App Registration only for user sign-in + role check
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

// Configure OIDC cookie for reverse proxy scenarios
builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.Secure = CookieSecurePolicy.Always;
});

// Role-based authorization
var requiredRole = builder.Configuration.GetValue<string>("Authorization:RequiredRole") ?? "IntuneReader";
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireIntuneRole", policy =>
        policy.RequireRole(requiredRole));
    // FallbackPolicy only requires authentication - role check is done in pages
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

// Microsoft Graph client using Managed Identity (no app secret needed for Graph calls)
builder.Services.AddSingleton(sp =>
{
    var credential = new DefaultAzureCredential();
    return new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
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
