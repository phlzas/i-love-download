using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using LoveDownload.Api;
using LoveDownload.Api.Data;
using LoveDownload.Api.Llm;
using LoveDownload.Api.Middleware;
using LoveDownload.Api.Models;
using LoveDownload.Api.Options;
using LoveDownload.Api.Reporting;
using LoveDownload.Api.Services;
using LoveDownload.Engine;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// QuestPDF is free under the Community license; declare it once before any PDF is generated.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var listenUrls = Environment.GetEnvironmentVariable("LOVEDOWNLOAD_URLS");
builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(listenUrls) ? "http://127.0.0.1:5099" : listenUrls);

Directory.CreateDirectory(RepoPaths.DataDirectory);
Directory.CreateDirectory(RepoPaths.DownloadsDirectory);

// AUTH_ENABLED drives the optional auth layer. Default false keeps the pipeline
// identical to the pre-auth app.
static bool IsAuthEnabled(IConfiguration configuration)
{
    var raw = configuration["AUTH_ENABLED"]
        ?? configuration["Auth:Enabled"]
        ?? Environment.GetEnvironmentVariable("AUTH_ENABLED");

    if (string.IsNullOrWhiteSpace(raw))
    {
        return false;
    }

    raw = raw.Trim();
    return raw.Equals("true", StringComparison.OrdinalIgnoreCase)
        || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || raw == "1";
}

var authEnabled = IsAuthEnabled(builder.Configuration);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={RepoPaths.DatabasePath}"));

builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddSingleton<JobControlRegistry>();
builder.Services.AddSingleton<ScheduleStore>();
builder.Services.AddSingleton<PdfReportService>();
builder.Services.AddSingleton<IProbeCache, SqliteProbeCache>();
builder.Services.AddSingleton<IInterfaceSpeedProvider, SqliteInterfaceSpeedProvider>();

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

var mvcBuilder = builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ---- Optional JWT auth (concept 6, ported from BE-03) ------------------------------
// Only wired when AUTH_ENABLED=true. With the flag off (the default) none of this is
// registered, so every existing endpoint and the m3/m4/m5 verifiers are unaffected.
if (authEnabled)
{
    // Fail fast with a clear message when the signing secret is missing or weak.
    var jwtOptions = JwtOptions.FromEnvironment();
    builder.Services.AddSingleton(jwtOptions);

    // A SEPARATE context on its own SQLite file: enabling auth never touches lovedownload.db.
    builder.Services.AddDbContext<AuthDbContext>(options =>
        options.UseSqlite($"Data Source={RepoPaths.AuthDatabasePath}"));

    builder.Services
        .AddIdentityCore<AppUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 8;
            options.Password.RequireDigit = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireNonAlphanumeric = true;
        })
        .AddEntityFrameworkStores<AuthDbContext>()
        .AddDefaultTokenProviders();

    builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtOptions.Issuer,
                ValidateAudience = true,
                ValidAudience = jwtOptions.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Secret)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = ClaimTypes.NameIdentifier,
                RoleClaimType = ClaimTypes.Role,
            };

            // Every auth failure answers with a JSON body instead of an empty 401,
            // so the API matches the {"error": "..."} contract for all 4xx responses.
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var header = context.Request.Headers.Authorization.ToString();
                    if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Token = header["Bearer ".Length..].Trim();
                        context.HttpContext.Items["BearerTokenPresent"] = true;
                    }

                    return Task.CompletedTask;
                },
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    // Distinguish a missing/malformed token ("required") from a rejected one.
                    var message = context.HttpContext.Items.ContainsKey("BearerTokenPresent")
                        ? "Invalid or expired token"
                        : "Access token required";
                    await context.HttpContext.WriteErrorAsync(StatusCodes.Status401Unauthorized, message);
                },
                OnForbidden = async context =>
                {
                    await context.HttpContext.WriteErrorAsync(
                        StatusCodes.Status403Forbidden,
                        "Forbidden: you do not have access to this resource.");
                },
            };
        });

    builder.Services.AddAuthorization(options =>
    {
        // Global fallback: every endpoint requires an authenticated caller unless it is
        // explicitly [AllowAnonymous] (the /auth entry points, /public and /health).
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        // Authenticated caller must carry a "role" claim of "admin".
        options.AddPolicy("AdminOnly", policy => policy.RequireClaim("role", "admin"));
    });

    builder.Services.PostConfigure<ApiBehaviorOptions>(options =>
    {
        // Automatic model-validation failures return {"error": "..."} like every other 4xx.
        options.InvalidModelStateResponseFactory = context =>
        {
            var message = context.ModelState
                .SelectMany(kvp => kvp.Value!.Errors)
                .Select(e => e.ErrorMessage)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? "Invalid request.";

            return new BadRequestObjectResult(new { error = message });
        };
    });

    // Brute-force protection on /auth/login.
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        options.OnRejected = async (context, cancellationToken) =>
        {
            await context.HttpContext.WriteErrorAsync(
                StatusCodes.Status429TooManyRequests,
                "Too many login attempts. Please try again later.",
                cancellationToken);
        };
    });
}
else
{
    // With auth disabled the auth controllers must not exist at all, so the app
    // exposes exactly the pre-auth routes (/downloads, /schedule).
    mvcBuilder.ConfigureApplicationPartManager(manager =>
        manager.FeatureProviders.Add(new AuthControllerFeatureProvider()));
}

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

    db.Database.ExecuteSqlRaw(
        "CREATE TABLE IF NOT EXISTS \"ProbeCache\" (" +
        "\"Url\" TEXT NOT NULL CONSTRAINT \"PK_ProbeCache\" PRIMARY KEY," +
        "\"RangeSupported\" INTEGER NOT NULL," +
        "\"Length\" INTEGER NULL," +
        "\"FileName\" TEXT NULL," +
        "\"FetchedAt\" INTEGER NOT NULL);");

    db.Database.ExecuteSqlRaw(
        "CREATE TABLE IF NOT EXISTS \"InterfaceSpeed\" (" +
        "\"InterfaceKey\" TEXT NOT NULL CONSTRAINT \"PK_InterfaceSpeed\" PRIMARY KEY," +
        "\"BytesPerSecond\" REAL NOT NULL," +
        "\"UpdatedAt\" INTEGER NOT NULL);");

    if (!HasColumn(db, "Jobs", "StartedAt"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Jobs\" ADD COLUMN \"StartedAt\" INTEGER NULL;");
    }

    if (!HasColumn(db, "Jobs", "CompletedAt"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Jobs\" ADD COLUMN \"CompletedAt\" INTEGER NULL;");
    }
}

if (authEnabled)
{
    // Separate auth database (data/auth.db), created on first run. Not migrated on
    // purpose: the auth schema is additive-only and independent of lovedownload.db.
    using (var authScope = app.Services.CreateScope())
    {
        var authDb = authScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        authDb.Database.EnsureCreated();
    }

    app.UseAuthentication();
    // Reusable token-checking middleware: rejects revoked (logged-out) tokens.
    app.UseMiddleware<RevokedTokenMiddleware>();
    app.UseAuthorization();
    app.UseRateLimiter();
}

app.MapControllers();

if (authEnabled)
{
    // Anonymous liveness endpoint, reachable without a token despite the fallback policy.
    app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
}

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
