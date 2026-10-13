namespace LoveDownload.Engine;

public readonly record struct SegmentSample(
    int ChunkIndex,
    string? InterfaceName,
    string? LocalAddress,
    long Start,
    long End,
    long BytesWritten,
    long ElapsedMs);
