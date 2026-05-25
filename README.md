# Intune Assignment Viewer

A .NET Blazor Server web app that lets authorized administrators view all Microsoft Intune assignments targeting an Entra ID group, in one place.

## Features

- 🔐 **Entra ID Authentication** with role-based access (configurable required role)
- 🔎 **Live group search** — results appear as you type
- 📊 **Card and table views** — switch between visual and tabular layouts
- 🗂️ **Complete policy coverage** — all categories Intune assigns to groups:
  - **Configuration** — Device Configurations + Settings Catalog
  - **Administrative Templates** — ADMX (`groupPolicyConfigurations`)
  - **Compliance** — Compliance policies for all platforms
  - **Applications** — Win32, LOB, Store, MSI, web apps (all `mobileApps` types)
  - **App Protection** — iOS / Android / Windows MAM policies
  - **App Configuration** — Managed device & managed app configurations
  - **Endpoint Security** — Settings Catalog ES policies + template-style intents (Antivirus, EDR, Firewall, ASR, Account Protection, Disk Encryption)
  - **Scripts** — PowerShell, Shell (macOS), Proactive Remediations
  - **Provisioning** — Autopilot, Enrollment Status Page, Cloud PC (Windows 365)
- 🎨 **Customizable branding** — logo and app title via `appsettings.json`
- ☁️ **Hybrid auth** — App Registration for sign-in + Managed Identity / Client Secret for Graph

## Deployment scenarios

The app supports two deployment models, configured via `appsettings.json`:

### A) Azure App Service (recommended)

Use a **system-assigned Managed Identity** on the App Service and grant it Graph API application permissions. No client secret needed for Graph calls.

```jsonc
"Graph": {
  "TenantId": "",
  "ClientId": "",
  "ClientSecret": ""  // leave empty -> ManagedIdentity is used automatically
}
```

### B) On-premises Windows Server (IIS)

Provide an explicit **client credential** for Graph (App Registration with application permissions):

```jsonc
"Graph": {
  "TenantId": "<your-tenant-id>",
  "ClientId": "<graph-app-registration-client-id>",
  "ClientSecret": "<client-secret>"
}
```

A `web.config` is included for IIS hosting (in-process model, `AspNetCoreModuleV2`).

#### IIS deployment steps

1. Install the [.NET 10 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/10.0) on the Windows server (`dotnet-hosting-10.x.x-win.exe`).
2. Publish the app: `dotnet publish -c Release -o C:\inetpub\IntuneAssignmentViewer`
3. In IIS, create a new site pointing to that folder. Set the app pool to **"No Managed Code"**.
4. Bind HTTPS (TLS) — required for OIDC.
5. Update the App Registration **Redirect URI** to `https://<your-host>/signin-oidc`.
6. Grant the app pool identity write access to a `logs` folder if you want stdout logging.

## Prerequisites

- .NET 10 SDK (build) / .NET 10 Runtime (Hosting Bundle for IIS)
- **App Registration #1 — Authentication**:
  - Redirect URI: `https://<host>/signin-oidc`
  - ID token issuance: **enabled**
  - App role: `IntuneReader` (assignable to users/groups)
- **Microsoft Graph application permissions** (granted to Managed Identity *or* App Registration #2):
  - `Group.Read.All`
  - `DeviceManagementConfiguration.Read.All`
  - `DeviceManagementApps.Read.All`
  - `DeviceManagementServiceConfig.Read.All` (for Autopilot / ESP)
  - `DeviceManagementManagedDevices.Read.All` (optional, for richer device info)
- Required role granted to authorized users (default: `IntuneReader`)

## Configuration reference

```jsonc
{
  "AzureAd": {                    // Sign-in App Registration
    "TenantId": "...",
    "ClientId": "..."
  },
  "Graph": {                      // Empty = use ManagedIdentity (Azure)
    "TenantId": "",
    "ClientId": "",
    "ClientSecret": ""
  },
  "Authorization": {
    "RequiredRole": "IntuneReader"
  },
  "CookiePolicy": {
    "Secure": "Always"            // "SameAsRequest" if running HTTP locally
  },
  "Branding": {
    "LogoPath": "/images/logo.svg",
    "AppTitle": "Intune Assignment Viewer"
  }
}
```

## Versioning

The current version is shown in the footer of every page and is sourced from the assembly's `InformationalVersion` (set via `<Version>` in `IntuneAssignmentViewer.csproj`).

## Credits

Graph endpoint coverage was inspired by Ugur Koc's excellent [IntuneAssignmentChecker](https://github.com/ugurkocde/IntuneAssignmentChecker) PowerShell module.
