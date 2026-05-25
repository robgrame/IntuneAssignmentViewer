using Microsoft.Graph;
using IntuneAssignmentViewer.Models;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;

namespace IntuneAssignmentViewer.Services;

public class IntuneService : IIntuneService
{
    private readonly GraphServiceClient _graphClient;
    private readonly GraphResponseCache _graphCache;
    private readonly IMemoryCache _memCache;
    private readonly ILogger<IntuneService> _logger;
    private readonly CacheOptions _cacheOpts;
    private readonly PerformanceOptions _perfOpts;

    private TimeSpan CatalogTtl => TimeSpan.FromMinutes(_cacheOpts.CatalogTtlMinutes);
    private TimeSpan AssignmentsTtl => TimeSpan.FromMinutes(_cacheOpts.AssignmentsTtlMinutes);
    private TimeSpan GroupNamesTtl => TimeSpan.FromMinutes(_cacheOpts.GroupNamesTtlMinutes);

    private const string GroupNameCacheKeyPrefix = "groupname:";

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
        GraphResponseCache graphCache,
        IMemoryCache memCache,
        IOptions<CacheOptions> cacheOpts,
        IOptions<PerformanceOptions> perfOpts,
        ILogger<IntuneService> logger)
    {
        _graphClient = graphClient;
        _graphCache = graphCache;
        _memCache = memCache;
        _cacheOpts = cacheOpts.Value;
        _perfOpts = perfOpts.Value;
        _logger = logger;
    }

    public void InvalidateCache() => _graphCache.InvalidateAll();

    /// <summary>
    /// Pre-warm the cache by enumerating every catalog endpoint and batch-fetching
    /// all per-policy /assignments. Called by the optional WarmupHostedService.
    /// Designed to be cheap when the cache is already warm (cache hits short-circuit).
    /// </summary>
    public async Task WarmupAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int totalPolicies = 0;
        int categoriesProcessed = 0;

        // Each entry: catalog URL + builder for the per-id /assignments URL
        var categories = new (string catalog, Func<string, string> assign)[]
        {
            ("https://graph.microsoft.com/beta/deviceManagement/deviceConfigurations?$top=100&$select=id",
                id => $"https://graph.microsoft.com/beta/deviceManagement/deviceConfigurations('{id}')/assignments"),
            ("https://graph.microsoft.com/beta/deviceManagement/configurationPolicies?$top=100",
                id => $"https://graph.microsoft.com/beta/deviceManagement/configurationPolicies('{id}')/assignments"),
            ("https://graph.microsoft.com/beta/deviceManagement/groupPolicyConfigurations?$top=100&$select=id",
                id => $"https://graph.microsoft.com/beta/deviceManagement/groupPolicyConfigurations('{id}')/assignments"),
            ("https://graph.microsoft.com/beta/deviceManagement/deviceCompliancePolicies?$top=100&$select=id",
                id => $"https://graph.microsoft.com/beta/deviceManagement/deviceCompliancePolicies('{id}')/assignments"),
            ("https://graph.microsoft.com/beta/deviceAppManagement/mobileApps?$top=100&$select=id&$filter=isAssigned eq true",
                id => $"https://graph.microsoft.com/beta/deviceAppManagement/mobileApps('{id}')/assignments"),
            ("https://graph.microsoft.com/beta/deviceAppManagement/mobileAppConfigurations?$top=100&$select=id",
                id => $"https://graph.microsoft.com/beta/deviceAppManagement/mobileAppConfigurations('{id}')/assignments"),
            ("https://graph.microsoft.com/beta/deviceAppManagement/targetedManagedAppConfigurations?$top=100&$select=id",
                id => $"https://graph.microsoft.com/beta/deviceAppManagement/targetedManagedAppConfigurations('{id}')/assignments"),
            ("https://graph.microsoft.com/beta/deviceManagement/intents?$top=100&$select=id",
                id => $"https://graph.microsoft.com/beta/deviceManagement/intents('{id}')/assignments"),
            ("https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts?$top=100&$select=id",
                id => $"https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts('{id}')/assignments"),
            ("https://graph.microsoft.com/beta/deviceManagement/deviceShellScripts?$top=100&$select=id",
                id => $"https://graph.microsoft.com/beta/deviceManagement/deviceShellScripts('{id}')/assignments"),
            ("https://graph.microsoft.com/beta/deviceManagement/deviceHealthScripts?$top=100&$select=id",
                id => $"https://graph.microsoft.com/beta/deviceManagement/deviceHealthScripts('{id}')/assignments"),
            ("https://graph.microsoft.com/beta/deviceManagement/windowsAutopilotDeploymentProfiles?$top=100&$select=id",
                id => $"https://graph.microsoft.com/beta/deviceManagement/windowsAutopilotDeploymentProfiles('{id}')/assignments"),
            ("https://graph.microsoft.com/beta/deviceManagement/deviceEnrollmentConfigurations?$top=100&$select=id",
                id => $"https://graph.microsoft.com/beta/deviceManagement/deviceEnrollmentConfigurations('{id}')/assignments"),
        };

        foreach (var (catalogUrl, assignBuilder) in categories)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var policies = await EnumerateAndPreFetchAsync(catalogUrl, assignBuilder);
                totalPolicies += policies.Count;
                categoriesProcessed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Warmup error on {Url}", catalogUrl);
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "Warmup completed: {Cats} categories, {Total} policies, {Elapsed} ms",
            categoriesProcessed, totalPolicies, sw.ElapsedMilliseconds);
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
        // CRITICAL: ConcurrentBag, NOT List<T>. The Get*Async methods run in parallel
        // via Task.WhenAll and all call .Add() on this collection. List<T> is not
        // thread-safe and would silently corrupt or throw.
        var assignments = new System.Collections.Concurrent.ConcurrentBag<IntuneAssignment>();
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
        // First page = catalog TTL; subsequent pages share same TTL
        var ttl = initialUrl.Contains("/assignments") ? AssignmentsTtl : CatalogTtl;
        while (!string.IsNullOrEmpty(nextUrl))
        {
            var root = await _graphCache.GetAsync(nextUrl, ttl);
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
        var ttl = url.Contains("/assignments") ? AssignmentsTtl : CatalogTtl;
        return await _graphCache.GetAsync(url, ttl);
    }

    /// <summary>
    /// Helper that enumerates a catalog endpoint into a list AND pre-fetches all the
    /// per-item /assignments URLs via Graph $batch (if enabled). Returns the policy
    /// list ready for iteration; subsequent FindGroupAssignmentAsync calls hit the cache.
    /// </summary>
    private async Task<List<JsonElement>> EnumerateAndPreFetchAsync(string catalogUrl, Func<string, string> assignmentsUrlBuilder)
    {
        var policies = new List<JsonElement>();
        await foreach (var p in EnumerateBetaAsync(catalogUrl))
            policies.Add(p);

        if (_perfOpts.EnableBatchRequests && policies.Count > 0)
        {
            var urls = policies
                .Select(p => assignmentsUrlBuilder(GetStr(p, "id")))
                .Where(u => !string.IsNullOrEmpty(u))
                .ToList();
            await _graphCache.PreFetchBatchAsync(urls, AssignmentsTtl);
        }

        return policies;
    }

    public const string VirtualAllUsersId = "__all_users__";
    public const string VirtualAllDevicesId = "__all_devices__";

    /// <summary>
    /// Standard pattern: iterate the /assignments collection looking for a target with the matching groupId.
    /// Returns intent string (Include/Exclude or app intent like Required/Available/Uninstall) if matched.
    /// Special sentinel values match the virtual targets All Users / All Devices.
    /// </summary>
    private async Task<string?> FindGroupAssignmentAsync(string assignmentsUrl, string groupId)
    {
        var root = await GetBetaAsync(assignmentsUrl);
        if (root is null || !root.Value.TryGetProperty("value", out var arr)) return null;

        foreach (var a in arr.EnumerateArray())
        {
            if (!a.TryGetProperty("target", out var target)) continue;
            var odataType = target.TryGetProperty("@odata.type", out var ot) ? ot.GetString() ?? "" : "";

            bool matched;
            bool isExclusion = false;

            if (groupId == VirtualAllUsersId)
            {
                matched = odataType.Contains("allLicensedUsersAssignmentTarget", StringComparison.OrdinalIgnoreCase);
            }
            else if (groupId == VirtualAllDevicesId)
            {
                matched = odataType.Contains("allDevicesAssignmentTarget", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                if (!target.TryGetProperty("groupId", out var gId)) continue;
                if (!string.Equals(gId.GetString(), groupId, StringComparison.OrdinalIgnoreCase)) continue;
                matched = true;
                isExclusion = odataType.Contains("exclusion", StringComparison.OrdinalIgnoreCase);
            }

            if (!matched) continue;

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

    private async Task GetDeviceConfigurationsAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
    {
        await foreach (var policy in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/deviceConfigurations?$top=100&$select=id,displayName,description"))
        {
            var pid = GetStr(policy, "id");
            var assignUrl = $"https://graph.microsoft.com/beta/deviceManagement/deviceConfigurations('{pid}')/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
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
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ---------- Settings Catalog (also handles Endpoint Security via templateReference) ----------

    private async Task GetSettingsCatalogAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
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

            var assignUrl = $"https://graph.microsoft.com/beta/deviceManagement/configurationPolicies('{pid}')/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(policy, "name"),
                PolicyType = esFamily != null ? PolicyType.EndpointSecurity : PolicyType.Configuration,
                PolicySubType = esFamily ?? "Settings Catalog",
                Platform = PlatformFromString(platforms),
                Description = GetStr(policy, "description"),
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ---------- Administrative Templates (ADMX) ----------

    private async Task GetAdministrativeTemplatesAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
    {
        await foreach (var policy in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/groupPolicyConfigurations?$top=100&$select=id,displayName,description"))
        {
            var pid = GetStr(policy, "id");
            var assignUrl = $"https://graph.microsoft.com/beta/deviceManagement/groupPolicyConfigurations('{pid}')/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(policy, "displayName"),
                PolicyType = PolicyType.AdministrativeTemplate,
                PolicySubType = "ADMX Template",
                Platform = "Windows",
                Description = GetStr(policy, "description"),
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ---------- Compliance ----------

    private async Task GetCompliancePoliciesAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
    {
        await foreach (var policy in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/deviceCompliancePolicies?$top=100&$select=id,displayName,description"))
        {
            var pid = GetStr(policy, "id");
            var assignUrl = $"https://graph.microsoft.com/beta/deviceManagement/deviceCompliancePolicies('{pid}')/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
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
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ---------- Mobile Apps ----------

    private async Task GetMobileAppsAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
    {
        await foreach (var app in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceAppManagement/mobileApps?$top=100&$select=id,displayName,description,isFeatured,isAssigned&$filter=isAssigned eq true"))
        {
            // Skip built-in/featured apps (system noise)
            if (GetBool(app, "isFeatured")) continue;

            var aid = GetStr(app, "id");
            var assignUrl = $"https://graph.microsoft.com/beta/deviceAppManagement/mobileApps('{aid}')/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
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
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ---------- App Protection Policies ----------

    private async Task GetAppProtectionPoliciesAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
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

            var assignUrl = $"https://graph.microsoft.com/beta/deviceAppManagement/{resourcePath}('{pid}')/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(policy, "displayName"),
                PolicyType = PolicyType.AppProtection,
                PolicySubType = GetSubTypeFromOdata(odata),
                Platform = PlatformFromOdata(odata),
                Description = GetStr(policy, "description"),
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ---------- App Configuration Policies ----------

    private async Task GetAppConfigurationPoliciesAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
    {
        await foreach (var policy in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceAppManagement/mobileAppConfigurations?$top=100&$select=id,displayName,description"))
        {
            var pid = GetStr(policy, "id");
            var assignUrl = $"https://graph.microsoft.com/beta/deviceAppManagement/mobileAppConfigurations('{pid}')/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
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
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }

        await foreach (var policy in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceAppManagement/targetedManagedAppConfigurations?$top=100&$select=id,displayName,description"))
        {
            var pid = GetStr(policy, "id");
            var assignUrl = $"https://graph.microsoft.com/beta/deviceAppManagement/targetedManagedAppConfigurations('{pid}')/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(policy, "displayName"),
                PolicyType = PolicyType.AppConfiguration,
                PolicySubType = "Managed App Config",
                Platform = "",
                Description = GetStr(policy, "description"),
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ---------- Endpoint Security Intents (Template-style) ----------

    private async Task GetEndpointSecurityIntentsAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
    {
        await foreach (var intent in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/intents?$top=100&$select=id,displayName,description,templateId"))
        {
            var iid = GetStr(intent, "id");
            var assignUrl = $"https://graph.microsoft.com/beta/deviceManagement/intents('{iid}')/assignments";
            var assignIntent = await FindGroupAssignmentAsync(assignUrl, groupId);
            if (assignIntent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = iid,
                PolicyName = GetStr(intent, "displayName"),
                PolicyType = PolicyType.EndpointSecurity,
                PolicySubType = "Security Intent",
                Platform = "",
                Description = GetStr(intent, "description"),
                AssignmentIntent = assignIntent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ---------- Device Management Scripts (PowerShell - Windows) ----------

    private async Task GetDeviceScriptsAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
    {
        await foreach (var script in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts?$top=100&$select=id,displayName,description"))
        {
            var sid = GetStr(script, "id");
            var assignUrl = $"https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts('{sid}')/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = sid,
                PolicyName = GetStr(script, "displayName"),
                PolicyType = PolicyType.Script,
                PolicySubType = "PowerShell Script",
                Platform = "Windows",
                Description = GetStr(script, "description"),
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ---------- Device Shell Scripts (macOS) ----------

    private async Task GetShellScriptsAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
    {
        await foreach (var script in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/deviceShellScripts?$top=100&$select=id,displayName,description"))
        {
            var sid = GetStr(script, "id");
            var assignUrl = $"https://graph.microsoft.com/beta/deviceManagement/deviceShellScripts('{sid}')/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = sid,
                PolicyName = GetStr(script, "displayName"),
                PolicyType = PolicyType.Script,
                PolicySubType = "Shell Script",
                Platform = "macOS",
                Description = GetStr(script, "description"),
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ---------- Proactive Remediations (Device Health Scripts) ----------

    private async Task GetHealthScriptsAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
    {
        await foreach (var script in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/deviceHealthScripts?$top=100&$select=id,displayName,description"))
        {
            var sid = GetStr(script, "id");
            var assignUrl = $"https://graph.microsoft.com/beta/deviceManagement/deviceHealthScripts('{sid}')/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = sid,
                PolicyName = GetStr(script, "displayName"),
                PolicyType = PolicyType.Script,
                PolicySubType = "Proactive Remediation",
                Platform = "Windows",
                Description = GetStr(script, "description"),
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ---------- Autopilot Deployment Profiles ----------

    private async Task GetAutopilotProfilesAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
    {
        await foreach (var p in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/windowsAutopilotDeploymentProfiles?$top=100&$select=id,displayName,description"))
        {
            var pid = GetStr(p, "id");
            var assignUrl = $"https://graph.microsoft.com/beta/deviceManagement/windowsAutopilotDeploymentProfiles('{pid}')/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(p, "displayName"),
                PolicyType = PolicyType.Provisioning,
                PolicySubType = "Autopilot Profile",
                Platform = "Windows",
                Description = GetStr(p, "description"),
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ---------- Enrollment Status Page ----------

    private async Task GetEnrollmentStatusPageAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
    {
        await foreach (var p in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/deviceEnrollmentConfigurations?$top=100"))
        {
            var odata = GetStr(p, "@odata.type");
            if (!odata.Contains("EnrollmentCompletionPageConfiguration", StringComparison.OrdinalIgnoreCase)) continue;

            var pid = GetStr(p, "id");
            var assignUrl = $"https://graph.microsoft.com/beta/deviceManagement/deviceEnrollmentConfigurations('{pid}')/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(p, "displayName"),
                PolicyType = PolicyType.Provisioning,
                PolicySubType = "Enrollment Status Page",
                Platform = "Windows",
                Description = GetStr(p, "description"),
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ---------- Windows 365 Cloud PC Provisioning Policies ----------

    private async Task GetCloudPcProvisioningPoliciesAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
    {
        await foreach (var p in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/virtualEndpoint/provisioningPolicies?$top=100"))
        {
            var pid = GetStr(p, "id");
            // Cloud PC uses /id/assignments (no parentheses)
            var assignUrl = $"https://graph.microsoft.com/beta/deviceManagement/virtualEndpoint/provisioningPolicies/{pid}/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(p, "displayName"),
                PolicyType = PolicyType.Provisioning,
                PolicySubType = "Cloud PC Provisioning",
                Platform = "Windows 365",
                Description = GetStr(p, "description"),
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ---------- Windows 365 Cloud PC User Settings ----------

    private async Task GetCloudPcUserSettingsAsync(string groupId, System.Collections.Concurrent.ConcurrentBag<IntuneAssignment> assignments)
    {
        await foreach (var p in EnumerateBetaAsync(
            "https://graph.microsoft.com/beta/deviceManagement/virtualEndpoint/userSettings?$top=100"))
        {
            var pid = GetStr(p, "id");
            var assignUrl = $"https://graph.microsoft.com/beta/deviceManagement/virtualEndpoint/userSettings/{pid}/assignments";
            var intent = await FindGroupAssignmentAsync(assignUrl, groupId);
            if (intent == null) continue;

            assignments.Add(new IntuneAssignment
            {
                PolicyId = pid,
                PolicyName = GetStr(p, "displayName"),
                PolicyType = PolicyType.Provisioning,
                PolicySubType = "Cloud PC User Settings",
                Platform = "Windows 365",
                Description = GetStr(p, "description"),
                AssignmentIntent = intent,
                AssignmentsUrl = assignUrl
            });
        }
    }

    // ===========================================================
    // Drill-down: get ALL assignments (groups + virtual targets) for a single policy
    // ===========================================================

    // Whitelist of allowed Intune assignments paths. The URL must match one of these
    // patterns to prevent SSRF via tampered URLs reaching GetBetaAsync (which attaches
    // the Graph bearer token).
    private static readonly System.Text.RegularExpressions.Regex AllowedAssignmentsUrlPattern =
        new(@"^https://graph\.microsoft\.com/beta/" +
            @"(?:" +
                // Parenthesised key form: deviceManagement/{resource}('{id}')/assignments
                // and deviceAppManagement/{resource}('{id}')/assignments
                @"(?:deviceManagement|deviceAppManagement)/[A-Za-z]+\('[0-9a-fA-F\-]+'\)/assignments" +
                @"|" +
                // Cloud PC / virtualEndpoint slash form: deviceManagement/virtualEndpoint/{resource}/{id}/assignments
                @"deviceManagement/virtualEndpoint/[A-Za-z]+/[0-9a-fA-F\-]+/assignments" +
            @")" +
            @"(?:\?.*)?$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task<List<PolicyAssignmentDetail>> GetPolicyAssignmentsAsync(string assignmentsUrl)
    {
        var results = new List<PolicyAssignmentDetail>();
        if (string.IsNullOrWhiteSpace(assignmentsUrl)) return results;

        // SSRF guard: only allow well-formed Intune assignment URLs
        if (!AllowedAssignmentsUrlPattern.IsMatch(assignmentsUrl))
        {
            _logger.LogWarning("Rejected drill-down URL (does not match allowed pattern): {Url}", assignmentsUrl);
            return results;
        }

        var groupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var raw = new List<PolicyAssignmentDetail>();

        // Iterate ALL pages of assignments (some policies have many targets)
        await foreach (var a in EnumerateBetaAsync(assignmentsUrl))
        {
            if (!a.TryGetProperty("target", out var target)) continue;
            var odataType = target.TryGetProperty("@odata.type", out var ot) ? ot.GetString() ?? "" : "";

            var detail = new PolicyAssignmentDetail
            {
                IsExcluded = odataType.Contains("exclusion", StringComparison.OrdinalIgnoreCase)
            };

            // Filter info
            if (target.TryGetProperty("deviceAndAppManagementAssignmentFilterId", out var fid) &&
                fid.ValueKind == JsonValueKind.String)
            {
                var v = fid.GetString();
                if (!string.IsNullOrEmpty(v) && v != "00000000-0000-0000-0000-000000000000")
                    detail.FilterId = v;
            }
            if (target.TryGetProperty("deviceAndAppManagementAssignmentFilterType", out var ft) &&
                ft.ValueKind == JsonValueKind.String)
            {
                var v = ft.GetString();
                if (!string.IsNullOrEmpty(v) && v != "none")
                    detail.FilterType = v;
            }

            // App intent (Required/Available/Uninstall) - stored separately from Include/Exclude
            if (a.TryGetProperty("intent", out var intentEl) && intentEl.ValueKind == JsonValueKind.String)
            {
                var iv = intentEl.GetString();
                if (!string.IsNullOrEmpty(iv))
                    detail.Intent = char.ToUpper(iv[0]) + iv[1..];
            }

            if (odataType.Contains("allLicensedUsersAssignmentTarget", StringComparison.OrdinalIgnoreCase))
            {
                detail.TargetType = AssignmentTargetType.AllUsers;
                detail.GroupName = "All Users";
            }
            else if (odataType.Contains("allDevicesAssignmentTarget", StringComparison.OrdinalIgnoreCase))
            {
                detail.TargetType = AssignmentTargetType.AllDevices;
                detail.GroupName = "All Devices";
            }
            else if (target.TryGetProperty("groupId", out var gId) && gId.ValueKind == JsonValueKind.String)
            {
                detail.TargetType = AssignmentTargetType.Group;
                detail.GroupId = gId.GetString() ?? "";
                if (!string.IsNullOrEmpty(detail.GroupId))
                    groupIds.Add(detail.GroupId);
            }
            else
            {
                detail.TargetType = AssignmentTargetType.Unknown;
                detail.GroupName = "Unknown target";
            }

            raw.Add(detail);
        }

        if (groupIds.Count > 0)
        {
            var names = await ResolveGroupNamesAsync(groupIds);
            foreach (var d in raw.Where(d => d.TargetType == AssignmentTargetType.Group))
            {
                d.GroupName = names.TryGetValue(d.GroupId, out var name) ? name : d.GroupId;
            }
        }

        return raw
            .OrderByDescending(d => d.TargetType == AssignmentTargetType.AllUsers || d.TargetType == AssignmentTargetType.AllDevices)
            .ThenBy(d => d.GroupName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Resolve group display names in chunks of 20 via Graph $batch (avoids
    /// requiring Directory.Read.All that getByIds would need).
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveGroupNamesAsync(IEnumerable<string> groupIds)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allIds = groupIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (allIds.Count == 0) return result;

        // 1) Serve from per-group name cache
        var uncached = new List<string>();
        foreach (var id in allIds)
        {
            if (_memCache.TryGetValue<string>(GroupNameCacheKeyPrefix + id, out var cachedName) && cachedName != null)
                result[id] = cachedName;
            else
                uncached.Add(id);
        }
        if (uncached.Count == 0) return result;

        // 2) Resolve cache misses via Graph $batch (chunks of 20)
        foreach (var chunk in uncached.Chunk(20))
        {
            var batchRequests = chunk.Select((id, i) => new
            {
                id = (i + 1).ToString(),
                method = "GET",
                url = $"/groups/{id}?$select=id,displayName"
            }).ToArray();

            var payload = JsonSerializer.Serialize(new { requests = batchRequests });
            try
            {
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using var response = await _graphCache.PostAsync("https://graph.microsoft.com/v1.0/$batch", content);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Graph $batch failed: {Status}", response.StatusCode);
                    continue;
                }
                await using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                if (!doc.RootElement.TryGetProperty("responses", out var arr)) continue;

                foreach (var r in arr.EnumerateArray())
                {
                    if (!r.TryGetProperty("status", out var st) || st.GetInt32() != 200) continue;
                    if (!r.TryGetProperty("body", out var body)) continue;
                    var id = GetStr(body, "id");
                    var name = GetStr(body, "displayName");
                    if (!string.IsNullOrEmpty(id))
                    {
                        var displayName = string.IsNullOrEmpty(name) ? id : name;
                        result[id] = displayName;
                        _memCache.Set(GroupNameCacheKeyPrefix + id, displayName, GroupNamesTtl);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error resolving group names chunk");
            }
        }

        // 3) Fallback for any IDs that didn't resolve (deleted groups, denied access, etc.)
        foreach (var id in allIds.Where(i => !result.ContainsKey(i)))
            result[id] = "(unknown / deleted)";

        return result;
    }
}
