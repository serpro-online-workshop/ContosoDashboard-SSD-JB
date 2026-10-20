# Implementation Plan: Document Upload and Management

**Branch**: `001-document-management` | **Date**: 2026-10-20 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-document-management/spec.md`

## Summary

Add centralized document upload, organization, sharing, and audit to the existing ContosoDashboard Blazor Server app. Files are stored on the local filesystem outside `wwwroot` behind an `IFileStorageService` abstraction; metadata, shares, and an activity log live in the existing SQLite database via EF Core. All reads and writes go through a `DocumentService` that centralizes the role/project/share access rules, and files are served only through authorized endpoints. UI additions: a Documents page (My Documents / Shared with Me tabs), a project Documents section, a task detail page with attachments, dashboard widgets, and an Administrator reports page.

## Technical Context

**Language/Version**: C# on .NET 10 (`net10.0`), nullable reference types and implicit usings enabled

**Primary Dependencies**: ASP.NET Core Blazor Server (`InputFile`), Entity Framework Core 10 (SQLite provider), cookie authentication with claims; no new runtime packages

**Storage**: SQLite (`ContosoDashboard.db`) for metadata; local filesystem under `AppData/uploads` (content root, outside `wwwroot`) for file bytes

**Testing**: xUnit test project `tests/ContosoDashboard.Tests` using in-memory SQLite for service and authorization tests; manual end-to-end checks per [quickstart.md](./quickstart.md)

**Target Platform**: Web (Blazor Server) on a developer machine/training environment; Windows, Linux, macOS

**Project Type**: Web application (single project, server-rendered Blazor)

**Performance Goals**: Upload ≤ 30 s for 25 MB; document list ≤ 2 s at 500 documents; search ≤ 2 s; preview ≤ 3 s (spec SC-002, SC-003)

**Constraints**: Offline (no cloud services); local file storage; storage behind `IFileStorageService`; existing mock cookie auth and role policies; 25 MB per-file limit; no major rewrites

**Scale/Scope**: Training scale (hundreds of users, thousands of documents); about 6 new/changed pages or components, 3 new tables

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | How the plan satisfies it |
|-----------|--------|---------------------------|
| I. Security by Default | PASS | All new pages use `[Authorize]`; endpoints use `RequireAuthorization()`; one `DocumentAccess` rule set enforced in `DocumentService` for every read/write/list/search; unauthorized and missing documents both return 404 (no existence leak); extension whitelist + content-signature check, system-generated storage names, path-traversal guard; files outside `wwwroot`. |
| II. Service-Layer Architecture | PASS | Logic in `DocumentService`, `IFileStorageService`, `IMalwareScanner`; Razor components call services only; entities in `Models/`; EF mappings in `ApplicationDbContext`. |
| III. Offline-First with Cloud Path | PASS | `LocalFileStorageService` default; Azure Blob implementation can be swapped via DI/config; relative storage keys are provider-neutral; scanner is pluggable (no-op in training). |
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
│   ├── IMalwareScanner.cs             # new (interface + NoOpMalwareScanner)
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
    └── DocumentSearchTests.cs
```

**Structure Decision**: Keep the single `ContosoDashboard` web project and its existing folder conventions; add an `Endpoints/` folder for file-serving routes and one sibling test project. No separate backend/frontend split.

## Key Design Decisions (summary; details in research.md)

1. **Upload sequence**: validate → authorize → scan → generate GUID storage key → write file → insert metadata; on a database failure, delete the written file; never leave a record without a file.
2. **Access rules in one place**: a `DocumentAccess` helper yields both a per-document check and an `IQueryable` filter, so lists, search, and the dashboard cannot drift from the single-document rules.
3. **Serving files**: minimal-API endpoints stream from `IFileStorageService`; preview is `Content-Disposition: inline` for PDF/JPEG/PNG only, with `X-Frame-Options: SAMEORIGIN` on that route only (global header stays `DENY`).
4. **Schema management**: switch from `EnsureCreated()` to EF Core migrations with a baseline; a one-time reset of the local dev database is required.
5. **Task integration**: the app has no task detail page today, so one is added to host attachments.
6. **Claims**: add the `Department` claim at login so team-based rules work without extra lookups.

## Risks and Open Points

- **Existing database**: `EnsureCreated()` cannot add tables to the existing `ContosoDashboard.db`; developers must delete the file once (documented in quickstart).
- **Team Lead scope**: the spec's Team Lead rules are applied to the `TeamLead` role only (not Project Manager/Administrator, who have their own scopes); "team" means same department (spec assumption).
- **Project deletion**: clarification left unresolved in the spec; the app currently has no project-delete feature, so the design makes `Document.ProjectId` nullable with `SetNull` on delete, keeping documents with their owner if deletion is ever added.
- **Toolchain**: `dotnet` was not on the PATH in this session; the SDK for `net10.0` and the `dotnet-ef` tool must be available when implementing.
- **Large uploads in Blazor Server**: each file is buffered in memory (≤ 25 MB) and files are processed one at a time to bound memory use.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Second project (`tests/ContosoDashboard.Tests`) | The role/project/share authorization matrix is the highest-risk part of the feature (constitution I and IV) and must be regression-tested | Manual-only verification cannot reliably cover the combinatorial access rules (roles × ownership × project membership × shares) |
| Introducing EF Core migrations | Constitution requires schema changes via migrations; `EnsureCreated()` cannot evolve an existing database | Keeping `EnsureCreated()` would force deleting the database for every future schema change and silently skip the new tables on existing databases |
