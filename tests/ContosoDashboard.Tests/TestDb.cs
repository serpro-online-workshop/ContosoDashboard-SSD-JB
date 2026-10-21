using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ContosoDashboard.Data;
using ContosoDashboard.Models;
using ContosoDashboard.Services;

namespace ContosoDashboard.Tests;

// In-memory SQLite database (keeps relational behavior) plus a temporary storage root
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;

    // Seeded by the model: 1 Admin (IT), 2 ProjectManager (Engineering, manages project 1),
    // 3 TeamLead (Engineering, member of project 1), 4 Employee (Engineering, member of project 1)
    public static readonly UserContext Admin = new(1, UserRole.Administrator, "IT");
    public static readonly UserContext ProjectManager = new(2, UserRole.ProjectManager, "Engineering");
    public static readonly UserContext TeamLead = new(3, UserRole.TeamLead, "Engineering");
    public static readonly UserContext Employee = new(4, UserRole.Employee, "Engineering");

    // Added by this helper: not a member of project 1
    public static readonly UserContext OtherEngineer = new(5, UserRole.Employee, "Engineering");
    public static readonly UserContext SalesEmployee = new(6, UserRole.Employee, "Sales");
    public static readonly UserContext SalesTeamLead = new(7, UserRole.TeamLead, "Sales");

    public static IReadOnlyList<UserContext> AllUsers { get; } =
        new[] { Admin, ProjectManager, TeamLead, Employee, OtherEngineer, SalesEmployee, SalesTeamLead };

    public DbContextOptions<ApplicationDbContext> Options { get; }
    public string StorageRoot { get; }

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        using (var context = NewContext())
        {
            context.Database.EnsureCreated();
            context.Users.AddRange(
                new User { UserId = 5, Email = "other.engineer@contoso.com", DisplayName = "Other Engineer", Department = "Engineering", Role = UserRole.Employee },
                new User { UserId = 6, Email = "sales.employee@contoso.com", DisplayName = "Sales Employee", Department = "Sales", Role = UserRole.Employee },
                new User { UserId = 7, Email = "sales.lead@contoso.com", DisplayName = "Sales Lead", Department = "Sales", Role = UserRole.TeamLead });
            context.SaveChanges();
        }

        StorageRoot = Path.Combine(Path.GetTempPath(), "contoso-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StorageRoot);
    }

    public ApplicationDbContext NewContext() => new(Options);

    public LocalFileStorageService NewStorage() => new(StorageRoot);

    public int AddDocument(
        UserContext uploader,
        string category = DocumentCategories.TeamResources,
        int? projectId = null,
        string scanStatus = ScanStatuses.Available,
        string title = "Test document",
        string extension = ".pdf",
        int? taskId = null,
        string? description = null,
        string? tags = null,
        long fileSize = 1024,
        DateTime? uploadDate = null,
        string fileType = "application/pdf")
    {
        using var context = NewContext();
        var uploaded = uploadDate ?? DateTime.UtcNow;
        var document = new Document
        {
            Title = title,
            Description = description,
            Tags = tags,
            Category = category,
            OriginalFileName = "file" + extension,
            FileType = fileType,
            FileSize = fileSize,
            FilePath = $"{uploader.UserId}/{(projectId?.ToString() ?? "personal")}/{Guid.NewGuid()}{extension}",
            UploaderUserId = uploader.UserId,
            ProjectId = projectId,
            TaskId = taskId,
            ScanStatus = scanStatus,
            ScanAttemptId = Guid.NewGuid(),
            UploadDate = uploaded,
            UpdatedDate = uploaded,
            ScanRequestedDate = uploaded
        };
        context.Documents.Add(document);
        context.SaveChanges();
        return document.DocumentId;
    }

    public void ShareWithUser(int documentId, UserContext recipient, UserContext sharedBy)
    {
        using var context = NewContext();
        context.DocumentShares.Add(new DocumentShare
        {
            DocumentId = documentId,
            SharedWithUserId = recipient.UserId,
            SharedByUserId = sharedBy.UserId
        });
        context.SaveChanges();
    }

    public void ShareWithDepartment(int documentId, string department, UserContext sharedBy)
    {
        using var context = NewContext();
        context.DocumentShares.Add(new DocumentShare
        {
            DocumentId = documentId,
            SharedWithDepartment = department,
            SharedByUserId = sharedBy.UserId
        });
        context.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        try
        {
            if (Directory.Exists(StorageRoot))
            {
                Directory.Delete(StorageRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup of the temp folder
        }
    }
}
