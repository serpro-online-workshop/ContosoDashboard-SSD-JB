# Tasks: Document Upload and Management

**Input**: Design documents from `/specs/001-document-management/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/](./contracts/), [quickstart.md](./quickstart.md)

**Tests**: Included. The plan adds one xUnit project (justified in plan.md → Complexity Tracking) because the access-control matrix and the quarantine state machine are the highest-risk parts of the feature (constitution I and IV). Within each story, write the tests first and confirm they fail before implementing.

**Scope**: Phase A of the plan (training build). The Azure cloud implementation (`cloud/`: `AzureBlobStorageService`, `AzureStorageScanQueue`, `ScanResultListener`, `DocumentScanFunction`) is a separate work item (plan → Delivery Phases, Phase C) and is not listed here; the queue contracts it depends on are built in US1.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: User story the task belongs to (US1–US7); none for Setup, Foundational, and Polish
- Paths are relative to the repository root `ContosoDashboard-SSD-JB/`

## Path Conventions

Single web project `ContosoDashboard/` (existing folders `Models/`, `Data/`, `Services/`, `Pages/`, `Shared/`) plus new `ContosoDashboard/Endpoints/`, `ContosoDashboard/Migrations/`, and test project `tests/ContosoDashboard.Tests/`.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Tooling, test project, and configuration

- [ ] T001 Verify the .NET SDK for `net10.0` is on the PATH (`dotnet --version`) and install the EF CLI if missing (`dotnet tool install --global dotnet-ef`); record the versions used in the feature notes
- [ ] T002 Create xUnit test project `tests/ContosoDashboard.Tests/ContosoDashboard.Tests.csproj` (`net10.0`, `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `Microsoft.EntityFrameworkCore.Sqlite` 10.0.*) with a `ProjectReference` to `ContosoDashboard/ContosoDashboard.csproj`
- [ ] T003 Create solution `ContosoDashboard.sln` at the repository root containing `ContosoDashboard/ContosoDashboard.csproj` and `tests/ContosoDashboard.Tests/ContosoDashboard.Tests.csproj` (`dotnet new sln`, `dotnet sln add`)
- [ ] T004 [P] Add `DocumentStorage` (`Provider: "Local"`, `RootPath: "AppData/uploads"`, `MaxFileSizeBytes: 26214400`) and `MalwareScan` (`Mode: "InProcess"`, `Scanner: "NoOp"`, `SimulatedDelaySeconds: 0`, `PendingTimeoutMinutes: 15`, `RequestQueueName`, `ResultQueueName`, `QueueServiceUri: ""`) sections to `ContosoDashboard/appsettings.json` per [contracts/file-storage-service.md](./contracts/file-storage-service.md) and [contracts/scan-pipeline.md](./contracts/scan-pipeline.md)
- [ ] T005 [P] Add `ContosoDashboard/AppData/` (uploaded files) to the repository ignore file `.gitignore` (create it if absent)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Schema and migrations, storage, access rules, and claims that every user story depends on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Baseline migration (must happen before any model change)

- [ ] T006 Generate the baseline migration for the CURRENT model before editing any entity: `dotnet ef migrations add InitialCreate --project ContosoDashboard` → `ContosoDashboard/Migrations/`
- [ ] T007 Replace `context.Database.EnsureCreated()` with `context.Database.Migrate()` in `ContosoDashboard/Program.cs` (keep the existing try/catch logging)

### Entities and schema

- [ ] T008 [P] Create `ContosoDashboard/Models/DocumentConstants.cs` with the fixed category list (`Project Documents`, `Team Resources`, `Personal Files`, `Reports`, `Presentations`, `Other`), scan status values (`Pending`, `Available`, `Rejected`, `ScanFailed`), and activity action values (`Upload`, `Download`, `Preview`, `Edit`, `Replace`, `Delete`, `Share`, `ScanCompleted`, `ScanRejected`, `ScanFailed`)
- [ ] T009 [P] Create `ContosoDashboard/Models/Document.cs` per [data-model.md](./data-model.md): `DocumentId` int PK identity; `Title` string(255) required; `Description` string(2000)?; `Category` string(50) required (one of the fixed list); `Tags` string(500)? (normalized lowercase, comma-separated, max 10 tags, each ≤ 30 characters); `OriginalFileName` string(255) required; `FileType` string(255) required; `FileSize` long (1 to 26,214,400); `FilePath` string(500) required, unique; `UploaderUserId` int FK → User required; `ProjectId` int? FK → Project; `TaskId` int? FK → TaskItem; `UploadDate`, `UpdatedDate` DateTime; `ScanStatus` string(20) default `Pending`; `ScanAttemptId` Guid; `ScanRequestedDate` DateTime; `ScanCompletedDate` DateTime?; `ScanDetail` string(500)?
- [ ] T010 [P] Create `ContosoDashboard/Models/DocumentShare.cs`: `DocumentShareId` int PK; `DocumentId` int FK (cascade) required; `SharedWithUserId` int? FK → User; `SharedWithDepartment` string(100)?; `SharedByUserId` int FK → User required; `SharedDate` DateTime (exactly one of user/department set)
- [ ] T011 [P] Create `ContosoDashboard/Models/DocumentActivity.cs`: `DocumentActivityId` int PK; `DocumentId` int (plain value, no FK); `DocumentTitle` string(255); `FileType` string(255)?; `UserId` int FK → User required; `Action` string(20); `Timestamp` DateTime; `Details` string(500)?
- [ ] T012 [P] Append `DocumentShared`, `DocumentAdded`, `DocumentRejected` to the end of the `NotificationType` enum in `ContosoDashboard/Models/Notification.cs` (do not reorder existing values)
- [ ] T013 [P] Add `Documents` navigation collections to `ContosoDashboard/Models/User.cs`, `ContosoDashboard/Models/Project.cs`, and `ContosoDashboard/Models/TaskItem.cs`
- [ ] T014 Configure the new entities in `ContosoDashboard/Data/ApplicationDbContext.cs`: `DbSet`s; uploader FK `Restrict`; `ProjectId`/`TaskId` FKs `SetNull`; `DocumentShare` cascade from `Document`, `Restrict` for users, check constraint (exactly one of `SharedWithUserId`/`SharedWithDepartment`), unique `(DocumentId, SharedWithUserId)` and `(DocumentId, SharedWithDepartment)`; indexes `UploaderUserId`, `ProjectId`, `TaskId`, `UploadDate`, `Category`, `(ScanStatus, ScanRequestedDate)`, unique `FilePath`; share indexes on `SharedWithUserId` and `SharedWithDepartment`; activity indexes `(Action, Timestamp)`, `UserId`, `DocumentId`
- [ ] T015 Generate the second migration `dotnet ef migrations add AddDocuments --project ContosoDashboard`, review the generated SQL/schema against data-model.md, and delete the existing local `ContosoDashboard/ContosoDashboard.db` once so migrations create a fresh database

### Identity, storage, and access rules

- [ ] T016 [P] Add the `Department` claim (`new Claim("Department", user.Department ?? "")`) in `ContosoDashboard/Pages/Login.cshtml.cs`
- [ ] T017 [P] Create `ContosoDashboard/Services/UserContext.cs`: `UserContext(int UserId, UserRole Role, string? Department)` with `static FromPrincipal(ClaimsPrincipal)` reading NameIdentifier, Role, and Department claims
- [ ] T018 [P] Create `ContosoDashboard/Services/IFileStorageService.cs` with `UploadAsync`, `DownloadAsync`, `DeleteAsync`, `GetUrlAsync` exactly as in [contracts/file-storage-service.md](./contracts/file-storage-service.md), plus a `DocumentStorageOptions` class bound to the `DocumentStorage` section
- [ ] T019 Create `ContosoDashboard/Services/LocalFileStorageService.cs`: root resolved against the content root and rejected if inside `wwwroot`; key validation (relative, `/` separators, no `..`, no rooted path, no backslash); resolved path must stay under root; never overwrite an existing key; create directories; `DeleteAsync` idempotent; `DownloadAsync` throws `FileNotFoundException`; `GetUrlAsync` throws `NotSupportedException`; no partial file left on write failure
- [ ] T020 Create `ContosoDashboard/Services/DocumentAccess.cs` implementing research R-6: `CanView/CanEdit/CanDelete/CanShare(Document, UserContext, projectMembership data, shares)` and `IQueryable<Document> ApplyViewFilter(IQueryable<Document>, UserContext)` restricted to `ScanStatus == Available` (plus the uploader's and Administrators' non-available rows only when a flag requests list-with-status); Team Lead rule uses same `Department` and excludes `Personal Files` documents with no project and no share to that user; shares grant view/download only
- [ ] T021 [P] Create test helper `tests/ContosoDashboard.Tests/TestDb.cs` (in-memory SQLite `ApplicationDbContext` factory with seeded users in different roles/departments, projects, memberships, and a temp-directory storage root with cleanup)
- [ ] T022 [P] Write `tests/ContosoDashboard.Tests/LocalFileStorageServiceTests.cs`: upload/download/delete round trip; rejects `..`, rooted paths, backslashes, keys that resolve outside root; refuses to overwrite; delete is idempotent; no partial file on failed write; `GetUrlAsync` throws
- [ ] T023 [P] Write `tests/ContosoDashboard.Tests/DocumentAccessTests.cs`: full matrix of Employee/TeamLead/ProjectManager/Administrator × uploader/project member/project manager/share recipient (user and department)/same-department/other; Team Lead cannot see unshared, project-less `Personal Files`; non-`Available` documents invisible except to uploader/Administrator list view; shares never grant edit/delete; query filter and per-document check agree for every case
- [ ] T024 Register in `ContosoDashboard/Program.cs`: bind `DocumentStorageOptions`, `IFileStorageService` → `LocalFileStorageService` (selected by `DocumentStorage:Provider`), and `DocumentAccess` as scoped; run `dotnet test` and confirm T022–T023 pass

**Checkpoint**: Schema migrated, storage and access rules proven by tests — user story phases can begin

---

## Phase 3: User Story 1 - Upload a Document with Metadata (Priority: P1) 🎯 MVP

**Goal**: A user uploads one or more valid files with metadata; files are stored, queued for an asynchronous malware scan, quarantined until clean, and then become available.

**Independent Test**: Log in as an Employee, upload a valid PDF with title and category: the row shows *Scanning* then *Available* (quickstart #1–#4, #17–#20). Oversized, unsupported, mismatched, and missing-metadata uploads are rejected; a flagged file ends as *Rejected*.

### Tests for User Story 1 ⚠️ (write first, expect failures)

- [ ] T025 [P] [US1] Write `tests/ContosoDashboard.Tests/DocumentUploadTests.cs`: size > 25 MB and 0 bytes rejected; extension whitelist; content-signature mismatch (e.g., text renamed `.pdf`); missing title/category; non-member project rejected; success yields `Pending`, a GUID-based `FilePath` of the form `{userId}/{projectId|personal}/{guid}.{ext}`, a `ScanRequest` enqueued, an `Upload` activity row, and no project notification yet; DB failure after file write deletes the file; tags normalized (lowercase, max 10, ≤ 30 chars); multi-file batch reports per-file results
- [ ] T026 [P] [US1] Write `tests/ContosoDashboard.Tests/ScanPipelineTests.cs`: `Clean` → `Available` + `DocumentAdded` notifications to project members (not the uploader); `Infected` → file deleted, `Rejected`, `DocumentRejected` to uploader; `Error` → `ScanFailed`; duplicate verdict ignored; verdict with stale `ScanAttemptId` ignored; verdict for a deleted document ignored; `ScanTimeoutSweeper` marks long-`Pending` documents `ScanFailed` and never `Available`; `RetryScanAsync` only from `ScanFailed` (uploader/Administrator) with a new attempt id; startup recovery re-enqueues `Pending`; non-`Available` documents excluded from list/count for others

### Implementation for User Story 1

- [ ] T027 [P] [US1] Create `ContosoDashboard/Services/IMalwareScanner.cs`: `IMalwareScanner`, `ScanResult`, `NoOpMalwareScanner` (always clean), and development-only `EicarScanner` (flags content containing the EICAR test string)
- [ ] T028 [P] [US1] Create `ContosoDashboard/Services/Scanning/IScanQueue.cs`: `ScanRequest`, `ScanVerdictMessage`, `ScanVerdict`, `IScanQueue`, `IScanResultHandler` per [contracts/scan-pipeline.md](./contracts/scan-pipeline.md), plus `MalwareScanOptions` bound to the `MalwareScan` section
- [ ] T029 [P] [US1] Create `ContosoDashboard/Services/DocumentFileValidator.cs`: 25 MB limit, empty file rejection, extension whitelist (`.pdf .doc .docx .xls .xlsx .ppt .pptx .txt .jpg .jpeg .png`), signature checks (PDF `%PDF`, PNG, JPEG, OOXML ZIP header, legacy OLE header, `.txt` no NUL bytes), server-side MIME mapping from extension, filename sanitization
- [ ] T030 [US1] Create `ContosoDashboard/Services/DocumentService.cs` with `IDocumentService` (all signatures from [contracts/document-service.md](./contracts/document-service.md); unimplemented methods throw `NotImplementedException` until their story) and `UploadAsync`: validate → authorize project membership (member, manager, or Administrator) → generate key `{userId}/{projectId|personal}/{guid}.{ext}` → store file → insert `Pending` document with new `ScanAttemptId`/`ScanRequestedDate` → compensate by deleting the file if the insert fails → enqueue `ScanRequest` → write `Upload` activity; categories validated against the fixed list
- [ ] T031 [US1] In `ContosoDashboard/Services/DocumentService.cs` implement `IScanResultHandler.ApplyVerdictAsync` (apply only to `Pending` with matching `ScanAttemptId`; `Clean` → `Available` + `DocumentAdded` notifications via `INotificationService` honoring `InAppNotificationsEnabled`; `Infected` → delete stored file, `Rejected`, `DocumentRejected` to uploader; `Error` → `ScanFailed`; write `ScanCompleted`/`ScanRejected`/`ScanFailed` activity) and `RetryScanAsync`
- [ ] T032 [P] [US1] Create `ContosoDashboard/Services/Scanning/InProcessScanQueue.cs` (bounded `Channel<ScanRequest>` implementing `IScanQueue`)
- [ ] T033 [P] [US1] Create `ContosoDashboard/Services/Scanning/ScanWorker.cs` (`BackgroundService`: read channel, honor `SimulatedDelaySeconds`, download via `IFileStorageService`, scan via `IMalwareScanner`, call `ApplyVerdictAsync` in a fresh scope, scanner exceptions → `Error`; on startup re-enqueue all `Pending` documents)
- [ ] T034 [P] [US1] Create `ContosoDashboard/Services/Scanning/ScanTimeoutSweeper.cs` (`BackgroundService`, runs every minute, marks `Pending` documents older than `PendingTimeoutMinutes` as `ScanFailed` with activity row; never sets `Available`)
- [ ] T035 [US1] Register in `ContosoDashboard/Program.cs`: `IDocumentService`, `IScanResultHandler` (same instance as the service), `IMalwareScanner` by `MalwareScan:Scanner`, `InProcessScanQueue` as `IScanQueue`, hosted `ScanWorker` and `ScanTimeoutSweeper`; fail startup if `Scanner: Eicar` or `SimulatedDelaySeconds > 0` is configured outside the Development environment
- [ ] T036 [US1] Create `ContosoDashboard/Shared/DocumentUploadDialog.razor`: `InputFile` with `@key` reset after success and `maxAllowedSize` 25 MB; capture name/size/content type before opening the stream, copy to `MemoryStream`, clear the file reference; multiple files processed sequentially with per-file progress and success/error messages ("Uploaded — scanning for malware"); fields Title (required, 1–255), Category (required, fixed list), Description (≤ 2000), Project (only projects the user belongs to or manages via `IProjectService.GetUserProjectsAsync`), Tags (comma-separated); builds `UserContext` from the auth state
- [ ] T037 [US1] Create `ContosoDashboard/Pages/Documents.razor` (`/documents`, `@attribute [Authorize]`): "My Documents" list with title, category, upload date, file size, project, and scan-status badge (*Scanning*, *Available*, *Rejected* with reason, *Scan failed* with Retry); auto-refresh while any document is `Pending`; Upload button opening the dialog; empty state; implement `GetMyDocumentsAsync` basic version (own uploads, newest first) in `ContosoDashboard/Services/DocumentService.cs`
- [ ] T038 [P] [US1] Add a "Documents" nav item (`/documents`) to `ContosoDashboard/Shared/NavMenu.razor`
- [ ] T039 [P] [US1] Verify `ContosoDashboard/Pages/Notifications.razor` renders the three new notification types (title/message and icon/badge fallback) and adjust if it switches on type

**Checkpoint**: User Story 1 is fully functional and independently testable (run T025–T026; quickstart #1–#4 and #17–#20)

---

## Phase 4: User Story 2 - Browse, Sort, Filter, and Search Documents (Priority: P1)

**Goal**: Users can sort, filter, and search the documents they are allowed to see, quickly and without leaking others' documents.

**Independent Test**: With several uploaded documents, sort by each column, apply each filter, and search by title/description/tag/uploader/project; results are correct and permission-limited (quickstart #5).

### Tests for User Story 2 ⚠️

- [ ] T040 [P] [US2] Write `tests/ContosoDashboard.Tests/DocumentSearchTests.cs`: sort by title/upload date/category/file size (both directions); filters by category, project, date range (inclusive bounds); search over title, description, tags (case-insensitive), uploader name, project name; results exclude documents the caller cannot access and non-`Available` documents of others; paging (default 50); empty term returns all accessible

### Implementation for User Story 2

- [ ] T041 [P] [US2] Create `ContosoDashboard/Services/DocumentModels.cs`: `DocumentQuery`, `PagedResult<T>`, `DocumentSummary`, `DocumentDetails`, `OperationResult`, `UploadResult`, `ShareTarget`, `DocumentMetadata`, `FileContent` shapes per [contracts/document-service.md](./contracts/document-service.md) (move any placeholder types from T030 here)
- [ ] T042 [US2] Implement `GetMyDocumentsAsync` (full query: filter, sort, paging) and `SearchAsync` in `ContosoDashboard/Services/DocumentService.cs` using `DocumentAccess.ApplyViewFilter` and database-side `LIKE` over title, description, tags, uploader display name, and project name
- [ ] T043 [US2] Extend `ContosoDashboard/Pages/Documents.razor` with sortable column headers, category/project/date-range filters, a search box, paging, and a "no results" state; reuse a shared table by creating `ContosoDashboard/Shared/DocumentList.razor` (parameters: items, show-actions callbacks) and use it from the page

**Checkpoint**: User Stories 1 and 2 both work independently

---

## Phase 5: User Story 3 - Download, Preview, Edit, Replace, and Delete (Priority: P2)

**Goal**: Users retrieve and maintain documents: authorized download, in-browser preview of PDF/JPEG/PNG, metadata edit, file replacement, and confirmed permanent deletion.

**Independent Test**: Upload a PDF and a PNG; preview both, download one, edit title/tags, replace the file, delete after confirming (quickstart #6–#8).

### Tests for User Story 3 ⚠️

- [ ] T044 [P] [US3] Write `tests/ContosoDashboard.Tests/DocumentManagementTests.cs`: download/preview only for `Available` and permitted users (otherwise `NotAvailable`/not found, indistinguishable from missing); preview refused for non-PDF/JPEG/PNG; edit/replace/delete allowed only for uploader, project manager, managing Team Lead, Administrator and denied for share recipients; replace sets `Pending` with a new attempt id, enqueues a scan, and deletes the old file; delete removes record, file, and shares and keeps activity history; delete allowed for `Rejected`/`ScanFailed` documents; `Download`/`Preview`/`Edit`/`Replace`/`Delete` activity rows written

### Implementation for User Story 3

- [ ] T045 [US3] Implement `GetAsync`, `OpenForDownloadAsync`, `OpenForPreviewAsync`, `UpdateMetadataAsync`, `ReplaceFileAsync`, `DeleteAsync` in `ContosoDashboard/Services/DocumentService.cs` per the behavior rules in [contracts/document-service.md](./contracts/document-service.md) (replace: validate → store new file under a new key → update record → `Pending` + new `ScanAttemptId` + enqueue → delete old file; delete: authorize → remove record → remove file → log `Delete` with title snapshot)
- [ ] T046 [P] [US3] Create `ContosoDashboard/Endpoints/DocumentEndpoints.cs`: `GET /documents/{id:int}/download` and `GET /documents/{id:int}/preview` with `RequireAuthorization()`, 404 for missing/unauthorized/non-available, 415 for non-previewable types, `Content-Disposition` attachment/inline with sanitized original filename, `X-Content-Type-Options: nosniff`, `Cache-Control: private, no-store`, and `X-Frame-Options: SAMEORIGIN` on the preview route only, per [contracts/http-endpoints.md](./contracts/http-endpoints.md)
- [ ] T047 [US3] Map the endpoints in `ContosoDashboard/Program.cs` (`app.MapDocumentEndpoints()` after `UseAuthorization()`)
- [ ] T048 [P] [US3] Create `ContosoDashboard/Shared/DocumentEditDialog.razor` (edit title, description, category, tags; replace file with the same validation and `InputFile`/`MemoryStream` pattern as the upload dialog)
- [ ] T049 [US3] Extend `ContosoDashboard/Shared/DocumentList.razor` and `ContosoDashboard/Pages/Documents.razor` with Download link, Preview (modal with `<iframe>` for PDF and `<img>` for images pointing at the preview route), Edit, Replace, and Delete with a confirmation dialog; disable actions until the document is *Available*; delete also offered for *Rejected*/*Scan failed* rows

**Checkpoint**: User Stories 1–3 work independently

---

## Phase 6: User Story 4 - Project Documents and Role-Based Management (Priority: P2)

**Goal**: Project members see and download project documents; Project Managers manage them; Team Leads manage their team's documents (except private personal files); Administrators have full access.

**Independent Test**: As a project member view/download project documents; as the Project Manager upload and delete any; as a non-member get nothing; as a Team Lead confirm personal-file privacy (quickstart #9–#10).

### Tests for User Story 4 ⚠️

- [ ] T050 [P] [US4] Write `tests/ContosoDashboard.Tests/ProjectDocumentsTests.cs`: members and manager list/download project documents; non-members get empty results; Project Manager can edit/delete any project document and upload to the project; Team Lead sees same-department documents but not unshared project-less `Personal Files`; Team Lead from another department sees none; Administrator sees all; uploading to a project requires membership

### Implementation for User Story 4

- [ ] T051 [US4] Implement `GetProjectDocumentsAsync` in `ContosoDashboard/Services/DocumentService.cs` (membership/manager/Administrator check, `Available` only, query/sort/paging)
- [ ] T052 [US4] Add a Documents section to `ContosoDashboard/Pages/ProjectDetails.razor` using `DocumentList` (download/preview for members; upload, edit, delete actions only where `DocumentAccess` allows) and the upload dialog preselected to the project
- [ ] T053 [US4] Add a "Team" scope tab on `ContosoDashboard/Pages/Documents.razor` visible to `TeamLead` and `Administrator` that lists all accessible documents via `SearchAsync` with an empty term, using the same sorting/filtering and management actions

**Checkpoint**: User Stories 1–4 work independently

---

## Phase 7: User Story 5 - Share Documents and "Shared with Me" (Priority: P3)

**Goal**: Owners share documents with users or departments; recipients are notified and find them under "Shared with Me" (view/download only).

**Independent Test**: Share with a user and with a department; recipients get notifications and see the document; others cannot open it (quickstart #11).

### Tests for User Story 5 ⚠️

- [ ] T054 [P] [US5] Write `tests/ContosoDashboard.Tests/DocumentSharingTests.cs`: owner/Administrator can share, others cannot; user share and department share both grant view/download only; duplicate shares ignored; notifications sent to each recipient (department share notifies every user in the department, excludes the sharer, honors `InAppNotificationsEnabled`); `GetSharedWithMeAsync` lists only `Available` shared documents; `RemoveShareAsync` revokes access; deleting the document removes it from recipients; sharing a non-`Available` document refused; `Share` activity written

### Implementation for User Story 5

- [ ] T055 [US5] Implement `ShareAsync`, `RemoveShareAsync`, and `GetSharedWithMeAsync` in `ContosoDashboard/Services/DocumentService.cs` (uses `IUserService` to resolve department members and `INotificationService` for `DocumentShared`)
- [ ] T056 [P] [US5] Create `ContosoDashboard/Shared/DocumentShareDialog.razor` (pick a user via `IUserService.GetAllUsersAsync` or a department; list and remove existing shares)
- [ ] T057 [US5] Add a "Shared with Me" tab and a Share action to `ContosoDashboard/Pages/Documents.razor` and `ContosoDashboard/Shared/DocumentList.razor` (Share visible to owner/Administrator only; recipients see view/download only)

**Checkpoint**: User Stories 1–5 work independently

---

## Phase 8: User Story 6 - Task and Dashboard Integration (Priority: P3)

**Goal**: Documents can be attached to and uploaded from a task; the dashboard shows Recent Documents and a document count; project members are notified when a document becomes available.

**Independent Test**: Open a task, upload and attach a document, see it on the task and in its project; open the dashboard and see it in Recent Documents and the count (quickstart #12–#13).

### Tests for User Story 6 ⚠️

- [ ] T058 [P] [US6] Write `tests/ContosoDashboard.Tests/TaskDocumentTests.cs`: upload with `TaskId` sets the document's `ProjectId` to the task's project; attach/detach requires access to the task and edit rights on the document; `GetTaskDocumentsAsync` returns only accessible `Available` documents; `GetRecentDocumentsAsync(5)` returns the caller's last 5 own uploads (excluding non-`Available`); `GetDocumentCountAsync` matches the access filter

### Implementation for User Story 6

- [ ] T059 [US6] Implement `AttachToTaskAsync`, `DetachFromTaskAsync`, `GetTaskDocumentsAsync`, `GetDocumentCountAsync`, `GetRecentDocumentsAsync` and honor `TaskId` in `UploadAsync` (derive `ProjectId` from the task, authorize task access via `ITaskService.GetTaskByIdAsync`) in `ContosoDashboard/Services/DocumentService.cs`
- [ ] T060 [US6] Create `ContosoDashboard/Pages/TaskDetails.razor` (`/tasks/{taskId:int}`, `@attribute [Authorize]`): task info via `ITaskService.GetTaskByIdAsync`, attached documents list, upload-from-task (dialog preselected to the task), attach an existing own document, detach
- [ ] T061 [US6] Make task rows in `ContosoDashboard/Pages/Tasks.razor` navigate to `/tasks/{taskId}` (replace the TODO in the row click handler)
- [ ] T062 [US6] Add a "Documents" count summary card and a "Recent Documents" widget (last 5 own uploads with title, category, date, link to `/documents`) to `ContosoDashboard/Pages/Index.razor` using `IDocumentService`
- [ ] T063 [US6] Verify in `ContosoDashboard/Pages/ProjectDetails.razor` that a document attached to a task appears in the project's Documents section, adjusting the query or display if needed

**Checkpoint**: User Stories 1–6 work independently

---

## Phase 9: User Story 7 - Activity Audit and Reporting (Priority: P3)

**Goal**: All document activity is logged and Administrators can generate usage reports.

**Independent Test**: Perform upload, download, share, and delete, then as an Administrator open the reports; as an Employee confirm access is denied (quickstart #14).

### Tests for User Story 7 ⚠️

- [ ] T064 [P] [US7] Write `tests/ContosoDashboard.Tests/DocumentActivityTests.cs`: every `DocumentService` operation (upload, download, preview, edit, replace, delete, share, scan outcomes) writes exactly one activity row with user, action, document id, title snapshot, and timestamp; rows survive document deletion; download/preview of an inaccessible document writes no row
- [ ] T065 [P] [US7] Write `tests/ContosoDashboard.Tests/DocumentReportTests.cs`: uploads by file type, most active uploaders (top N), access patterns by day and action, date-range filtering; non-Administrator callers get no data

### Implementation for User Story 7

- [ ] T066 [US7] Create `ContosoDashboard/Services/DocumentReportService.cs` with `IDocumentReportService` per [contracts/document-service.md](./contracts/document-service.md) (`GetUploadsByFileTypeAsync`, `GetMostActiveUploadersAsync`, `GetAccessPatternsAsync`; Administrator only) and register it in `ContosoDashboard/Program.cs`
- [ ] T067 [US7] Create `ContosoDashboard/Pages/DocumentReports.razor` (`/documents/reports`, `@attribute [Authorize(Policy = "Administrator")]`) with date-range filter and three report tables
- [ ] T068 [P] [US7] Add a "Document Reports" nav item visible only to Administrators in `ContosoDashboard/Shared/NavMenu.razor` (wrap in `<AuthorizeView Roles="Administrator">`)
- [ ] T069 [US7] Audit pass over `ContosoDashboard/Services/DocumentService.cs`: confirm every operation logs its activity (fix gaps found by T064)

**Checkpoint**: All user stories are independently functional

---

## Phase 10: Polish & Cross-Cutting Concerns

- [ ] T070 [P] Add a performance test in `tests/ContosoDashboard.Tests/DocumentSearchTests.cs` seeding ~500 documents and asserting list and search queries complete well under 2 seconds (SC-003)
- [ ] T071 [P] Security review pass: confirm no Razor page or component injects `ApplicationDbContext` for documents, all new pages carry `[Authorize]`, no user-supplied filename reaches a storage path, security headers still present on all routes, and CSP/`X-Frame-Options` behavior for preview works in a browser
- [ ] T072 [P] Update `README.md` with the document management feature: configuration (`DocumentStorage`, `MalwareScan`), upload limits and allowed types, quarantine/scan behavior, the one-time local database reset, and that users must sign in again to receive the `Department` claim
- [ ] T073 Run the full automated suite (`dotnet test`) and all manual scenarios in [quickstart.md](./quickstart.md) (#1–#20), recording results; fix any failures
- [ ] T074 Final cleanup: remove temporary upload folders and test databases created during validation, and confirm `ContosoDashboard/AppData/` is ignored

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies
- **Foundational (Phase 2)**: depends on Setup; **blocks all user stories**. Inside it: T006 → T007 → entities (T008–T013, parallel) → T014 → T015; T016–T019 can run alongside entities; T020 needs T008/T009/T010; T022–T023 need T021; T024 last.
- **User Stories (Phase 3–9)**: all depend on Foundational completion
- **Polish (Phase 10)**: after the stories you intend to ship

### User Story Dependencies

- **US1 (P1)**: starts after Foundational; no dependency on other stories (MVP)
- **US2 (P1)**: needs US1's `Documents.razor` and `DocumentService`; otherwise independent
- **US3 (P2)**: needs US1 (documents must exist); independent of US2 except sharing `DocumentList.razor`
- **US4 (P2)**: needs US1; the Team tab (T053) needs US2's `SearchAsync`
- **US5 (P3)**: needs US1; shares UI files with US3/US2 (`DocumentList.razor`, `Documents.razor`)
- **US6 (P3)**: needs US1 (upload) and uses `ProjectDetails.razor` edits from US4 for T063
- **US7 (P3)**: needs US1; most complete after US3 and US5 log their activities

### Within Each User Story

- Tests first (must fail), then models/helpers, then services, then endpoints/UI
- `DocumentService.cs`, `Documents.razor`, `DocumentList.razor`, and `Program.cs` are shared files: tasks touching them are sequential, never `[P]`

### Parallel Opportunities

- Setup T004, T005; Foundational T008–T013 (distinct model files), T016–T018, T021–T023
- US1: T025/T026 (tests), T027/T028/T029, then T032/T033/T034
- Different stories can be staffed in parallel after US1 if shared-file edits are coordinated

## Parallel Example: User Story 1

```text
# Tests together:
T025 DocumentUploadTests.cs   |   T026 ScanPipelineTests.cs

# Independent helpers together:
T027 IMalwareScanner.cs   |   T028 Scanning/IScanQueue.cs   |   T029 DocumentFileValidator.cs

# Queue and workers together (after T028):
T032 InProcessScanQueue.cs   |   T033 ScanWorker.cs   |   T034 ScanTimeoutSweeper.cs
```

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Phase 1 → Phase 2 (including baseline migration and the database reset)
2. Phase 3 (US1): upload with quarantine and in-process scan
3. **Stop and validate** with T025–T026 and quickstart #1–#4, #17–#20
4. Demo/deploy to the training environment

### Incremental Delivery

1. Foundation → US1 (MVP: upload) → US2 (find documents; completes the P1 value) → US3 and US4 (P2: lifecycle and project/role management) → US5, US6, US7 (P3)
2. Each phase ends with its checkpoint; do not start the next story with failing tests

### Parallel Team Strategy

After Phase 2, one developer finishes US1 (shared service and page), then others take US3/US4, US5, and US6/US7 in parallel, agreeing on an order for edits to `DocumentService.cs`, `Documents.razor`, and `Program.cs`.

## Notes

- Requirement coverage: FR-001–FR-009, FR-028 → US1/Foundation; FR-010–FR-013 → US2; FR-014–FR-018 → US3/US4; FR-019–FR-020 → US5; FR-021–FR-023 → US6 (FR-023 delivered in US1's verdict handling); FR-024–FR-025 → US7; FR-026–FR-027 → Foundation and US1.
- The unresolved clarification (project deletion behavior) needs no task: the app has no project-delete feature; the schema uses `SetNull` on `ProjectId`.
- Cloud-phase work (Azure Functions + Queue Storage, Azure Blob storage) is intentionally excluded; add it as a follow-up feature using [contracts/scan-pipeline.md](./contracts/scan-pipeline.md).
- Commit after each task or logical group; stop at any checkpoint to validate a story on its own.
