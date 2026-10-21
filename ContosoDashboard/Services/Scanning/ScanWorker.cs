using Microsoft.Extensions.Options;

namespace ContosoDashboard.Services;

// In-process scan worker (training mode). Reads scan requests, scans the stored file, and applies the verdict.
public class ScanWorker : BackgroundService
{
    private readonly InProcessScanQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileStorageService _storage;
    private readonly IMalwareScanner _scanner;
    private readonly MalwareScanOptions _options;
    private readonly ILogger<ScanWorker> _logger;

    public ScanWorker(
        InProcessScanQueue queue,
        IServiceScopeFactory scopeFactory,
        IFileStorageService storage,
        IMalwareScanner scanner,
        IOptions<MalwareScanOptions> options,
        ILogger<ScanWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _storage = storage;
        _scanner = scanner;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverPendingScansAsync(stoppingToken);

        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(request, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    // The in-memory queue is lost on restart, so every still-pending document is queued again
    private async Task RecoverPendingScansAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var maintenance = scope.ServiceProvider.GetRequiredService<IScanMaintenance>();
            foreach (var request in await maintenance.GetPendingScanRequestsAsync(ct))
            {
                await _queue.EnqueueAsync(request, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not recover pending scans at startup");
        }
    }

    // Public so tests can drive the pipeline deterministically
    public async Task ProcessAsync(ScanRequest request, CancellationToken ct)
    {
        if (_options.SimulatedDelaySeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.SimulatedDelaySeconds), ct);
        }

        ScanVerdictMessage verdict;
        try
        {
            await using var content = await _storage.DownloadAsync(request.StorageKey, ct);
            var result = await _scanner.ScanAsync(content, request.StorageKey, ct);

            verdict = new ScanVerdictMessage(
                request.DocumentId, request.ScanAttemptId,
                result.IsClean ? ScanVerdict.Clean : ScanVerdict.Infected,
                result.Reason, _scanner.GetType().Name, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Scan failed for document {DocumentId}", request.DocumentId);
            verdict = new ScanVerdictMessage(
                request.DocumentId, request.ScanAttemptId, ScanVerdict.Error,
                "The security scan could not be completed.", _scanner.GetType().Name, DateTime.UtcNow);
        }

        using var scope = _scopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IScanResultHandler>();
        await handler.ApplyVerdictAsync(verdict, ct);
    }
}
