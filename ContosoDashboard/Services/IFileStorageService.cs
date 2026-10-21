namespace ContosoDashboard.Services;

public class DocumentStorageOptions
{
    public const string SectionName = "DocumentStorage";

    public string Provider { get; set; } = "Local";
    public string RootPath { get; set; } = "AppData/uploads";
    public long MaxFileSizeBytes { get; set; } = 26_214_400;
}

// Abstraction over where file bytes live. Keys are relative and provider-neutral:
// {userId}/{projectId or "personal"}/{guid}.{ext}
public interface IFileStorageService
{
    Task<string> UploadAsync(Stream fileStream, string storageKey, string contentType, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string storageKey, CancellationToken ct = default);
    Task DeleteAsync(string storageKey, CancellationToken ct = default);
    Task<string> GetUrlAsync(string storageKey, TimeSpan expiration, CancellationToken ct = default);
}
