using System.Text.RegularExpressions;

namespace CodeMapSync.Sql;

// Conservative static lineage analysis. This is NOT a complete T-SQL parser.
// DML statements and temporary-table propagation are handled independently to avoid
// incorrectly joining every table referenced anywhere in a procedure.
public sealed class SqlLineageAnalyzer
{
    public sealed record Link(string From, string To, string Type, string Evidence, bool Certain = false);
    public sealed record Analysis(IReadOnlyList<Link> Links, IReadOnlyList<string> Warnings);

    private const string Name = @"(?:\[[^\]]+\]|[#@]?[A-Za-z_][\w#$@]*)(?:\s*\.\s*(?:\[[^\]]+\]|[A-Za-z_][\w$#]*)){0,2}";
    private static readonly Regex ReadTable = new(@"\b(?:FROM|JOIN|APPLY)\s+(?<name>" + Name + @")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Target = new(@"\b(?<op>INSERT\s+(?:INTO\s+)?|MERGE\s+(?:INTO\s+)?|UPDATE\s+|DELETE\s+FROM\s+|SELECT\s+INTO\s+)(?<name>" + Name + @")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SelectInto = new(@"\bSELECT\b(?:(?!\bFROM\b|;)[\s\S]){0,4000}?\bINTO\s+(?<name>" + Name + @")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Cte = new(@"\b(?:WITH|,)\s*(?<name>[A-Za-z_]\w*)\s*(?:\([^)]*\))?\s+AS\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BlockComment = new(@"/\*[\s\S]*?\*/", RegexOptions.Compiled);
    private static readonly Regex LineComment = new(@"--[^\r\n]*", RegexOptions.Compiled);
    private static readonly Regex Strings = new(@"N?'(?:''|[^'])*'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Boundary = new(@"\b(?:INSERT\s+(?:INTO\s+)?|UPDATE\s+|DELETE\s+FROM\s+|MERGE\s+(?:INTO\s+)?|SELECT\s+|EXEC(?:UTE)?\s+)" , RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Analysis Analyze(string definition)
    {
        var warnings = new List<string>();
        var links = new List<Link>();
        if (string.IsNullOrWhiteSpace(definition)) return new(links, warnings);
        // Preserve offsets when removing comments and literals, preventing phantom table names.
        var clean = Mask(Mask(Mask(definition, BlockComment), LineComment), Strings);
        if (Regex.IsMatch(clean, @"\b(?:EXEC|EXECUTE)\s*\(?\s*[@(]|\bsp_executesql\b", RegexOptions.IgnoreCase))
            warnings.Add("Dynamic SQL / indirect execution may hide dependencies.");
        if (Regex.IsMatch(clean, @"\b(?:OPENQUERY|OPENROWSET|BULK|PIVOT|UNPIVOT)\b", RegexOptions.IgnoreCase))
            warnings.Add("Advanced SQL syntax requires manual verification.");

        var matches = Target.Matches(clean).Cast<Match>()
            .Concat(SelectInto.Matches(clean).Cast<Match>())
            .OrderBy(m=>m.Index).ToArray();
        var cteNames = Cte.Matches(clean).Cast<Match>().Select(m => Normalize(m.Groups["name"].Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tempSources = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var targetMatch in matches)
        {
            var op = targetMatch.Groups["op"].Success
                ? Regex.Replace(targetMatch.Groups["op"].Value.Trim(), @"\s+", " ").ToUpperInvariant()
                : "SELECT INTO";
            var target = Normalize(targetMatch.Groups["name"].Value);
            if (target.Length == 0) continue;
            // A statement extends until the next write operation or a semicolon.
            var start = targetMatch.Index + targetMatch.Length;
            var end = matches.FirstOrDefault(m => m.Index > targetMatch.Index)?.Index ?? clean.Length;
            var semi = clean.IndexOf(';', start);
            if (semi >= 0 && semi < end) end = semi;
            var fragment = clean[start..end];
            var reads = ReadTable.Matches(fragment).Cast<Match>()
                .Select(m => Normalize(m.Groups["name"].Value))
                .Where(x => x.Length > 0 && !cteNames.Contains(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // UPDATE alias FROM table and SELECT INTO table FROM ... are recognized.
            // For a CTE, a narrow fallback includes its underlying tables as candidates.
            if (ReadTable.Matches(fragment).Cast<Match>().Any(m => cteNames.Contains(Normalize(m.Groups["name"].Value))))
            {
                var cteRead = ReadTable.Matches(clean[..targetMatch.Index]).Cast<Match>()
                    .Select(m => Normalize(m.Groups["name"].Value)).Where(x => !cteNames.Contains(x));
                reads.UnionWith(cteRead);
                warnings.Add("CTE lineage inferred conservatively; verify complex CTE scopes.");
            }
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var read in reads)
            {
                if (tempSources.TryGetValue(read, out var origins)) expanded.UnionWith(origins);
                else if (!read.StartsWith('#') && !read.StartsWith('@')) expanded.Add(read);
            }
            var isTemp = target.StartsWith('#') || target.StartsWith('@');
            if (isTemp)
            {
                if (!tempSources.TryGetValue(target, out var old)) tempSources[target] = old = new(StringComparer.OrdinalIgnoreCase);
                old.UnionWith(expanded);
            }
            else
            {
                foreach (var read in expanded.Where(x => !x.Equals(target, StringComparison.OrdinalIgnoreCase)))
                    links.Add(new(read, target, "data_flows_to", $"{op}: {read} -> {target}; intermediate objects resolved when possible"));
                if (reads.Count == 0 && op.StartsWith("UPDATE"))
                    warnings.Add($"UPDATE {target}: no explicit FROM/JOIN source resolved.");
            }
            foreach (var read in reads.Where(x => !x.StartsWith('#') && !x.StartsWith('@')))
                links.Add(new(read, target, "statement_reads", $"{op} statement reads {read}"));
            if (!isTemp) links.Add(new(target, target, "statement_writes", $"{op} writes {target}"));
            // Join sources in the same write statement participate in one derivation.
            var arr = expanded.ToArray();
            if (!isTemp && arr.Length > 1)
                for (var i = 0; i < arr.Length; i++)
                    for (var j = i + 1; j < arr.Length; j++)
                        links.Add(new(arr[i], arr[j], "joined_with", $"Join/source co-occurrence while populating {target}"));
        }
        // SELECT-only modules (views/functions) are handled by the builder's catalog dependencies.
        return new(links.DistinctBy(l => (l.From.ToLowerInvariant(), l.To.ToLowerInvariant(), l.Type, l.Evidence)).ToArray(), warnings.Distinct().ToArray());
    }

    private static string Mask(string input, Regex pattern) => pattern.Replace(input, m => new string(' ', m.Length));
    public static string Normalize(string name) => Regex.Replace(name.Trim(), @"[\[\]\s]", "").TrimEnd(';');
}
