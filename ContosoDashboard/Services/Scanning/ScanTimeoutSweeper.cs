using Microsoft.Extensions.Options;

namespace ContosoDashboard.Services;

// Fails closed: documents stuck in Pending for too long become ScanFailed. It never makes anything Available.
public class ScanTimeoutSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MalwareScanOptions _options;
    private readonly ILogger<ScanTimeoutSweeper> _logger;

    public ScanTimeoutSweeper(IServiceScopeFactory scopeFactory, IOptions<MalwareScanOptions> options, ILogger<ScanTimeoutSweeper> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    public async Task<int> SweepAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var maintenance = scope.ServiceProvider.GetRequiredService<IScanMaintenance>();
            var count = await maintenance.FailTimedOutScansAsync(TimeSpan.FromMinutes(_options.PendingTimeoutMinutes), ct);
            if (count > 0)
            {
                _logger.LogWarning("{Count} document scan(s) timed out and were marked as failed", count);
            }

            return count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Scan timeout sweep failed");
            return 0;
        }
    }
}
