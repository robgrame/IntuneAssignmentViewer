using IntuneAssignmentViewer.Models;

namespace IntuneAssignmentViewer.Services;

public interface IIntuneService
{
    Task<List<GroupInfo>> SearchGroupsAsync(string searchTerm);
    Task<List<IntuneAssignment>> GetAssignmentsForGroupAsync(string groupId, PolicyType? filterType = null);
    Task<List<PolicyAssignmentDetail>> GetPolicyAssignmentsAsync(string assignmentsUrl);
    void InvalidateCache();
    Task WarmupAsync(CancellationToken ct = default);
}
