using System.Threading.Channels;

namespace LoveDownload.Api.Services;

public sealed class DownloadQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public bool Enqueue(Guid id) => _channel.Writer.TryWrite(id);

    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(id, ct);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
