namespace ContosoDashboard.Services;

public enum DocumentError
{
    None,
    NotFound,
    Forbidden,
    FileTooLarge,
    EmptyFile,
    UnsupportedType,
    ContentMismatch,
    InvalidMetadata,
    NotProjectMember,
    NotAvailable,
    InvalidState,
    StorageFailure
}

public class OperationResult
{
    public bool Success { get; init; }
    public DocumentError Error { get; init; }
    public string Message { get; init; } = string.Empty;

    public static OperationResult Ok(string message = "") => new() { Success = true, Message = message };
    public static OperationResult Fail(DocumentError error, string message) => new() { Error = error, Message = message };
}

public class UploadResult : OperationResult
{
    public int? DocumentId { get; init; }

    public static UploadResult Created(int documentId, string message) =>
        new() { Success = true, DocumentId = documentId, Message = message };

    public new static UploadResult Fail(DocumentError error, string message) =>
        new() { Error = error, Message = message };
}

public class UploadFile
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size => Stream.Length;

    // In-memory copy of the browser file so the upload does not depend on the browser stream's lifetime
    public Stream Stream { get; set; } = Stream.Null;
}

public class UploadRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = string.Empty;
    public int? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? Tags { get; set; }
    public UploadFile File { get; set; } = new();
}

public record DocumentMetadata(string Title, string? Description, string Category, string? Tags);

public record ShareTarget(int? UserId, string? Department);

public class DocumentQuery
{
    public string? Category { get; set; }
    public int? ProjectId { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string SortBy { get; set; } = SortUploadDate;
    public bool Descending { get; set; } = true;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;

    public const string SortTitle = "Title";
    public const string SortUploadDate = "UploadDate";
    public const string SortCategory = "Category";
    public const string SortFileSize = "FileSize";
}

public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public class DocumentSummary
{
    public int DocumentId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Tags { get; set; }
    public string FileType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime UploadDate { get; set; }
    public int UploaderUserId { get; set; }
    public string UploaderName { get; set; } = string.Empty;
    public int? ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public int? TaskId { get; set; }
    public string ScanStatus { get; set; } = string.Empty;
    public string? ScanDetail { get; set; }
}

public class DocumentDetails : DocumentSummary
{
    public string OriginalFileName { get; set; } = string.Empty;
    public DateTime UpdatedDate { get; set; }
}

public class FileContent : IAsyncDisposable
{
    public Stream Content { get; init; } = Stream.Null;
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
