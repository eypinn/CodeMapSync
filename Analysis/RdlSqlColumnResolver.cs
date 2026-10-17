using System.Text.RegularExpressions;
using CodeMapSync.Models;

namespace CodeMapSync.Analysis;

/// <summary>
/// Connects RDL DataField names to SQL result columns and catalog columns.
/// SQL columns intentionally remain search keys/attributes on their parent SQL object.
/// Edges carry the column name in DiscoveryMethod and are indexed in both directions.
/// </summary>
public sealed class RdlSqlColumnResolver
{
    private static readonly Regex Select = new(@"\bSELECT\s+(?:DISTINCT\s+|TOP\s*\([^)]*\)\s+|TOP\s+\d+\s+)*(?<columns>[\s\S]*?)\s+\bFROM\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Alias = new(@"\bAS\s+\[?(?<alias>[A-Za-z_]\w*)\]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"^(?:\[?(?<prefix>[A-Za-z_]\w*)\]?\s*\.\s*)?\[?(?<name>[A-Za-z_]\w*)\]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ColumnReference = new(@"(?:(?<prefix>\[?[A-Za-z_]\w*\]?)\s*\.\s*)?\[?(?<name>[A-Za-z_]\w*)\]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FromJoin = new(@"\b(?:FROM|JOIN)\s+(?<table>(?:\[?\w+\]?\.){0,2}\[?\w+\]?)\s*(?:(?:AS\s+)?(?<alias>(?!ON\b|WHERE\b|JOIN\b|INNER\b|LEFT\b|RIGHT\b|FULL\b|CROSS\b|GROUP\b|ORDER\b)[A-Za-z_]\w*))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> SqlWords = new(StringComparer.OrdinalIgnoreCase) { "AS", "CASE", "WHEN", "THEN", "ELSE", "END", "NULL", "IS", "NOT", "AND", "OR", "DISTINCT", "CAST", "CONVERT", "ISNULL", "COALESCE", "SUM", "COUNT", "MIN", "MAX", "AVG", "OVER", "PARTITION", "BY", "DESC", "ASC", "IIF", "NULLIF", "ROUND", "ABS", "GETDATE", "YEAR", "MONTH", "DAY", "DATEADD", "DATEDIFF", "TRY_CAST", "TRY_CONVERT", "N", "TRUE", "FALSE" };

    public int Resolve(IReadOnlyCollection<CodeMapDocument> maps)
    {
        var entries = maps.SelectMany(m => m.Entries).ToArray();
        var byId = entries.GroupBy(e => e.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var sql = entries.Where(e => e.Language == "sql").ToArray();
        var added = 0;
        foreach (var field in entries.Where(e => e.Kind == "rdl_field"))
        {
            if (!field.Attributes.TryGetValue("dataField", out var dataField) || string.IsNullOrWhiteSpace(dataField)) continue;
            var dataset = field.Relations.Where(r => r.RelationType == "rdl_field_of_dataset")
                .Select(r => byId.GetValueOrDefault(r.TargetId)).FirstOrDefault(e => e is not null);
            if (dataset is null) continue;
            var sqlTargets = dataset.Relations.Where(r => r.RelationType is "rdl_executes_procedure" or "rdl_queries_sql_object")
                .Select(r => (Relation: r, Entry: byId.GetValueOrDefault(r.TargetId)))
                .Where(x => x.Entry is not null).ToArray();
            foreach (var (relation, module) in sqlTargets)
            {
                if (module is null) continue;
                // The dataset field comes from this SQL result set, even if its expression is not statically resolvable.
                added += Link(field, module, "rdl_field_from_sql_result", relation.Status,
                    $"RDL DataField={dataField}; SQL result of {module.Key}");
                if (module.Kind is "sql_table" or "sql_view")
                {
                    if (HasColumn(module, dataField))
                        added += Link(field, module, "rdl_field_from_sql_column", "matched", $"SQL column={module.Key}.{dataField}; catalog-column-match");
                    continue;
                }
                var definition = module.Attributes.GetValueOrDefault("definition") ?? "";
                if (definition.Length == 0) continue;
                // A procedure can have multiple SELECT result sets; never claim an exact match from name alone.
                var projections = ExtractProjections(definition, dataField);
                foreach (var projection in projections)
                {
                    var detail = $"SQL {module.Key} output {dataField}: {projection.Expression}";
                    if (!field.Evidence.Any(e => e.Detail == detail))
                        field.Evidence.Add(new CodeEvidence { Detail = detail });
                    var aliases = FromJoin.Matches(projection.Sql).Cast<Match>()
                        .Select(m => (Table: Normalize(m.Groups["table"].Value), Alias: m.Groups["alias"].Value))
                        .ToArray();
                    var candidates = sql.Where(e => e.Kind is "sql_table" or "sql_view" &&
                        (module.DatabaseName is null || e.DatabaseName is null || module.DatabaseName.Equals(e.DatabaseName, StringComparison.OrdinalIgnoreCase)) &&
                        aliases.Any(a => a.Table.Equals(e.Key, StringComparison.OrdinalIgnoreCase) ||
                            a.Table.Equals(e.Key.Split('.').Last(), StringComparison.OrdinalIgnoreCase))).ToArray();
                    var refs = ColumnReference.Matches(projection.Expression).Cast<Match>()
                        .Where(m => !SqlWords.Contains(m.Groups["name"].Value))
                        .Select(m => (Prefix: m.Groups["prefix"].Value.Trim('[', ']'), Name: m.Groups["name"].Value))
                        .Distinct().ToArray();
                    foreach (var reference in refs)
                    {
                        var columnMatches = candidates.Where(c => HasColumn(c, reference.Name) &&
                            (reference.Prefix.Length == 0 || aliases.Any(a =>
                                (a.Alias.Equals(reference.Prefix, StringComparison.OrdinalIgnoreCase) ||
                                 a.Table.Split('.').Last().Equals(reference.Prefix, StringComparison.OrdinalIgnoreCase)) &&
                                (a.Table.Equals(c.Key, StringComparison.OrdinalIgnoreCase) ||
                                 a.Table.Equals(c.Key.Split('.').Last(), StringComparison.OrdinalIgnoreCase))))).ToArray();
                        var status = columnMatches.Length == 1 && projections.Count == 1 ? "matched" : "candidate";
                        foreach (var target in columnMatches)
                            added += Link(field, target, "rdl_field_depends_on_sql_column", status,
                                $"SQL column={target.Key}.{reference.Name}; SP={module.Key}; output={dataField}; expression={projection.Expression}");
                    }
                }
            }
        }
        return added;
    }

    private static bool HasColumn(CodeMapEntry entry, string column) =>
        entry.Attributes.Keys.Any(k => k.Equals("column:" + column, StringComparison.OrdinalIgnoreCase)) ||
        entry.SearchKeys.Any(k => k.Equals(entry.Key + "." + column, StringComparison.OrdinalIgnoreCase));

    private static int Link(CodeMapEntry from, CodeMapEntry to, string type, string status, string detail)
    {
        if (from.Relations.Any(r => r.TargetId.Equals(to.UniqueId, StringComparison.OrdinalIgnoreCase) &&
                                    r.RelationType == type && r.DiscoveryMethod == detail)) return 0;
        from.Relations.Add(new CodeMapRelation { TargetId = to.UniqueId, RelationType = type, Status = status,
            DiscoveryMethod = detail, MatchStrength = status == "matched" ? "strong" : "weak" });
        if (!from.RelatedMapObjectIds.Contains(to.UniqueId, StringComparer.OrdinalIgnoreCase)) from.RelatedMapObjectIds.Add(to.UniqueId);
        return 1;
    }

    private sealed record Projection(string Sql, string Expression);
    private static List<Projection> ExtractProjections(string definition, string fieldName)
    {
        var result = new List<Projection>();
        foreach (Match select in Select.Matches(definition))
        {
            foreach (var expression in SplitColumns(select.Groups["columns"].Value))
            {
                var value = expression.Trim();
                var alias = Alias.Match(value);
                var name = alias.Success ? alias.Groups["alias"].Value : Identifier.Match(value).Groups["name"].Value;
                if (!name.Equals(fieldName, StringComparison.OrdinalIgnoreCase)) continue;
                var source = alias.Success ? value[..alias.Index].Trim() : value;
                // Scope aliases to the matching SELECT's FROM/JOIN portion, not the whole procedure.
                var tail = definition[select.Index..];
                var end = tail.IndexOf(';');
                var scoped = end >= 0 ? tail[..end] : tail;
                result.Add(new Projection(scoped, source));
            }
        }
        return result;
    }
    private static string Normalize(string name) => Regex.Replace(name, @"[\[\]\s]", "");
    private static IEnumerable<string> SplitColumns(string text)
    {
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\'')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '\'') { i++; continue; }
                quoted = !quoted;
            }
            if (quoted) continue;
            if (text[i] == '(') depth++;
            if (text[i] == ')') depth = Math.Max(0, depth - 1);
            if (text[i] == ',' && depth == 0) { yield return text[start..i]; start = i + 1; }
        }
        yield return text[start..];
    }
}
