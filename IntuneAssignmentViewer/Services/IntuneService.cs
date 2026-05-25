using Microsoft.Graph;
using IntuneAssignmentViewer.Models;
using System.Text.Json;
using Azure.Core;
using System.Net.Http.Headers;

namespace IntuneAssignmentViewer.Services;

public class IntuneService : IIntuneService
{
    private readonly GraphServiceClient _graphClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenCredential _credential;
    private readonly ILogger<IntuneService> _logger;
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresOn;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    // Endpoint Security template families found in configurationPolicies.templateReference.templateFamily
    private static readonly Dictionary<string, string> EndpointSecurityFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        { "endpointSecurityAntivirus", "Antivirus" },
        { "endpointSecurityDiskEncryption", "Disk Encryption" },
        { "endpointSecurityFirewall", "Firewall" },
        { "endpointSecurityEndpointDetectionAndResponse", "EDR" },
        { "endpointSecurityAttackSurfaceReduction", "Attack Surface Reduction" },
        { "endpointSecurityAccountProtection", "Account Protection" }
    };

    public IntuneService(
        GraphServiceClient graphClient,
        IHttpClientFactory httpClientFactory,
        TokenCredential credential,
        ILogger<IntuneService> logger)
    {
        _graphClient = graphClient;
        _httpClientFactory = httpClientFactory;
        _credential = credential;
        _logger = logger;
    }

    private async Task<string> GetTokenAsync()
    {
        // Refresh if expired or expires within 5 minutes
        if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiresOn.AddMinutes(-5))
            return _cachedToken;

        await _tokenLock.WaitAsync();
        try
        {
            if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiresOn.AddMinutes(-5))
                return _cachedToken;

            var ctx = new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" });
            var accessToken = await _credential.GetTokenAsync(ctx, CancellationToken.None);
            _cachedToken = accessToken.Token;
            _tokenExpiresOn = accessToken.ExpiresOn;
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<HttpClient> GetClientAsync()
    {
        var client = _httpClientFactory.CreateClient("GraphBeta");
        var token = await GetTokenAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<List<GroupInfo>> SearchGroupsAsync(string searchTerm)
    {
        var groups = new List<GroupInfo>();
        try
        {
            var result = await _graphClient.Groups.GetAsync(config =>
            {
                config.QueryParameters.Filter = $"startsWith(displayName, '{searchTerm}')";
                config.QueryParameters.Top = 20;
                config.QueryParameters.Select = new[] { "id", "displayName", "description" };
            });

            if (result?.Value != null)
            {
                groups.AddRange(result.Value.Select(g => new GroupInfo
                {
                    Id = g.Id ?? string.Empty,
                    DisplayName = g.DisplayName ?? string.Empty,
                    Description = g.Description ?? string.Empty
                }));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching groups with term: {SearchTerm}", searchTerm);
        }
        return groups;
    }

    public async Task<List<IntuneAssignment>> GetAssignmentsForGroupAsync(string groupId, PolicyType? filterType = null)
    {
        var assignments = new List<IntuneAssignment>();
        var tasks = new List<Task>();

        if (filterType is null or PolicyType.Configuration)
        {
            tasks.Add(GetDeviceConfigurationsAsync(groupId, assignments));
            tasks.Add(GetSettingsCatalogAsync(groupId, assignments));
        }
        if (filterType is null or PolicyType.AdministrativeTemplate)
            tasks.Add(GetAdministrativeTemplatesAsync(groupId, assignments));
        if (filterType is null or PolicyType.Compliance)
            tasks.Add(GetCompliancePoliciesAsync(groupId, assignments));
        if (filterType is null or PolicyType.Application)
            tasks.Add(GetMobileAppsAsync(groupId, assignments));
        if (filterType is null or PolicyType.AppProtection)
            tasks.Add(GetAppProtectionPoliciesAsync(groupId, assignments));
        if (filterType is null or PolicyType.AppConfiguration)
            tasks.Add(GetAppConfigurationPoliciesAsync(groupId, assignments));
        if (filterType is null or PolicyType.EndpointSecurity)
            tasks.Add(GetEndpointSecurityIntentsAsync(groupId, assignments));
        if (filterType is null or PolicyType.Script)
        {
            tasks.Add(GetDeviceScriptsAsync(groupId, assignments));
            tasks.Add(GetShellScriptsAsync(groupId, assignments));
            tasks.Add(GetHealthScriptsAsync(groupId, assignments));
        }
        if (filterType is null or PolicyType.Provisioning)
        {
            tasks.Add(GetAutopilotProfilesAsync(groupId, assignments));
            tasks.Add(GetEnrollmentStatusPageAsync(groupId, assignments));
            tasks.Add(GetCloudPcProvisioningPoliciesAsync(groupId, assignments));
            tasks.Add(GetCloudPcUserSettingsAsync(groupId, assignments));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving assignments for group: {GroupId}", groupId);
        }

        return assignments
            .OrderBy(a => a.PolicyType)
            .ThenBy(a => a.PolicyName)
            .ToList();
    }

    // ---------- Helpers ----------

    private async IAsyncEnumerable<JsonElement> EnumerateBetaAsync(string initialUrl)
    {
        var nextUrl = initialUrl;
        while (!string.IsNullOrEmpty(nextUrl))
        {
            JsonElement? root = await GetBetaAsync(nextUrl);
            if (root == null) yield break;

            if (root.Value.TryGetProperty("value", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                    yield return item.Clone();
            }

            nextUrl = root.Value.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
        }
    }

    private async Task<JsonElement?> GetBetaAsync(string url)
    {
        try
        {
            var client = await GetClientAsync();
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Graph request failed: {Status} {Url} - {Body}",
                        response.StatusCode, url, body.Length > 500 ? body[..500] : body);
                }
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching: {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Standard pattern: iterate the /assignments collection looking for a target with the matching groupId.
    /// Returns intent string (Include/Exclude or app intent like Required/Available/Uninstall) if matched.
    /// </summary>
    private async Task<string?> FindGroupAssignmentAsync(string assignmentsUrl, string groupId)
    {
        var root = await GetBetaAsync(assignmentsUrl);
        if (root is null || !root.Value.TryGetProperty("value", out var arr)) return null;

        foreach (var a in arr.EnumerateArray())
        {
            if (!a.TryGetProperty("target", out var target)) continue;
            if (!target.TryGetProperty("groupId", out var gId)) continue;
            if (!string.Equals(gId.GetString(), groupId, StringComparison.OrdinalIgnoreCase)) continue;

            var odataType = target.TryGetProperty("@odata.type", out var ot) ? ot.GetString() ?? "" : "";
            var isExclusion = odataType.Contains("exclusion", StringComparison.OrdinalIgnoreCase);

            // Check for app intent (required/available/uninstall) on the assignment itself
            if (a.TryGetProperty("intent", out var intentEl) && intentEl.ValueKind == JsonValueKind.String)
            {
                var intentVal = intentEl.GetString();
                if (!string.IsNullOrEmpty(intentVal))
                    return char.ToUpper(intentVal[0]) + intentVal[1..];
            }
            return isExclusion ? "Exclude" : "Include";
        }
        return null;
    }

    private static string GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool GetBool(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && (v.ValueKind == JsonValueKind.True);

    private static string GetSubTypeFromOdata(string odataType)
    {
        if (string.IsNullOrEmpty(odataType)) return "Unknown";
        var name = odataType.Replace("#microsoft.graph.", "").Trim();
        if (string.IsNullOrEmpty(name)) return "Unknown";
        var sb = new System.Text.StringBuilder();
        sb.Append(char.ToUpper(name[0]));
        for (int i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    private static string PlatformFromOdata(string odataType)
    {
        if (string.IsNullOrEmpty(odataType)) return "";
        var lower = odataType.ToLowerInvariant();
        if (lower.Contains("windows")) return "Windows";
        if (lower.Contains("ios")) return "iOS";
        if (lower.Contains("macos") || lower.Contains("osx")) return "macOS";
        if (lower.Contains("android")) return "Android";
        return "";
    }

    private static string PlatformFromString(string platform)
    {
        if (string.IsNullOrEmpty(platform)) return "";
        var lower = platform.ToLowerInvariant();
        if (lower.Contains("windows")) return "Windows";
        if (lower.Contains("ios")) return "iOS";
        if (lower.Contains("macos")) return "macOS";
        if (lower.Contains("android")) return "Android";
        return char.ToUpper(platform[0]) + platform[1..];
    }

    // ---------- Legacy Device Configurations ----------

    private async Task GetDeviceConfigurationsAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var policy in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/deviceConfigurations?$top=100&$select=id,displayName,description,@odata.type"))
        {
            var pid = GetStr(policy, "id");
            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceManagement/deviceConfigurations('{pid}')/assignments", groupId);
            if (intent == null) continue;

            var odata = GetStr(policy, "@odata.type");
            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(policy, "displayName"),
                PolicyType = PolicyType.Configuration,
                PolicySubType = GetSubTypeFromOdata(odata),
                Platform = PlatformFromOdata(odata),
                Description = GetStr(policy, "description"),
                AssignmentIntent = intent
            });
        }
    }

    // ---------- Settings Catalog (also handles Endpoint Security via templateReference) ----------

    private async Task GetSettingsCatalogAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var policy in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/configurationPolicies?$top=100"))
        {
            var pid = GetStr(policy, "id");
            var platforms = GetStr(policy, "platforms");

            // Determine if this policy belongs to Endpoint Security
            string? esFamily = null;
            if (policy.TryGetProperty("templateReference", out var tr) && tr.ValueKind == JsonValueKind.Object)
            {
                var family = GetStr(tr, "templateFamily");
                if (!string.IsNullOrEmpty(family) && EndpointSecurityFamilies.TryGetValue(family, out var friendly))
                    esFamily = friendly;
            }

            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceManagement/configurationPolicies('{pid}')/assignments", groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(policy, "name"),
                PolicyType = esFamily != null ? PolicyType.EndpointSecurity : PolicyType.Configuration,
                PolicySubType = esFamily ?? "Settings Catalog",
                Platform = PlatformFromString(platforms),
                Description = GetStr(policy, "description"),
                AssignmentIntent = intent
            });
        }
    }

    // ---------- Administrative Templates (ADMX) ----------

    private async Task GetAdministrativeTemplatesAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var policy in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/groupPolicyConfigurations?$top=100&$select=id,displayName,description"))
        {
            var pid = GetStr(policy, "id");
            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceManagement/groupPolicyConfigurations('{pid}')/assignments", groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(policy, "displayName"),
                PolicyType = PolicyType.AdministrativeTemplate,
                PolicySubType = "ADMX Template",
                Platform = "Windows",
                Description = GetStr(policy, "description"),
                AssignmentIntent = intent
            });
        }
    }

    // ---------- Compliance ----------

    private async Task GetCompliancePoliciesAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var policy in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/deviceCompliancePolicies?$top=100&$select=id,displayName,description,@odata.type"))
        {
            var pid = GetStr(policy, "id");
            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceManagement/deviceCompliancePolicies('{pid}')/assignments", groupId);
            if (intent == null) continue;

            var odata = GetStr(policy, "@odata.type");
            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(policy, "displayName"),
                PolicyType = PolicyType.Compliance,
                PolicySubType = GetSubTypeFromOdata(odata),
                Platform = PlatformFromOdata(odata),
                Description = GetStr(policy, "description"),
                AssignmentIntent = intent
            });
        }
    }

    // ---------- Mobile Apps ----------

    private async Task GetMobileAppsAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var app in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceAppManagement/mobileApps?$top=100&$select=id,displayName,description,@odata.type,isFeatured,isAssigned&$filter=isAssigned eq true"))
        {
            // Skip built-in/featured apps (system noise)
            if (GetBool(app, "isFeatured")) continue;

            var aid = GetStr(app, "id");
            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceAppManagement/mobileApps('{aid}')/assignments", groupId);
            if (intent == null) continue;

            var odata = GetStr(app, "@odata.type");
            assignments.Add(new IntuneAssignment
            {
                PolicyId = aid,
                PolicyName = GetStr(app, "displayName"),
                PolicyType = PolicyType.Application,
                PolicySubType = GetSubTypeFromOdata(odata),
                Platform = PlatformFromOdata(odata),
                Description = GetStr(app, "description"),
                AssignmentIntent = intent
            });
        }
    }

    // ---------- App Protection Policies ----------

    private async Task GetAppProtectionPoliciesAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var policy in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceAppManagement/managedAppPolicies?$top=100"))
        {
            var pid = GetStr(policy, "id");
            var odata = GetStr(policy, "@odata.type");
            string? resourcePath = odata switch
            {
                _ when odata.Contains("iosManagedAppProtection") => "iosManagedAppProtections",
                _ when odata.Contains("androidManagedAppProtection") => "androidManagedAppProtections",
                _ when odata.Contains("windowsManagedAppProtection") => "windowsManagedAppProtections",
                _ when odata.Contains("mdmWindowsInformationProtection") => "mdmWindowsInformationProtectionPolicies",
                _ when odata.Contains("windowsInformationProtection") => "windowsInformationProtectionPolicies",
                _ => null
            };
            if (resourcePath == null) continue;

            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceAppManagement/{resourcePath}('{pid}')/assignments", groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(policy, "displayName"),
                PolicyType = PolicyType.AppProtection,
                PolicySubType = GetSubTypeFromOdata(odata),
                Platform = PlatformFromOdata(odata),
                Description = GetStr(policy, "description"),
                AssignmentIntent = intent
            });
        }
    }

    // ---------- App Configuration Policies ----------

    private async Task GetAppConfigurationPoliciesAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var policy in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceAppManagement/mobileAppConfigurations?$top=100&$select=id,displayName,description,@odata.type"))
        {
            var pid = GetStr(policy, "id");
            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceAppManagement/mobileAppConfigurations('{pid}')/assignments", groupId);
            if (intent == null) continue;

            var odata = GetStr(policy, "@odata.type");
            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(policy, "displayName"),
                PolicyType = PolicyType.AppConfiguration,
                PolicySubType = "Managed Device " + GetSubTypeFromOdata(odata),
                Platform = PlatformFromOdata(odata),
                Description = GetStr(policy, "description"),
                AssignmentIntent = intent
            });
        }

        await foreach (var policy in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceAppManagement/targetedManagedAppConfigurations?$top=100&$select=id,displayName,description"))
        {
            var pid = GetStr(policy, "id");
            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceAppManagement/targetedManagedAppConfigurations('{pid}')/assignments", groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(policy, "displayName"),
                PolicyType = PolicyType.AppConfiguration,
                PolicySubType = "Managed App Config",
                Platform = "",
                Description = GetStr(policy, "description"),
                AssignmentIntent = intent
            });
        }
    }

    // ---------- Endpoint Security Intents (Template-style) ----------

    private async Task GetEndpointSecurityIntentsAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var intent in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/intents?$top=100&$select=id,displayName,description,templateId"))
        {
            var iid = GetStr(intent, "id");
            var assignIntent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceManagement/intents('{iid}')/assignments", groupId);
            if (assignIntent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = iid,
                PolicyName = GetStr(intent, "displayName"),
                PolicyType = PolicyType.EndpointSecurity,
                PolicySubType = "Security Intent",
                Platform = "",
                Description = GetStr(intent, "description"),
                AssignmentIntent = assignIntent
            });
        }
    }

    // ---------- Device Management Scripts (PowerShell - Windows) ----------

    private async Task GetDeviceScriptsAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var script in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts?$top=100&$select=id,displayName,description"))
        {
            var sid = GetStr(script, "id");
            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts('{sid}')/assignments", groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = sid,
                PolicyName = GetStr(script, "displayName"),
                PolicyType = PolicyType.Script,
                PolicySubType = "PowerShell Script",
                Platform = "Windows",
                Description = GetStr(script, "description"),
                AssignmentIntent = intent
            });
        }
    }

    // ---------- Device Shell Scripts (macOS) ----------

    private async Task GetShellScriptsAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var script in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/deviceShellScripts?$top=100&$select=id,displayName,description"))
        {
            var sid = GetStr(script, "id");
            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceManagement/deviceShellScripts('{sid}')/assignments", groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = sid,
                PolicyName = GetStr(script, "displayName"),
                PolicyType = PolicyType.Script,
                PolicySubType = "Shell Script",
                Platform = "macOS",
                Description = GetStr(script, "description"),
                AssignmentIntent = intent
            });
        }
    }

    // ---------- Proactive Remediations (Device Health Scripts) ----------

    private async Task GetHealthScriptsAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var script in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/deviceHealthScripts?$top=100&$select=id,displayName,description"))
        {
            var sid = GetStr(script, "id");
            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceManagement/deviceHealthScripts('{sid}')/assignments", groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = sid,
                PolicyName = GetStr(script, "displayName"),
                PolicyType = PolicyType.Script,
                PolicySubType = "Proactive Remediation",
                Platform = "Windows",
                Description = GetStr(script, "description"),
                AssignmentIntent = intent
            });
        }
    }

    // ---------- Autopilot Deployment Profiles ----------

    private async Task GetAutopilotProfilesAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var p in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/windowsAutopilotDeploymentProfiles?$top=100&$select=id,displayName,description"))
        {
            var pid = GetStr(p, "id");
            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceManagement/windowsAutopilotDeploymentProfiles('{pid}')/assignments", groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(p, "displayName"),
                PolicyType = PolicyType.Provisioning,
                PolicySubType = "Autopilot Profile",
                Platform = "Windows",
                Description = GetStr(p, "description"),
                AssignmentIntent = intent
            });
        }
    }

    // ---------- Enrollment Status Page ----------

    private async Task GetEnrollmentStatusPageAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var p in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/deviceEnrollmentConfigurations?$top=100"))
        {
            var odata = GetStr(p, "@odata.type");
            if (!odata.Contains("EnrollmentCompletionPageConfiguration", StringComparison.OrdinalIgnoreCase)) continue;

            var pid = GetStr(p, "id");
            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceManagement/deviceEnrollmentConfigurations('{pid}')/assignments", groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(p, "displayName"),
                PolicyType = PolicyType.Provisioning,
                PolicySubType = "Enrollment Status Page",
                Platform = "Windows",
                Description = GetStr(p, "description"),
                AssignmentIntent = intent
            });
        }
    }

    // ---------- Windows 365 Cloud PC Provisioning Policies ----------

    private async Task GetCloudPcProvisioningPoliciesAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var p in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/virtualEndpoint/provisioningPolicies?$top=100"))
        {
            var pid = GetStr(p, "id");
            // Cloud PC uses /id/assignments (no parentheses)
            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceManagement/virtualEndpoint/provisioningPolicies/{pid}/assignments", groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(p, "displayName"),
                PolicyType = PolicyType.Provisioning,
                PolicySubType = "Cloud PC Provisioning",
                Platform = "Windows 365",
                Description = GetStr(p, "description"),
                AssignmentIntent = intent
            });
        }
    }

    // ---------- Windows 365 Cloud PC User Settings ----------

    private async Task GetCloudPcUserSettingsAsync(string groupId, List<IntuneAssignment> assignments)
    {
        await foreach (var p in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/virtualEndpoint/userSettings?$top=100"))
        {
            var pid = GetStr(p, "id");
            var intent = await FindGroupAssignmentAsync(
                $"https://graph.microsoft.com/beta/deviceManagement/virtualEndpoint/userSettings/{pid}/assignments", groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(p, "displayName"),
                PolicyType = PolicyType.Provisioning,
                PolicySubType = "Cloud PC User Settings",
                Platform = "Windows 365",
                Description = GetStr(p, "description"),
                AssignmentIntent = intent
            });
        }
    }
}
