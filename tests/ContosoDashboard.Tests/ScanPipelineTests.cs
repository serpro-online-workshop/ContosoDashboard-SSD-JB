using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Services;

namespace ContosoDashboard.Tests;

public class ScanPipelineTests : IDisposable
{
    private readonly ServiceHarness _h = new();
    private readonly UserContext _emp = TestDb.Employee;

    public void Dispose() => _h.Dispose();

    private List<Notification> Notifications(NotificationType type)
    {
        using var context = _h.Db.NewContext();
        return context.Notifications.AsNoTracking().Where(n => n.Type == type).ToList();
    }

    private async Task<int> PendingProjectDocumentAsync()
        => await _h.UploadAsync(_emp, TestFiles.Request("a.pdf", TestFiles.Pdf(), "Spec", projectId: 1));

    [Fact]
    public async Task CleanVerdict_MakesDocumentAvailable_AndNotifiesProjectMembersExceptUploader()
    {
        var id = await PendingProjectDocumentAsync();

        Assert.True(await _h.ApplyAsync(id, ScanVerdict.Clean));

        var document = _h.Load(id);
        Assert.Equal(ScanStatuses.Available, document.ScanStatus);
        Assert.NotNull(document.ScanCompletedDate);

        var recipients = Notifications(NotificationType.DocumentAdded).Select(n => n.UserId).OrderBy(x => x).ToList();
        Assert.Equal(new[] { 2, 3 }, recipients); // manager and team lead; the uploader (4) is excluded
    }

    [Fact]
    public async Task CleanVerdict_HonorsInAppNotificationPreference()
    {
        using (var context = _h.Db.NewContext())
        {
            context.Users.Single(u => u.UserId == 3).InAppNotificationsEnabled = false;
            context.SaveChanges();
        }

        var id = await PendingProjectDocumentAsync();
        await _h.ApplyAsync(id, ScanVerdict.Clean);

        Assert.Equal(new[] { 2 }, Notifications(NotificationType.DocumentAdded).Select(n => n.UserId).ToArray());
    }

    [Fact]
    public async Task PersonalDocument_CleanVerdict_SendsNoProjectNotifications()
    {
        var id = await _h.UploadAsync(_emp, TestFiles.Request("a.pdf", TestFiles.Pdf()));
        await _h.ApplyAsync(id, ScanVerdict.Clean);

        Assert.Empty(Notifications(NotificationType.DocumentAdded));
    }

    [Fact]
    public async Task InfectedVerdict_DeletesFile_RejectsDocument_AndNotifiesUploader()
    {
        var id = await PendingProjectDocumentAsync();
        Assert.Single(_h.StoredFiles());

        Assert.True(await _h.ApplyAsync(id, ScanVerdict.Infected, reason: "Detected as Test.Virus"));

        var document = _h.Load(id);
        Assert.Equal(ScanStatuses.Rejected, document.ScanStatus);
        Assert.Equal("Detected as Test.Virus", document.ScanDetail);
        Assert.Empty(_h.StoredFiles());

        var notification = Assert.Single(Notifications(NotificationType.DocumentRejected));
        Assert.Equal(4, notification.UserId);
        Assert.Empty(Notifications(NotificationType.DocumentAdded));
    }

    [Fact]
    public async Task ErrorVerdict_FailsClosed_AsScanFailed()
    {
        var id = await PendingProjectDocumentAsync();

        Assert.True(await _h.ApplyAsync(id, ScanVerdict.Error, reason: "engine unavailable"));

        var document = _h.Load(id);
        Assert.Equal(ScanStatuses.ScanFailed, document.ScanStatus);
        Assert.Single(_h.StoredFiles());
        Assert.Empty(Notifications(NotificationType.DocumentAdded));
    }

    [Fact]
    public async Task VerdictOutcomes_WriteActivityRows()
    {
        var clean = await _h.UploadAsync(_emp, TestFiles.Request("a.pdf", TestFiles.Pdf(), "A"));
        var bad = await _h.UploadAsync(_emp, TestFiles.Request("b.pdf", TestFiles.Pdf(), "B"));
        var failed = await _h.UploadAsync(_emp, TestFiles.Request("c.pdf", TestFiles.Pdf(), "C"));

        await _h.ApplyAsync(clean, ScanVerdict.Clean);
        await _h.ApplyAsync(bad, ScanVerdict.Infected);
        await _h.ApplyAsync(failed, ScanVerdict.Error);

        using var context = _h.Db.NewContext();
        var actions = context.DocumentActivities.Where(a => a.Action != DocumentActions.Upload)
            .OrderBy(a => a.DocumentActivityId).Select(a => a.Action).ToList();
        Assert.Equal(new[] { DocumentActions.ScanCompleted, DocumentActions.ScanRejected, DocumentActions.ScanFailed }, actions);
    }

    [Fact]
    public async Task DuplicateVerdict_IsIgnored()
    {
        var id = await PendingProjectDocumentAsync();
        var attempt = _h.Load(id).ScanAttemptId;

        Assert.True(await _h.ApplyAsync(id, ScanVerdict.Clean, attempt));
        Assert.False(await _h.ApplyAsync(id, ScanVerdict.Clean, attempt));
        Assert.False(await _h.ApplyAsync(id, ScanVerdict.Infected, attempt)); // cannot flip a decided document

        Assert.Equal(ScanStatuses.Available, _h.Load(id).ScanStatus);
        Assert.Equal(2, Notifications(NotificationType.DocumentAdded).Count); // sent once
    }

    [Fact]
    public async Task StaleAttemptId_IsIgnored_IncludingAfterReplace()
    {
        var id = await PendingProjectDocumentAsync();
        var firstAttempt = _h.Load(id).ScanAttemptId;

        Assert.False(await _h.ApplyAsync(id, ScanVerdict.Clean, Guid.NewGuid()));

        var replace = await _h.Service.ReplaceFileAsync(id, TestFiles.File("new.pdf", TestFiles.Pdf()), _emp);
        Assert.True(replace.Success);
        Assert.NotEqual(firstAttempt, _h.Load(id).ScanAttemptId);

        Assert.False(await _h.ApplyAsync(id, ScanVerdict.Clean, firstAttempt)); // verdict for the replaced file
        Assert.Equal(ScanStatuses.Pending, _h.Load(id).ScanStatus);
    }

    [Fact]
    public async Task VerdictForDeletedOrUnknownDocument_IsIgnored()
    {
        var id = await PendingProjectDocumentAsync();
        var attempt = _h.Load(id).ScanAttemptId;

        Assert.True((await _h.Service.DeleteAsync(id, _emp)).Success);

        Assert.False(await _h.ApplyAsync(id, ScanVerdict.Clean, attempt));
        Assert.False(await _h.ApplyAsync(9999, ScanVerdict.Clean, Guid.NewGuid()));
    }

    [Fact]
    public async Task TimeoutSweep_FailsLongPendingDocuments_AndNeverMakesAnythingAvailable()
    {
        var stale = await _h.UploadAsync(_emp, TestFiles.Request("a.pdf", TestFiles.Pdf(), "Stale"));
        var fresh = await _h.UploadAsync(_emp, TestFiles.Request("b.pdf", TestFiles.Pdf(), "Fresh"));
        var available = await _h.UploadAvailableAsync(_emp, title: "Done");

        using (var context = _h.Db.NewContext())
        {
            context.Documents.Single(d => d.DocumentId == stale).ScanRequestedDate = DateTime.UtcNow.AddMinutes(-20);
            context.Documents.Single(d => d.DocumentId == available).ScanRequestedDate = DateTime.UtcNow.AddMinutes(-60);
            context.SaveChanges();
        }

        var count = await _h.Service.FailTimedOutScansAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(1, count);
        Assert.Equal(ScanStatuses.ScanFailed, _h.Load(stale).ScanStatus);
        Assert.Equal(ScanStatuses.Pending, _h.Load(fresh).ScanStatus);
        Assert.Equal(ScanStatuses.Available, _h.Load(available).ScanStatus);
        Assert.Equal(0, await _h.Service.FailTimedOutScansAsync(TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public async Task Retry_OnlyFromScanFailed_ByUploaderOrAdministrator()
    {
        var id = await PendingProjectDocumentAsync();

        var whilePending = await _h.Service.RetryScanAsync(id, _emp);
        Assert.Equal(DocumentError.InvalidState, whilePending.Error);

        await _h.ApplyAsync(id, ScanVerdict.Error);
        var oldAttempt = _h.Load(id).ScanAttemptId;
        _h.Queue.Requests.Clear();

        Assert.Equal(DocumentError.NotFound, (await _h.Service.RetryScanAsync(id, TestDb.OtherEngineer)).Error);
        Assert.Equal(DocumentError.NotFound, (await _h.Service.RetryScanAsync(id, TestDb.ProjectManager)).Error);

        Assert.True((await _h.Service.RetryScanAsync(id, _emp)).Success);

        var document = _h.Load(id);
        Assert.Equal(ScanStatuses.Pending, document.ScanStatus);
        Assert.NotEqual(oldAttempt, document.ScanAttemptId);
        var request = Assert.Single(_h.Queue.Requests);
        Assert.Equal(document.ScanAttemptId, request.ScanAttemptId);

        await _h.ApplyAsync(id, ScanVerdict.Error);
        Assert.True((await _h.Service.RetryScanAsync(id, TestDb.Admin)).Success);
    }

    [Fact]
    public async Task PendingRecovery_ReturnsOnlyPendingDocuments()
    {
        var pending = await PendingProjectDocumentAsync();
        var done = await _h.UploadAvailableAsync(_emp, title: "Done");
        var failed = await _h.UploadAsync(_emp, TestFiles.Request("f.pdf", TestFiles.Pdf(), "Failed"));
        await _h.ApplyAsync(failed, ScanVerdict.Error);

        var requests = await _h.Service.GetPendingScanRequestsAsync();

        Assert.Equal(pending, Assert.Single(requests).DocumentId);
        Assert.DoesNotContain(requests, r => r.DocumentId == done || r.DocumentId == failed);
    }

    [Fact]
    public async Task NonAvailableDocuments_AreHiddenFromOthers_ButListedForTheUploader()
    {
        var pending = await PendingProjectDocumentAsync();

        var otherSearch = await _h.Service.SearchAsync(null, new DocumentQuery(), TestDb.ProjectManager);
        Assert.DoesNotContain(otherSearch.Items, d => d.DocumentId == pending);

        var ownList = await _h.Service.GetMyDocumentsAsync(new DocumentQuery(), _emp);
        var own = Assert.Single(ownList.Items);
        Assert.Equal(ScanStatuses.Pending, own.ScanStatus);

        Assert.Null(await _h.Service.OpenForDownloadAsync(pending, _emp));
        Assert.Null(await _h.Service.OpenForDownloadAsync(pending, TestDb.Admin));
    }

    // ---- Worker end to end -------------------------------------------------------------------------

    private ServiceProvider BuildProvider(IMalwareScanner scanner, int delaySeconds = 0)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new ApplicationDbContext(_h.Db.Options));
        services.AddSingleton<IFileStorageService>(_h.Storage);
        services.AddSingleton<InProcessScanQueue>();
        services.AddSingleton<IScanQueue>(sp => sp.GetRequiredService<InProcessScanQueue>());
        services.AddSingleton(scanner);
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<DocumentAccess>();
        services.AddSingleton(Options.Create(new DocumentStorageOptions()));
        services.AddSingleton(Options.Create(new MalwareScanOptions { SimulatedDelaySeconds = delaySeconds }));
        services.AddScoped<DocumentService>();
        services.AddScoped<IScanResultHandler>(sp => sp.GetRequiredService<DocumentService>());
        services.AddScoped<IScanMaintenance>(sp => sp.GetRequiredService<DocumentService>());
        services.AddSingleton<ScanWorker>();
        return services.BuildServiceProvider();
    }

    private static string EicarText() =>
        "X5O!P%@AP[4\\PZX54(P^)7CC)7}$" + "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!" + "$H+H*";

    [Fact]
    public async Task Worker_CleanFile_BecomesAvailable()
    {
        using var provider = BuildProvider(new NoOpMalwareScanner());
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DocumentService>();

        var upload = await service.UploadAsync(TestFiles.Request("a.pdf", TestFiles.Pdf(), "Clean"), _emp);
        var request = Assert.Single(await service.GetPendingScanRequestsAsync());

        await provider.GetRequiredService<ScanWorker>().ProcessAsync(request, CancellationToken.None);

        Assert.Equal(ScanStatuses.Available, _h.Load(upload.DocumentId!.Value).ScanStatus);
    }

    [Fact]
    public async Task Worker_FlaggedFile_IsRejectedAndRemoved()
    {
        // A harmless marker instead of real EICAR text: host antivirus blocks EICAR files on disk
        using var provider = BuildProvider(new MarkerScanner());
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DocumentService>();

        var upload = await service.UploadAsync(TestFiles.Request("virus.txt", TestFiles.Text("data " + MarkerScanner.Marker), "Bad"), _emp);
        var request = Assert.Single(await service.GetPendingScanRequestsAsync());

        await provider.GetRequiredService<ScanWorker>().ProcessAsync(request, CancellationToken.None);

        Assert.Equal(ScanStatuses.Rejected, _h.Load(upload.DocumentId!.Value).ScanStatus);
        Assert.Empty(_h.StoredFiles());
        Assert.Single(Notifications(NotificationType.DocumentRejected));
    }

    [Fact]
    public async Task Worker_MissingFile_FailsClosed()
    {
        using var provider = BuildProvider(new NoOpMalwareScanner());
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DocumentService>();

        var upload = await service.UploadAsync(TestFiles.Request("a.pdf", TestFiles.Pdf(), "Gone"), _emp);
        var request = Assert.Single(await service.GetPendingScanRequestsAsync());
        File.Delete(Assert.Single(_h.StoredFiles()));

        await provider.GetRequiredService<ScanWorker>().ProcessAsync(request, CancellationToken.None);

        Assert.Equal(ScanStatuses.ScanFailed, _h.Load(upload.DocumentId!.Value).ScanStatus);
    }

    [Fact]
    public async Task Worker_ScannerException_FailsClosed()
    {
        using var provider = BuildProvider(new ThrowingScanner());
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DocumentService>();

        var upload = await service.UploadAsync(TestFiles.Request("a.pdf", TestFiles.Pdf(), "Boom"), _emp);
        var request = Assert.Single(await service.GetPendingScanRequestsAsync());

        await provider.GetRequiredService<ScanWorker>().ProcessAsync(request, CancellationToken.None);

        Assert.Equal(ScanStatuses.ScanFailed, _h.Load(upload.DocumentId!.Value).ScanStatus);
    }

    [Fact]
    public async Task EicarScanner_FlagsOnlyTheTestSignature()
    {
        var scanner = new EicarScanner();
        Assert.False((await scanner.ScanAsync(TestFiles.Text("prefix " + EicarText() + " suffix"), "x.txt")).IsClean);
        Assert.True((await scanner.ScanAsync(TestFiles.Text("harmless content"), "x.txt")).IsClean);
        Assert.True((await new NoOpMalwareScanner().ScanAsync(TestFiles.Text(EicarText()), "x.txt")).IsClean);
    }

    private sealed class MarkerScanner : IMalwareScanner
    {
        public const string Marker = "MALWARE-TEST-MARKER";

        public async Task<ScanResult> ScanAsync(Stream content, string fileName, CancellationToken ct = default)
        {
            using var reader = new StreamReader(content);
            var text = await reader.ReadToEndAsync(ct);
            return text.Contains(Marker) ? new ScanResult(false, "Marker detected") : new ScanResult(true);
        }
    }

    private sealed class ThrowingScanner : IMalwareScanner
    {
        public Task<ScanResult> ScanAsync(Stream content, string fileName, CancellationToken ct = default)
            => throw new InvalidOperationException("engine crashed");
    }
}
