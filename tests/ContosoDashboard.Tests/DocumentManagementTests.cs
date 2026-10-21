using Microsoft.EntityFrameworkCore;
using ContosoDashboard.Models;
using ContosoDashboard.Services;

namespace ContosoDashboard.Tests;

public class DocumentManagementTests : IDisposable
{
    private readonly ServiceHarness _h = new();
    private readonly UserContext _emp = TestDb.Employee;
    private readonly UserContext _pm = TestDb.ProjectManager;
    private readonly UserContext _tl = TestDb.TeamLead;
    private readonly UserContext _admin = TestDb.Admin;
    private readonly UserContext _other = TestDb.OtherEngineer;

    public void Dispose() => _h.Dispose();

    private static async Task<string> ReadAsync(FileContent? content)
    {
        Assert.NotNull(content);
        await using (content)
        using (var reader = new StreamReader(content!.Content))
        {
            return await reader.ReadToEndAsync();
        }
    }

    private List<string> Actions()
    {
        using var context = _h.Db.NewContext();
        return context.DocumentActivities.OrderBy(a => a.DocumentActivityId).Select(a => a.Action).ToList();
    }

    // ---- Download & preview -------------------------------------------------------------------------

    [Fact]
    public async Task Download_ReturnsFileWithOriginalName_AndLogsActivity()
    {
        var id = await _h.UploadAvailableAsync(_emp, "Budget Q1.pdf", projectId: 1);

        var content = await _h.Service.OpenForDownloadAsync(id, _pm);

        Assert.Equal("Budget Q1.pdf", content!.FileName);
        Assert.Equal("application/pdf", content.ContentType);
        Assert.StartsWith("%PDF", await ReadAsync(content));
        Assert.Equal(DocumentActions.Download, Actions().Last());
    }

    [Fact]
    public async Task Download_DeniedForUsersWithoutAccess_WithNoActivityRow()
    {
        var id = await _h.UploadAvailableAsync(_emp, category: DocumentCategories.PersonalFiles);
        var before = Actions().Count;

        Assert.Null(await _h.Service.OpenForDownloadAsync(id, _other));
        Assert.Null(await _h.Service.OpenForDownloadAsync(id, _tl));      // private personal file
        Assert.Null(await _h.Service.OpenForDownloadAsync(9999, _emp));   // unknown id
        Assert.Equal(before, Actions().Count);

        Assert.NotNull(await _h.Service.OpenForDownloadAsync(id, _emp));
        Assert.NotNull(await _h.Service.OpenForDownloadAsync(id, _admin));
    }

    [Fact]
    public async Task Download_And_Preview_RequireAvailableStatus()
    {
        var id = await _h.UploadAsync(_emp, TestFiles.Request("a.pdf", TestFiles.Pdf()));

        Assert.Null(await _h.Service.OpenForDownloadAsync(id, _emp));
        Assert.Null(await _h.Service.OpenForPreviewAsync(id, _emp));
        Assert.Null(await _h.Service.OpenForDownloadAsync(id, _admin));

        await _h.ApplyAsync(id, ScanVerdict.Clean);
        Assert.NotNull(await _h.Service.OpenForDownloadAsync(id, _emp));
    }

    [Fact]
    public async Task Preview_OnlyForPdfJpegAndPng()
    {
        var pdf = await _h.UploadAvailableAsync(_emp, "a.pdf", TestFiles.Pdf());
        var png = await _h.UploadAvailableAsync(_emp, "b.png", TestFiles.Png());
        var txt = await _h.UploadAvailableAsync(_emp, "c.txt", TestFiles.Text("plain"));

        Assert.NotNull(await _h.Service.OpenForPreviewAsync(pdf, _emp));
        Assert.NotNull(await _h.Service.OpenForPreviewAsync(png, _emp));
        Assert.Null(await _h.Service.OpenForPreviewAsync(txt, _emp));
        Assert.NotNull(await _h.Service.OpenForDownloadAsync(txt, _emp)); // still downloadable

        Assert.Equal(new[] { DocumentActions.Preview, DocumentActions.Preview, DocumentActions.Download },
            Actions().Where(a => a is DocumentActions.Preview or DocumentActions.Download).ToArray());
    }

    [Fact]
    public async Task Download_WhenStoredFileIsMissing_ReturnsNull()
    {
        var id = await _h.UploadAvailableAsync(_emp);
        File.Delete(Assert.Single(_h.StoredFiles()));

        Assert.Null(await _h.Service.OpenForDownloadAsync(id, _emp));
    }

    [Fact]
    public async Task Get_ReturnsDetails_OnlyWhenPermitted_AndShowsOwnNonAvailableStatus()
    {
        var pending = await _h.UploadAsync(_emp, TestFiles.Request("a.pdf", TestFiles.Pdf(), "Pending doc"));

        var own = await _h.Service.GetAsync(pending, _emp);
        Assert.Equal(ScanStatuses.Pending, own!.ScanStatus);
        Assert.Equal("a.pdf", own.OriginalFileName);

        Assert.Null(await _h.Service.GetAsync(pending, _other));
        Assert.Null(await _h.Service.GetAsync(pending, _pm));
        Assert.NotNull(await _h.Service.GetAsync(pending, _admin));
    }

    // ---- Edit --------------------------------------------------------------------------------------

    [Fact]
    public async Task Edit_UpdatesMetadata_AndLogsActivity()
    {
        var id = await _h.UploadAvailableAsync(_emp, projectId: 1);

        var result = await _h.Service.UpdateMetadataAsync(id,
            new DocumentMetadata("  New title ", "New description", DocumentCategories.Reports, "One, TWO"), _emp);

        Assert.True(result.Success);
        var document = _h.Load(id);
        Assert.Equal("New title", document.Title);
        Assert.Equal("New description", document.Description);
        Assert.Equal(DocumentCategories.Reports, document.Category);
        Assert.Equal("one,two", document.Tags);
        Assert.Equal(ScanStatuses.Available, document.ScanStatus);
        Assert.Equal(DocumentActions.Edit, Actions().Last());
    }

    [Fact]
    public async Task Edit_ValidatesInput()
    {
        var id = await _h.UploadAvailableAsync(_emp);

        Assert.Equal(DocumentError.InvalidMetadata,
            (await _h.Service.UpdateMetadataAsync(id, new DocumentMetadata("", null, DocumentCategories.Other, null), _emp)).Error);
        Assert.Equal(DocumentError.InvalidMetadata,
            (await _h.Service.UpdateMetadataAsync(id, new DocumentMetadata("Ok", null, "Bogus", null), _emp)).Error);
        Assert.Equal("Available document", _h.Load(id).Title);
    }

    // ---- Who may manage ----------------------------------------------------------------------------

    [Fact]
    public async Task Manage_AllowedForUploaderProjectManagerTeamLeadAndAdmin()
    {
        foreach (var manager in new[] { _emp, _pm, _tl, _admin })
        {
            var id = await _h.UploadAvailableAsync(_emp, projectId: 1);
            var result = await _h.Service.UpdateMetadataAsync(id,
                new DocumentMetadata("Edited by " + manager.Role, null, DocumentCategories.Reports, null), manager);
            Assert.True(result.Success, $"{manager.Role} should be able to edit");
        }
    }

    [Fact]
    public async Task Manage_DeniedForShareRecipientsAndOutsiders_AsNotFound()
    {
        var id = await _h.UploadAvailableAsync(_emp, category: DocumentCategories.PersonalFiles);
        _h.Db.ShareWithUser(id, _other, _emp);
        _h.Db.ShareWithUser(id, _tl, _emp);

        foreach (var denied in new[] { _other, _tl, TestDb.SalesEmployee })
        {
            Assert.Equal(DocumentError.NotFound,
                (await _h.Service.UpdateMetadataAsync(id, new DocumentMetadata("Hacked", null, DocumentCategories.Other, null), denied)).Error);
            Assert.Equal(DocumentError.NotFound,
                (await _h.Service.ReplaceFileAsync(id, TestFiles.File("x.pdf", TestFiles.Pdf()), denied)).Error);
            Assert.Equal(DocumentError.NotFound, (await _h.Service.DeleteAsync(id, denied)).Error);
        }

        var document = _h.Load(id);
        Assert.Equal("Available document", document.Title);
        Assert.Single(_h.StoredFiles());
    }

    [Fact]
    public async Task ProjectManager_CannotManageDocumentsOfOtherProjectsOrPersonalFiles()
    {
        var personal = await _h.UploadAvailableAsync(_emp, category: DocumentCategories.PersonalFiles);

        Assert.Equal(DocumentError.NotFound, (await _h.Service.DeleteAsync(personal, _pm)).Error);
    }

    // ---- Replace -----------------------------------------------------------------------------------

    [Fact]
    public async Task Replace_StoresNewFile_ResetsScan_EnqueuesAndRemovesOldFile()
    {
        var id = await _h.UploadAvailableAsync(_emp, "old.pdf", projectId: 1);
        var before = _h.Load(id);
        var oldPath = before.FilePath;
        _h.Queue.Requests.Clear();

        var result = await _h.Service.ReplaceFileAsync(id, TestFiles.File("new picture.png", TestFiles.Png(500)), _emp);

        Assert.True(result.Success, result.Message);
        var after = _h.Load(id);
        Assert.NotEqual(oldPath, after.FilePath);
        Assert.EndsWith(".png", after.FilePath);
        Assert.Equal("image/png", after.FileType);
        Assert.Equal("new picture.png", after.OriginalFileName);
        Assert.Equal(508, after.FileSize);
        Assert.Equal(ScanStatuses.Pending, after.ScanStatus);
        Assert.NotEqual(before.ScanAttemptId, after.ScanAttemptId);

        var request = Assert.Single(_h.Queue.Requests);
        Assert.Equal(after.ScanAttemptId, request.ScanAttemptId);

        var files = _h.StoredFiles().ToList();
        Assert.Single(files);
        Assert.EndsWith(".png", files[0]);
        Assert.Equal(DocumentActions.Replace, Actions().Last());

        // Unavailable until the new file is scanned clean
        Assert.Null(await _h.Service.OpenForDownloadAsync(id, _emp));
        await _h.ApplyAsync(id, ScanVerdict.Clean);
        Assert.NotNull(await _h.Service.OpenForDownloadAsync(id, _emp));
    }

    [Fact]
    public async Task Replace_WithInvalidFile_ChangesNothing()
    {
        var id = await _h.UploadAvailableAsync(_emp);
        var before = _h.Load(id);

        var result = await _h.Service.ReplaceFileAsync(id, TestFiles.File("fake.pdf", TestFiles.Text("not a pdf")), _emp);

        Assert.Equal(DocumentError.ContentMismatch, result.Error);
        var after = _h.Load(id);
        Assert.Equal(before.FilePath, after.FilePath);
        Assert.Equal(ScanStatuses.Available, after.ScanStatus);
        Assert.Single(_h.StoredFiles());
    }

    [Fact]
    public async Task Replace_DatabaseFailure_RemovesTheNewFile_AndKeepsTheOld()
    {
        var id = await _h.UploadAvailableAsync(_emp);
        var oldPath = _h.Load(id).FilePath;

        _h.Context.FailOnSave = true;
        var result = await _h.Service.ReplaceFileAsync(id, TestFiles.File("new.pdf", TestFiles.Pdf()), _emp);
        _h.Context.FailOnSave = false;

        Assert.Equal(DocumentError.StorageFailure, result.Error);
        Assert.Equal(oldPath, _h.Load(id).FilePath);
        Assert.Single(_h.StoredFiles());
    }

    // ---- Delete ------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesRecordFileAndShares_ButKeepsActivityHistory()
    {
        var id = await _h.UploadAvailableAsync(_emp, "gone.pdf", projectId: 1, title: "Doomed");
        _h.Db.ShareWithUser(id, _other, _emp);
        _h.Db.ShareWithDepartment(id, "Sales", _emp);

        var result = await _h.Service.DeleteAsync(id, _emp);

        Assert.True(result.Success);
        using var context = _h.Db.NewContext();
        Assert.False(context.Documents.Any(d => d.DocumentId == id));
        Assert.Empty(context.DocumentShares);
        Assert.Empty(_h.StoredFiles());

        var history = context.DocumentActivities.Where(a => a.DocumentId == id).OrderBy(a => a.DocumentActivityId).ToList();
        Assert.Equal(DocumentActions.Delete, history.Last().Action);
        Assert.All(history, a => Assert.Equal("Doomed", a.DocumentTitle));
    }

    [Theory]
    [InlineData(ScanStatuses.Rejected)]
    [InlineData(ScanStatuses.ScanFailed)]
    [InlineData(ScanStatuses.Pending)]
    public async Task Delete_IsAllowedInAnyScanState(string status)
    {
        var id = _h.Db.AddDocument(_emp, scanStatus: status);

        Assert.True((await _h.Service.DeleteAsync(id, _emp)).Success);
        Assert.False(_h.Context.Documents.AsNoTracking().Any(d => d.DocumentId == id));
    }

    [Fact]
    public async Task Delete_ByProjectManager_Succeeds_ForProjectDocuments()
    {
        var id = await _h.UploadAvailableAsync(_emp, projectId: 1);

        Assert.True((await _h.Service.DeleteAsync(id, _pm)).Success);
        Assert.Empty(_h.StoredFiles());
    }

    [Fact]
    public async Task Delete_UnknownDocument_IsNotFound()
    {
        Assert.Equal(DocumentError.NotFound, (await _h.Service.DeleteAsync(12345, _admin)).Error);
    }

    [Fact]
    public async Task Activity_IsRecordedForEveryOperation()
    {
        var id = await _h.UploadAvailableAsync(_emp, "a.pdf", projectId: 1);
        await using (await _h.Service.OpenForDownloadAsync(id, _emp) ?? throw new InvalidOperationException()) { }
        await using (await _h.Service.OpenForPreviewAsync(id, _emp) ?? throw new InvalidOperationException()) { }
        await _h.Service.UpdateMetadataAsync(id, new DocumentMetadata("T", null, DocumentCategories.Other, null), _emp);
        await _h.Service.ReplaceFileAsync(id, TestFiles.File("b.pdf", TestFiles.Pdf()), _emp);
        await _h.Service.DeleteAsync(id, _emp);

        Assert.Equal(
            new[]
            {
                DocumentActions.Upload, DocumentActions.ScanCompleted, DocumentActions.Download, DocumentActions.Preview,
                DocumentActions.Edit, DocumentActions.Replace, DocumentActions.Delete
            },
            Actions().ToArray());
    }
}
