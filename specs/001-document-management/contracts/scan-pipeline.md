# Contract: Asynchronous Malware Scan Pipeline

Files are scanned by a background job after upload. The document stays **quarantined** until a clean verdict is applied. The pipeline has one contract (queue messages + completion rules) and two implementations selected by configuration.

| Mode | Where it runs | Queue | Scanner | Used in |
|------|---------------|-------|---------|---------|
| `InProcess` (default) | Hosted `BackgroundService` inside the web app | In-memory `Channel<ScanRequest>` | `IMalwareScanner` (no-op in training) | Training / offline / tests |
| `AzureQueue` | Azure Functions app, separate from the web app | Azure Queue Storage | Engine of choice inside the function (e.g., Microsoft Defender for Storage result, ClamAV, or a third-party API) | Cloud deployment |

The training build contains no Azure SDK references. The Azure implementation lives in separate projects delivered in the cloud-migration phase (see plan, Delivery Phases); the message contracts below are fixed now so both modes behave identically.

## State machine (`Document.ScanStatus`)

```text
Pending ──clean──▶ Available
Pending ──infected──▶ Rejected          (file deleted, uploader notified)
Pending ──error/timeout──▶ ScanFailed   (fail closed; retry allowed)
ScanFailed ──retry──▶ Pending           (new ScanAttemptId)
Available ──replace file──▶ Pending     (new ScanAttemptId)
```

Rules:
- Only `Pending` accepts a verdict. A verdict is applied only if `documentId` exists and `scanAttemptId` equals the document's current `ScanAttemptId`; otherwise it is ignored (duplicate, stale after replace, or document deleted). Applying a verdict is idempotent.
- Download, preview, share, attach, search results, counts, recent-documents, and notifications ignore documents that are not `Available`. The uploader (and Administrators) can see the row with its status in My Documents only.
- Nothing is ever served from a non-`Available` document, regardless of how the request is made.

## Queue contracts

Both queues carry JSON (UTF-8) and **no user content, names, or secrets**; the file itself is read from storage by key.

### `document-scan-requests` (web app → scanner)

```json
{
  "schemaVersion": 1,
  "documentId": 42,
  "scanAttemptId": "0b7c2d0e-3f58-4a8f-9c1e-5d8a7e6b1f10",
  "storageKey": "3/12/6f1d5c0a-0f3e-4e47-9d3a-0c9f4a2b7a55.pdf",
  "sizeBytes": 1048576,
  "enqueuedUtc": "2026-10-20T19:44:18Z"
}
```

### `document-scan-results` (scanner → web app)

```json
{
  "schemaVersion": 1,
  "documentId": 42,
  "scanAttemptId": "0b7c2d0e-3f58-4a8f-9c1e-5d8a7e6b1f10",
  "verdict": "Clean",
  "reason": null,
  "scanner": "defender-for-storage",
  "scannedUtc": "2026-10-20T19:44:41Z"
}
```

`verdict` is `Clean`, `Infected`, or `Error`. `reason` is a short, user-safe text (no scanner internals) when not clean.

Both modes use the same C# types:

```csharp
public record ScanRequest(int DocumentId, Guid ScanAttemptId, string StorageKey, long SizeBytes, DateTime EnqueuedUtc);
public record ScanVerdictMessage(int DocumentId, Guid ScanAttemptId, ScanVerdict Verdict, string? Reason, string Scanner, DateTime ScannedUtc);
public enum ScanVerdict { Clean, Infected, Error }

public interface IScanQueue
{
    Task EnqueueAsync(ScanRequest request, CancellationToken ct = default);
}

public interface IScanResultHandler       // implemented by DocumentService
{
    Task<bool> ApplyVerdictAsync(ScanVerdictMessage verdict, CancellationToken ct = default); // false = ignored
}
```

## Mode 1: InProcess (training)

- `InProcessScanQueue : IScanQueue` writes to a bounded `Channel<ScanRequest>`.
- `ScanWorker : BackgroundService` reads requests, opens the file through `IFileStorageService.DownloadAsync`, calls `IMalwareScanner.ScanAsync`, and calls `IScanResultHandler.ApplyVerdictAsync` in a fresh DI scope. Scanner exceptions produce an `Error` verdict.
- On startup, a recovery step re-enqueues every `Pending` document (the channel is lost on restart).

## Mode 2: AzureQueue (cloud) — Azure Functions with Queue Storage triggers

### Components

1. **Web app** (`AzureStorageScanQueue : IScanQueue`, in the cloud project): sends `ScanRequest` JSON to `document-scan-requests`; a hosted `ScanResultListener` polls `document-scan-results`, calls `ApplyVerdictAsync`, and deletes the message only after a successful apply (ignored verdicts also count as handled).
2. **Azure Function** (`DocumentScanFunction`, .NET isolated worker model on a Functions-supported .NET version):

```csharp
[Function("ScanDocument")]
[QueueOutput("document-scan-results", Connection = "ScanStorage")]
public async Task<ScanVerdictMessage> Run(
    [QueueTrigger("document-scan-requests", Connection = "ScanStorage")] ScanRequest request,
    FunctionContext context)
{
    // 1. Stream the blob at request.StorageKey from the documents container (never buffer more than needed)
    // 2. Verify size matches request.SizeBytes
    // 3. Run the scanning engine; map result to Clean / Infected
    // 4. Return a ScanVerdictMessage (written to the results queue by the output binding)
    // On transient failure: throw, so the host retries; the final failure goes to the poison queue
}
```

3. **Poison handling**: the Functions queue trigger retries up to `maxDequeueCount` (default 5) and then moves the message to `document-scan-requests-poison`. The web app does not read the poison queue; instead its **timeout sweeper** (below) fails the document closed. Operations should alert on poison-queue depth.

### Security and operations

- **Identity**: managed identities and RBAC, no connection-string secrets. Function identity: *Storage Queue Data Message Processor* on the request queue, *Storage Queue Data Message Sender* on the results queue, *Storage Blob Data Reader* on the documents container. Web app identity: *Queue Data Message Sender* (requests), *Queue Data Message Processor* (results), and blob read/write/delete via `AzureBlobStorageService`. Local development uses Azurite.
- **Trust boundary**: only the Function identity can write to the results queue; the web app still validates `documentId` + `scanAttemptId` against the document row before applying, so a malformed or stale message cannot change state.
- **Encoding**: configure queue client message encoding to match the Functions trigger (Base64 vs. plain) on both sides; covered by an integration check in the cloud phase.
- **Scale/size**: messages are metadata only (< 1 KB); files up to 25 MB are streamed; Function timeout and memory plan sized for 25 MB scans.
- **Observability**: `scanAttemptId` and `documentId` are logged on every hop; the web app writes `ScanCompleted` / `ScanRejected` / `ScanFailed` activity rows.

## Timeout sweeper (both modes)

`ScanTimeoutSweeper : BackgroundService` runs every minute and marks documents that have been `Pending` longer than `MalwareScan:PendingTimeoutMinutes` (default 15) as `ScanFailed` (fail closed). It never marks anything `Available`. Retry (`RetryScanAsync`, uploader or Administrator) assigns a new `ScanAttemptId`, sets `Pending`, and re-enqueues.

## Configuration

```json
"MalwareScan": {
  "Mode": "InProcess",
  "Scanner": "NoOp",
  "SimulatedDelaySeconds": 0,
  "PendingTimeoutMinutes": 15,
  "RequestQueueName": "document-scan-requests",
  "ResultQueueName": "document-scan-results",
  "QueueServiceUri": ""
}
```

`Scanner` (in-process mode) is `NoOp` (training default) or `Eicar` (development/test only: flags content containing the EICAR test string). `SimulatedDelaySeconds` delays the in-process worker so quarantine behavior can be observed; both options are rejected outside the Development environment. `QueueServiceUri` and the Azure mode are used only in the cloud deployment (managed identity; no keys in configuration).
