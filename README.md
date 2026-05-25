# Intune Assignment Viewer

A .NET Blazor Server web application that allows authorized users to view Intune policy assignments for Entra ID groups.

## Features

- **Entra ID Authentication** — Only users with a specific Entra role can access the portal
- **Group Search** — Search for Entra ID groups by display name
- **Assignment Viewer** — View Intune assignments for a selected group
- **Policy Type Filter** — Filter assignments by:
  - Configuration (Device Configurations + Settings Catalog)
  - Compliance Policies
  - Applications
- **Customizable Branding** — Company logo and app title configurable via `appsettings.json`

## Prerequisites

- .NET 8+ SDK
- An Azure AD App Registration with the following API permissions (Application type):
  - `Group.Read.All`
  - `DeviceManagementConfiguration.Read.All`
  - `DeviceManagementApps.Read.All`
- Admin consent granted for the above permissions
- An Entra ID role assigned to authorized users (default: `IntuneReader`)

## Configuration

### 1. App Registration

1. Go to [Azure Portal](https://portal.azure.com) → Microsoft Entra ID → App registrations
2. Create a new registration
3. Set Redirect URI to `https://localhost:7xxx/signin-oidc` (adjust port)
4. Add a Client Secret
5. Add API permissions listed above and grant admin consent

### 2. App Settings

Edit `appsettings.json`:

```json
{
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "YOUR_TENANT_ID",
    "ClientId": "YOUR_CLIENT_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET",
    "CallbackPath": "/signin-oidc"
  },
  "Authorization": {
    "RequiredRole": "IntuneReader"
  },
  "Branding": {
    "LogoPath": "/images/logo.png",
    "AppTitle": "Intune Assignment Viewer"
  }
}
```

### 3. Customize Logo

Replace `wwwroot/images/logo.png` with your company logo. The logo is displayed in the top navigation bar (max height: 32px).

### 4. Entra ID Role Setup

1. In the App Registration → App roles, create a role (e.g., `IntuneReader`)
2. Assign users/groups to this role in Enterprise Applications

## Running Locally

```bash
cd IntuneAssignmentViewer
dotnet run
```

Navigate to `https://localhost:7xxx` in your browser.

## Architecture

```
IntuneAssignmentViewer/
├── Components/
│   ├── Layout/          # MainLayout with branding, NavMenu
│   └── Pages/
│       ├── Home.razor          # Landing page
│       └── Assignments.razor   # Main assignment viewer
├── Models/
│   ├── IntuneAssignment.cs     # DTOs
│   └── BrandingSettings.cs     # Branding config model
├── Services/
│   ├── IIntuneService.cs       # Service interface
│   └── IntuneService.cs        # Microsoft Graph implementation
├── Program.cs                  # DI, auth, middleware
└── appsettings.json            # Configuration
```

## License

MIT
