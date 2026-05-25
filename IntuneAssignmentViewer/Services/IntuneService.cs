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

            while (configs?.Value != null)
            {
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

                if (!string.IsNullOrEmpty(configs.OdataNextLink))
                {
                    configs = await _graphClient.DeviceManagement.DeviceConfigurations
                        .WithUrl(configs.OdataNextLink)
                        .GetAsync();
                }
                else
                {
                    break;
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
            var nextUrl = "https://graph.microsoft.com/beta/deviceManagement/configurationPolicies?$top=100&$select=id,name,description";

            while (!string.IsNullOrEmpty(nextUrl))
            {
                var requestInfo = new RequestInformation
                {
                    HttpMethod = Method.GET,
                    UrlTemplate = nextUrl
                };

                var response = await _graphClient.RequestAdapter.SendPrimitiveAsync<Stream>(requestInfo);
                if (response == null) break;

                using var doc = await JsonDocument.ParseAsync(response);
                var root = doc.RootElement;

                if (!root.TryGetProperty("value", out var policiesArray)) break;

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

                // Handle pagination
                nextUrl = root.TryGetProperty("@odata.nextLink", out var nextLinkEl) 
                    ? nextLinkEl.GetString() 
                    : null;
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

            while (policies?.Value != null)
            {
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

                if (!string.IsNullOrEmpty(policies.OdataNextLink))
                {
                    policies = await _graphClient.DeviceManagement.DeviceCompliancePolicies
                        .WithUrl(policies.OdataNextLink)
                        .GetAsync();
                }
                else
                {
                    break;
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
            // Use beta endpoint to get all app types (Win32, LOB, Store, etc.)
            var nextUrl = "https://graph.microsoft.com/beta/deviceAppManagement/mobileApps?$top=100&$select=id,displayName,description&$filter=isAssigned eq true";

            while (!string.IsNullOrEmpty(nextUrl))
            {
                var requestInfo = new RequestInformation
                {
                    HttpMethod = Method.GET,
                    UrlTemplate = nextUrl
                };

                var response = await _graphClient.RequestAdapter.SendPrimitiveAsync<Stream>(requestInfo);
                if (response == null) break;

                using var doc = await JsonDocument.ParseAsync(response);
                var root = doc.RootElement;

                if (!root.TryGetProperty("value", out var appsArray)) break;

                foreach (var app in appsArray.EnumerateArray())
                {
                    var appId = app.GetProperty("id").GetString();
                    var appName = app.TryGetProperty("displayName", out var nameEl) ? nameEl.GetString() : "Unknown";
                    var description = app.TryGetProperty("description", out var descEl) ? descEl.GetString() : "";

                    // Get assignments for this app
                    var assignUrl = $"https://graph.microsoft.com/beta/deviceAppManagement/mobileApps('{appId}')/assignments";
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
                            var intent = "Include";
                            if (target.TryGetProperty("@odata.type", out var odataType))
                            {
                                intent = odataType.GetString()?.Contains("exclusion") == true ? "Exclude" : "Include";
                            }
                            // Also check the assignment intent property
                            if (assign.TryGetProperty("intent", out var intentEl))
                            {
                                var intentVal = intentEl.GetString();
                                if (!string.IsNullOrEmpty(intentVal))
                                {
                                    intent = intentVal;
                                }
                            }

                            assignments.Add(new IntuneAssignment
                            {
                                PolicyId = appId ?? string.Empty,
                                PolicyName = appName ?? "Unknown",
                                PolicyType = PolicyType.Application,
                                Description = description ?? string.Empty,
                                AssignmentIntent = intent
                            });
                            break;
                        }
                    }
                }

                // Handle pagination
                nextUrl = root.TryGetProperty("@odata.nextLink", out var nextLinkEl) 
                    ? nextLinkEl.GetString() 
                    : null;
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
