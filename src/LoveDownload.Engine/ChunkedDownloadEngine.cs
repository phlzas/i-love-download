using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace LoveDownload.Engine;

public sealed class ChunkedDownloadEngine : IDownloadEngine
{
    private const int BufferSize = 81920;

    private readonly HttpClient _http;
    private readonly EngineOptions _options;
    private readonly SingleConnectionDownloadEngine _single;
    private readonly IProbeCache? _probeCache;
    private readonly IInterfaceSpeedProvider? _speedProvider;
    private readonly object _progressGate = new();
    private long _reportedBytes;

    public ChunkedDownloadEngine(
        HttpClient http,
        EngineOptions options,
        IProbeCache? probeCache = null,
        IInterfaceSpeedProvider? speedProvider = null)
    {
        _http = http;
        _options = options;
        _single = new SingleConnectionDownloadEngine(http);
        _probeCache = probeCache;
        _speedProvider = speedProvider;
    }

    public async Task<DownloadOutcome> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct,
        IProgress<SegmentSample>? segments = null)
    {
        try
        {
            var probe = await ProbeAsync(request, ct).ConfigureAwait(false);
            if (!probe.RangeSupported || probe.Length is not long length || length <= 0 || length <= _options.ChunkSizeBytes)
            {
                return await _single.DownloadAsync(request, progress, ct, segments).ConfigureAwait(false);
            }

            return await DownloadChunkedAsync(request, length, probe.FileName, progress, segments, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DownloadOutcome(false, null, null, 0, null, ex.Message);
        }
    }

    private async Task<ProbeResult> ProbeAsync(DownloadRequest request, CancellationToken ct)
    {
        if (_probeCache is not null)
        {
            try
            {
                var cached = await _probeCache.GetAsync(request.Url, ct).ConfigureAwait(false);
                if (cached is { } hit)
                {
                    return hit;
                }
            }
            catch
            {
                // Probe caching is best-effort; fall through to a live probe.
            }
        }

        var result = await ProbeNetworkAsync(request, ct).ConfigureAwait(false);

        if (_probeCache is not null)
        {
            try
            {
                await _probeCache.SetAsync(request.Url, result, ct).ConfigureAwait(false);
            }
            catch
            {
                // Persisting the probe is best-effort.
            }
        }

        return result;
    }

    private async Task<ProbeResult> ProbeNetworkAsync(DownloadRequest request, CancellationToken ct)
    {
        long? length = null;
        var rangeSupported = false;
        string? fileName = null;

        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, request.Url);
            using var response = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                length = response.Content.Headers.ContentLength;
                rangeSupported = HasBytesRange(response);
                fileName = EngineFileName.Resolve(response, request);
            }
        }
        catch (HttpRequestException)
        {
            rangeSupported = false;
        }

        if (rangeSupported)
        {
            return new ProbeResult(true, length, fileName);
        }

        try
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, request.Url);
            get.Headers.Range = new RangeHeaderValue(0, 0);
            using var probe = await _http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (probe.StatusCode == HttpStatusCode.PartialContent)
            {
                rangeSupported = true;
                if (probe.Content.Headers.ContentRange?.Length is long total && total > 0)
                {
                    length = total;
                }

                fileName ??= EngineFileName.Resolve(probe, request);
            }
            else if (length is null && probe.IsSuccessStatusCode)
            {
                length = probe.Content.Headers.ContentLength;
                fileName ??= EngineFileName.Resolve(probe, request);
            }
        }
        catch (HttpRequestException)
        {
            rangeSupported = false;
        }

        return new ProbeResult(rangeSupported, length, fileName);
    }

    private async Task<DownloadOutcome> DownloadChunkedAsync(
        DownloadRequest request,
        long length,
        string? fileName,
        IProgress<DownloadProgress>? progress,
        IProgress<SegmentSample>? segments,
        CancellationToken ct)
    {
        var resolvedName = fileName ?? request.JobId.ToString("D");
        var jobDirectory = Path.Combine(request.DestinationRoot, request.JobId.ToString("D"));
        Directory.CreateDirectory(jobDirectory);
        var filePath = Path.Combine(jobDirectory, resolvedName);

        var chunkSize = Math.Max(1L, _options.ChunkSizeBytes);
        var maxConnections = Math.Max(1, _options.MaxConnections);
        var chunkCount = (int)Math.Min(int.MaxValue, (length + chunkSize - 1) / chunkSize);

        var selector = await CreateSelectorAsync(ct).ConfigureAwait(false);
        var boundClients = new Dictionary<IPAddress, HttpClient>();
        var createdClients = new List<HttpClient>();
        var actualLocalIps = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var errors = new ConcurrentQueue<Exception>();
        var partPaths = new string[chunkCount];
        var appendFrom = new long[chunkCount];
        var needsDownload = new bool[chunkCount];

        // Inspect any existing part files so a paused/interrupted run resumes from
        // what is already on disk instead of starting over.
        // Seed the progress counter with the bytes already present (complete + partial)
        // so progress stays continuous across a resume and never moves backwards.
        long existingBytes = 0;
        for (var index = 0; index < chunkCount; index++)
        {
            var start = index * chunkSize;
            var end = Math.Min(length - 1, start + chunkSize - 1);
            var expected = end - start + 1;
            var partPath = Path.Combine(jobDirectory, $"{resolvedName}.{index:D5}.part");
            partPaths[index] = partPath;

            var existing = TryGetLength(partPath);
            if (existing <= 0)
            {
                // Missing or empty: download the whole chunk fresh.
                appendFrom[index] = 0;
                needsDownload[index] = true;
            }
            else if (existing < expected)
            {
                // Partial: resume only the missing tail and append to what exists.
                appendFrom[index] = existing;
                existingBytes += existing;
                needsDownload[index] = true;
            }
            else if (existing == expected)
            {
                // Already complete: reuse as-is, count its bytes as already downloaded.
                existingBytes += existing;
                needsDownload[index] = false;
            }
            else
            {
                // Corrupt (longer than expected): discard and re-download the whole chunk.
                TryDelete(partPath);
                appendFrom[index] = 0;
                needsDownload[index] = true;
            }
        }

        Interlocked.Exchange(ref _reportedBytes, existingBytes);
        lock (_progressGate)
        {
            progress?.Report(new DownloadProgress(existingBytes, length));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(maxConnections);

        try
        {
            var tasks = new List<Task>(chunkCount);
            for (var index = 0; index < chunkCount; index++)
            {
                if (!needsDownload[index])
                {
                    continue;
                }

                var start = index * chunkSize;
                var end = Math.Min(length - 1, start + chunkSize - 1);
                var partPath = partPaths[index];

                var assigned = selector.Next();
                var client = ResolveClient(assigned, boundClients, createdClients, actualLocalIps);

                tasks.Add(RunChunkAsync(client, request.Url, index, assigned, start, end, appendFrom[index], partPath, length, progress, segments, gate, errors, linked, ct));
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (!errors.IsEmpty)
            {
            }

            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            if (errors.TryPeek(out var failure))
            {
                throw failure;
            }

            long reassembled = 0;
            await using (var output = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                for (var index = 0; index < chunkCount; index++)
                {
                    await using var part = new FileStream(partPaths[index], FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
                    reassembled += part.Length;
                    await part.CopyToAsync(output, BufferSize, ct).ConfigureAwait(false);
                }
            }

            var finalLength = new FileInfo(filePath).Length;
            if (reassembled != length || finalLength != length)
            {
                throw new InvalidDataException($"Reassembled {reassembled} bytes (file {finalLength}) but expected {length}.");
            }

            lock (_progressGate)
            {
                progress?.Report(new DownloadProgress(length, length));
            }

            // Reassembly succeeded, so the part files are no longer needed. Parts are kept
            // on cancel/failure so a subsequent run can resume from them.
            foreach (var partPath in partPaths)
            {
                TryDelete(partPath);
            }

            return new DownloadOutcome(true, resolvedName, filePath, length, length, null, chunkCount, DescribeInterfaces(selector, actualLocalIps));
        }
        finally
        {
            foreach (var client in createdClients)
            {
                client.Dispose();
            }
        }
    }

    private async Task<NetworkInterfaceSelector> CreateSelectorAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, double>? weights = null;

        if (_speedProvider is not null)
        {
            try
            {
                var names = NetworkInterfaceSelector.Discover(_options)
                    .Select(x => x.InterfaceName)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in names)
                {
                    var weight = await _speedProvider.GetWeightAsync(name, ct).ConfigureAwait(false);
                    if (weight > 0)
                    {
                        map[name] = weight;
                    }
                }

                if (map.Count > 0)
                {
                    weights = map;
                }
            }
            catch
            {
                // No usable speed memory: keep plain round-robin.
                weights = null;
            }
        }

        return new NetworkInterfaceSelector(_options, weights);
    }

    private async Task RunChunkAsync(
        HttpClient client,
        string url,
        int chunkIndex,
        NetworkInterfaceAddress? assigned,
        long start,
        long end,
        long appendFrom,
        string partPath,
        long totalLength,
        IProgress<DownloadProgress>? progress,
        IProgress<SegmentSample>? segments,
        SemaphoreSlim gate,
        ConcurrentQueue<Exception> errors,
        CancellationTokenSource linked,
        CancellationToken ct)
    {
        var acquired = false;
        try
        {
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;

            var requestStart = start + appendFrom;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(requestStart, end);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new InvalidDataException($"Range {requestStart}-{end} was ignored (status {(int)response.StatusCode}).");
            }

            var expected = end - requestStart + 1;
            long written = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            await using (var source = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false))
            await using (var destination = new FileStream(
                partPath,
                appendFrom > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                useAsync: true))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), linked.Token).ConfigureAwait(false)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                        written += read;

                        var aggregated = Interlocked.Add(ref _reportedBytes, read);
                        lock (_progressGate)
                        {
                            progress?.Report(new DownloadProgress(aggregated, totalLength));
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            sw.Stop();

            if (written != expected)
            {
                throw new InvalidDataException($"Range {requestStart}-{end} produced {written} bytes, expected {expected}.");
            }

            try
            {
                segments?.Report(new SegmentSample(
                    chunkIndex,
                    assigned?.InterfaceName,
                    assigned?.Address?.ToString(),
                    requestStart,
                    end,
                    written,
                    sw.ElapsedMilliseconds));
            }
            catch { /* stats must never break the download */ }

            if (_speedProvider is not null && assigned is not null)
            {
                try
                {
                    await _speedProvider.RecordSegmentAsync(
                        assigned.InterfaceName,
                        written,
                        sw.ElapsedMilliseconds,
                        linked.Token).ConfigureAwait(false);
                }
                catch { /* speed memory must never break the download */ }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Enqueue(ex);
            linked.Cancel();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
            }
        }
    }

    private HttpClient ResolveClient(
        NetworkInterfaceAddress? assigned,
        Dictionary<IPAddress, HttpClient> boundClients,
        List<HttpClient> createdClients,
        ConcurrentDictionary<string, byte> actualLocalIps)
    {
        if (assigned is null)
        {
            return _http;
        }

        if (!boundClients.TryGetValue(assigned.Address, out var client))
        {
            client = CreateBoundClient(assigned.Address, actualLocalIps);
            boundClients[assigned.Address] = client;
            createdClients.Add(client);
        }

        return client;
    }

    private static HttpClient CreateBoundClient(IPAddress localAddress, ConcurrentDictionary<string, byte> actualLocalIps)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(localAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(localAddress, 0));

                    var host = context.DnsEndPoint.Host;
                    var port = context.DnsEndPoint.Port;

                    if (IPAddress.TryParse(host, out var literal))
                    {
                        if (literal.AddressFamily != localAddress.AddressFamily)
                        {
                            throw new InvalidOperationException($"Local {localAddress.AddressFamily} cannot reach {literal.AddressFamily} target {host}.");
                        }

                        await socket.ConnectAsync(new IPEndPoint(literal, port), token).ConfigureAwait(false);
                    }
                    else
                    {
                        var resolved = await Dns.GetHostAddressesAsync(host, localAddress.AddressFamily, token).ConfigureAwait(false);
                        if (resolved.Length == 0)
                        {
                            throw new InvalidOperationException($"No {localAddress.AddressFamily} address resolved for {host}.");
                        }

                        await socket.ConnectAsync(new IPEndPoint(resolved[0], port), token).ConfigureAwait(false);
                    }

                    if (socket.LocalEndPoint is IPEndPoint localEndpoint)
                    {
                        actualLocalIps.TryAdd(localEndpoint.Address.ToString(), 0);
                    }

                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
    }

    private static string? DescribeInterfaces(NetworkInterfaceSelector selector, ConcurrentDictionary<string, byte> actualLocalIps)
    {
        if (actualLocalIps.IsEmpty)
        {
            return null;
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in selector.Available)
        {
            names[entry.Address.ToString()] = entry.InterfaceName;
        }

        var parts = new List<string>();
        foreach (var ip in actualLocalIps.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            parts.Add(names.TryGetValue(ip, out var name) ? $"{name} ({ip})" : ip);
        }

        return string.Join(", ", parts);
    }

    private static bool HasBytesRange(HttpResponseMessage response)
    {
        foreach (var value in response.Headers.AcceptRanges)
        {
            if (string.Equals(value, "bytes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static long TryGetLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
