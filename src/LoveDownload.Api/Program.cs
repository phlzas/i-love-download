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

    var unfinished = db.Jobs
        .Where(x => x.Status == DownloadStatus.Queued || x.Status == DownloadStatus.Downloading)
        .Select(x => x.Id)
        .ToList();

    foreach (var jobId in unfinished)
    {
        queue.Enqueue(jobId);
    }
}

app.Run();
