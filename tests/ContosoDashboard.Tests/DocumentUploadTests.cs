using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ContosoDashboard.Models;
using ContosoDashboard.Services;

namespace ContosoDashboard.Tests;

public class DocumentUploadTests : IDisposable
{
    private readonly ServiceHarness _h = new();
    private readonly UserContext _emp = TestDb.Employee;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task ValidUpload_CreatesPendingDocument_WithGuidKey_ActivityAndQueuedScan()
    {
        var result = await _h.Service.UploadAsync(
            TestFiles.Request("Plan.PDF", TestFiles.Pdf(), "Plan", projectId: 1), _emp);

        Assert.True(result.Success, result.Message);
        var document = _h.Load(result.DocumentId!.Value);

        Assert.Equal(ScanStatuses.Pending, document.ScanStatus);
        Assert.Matches(new Regex(@"^4/1/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.pdf$"), document.FilePath);
        Assert.Equal("application/pdf", document.FileType);
        Assert.Equal("Plan.PDF", document.OriginalFileName);
        Assert.Equal(4, document.UploaderUserId);
        Assert.True(document.FileSize > 0);
        Assert.Single(_h.StoredFiles());

        var request = Assert.Single(_h.Queue.Requests);
        Assert.Equal(document.DocumentId, request.DocumentId);
        Assert.Equal(document.ScanAttemptId, request.ScanAttemptId);
        Assert.Equal(document.FilePath, request.StorageKey);

        using var context = _h.Db.NewContext();
        var activity = Assert.Single(context.DocumentActivities);
        Assert.Equal(DocumentActions.Upload, activity.Action);
        Assert.Equal(4, activity.UserId);
        Assert.Equal("Plan", activity.DocumentTitle);

        // No project notification until the scan is clean
        Assert.Empty(context.Notifications);
    }

    [Fact]
    public async Task PersonalUpload_UsesPersonalSegmentInKey()
    {
        var id = await _h.UploadAsync(_emp, TestFiles.Request("a.txt", TestFiles.Text("hello")));
        Assert.StartsWith("4/personal/", _h.Load(id).FilePath);
        Assert.EndsWith(".txt", _h.Load(id).FilePath);
    }

    [Fact]
    public async Task FileOverTheLimit_IsRejected_ExactlyAtLimitIsAccepted()
    {
        var limit = 26_214_400;

        var tooBig = new byte[limit + 1];
        "%PDF"u8.CopyTo(tooBig);
        var rejected = await _h.Service.UploadAsync(TestFiles.Request("big.pdf", new MemoryStream(tooBig)), _emp);
        Assert.False(rejected.Success);
        Assert.Equal(DocumentError.FileTooLarge, rejected.Error);
        Assert.Contains("25 MB", rejected.Message);

        var exact = new byte[limit];
        "%PDF"u8.CopyTo(exact);
        var accepted = await _h.Service.UploadAsync(TestFiles.Request("exact.pdf", new MemoryStream(exact)), _emp);
        Assert.True(accepted.Success, accepted.Message);
    }

    [Fact]
    public async Task EmptyFile_IsRejected()
    {
        var result = await _h.Service.UploadAsync(TestFiles.Request("empty.pdf", new MemoryStream()), _emp);
        Assert.Equal(DocumentError.EmptyFile, result.Error);
        Assert.Empty(_h.StoredFiles());
    }

    [Theory]
    [InlineData("virus.exe")]
    [InlineData("script.js")]
    [InlineData("archive.zip")]
    [InlineData("noextension")]
    [InlineData("double.pdf.exe")]
    public async Task UnsupportedExtensions_AreRejected_WithSupportedTypesListed(string fileName)
    {
        var result = await _h.Service.UploadAsync(TestFiles.Request(fileName, TestFiles.Text("%PDF-1.4 data")), _emp);

        Assert.False(result.Success);
        Assert.Equal(DocumentError.UnsupportedType, result.Error);
        Assert.Contains("PDF", result.Message);
        Assert.Empty(_h.StoredFiles());
    }

    [Fact]
    public async Task ContentSignatureMismatch_IsRejected()
    {
        var textAsPdf = await _h.Service.UploadAsync(TestFiles.Request("fake.pdf", TestFiles.Text("just some text")), _emp);
        Assert.Equal(DocumentError.ContentMismatch, textAsPdf.Error);

        var pdfAsPng = await _h.Service.UploadAsync(TestFiles.Request("fake.png", TestFiles.Pdf()), _emp);
        Assert.Equal(DocumentError.ContentMismatch, pdfAsPng.Error);

        var binaryAsTxt = await _h.Service.UploadAsync(
            TestFiles.Request("fake.txt", new MemoryStream(new byte[] { 0x41, 0x00, 0x42 })), _emp);
        Assert.Equal(DocumentError.ContentMismatch, binaryAsTxt.Error);

        Assert.Empty(_h.StoredFiles());
    }

    [Fact]
    public async Task MissingOrInvalidMetadata_IsRejected()
    {
        var noTitle = await _h.Service.UploadAsync(TestFiles.Request("a.pdf", TestFiles.Pdf(), title: "  "), _emp);
        Assert.Equal(DocumentError.InvalidMetadata, noTitle.Error);
        Assert.Contains("title", noTitle.Message, StringComparison.OrdinalIgnoreCase);

        var noCategory = await _h.Service.UploadAsync(TestFiles.Request("a.pdf", TestFiles.Pdf(), category: ""), _emp);
        Assert.Equal(DocumentError.InvalidMetadata, noCategory.Error);

        var badCategory = await _h.Service.UploadAsync(TestFiles.Request("a.pdf", TestFiles.Pdf(), category: "Secrets"), _emp);
        Assert.Equal(DocumentError.InvalidMetadata, badCategory.Error);

        var longTitle = await _h.Service.UploadAsync(TestFiles.Request("a.pdf", TestFiles.Pdf(), title: new string('x', 256)), _emp);
        Assert.Equal(DocumentError.InvalidMetadata, longTitle.Error);

        var longDescription = await _h.Service.UploadAsync(
            TestFiles.Request("a.pdf", TestFiles.Pdf(), description: new string('x', 2001)), _emp);
        Assert.Equal(DocumentError.InvalidMetadata, longDescription.Error);

        Assert.Empty(_h.StoredFiles());
    }

    [Fact]
    public async Task UploadToProject_RequiresMembership()
    {
        var notMember = await _h.Service.UploadAsync(
            TestFiles.Request("a.pdf", TestFiles.Pdf(), projectId: 1), TestDb.OtherEngineer);
        Assert.Equal(DocumentError.NotProjectMember, notMember.Error);
        Assert.Empty(_h.StoredFiles());

        var unknownProject = await _h.Service.UploadAsync(TestFiles.Request("a.pdf", TestFiles.Pdf(), projectId: 999), _emp);
        Assert.Equal(DocumentError.NotProjectMember, unknownProject.Error);

        Assert.True((await _h.Service.UploadAsync(TestFiles.Request("a.pdf", TestFiles.Pdf(), projectId: 1), _emp)).Success);
        Assert.True((await _h.Service.UploadAsync(TestFiles.Request("b.pdf", TestFiles.Pdf(), projectId: 1), TestDb.ProjectManager)).Success);
        Assert.True((await _h.Service.UploadAsync(TestFiles.Request("c.pdf", TestFiles.Pdf(), projectId: 1), TestDb.Admin)).Success);
    }

    [Fact]
    public async Task TaskAttachment_IsNotAvailableYet()
    {
        var request = TestFiles.Request("a.pdf", TestFiles.Pdf());
        request.TaskId = 1;

        var result = await _h.Service.UploadAsync(request, _emp);

        Assert.Equal(DocumentError.InvalidMetadata, result.Error);
        Assert.Empty(_h.StoredFiles());
    }

    [Fact]
    public async Task DatabaseFailureAfterFileWrite_DeletesTheStoredFile_AndLeavesNoRecord()
    {
        _h.Context.FailOnSave = true;

        var result = await _h.Service.UploadAsync(TestFiles.Request("a.pdf", TestFiles.Pdf()), _emp);

        Assert.False(result.Success);
        Assert.Equal(DocumentError.StorageFailure, result.Error);
        Assert.Empty(_h.StoredFiles());
        Assert.Empty(_h.Queue.Requests);

        using var context = _h.Db.NewContext();
        Assert.Empty(context.Documents);
    }

    [Fact]
    public async Task EnqueueFailure_KeepsDocumentPending_ForRecovery()
    {
        _h.Queue.Fail = true;

        var result = await _h.Service.UploadAsync(TestFiles.Request("a.pdf", TestFiles.Pdf()), _emp);

        Assert.True(result.Success);
        Assert.Equal(ScanStatuses.Pending, _h.Load(result.DocumentId!.Value).ScanStatus);

        var pending = await _h.Service.GetPendingScanRequestsAsync();
        Assert.Equal(result.DocumentId, Assert.Single(pending).DocumentId);
    }

    [Fact]
    public async Task Tags_AreNormalized_AndLimited()
    {
        var id = await _h.UploadAsync(_emp, TestFiles.Request("a.pdf", TestFiles.Pdf(), tags: " Alpha, beta ,ALPHA,, Gamma "));
        Assert.Equal("alpha,beta,gamma", _h.Load(id).Tags);

        var elevenTags = string.Join(",", Enumerable.Range(1, 11).Select(i => "t" + i));
        var tooMany = await _h.Service.UploadAsync(TestFiles.Request("a.pdf", TestFiles.Pdf(), tags: elevenTags), _emp);
        Assert.Equal(DocumentError.InvalidMetadata, tooMany.Error);

        var tooLong = await _h.Service.UploadAsync(TestFiles.Request("a.pdf", TestFiles.Pdf(), tags: new string('x', 31)), _emp);
        Assert.Equal(DocumentError.InvalidMetadata, tooLong.Error);

        var tenTags = string.Join(",", Enumerable.Range(1, 10).Select(i => "t" + i));
        Assert.True((await _h.Service.UploadAsync(TestFiles.Request("a.pdf", TestFiles.Pdf(), tags: tenTags), _emp)).Success);
    }

    [Fact]
    public async Task UserSuppliedFileName_NeverReachesTheStoragePath()
    {
        var id = await _h.UploadAsync(_emp, TestFiles.Request("..\\..\\evil name.pdf", TestFiles.Pdf()));
        var document = _h.Load(id);

        Assert.DoesNotContain("evil", document.FilePath);
        Assert.DoesNotContain("..", document.FilePath);
        Assert.Equal("evil name.pdf", document.OriginalFileName);
    }

    [Fact]
    public async Task Batch_EachFileIsReportedIndependently()
    {
        var results = new[]
        {
            await _h.Service.UploadAsync(TestFiles.Request("good1.pdf", TestFiles.Pdf(), "One"), _emp),
            await _h.Service.UploadAsync(TestFiles.Request("bad.exe", TestFiles.Text("x"), "Two"), _emp),
            await _h.Service.UploadAsync(TestFiles.Request("good2.png", TestFiles.Png(), "Three"), _emp)
        };

        Assert.Equal(new[] { true, false, true }, results.Select(r => r.Success).ToArray());
        Assert.Equal(2, _h.StoredFiles().Count());
    }

    [Fact]
    public async Task ClientContentType_IsNotTrusted()
    {
        var request = TestFiles.Request("a.pdf", TestFiles.Pdf());
        request.File.ContentType = "text/html";

        var id = await _h.UploadAsync(_emp, request);

        Assert.Equal("application/pdf", _h.Load(id).FileType);
    }
}
