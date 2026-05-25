namespace IntuneAssignmentViewer.Models;

public enum PolicyType
{
    Configuration,
    Compliance,
    Application
}

public class IntuneAssignment
{
    public string PolicyId { get; set; } = string.Empty;
    public string PolicyName { get; set; } = string.Empty;
    public PolicyType PolicyType { get; set; }
    public string AssignmentIntent { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class GroupInfo
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
