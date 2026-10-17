using System.Text.RegularExpressions;
using CodeMapSync.Models;

namespace CodeMapSync.Analysis;

/// <summary>Resolves RDL dataset query references to SQL objects, with database-aware confidence.</summary>
public sealed class RdlSqlRelationshipResolver
{
    public int Resolve(IReadOnlyCollection<CodeMapDocument> maps)
    {
        var sql = maps.SelectMany(m => m.Entries).Where(e => e.Language == "sql" && e.Kind is "sql_table" or "sql_view" or "sql_procedure" or "sql_function").ToArray();
        var added = 0;
        foreach (var dataset in maps.SelectMany(m => m.Entries).Where(e => e.Kind == "rdl_dataset"))
        foreach (var attr in dataset.Attributes.Where(a => a.Key.StartsWith("sqlProcedure:", StringComparison.OrdinalIgnoreCase) || a.Key.StartsWith("sqlReference:", StringComparison.OrdinalIgnoreCase)))
        {
            var procedure = attr.Key.StartsWith("sqlProcedure:", StringComparison.OrdinalIgnoreCase);
            var name = attr.Key[(procedure ? "sqlProcedure:" : "sqlReference:").Length..].Replace("[", "").Replace("]", "").Trim();
            if (name.Length == 0 || name.StartsWith("#") || name.StartsWith("@")) continue;
            var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var shortName = parts[^1];
            var schema = parts.Length >= 2 ? parts[^2] : null;
            var db = parts.Length >= 3 ? parts[^3] : dataset.DatabaseName;
            var matches = sql.Where(e => (procedure ? e.Kind == "sql_procedure" : e.Kind is "sql_table" or "sql_view" or "sql_procedure" or "sql_function") &&
                e.Key.Split('.').Last().Equals(shortName, StringComparison.OrdinalIgnoreCase) &&
                (schema is null || string.Equals(e.SchemaName, schema, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(db) || string.IsNullOrWhiteSpace(e.DatabaseName) || string.Equals(e.DatabaseName, db, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (matches.Length == 0)
            {
                if (!dataset.Evidence.Any(e => e.Detail == $"Unresolved RDL SQL reference: {name}"))
                    dataset.Evidence.Add(new CodeEvidence { FilePath = dataset.FilePath, LineIndex = dataset.StartLineIndex, Detail = $"Unresolved RDL SQL reference: {name}" });
                continue;
            }
            foreach (var target in matches)
            {
                var status = matches.Length == 1 ? "matched" : "candidate";
                var type = procedure ? "rdl_executes_procedure" : "rdl_queries_sql_object";
                if (dataset.Relations.Any(r => r.TargetId == target.UniqueId && r.RelationType == type)) continue;
                dataset.Relations.Add(new CodeMapRelation { TargetId = target.UniqueId, RelationType = type, Status = status,
                    DiscoveryMethod = attr.Value, MatchStrength = matches.Length == 1 && schema is not null ? "strong" : "weak",
                    MatchScore = matches.Length == 1 ? (schema is not null ? 0.95 : 0.8) : 0.4 });
                if (!dataset.RelatedMapObjectIds.Contains(target.UniqueId)) dataset.RelatedMapObjectIds.Add(target.UniqueId);
                added++;
            }
        }
        return added;
    }
}
