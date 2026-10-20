# Contract: HTTP Endpoints and UI Routes

## File endpoints (minimal API)

Both require an authenticated user (`RequireAuthorization()`); identity comes from the cookie claims.

| Route | Method | Success | Not found / not permitted | Notes |
|-------|--------|---------|---------------------------|-------|
| `/documents/{id:int}/download` | GET | 200 file stream, `Content-Disposition: attachment` with sanitized original filename, stored MIME type, `X-Content-Type-Options: nosniff` | 404 (same response for missing, unauthorized, and not-yet-`Available` documents) | Logs a `Download` activity |
| `/documents/{id:int}/preview` | GET | 200 file stream, `Content-Disposition: inline`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN` (this route only) | 404 (as above); 415 if the type is not previewable (only PDF, JPEG, PNG) | Logs a `Preview` activity |

Unauthenticated requests are redirected to `/login` by the existing cookie configuration. Cache headers: `Cache-Control: private, no-store` on both routes.

## UI routes (Blazor)

| Route | Page | Access | Purpose |
|-------|------|--------|---------|
| `/documents` | `Documents.razor` | `[Authorize]` | My Documents and Shared with Me tabs, search, sort, filter, upload, edit, replace, share, delete |
| `/projects/{projectId:int}` | `ProjectDetails.razor` (changed) | `[Authorize]` | New Documents section listing project documents; upload for permitted members |
| `/tasks/{taskId:int}` | `TaskDetails.razor` (new) | `[Authorize]` | Task detail with attached documents, upload, attach/detach |
| `/` | `Index.razor` (changed) | `[Authorize]` | Recent Documents widget (last 5 own uploads) and document-count summary card |
| `/documents/reports` | `DocumentReports.razor` (new) | `[Authorize(Policy = "Administrator")]` | File-type, uploader, and access-pattern reports |

Navigation: `NavMenu.razor` gains "Documents" for all users and "Document Reports" for Administrators only.

## Notifications (existing page)

Three new notification types appear on `/notifications`: `DocumentShared` ("{user} shared '{title}' with you"), `DocumentAdded` ("New document '{title}' added to {project}", sent once the scan is clean), and `DocumentRejected` ("'{title}' was rejected by the security scan", sent to the uploader).

## Scan status in the UI

Document lists show a status badge for the uploader/Administrators: *Scanning* (with auto-refresh while any document is pending), *Available*, *Rejected* (reason shown, delete action), *Scan failed* (Retry and Delete actions). Download/preview/share actions are disabled until *Available*. The upload dialog confirms "Uploaded — scanning for malware" rather than "available".

## Upload form contract (UI)

| Field | Required | Validation |
|-------|----------|------------|
| Files | Yes | One or more; each ≤ 25 MB; allowed extensions only; per-file result shown |
| Title | Yes | 1–255 characters; applied to a single file, or a per-file title when several are selected |
| Category | Yes | One of the six fixed values |
| Description | No | ≤ 2000 characters |
| Project | No | Only projects the user belongs to or manages |
| Tags | No | Comma-separated, normalized to lowercase |

A progress indicator is shown while a file is transferring; success and error messages are shown per file. The form is reachable in no more than three clicks from the page that hosts it (open dialog → choose file/fill form → submit).

## Configuration surface

`appsettings.json` → `DocumentStorage` (see [file-storage-service.md](./file-storage-service.md)) and `MalwareScan` (see [scan-pipeline.md](./scan-pipeline.md)). No new secrets.
