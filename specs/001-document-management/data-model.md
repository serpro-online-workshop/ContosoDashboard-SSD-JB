# Data Model: Document Upload and Management

Conventions follow the existing models: integer identity keys, `DateTime` values in UTC, data annotations plus fluent configuration in `ApplicationDbContext`.

## Document

| Field | Type | Rules |
|-------|------|-------|
| DocumentId | int (PK) | Identity |
| Title | string(255) | Required |
| Description | string(2000)? | Optional |
| Category | string(50) | Required; one of: `Project Documents`, `Team Resources`, `Personal Files`, `Reports`, `Presentations`, `Other` |
| Tags | string(500)? | Normalized lowercase, comma-separated, max 10 tags, each ≤ 30 characters |
| OriginalFileName | string(255) | Display/download name only; sanitized; never used in storage paths |
| FileType | string(255) | MIME type derived server-side (long Office types fit) |
| FileSize | long | Bytes; 1 to 26,214,400 |
| FilePath | string(500) | Required, unique; relative storage key `{userId}/{projectId or "personal"}/{guid}.{ext}` |
| UploaderUserId | int (FK → User) | Required; `Restrict` on delete |
| ProjectId | int? (FK → Project) | Optional; `SetNull` on project delete |
| TaskId | int? (FK → TaskItem) | Optional; `SetNull` on task delete; when set, `ProjectId` equals the task's project |
| UploadDate | DateTime | Set at creation |
| UpdatedDate | DateTime | Set on edit/replace |

**Indexes**: `UploaderUserId`, `ProjectId`, `TaskId`, `UploadDate`, `Category`, unique `FilePath`.

**Validation rules**
- Title required; Category must be in the fixed list.
- Uploader must be a member or manager of `ProjectId` (or Administrator) when a project is set.
- File extension must be in the whitelist and the file signature must match it.
- `FilePath` is generated before insertion, so it is never empty or duplicated.

**State transitions**: Created (upload) → Edited (metadata) / Replaced (file) → Shared (adds `DocumentShare` rows) → Deleted (record and file permanently removed; shares removed by cascade; activity history retained).

## DocumentShare

| Field | Type | Rules |
|-------|------|-------|
| DocumentShareId | int (PK) | Identity |
| DocumentId | int (FK → Document) | Required; cascade delete |
| SharedWithUserId | int? (FK → User) | Set for a user share |
| SharedWithDepartment | string(100)? | Set for a team (department) share |
| SharedByUserId | int (FK → User) | Required; `Restrict` |
| SharedDate | DateTime | Set at creation |

**Rules**: Exactly one of `SharedWithUserId` / `SharedWithDepartment` is set (check constraint). Unique per `(DocumentId, SharedWithUserId)` and per `(DocumentId, SharedWithDepartment)`. Shares grant view and download only. A department share applies to whoever is currently in that department. Indexes on `SharedWithUserId` and `SharedWithDepartment`.

## DocumentActivity

| Field | Type | Rules |
|-------|------|-------|
| DocumentActivityId | int (PK) | Identity |
| DocumentId | int | Plain value (no FK) so history survives deletion |
| DocumentTitle | string(255) | Snapshot at time of action |
| FileType | string(255)? | Snapshot, used by the file-type report |
| UserId | int (FK → User) | Actor; `Restrict` |
| Action | string(20) | `Upload`, `Download`, `Preview`, `Edit`, `Replace`, `Delete`, `Share` |
| Timestamp | DateTime | Set at creation |
| Details | string(500)? | E.g., share target |

**Indexes**: `(Action, Timestamp)`, `UserId`, `DocumentId`.

## Changes to existing entities

- **Notification / NotificationType**: append `DocumentShared` and `DocumentAdded` to the end of the enum (integer-backed, so existing values are unchanged).
- **User**: add `Documents` navigation (uploaded documents); no new columns. `Department` (already present) defines "team".
- **Project**: add `Documents` navigation; **TaskItem**: add `Documents` navigation.
- **Claims (not schema)**: login adds a `Department` claim.

## Relationships

```text
User 1─* Document (UploaderUserId)
Project 0..1─* Document        TaskItem 0..1─* Document
Document 1─* DocumentShare ─* (User | Department)
Document 1─* DocumentActivity (by DocumentId value, no FK)
```

## Access query shape (informational)

A user can view a document when any of these holds: they are an Administrator; they uploaded it; they manage or belong to its project; a share targets them or their department; or they are a `TeamLead` and the uploader shares their department, except a no-project, unshared `Personal Files` document. The same predicate is used for single-document checks and list/search/count queries.
