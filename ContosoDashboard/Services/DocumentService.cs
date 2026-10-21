using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ContosoDashboard.Data;
using ContosoDashboard.Models;

namespace ContosoDashboard.Services;

public interface IDocumentService
{
    // Upload & change
    Task<UploadResult> UploadAsync(UploadRequest request, UserContext user);
    Task<OperationResult> UpdateMetadataAsync(int documentId, DocumentMetadata metadata, UserContext user);
    Task<OperationResult> ReplaceFileAsync(int documentId, UploadFile file, UserContext user);
    Task<OperationResult> DeleteAsync(int documentId, UserContext user);

    // Read
    Task<DocumentDetails?> GetAsync(int documentId, UserContext user);
    Task<FileContent?> OpenForDownloadAsync(int documentId, UserContext user);
    Task<FileContent?> OpenForPreviewAsync(int documentId, UserContext user);
    Task<PagedResult<DocumentSummary>> GetMyDocumentsAsync(DocumentQuery query, UserContext user);
    Task<PagedResult<DocumentSummary>> GetSharedWithMeAsync(DocumentQuery query, UserContext user);
    Task<PagedResult<DocumentSummary>> GetProjectDocumentsAsync(int projectId, DocumentQuery query, UserContext user);
    Task<List<DocumentSummary>> GetTaskDocumentsAsync(int taskId, UserContext user);
    Task<PagedResult<DocumentSummary>> SearchAsync(string? term, DocumentQuery query, UserContext user);

    // Sharing & attachments
    Task<OperationResult> ShareAsync(int documentId, ShareTarget target, UserContext user);
    Task<OperationResult> RemoveShareAsync(int shareId, UserContext user);
    Task<OperationResult> AttachToTaskAsync(int documentId, int taskId, UserContext user);
    Task<OperationResult> DetachFromTaskAsync(int documentId, UserContext user);

    // Scanning
    Task<OperationResult> RetryScanAsync(int documentId, UserContext user);

    // Dashboard
    Task<int> GetDocumentCountAsync(UserContext user);
    Task<List<DocumentSummary>> GetRecentDocumentsAsync(int count, UserContext user);
}

public class DocumentService : IDocumentService, IScanResultHandler, IScanMaintenance
{
    private readonly ApplicationDbContext _context;
    private readonly IFileStorageService _storage;
    private readonly IScanQueue _scanQueue;
    private readonly INotificationService _notifications;
    private readonly DocumentAccess _access;
    private readonly DocumentStorageOptions _options;
    private readonly ILogger<DocumentService> _logger;

    public DocumentService(
        ApplicationDbContext context,
        IFileStorageService storage,
        IScanQueue scanQueue,
        INotificationService notifications,
        DocumentAccess access,
        IOptions<DocumentStorageOptions> options,
        ILogger<DocumentService> logger)
    {
        _context = context;
        _storage = storage;
        _scanQueue = scanQueue;
        _notifications = notifications;
        _access = access;
        _options = options.Value;
        _logger = logger;
    }

    // ---------------------------------------------------------------- Upload & change

    public async Task<UploadResult> UploadAsync(UploadRequest request, UserContext user)
    {
        var metadataError = DocumentFileValidator.ValidateMetadata(request.Title, request.Description, request.Category);
        if (metadataError != null)
        {
            return UploadResult.Fail(DocumentError.InvalidMetadata, metadataError);
        }

        var tagError = DocumentFileValidator.NormalizeTags(request.Tags, out var tags);
        if (tagError != null)
        {
            return UploadResult.Fail(DocumentError.InvalidMetadata, tagError);
        }

        if (request.TaskId != null)
        {
            return UploadResult.Fail(DocumentError.InvalidMetadata, "Attaching documents to tasks is not available yet.");
        }

        var validation = DocumentFileValidator.Validate(request.File.FileName, request.File.Stream, _options.MaxFileSizeBytes);
        if (!validation.IsValid)
        {
            return UploadResult.Fail(validation.Error, validation.Message!);
        }

        if (request.ProjectId != null && !await _access.CanUploadToProjectAsync(request.ProjectId.Value, user))
        {
            return UploadResult.Fail(DocumentError.NotProjectMember, "You can only add documents to projects you belong to.");
        }

        var storageKey = BuildStorageKey(user.UserId, request.ProjectId, validation.Extension);

        try
        {
            request.File.Stream.Position = 0;
            await _storage.UploadAsync(request.File.Stream, storageKey, validation.ContentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store uploaded file for user {UserId}", user.UserId);
            return UploadResult.Fail(DocumentError.StorageFailure, "The file could not be saved. Please try again.");
        }

        var now = DateTime.UtcNow;
        var document = new Document
        {
            Title = request.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Category = request.Category,
            Tags = tags,
            OriginalFileName = DocumentFileValidator.SanitizeFileName(request.File.FileName),
            FileType = validation.ContentType,
            FileSize = request.File.Stream.Length,
            FilePath = storageKey,
            UploaderUserId = user.UserId,
            ProjectId = request.ProjectId,
            UploadDate = now,
            UpdatedDate = now,
            ScanStatus = ScanStatuses.Pending,
            ScanAttemptId = Guid.NewGuid(),
            ScanRequestedDate = now
        };

        try
        {
            _context.Documents.Add(document);
            await _context.SaveChangesAsync();

            LogActivity(document, DocumentActions.Upload, user.UserId);
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save document record; removing stored file {Key}", storageKey);
            await TryDeleteFileAsync(storageKey);
            return UploadResult.Fail(DocumentError.StorageFailure, "The file could not be saved. Please try again.");
        }

        await EnqueueScanAsync(document);

        return UploadResult.Created(document.DocumentId, "Uploaded — scanning for malware.");
    }

    public async Task<OperationResult> UpdateMetadataAsync(int documentId, DocumentMetadata metadata, UserContext user)
    {
        var document = await GetManageableAsync(documentId, user);
        if (document == null)
        {
            return NotFound();
        }

        var metadataError = DocumentFileValidator.ValidateMetadata(metadata.Title, metadata.Description, metadata.Category);
        if (metadataError != null)
        {
            return OperationResult.Fail(DocumentError.InvalidMetadata, metadataError);
        }

        var tagError = DocumentFileValidator.NormalizeTags(metadata.Tags, out var tags);
        if (tagError != null)
        {
            return OperationResult.Fail(DocumentError.InvalidMetadata, tagError);
        }

        document.Title = metadata.Title.Trim();
        document.Description = string.IsNullOrWhiteSpace(metadata.Description) ? null : metadata.Description.Trim();
        document.Category = metadata.Category;
        document.Tags = tags;
        document.UpdatedDate = DateTime.UtcNow;

        LogActivity(document, DocumentActions.Edit, user.UserId);
        await _context.SaveChangesAsync();

        return OperationResult.Ok("Document updated.");
    }

    public async Task<OperationResult> ReplaceFileAsync(int documentId, UploadFile file, UserContext user)
    {
        var document = await GetManageableAsync(documentId, user);
        if (document == null)
        {
            return NotFound();
        }

        var validation = DocumentFileValidator.Validate(file.FileName, file.Stream, _options.MaxFileSizeBytes);
        if (!validation.IsValid)
        {
            return OperationResult.Fail(validation.Error, validation.Message!);
        }

        var oldKey = document.FilePath;
        var newKey = BuildStorageKey(document.UploaderUserId, document.ProjectId, validation.Extension);

        try
        {
            file.Stream.Position = 0;
            await _storage.UploadAsync(file.Stream, newKey, validation.ContentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store replacement file for document {DocumentId}", documentId);
            return OperationResult.Fail(DocumentError.StorageFailure, "The file could not be saved. Please try again.");
        }

        var now = DateTime.UtcNow;
        document.FilePath = newKey;
        document.OriginalFileName = DocumentFileValidator.SanitizeFileName(file.FileName);
        document.FileType = validation.ContentType;
        document.FileSize = file.Stream.Length;
        document.UpdatedDate = now;
        document.ScanStatus = ScanStatuses.Pending;
        document.ScanAttemptId = Guid.NewGuid();
        document.ScanRequestedDate = now;
        document.ScanCompletedDate = null;
        document.ScanDetail = null;
        LogActivity(document, DocumentActions.Replace, user.UserId);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update record for replaced document {DocumentId}", documentId);
            await TryDeleteFileAsync(newKey);
            return OperationResult.Fail(DocumentError.StorageFailure, "The file could not be saved. Please try again.");
        }

        await EnqueueScanAsync(document);
        await TryDeleteFileAsync(oldKey);

        return OperationResult.Ok("File replaced — scanning for malware.");
    }

    public async Task<OperationResult> DeleteAsync(int documentId, UserContext user)
    {
        var document = await GetManageableAsync(documentId, user);
        if (document == null)
        {
            return NotFound();
        }

        var key = document.FilePath;
        LogActivity(document, DocumentActions.Delete, user.UserId);
        _context.Documents.Remove(document); // shares are removed by cascade
        await _context.SaveChangesAsync();

        await TryDeleteFileAsync(key);

        return OperationResult.Ok("Document deleted.");
    }

    // ---------------------------------------------------------------- Read

    public async Task<DocumentDetails?> GetAsync(int documentId, UserContext user)
    {
        return await _access.Viewable(_context.Documents, user, includeOwnNonAvailable: true)
            .Where(d => d.DocumentId == documentId)
            .Select(d => new DocumentDetails
            {
                DocumentId = d.DocumentId,
                Title = d.Title,
                Description = d.Description,
                Category = d.Category,
                Tags = d.Tags,
                FileType = d.FileType,
                FileSize = d.FileSize,
                UploadDate = d.UploadDate,
                UpdatedDate = d.UpdatedDate,
                OriginalFileName = d.OriginalFileName,
                UploaderUserId = d.UploaderUserId,
                UploaderName = d.UploaderUser.DisplayName,
                ProjectId = d.ProjectId,
                ProjectName = d.Project != null ? d.Project.Name : null,
                TaskId = d.TaskId,
                ScanStatus = d.ScanStatus,
                ScanDetail = d.ScanDetail
            })
            .FirstOrDefaultAsync();
    }

    public Task<FileContent?> OpenForDownloadAsync(int documentId, UserContext user)
        => OpenAsync(documentId, user, DocumentActions.Download, previewOnly: false);

    public Task<FileContent?> OpenForPreviewAsync(int documentId, UserContext user)
        => OpenAsync(documentId, user, DocumentActions.Preview, previewOnly: true);

    public async Task<PagedResult<DocumentSummary>> GetMyDocumentsAsync(DocumentQuery query, UserContext user)
    {
        var documents = _context.Documents.Where(d => d.UploaderUserId == user.UserId);
        return await ExecuteQueryAsync(documents, query);
    }

    public async Task<PagedResult<DocumentSummary>> SearchAsync(string? term, DocumentQuery query, UserContext user)
    {
        var documents = _access.Viewable(_context.Documents, user);

        if (!string.IsNullOrWhiteSpace(term))
        {
            var pattern = "%" + EscapeLike(term.Trim()) + "%";
            documents = documents.Where(d =>
                EF.Functions.Like(d.Title, pattern, "\\") ||
                (d.Description != null && EF.Functions.Like(d.Description, pattern, "\\")) ||
                (d.Tags != null && EF.Functions.Like(d.Tags, pattern, "\\")) ||
                EF.Functions.Like(d.UploaderUser.DisplayName, pattern, "\\") ||
                (d.Project != null && EF.Functions.Like(d.Project.Name, pattern, "\\")));
        }

        return await ExecuteQueryAsync(documents, query);
    }

    // ---------------------------------------------------------------- Scanning

    public async Task<bool> ApplyVerdictAsync(ScanVerdictMessage verdict, CancellationToken ct = default)
    {
        var document = await _context.Documents.FirstOrDefaultAsync(d => d.DocumentId == verdict.DocumentId, ct);

        if (document == null ||
            document.ScanStatus != ScanStatuses.Pending ||
            document.ScanAttemptId != verdict.ScanAttemptId)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        document.ScanCompletedDate = now;

        switch (verdict.Verdict)
        {
            case ScanVerdict.Clean:
                document.ScanStatus = ScanStatuses.Available;
                document.ScanDetail = null;
                LogActivity(document, DocumentActions.ScanCompleted, document.UploaderUserId, verdict.Scanner);
                await _context.SaveChangesAsync(ct);
                await NotifyProjectOfNewDocumentAsync(document);
                break;

            case ScanVerdict.Infected:
                document.ScanStatus = ScanStatuses.Rejected;
                document.ScanDetail = Truncate(verdict.Reason ?? "The file was flagged by the security scan.", 500);
                LogActivity(document, DocumentActions.ScanRejected, document.UploaderUserId, verdict.Scanner);
                await _context.SaveChangesAsync(ct);
                await TryDeleteFileAsync(document.FilePath);
                await NotifyUserAsync(document.UploaderUserId, NotificationType.DocumentRejected,
                    "Document rejected",
                    $"'{document.Title}' was rejected by the security scan.",
                    NotificationPriority.Important);
                break;

            default:
                document.ScanStatus = ScanStatuses.ScanFailed;
                document.ScanDetail = Truncate(verdict.Reason ?? "The security scan could not be completed.", 500);
                LogActivity(document, DocumentActions.ScanFailed, document.UploaderUserId, verdict.Scanner);
                await _context.SaveChangesAsync(ct);
                break;
        }

        return true;
    }

    public async Task<OperationResult> RetryScanAsync(int documentId, UserContext user)
    {
        var document = await _context.Documents.FirstOrDefaultAsync(d => d.DocumentId == documentId);

        if (document == null || (document.UploaderUserId != user.UserId && !user.IsAdministrator))
        {
            return NotFound();
        }

        if (document.ScanStatus != ScanStatuses.ScanFailed)
        {
            return OperationResult.Fail(DocumentError.InvalidState, "Only documents whose scan failed can be rescanned.");
        }

        document.ScanStatus = ScanStatuses.Pending;
        document.ScanAttemptId = Guid.NewGuid();
        document.ScanRequestedDate = DateTime.UtcNow;
        document.ScanCompletedDate = null;
        document.ScanDetail = null;
        await _context.SaveChangesAsync();

        await EnqueueScanAsync(document);

        return OperationResult.Ok("Scan restarted.");
    }

    public async Task<int> FailTimedOutScansAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - timeout;
        var timedOut = await _context.Documents
            .Where(d => d.ScanStatus == ScanStatuses.Pending && d.ScanRequestedDate < cutoff)
            .ToListAsync(ct);

        foreach (var document in timedOut)
        {
            document.ScanStatus = ScanStatuses.ScanFailed;
            document.ScanCompletedDate = DateTime.UtcNow;
            document.ScanDetail = "The security scan did not finish in time.";
            LogActivity(document, DocumentActions.ScanFailed, document.UploaderUserId, "timeout");
        }

        if (timedOut.Count > 0)
        {
            await _context.SaveChangesAsync(ct);
        }

        return timedOut.Count;
    }

    public async Task<IReadOnlyList<ScanRequest>> GetPendingScanRequestsAsync(CancellationToken ct = default)
    {
        return await _context.Documents
            .Where(d => d.ScanStatus == ScanStatuses.Pending)
            .Select(d => new ScanRequest(d.DocumentId, d.ScanAttemptId, d.FilePath, d.FileSize, DateTime.UtcNow))
            .ToListAsync(ct);
    }

    // ---------------------------------------------------------------- Later user stories

    public Task<PagedResult<DocumentSummary>> GetSharedWithMeAsync(DocumentQuery query, UserContext user)
        => throw new NotImplementedException();

    public Task<PagedResult<DocumentSummary>> GetProjectDocumentsAsync(int projectId, DocumentQuery query, UserContext user)
        => throw new NotImplementedException();

    public Task<List<DocumentSummary>> GetTaskDocumentsAsync(int taskId, UserContext user)
        => throw new NotImplementedException();

    public Task<OperationResult> ShareAsync(int documentId, ShareTarget target, UserContext user)
        => throw new NotImplementedException();

    public Task<OperationResult> RemoveShareAsync(int shareId, UserContext user)
        => throw new NotImplementedException();

    public Task<OperationResult> AttachToTaskAsync(int documentId, int taskId, UserContext user)
        => throw new NotImplementedException();

    public Task<OperationResult> DetachFromTaskAsync(int documentId, UserContext user)
        => throw new NotImplementedException();

    public Task<int> GetDocumentCountAsync(UserContext user)
        => throw new NotImplementedException();

    public Task<List<DocumentSummary>> GetRecentDocumentsAsync(int count, UserContext user)
        => throw new NotImplementedException();

    // ---------------------------------------------------------------- Helpers

    private static OperationResult NotFound()
        => OperationResult.Fail(DocumentError.NotFound, "The document was not found.");

    private static string BuildStorageKey(int userId, int? projectId, string extension)
        => $"{userId}/{(projectId?.ToString() ?? "personal")}/{Guid.NewGuid()}{extension}";

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private Task<Document?> GetManageableAsync(int documentId, UserContext user)
        => _access.Manageable(_context.Documents, user).FirstOrDefaultAsync(d => d.DocumentId == documentId);

    private void LogActivity(Document document, string action, int userId, string? details = null)
    {
        _context.DocumentActivities.Add(new DocumentActivity
        {
            DocumentId = document.DocumentId,
            DocumentTitle = document.Title,
            FileType = document.FileType,
            UserId = userId,
            Action = action,
            Timestamp = DateTime.UtcNow,
            Details = details
        });
    }

    private async Task EnqueueScanAsync(Document document)
    {
        try
        {
            await _scanQueue.EnqueueAsync(new ScanRequest(
                document.DocumentId, document.ScanAttemptId, document.FilePath, document.FileSize, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            // The document stays Pending; startup recovery or the timeout sweeper handles it
            _logger.LogError(ex, "Failed to enqueue scan for document {DocumentId}", document.DocumentId);
        }
    }

    private async Task TryDeleteFileAsync(string storageKey)
    {
        try
        {
            await _storage.DeleteAsync(storageKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete stored file {Key}", storageKey);
        }
    }

    private async Task<FileContent?> OpenAsync(int documentId, UserContext user, string action, bool previewOnly)
    {
        var document = await _access.Viewable(_context.Documents, user)
            .FirstOrDefaultAsync(d => d.DocumentId == documentId);

        if (document == null || (previewOnly && !DocumentFileValidator.IsPreviewable(document.FileType)))
        {
            return null;
        }

        Stream stream;
        try
        {
            stream = await _storage.DownloadAsync(document.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stored file missing for document {DocumentId}", documentId);
            return null;
        }

        LogActivity(document, action, user.UserId);
        await _context.SaveChangesAsync();

        return new FileContent
        {
            Content = stream,
            FileName = document.OriginalFileName,
            ContentType = document.FileType
        };
    }

    private async Task<PagedResult<DocumentSummary>> ExecuteQueryAsync(IQueryable<Document> documents, DocumentQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            documents = documents.Where(d => d.Category == query.Category);
        }

        if (query.ProjectId != null)
        {
            documents = documents.Where(d => d.ProjectId == query.ProjectId);
        }

        if (query.FromDate != null)
        {
            var from = query.FromDate.Value.Date;
            documents = documents.Where(d => d.UploadDate >= from);
        }

        if (query.ToDate != null)
        {
            var toExclusive = query.ToDate.Value.Date.AddDays(1);
            documents = documents.Where(d => d.UploadDate < toExclusive);
        }

        var total = await documents.CountAsync();

        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(query.Page, 1);

        var items = await ApplySort(documents, query)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new DocumentSummary
            {
                DocumentId = d.DocumentId,
                Title = d.Title,
                Description = d.Description,
                Category = d.Category,
                Tags = d.Tags,
                FileType = d.FileType,
                FileSize = d.FileSize,
                UploadDate = d.UploadDate,
                UploaderUserId = d.UploaderUserId,
                UploaderName = d.UploaderUser.DisplayName,
                ProjectId = d.ProjectId,
                ProjectName = d.Project != null ? d.Project.Name : null,
                TaskId = d.TaskId,
                ScanStatus = d.ScanStatus,
                ScanDetail = d.ScanDetail
            })
            .ToListAsync();

        return new PagedResult<DocumentSummary> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    private static IOrderedQueryable<Document> ApplySort(IQueryable<Document> documents, DocumentQuery query)
    {
        var descending = query.Descending;
        return query.SortBy switch
        {
            DocumentQuery.SortTitle => descending
                ? documents.OrderByDescending(d => d.Title).ThenByDescending(d => d.DocumentId)
                : documents.OrderBy(d => d.Title).ThenBy(d => d.DocumentId),
            DocumentQuery.SortCategory => descending
                ? documents.OrderByDescending(d => d.Category).ThenByDescending(d => d.DocumentId)
                : documents.OrderBy(d => d.Category).ThenBy(d => d.DocumentId),
            DocumentQuery.SortFileSize => descending
                ? documents.OrderByDescending(d => d.FileSize).ThenByDescending(d => d.DocumentId)
                : documents.OrderBy(d => d.FileSize).ThenBy(d => d.DocumentId),
            _ => descending
                ? documents.OrderByDescending(d => d.UploadDate).ThenByDescending(d => d.DocumentId)
                : documents.OrderBy(d => d.UploadDate).ThenBy(d => d.DocumentId)
        };
    }

    private async Task NotifyProjectOfNewDocumentAsync(Document document)
    {
        if (document.ProjectId == null)
        {
            return;
        }

        var project = await _context.Projects
            .Where(p => p.ProjectId == document.ProjectId)
            .Select(p => new { p.Name, p.ProjectManagerId })
            .FirstOrDefaultAsync();

        if (project == null)
        {
            return;
        }

        var recipientIds = await _context.ProjectMembers
            .Where(pm => pm.ProjectId == document.ProjectId)
            .Select(pm => pm.UserId)
            .ToListAsync();
        recipientIds.Add(project.ProjectManagerId);

        foreach (var userId in recipientIds.Distinct().Where(id => id != document.UploaderUserId))
        {
            await NotifyUserAsync(userId, NotificationType.DocumentAdded,
                "New document added",
                $"New document '{document.Title}' added to {project.Name}.",
                NotificationPriority.Informational);
        }
    }

    // Creates an in-app notification unless the recipient turned in-app notifications off
    private async Task NotifyUserAsync(int userId, NotificationType type, string title, string message, NotificationPriority priority)
    {
        var enabled = await _context.Users
            .Where(u => u.UserId == userId)
            .Select(u => (bool?)u.InAppNotificationsEnabled)
            .FirstOrDefaultAsync();

        if (enabled != true)
        {
            return;
        }

        await _notifications.CreateNotificationAsync(new Notification
        {
            UserId = userId,
            Type = type,
            Title = Truncate(title, 255),
            Message = Truncate(message, 1000),
            Priority = priority
        });
    }
}
