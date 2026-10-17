using CodeMapSync.Models;
using System.Security.Cryptography;
using System.Text;

namespace CodeMapSync.Sql;

public sealed class SqlCodeMapBuilder
{
    private readonly SqlLineageAnalyzer _lineage = new();
    private static string Value(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var v) ? Convert.ToString(v) ?? "" : "";
    private static string Id(string source, string schema, string name) => $"sql:{source}:{schema}.{name}";
    private static void Link(CodeMapEntry from, CodeMapEntry to, string type, string status, string method)
    {
        if (!from.RelatedMapObjectIds.Contains(to.UniqueId)) from.RelatedMapObjectIds.Add(to.UniqueId);
        if (!from.Relations.Any(r => r.TargetId == to.UniqueId && r.RelationType == type))
            from.Relations.Add(new CodeMapRelation { TargetId=to.UniqueId, RelationType=type, Status=status, DiscoveryMethod=method });
    }
    public CodeMapDocument Build(SqlDatabaseSource source, SqlCatalog catalog)
    {
        var map = new CodeMapDocument { SchemaVersion="1.2", SourceId=source.Id,
            ProjectId=source.Id, ProjectName=source.Name, GeneratedAtUtc=DateTimeOffset.UtcNow };
        var byName = new Dictionary<string, CodeMapEntry>(StringComparer.OrdinalIgnoreCase);
        var byObjectId = new Dictionary<string, CodeMapEntry>();
        foreach (var row in catalog.Objects)
        {
            var schema=Value(row,"schema_name"); var name=Value(row,"object_name");
            var kind=Value(row,"object_type") switch { "U"=>"sql_table", "V"=>"sql_view", "P"=>"sql_procedure",
                "FN" or "IF" or "TF"=>"sql_function", "TR"=>"sql_trigger", _=>"sql_object" };
            var e = new CodeMapEntry { UniqueId=Id(source.Id,schema,name),ProjectId=source.Id,
                ProjectName=source.Name, Key=$"{schema}.{name}",Label=$"{schema}.{name}",Kind=kind,Language="sql",
                DatabaseName=catalog.DatabaseName, SchemaName=schema, TableName=kind=="sql_table"?name:null,
                SearchKeys=[name,$"{schema}.{name}"],ResolutionStatus="discovered" };
            e.Attributes["objectType"]=Value(row,"object_type");
            e.Attributes["modifiedAt"]=Value(row,"modify_date");
            var definition=Value(row,"sql_definition");
            if (definition.Length>0)
            {
                // SQL definitions may contain literals or sensitive business logic. Store in private Blob only.
                e.Attributes["definition"]=definition;
                e.Attributes["definitionAvailable"]="true";
            }
            else if (kind is "sql_procedure" or "sql_view" or "sql_function" or "sql_trigger")
                e.Attributes["definitionAvailable"]="false";
            map.Entries.Add(e);
            byName[$"{schema}.{name}"]=e;
            byObjectId[Value(row,"object_id")]=e;
        }
        // Columns belong to their table/view entry; do not create standalone
        // sql_column, sql_index, FK or identity map objects.
        foreach (var row in catalog.Columns)
        {
            if (!byObjectId.TryGetValue(Value(row,"object_id"), out var parent)) continue;
            var columnName = Value(row,"column_name");
            if (columnName.Length == 0) continue;
            var parts = new List<string> { "name=" + columnName };
            foreach (var key in new[] { "data_type", "max_length", "precision", "scale", "is_nullable", "is_identity", "is_computed" })
                parts.Add(key + "=" + Value(row,key));
            parent.Attributes["column:" + columnName] = string.Join(";", parts);
            // Column names are searchable only in combination with their parent object.
            var qualifiedColumn = parent.Key + "." + columnName;
            if (!parent.SearchKeys.Contains(qualifiedColumn, StringComparer.OrdinalIgnoreCase))
                parent.SearchKeys.Add(qualifiedColumn);
        }
        // FK information is retained as compact evidence, without creating FK objects
        // or relationships between tables solely because of a foreign key.
        foreach (var row in catalog.ForeignKeys)
        {
            if (!byName.TryGetValue(Value(row,"source_schema")+"."+Value(row,"source_table"), out var parent)) continue;
            parent.Evidence.Add(new CodeEvidence { Detail = "FK " + Value(row,"constraint_name") + ": " +
                Value(row,"source_column") + " -> " + Value(row,"target_schema") + "." +
                Value(row,"target_table") + "." + Value(row,"target_column") });
        }
        foreach (var row in catalog.Dependencies)
        {
            if (!byObjectId.TryGetValue(Value(row,"referencing_id"),out var from)) continue;
            CodeMapEntry? to=null;
            var targetId=Value(row,"referenced_id");
            if (targetId.Length>0) byObjectId.TryGetValue(targetId,out to);
            var externalDb=Value(row,"referenced_database_name");
            if (to is null && externalDb.Length==0)
            {
                var schema=Value(row,"referenced_schema_name");
                if (schema.Length>0) byName.TryGetValue(schema+"."+Value(row,"referenced_entity_name"),out to);
            }
            if (to is not null)
            {
                var relationType = from.Kind=="sql_procedure" && to.Kind=="sql_procedure" ? "sql_calls" : "sql_references";
                Link(from,to,relationType,"matched","sys.sql_expression_dependencies");
            }
            else from.Evidence.Add(new CodeEvidence { Detail=$"Unresolved SQL dependency: {externalDb}.{Value(row,"referenced_schema_name")}.{Value(row,"referenced_entity_name")}" });
        }
        // Statement-level, conservative data lineage. Static SQL dependencies alone do
        // not tell which source tables contribute to a specific INSERT/UPDATE target.
        foreach (var row in catalog.Objects)
        {
            if (!byObjectId.TryGetValue(Value(row,"object_id"), out var module)) continue;
            if (module.Kind is not ("sql_procedure" or "sql_trigger" or "sql_function")) continue;
            var definition = Value(row,"sql_definition");
            if (string.IsNullOrWhiteSpace(definition)) continue;
            var analysis = _lineage.Analyze(definition);
            foreach (var warning in analysis.Warnings)
                module.Evidence.Add(new CodeEvidence { Detail=warning });
            foreach (var relation in analysis.Links)
            {
                CodeMapEntry? Resolve(string name)
                {
                    if (byName.TryGetValue(name,out var exact)) return exact;
                    if (!name.Contains('.'))
                    {
                        // A schema-less reference is safe only if unique in this database.
                        var matches=byName.Values.Where(e =>
                            e.Key.EndsWith("."+name,StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (matches.Length==1) return matches[0];
                    }
                    return null;
                }
                var from=Resolve(relation.From);
                var to=Resolve(relation.To);
                if (relation.Type=="statement_writes")
                {
                    if (to is not null) Link(module,to,"sql_writes","candidate","sql-static-lineage");
                    continue;
                }
                if (relation.Type=="statement_reads")
                {
                    if (from is not null) Link(module,from,"sql_reads","candidate","sql-static-lineage");
                    continue;
                }
                if (from is null || to is null) continue;
                Link(from,to,relation.Type,"candidate","sql-static-lineage");
                from.Evidence.Add(new CodeEvidence { Detail=$"Via {module.Key}: {relation.Evidence}" });
                Link(module,from,"sql_reads","candidate","sql-static-lineage");
                Link(module,to,"sql_writes","candidate","sql-static-lineage");
            }
        }
        // Hash excludes timestamps; no raw connection strings are ever persisted.
        var canonical="sql-compact-v6\n"+string.Join("\n",catalog.Objects.Select(r=>Value(r,"schema_name")+"."+Value(r,"object_name")+":"+Value(r,"sql_definition")))+
            string.Join("\n",catalog.Columns.Select(r=>Value(r,"object_id")+":"+Value(r,"column_name")+":"+Value(r,"data_type")))+
            string.Join("\n",catalog.ForeignKeys.Select(r=>Value(r,"constraint_name")+":"+Value(r,"source_column")+":"+Value(r,"target_column")))+
            string.Join("\n",catalog.Dependencies.Select(r=>Value(r,"referencing_id")+":"+Value(r,"referenced_id")))+
            string.Join("\n",catalog.Indexes.Select(r=>Value(r,"object_id")+":"+Value(r,"index_name")+":"+Value(r,"column_name")));
        map.SourceRevision=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return map;
    }
}
