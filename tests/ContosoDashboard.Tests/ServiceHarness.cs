using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Services;

namespace ContosoDashboard.Tests;

public class RecordingScanQueue : IScanQueue
{
    public List<ScanRequest> Requests { get; } = new();
    public bool Fail { get; set; }

    public Task EnqueueAsync(ScanRequest request, CancellationToken ct = default)
    {
        if (Fail)
        {
            throw new InvalidOperationException("queue unavailable");
        }

        Requests.Add(request);
        return Task.CompletedTask;
    }
}

// Lets a test force the next save to fail (e.g., to verify compensation after the file was written)
public class FailingDbContext : ApplicationDbContext
{
    public bool FailOnSave { get; set; }

    public FailingDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (FailOnSave)
        {
            throw new DbUpdateException("simulated database failure");
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}

public static class TestFiles
{
    public static MemoryStream Pdf(int extraBytes = 200)
    {
        var bytes = new byte[8 + extraBytes];
        "%PDF-1.4"u8.CopyTo(bytes);
        return new MemoryStream(bytes);
    }

    public static MemoryStream Png(int extraBytes = 200)
    {
        var bytes = new byte[8 + extraBytes];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return new MemoryStream(bytes);
    }

    public static MemoryStream Text(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));

    public static UploadFile File(string name, MemoryStream stream)
        => new() { FileName = name, ContentType = "application/octet-stream", Stream = stream };

    public static UploadRequest Request(
        string name,
        MemoryStream stream,
        string title = "Quarterly report",
        string category = DocumentCategories.TeamResources,
        int? projectId = null,
        string? tags = null,
        string? description = null)
        => new()
        {
            Title = title,
            Category = category,
            ProjectId = projectId,
            Tags = tags,
            Description = description,
            File = File(name, stream)
        };
}

// A DocumentService wired against a fresh in-memory database, recording queue, and temp storage
public sealed class ServiceHarness : IDisposable
{
    public TestDb Db { get; }
    public FailingDbContext Context { get; }
    public RecordingScanQueue Queue { get; } = new();
    public LocalFileStorageService Storage { get; }
    public DocumentService Service { get; }

    public ServiceHarness(TestDb? db = null, long maxFileSize = 26_214_400)
    {
        Db = db ?? new TestDb();
        Context = new FailingDbContext(Db.Options);
        Storage = Db.NewStorage();

        Service = new DocumentService(
            Context,
            Storage,
            Queue,
            new NotificationService(Context),
            new DocumentAccess(Context),
            Options.Create(new DocumentStorageOptions { MaxFileSizeBytes = maxFileSize }),
            NullLogger<DocumentService>.Instance);
    }

    public IEnumerable<string> StoredFiles() => Directory.Exists(Db.StorageRoot)
        ? Directory.GetFiles(Db.StorageRoot, "*", SearchOption.AllDirectories)
        : Array.Empty<string>();

    public async Task<int> UploadAsync(UserContext user, UploadRequest request)
    {
        var result = await Service.UploadAsync(request, user);
        Assert.True(result.Success, result.Message);
        return result.DocumentId!.Value;
    }

    public Task<bool> ApplyAsync(int documentId, ScanVerdict verdict, Guid? attemptId = null, string? reason = null)
    {
        var attempt = attemptId ?? Context.Documents.AsNoTracking().Single(d => d.DocumentId == documentId).ScanAttemptId;
        return Service.ApplyVerdictAsync(new ScanVerdictMessage(documentId, attempt, verdict, reason, "test", DateTime.UtcNow));
    }

    // Uploads a file and applies a clean verdict so the document is Available with a stored file
    public async Task<int> UploadAvailableAsync(
        UserContext user,
        string fileName = "report.pdf",
        MemoryStream? content = null,
        int? projectId = null,
        string category = DocumentCategories.TeamResources,
        string title = "Available document")
    {
        var id = await UploadAsync(user, TestFiles.Request(fileName, content ?? TestFiles.Pdf(), title, category, projectId));
        Assert.True(await ApplyAsync(id, ScanVerdict.Clean));
        return id;
    }

    public Document Load(int documentId)
    {
        using var context = Db.NewContext();
        return context.Documents.AsNoTracking().Single(d => d.DocumentId == documentId);
    }

    public void Dispose()
    {
        Context.Dispose();
        Db.Dispose();
    }
}
