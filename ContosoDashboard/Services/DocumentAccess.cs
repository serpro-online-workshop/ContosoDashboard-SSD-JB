using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ContosoDashboard.Data;
using ContosoDashboard.Models;

namespace ContosoDashboard.Services;

// Single source of truth for document access rules. The same expressions back both the
// per-document checks and every list, search, and count query so they cannot drift apart.
public class DocumentAccess
{
    private readonly ApplicationDbContext _context;

    public DocumentAccess(ApplicationDbContext context)
    {
        _context = context;
    }

    // View/download/preview rule. Only Available documents are visible; when includeOwnNonAvailable is
    // set, the uploader and Administrators additionally see non-available rows (status display only).
    public static Expression<Func<Document, bool>> ViewExpression(UserContext user, bool includeOwnNonAvailable = false)
    {
        var uid = user.UserId;
        var dept = user.Department;
        var isAdmin = user.IsAdministrator;
        var isTeamLead = user.IsTeamLead;

        return d =>
            (d.ScanStatus == ScanStatuses.Available &&
                (isAdmin
                 || d.UploaderUserId == uid
                 || (d.ProjectId != null &&
                     (d.Project!.ProjectManagerId == uid || d.Project.ProjectMembers.Any(pm => pm.UserId == uid)))
                 || d.Shares.Any(s => s.SharedWithUserId == uid ||
                                      (dept != null && s.SharedWithDepartment == dept))
                 || (isTeamLead && dept != null && d.UploaderUser.Department == dept &&
                     !(d.Category == DocumentCategories.PersonalFiles && d.ProjectId == null))))
            || (includeOwnNonAvailable && d.ScanStatus != ScanStatuses.Available &&
                (isAdmin || d.UploaderUserId == uid));
    }

    // Edit/replace/delete rule (any scan state). Share recipients never get this permission.
    public static Expression<Func<Document, bool>> ManageExpression(UserContext user)
    {
        var uid = user.UserId;
        var dept = user.Department;
        var isAdmin = user.IsAdministrator;
        var isTeamLead = user.IsTeamLead;

        return d =>
            isAdmin
            || d.UploaderUserId == uid
            || (d.ProjectId != null && d.Project!.ProjectManagerId == uid)
            || (isTeamLead && dept != null && d.UploaderUser.Department == dept &&
                !(d.Category == DocumentCategories.PersonalFiles && d.ProjectId == null));
    }

    public static Expression<Func<Document, bool>> ShareExpression(UserContext user)
    {
        var uid = user.UserId;
        var isAdmin = user.IsAdministrator;
        return d => isAdmin || d.UploaderUserId == uid;
    }

    public IQueryable<Document> Viewable(IQueryable<Document> query, UserContext user, bool includeOwnNonAvailable = false)
        => query.Where(ViewExpression(user, includeOwnNonAvailable));

    public IQueryable<Document> Manageable(IQueryable<Document> query, UserContext user)
        => query.Where(ManageExpression(user));

    public Task<bool> CanViewAsync(int documentId, UserContext user, CancellationToken ct = default)
        => _context.Documents.Where(d => d.DocumentId == documentId).Where(ViewExpression(user)).AnyAsync(ct);

    public Task<bool> CanManageAsync(int documentId, UserContext user, CancellationToken ct = default)
        => _context.Documents.Where(d => d.DocumentId == documentId).Where(ManageExpression(user)).AnyAsync(ct);

    public Task<bool> CanShareAsync(int documentId, UserContext user, CancellationToken ct = default)
        => _context.Documents.Where(d => d.DocumentId == documentId).Where(ShareExpression(user)).AnyAsync(ct);

    // Uploading to a project requires being its manager, one of its members, or an Administrator
    public async Task<bool> CanUploadToProjectAsync(int projectId, UserContext user, CancellationToken ct = default)
    {
        if (user.IsAdministrator)
        {
            return await _context.Projects.AnyAsync(p => p.ProjectId == projectId, ct);
        }

        return await _context.Projects.AnyAsync(p => p.ProjectId == projectId &&
            (p.ProjectManagerId == user.UserId || p.ProjectMembers.Any(pm => pm.UserId == user.UserId)), ct);
    }
}
