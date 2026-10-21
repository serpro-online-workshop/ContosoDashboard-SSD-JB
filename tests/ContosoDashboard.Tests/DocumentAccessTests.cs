using Microsoft.EntityFrameworkCore;
using ContosoDashboard.Models;
using ContosoDashboard.Services;

namespace ContosoDashboard.Tests;

public class DocumentAccessTests : IDisposable
{
    private readonly TestDb _db = new();

    private readonly UserContext _admin = TestDb.Admin;
    private readonly UserContext _pm = TestDb.ProjectManager;
    private readonly UserContext _tl = TestDb.TeamLead;
    private readonly UserContext _emp = TestDb.Employee;
    private readonly UserContext _other = TestDb.OtherEngineer;
    private readonly UserContext _sales = TestDb.SalesEmployee;
    private readonly UserContext _salesTl = TestDb.SalesTeamLead;

    public void Dispose() => _db.Dispose();

    private HashSet<int> ViewableBy(UserContext user, bool includeOwnNonAvailable = false)
    {
        using var context = _db.NewContext();
        var access = new DocumentAccess(context);
        return access.Viewable(context.Documents, user, includeOwnNonAvailable)
            .Select(d => d.DocumentId).ToHashSet();
    }

    private HashSet<int> ManageableBy(UserContext user)
    {
        using var context = _db.NewContext();
        var access = new DocumentAccess(context);
        return access.Manageable(context.Documents, user).Select(d => d.DocumentId).ToHashSet();
    }

    private static string Names(IEnumerable<UserContext> users) => string.Join(", ", users.Select(u => $"{u.Role}/{u.UserId}"));

    private void AssertViewers(int documentId, params UserContext[] expected)
    {
        var actual = TestDb.AllUsers.Where(u => ViewableBy(u).Contains(documentId)).ToList();
        Assert.True(expected.ToHashSet().SetEquals(actual),
            $"Viewers differ. Expected: [{Names(expected)}] Actual: [{Names(actual)}]");
    }

    private void AssertManagers(int documentId, params UserContext[] expected)
    {
        var actual = TestDb.AllUsers.Where(u => ManageableBy(u).Contains(documentId)).ToList();
        Assert.True(expected.ToHashSet().SetEquals(actual),
            $"Managers differ. Expected: [{Names(expected)}] Actual: [{Names(actual)}]");
    }

    [Fact]
    public void PersonalFile_IsPrivateToOwnerAndAdministrator()
    {
        var id = _db.AddDocument(_emp, DocumentCategories.PersonalFiles);

        AssertViewers(id, _emp, _admin);
        AssertManagers(id, _emp, _admin);
    }

    [Fact]
    public void ProjectDocument_VisibleToManagerMembersTeamLeadAdmin_ManageableByManager()
    {
        var id = _db.AddDocument(_emp, DocumentCategories.ProjectDocuments, projectId: 1);

        AssertViewers(id, _emp, _admin, _pm, _tl);
        AssertManagers(id, _emp, _admin, _pm, _tl);
    }

    [Fact]
    public void ProjectLessTeamDocument_VisibleToSameDepartmentTeamLead()
    {
        var id = _db.AddDocument(_emp, DocumentCategories.TeamResources);

        AssertViewers(id, _emp, _admin, _tl);
        AssertManagers(id, _emp, _admin, _tl);
    }

    [Fact]
    public void TeamLead_CannotSeeUnsharedProjectLessPersonalFiles_ButCanSeeInProject()
    {
        var personalNoProject = _db.AddDocument(_emp, DocumentCategories.PersonalFiles);
        var personalInProject = _db.AddDocument(_emp, DocumentCategories.PersonalFiles, projectId: 1);

        Assert.DoesNotContain(personalNoProject, ViewableBy(_tl));
        Assert.Contains(personalInProject, ViewableBy(_tl));
    }

    [Fact]
    public void ProjectManagerRole_WithoutProjectLink_GetsNoTeamLeadPrivileges()
    {
        var id = _db.AddDocument(_other, DocumentCategories.TeamResources);

        Assert.DoesNotContain(id, ViewableBy(_pm));
        Assert.DoesNotContain(id, ManageableBy(_pm));
    }

    [Fact]
    public void UserShare_GrantsViewOnly()
    {
        var id = _db.AddDocument(_emp, DocumentCategories.PersonalFiles);
        _db.ShareWithUser(id, _other, _emp);

        AssertViewers(id, _emp, _admin, _other);
        AssertManagers(id, _emp, _admin);
    }

    [Fact]
    public void DepartmentShare_GrantsViewToThatDepartmentOnly()
    {
        var id = _db.AddDocument(_emp, DocumentCategories.PersonalFiles);
        _db.ShareWithDepartment(id, "Sales", _emp);

        AssertViewers(id, _emp, _admin, _sales, _salesTl);
        AssertManagers(id, _emp, _admin);
    }

    [Fact]
    public void ShareToTeamLead_DoesNotGrantManageForPrivatePersonalFile()
    {
        var id = _db.AddDocument(_emp, DocumentCategories.PersonalFiles);
        _db.ShareWithUser(id, _tl, _emp);

        Assert.Contains(id, ViewableBy(_tl));
        Assert.DoesNotContain(id, ManageableBy(_tl));
    }

    [Theory]
    [InlineData(ScanStatuses.Pending)]
    [InlineData(ScanStatuses.Rejected)]
    [InlineData(ScanStatuses.ScanFailed)]
    public void NonAvailableDocuments_AreInvisibleToEveryone_ExceptStatusListForUploaderAndAdmin(string status)
    {
        var id = _db.AddDocument(_emp, DocumentCategories.ProjectDocuments, projectId: 1, scanStatus: status);

        foreach (var user in TestDb.AllUsers)
        {
            Assert.DoesNotContain(id, ViewableBy(user));
        }

        foreach (var user in TestDb.AllUsers)
        {
            var expected = user.UserId == _emp.UserId || user.IsAdministrator;
            Assert.Equal(expected, ViewableBy(user, includeOwnNonAvailable: true).Contains(id));
        }
    }

    [Fact]
    public void NonAvailableDocuments_CanStillBeManaged_ForCleanup()
    {
        var id = _db.AddDocument(_emp, scanStatus: ScanStatuses.Rejected);

        Assert.Contains(id, ManageableBy(_emp));
        Assert.Contains(id, ManageableBy(_admin));
    }

    [Fact]
    public async Task SingleDocumentChecks_AgreeWithListQueries_ForEveryUser()
    {
        var ids = new[]
        {
            _db.AddDocument(_emp, DocumentCategories.PersonalFiles),
            _db.AddDocument(_emp, DocumentCategories.ProjectDocuments, projectId: 1),
            _db.AddDocument(_other, DocumentCategories.TeamResources),
            _db.AddDocument(_sales, DocumentCategories.Reports),
            _db.AddDocument(_emp, scanStatus: ScanStatuses.Pending)
        };
        _db.ShareWithDepartment(ids[3], "Engineering", _sales);

        foreach (var user in TestDb.AllUsers)
        {
            var viewable = ViewableBy(user);
            var manageable = ManageableBy(user);

            using var context = _db.NewContext();
            var access = new DocumentAccess(context);

            foreach (var id in ids)
            {
                Assert.Equal(viewable.Contains(id), await access.CanViewAsync(id, user));
                Assert.Equal(manageable.Contains(id), await access.CanManageAsync(id, user));
            }
        }
    }

    [Fact]
    public async Task CanShare_OnlyOwnerAndAdministrator()
    {
        var id = _db.AddDocument(_emp, DocumentCategories.ProjectDocuments, projectId: 1);

        using var context = _db.NewContext();
        var access = new DocumentAccess(context);

        Assert.True(await access.CanShareAsync(id, _emp));
        Assert.True(await access.CanShareAsync(id, _admin));
        Assert.False(await access.CanShareAsync(id, _pm));
        Assert.False(await access.CanShareAsync(id, _tl));
        Assert.False(await access.CanShareAsync(id, _other));
    }

    [Fact]
    public async Task CanUploadToProject_RequiresMembershipManagementOrAdmin()
    {
        using var context = _db.NewContext();
        var access = new DocumentAccess(context);

        Assert.True(await access.CanUploadToProjectAsync(1, _emp));      // member
        Assert.True(await access.CanUploadToProjectAsync(1, _tl));       // member
        Assert.True(await access.CanUploadToProjectAsync(1, _pm));       // manager
        Assert.True(await access.CanUploadToProjectAsync(1, _admin));
        Assert.False(await access.CanUploadToProjectAsync(1, _other));   // not a member
        Assert.False(await access.CanUploadToProjectAsync(1, _sales));
        Assert.False(await access.CanUploadToProjectAsync(999, _admin)); // unknown project
    }
}
