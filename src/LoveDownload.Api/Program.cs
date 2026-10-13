using System.Text.Json.Serialization;
using LoveDownload.Api;
using LoveDownload.Api.Data;
using LoveDownload.Api.Llm;
using LoveDownload.Api.Models;
using LoveDownload.Api.Reporting;
using LoveDownload.Api.Services;
using LoveDownload.Engine;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// QuestPDF is free under the Community license; declare it once before any PDF is generated.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var listenUrls = Environment.GetEnvironmentVariable("LOVEDOWNLOAD_URLS");
builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(listenUrls) ? "http://127.0.0.1:5099" : listenUrls);

Directory.CreateDirectory(RepoPaths.DataDirectory);
Directory.CreateDirectory(RepoPaths.DownloadsDirectory);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={RepoPaths.DatabasePath}"));

builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddSingleton<JobControlRegistry>();
builder.Services.AddSingleton<ScheduleStore>();
builder.Services.AddSingleton<PdfReportService>();

var engineOptions = new EngineOptions();
builder.Configuration.GetSection("Engine").Bind(engineOptions);
builder.Services.AddSingleton(engineOptions);

// LLM scheduling (concept 5): options come from the "Llm" section then LLM_* env vars; Enabled defaults to false.
var llmOptions = LlmOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(llmOptions);
builder.Services.AddSingleton<ILlmCostLogger, LlmCostLogger>();
builder.Services.AddSingleton<PromptLoader>();
builder.Services.AddSingleton<ScheduleNlParser>();
builder.Services.AddHttpClient<ILlmClient, OpenAiCompatibleLlmClient>();

builder.Services
    .AddHttpClient<IDownloadEngine, ChunkedDownloadEngine>(client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
    });
builder.Services.AddHostedService<DownloadWorker>();
builder.Services.AddHostedService<DownloadScheduler>();

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

static bool HasColumn(AppDbContext db, string table, string column)
{
    // Table name is a compile-time constant, so it is embedded literally rather than
    // parameterized (EF would otherwise quote the placeholder and match nothing).
    var sql = "SELECT name AS \"Value\" FROM pragma_table_info('" + table + "')";
    var names = db.Database.SqlQueryRaw<string>(sql).ToList();
    return names.Any(n => string.Equals(n, column, StringComparison.OrdinalIgnoreCase));
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated does not add tables to a pre-existing database, so upgrade a stale
    // schema in place. Keep the DDL in sync with ScheduleSetting / AppDbContext.
    db.Database.ExecuteSqlRaw(
        "CREATE TABLE IF NOT EXISTS \"ScheduleSettings\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_ScheduleSettings\" PRIMARY KEY AUTOINCREMENT," +
        "\"Enabled\" INTEGER NOT NULL," +
        "\"StartMinutes\" INTEGER NOT NULL," +
        "\"EndMinutes\" INTEGER NOT NULL," +
        "\"UpdatedAt\" INTEGER NOT NULL);");

    db.Database.ExecuteSqlRaw(
        "CREATE TABLE IF NOT EXISTS \"SegmentStats\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_SegmentStats\" PRIMARY KEY AUTOINCREMENT," +
        "\"JobId\" TEXT NOT NULL," +
        "\"ChunkIndex\" INTEGER NOT NULL," +
        "\"InterfaceName\" TEXT NULL," +
        "\"LocalAddress\" TEXT NULL," +
        "\"Start\" INTEGER NOT NULL," +
        "\"End\" INTEGER NOT NULL," +
        "\"BytesWritten\" INTEGER NOT NULL," +
        "\"ElapsedMs\" INTEGER NOT NULL," +
        "\"CreatedAt\" INTEGER NOT NULL);");

    db.Database.ExecuteSqlRaw(
        "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_SegmentStats_JobId_ChunkIndex\" " +
        "ON \"SegmentStats\" (\"JobId\", \"ChunkIndex\");");

    if (!HasColumn(db, "Jobs", "StartedAt"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Jobs\" ADD COLUMN \"StartedAt\" INTEGER NULL;");
    }

    if (!HasColumn(db, "Jobs", "CompletedAt"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Jobs\" ADD COLUMN \"CompletedAt\" INTEGER NULL;");
    }
}

app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var queue = app.Services.GetRequiredService<DownloadQueue>();

    var interrupted = db.Jobs
        .Where(x => x.Status == DownloadStatus.Downloading)
        .ToList();

    foreach (var job in interrupted)
    {
        job.Status = DownloadStatus.Queued;
        job.UpdatedAt = DateTimeOffset.UtcNow;
    }

    if (interrupted.Count > 0)
    {
        db.SaveChanges();
    }

    var queued = db.Jobs
        .Where(x => x.Status == DownloadStatus.Queued)
        .Select(x => x.Id)
        .ToList();

    foreach (var jobId in queued)
    {
        queue.Enqueue(jobId);
    }
}

app.Run();
