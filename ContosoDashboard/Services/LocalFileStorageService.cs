namespace ContosoDashboard.Services;

public class LocalFileStorageService : IFileStorageService
{
    private readonly string _rootDirectory;
    private readonly ILogger<LocalFileStorageService>? _logger;

    public LocalFileStorageService(string rootDirectory, ILogger<LocalFileStorageService>? logger = null)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _logger = logger;
    }

    // Resolves the configured root against the content root and refuses locations under wwwroot
    public static string ResolveRoot(DocumentStorageOptions options, string contentRootPath)
    {
        var root = Path.IsPathRooted(options.RootPath)
            ? Path.GetFullPath(options.RootPath)
            : Path.GetFullPath(Path.Combine(contentRootPath, options.RootPath));

        var webRoot = Path.GetFullPath(Path.Combine(contentRootPath, "wwwroot"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (root.Equals(webRoot, StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The document storage root must not be inside wwwroot.");
        }

        return root;
    }

    public async Task<string> UploadAsync(Stream fileStream, string storageKey, string contentType, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        if (File.Exists(fullPath))
        {
            throw new IOException("A file already exists for this storage key.");
        }

        // Write to a temporary file first so a failure never leaves a partial file at the final key
        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, useAsync: true))
            {
                await fileStream.CopyToAsync(target, ct);
            }

            File.Move(tempPath, fullPath, overwrite: false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return storageKey;
    }

    public Task<Stream> DownloadAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(storageKey);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The requested file was not found.");
        }

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct = default)
    {
        var fullPath = ResolvePath(storageKey);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    public Task<string> GetUrlAsync(string storageKey, TimeSpan expiration, CancellationToken ct = default)
    {
        throw new NotSupportedException("Local storage does not expose direct URLs; files are served through authorized endpoints.");
    }

    private string ResolvePath(string storageKey)
    {
        ValidateKey(storageKey);

        var fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, NormalizeKey(storageKey)));
        if (!fullPath.StartsWith(_rootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Storage key resolves outside the storage root.", nameof(storageKey));
        }

        return fullPath;
    }

    private static string NormalizeKey(string storageKey) => storageKey.Replace('/', Path.DirectorySeparatorChar);

    private static void ValidateKey(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) ||
            Path.IsPathRooted(storageKey) ||
            storageKey.Contains('\\') ||
            storageKey.Contains(':') ||
            storageKey.Contains('\0'))
        {
            throw new ArgumentException("Invalid storage key.", nameof(storageKey));
        }

        foreach (var segment in storageKey.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new ArgumentException("Invalid storage key.", nameof(storageKey));
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not delete temporary upload file {Path}", path);
        }
    }
}
