# Contract: DocumentService

Application service consumed by Razor components and endpoints. Every method takes the requesting user's identity (id, role, department) and enforces authorization itself; callers never pre-filter. Unauthorized and nonexistent documents yield the same result ("not found" / empty).

```csharp
public interface IDocumentService
{
    // Upload & change
    Task<UploadResult> UploadAsync(UploadRequest request, UserContext user);
    Task<OperationResult> UpdateMetadataAsync(int documentId, DocumentMetadata metadata, UserContext user);
    Task<OperationResult> ReplaceFileAsync(int documentId, UploadFile file, UserContext user);
    Task<OperationResult> DeleteAsync(int documentId, UserContext user);

    // Read
    Task<DocumentDetails?> GetAsync(int documentId, UserContext user);
    Task<FileContent?> OpenForDownloadAsync(int documentId, UserContext user);   // logs Download
    Task<FileContent?> OpenForPreviewAsync(int documentId, UserContext user);    // logs Preview; PDF/JPEG/PNG only
    Task<PagedResult<DocumentSummary>> GetMyDocumentsAsync(DocumentQuery query, UserContext user);
    Task<PagedResult<DocumentSummary>> GetSharedWithMeAsync(DocumentQuery query, UserContext user);
    Task<PagedResult<DocumentSummary>> GetProjectDocumentsAsync(int projectId, DocumentQuery query, UserContext user);
    Task<List<DocumentSummary>> GetTaskDocumentsAsync(int taskId, UserContext user);
    Task<PagedResult<DocumentSummary>> SearchAsync(string term, DocumentQuery query, UserContext user);

    // Sharing & attachments
    Task<OperationResult> ShareAsync(int documentId, ShareTarget target, UserContext user);
    Task<OperationResult> RemoveShareAsync(int shareId, UserContext user);
    Task<OperationResult> AttachToTaskAsync(int documentId, int taskId, UserContext user);
    Task<OperationResult> DetachFromTaskAsync(int documentId, UserContext user);

    // Scanning
    Task<OperationResult> RetryScanAsync(int documentId, UserContext user);     // only from ScanFailed
    // IScanResultHandler.ApplyVerdictAsync is also implemented by this service (see scan-pipeline.md)

    // Dashboard
    Task<int> GetDocumentCountAsync(UserContext user);
    Task<List<DocumentSummary>> GetRecentDocumentsAsync(int count, UserContext user);   // user's own uploads
}
```

## Key types (shape only)

- `UserContext`: `UserId`, `Role`, `Department`; built from claims (never from client input).
- `UploadRequest`: `Title`, `Description?`, `Category`, `ProjectId?`, `TaskId?`, `Tags`, `UploadFile File`.
- `UploadFile`: `FileName`, `ContentType`, `Size`, `Stream` (memory copy).
- `DocumentQuery`: `Category?`, `ProjectId?`, `FromDate?`, `ToDate?`, `SortBy` (`Title|UploadDate|Category|FileSize`), `Descending`, `Page`, `PageSize` (default 50).
- `OperationResult` / `UploadResult`: `Success`, `ErrorCode`, user-safe `Message`, and the created `DocumentId` for uploads.

## Error codes (user-safe messages)

`NotFound`, `Forbidden` (treated as NotFound for single documents), `FileTooLarge`, `EmptyFile`, `UnsupportedType`, `ContentMismatch`, `InvalidMetadata`, `NotProjectMember`, `NotAvailable` (scan pending/failed/rejected), `StorageFailure`.

## Behavior rules

1. **Upload**: validate → authorize project → generate key → store file → insert record with `ScanStatus = Pending` and a new `ScanAttemptId` → enqueue `ScanRequest` → write `Upload` activity. The call returns as soon as the file is queued ("uploaded, scanning"). On insert failure, delete the stored file; if enqueue fails, the document stays `Pending` and the timeout sweeper / startup recovery picks it up. Project-member notification (`DocumentAdded`) is sent when the verdict is `Clean`.
2. **Replace**: validate → store the new file under a new key → update record (size, type, original name, updated date), set `Pending` with a new `ScanAttemptId`, enqueue → delete the old file → log `Replace`. The document is unavailable until the new file is clean.
3. **Scan verdict** (`IScanResultHandler.ApplyVerdictAsync`): see [scan-pipeline.md](./scan-pipeline.md). `Clean` → `Available` and notify project members; `Infected` → delete the stored file, `Rejected`, notify the uploader (`DocumentRejected`); `Error` → `ScanFailed`. Each outcome writes a `ScanCompleted` / `ScanRejected` / `ScanFailed` activity row.
4. **Retry scan**: `RetryScanAsync(documentId, user)` (uploader or Administrator, only from `ScanFailed`) → new `ScanAttemptId`, `Pending`, enqueue.
5. **Delete**: authorize → delete record (shares cascade) → delete file → log `Delete` with title snapshot. Callers must obtain user confirmation first. Allowed in any scan state (e.g., to remove `Rejected`/`ScanFailed` records); a later verdict for the deleted document is ignored.
6. **Access**: all read, list, search, and count methods apply the shared access filter described in [data-model.md](../data-model.md) and [research.md](../research.md) (R-6), and only ever return `Available` documents, except that the uploader and Administrators also see their non-available rows (with status) in list methods. Download, preview, share, and attach return `NotAvailable` for anything not `Available`.
7. **Sharing**: owner or Administrator only; target is a user id or department name; duplicates are ignored; sharing notifies each recipient (`DocumentShared`); shared recipients can view/download only.
8. **Attach to task**: requires access to the task and edit rights on the document; sets `TaskId` and `ProjectId` (the task's project).
9. **Activity**: every upload, download, preview, edit, replace, delete, share, and scan outcome creates a `DocumentActivity` row.

## Reporting contract (Administrator only)

```csharp
public interface IDocumentReportService
{
    Task<List<FileTypeCount>> GetUploadsByFileTypeAsync(DateTime? from, DateTime? to, UserContext admin);
    Task<List<UploaderCount>> GetMostActiveUploadersAsync(int top, DateTime? from, DateTime? to, UserContext admin);
    Task<List<AccessPatternPoint>> GetAccessPatternsAsync(DateTime? from, DateTime? to, UserContext admin); // by day and action
}
```

Non-administrators receive an empty result / `Forbidden`.
