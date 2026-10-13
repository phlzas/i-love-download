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
    private readonly object _progressGate = new();
    private long _reportedBytes;

    public ChunkedDownloadEngine(HttpClient http, EngineOptions options)
    {
        _http = http;
        _options = options;
        _single = new SingleConnectionDownloadEngine(http);
    }

    public async Task<DownloadOutcome> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        try
        {
            var probe = await ProbeAsync(request, ct).ConfigureAwait(false);
            if (!probe.RangeSupported || probe.Length is not long length || length <= 0 || length <= _options.ChunkSizeBytes)
            {
                return await _single.DownloadAsync(request, progress, ct).ConfigureAwait(false);
            }

            return await DownloadChunkedAsync(request, length, probe.FileName, progress, ct).ConfigureAwait(false);
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
        CancellationToken ct)
    {
        Interlocked.Exchange(ref _reportedBytes, 0);

        var resolvedName = fileName ?? request.JobId.ToString("D");
        var jobDirectory = Path.Combine(request.DestinationRoot, request.JobId.ToString("D"));
        Directory.CreateDirectory(jobDirectory);
        var filePath = Path.Combine(jobDirectory, resolvedName);

        var chunkSize = Math.Max(1L, _options.ChunkSizeBytes);
        var maxConnections = Math.Max(1, _options.MaxConnections);
        var chunkCount = (int)Math.Min(int.MaxValue, (length + chunkSize - 1) / chunkSize);

        var selector = new NetworkInterfaceSelector(_options);
        var boundClients = new Dictionary<IPAddress, HttpClient>();
        var createdClients = new List<HttpClient>();
        var actualLocalIps = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var errors = new ConcurrentQueue<Exception>();
        var partPaths = new string[chunkCount];

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(maxConnections);

        try
        {
            var tasks = new List<Task>(chunkCount);
            for (var index = 0; index < chunkCount; index++)
            {
                var start = index * chunkSize;
                var end = Math.Min(length - 1, start + chunkSize - 1);
                var partPath = Path.Combine(jobDirectory, $"{resolvedName}.{index:D5}.part");
                partPaths[index] = partPath;

                var assigned = selector.Next();
                var client = ResolveClient(assigned, boundClients, createdClients, actualLocalIps);

                tasks.Add(RunChunkAsync(client, request.Url, start, end, partPath, length, progress, gate, errors, linked, ct));
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

            return new DownloadOutcome(true, resolvedName, filePath, length, length, null, chunkCount, DescribeInterfaces(selector, actualLocalIps));
        }
        finally
        {
            foreach (var client in createdClients)
            {
                client.Dispose();
            }

            foreach (var partPath in partPaths)
            {
                if (!string.IsNullOrEmpty(partPath))
                {
                    TryDelete(partPath);
                }
            }
        }
    }

    private async Task RunChunkAsync(
        HttpClient client,
        string url,
        long start,
        long end,
        string partPath,
        long totalLength,
        IProgress<DownloadProgress>? progress,
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

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new InvalidDataException($"Range {start}-{end} was ignored (status {(int)response.StatusCode}).");
            }

            var expected = end - start + 1;
            long written = 0;

            await using (var source = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false))
            await using (var destination = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
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

            if (written != expected)
            {
                throw new InvalidDataException($"Range {start}-{end} produced {written} bytes, expected {expected}.");
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

    private readonly record struct ProbeResult(bool RangeSupported, long? Length, string? FileName);
}
