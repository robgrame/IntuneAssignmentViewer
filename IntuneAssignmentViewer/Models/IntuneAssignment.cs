namespace IntuneAssignmentViewer.Models;

public enum PolicyType
{
    Configuration,
    Compliance,
    Application,
    AppProtection,
    AppConfiguration,
    EndpointSecurity,
    Script,
    AdministrativeTemplate,
    Provisioning
}

public class IntuneAssignment
{
    public string PolicyId { get; set; } = string.Empty;
    public string PolicyName { get; set; } = string.Empty;
    public PolicyType PolicyType { get; set; }
    public string PolicySubType { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string AssignmentIntent { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>
    /// Graph URL pointing to the /assignments collection for this policy.
    /// Used to re-fetch all targets for drill-down view.
    /// </summary>
    public string AssignmentsUrl { get; set; } = string.Empty;
}

public class GroupInfo
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public enum AssignmentTargetType
{
    Group,
    AllUsers,
    AllDevices,
    Unknown
}

public class PolicyAssignmentDetail
{
    public AssignmentTargetType TargetType { get; set; }
    public string GroupId { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    /// <summary>"Required" / "Available" / "Uninstall" for apps; empty for other policies.</summary>
    public string Intent { get; set; } = string.Empty;
    /// <summary>True if the target is an ExclusionGroupAssignmentTarget.</summary>
    public bool IsExcluded { get; set; }
    public string Mode => IsExcluded ? "Exclude" : "Include";
    public string FilterId { get; set; } = string.Empty;
    public string FilterType { get; set; } = string.Empty;
}
