namespace ContosoDashboard.Models;

public static class DocumentCategories
{
    public const string ProjectDocuments = "Project Documents";
    public const string TeamResources = "Team Resources";
    public const string PersonalFiles = "Personal Files";
    public const string Reports = "Reports";
    public const string Presentations = "Presentations";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All = new[]
    {
        ProjectDocuments, TeamResources, PersonalFiles, Reports, Presentations, Other
    };

    public static bool IsValid(string? category) => category != null && All.Contains(category);
}

public static class ScanStatuses
{
    public const string Pending = "Pending";
    public const string Available = "Available";
    public const string Rejected = "Rejected";
    public const string ScanFailed = "ScanFailed";
}

public static class DocumentActions
{
    public const string Upload = "Upload";
    public const string Download = "Download";
    public const string Preview = "Preview";
    public const string Edit = "Edit";
    public const string Replace = "Replace";
    public const string Delete = "Delete";
    public const string Share = "Share";
    public const string ScanCompleted = "ScanCompleted";
    public const string ScanRejected = "ScanRejected";
    public const string ScanFailed = "ScanFailed";
}
