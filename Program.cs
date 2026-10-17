using CodeMapSync.Options;
using CodeMapSync.Providers;
using CodeMapSync.Services;
using CodeMapSync.Analysis;
using CodeMapSync.Discovery;
using CodeMapSync.Sql;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("Sync").Get<SyncOptions>() ?? new SyncOptions();
if (options.Sources.Any(s => string.IsNullOrWhiteSpace(s.Id) || s.Id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))))
    throw new InvalidOperationException("Source Id must be alphanumeric, '-' or '_'");
if (options.Sources.GroupBy(s => s.Id).Any(g => g.Count() > 1))
    throw new InvalidOperationException("Duplicate source Id");
builder.Services.AddSingleton(options);
var sqlOptions=builder.Configuration.GetSection("SqlSources").Get<SqlSourceOptions>() ?? new SqlSourceOptions();
if (sqlOptions.Databases.Any(x=>string.IsNullOrWhiteSpace(x.Id) || x.Id.Any(c=>!(char.IsLetterOrDigit(c)||c is '-' or '_')) || string.IsNullOrWhiteSpace(x.ConnectionStringName)))
    throw new InvalidOperationException("Invalid SqlSources database ID or connection string name");
if (sqlOptions.Databases.GroupBy(x=>x.Id,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1) ||
    sqlOptions.Databases.Any(x=>options.Sources.Any(s=>s.Id.Equals(x.Id,StringComparison.OrdinalIgnoreCase))))
    throw new InvalidOperationException("Duplicate source IDs across SQL and repository sources");
builder.Services.AddSingleton(sqlOptions);
builder.Services.AddSingleton<SqlMetadataReader>();
builder.Services.AddSingleton<SqlCodeMapBuilder>();
builder.Services.AddSingleton<SqlSourceSnapshotWriter>();
builder.Services.AddSingleton<SqlSyncService>();
builder.Services.AddSingleton<ISourceProvider, GitSourceProvider>();
builder.Services.AddSingleton<ISourceProvider, FileSystemSourceProvider>();
builder.Services.AddSingleton<ISourceProvider, TfvcSourceProvider>();
builder.Services.AddHttpClient("github-discovery", client => { client.DefaultRequestHeaders.UserAgent.ParseAdd("CodeMapSync/3.0"); client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json")); client.Timeout = TimeSpan.FromSeconds(30); });
builder.Services.AddSingleton<ISourceDiscoveryProvider, GitHubDiscoveryProvider>();
builder.Services.AddSingleton<ICodeAnalyzer, CSharpAnalyzer>();
builder.Services.AddSingleton<ICodeAnalyzer, WebAnalyzer>();
builder.Services.AddSingleton<ICodeAnalyzer, XmlRdlAnalyzer>();
builder.Services.AddSingleton<CodeMapBuilder>();
builder.Services.AddSingleton<BlobSnapshotStore>();
builder.Services.AddSingleton<RepositorySyncService>();
builder.Services.AddHostedService<PeriodicSyncWorker>();
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapPost("/sync", async (HttpContext http, RepositorySyncService service, string? sourceId, CancellationToken ct) =>
{
    var key = options.TriggerApiKey;
    if (string.IsNullOrWhiteSpace(key)) return Results.Problem("Trigger API key is not configured", statusCode: 503);
    var supplied = http.Request.Headers["X-Sync-Key"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(supplied))) return Results.Unauthorized();
    // sourceId may be a configured group id or a discovered repository id; the service resolves it.
    try { return Results.Ok(await service.RunAsync(sourceId, ct)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException ex) when (ex.Message == "Synchronization is already running") { return Results.Conflict(ex.Message); }
});
app.Run();
