using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using IntuneAssignmentViewer.Models;
using System.Text.Json;

namespace IntuneAssignmentViewer.Services;

public class IntuneService : IIntuneService
{
    private readonly GraphServiceClient _graphClient;
    private readonly ILogger<IntuneService> _logger;

    public IntuneService(GraphServiceClient graphClient, ILogger<IntuneService> logger)
    {
        _graphClient = graphClient;
        _logger = logger;
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

        try
        {
            if (filterType == null || filterType == PolicyType.Configuration)
            {
                await GetDeviceConfigurationAssignmentsAsync(groupId, assignments);
                await GetConfigurationPolicyAssignmentsAsync(groupId, assignments);
            }

            if (filterType == null || filterType == PolicyType.Compliance)
            {
                await GetCompliancePolicyAssignmentsAsync(groupId, assignments);
            }

            if (filterType == null || filterType == PolicyType.Application)
            {
                await GetApplicationAssignmentsAsync(groupId, assignments);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving assignments for group: {GroupId}", groupId);
        }

        return assignments.OrderBy(a => a.PolicyType).ThenBy(a => a.PolicyName).ToList();
    }

    private async Task GetDeviceConfigurationAssignmentsAsync(string groupId, List<IntuneAssignment> assignments)
    {
        try
        {
            var configs = await _graphClient.DeviceManagement.DeviceConfigurations.GetAsync(config =>
            {
                config.QueryParameters.Top = 100;
                config.QueryParameters.Select = new[] { "id", "displayName", "description" };
            });

            if (configs?.Value == null) return;

            foreach (var policy in configs.Value)
            {
                var policyAssignments = await _graphClient.DeviceManagement
                    .DeviceConfigurations[policy.Id]
                    .Assignments.GetAsync();

                if (policyAssignments?.Value == null) continue;

                var matchingAssignment = policyAssignments.Value
                    .FirstOrDefault(a => IsTargetedToGroup(a.Target, groupId));

                if (matchingAssignment != null)
                {
                    assignments.Add(new IntuneAssignment
                    {
                        PolicyId = policy.Id ?? string.Empty,
                        PolicyName = policy.DisplayName ?? "Unknown",
                        PolicyType = PolicyType.Configuration,
                        Description = policy.Description ?? string.Empty,
                        AssignmentIntent = GetAssignmentIntent(matchingAssignment.Target)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching device configuration assignments");
        }
    }

    private async Task GetConfigurationPolicyAssignmentsAsync(string groupId, List<IntuneAssignment> assignments)
    {
        try
        {
            // Settings Catalog policies (configurationPolicies) - use raw request via Graph SDK
            var requestUrl = "https://graph.microsoft.com/beta/deviceManagement/configurationPolicies?$top=100&$select=id,name,description";
            var requestInfo = new RequestInformation
            {
                HttpMethod = Method.GET,
                UrlTemplate = requestUrl
            };

            var response = await _graphClient.RequestAdapter.SendPrimitiveAsync<Stream>(requestInfo);
            if (response == null) return;

            using var doc = await JsonDocument.ParseAsync(response);
            var root = doc.RootElement;

            if (!root.TryGetProperty("value", out var policiesArray)) return;

            foreach (var policy in policiesArray.EnumerateArray())
            {
                var policyId = policy.GetProperty("id").GetString();
                var policyName = policy.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : "Unknown";
                var description = policy.TryGetProperty("description", out var descEl) ? descEl.GetString() : "";

                // Get assignments for this policy
                var assignUrl = $"https://graph.microsoft.com/beta/deviceManagement/configurationPolicies('{policyId}')/assignments";
                var assignRequestInfo = new RequestInformation
                {
                    HttpMethod = Method.GET,
                    UrlTemplate = assignUrl
                };

                var assignResponse = await _graphClient.RequestAdapter.SendPrimitiveAsync<Stream>(assignRequestInfo);
                if (assignResponse == null) continue;

                using var assignDoc = await JsonDocument.ParseAsync(assignResponse);
                var assignRoot = assignDoc.RootElement;

                if (!assignRoot.TryGetProperty("value", out var assignmentsArray)) continue;

                foreach (var assign in assignmentsArray.EnumerateArray())
                {
                    if (!assign.TryGetProperty("target", out var target)) continue;
                    if (!target.TryGetProperty("groupId", out var gId)) continue;

                    if (string.Equals(gId.GetString(), groupId, StringComparison.OrdinalIgnoreCase))
                    {
                        var intent = target.TryGetProperty("@odata.type", out var odataType)
                            ? (odataType.GetString()?.Contains("exclusion") == true ? "Exclude" : "Include")
                            : "Include";

                        assignments.Add(new IntuneAssignment
                        {
                            PolicyId = policyId ?? string.Empty,
                            PolicyName = policyName ?? "Unknown",
                            PolicyType = PolicyType.Configuration,
                            Description = description ?? string.Empty,
                            AssignmentIntent = intent
                        });
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching Settings Catalog policy assignments");
        }
    }

    private async Task GetCompliancePolicyAssignmentsAsync(string groupId, List<IntuneAssignment> assignments)
    {
        try
        {
            var policies = await _graphClient.DeviceManagement.DeviceCompliancePolicies.GetAsync(config =>
            {
                config.QueryParameters.Top = 100;
                config.QueryParameters.Select = new[] { "id", "displayName", "description" };
            });

            if (policies?.Value == null) return;

            foreach (var policy in policies.Value)
            {
                var policyAssignments = await _graphClient.DeviceManagement
                    .DeviceCompliancePolicies[policy.Id]
                    .Assignments.GetAsync();

                if (policyAssignments?.Value == null) continue;

                var matchingAssignment = policyAssignments.Value
                    .FirstOrDefault(a => IsTargetedToGroup(a.Target, groupId));

                if (matchingAssignment != null)
                {
                    assignments.Add(new IntuneAssignment
                    {
                        PolicyId = policy.Id ?? string.Empty,
                        PolicyName = policy.DisplayName ?? "Unknown",
                        PolicyType = PolicyType.Compliance,
                        Description = policy.Description ?? string.Empty,
                        AssignmentIntent = GetAssignmentIntent(matchingAssignment.Target)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching compliance policy assignments");
        }
    }

    private async Task GetApplicationAssignmentsAsync(string groupId, List<IntuneAssignment> assignments)
    {
        try
        {
            var apps = await _graphClient.DeviceAppManagement.MobileApps.GetAsync(config =>
            {
                config.QueryParameters.Top = 100;
                config.QueryParameters.Select = new[] { "id", "displayName", "description" };
            });

            if (apps?.Value == null) return;

            foreach (var app in apps.Value)
            {
                var appAssignments = await _graphClient.DeviceAppManagement
                    .MobileApps[app.Id]
                    .Assignments.GetAsync();

                if (appAssignments?.Value == null) continue;

                var matchingAssignment = appAssignments.Value
                    .FirstOrDefault(a => IsTargetedToGroup(a.Target, groupId));

                if (matchingAssignment != null)
                {
                    assignments.Add(new IntuneAssignment
                    {
                        PolicyId = app.Id ?? string.Empty,
                        PolicyName = app.DisplayName ?? "Unknown",
                        PolicyType = PolicyType.Application,
                        Description = app.Description ?? string.Empty,
                        AssignmentIntent = matchingAssignment.Intent?.ToString() ?? "Unknown"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching application assignments");
        }
    }

    private static bool IsTargetedToGroup(DeviceAndAppManagementAssignmentTarget? target, string groupId)
    {
        if (target is GroupAssignmentTarget groupTarget)
            return string.Equals(groupTarget.GroupId, groupId, StringComparison.OrdinalIgnoreCase);

        if (target is ExclusionGroupAssignmentTarget exclusionTarget)
            return string.Equals(exclusionTarget.GroupId, groupId, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static string GetAssignmentIntent(DeviceAndAppManagementAssignmentTarget? target)
    {
        if (target is ExclusionGroupAssignmentTarget)
            return "Exclude";
        if (target is GroupAssignmentTarget)
            return "Include";
        if (target is AllDevicesAssignmentTarget)
            return "All Devices";
        if (target is AllLicensedUsersAssignmentTarget)
            return "All Users";
        return "Unknown";
    }
}
