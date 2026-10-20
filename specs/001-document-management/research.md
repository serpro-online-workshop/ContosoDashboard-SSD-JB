# Research: Document Upload and Management

All Technical Context items were resolved from the existing code base and the stakeholder document; no `NEEDS CLARIFICATION` items remain.

## R-1: Schema management (EnsureCreated vs. migrations)

- **Decision**: Add EF Core migrations (a baseline `InitialCreate` capturing today's model, then `AddDocuments`) and replace `EnsureCreated()` with `Database.Migrate()` at startup.
- **Rationale**: `Program.cs` uses `EnsureCreated()`, which creates the schema only when the database is empty and never adds new tables to the existing `ContosoDashboard.db`. The constitution requires migrations for schema changes. `Microsoft.EntityFrameworkCore.Tools` is already referenced.
- **Alternatives considered**: Keep `EnsureCreated()` and tell users to delete the database (fragile, repeats for every change); hand-written `CREATE TABLE` at startup (bypasses EF, drifts from the model).
- **Consequence**: The existing local database has no migration history, so it must be deleted once; seeded sample data is recreated by the baseline migration.

## R-2: File storage abstraction and layout

- **Decision**: `IFileStorageService` with `UploadAsync`, `DownloadAsync`, `DeleteAsync`, `GetUrlAsync` (see [contracts/file-storage-service.md](./contracts/file-storage-service.md)). Local implementation writes beneath a configurable root (default `AppData/uploads` relative to the content root). Storage key: `{userId}/{projectId|"personal"}/{guid}{.ext}`.
- **Rationale**: Matches the stakeholder design and constitution III; the same key works as an Azure blob name. Keys are relative and provider-neutral, so the database schema does not change on migration.
- **Path safety**: The local provider resolves the full path and rejects any key that escapes the root; extensions come from a whitelist, never from user input directly; user filenames are never part of the path.
- **`GetUrlAsync`**: The local provider throws `NotSupportedException` because local files are never exposed by URL; the application always streams through authorized endpoints. A future Azure provider may return a short-lived URL only after authorization.
- **Alternatives considered**: Storing files in SQLite as BLOBs (bloats the database, harder to migrate to blob storage); storing under `wwwroot` (violates security requirement).

## R-3: Upload workflow and failure handling

- **Decision**: Validate (size, extension, signature, required metadata) → authorize (project membership) → generate storage key → save file → insert metadata as `Pending` → enqueue a scan request → return "uploaded, scanning". If the metadata insert fails, delete the just-written file. Replacement writes the new file first, updates the record (back to `Pending`), then deletes the old file (a failed old-file delete is logged, not surfaced). Malware scanning is asynchronous (R-5, R-14).
- **Rationale**: Satisfies FR-008 (no orphaned records) and the stakeholder sequence "generate unique path → save file → save metadata". Compensating delete prevents orphaned files.
- **Blazor input**: `InputFile` with `maxAllowedSize` of 25 MB; capture name/size/content type first, copy the stream into a `MemoryStream`, then clear the browser-file reference; files in a multi-file selection are processed sequentially and reported individually (FR-001, US1 scenario 6). `@key` on `InputFile` resets it after success.
- **Alternatives considered**: Streaming directly to disk (cannot sniff the signature first without re-reading); parallel uploads (memory pressure in a server-side circuit).

## R-4: File validation

- **Decision**: Three checks: size (> 0 and ≤ 25 MB), extension whitelist (`.pdf .doc .docx .xls .xlsx .ppt .pptx .txt .jpg .jpeg .png`), and content signature that must agree with the extension (PDF `%PDF`, PNG/JPEG magic bytes, OOXML ZIP header, legacy Office OLE header; `.txt` must contain no NUL bytes). The client-supplied content type is stored as metadata but not trusted. MIME type is derived server-side from the extension.
- **Rationale**: Extension-only checks are trivially bypassed (constitution I). Signature checks need no external dependency.
- **Alternatives considered**: Trusting the browser content type (spoofable); a third-party content-inspection library (new dependency, not needed for this scope).

## R-5: Malware scanning (scanner abstraction)

- **Decision**: `IMalwareScanner.ScanAsync(Stream, fileName)` returns clean/infected with a reason; the training build registers `NoOpMalwareScanner` (always clean). The scanner is invoked by a background worker, not on the request path (see R-14); a flagged file is deleted and its document rejected, with a log entry.
- **Rationale**: Stakeholder-confirmed (spec FR-006). Keeps the offline constraint while leaving a seam for a real engine.
- **Alternatives considered**: Bundling an antivirus engine (not offline-friendly, heavy); skipping scanning (violates the requirement); scanning inline during upload (blocks the Blazor circuit for large files and cannot use cloud scanners).

## R-6: Authorization model

- **Decision**: A single `DocumentAccess` component evaluates a user context (id, role, department, managed/member project ids) and provides (a) `CanView/CanEdit/CanDelete/CanShare(document)` and (b) an `IQueryable<Document>` filter for lists, search, and counts. Rules:
  - **View/download/preview**: Administrator; uploader; the project's manager or members; direct or department share recipient; a `TeamLead` for documents uploaded by users in the same department, except `Personal Files` documents with no project that are not shared with that Team Lead (clarification 2026-10-20).
  - **Edit/replace/delete**: Administrator; uploader; project manager of the document's project; a `TeamLead` for documents they can manage under the rule above. Share recipients get view/download only.
  - **Share**: owner and Administrator.
- **Rationale**: One rule set used for both single-item and list paths prevents the classic bug where search or a count leaks documents a direct request would block (constitution I).
- **Failure behavior**: Unauthorized and nonexistent identifiers are indistinguishable (404 for endpoints; "not found" result in the service).
- **Alternatives considered**: ASP.NET resource-based `IAuthorizationHandler` (more ceremony for the same rules; lists still need a query filter); per-page checks (duplicated, error-prone).

## R-7: Serving, download, and preview

- **Decision**: Minimal-API endpoints `GET /documents/{id}/download` and `GET /documents/{id}/preview`, both `RequireAuthorization()`, streaming from `IFileStorageService` after `DocumentService` authorization and activity logging. Preview is allowed only for PDF/JPEG/PNG and sets `Content-Disposition: inline`, `X-Content-Type-Options: nosniff`; because the global header is `X-Frame-Options: DENY`, the preview route overrides it with `SAMEORIGIN` so an in-page frame can render it (the existing CSP `default-src 'self'` already permits same-origin frames, objects, and images). Download always uses `attachment` with the original filename sanitized.
- **Rationale**: Files are outside `wwwroot`, so static serving is impossible by design; endpoints enable authorization checks and auditing.
- **Alternatives considered**: Blazor-side base64 data URLs (heavy for 25 MB, bypass endpoint auditing); relaxing the global frame policy (weakens every page).

## R-8: Data model choices

- **Decision**: Integer keys (consistent with `User`/`Project`), `Category` stored as text validated against a fixed list, `FileType` max length 255, `FilePath` max length 500, tags in a single delimited text column (normalized lowercase, comma-separated). One `DocumentShare` table with either `SharedWithUserId` or `SharedWithDepartment` set. Task link is a nullable `TaskId` on `Document` (one task per document); attaching to a task sets `ProjectId` to the task's project. See [data-model.md](./data-model.md).
- **Rationale**: Matches stakeholder constraints; simplest structure satisfying the spec (constitution V). SQLite `LIKE` is case-insensitive for ASCII, which satisfies case-insensitive tag search.
- **Alternatives considered**: Tag and DocumentTag tables (extra joins, no requirement for tag management); one share row per department member (requires fan-out and breaks when membership changes); many-to-many Document–Task (spec only requires attaching to a task).

## R-9: Search and list performance

- **Decision**: Database-side filtering and sorting using the access filter plus `LIKE` over title, description, tags, uploader display name, and project name; indexes on `UploaderUserId`, `ProjectId`, `TaskId`, `UploadDate`, `Category`; share indexes on `(SharedWithUserId)` and `(SharedWithDepartment)`; paging at 50 rows. Meets the 2-second target at the 500-document scale.
- **Alternatives considered**: SQLite FTS (unneeded at this scale, complicates migrations); in-memory filtering (does not scale and risks leaking un-filtered data).

## R-10: Notifications

- **Decision**: Append `DocumentShared`, `DocumentAdded`, and `DocumentRejected` to `NotificationType` (stored as integers, so appending is backward compatible) and create notifications through the existing `INotificationService`. Sharing with a department notifies every user in that department; a project document notifies project members and the manager except the uploader once the document becomes `Available`; a rejected scan notifies the uploader. Respect the user's `InAppNotificationsEnabled` setting.
- **Alternatives considered**: A separate document-notification mechanism (duplicates existing behavior).

## R-11: Audit and reporting

- **Decision**: `DocumentActivity` rows for Upload, Download, Preview, Edit, Replace, Delete, Share, and scan outcomes (ScanCompleted, ScanRejected, ScanFailed); the row keeps `DocumentId` as a plain value plus a title snapshot so history survives deletion. `DocumentReportService` exposes three queries (by file type, by uploader, access patterns by day/action) to an Administrator-only page.
- **Alternatives considered**: Logging only to the application log (not queryable for reports); foreign key with cascade delete (would erase audit history).

## R-12: Claims and task integration

- **Decision**: Add the `Department` claim at login (it is missing today) and keep the user id claim as the identity source. Add `TaskDetails.razor` (`/tasks/{taskId:int}`) because the existing Tasks page has no detail view (its handler is a TODO); the page lists attached documents and offers upload/attach, using `ITaskService.GetTaskByIdAsync` for authorization of task access.
- **Alternatives considered**: A modal on the Tasks list (the list page is already constrained and the spec says "task detail page").

## R-13: Testing approach

- **Decision**: xUnit project with SQLite in-memory databases and a temporary-directory storage root. Priorities: access matrix, upload validation and failure cleanup, storage path safety, search/list filtering, share and notification behavior. UI flows are covered by the manual checks in [quickstart.md](./quickstart.md).
- **Alternatives considered**: EF InMemory provider (does not enforce relational behavior such as foreign keys or `LIKE` semantics). Scan-pipeline tests (state machine, idempotency, stale verdicts, timeout, restart recovery) run against the in-process mode with a fake scanner; the Azure mode gets contract tests with Azurite in the cloud phase.

## R-14: Asynchronous scanning with Azure Functions and Queue Storage

- **Decision**: Upload stores the file and a `Pending` document, then enqueues a small JSON `ScanRequest`. In the cloud deployment an Azure Function with a `QueueTrigger` on `document-scan-requests` streams the file from blob storage, runs the scanning engine, and writes a `ScanVerdictMessage` to `document-scan-results` through a queue output binding; a hosted listener in the web app applies verdicts. The document is quarantined (inaccessible) until `Clean`. In the training build the same contract runs in-process (`Channel` + `BackgroundService`) with the no-op scanner, so offline use needs no Azure services (constitution III). Full contract: [contracts/scan-pipeline.md](./contracts/scan-pipeline.md).
- **Rationale**:
  - Scanning a 25 MB file inline blocks the Blazor circuit and cannot use cloud scanners; a queue decouples upload latency from scan time and allows retry.
  - A results queue (instead of a callback HTTP endpoint or direct database access) means the Function needs no database credentials and the web app exposes no new inbound surface; the app validates `documentId` + `scanAttemptId` before applying any verdict.
  - Quarantine-until-clean keeps the security guarantee of "scan before access" while the work is asynchronous.
  - Queue Storage fits the need (simple at-least-once delivery, built-in retries and poison queue, Azurite for local dev); verdict application is idempotent so duplicate delivery is harmless.
- **Failure handling**: the Functions host retries (default 5 dequeues) then poison-queues the request; the web app's timeout sweeper marks documents `Pending` longer than 15 minutes as `ScanFailed` (fail closed) and allows retry with a new attempt id. In-process mode re-enqueues `Pending` documents on startup.
- **Alternatives considered**: Service Bus (richer features, more cost/complexity than needed); Event Grid blob-created trigger (no request/verdict correlation to our document model); Function calling an authenticated web API callback (new inbound endpoint, needs secrets/token handling); inline synchronous scan (see rationale); Defender for Storage alone (can be the scanner engine inside the function, but we still need the verdict flow back to the app).
- **Scope note**: This feature delivers the quarantine workflow, queue contract, and in-process mode. The Azure implementation (`AzureStorageScanQueue`, result listener, `DocumentScanFunction`) and `AzureBlobStorageService` are delivered together in the cloud-migration phase in separate projects, keeping the training build free of Azure SDK dependencies.
