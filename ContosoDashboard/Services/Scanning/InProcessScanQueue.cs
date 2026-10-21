using System.Threading.Channels;

namespace ContosoDashboard.Services;

// Training-mode queue: an in-memory channel consumed by ScanWorker. Not shared across web instances.
public class InProcessScanQueue : IScanQueue
{
    private readonly Channel<ScanRequest> _channel = Channel.CreateBounded<ScanRequest>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    public ChannelReader<ScanRequest> Reader => _channel.Reader;

    public async Task EnqueueAsync(ScanRequest request, CancellationToken ct = default)
        => await _channel.Writer.WriteAsync(request, ct);
}
