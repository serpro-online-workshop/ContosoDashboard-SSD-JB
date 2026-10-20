# Quickstart: Validating Document Upload and Management

A validation guide for the finished feature. Details of entities and contracts are in [data-model.md](./data-model.md) and [contracts/](./contracts/).

## Prerequisites

- .NET SDK that supports `net10.0` and the `dotnet-ef` tool (`dotnet tool install --global dotnet-ef` if missing)
- Run commands from `ContosoDashboard/` unless noted
- Sample users (all existing): `admin@contoso.com` (Administrator), `camille.nicole@contoso.com` (Project Manager), `floris.kregel@contoso.com` (Team Lead), `ni.kang@contoso.com` (Employee)

## One-time setup

1. Stop the app and delete the old local database once: `ContosoDashboard/ContosoDashboard.db` (it was created without migration history, so migrations cannot be applied to it).
2. Start the app: `dotnet run`. Migrations create the schema and seed data, and the upload folder `AppData/uploads` is created on first upload.
3. Open the printed URL and sign in via `/login` by choosing a user.

## Automated checks

```powershell
dotnet test ..\tests\ContosoDashboard.Tests
```

Expected: all tests pass, covering the access matrix, upload validation and cleanup, storage path safety, and search/list filtering.

## Manual validation scenarios

| # | Scenario (spec reference) | Steps | Expected |
|---|---------------------------|-------|----------|
| 1 | Upload (US1) | As Employee, open Documents → Upload, pick a small PDF, enter title + category | Progress shown, success message, document in My Documents with date, size, uploader |
| 2 | Size/type limits (US1) | Try a file > 25 MB; try an `.exe` | Each rejected with a clear message; nothing stored |
| 3 | Content mismatch (US1) | Rename a text file to `.pdf` and upload | Rejected as content mismatch |
| 4 | Multi-file (US1) | Select three files, one invalid | Two succeed, one reports an error |
| 5 | Sort/filter/search (US2) | Upload several; sort by each column; filter by category/project/date; search by tag and uploader | Correct ordering and subsets; no other users' private documents appear |
| 6 | Preview/download (US3) | Preview a PDF and a PNG; download one | Preview renders in-page; download keeps original filename |
| 7 | Edit/replace/delete (US3) | Edit title and tags; replace file; delete and confirm (also cancel once) | Changes persist; delete removes it; cancel keeps it |
| 8 | IDOR (US3, FR-018) | Note a document id as user A; as user B (not permitted) open `/documents/{id}/download` | 404, same as a nonexistent id |
| 9 | Project documents (US4) | As a project member open the project; as the Project Manager upload and delete | Members see/download; manager can manage; non-member gets nothing |
| 10 | Team Lead scope (US4) | Employee in Team Lead's department uploads a Personal Files document with no project; then a Project Documents one | Team Lead cannot see the first, can see/manage the second |
| 11 | Sharing (US5) | Share with a user, then with a department | Recipients get in-app notifications and see it in Shared with Me; others cannot open it |
| 12 | Task integration (US6) | Open a task detail page; upload and attach a document | Appears under the task and in the task's project documents |
| 13 | Dashboard (US6) | Return to `/` | Recent Documents shows last 5 uploads; count card matches |
| 14 | Audit/reports (US7) | Perform upload/download/share/delete; as Administrator open Document Reports; try as Employee | Reports show the activity; Employee is denied |
| 15 | Failure cleanup | Make the upload folder read-only and upload | Error shown; no database record created |
| 16 | Performance | Seed ~500 documents; open list and search; upload a 25 MB file | List and search under 2 s; upload under 30 s |

## Cleaning up between runs

Delete `ContosoDashboard/AppData/uploads` and `ContosoDashboard/ContosoDashboard.db` to return to a clean state; both are recreated on the next run.
