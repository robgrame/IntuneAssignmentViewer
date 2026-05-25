using IntuneAssignmentViewer.Models;

namespace IntuneAssignmentViewer.Services;

public interface IIntuneService
{
    Task<List<GroupInfo>> SearchGroupsAsync(string searchTerm);
    Task<List<IntuneAssignment>> GetAssignmentsForGroupAsync(string groupId, PolicyType? filterType = null);
}
