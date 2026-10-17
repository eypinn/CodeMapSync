using CodeMapSync.Options;
using CodeMapSync.Providers;
using CodeMapSync.Analysis;
using CodeMapSync.Discovery;
namespace CodeMapSync.Services;
using CodeMapSync.Models;
using CodeMapSync.Sql;

public sealed class RepositorySyncService
{
    private readonly SyncOptions _options;
    private readonly SqlSyncService _sqlSync;
    private readonly SqlSourceOptions _sqlOptions;
    private readonly CSharpSqlRelationshipResolver _sqlResolver = new();
    private readonly RdlSqlRelationshipResolver _rdlResolver = new();
    private readonly RdlSqlColumnResolver _rdlColumnResolver = new();
    private readonly IReadOnlyDictionary<string, ISourceProvider> _providers;
    private readonly BlobSnapshotStore _store;
    private readonly IReadOnlyDictionary<string, ISourceDiscoveryProvider> _discovery;
    private readonly CodeMapBuilder _builder;
    private readonly CrossProjectRelationshipResolver _crossResolver = new();
    private readonly ILogger<RepositorySyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public RepositorySyncService(SyncOptions options, IEnumerable<ISourceProvider> providers, IEnumerable<ISourceDiscoveryProvider> discovery, BlobSnapshotStore store, CodeMapBuilder builder, SqlSyncService sqlSync, SqlSourceOptions sqlOptions, ILogger<RepositorySyncService> logger)
    {
        _options = options;
        _sqlSync = sqlSync;
        _sqlOptions = sqlOptions;
        _providers = providers.ToDictionary(p => p.Type, StringComparer.OrdinalIgnoreCase);
        _store = store;
        _discovery = discovery.ToDictionary(p => p.Type, StringComparer.OrdinalIgnoreCase);
        _builder = builder;
        _logger = logger;
    }
    private async Task<IReadOnlyList<SourceDefinition>> ExpandSourcesAsync(string? sourceId, CancellationToken ct)
    {
        var all = new List<SourceDefinition>();
        foreach (var source in _options.Sources.Where(s => s.Enabled))
        {
            if (_discovery.TryGetValue(source.Type, out var discoverer))
            {
                // Discover the group when all sources, the group, or a discovered child was requested.
                var children = await discoverer.DiscoverAsync(source, ct);
                all.AddRange(children.Where(child => sourceId is null || sourceId == source.Id || sourceId == child.Id));
            }
            else if (sourceId is null || source.Id == sourceId) all.Add(source);
        }
        if (sourceId is not null && all.Count == 0) throw new KeyNotFoundException($"Source not found: {sourceId}");
        if (all.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Duplicate discovered source IDs");
        return all;
    }
    public async Task<List<SyncResult>> RunAsync(string? sourceId, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("Synchronization is already running");
        try
        {
            var results = new List<SyncResult>();
            // Do not enumerate GitHub repositories when the request targets SQL only.
            var sqlOnly = sourceId is not null && _sqlOptions.Databases.Any(x => x.Enabled &&
                (sourceId.Equals(x.Id, StringComparison.OrdinalIgnoreCase) ||
                 (x.DiscoverDatabases && sourceId.StartsWith(x.Id + "-", StringComparison.OrdinalIgnoreCase))));
            var allSources = sqlOnly ? new List<SourceDefinition>() : (await ExpandSourcesAsync(null, ct)).ToList();
            var selectedSql = _sqlOptions.Databases.Any(x=>x.Enabled && (sourceId is null || sourceId==x.Id || (x.DiscoverDatabases && sourceId.StartsWith(x.Id+"-",StringComparison.OrdinalIgnoreCase))));
            var selectedSources = sourceId is null ? allSources : allSources.Where(s => s.Id == sourceId || _options.Sources.Any(group => group.Id == sourceId && group.Type == "github" && s.Id.StartsWith(group.Id + "-", StringComparison.OrdinalIgnoreCase))).ToList();
            if (sourceId is not null && selectedSources.Count() == 0 && !selectedSql) throw new KeyNotFoundException($"Source not found: {sourceId}");
            foreach (var source in selectedSources)
            {
                var temp = Path.Combine(Path.GetFullPath(_options.WorkspaceRoot), "staging", Guid.NewGuid().ToString("N"));
                try
                {
                    if (!_providers.TryGetValue(source.Type, out var provider)) throw new NotSupportedException($"Unknown provider: {source.Type}");
                    var prior = await _store.ReadManifestAsync(source.Id, ct);
                    var remoteRevision = await provider.GetRemoteVersionAsync(source, ct);
                    if (source.Type.Equals("git", StringComparison.OrdinalIgnoreCase) && remoteRevision is not null && prior?.AnalyzerVersion == "orm-v10-rdl" && prior?.NativeRevision == remoteRevision)
                    {
                        results.Add(new(source.Id, "unchanged", prior.ContentHash, prior.Files.Count, null));
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                    var snapshot = await provider.FetchAsync(source, temp, ct);
                    var (hash, files) = await SourceHasher.CalculateAsync(snapshot.LocalPath, source, ct);
                    if (prior?.AnalyzerVersion == "orm-v10-rdl" && prior?.ContentHash == hash)
                    {
                        results.Add(new(source.Id, "unchanged", hash, files.Count, null));
                        continue;
                    }
                    var manifest = new SourceManifest { SourceId = source.Id, SourceType = source.Type, AnalyzerVersion = "orm-v10-rdl", NativeRevision = snapshot.NativeRevision ?? remoteRevision,
                        ContentHash = hash, SyncedAtUtc = DateTimeOffset.UtcNow, Files = files };
                    var map = await _builder.BuildAsync(source, snapshot.LocalPath, hash, ct);
                    await _store.UploadCodeMapAsync(source.Id, hash, map, ct);
                    await _store.UploadAsync(source.Id, snapshot.LocalPath, manifest, ct);
                    results.Add(new(source.Id, prior is null ? "new" : "updated", hash, files.Count, null));
                    
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Source {SourceId} synchronization failed", source.Id);
                    results.Add(new(source.Id, "failed", null, 0, ex.Message));
                }
                finally
                {
                    try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { _logger.LogWarning(ex, "Could not clean up repository staging directory: {Directory}", temp); }
                }
            }
            // SQL snapshot pass runs independently of repository fetches.
            if (selectedSql) results.AddRange(await _sqlSync.RunAsync(sourceId,ct));
            // Cross-project pass reads even unchanged maps from Blob Storage.
            // This also makes manual sync of one repository link to previously indexed peers.
            var maps = new List<CodeMapDocument>();
            foreach (var source in allSources)
            {
                var manifest = await _store.ReadManifestAsync(source.Id, ct);
                if (manifest is null) continue;
                var map = await _store.ReadCodeMapAsync(source.Id, manifest.ContentHash, ct);
                if (map is not null) maps.Add(map);
            }
            // SQL sync returns per-database source IDs; read their latest snapshots.
            // Include unchanged SQL sources from the current run.
            foreach (var sqlResult in results.Where(r => r.Status is "updated" or "unchanged"))
            {
                if (maps.Any(m=>m.SourceId==sqlResult.SourceId)) continue;
                var sqlMap=await _store.ReadLatestCodeMapAsync(sqlResult.SourceId,ct);
                if (sqlMap is not null && sqlMap.Entries.Any(e=>e.Language=="sql")) maps.Add(sqlMap);
            }
            foreach (var sqlSource in _sqlOptions.Databases.Where(x=>x.Enabled && !x.DiscoverDatabases))
            {
                if (maps.Any(m=>m.SourceId==sqlSource.Id)) continue;
                var map=await _store.ReadLatestCodeMapAsync(sqlSource.Id,ct);
                if (map is not null) maps.Add(map);
            }
            // SQL-only requests still need existing C# snapshots to create cross-source links.
            if (sqlOnly)
            {
                foreach (var existing in await _store.ListLatestCodeMapsAsync(ct))
                    if (!maps.Any(m => m.SourceId == existing.SourceId)) maps.Add(existing);
            }
            // Load all existing SQL/C# snapshots so a C#-only sync can still resolve
            // references to a SQL snapshot that was not synchronized this run.
            foreach (var existing in await _store.ListLatestCodeMapsAsync(ct))
                if (!maps.Any(m => m.SourceId == existing.SourceId) &&
                    existing.Entries.Any(e => e.Language is "sql" or "csharp" or "rdl"))
                    maps.Add(existing);
            _logger.LogInformation("Relationship pass: Maps={Maps}, CSharpEntries={CSharp}, SqlObjects={Sql}, EfCandidates={Ef}",
                maps.Count, maps.Sum(m => m.Entries.Count(e => e.Language == "csharp")),
                maps.Sum(m => m.Entries.Count(e => e.Language == "sql" && e.Kind is "sql_table" or "sql_view" or "sql_procedure")),
                maps.Sum(m => m.Entries.Count(e => e.Attributes.Keys.Any(k => k.StartsWith("efTable:", StringComparison.OrdinalIgnoreCase)))));
            if (maps.Count > 0)
            {
                var before = maps.ToDictionary(m => m.SourceId,
                    m => (Relations: System.Text.Json.JsonSerializer.Serialize(m.Entries.Select(e => e.Relations)), Evidence: m.Entries.Sum(e => e.Evidence.Count),
                        Incoming: System.Text.Json.JsonSerializer.Serialize(m.Entries.Select(e => e.IncomingRelations))));
                var crossLinks = _crossResolver.Resolve(maps);
                var sqlLinks = _sqlResolver.Resolve(maps);
                var rdlLinks = _rdlResolver.Resolve(maps);
                var rdlColumnLinks = _rdlColumnResolver.Resolve(maps);
                _logger.LogInformation("RDL SQL links: Object={ObjectLinks}, Column={ColumnLinks}", rdlLinks, rdlColumnLinks);
                var incomingCount = new IncomingRelationIndexer().Rebuild(maps);
                _logger.LogInformation("Indexed incoming relations: {Count}", incomingCount);
                var stats = _sqlResolver.LastStats;
                _logger.LogInformation("SQL match: SqlObjects={SqlObjects}, References={References}, Matched={Matched}, Added={Added}, Missing={Missing}, Ambiguous={Ambiguous}",
                    stats.SqlObjects, stats.References, stats.MatchedReferences, stats.AddedRelations,
                    stats.MissingReferences, stats.AmbiguousReferences);
                // Persist evidence even if no relations were added; do not reupload unchanged maps.
                foreach (var map in maps)
                {
                    var previous = before[map.SourceId];
                    if (System.Text.Json.JsonSerializer.Serialize(map.Entries.Select(e => e.Relations)) == previous.Relations &&
                        map.Entries.Sum(e => e.Evidence.Count) == previous.Evidence &&
                        System.Text.Json.JsonSerializer.Serialize(map.Entries.Select(e => e.IncomingRelations)) == previous.Incoming) continue;
                    await _store.UploadCodeMapAsync(map.SourceId, map.SourceRevision, map, ct);
                    _logger.LogInformation("Persisted relations: SourceId={SourceId}, Revision={Revision}", map.SourceId, map.SourceRevision);
                }
                _logger.LogInformation("Cross-source relationships: Cross={Cross}, Sql={Sql}, Rdl={Rdl}", crossLinks, sqlLinks, rdlLinks);
            }
            return results;
        }
        finally { _gate.Release(); }
    }
}
public sealed record SyncResult(string SourceId, string Status, string? ContentHash, int FileCount, string? Error);
