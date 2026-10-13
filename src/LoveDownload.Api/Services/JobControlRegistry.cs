using System.Collections.Concurrent;

namespace LoveDownload.Api.Services;

/// <summary>
/// Tracks the live cancellation token source for each in-flight download job so
/// that a pause request (served on an HTTP thread) can cancel the download that
/// the background worker is currently running.
/// </summary>
public sealed class JobControlRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    public void Register(Guid id, CancellationTokenSource cts) => _sources[id] = cts;

    public void Unregister(Guid id) => _sources.TryRemove(id, out _);

    public bool TryCancel(Guid id)
    {
        if (!_sources.TryGetValue(id, out var cts))
        {
            return false;
        }

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The worker unregistered and disposed the source between our lookup
            // and the call; there is nothing live to cancel.
            return false;
        }
    }
}
