using ContosoDashboard.Models;

namespace ContosoDashboard.Services;

public static class DocumentFileValidator
{
    public const long DefaultMaxFileSizeBytes = 26_214_400;
    private const int MaxTags = 10;
    private const int MaxTagLength = 30;

    // Allowed extensions and the server-side MIME type derived from each (client content type is not trusted)
    public static readonly IReadOnlyDictionary<string, string> AllowedExtensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".ppt"] = "application/vnd.ms-powerpoint",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".txt"] = "text/plain",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png"
        };

    private static readonly HashSet<string> PreviewableTypes =
        new(StringComparer.OrdinalIgnoreCase) { "application/pdf", "image/jpeg", "image/png" };

    public static string SupportedTypesText => "PDF, Word, Excel, PowerPoint, text, JPEG, and PNG";

    public static bool IsPreviewable(string? contentType)
        => contentType != null && PreviewableTypes.Contains(contentType);

    public static FileValidationResult Validate(string fileName, Stream content, long maxFileSizeBytes = DefaultMaxFileSizeBytes)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);

        if (string.IsNullOrEmpty(extension) || !AllowedExtensions.TryGetValue(extension, out var mimeType))
        {
            return FileValidationResult.Fail(DocumentError.UnsupportedType,
                $"This file type is not supported. Supported types: {SupportedTypesText}.");
        }

        var length = content.Length;
        if (length == 0)
        {
            return FileValidationResult.Fail(DocumentError.EmptyFile, "The file is empty.");
        }

        if (length > maxFileSizeBytes)
        {
            return FileValidationResult.Fail(DocumentError.FileTooLarge,
                $"The file is too large. The maximum size is {maxFileSizeBytes / (1024 * 1024)} MB.");
        }

        if (!SignatureMatches(extension.ToLowerInvariant(), content))
        {
            return FileValidationResult.Fail(DocumentError.ContentMismatch,
                "The file content does not match its file type.");
        }

        return FileValidationResult.Ok(extension.ToLowerInvariant(), mimeType);
    }

    public static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        var cleaned = new string(name.Where(c => !char.IsControl(c) && c != '"' && c != ';').ToArray()).Trim();

        if (cleaned.Length == 0)
        {
            return "document";
        }

        if (cleaned.Length > 255)
        {
            var extension = Path.GetExtension(cleaned);
            cleaned = cleaned[..(255 - extension.Length)] + extension;
        }

        return cleaned;
    }

    // Returns null when valid; otherwise a user-safe message. Normalized tags are lowercase and comma-separated.
    public static string? NormalizeTags(string? tags, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(tags))
        {
            return null;
        }

        var list = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();

        if (list.Count == 0)
        {
            return null;
        }

        if (list.Count > MaxTags)
        {
            return $"A document can have at most {MaxTags} tags.";
        }

        if (list.Any(t => t.Length > MaxTagLength))
        {
            return $"Each tag can have at most {MaxTagLength} characters.";
        }

        normalized = string.Join(",", list);
        return null;
    }

    public static string? ValidateMetadata(string? title, string? description, string? category)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "A title is required.";
        }

        if (title.Trim().Length > 255)
        {
            return "The title can have at most 255 characters.";
        }

        if (description != null && description.Length > 2000)
        {
            return "The description can have at most 2000 characters.";
        }

        if (!DocumentCategories.IsValid(category))
        {
            return "Please choose a valid category.";
        }

        return null;
    }

    private static bool SignatureMatches(string extension, Stream content)
    {
        var position = content.CanSeek ? content.Position : 0;
        try
        {
            if (content.CanSeek)
            {
                content.Position = 0;
            }

            var header = new byte[8192];
            var read = 0;
            int n;
            while (read < header.Length && (n = content.Read(header, read, header.Length - read)) > 0)
            {
                read += n;
            }

            var span = header.AsSpan(0, read);

            return extension switch
            {
                ".pdf" => span[..Math.Min(read, 1024)].IndexOf("%PDF"u8) >= 0,
                ".png" => span.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
                ".jpg" or ".jpeg" => span.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
                ".docx" or ".xlsx" or ".pptx" => span.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }),
                ".doc" or ".xls" or ".ppt" => span.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }),
                ".txt" => span.IndexOf((byte)0) < 0,
                _ => false
            };
        }
        finally
        {
            if (content.CanSeek)
            {
                content.Position = position;
            }
        }
    }
}

public record FileValidationResult(bool IsValid, DocumentError Error, string? Message, string Extension, string ContentType)
{
    public static FileValidationResult Ok(string extension, string contentType)
        => new(true, DocumentError.None, null, extension, contentType);

    public static FileValidationResult Fail(DocumentError error, string message)
        => new(false, error, message, string.Empty, string.Empty);
}
