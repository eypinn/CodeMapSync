using System.Text.Json;
using CodeMapSync.Services;

namespace CodeMapSync.Sql;

// Writes source snapshots using the same sources/{id}/manifest.json convention as C#.
public sealed class SqlSourceSnapshotWriter(BlobSnapshotStore store)
{
    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
    private static string Get(Dictionary<string,object?> row, string key) => row.TryGetValue(key,out var v) ? Convert.ToString(v) ?? "" : "";
    public async Task WriteAsync(SqlDatabaseSource source, SqlCatalog catalog, string revision, CancellationToken ct)
    {
        var root=Path.Combine(Path.GetTempPath(),"codemap-sql",Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var objectList=new List<object>();
            foreach (var row in catalog.Objects)
            {
                var schema=Get(row,"schema_name"); var name=Get(row,"object_name");
                var type=Get(row,"object_type"); var def=Get(row,"sql_definition");
                var relative=$"objects/{Safe(schema)}/{Safe(name)}_{Safe(type)}.sql";
                if (!string.IsNullOrWhiteSpace(def))
                {
                    var path=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path,def,ct);
                }
                objectList.Add(new {schema,name,type,definitionPath=string.IsNullOrWhiteSpace(def)?null:relative,
                    columns=catalog.Columns.Where(c=>Get(c,"schema_name")==schema && Get(c,"object_name")==name).ToArray()});
            }
            await File.WriteAllTextAsync(Path.Combine(root,"schema.json"),JsonSerializer.Serialize(new
            {
                database=catalog.DatabaseName,server=catalog.ServerName,objects=objectList,
                foreignKeys=catalog.ForeignKeys,dependencies=catalog.Dependencies,indexes=catalog.Indexes
            },new JsonSerializerOptions{WriteIndented=true}),ct);
            var (_,files)=await SourceHasher.CalculateAsync(root,new CodeMapSync.Options.SourceDefinition(),ct);
            var manifest=new SourceManifest{SourceId=source.Id,SourceType="sql",AnalyzerVersion="sql-compact-v6",
                NativeRevision=revision,ContentHash=revision,SyncedAtUtc=DateTimeOffset.UtcNow,Files=files};
            await store.UploadAsync(source.Id,root,manifest,ct);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root,true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning("SQL snapshot temporary cleanup failed: {0}", ex.Message); }
        }
    }
}
