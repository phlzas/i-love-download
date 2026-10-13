using System.Text.Json.Serialization;
using LoveDownload.Api;
using LoveDownload.Api.Data;
using LoveDownload.Api.Models;
using LoveDownload.Api.Services;
using LoveDownload.Engine;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://127.0.0.1:5099");

Directory.CreateDirectory(RepoPaths.DataDirectory);
Directory.CreateDirectory(RepoPaths.DownloadsDirectory);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={RepoPaths.DatabasePath}"));

builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddSingleton<JobControlRegistry>();

var engineOptions = new EngineOptions();
builder.Configuration.GetSection("Engine").Bind(engineOptions);
builder.Services.AddSingleton(engineOptions);

builder.Services
    .AddHttpClient<IDownloadEngine, ChunkedDownloadEngine>(client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
    });
builder.Services.AddHostedService<DownloadWorker>();

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
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
