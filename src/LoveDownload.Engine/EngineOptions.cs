using System.Net.Sockets;

namespace LoveDownload.Engine;

public sealed class EngineOptions
{
    public long ChunkSizeBytes { get; set; } = 4L * 1024 * 1024;

    public int MaxConnections { get; set; } = 8;

    public IList<string> InterfaceAllowList { get; set; } = new List<string>();

    public AddressFamily AddressFamily { get; set; } = AddressFamily.InterNetwork;
}
