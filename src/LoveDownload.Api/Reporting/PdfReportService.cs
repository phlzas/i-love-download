using System.Globalization;
using LoveDownload.Api.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace LoveDownload.Api.Reporting;

public sealed class PdfReportService
{
    public byte[] Build(DownloadJob job, IReadOnlyList<DownloadSegmentStat> segments)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(segments);

        var model = ReportModel.Create(job, segments);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Element(c => ComposeHeader(c, model));
                page.Content().Element(c => ComposeContent(c, model));
                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("LoveDownload report - page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        }).GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, ReportModel model)
    {
        container.Column(column =>
        {
            column.Item().Text("Download Report").FontSize(18).SemiBold();
            column.Item().PaddingTop(2).Text(model.Job.Url).FontSize(9).FontColor(Colors.Grey.Darken1);
            column.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });
    }

    private static void ComposeContent(IContainer container, ReportModel model)
    {
        container.PaddingTop(10).Column(column =>
        {
            column.Spacing(12);

            column.Item().Element(c => ComposeJobDetails(c, model));
            column.Item().Element(c => ComposeSummary(c, model));
            column.Item().Element(c => ComposeTimeSaved(c, model));
            column.Item().Element(c => ComposeInterfaces(c, model));
            column.Item().Element(c => ComposeChunks(c, model));
        });
    }

    private static void ComposeJobDetails(IContainer container, ReportModel model)
    {
        var job = model.Job;
        container.Column(column =>
        {
            column.Item().Text("Job").FontSize(12).SemiBold();
            column.Item().PaddingTop(4).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(110);
                    columns.RelativeColumn();
                });

                DetailRow(table, "File name", job.FileName ?? "(unknown)");
                DetailRow(table, "Status", job.Status.ToString());
                DetailRow(table, "Created at", FormatTimestamp(job.CreatedAt));
                DetailRow(table, "Started at", FormatTimestamp(job.StartedAt));
                DetailRow(table, "Completed at", FormatTimestamp(job.CompletedAt));
            });
        });
    }

    private static void ComposeSummary(IContainer container, ReportModel model)
    {
        container.Column(column =>
        {
            column.Item().Text("Summary").FontSize(12).SemiBold();
            column.Item().PaddingTop(4).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(180);
                    columns.RelativeColumn();
                });

                DetailRow(table, "Total bytes downloaded", $"{FormatBytes(model.TotalBytes)} ({model.TotalBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes)");
                DetailRow(table, "Elapsed time", model.ElapsedMs.HasValue ? FormatDuration(model.ElapsedMs.Value) : "n/a");
                DetailRow(table, "Overall average speed", FormatRate(model.OverallBytesPerSecond));
            });
        });
    }

    private static void ComposeTimeSaved(IContainer container, ReportModel model)
    {
        container.Column(column =>
        {
            column.Item().Text("Time saved vs single link").FontSize(12).SemiBold();
            column.Item().PaddingTop(4).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(180);
                    columns.RelativeColumn();
                });

                if (model.TimeSaved is null)
                {
                    DetailRow(table, "Result", "n/a / insufficient data");
                    return;
                }

                var saved = model.TimeSaved;
                DetailRow(table, "Slowest link (single-link bps)", FormatRate(saved.SingleLinkBytesPerSecond));
                DetailRow(table, "Single-link estimate", FormatDuration(saved.SingleLinkEstimateMs));
                DetailRow(table, "Actual elapsed", FormatDuration(saved.ActualMs));
                DetailRow(table, "Time saved", FormatDuration(saved.TimeSavedMs));
            });
        });
    }

    private static void ComposeInterfaces(IContainer container, ReportModel model)
    {
        container.Column(column =>
        {
            column.Item().Text("Per-interface breakdown").FontSize(12).SemiBold();

            if (model.Interfaces.Count == 0)
            {
                column.Item().PaddingTop(4).Text("No per-segment data.").Italic().FontColor(Colors.Grey.Darken1);
                return;
            }

            column.Item().PaddingTop(4).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(2);
                    columns.ConstantColumn(50);
                    columns.RelativeColumn(2);
                    columns.ConstantColumn(70);
                    columns.RelativeColumn(2);
                });

                table.Header(header =>
                {
                    HeaderCell(header, "Interface");
                    HeaderCell(header, "Local address");
                    HeaderCell(header, "Chunks");
                    HeaderCell(header, "Bytes");
                    HeaderCell(header, "Elapsed");
                    HeaderCell(header, "Avg speed");
                });

                foreach (var iface in model.Interfaces)
                {
                    BodyCell(table, iface.InterfaceName ?? "(unknown)");
                    BodyCell(table, iface.LocalAddress ?? "(unknown)");
                    BodyCell(table, iface.ChunkCount.ToString(CultureInfo.InvariantCulture));
                    BodyCell(table, FormatBytes(iface.Bytes));
                    BodyCell(table, FormatDuration(iface.ElapsedMs));
                    BodyCell(table, FormatRate(iface.BytesPerSecond));
                }
            });
        });
    }

    private static void ComposeChunks(IContainer container, ReportModel model)
    {
        container.Column(column =>
        {
            column.Item().Text("Per-chunk detail").FontSize(12).SemiBold();

            if (model.Segments.Count == 0)
            {
                column.Item().PaddingTop(4).Text("No per-segment data.").Italic().FontColor(Colors.Grey.Darken1);
                return;
            }

            column.Item().PaddingTop(4).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(40);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(2);
                    columns.ConstantColumn(60);
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(2);
                });

                table.Header(header =>
                {
                    HeaderCell(header, "Chunk");
                    HeaderCell(header, "Start");
                    HeaderCell(header, "End");
                    HeaderCell(header, "Bytes");
                    HeaderCell(header, "Elapsed");
                    HeaderCell(header, "Interface / address");
                });

                foreach (var segment in model.Segments)
                {
                    BodyCell(table, segment.ChunkIndex.ToString(CultureInfo.InvariantCulture));
                    BodyCell(table, segment.Start.ToString("N0", CultureInfo.InvariantCulture));
                    BodyCell(table, segment.End.ToString("N0", CultureInfo.InvariantCulture));
                    BodyCell(table, FormatBytes(segment.BytesWritten));
                    BodyCell(table, FormatDuration(segment.ElapsedMs));
                    BodyCell(table, $"{segment.InterfaceName ?? "(unknown)"} / {segment.LocalAddress ?? "(unknown)"}");
                }
            });
        });
    }

    private static void DetailRow(TableDescriptor table, string label, string value)
    {
        table.Cell().Element(LabelCell).Text(label);
        table.Cell().Element(ValueCell).Text(value);
    }

    private static void HeaderCell(TableCellDescriptor header, string text)
    {
        header.Cell().Element(HeaderCellStyle).Text(text).SemiBold();
    }

    private static void BodyCell(TableDescriptor table, string text)
    {
        table.Cell().Element(BodyCellStyle).Text(text);
    }

    private static IContainer LabelCell(IContainer container) =>
        container.PaddingVertical(2).PaddingRight(8).DefaultTextStyle(x => x.FontColor(Colors.Grey.Darken2));

    private static IContainer ValueCell(IContainer container) =>
        container.PaddingVertical(2);

    private static IContainer HeaderCellStyle(IContainer container) =>
        container.Background(Colors.Grey.Lighten2).Padding(3).BorderBottom(1).BorderColor(Colors.Grey.Medium);

    private static IContainer BodyCellStyle(IContainer container) =>
        container.Padding(3).BorderBottom(1).BorderColor(Colors.Grey.Lighten3);

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value.HasValue ? value.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) : "n/a";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "n/a";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} B"
            : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string FormatRate(double? bytesPerSecond)
    {
        if (!bytesPerSecond.HasValue || bytesPerSecond.Value <= 0)
        {
            return "n/a";
        }

        return $"{FormatBytes((long)Math.Round(bytesPerSecond.Value))}/s";
    }

    private static string FormatDuration(double milliseconds)
    {
        if (milliseconds < 0)
        {
            return "n/a";
        }

        if (milliseconds < 1000)
        {
            return $"{milliseconds.ToString("0", CultureInfo.InvariantCulture)} ms";
        }

        var totalSeconds = milliseconds / 1000.0;
        if (totalSeconds < 60)
        {
            return $"{totalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
        }

        var span = TimeSpan.FromMilliseconds(milliseconds);
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s";
        }

        return $"{span.Minutes}m {span.Seconds}s";
    }

    private sealed record InterfaceSummary(
        string? InterfaceName,
        string? LocalAddress,
        int ChunkCount,
        long Bytes,
        long ElapsedMs,
        double? BytesPerSecond);

    private sealed record TimeSaved(
        double SingleLinkBytesPerSecond,
        double SingleLinkEstimateMs,
        double ActualMs,
        double TimeSavedMs);

    private sealed class ReportModel
    {
        public required DownloadJob Job { get; init; }
        public required IReadOnlyList<DownloadSegmentStat> Segments { get; init; }
        public required IReadOnlyList<InterfaceSummary> Interfaces { get; init; }
        public required long TotalBytes { get; init; }
        public required double? ElapsedMs { get; init; }
        public required double? OverallBytesPerSecond { get; init; }
        public required TimeSaved? TimeSaved { get; init; }

        public static ReportModel Create(DownloadJob job, IReadOnlyList<DownloadSegmentStat> segments)
        {
            var interfaces = segments
                .GroupBy(s => new { s.InterfaceName, s.LocalAddress })
                .Select(group =>
                {
                    var bytes = group.Sum(x => x.BytesWritten);
                    var elapsed = group.Sum(x => x.ElapsedMs);
                    return new InterfaceSummary(
                        group.Key.InterfaceName,
                        group.Key.LocalAddress,
                        group.Count(),
                        bytes,
                        elapsed,
                        elapsed > 0 ? (double)bytes / (elapsed / 1000.0) : null);
                })
                .OrderByDescending(x => x.Bytes)
                .ToList();

            var segmentBytes = segments.Sum(s => s.BytesWritten);
            var totalBytes = job.BytesDownloaded > 0 ? job.BytesDownloaded : segmentBytes;

            double? elapsedMs = job.StartedAt.HasValue && job.CompletedAt.HasValue
                ? (job.CompletedAt.Value - job.StartedAt.Value).TotalMilliseconds
                : null;

            double? overallBytesPerSecond = elapsedMs.HasValue && elapsedMs.Value > 0
                ? totalBytes / (elapsedMs.Value / 1000.0)
                : null;

            return new ReportModel
            {
                Job = job,
                Segments = segments,
                Interfaces = interfaces,
                TotalBytes = totalBytes,
                ElapsedMs = elapsedMs,
                OverallBytesPerSecond = overallBytesPerSecond,
                TimeSaved = ComputeTimeSaved(interfaces, totalBytes, elapsedMs)
            };
        }

        private static TimeSaved? ComputeTimeSaved(
            IReadOnlyList<InterfaceSummary> interfaces,
            long totalBytes,
            double? elapsedMs)
        {
            if (interfaces.Count < 2 || totalBytes <= 0 || !elapsedMs.HasValue)
            {
                return null;
            }

            var rates = interfaces
                .Where(x => x.BytesPerSecond.HasValue && x.BytesPerSecond.Value > 0)
                .Select(x => x.BytesPerSecond!.Value)
                .ToList();

            if (rates.Count < 2)
            {
                return null;
            }

            var singleLinkBytesPerSecond = rates.Min();
            if (singleLinkBytesPerSecond <= 0)
            {
                return null;
            }

            var singleLinkEstimateMs = totalBytes / singleLinkBytesPerSecond * 1000.0;
            var actualMs = elapsedMs.Value;
            var timeSavedMs = Math.Max(0, singleLinkEstimateMs - actualMs);

            return new TimeSaved(singleLinkBytesPerSecond, singleLinkEstimateMs, actualMs, timeSavedMs);
        }
    }
}
