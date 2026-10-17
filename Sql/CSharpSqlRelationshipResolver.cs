using CodeMapSync.Models;

namespace CodeMapSync.Sql;

/// <summary>Connects discovered C# SQL references to table, view, procedure and function
/// entries using their Key/SearchKeys. No SQL is executed.</summary>
public sealed class CSharpSqlRelationshipResolver
{
    public sealed record ResolutionStats(int References, int MatchedReferences, int AddedRelations,
        int MissingReferences, int AmbiguousReferences, int SqlObjects);

    public ResolutionStats LastStats { get; private set; } = new(0, 0, 0, 0, 0, 0);

    private static readonly string[] Prefixes = ["efTable:", "efView:", "dapperProcedure:", "sqlProcedure:",
        "dapperSql:", "sqlReference:", "sqlFunction:"];

    public int Resolve(IReadOnlyCollection<CodeMapDocument> maps)
    {
        var sql = maps.SelectMany(m => m.Entries)
            .Where(e => GetSqlType(e) is not null)
            .GroupBy(e => e.UniqueId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).ToArray();
        var references = 0; var matched = 0; var added = 0; var missing = 0; var ambiguous = 0;
        foreach (var entry in maps.SelectMany(m => m.Entries)
            .Where(e => string.Equals(e.Language, "csharp", StringComparison.OrdinalIgnoreCase)))
        foreach (var attr in entry.Attributes)
        {
            var prefix = Prefixes.FirstOrDefault(p => attr.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (prefix is null) continue;
            var requested = Normalize(attr.Key[prefix.Length..]);
            if (requested.Length == 0) continue;
            references++;
            var kinds = prefix switch
            {
                "efTable:" => new[] { "table", "view" },
                "efView:" => new[] { "view" },
                "dapperProcedure:" or "sqlProcedure:" => new[] { "procedure" },
                "sqlFunction:" => new[] { "function" },
                _ => new[] { "table", "view", "procedure", "function" }
            };
            var type = prefix switch
            {
                "efTable:" or "efView:" => "maps_to",
                "dapperProcedure:" or "sqlProcedure:" => "calls",
                "sqlFunction:" => "references",
                _ => "references"
            };
            var candidates = sql.Where(e => GetSqlType(e) is { } sqlType && kinds.Contains(sqlType)).ToArray();
            var matches = candidates.Where(e => Names(e).Contains(requested, StringComparer.OrdinalIgnoreCase)).ToArray();
            // Fallback for EF naming conventions, only when no explicit match exists.
            if (matches.Length == 0 && prefix == "efTable:" &&
                attr.Value is "efcore-dbset-convention" or "efcore-entity-mapping" &&
                !requested.Contains('.'))
            {
                var alternate = requested.EndsWith("ies", StringComparison.OrdinalIgnoreCase)
                    ? requested[..^3] + "y" : requested.EndsWith("y", StringComparison.OrdinalIgnoreCase)
                    ? requested[..^1] + "ies" : requested.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                    ? requested[..^1] : requested + "s";
                matches = candidates.Where(e => Names(e).Contains(alternate, StringComparer.OrdinalIgnoreCase)).ToArray();
            }
            if (matches.Length == 0)
            {
                missing++;
                Evidence(entry, $"SQL-MATCH missing: {prefix}{requested}; SQL candidates={candidates.Length}");
                continue;
            }
            if (matches.Length > 1)
            {
                ambiguous++;
                Evidence(entry, $"SQL-MATCH multiple candidates: {prefix}{requested}; targets={string.Join(",", matches.Select(m => m.UniqueId))}");
            }
            matched++;
            // Multiple databases may have the same object. Preserve candidate links
            // instead of dropping all of them; never mark an ambiguous link as confirmed.
            foreach (var target in matches)
            {
                if (entry.Relations.Any(r => r.TargetId == target.UniqueId && r.RelationType == type)) continue;
                entry.Relations.Add(new CodeMapRelation
                {
                    TargetId = target.UniqueId, RelationType = type, Status = "candidate",
                    DiscoveryMethod = attr.Value,
                    MatchStrength = matches.Length == 1 &&
                        (requested.Contains('.') || attr.Value == "efcore-fluent-api-mapping") ? "strong" : "weak",
                    MatchScore = matches.Length == 1 ?
                        (requested.Contains('.') || attr.Value == "efcore-fluent-api-mapping" ? 0.9 : 0.75) : 0.4
                });
                if (!entry.RelatedMapObjectIds.Contains(target.UniqueId)) entry.RelatedMapObjectIds.Add(target.UniqueId);
                added++;
            }
        }
        LastStats = new(references, matched, added, missing, ambiguous, sql.Length);
        return added;
    }

    private static string? GetSqlType(CodeMapEntry entry)
    {
        if (!string.Equals(entry.Language, "sql", StringComparison.OrdinalIgnoreCase)) return null;
        if (entry.Attributes.TryGetValue("objectType", out var type))
        {
            return type.Trim().ToUpperInvariant() switch
            {
                "U" => "table", "V" => "view", "P" or "PC" => "procedure",
                "FN" or "IF" or "TF" or "FS" or "FT" => "function", _ => null
            };
        }
        return entry.Kind.ToLowerInvariant() switch
        {
            "sql_table" => "table", "sql_view" => "view",
            "sql_procedure" => "procedure", "sql_function" => "function", _ => null
        };
    }

    private static IEnumerable<string> Names(CodeMapEntry entry)
    {
        var schema = Normalize(entry.SchemaName ?? "");
        foreach (var name in entry.SearchKeys.Append(entry.Key))
        {
            var normalized = Normalize(name);
            if (normalized.Length == 0 || normalized.Equals(schema, StringComparison.OrdinalIgnoreCase)) continue;
            yield return normalized;
        }
        // Legacy SQL snapshots did not always include the unqualified alias.
        var key = Normalize(entry.Key);
        if (key.Contains('.')) yield return key[(key.LastIndexOf('.') + 1)..];
    }
    private static void Evidence(CodeMapEntry entry, string detail)
    {
        if (!entry.Evidence.Any(e => e.Detail == detail))
            entry.Evidence.Add(new CodeEvidence { FilePath = entry.FilePath, Detail = detail });
    }
    private static string Normalize(string name) => name.Replace("[", "").Replace("]", "").Trim();
}
