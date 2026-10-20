# Implementation Plan: Document Upload and Management

**Branch**: `001-document-management` | **Date**: 2026-10-20 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-document-management/spec.md`

## Summary

Add centralized document upload, organization, sharing, and audit to the existing ContosoDashboard Blazor Server app. Files are stored on the local filesystem outside `wwwroot` behind an `IFileStorageService` abstraction; metadata, shares, and an activity log live in the existing SQLite database via EF Core. All reads and writes go through a `DocumentService` that centralizes the role/project/share access rules, and files are served only through authorized endpoints. Malware scanning is an asynchronous background job: uploads are quarantined (`Pending`) until a queue-driven scan reports clean. In the training build the scan runs in-process; in the cloud deployment it runs as an Azure Function triggered by Azure Queue Storage, behind the same queue contract. UI additions: a Documents page (My Documents / Shared with Me tabs), a project Documents section, a task detail page with attachments, dashboard widgets, and an Administrator reports page.

## Technical Context

**Language/Version**: C# on .NET 10 (`net10.0`), nullable reference types and implicit usings enabled

**Primary Dependencies**: ASP.NET Core Blazor Server (`InputFile`), Entity Framework Core 10 (SQLite provider), cookie authentication with claims; no new runtime packages in the training build. Cloud phase only (separate projects): `Azure.Storage.Queues`, `Azure.Storage.Blobs`, `Azure.Identity`, Azure Functions isolated worker with the Storage Queues extension

**Storage**: SQLite (`ContosoDashboard.db`) for metadata; local filesystem under `AppData/uploads` (content root, outside `wwwroot`) for file bytes

**Testing**: xUnit test project `tests/ContosoDashboard.Tests` using in-memory SQLite for service and authorization tests; manual end-to-end checks per [quickstart.md](./quickstart.md)

**Target Platform**: Web (Blazor Server) on a developer machine/training environment; Windows, Linux, macOS

**Project Type**: Web application (single project, server-rendered Blazor)

**Performance Goals**: Upload ≤ 30 s for 25 MB; document list ≤ 2 s at 500 documents; search ≤ 2 s; preview ≤ 3 s; 95% of files available within 2 minutes of upload (spec SC-002, SC-003, SC-009)

**Constraints**: Offline (no cloud services) in the training build; local file storage; storage behind `IFileStorageService`; scanning behind `IScanQueue`/`IMalwareScanner`; existing mock cookie auth and role policies; 25 MB per-file limit; no major rewrites

**Scale/Scope**: Training scale (hundreds of users, thousands of documents); about 6 new/changed pages or components, 3 new tables

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | How the plan satisfies it |
|-----------|--------|---------------------------|
| I. Security by Default | PASS | All new pages use `[Authorize]`; endpoints use `RequireAuthorization()`; one `DocumentAccess` rule set enforced in `DocumentService` for every read/write/list/search; unauthorized and missing documents both return 404 (no existence leak); extension whitelist + content-signature check, system-generated storage names, path-traversal guard; files outside `wwwroot`. |
| II. Service-Layer Architecture | PASS | Logic in `DocumentService`, `IFileStorageService`, `IScanQueue`, `IMalwareScanner`; Razor components call services only; entities in `Models/`; EF mappings in `ApplicationDbContext`; background workers call the same service (`IScanResultHandler`). |
| III. Offline-First with Cloud Path | PASS | `LocalFileStorageService` and the in-process scan worker are the defaults; Azure Blob storage and the Azure Functions + Queue Storage scan pipeline swap in via DI/config with identical queue contracts; relative storage keys are provider-neutral; no Azure SDK in the training build. |
| IV. Test-Verified Changes | PASS (with justification) | No test project exists today; the access-control matrix is high-risk, so one xUnit project is added (see Complexity Tracking). Quickstart provides manual checks for UI flows. |
| V. Spec-Driven Simplicity | PASS | No new runtime dependencies; tags stored as a delimited text column rather than a tag table; single table for user/team shares. |

**Post-design re-check (after Phase 1)**: PASS. One deviation, adopting EF Core migrations in place of `EnsureCreated`, follows the constitution (schema changes via migrations) and is documented in [research.md](./research.md) (R-1).

## Project Structure

### Documentation (this feature)

```text
specs/001-document-management/
├── plan.md              # This file
├── research.md          # Phase 0 decisions
├── data-model.md        # Phase 1 entities and rules
├── quickstart.md        # Phase 1 validation guide
├── contracts/
│   ├── file-storage-service.md   # IFileStorageService / IMalwareScanner contracts
│   ├── scan-pipeline.md          # Async scan: quarantine states, queue messages, Azure Functions design
│   ├── document-service.md       # DocumentService operations and access rules
│   └── http-endpoints.md         # Download/preview endpoints and UI routes
├── checklists/requirements.md
└── tasks.md             # Created by /speckit.tasks
```

### Source Code (repository root)

```text
ContosoDashboard/
├── Models/
│   ├── Document.cs                    # new
│   ├── DocumentShare.cs               # new
│   ├── DocumentActivity.cs            # new
│   └── Notification.cs                # changed: add DocumentShared, DocumentAdded types
├── Data/
│   └── ApplicationDbContext.cs        # changed: DbSets, relationships, indexes
├── Migrations/                        # new: baseline + AddDocuments
├── Services/
│   ├── IFileStorageService.cs         # new (interface)
│   ├── LocalFileStorageService.cs     # new
│   ├── IMalwareScanner.cs             # new (interface + NoOpMalwareScanner + dev-only EicarScanner)
│   ├── Scanning/
│   │   ├── IScanQueue.cs              # new (queue + message records, IScanResultHandler)
│   │   ├── InProcessScanQueue.cs      # new (Channel-based)
│   │   ├── ScanWorker.cs              # new (BackgroundService: dequeue, scan, apply verdict, startup recovery)
│   │   └── ScanTimeoutSweeper.cs      # new (BackgroundService: Pending too long → ScanFailed)
│   ├── DocumentService.cs             # new (interface + implementation + access rules)
│   ├── DocumentReportService.cs       # new (admin reports)
│   ├── DashboardService.cs            # changed: document count, recent documents
│   └── TaskService.cs                 # unchanged API; used for task lookups
├── Endpoints/
│   └── DocumentEndpoints.cs           # new: download + preview
├── Pages/
│   ├── Documents.razor                # new: My Documents / Shared with Me, search
│   ├── DocumentReports.razor          # new: Administrator only
│   ├── TaskDetails.razor              # new: task detail with attachments
│   ├── ProjectDetails.razor           # changed: Documents section
│   ├── Tasks.razor                    # changed: link rows to task details
│   ├── Index.razor                    # changed: Recent Documents + count card
│   └── Login.cshtml.cs                # changed: add Department claim
├── Shared/
│   ├── DocumentUploadDialog.razor     # new
│   ├── DocumentList.razor             # new (reusable table)
│   ├── DocumentEditDialog.razor       # new
│   ├── DocumentShareDialog.razor      # new
│   └── NavMenu.razor                  # changed: Documents (and Reports) links
├── appsettings.json                   # changed: DocumentStorage section
└── Program.cs                         # changed: DI, migrations, endpoints, preview framing

tests/
└── ContosoDashboard.Tests/            # new xUnit project
    ├── DocumentAccessTests.cs
    ├── DocumentUploadTests.cs
    ├── LocalFileStorageServiceTests.cs
    ├── DocumentSearchTests.cs
    └── ScanPipelineTests.cs

cloud/                                 # Phase C only; NOT part of the training build or solution
├── ContosoDashboard.Cloud/            # AzureBlobStorageService, AzureStorageScanQueue, ScanResultListener
└── DocumentScanFunction/              # Azure Functions (isolated worker): QueueTrigger → scan → results queue
```

**Structure Decision**: Keep the single `ContosoDashboard` web project and its existing folder conventions; add an `Endpoints/` folder for file-serving routes, one sibling test project, and a `cloud/` area reserved for the Azure implementations so the training build carries no Azure dependencies. No separate backend/frontend split.

## Key Design Decisions (summary; details in research.md)

1. **Upload sequence**: validate → authorize → generate GUID storage key → write file → insert metadata as `Pending` → enqueue scan request; on a database failure, delete the written file; never leave a record without a file. The document is unavailable until the scan verdict is `Clean`.
2. **Access rules in one place**: a `DocumentAccess` helper yields both a per-document check and an `IQueryable` filter (both restricted to `Available` documents), so lists, search, and the dashboard cannot drift from the single-document rules.
3. **Serving files**: minimal-API endpoints stream from `IFileStorageService`; preview is `Content-Disposition: inline` for PDF/JPEG/PNG only, with `X-Frame-Options: SAMEORIGIN` on that route only (global header stays `DENY`).
4. **Schema management**: switch from `EnsureCreated()` to EF Core migrations with a baseline; a one-time reset of the local dev database is required.
5. **Task integration**: the app has no task detail page today, so one is added to host attachments.
6. **Claims**: add the `Department` claim at login so team-based rules work without extra lookups.
7. **Async scanning**: one queue contract, two implementations (see the next section and [contracts/scan-pipeline.md](./contracts/scan-pipeline.md)).

## Background Scan Job (Azure Functions + Queue Storage)

Virus scanning is a background job that runs after upload, so a 25 MB scan never blocks the Blazor circuit and a cloud scanner can be used. The same queue contract is implemented twice:

| | Training build (Phase A) | Cloud deployment (Phase C) |
|---|---|---|
| Queue | In-process `Channel<ScanRequest>` | Azure Queue Storage: `document-scan-requests` (in), `document-scan-results` (out) |
| Worker | `ScanWorker` (`BackgroundService`) calling `IMalwareScanner` (no-op) | Azure Function `DocumentScanFunction` with a `QueueTrigger` on `document-scan-requests` and a queue output binding to `document-scan-results` |
| Verdict return | Direct call to `IScanResultHandler.ApplyVerdictAsync` | Web-app `ScanResultListener` reads the results queue and calls `ApplyVerdictAsync` |
| Identity/secrets | None | Managed identity + RBAC (queue sender/processor, blob reader), no keys; Azurite for local dev |

**End-to-end flow**

1. `DocumentService.UploadAsync` stores the file and inserts the document as `Pending` with a new `ScanAttemptId`, then enqueues `ScanRequest { documentId, scanAttemptId, storageKey, sizeBytes }` (metadata only, no user content).
2. The worker (in-process) or Azure Function (cloud) streams the file from storage, scans it, and produces `ScanVerdictMessage { documentId, scanAttemptId, verdict: Clean|Infected|Error, reason, scanner }`.
3. `ApplyVerdictAsync` applies it only if the document is still `Pending` and the `scanAttemptId` matches (duplicates, stale results after a replace, and deleted documents are ignored):
   - `Clean` → `Available`; project members get a `DocumentAdded` notification.
   - `Infected` → stored file deleted, `Rejected`, uploader gets `DocumentRejected`.
   - `Error` → `ScanFailed` (fail closed), retryable by the uploader or an Administrator.
4. Each outcome writes an activity row (`ScanCompleted`, `ScanRejected`, `ScanFailed`).

**Resilience**: the Functions host retries a failing message up to 5 times, then moves it to `document-scan-requests-poison` (ops alert on its depth). Independently, `ScanTimeoutSweeper` marks documents `Pending` longer than `MalwareScan:PendingTimeoutMinutes` (default 15) as `ScanFailed`. The in-process worker re-enqueues `Pending` documents on startup. A document is never made `Available` except by a matching `Clean` verdict.

**Why a results queue**: the Function needs no database credentials and the web app exposes no new inbound endpoint; the app remains the only writer of document state.

**Configuration**: `MalwareScan` section in `appsettings.json` (`Mode: InProcess | AzureQueue`, `Scanner: NoOp | Eicar` and `SimulatedDelaySeconds` for Development only, `PendingTimeoutMinutes`, queue names, `QueueServiceUri` for the cloud). Full contract, message schemas, RBAC roles, and Function sketch: [contracts/scan-pipeline.md](./contracts/scan-pipeline.md); rationale and alternatives: [research.md](./research.md) (R-14).

## Delivery Phases

- **Phase A (training build, this feature)**: everything above except `cloud/`. Includes the quarantine state machine, `IScanQueue`, in-process worker, timeout sweeper, startup recovery, UI status badges, and tests. Runs fully offline.
- **Phase C (cloud migration, same contracts, separate work item)**: `AzureBlobStorageService`, `AzureStorageScanQueue`, `ScanResultListener`, and `DocumentScanFunction` under `cloud/`; managed-identity RBAC; Azurite-based integration tests; alerting on the poison queue. No changes to `DocumentService`, pages, or the database schema are required.

## Risks and Open Points

- **Existing database**: `EnsureCreated()` cannot add tables to the existing `ContosoDashboard.db`; developers must delete the file once (documented in quickstart).
- **Team Lead scope**: the spec's Team Lead rules are applied to the `TeamLead` role only (not Project Manager/Administrator, who have their own scopes); "team" means same department (spec assumption).
- **Project deletion**: clarification left unresolved in the spec; the app currently has no project-delete feature, so the design makes `Document.ProjectId` nullable with `SetNull` on delete, keeping documents with their owner if deletion is ever added.
- **Toolchain**: `dotnet` was not on the PATH in this session; the SDK for `net10.0` and the `dotnet-ef` tool must be available when implementing. The cloud phase also needs Azure Functions Core Tools and Azurite.
- **Large uploads in Blazor Server**: each file is buffered in memory (≤ 25 MB) and files are processed one at a time to bound memory use.
- **Scope change vs. original spec wording**: scanning is asynchronous with quarantine (spec FR-006/FR-028 updated). A newly uploaded file is unusable for a short time; a replaced file makes the document unavailable until the new file is clean.
- **Azure Functions runtime**: the isolated worker must support the chosen .NET version at cloud-phase time; queue message encoding (Base64 vs. plain) must match on both sides.
- **Single-instance assumption (training)**: the in-process queue is not shared across web instances; scale-out requires the Azure queue mode.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Second project (`tests/ContosoDashboard.Tests`) | The role/project/share authorization matrix and the quarantine state machine are the highest-risk parts of the feature (constitution I and IV) and must be regression-tested | Manual-only verification cannot reliably cover the combinatorial access rules (roles × ownership × project membership × shares) or duplicate/stale scan verdicts |
| Introducing EF Core migrations | Constitution requires schema changes via migrations; `EnsureCreated()` cannot evolve an existing database | Keeping `EnsureCreated()` would force deleting the database for every future schema change and silently skip the new tables on existing databases |
| Background scan pipeline (queue abstraction, workers, quarantine states) instead of an inline scan | Requested design: scanning must run as a background job via Azure Functions + Queue Storage in the cloud; large-file scans must not block uploads | An inline scan blocks the Blazor circuit, cannot use a cloud scanner, and would need rewriting for the Azure Functions design |
| `cloud/` area with Azure projects (Phase C) | Fixes the Azure Functions + Queue Storage deployment path and keeps Azure SDKs out of the training build | Referencing Azure SDKs in the main project would break the offline-first principle |
