using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Microsoft.Graph;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using IntuneAssignmentViewer.Components;
using IntuneAssignmentViewer.Models;
using IntuneAssignmentViewer.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure authentication with Microsoft Identity
var initialScopes = new[] {
    "https://graph.microsoft.com/.default"
};

builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi(initialScopes)
    .AddMicrosoftGraph(builder.Configuration.GetSection("Graph"))
    .AddInMemoryTokenCaches();

// Role-based authorization
var requiredRole = builder.Configuration.GetValue<string>("Authorization:RequiredRole") ?? "IntuneReader";
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireIntuneRole", policy =>
        policy.RequireRole(requiredRole));
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireRole(requiredRole)
        .Build();
});

builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

// Branding configuration
builder.Services.Configure<BrandingSettings>(builder.Configuration.GetSection("Branding"));

// Application services
builder.Services.AddScoped<IIntuneService, IntuneService>();

// Add Razor components
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapControllers();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
