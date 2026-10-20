# Contract: File Storage and Malware Scanning

## IFileStorageService

Abstraction over where file bytes live. The application depends only on this interface; implementations are selected by configuration and DI.

```csharp
public interface IFileStorageService
{
    Task<string> UploadAsync(Stream fileStream, string storageKey, string contentType, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string storageKey, CancellationToken ct = default);
    Task DeleteAsync(string storageKey, CancellationToken ct = default);
    Task<string> GetUrlAsync(string storageKey, TimeSpan expiration, CancellationToken ct = default);
}
```

| Method | Behavior | Failure behavior |
|--------|----------|------------------|
| `UploadAsync` | Writes the stream at `storageKey` (caller-generated, relative); returns the stored key. Creates intermediate directories. Never overwrites an existing key. | Throws `IOException` on write failure; leaves no partial file. Throws `ArgumentException` for an invalid key. |
| `DownloadAsync` | Returns a readable, seekable-optional stream for the key. Caller disposes. | Throws `FileNotFoundException` if missing. |
| `DeleteAsync` | Removes the file; idempotent if already absent. | Throws `IOException` on other failures. |
| `GetUrlAsync` | Returns a time-limited direct URL if the provider supports it. | `LocalFileStorageService` throws `NotSupportedException`; the application never calls it for local storage and always streams through authorized endpoints. |

**Storage key rules** (enforced by the caller and re-validated by the provider):
- Format `{userId}/{projectId|"personal"}/{guid}.{ext}`, using `/` separators, no `..`, no rooted paths, no backslashes; extension from the whitelist.
- The local provider resolves `Path.Combine(root, key)` and rejects any result outside `root`.

**Configuration** (`appsettings.json`):

```json
"DocumentStorage": {
  "Provider": "Local",
  "RootPath": "AppData/uploads",
  "MaxFileSizeBytes": 26214400
}
```

`Provider` selects the registered implementation (`Local` now; `AzureBlob` later). The root path is resolved against the content root and must not be inside `wwwroot`.

## IMalwareScanner

```csharp
public interface IMalwareScanner
{
    Task<ScanResult> ScanAsync(Stream content, string fileName, CancellationToken ct = default);
}

public record ScanResult(bool IsClean, string? Reason);
```

- The scanner receives a rewound stream and must leave it reusable (or the caller rewinds).
- `NoOpMalwareScanner` (training) always returns `IsClean = true`.
- If `IsClean` is false, the upload is rejected with a user-safe message and a log entry; no file or record is created.
- A scanner exception fails the upload (fail closed) with a generic error.
