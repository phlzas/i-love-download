using LoveDownload.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LoveDownload.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<DownloadJob> Jobs => Set<DownloadJob>();
    public DbSet<ScheduleSetting> ScheduleSettings => Set<ScheduleSetting>();
    public DbSet<DownloadSegmentStat> SegmentStats => Set<DownloadSegmentStat>();
    public DbSet<ProbeCacheEntry> ProbeCacheEntries => Set<ProbeCacheEntry>();
    public DbSet<InterfaceSpeed> InterfaceSpeeds => Set<InterfaceSpeed>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite cannot ORDER BY DateTimeOffset, so persist UTC ticks (INTEGER) and keep ORDER BY server-side.
        var utcTicks = new ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));

        // Nullable-aware variant for optional timestamps: EF passes null through untouched.
        var utcTicksNullable = new ValueConverter<DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.UtcTicks : (long?)null,
            v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : (DateTimeOffset?)null);

        var job = modelBuilder.Entity<DownloadJob>();
        job.HasKey(x => x.Id);
        job.Property(x => x.Url).IsRequired();
        job.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        job.Property(x => x.CreatedAt).HasConversion(utcTicks);
        job.Property(x => x.UpdatedAt).HasConversion(utcTicks);
        job.Property(x => x.StartedAt).HasConversion(utcTicksNullable);
        job.Property(x => x.CompletedAt).HasConversion(utcTicksNullable);
        job.HasIndex(x => x.CreatedAt);

        var schedule = modelBuilder.Entity<ScheduleSetting>();
        schedule.HasKey(x => x.Id);
        schedule.Property(x => x.UpdatedAt).HasConversion(utcTicks);

        var segment = modelBuilder.Entity<DownloadSegmentStat>();
        segment.HasKey(x => x.Id);
        segment.Property(x => x.CreatedAt).HasConversion(utcTicks);
        segment.HasIndex(x => new { x.JobId, x.ChunkIndex }).IsUnique();

        var probe = modelBuilder.Entity<ProbeCacheEntry>();
        probe.ToTable("ProbeCache");
        probe.HasKey(x => x.Url);
        probe.Property(x => x.Url).HasMaxLength(2048);
        probe.Property(x => x.FetchedAt).HasConversion(utcTicks);

        var speed = modelBuilder.Entity<InterfaceSpeed>();
        speed.ToTable("InterfaceSpeed");
        speed.HasKey(x => x.InterfaceKey);
        speed.Property(x => x.InterfaceKey).HasMaxLength(256);
        speed.Property(x => x.UpdatedAt).HasConversion(utcTicks);
    }
}
