using CodeMapSync.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace CodeMapSync.Sql;

public sealed class SqlSyncService(
    SqlSourceOptions options, IConfiguration configuration, SqlMetadataReader reader,
    SqlCodeMapBuilder builder, SqlSourceSnapshotWriter sourceWriter, BlobSnapshotStore store,
    ILogger<SqlSyncService> logger)
{
    public async Task<List<SyncResult>> RunAsync(string? sourceId, CancellationToken ct)
    {
        var results=new List<SyncResult>();
        logger.LogInformation("SQL synchronization started. RequestedSourceId={SourceId}, ConfiguredSources={Count}", sourceId, options.Databases.Count);
        foreach (var configured in options.Databases.Where(x=>x.Enabled))
        {
            if (sourceId is not null && sourceId!=configured.Id && !sourceId.StartsWith(configured.Id+"-",StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var connection=configuration.GetConnectionString(configured.ConnectionStringName);
                if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Missing SQL connection string: "+configured.ConnectionStringName);
                logger.LogInformation("SQL source discovery started. SourceId={SourceId}, DiscoveryEnabled={Discovery}", configured.Id, configured.DiscoverDatabases);
                var databases = configured.DiscoverDatabases
                    ? await reader.ListDatabasesAsync(connection,ct)
                    : new [] { new SqlConnectionStringBuilder(connection).InitialCatalog };
                if (databases.Count == 0) throw new InvalidOperationException("No SQL database was discovered. Configure Initial Catalog or DiscoverDatabases.");
                foreach (var db in databases)
                {
                    if (string.IsNullOrWhiteSpace(db)) continue;
                    if (configured.DiscoverDatabases &&
                        (!configured.IncludeDatabases.Contains("*") && !configured.IncludeDatabases.Contains(db,StringComparer.OrdinalIgnoreCase) ||
                         configured.ExcludeDatabases.Contains(db,StringComparer.OrdinalIgnoreCase))) continue;
                    var id=configured.DiscoverDatabases ? configured.Id+"-"+Sanitize(db) : configured.Id;
                    if (sourceId is not null && sourceId!=configured.Id && sourceId!=id) continue;
                    {
                        var cs=new SqlConnectionStringBuilder(connection){InitialCatalog=db}.ConnectionString;
                        var source=new SqlDatabaseSource{Id=id,Name=configured.Name+" / "+db,ConnectionStringName=configured.ConnectionStringName};
                        var stage="read-metadata";
                        try
                        {
                            logger.LogInformation("SQL stage started. SourceId={SourceId}, Database={Database}, Stage={Stage}",id,db,stage);
                            var catalog=await reader.ReadAsync(cs,ct);
                            logger.LogInformation("SQL metadata read. SourceId={SourceId}, Objects={Objects}, Columns={Columns}, ForeignKeys={ForeignKeys}, Dependencies={Dependencies}, Indexes={Indexes}", id,catalog.Objects.Count,catalog.Columns.Count,catalog.ForeignKeys.Count,catalog.Dependencies.Count,catalog.Indexes.Count);
                            stage="build-codemap";
                            var map=builder.Build(source,catalog);
                            logger.LogInformation("SQL CodeMap built. SourceId={SourceId}, Revision={Revision}, Entries={Entries}",id,map.SourceRevision,map.Entries.Count);
                            stage="read-snapshot";
                            var previous=await store.ReadCodeMapAsync(id,map.SourceRevision,ct);
                            var manifest=await store.ReadManifestAsync(id,ct);
                            var sourceNeedsUpload=manifest?.ContentHash!=map.SourceRevision || manifest.AnalyzerVersion!="sql-compact-v6";
                            // Source is published before the CodeMap latest pointer. A retry can repair partial uploads.
                            if (sourceNeedsUpload)
                            {
                                stage="upload-source";
                                logger.LogInformation("SQL Source upload started. SourceId={SourceId}, Revision={Revision}",id,map.SourceRevision);
                                await sourceWriter.WriteAsync(source,catalog,map.SourceRevision,ct);
                            }
                            // If the map is absent or the source was rebuilt, republish map and latest pointer.
                            // This also avoids leaving latest.json pointing to an uncommitted Source.
                            if (previous is null || sourceNeedsUpload)
                            {
                                stage="upload-codemap";
                                logger.LogInformation("SQL CodeMap upload started. SourceId={SourceId}, Revision={Revision}",id,map.SourceRevision);
                                await store.UploadCodeMapAsync(id,map.SourceRevision,map,ct);
                            }
                            logger.LogInformation("SQL synchronization completed. SourceId={SourceId}, Revision={Revision}, Changed={Changed}",id,map.SourceRevision,sourceNeedsUpload || previous is null);
                            results.Add(new SyncResult(id,sourceNeedsUpload || previous is null?"updated":"unchanged",map.SourceRevision,map.Entries.Count,null));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogError(ex,"SQL synchronization failed. SourceId={SourceId}, Database={Database}, Stage={Stage}, ExceptionType={ExceptionType}",id,db,stage,ex.GetType().FullName);
                            results.Add(new SyncResult(id,"failed",null,0,$"SQL database sync failed at stage '{stage}'; inspect server logs"));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,"SQL source discovery failed. SourceId={SourceId}, DiscoveryEnabled={DiscoveryEnabled}, ExceptionType={ExceptionType}",configured.Id,configured.DiscoverDatabases,ex.GetType().FullName);
                results.Add(new SyncResult(configured.Id,"failed",null,0,"SQL discovery failed; inspect server logs"));
            }
        }
        return results;
    }
    private static string Sanitize(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}
