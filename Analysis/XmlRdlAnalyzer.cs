using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CodeMapSync.Models;

namespace CodeMapSync.Analysis;

/// <summary>Static, namespace-tolerant SSRS RDL/RDLC lineage extraction. Does not execute reports.</summary>
public sealed class XmlRdlAnalyzer : ICodeAnalyzer
{
    private static readonly Regex FieldRef = new(@"\bFields!([A-Za-z_][\w]*)\.Value\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ParameterRef = new(@"\bParameters!([A-Za-z_][\w]*)\.Value\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VariableRef = new(@"\bVariables!([A-Za-z_][\w]*)\.Value\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReportItemRef = new(@"\bReportItems!([A-Za-z_][\w]*)\.Value\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LookupDataset = new(@"\bLookup(?:Set)?\s*\([\s\S]*?,\s*""(?<name>[^""\r\n]+)""\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SqlObject = new(@"\b(?:FROM|JOIN|APPLY|UPDATE|INTO|EXEC(?:UTE)?)\s+(?<name>(?:\[[^\]]+\]|[A-Za-z_]\w*)(?:\s*\.\s*(?:\[[^\]]+\]|[A-Za-z_]\w*)){0,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public bool Supports(string path) => path.EndsWith(".rdl", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rdlc", StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<XElement> Children(XElement parent, string name) => parent.Elements().Where(x => x.Name.LocalName == name);
    private static XElement? Child(XElement parent, string name) => Children(parent, name).FirstOrDefault();
    private static string Val(XElement? element, string child) => element is null ? "" : Child(element, child)?.Value.Trim() ?? "";
    private static int Line(XElement el) => el is IXmlLineInfo info && info.HasLineInfo() ? Math.Max(0, info.LineNumber - 1) : 0;
    private static string Normal(string name) => Regex.Replace(name.Replace("[", "").Replace("]", ""), @"\s*\.\s*", ".").Trim();
    private static void Link(CodeMapEntry from, CodeMapEntry to, string type, string discovery, string status = "matched")
    {
        if (from.Relations.Any(r => r.TargetId == to.UniqueId && r.RelationType == type)) return;
        from.Relations.Add(new CodeMapRelation { TargetId = to.UniqueId, RelationType = type, Status = status, DiscoveryMethod = discovery, MatchStrength = status == "matched" ? "strong" : "weak" });
        if (!from.RelatedMapObjectIds.Contains(to.UniqueId)) from.RelatedMapObjectIds.Add(to.UniqueId);
    }
    public IEnumerable<CodeMapEntry> Analyze(string sourceId, string project, string path, string content)
    {
        XDocument xml;
        try { xml = XDocument.Parse(content, LoadOptions.SetLineInfo); }
        catch (Exception) { return []; }
        var root = xml.Root;
        if (root is null || root.Name.LocalName != "Report") return [];
        var entries = new List<CodeMapEntry>();
        var reportKey = Path.GetFileNameWithoutExtension(path);
        CodeMapEntry Add(XElement el, string kind, string key)
        {
            var e = MapIds.New(sourceId, project, path, "rdl", kind, reportKey + ":" + key, Line(el));
            e.Label = key;
            e.Attributes["report"] = reportKey;
            entries.Add(e);
            return e;
        }
        var report = Add(root, "rdl_report", reportKey);
        report.Attributes["rdlNamespace"] = root.Name.NamespaceName;
        report.Attributes["description"] = Val(root, "Description");
        var sources = new Dictionary<string, CodeMapEntry>(StringComparer.OrdinalIgnoreCase);
        var datasets = new Dictionary<string, CodeMapEntry>(StringComparer.OrdinalIgnoreCase);
        var fields = new Dictionary<string, CodeMapEntry>(StringComparer.OrdinalIgnoreCase);
        var parameters = new Dictionary<string, CodeMapEntry>(StringComparer.OrdinalIgnoreCase);
        var variables = new Dictionary<string, CodeMapEntry>(StringComparer.OrdinalIgnoreCase);
        var items = new Dictionary<string, CodeMapEntry>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(CodeMapEntry entry, string expression, string? dataset, string reason)>();
        foreach (var ds in Children(Child(root, "DataSources") ?? root, "DataSource"))
        {
            var name = (string?)ds.Attribute("Name") ?? "";
            if (name.Length == 0) continue;
            var entry = Add(ds, "rdl_datasource", name);
            var cp = Child(ds, "ConnectionProperties");
            var connection = Val(cp, "ConnectString");
            entry.Attributes["provider"] = Val(cp, "DataProvider");
            entry.Attributes["dataSourceReference"] = Val(ds, "DataSourceReference");
            // Avoid publishing credentials embedded in connection strings.
            var db = Regex.Match(connection, @"(?:Initial\s+Catalog|Database)\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
            if (db.Success) entry.DatabaseName = db.Groups[1].Value.Trim();
            var server = Regex.Match(connection, @"(?:Data\s+Source|Server)\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
            if (server.Success) entry.DatabaseServer = server.Groups[1].Value.Trim();
            sources[name] = entry;
            Link(report, entry, "rdl_uses_datasource", "DataSources/DataSource");
        }
        foreach (var ds in Children(Child(root, "DataSets") ?? root, "DataSet"))
        {
            var name = (string?)ds.Attribute("Name") ?? "";
            if (name.Length == 0) continue;
            var entry = Add(ds, "rdl_dataset", name);
            datasets[name] = entry;
            Link(report, entry, "rdl_contains_dataset", "DataSets/DataSet");
            var query = Child(ds, "Query");
            var sourceName = Val(query, "DataSourceName");
            entry.Attributes["dataSourceName"] = sourceName;
            if (sources.TryGetValue(sourceName, out var source))
            {
                Link(entry, source, "rdl_uses_datasource", "Query/DataSourceName");
                entry.DatabaseName = source.DatabaseName;
                entry.DatabaseServer = source.DatabaseServer;
            }
            else if (Child(ds, "SharedDataSet") is XElement shared)
            {
                entry.Attributes["sharedDataSetReference"] = Val(shared, "SharedDataSetReference");
                entry.Evidence.Add(new CodeEvidence { FilePath = path, LineIndex = Line(shared), Detail = "Shared dataset requires external .rsd definition for query lineage." });
            }
            entry.Attributes["commandType"] = Val(query, "CommandType") is { Length: > 0 } type ? type : "Text";
            entry.Attributes["commandText"] = Val(query, "CommandText");
            var cmd = entry.Attributes["commandText"];
            if (cmd.Length > 0)
            {
                entry.Evidence.Add(new CodeEvidence { FilePath = path, LineIndex = query is null ? Line(ds) : Line(query), Detail = $"Query command ({entry.Attributes["commandType"]}): {cmd[..Math.Min(500, cmd.Length)]}" });
                if (entry.Attributes["commandType"].Equals("StoredProcedure", StringComparison.OrdinalIgnoreCase))
                    entry.Attributes["sqlProcedure:" + Normal(cmd)] = "rdl-commandtype-storedprocedure";
                else if (entry.Attributes["commandType"].Equals("Text", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Match match in SqlObject.Matches(cmd))
                    {
                        var sqlName = Normal(match.Groups["name"].Value);
                        if (sqlName.StartsWith("@") || sqlName.StartsWith("#")) continue;
                        var prefix = Regex.IsMatch(match.Value, @"^\s*EXEC", RegexOptions.IgnoreCase) ? "sqlProcedure:" : "sqlReference:";
                        entry.Attributes[prefix + sqlName] = "rdl-query-static-sql";
                    }
                    if (Regex.IsMatch(cmd, @"\b(?:sp_executesql|EXEC\s*\(|OPENQUERY|OPENROWSET)\b", RegexOptions.IgnoreCase))
                        entry.Evidence.Add(new CodeEvidence { Detail = "Dynamic or external SQL may require manual dependency resolution." });
                }
            }
            foreach (var qp in Children(Child(query ?? ds, "QueryParameters") ?? ds, "QueryParameter"))
            {
                var qname = (string?)qp.Attribute("Name") ?? "";
                var expr = Val(qp, "Value");
                if (qname.Length > 0) entry.Attributes["queryParameter:" + qname] = expr;
                pending.Add((entry, expr, name, "rdl_query_parameter_uses"));
            }
            foreach (var filter in Children(Child(ds, "Filters") ?? ds, "Filter"))
                pending.Add((entry, Val(filter, "FilterExpression"), name, "rdl_dataset_filter_uses"));
            foreach (var f in Children(Child(ds, "Fields") ?? ds, "Field"))
            {
                var fieldName = (string?)f.Attribute("Name") ?? "";
                if (fieldName.Length == 0) continue;
                var field = Add(f, "rdl_field", name + "." + fieldName);
                field.Attributes["dataset"] = name;
                field.Attributes["fieldName"] = fieldName;
                field.Attributes["dataField"] = Val(f, "DataField");
                field.Attributes["valueExpression"] = Val(f, "Value");
                fields[name + "." + fieldName] = field;
                Link(field, entry, "rdl_field_of_dataset", "Fields/Field");
                if (field.Attributes["valueExpression"].Length > 0) pending.Add((field, field.Attributes["valueExpression"], name, "rdl_calculated_field_uses"));
            }
        }
        foreach (var p in Children(Child(root, "ReportParameters") ?? root, "ReportParameter"))
        {
            var name = (string?)p.Attribute("Name") ?? "";
            if (name.Length == 0) continue;
            var entry = Add(p, "rdl_parameter", name);
            parameters[name] = entry;
            Link(report, entry, "rdl_declares_parameter", "ReportParameters");
            entry.Attributes["dataType"] = Val(p, "DataType");
            foreach (var values in p.Descendants().Where(x => x.Name.LocalName is "DataSetReference"))
            {
                var dsName = Val(values, "DataSetName");
                if (datasets.TryGetValue(dsName, out var target)) Link(entry, target, "rdl_parameter_values_from_dataset", "DataSetReference");
            }
            foreach (var v in p.Descendants().Where(x => x.Name.LocalName == "Value")) pending.Add((entry, v.Value, null, "rdl_parameter_expression_uses"));
        }
        foreach (var v in root.Descendants().Where(x => x.Name.LocalName == "Variable"))
        {
            var name = (string?)v.Attribute("Name") ?? "";
            if (name.Length == 0) continue;
            var entry = Add(v, "rdl_variable", name);
            entry.Attributes["expression"] = Val(v, "Value");
            variables[name] = entry;
            pending.Add((entry, entry.Attributes["expression"], null, "rdl_variable_uses"));
        }
        // Tablix, nested groups, filters and visibility can reference dataset fields.
        foreach (var item in root.Descendants().Where(x => x.Name.LocalName is "Textbox" or "Tablix" or "Chart" or "GaugePanel" or "Subreport"))
        {
            var name = (string?)item.Attribute("Name") ?? "";
            if (name.Length == 0) continue;
            var entry = Add(item, "rdl_report_item", name);
            entry.Attributes["itemType"] = item.Name.LocalName;
            var datasetName = Val(item, "DataSetName");
            if (datasetName.Length == 0)
                datasetName = item.Ancestors().Select(a => Val(a, "DataSetName")).FirstOrDefault(x => x.Length > 0) ?? "";
            if (datasetName.Length == 0 && datasets.Count == 1) datasetName = datasets.Keys.First();
            entry.Attributes["dataset"] = datasetName;
            items[name] = entry;
            Link(report, entry, "rdl_contains_item", "ReportItems");
            if (datasets.TryGetValue(datasetName, out var target)) Link(entry, target, "rdl_item_uses_dataset", "DataSetName");
            if (item.Name.LocalName == "Subreport") entry.Attributes["subreportName"] = Val(item, "ReportName");
            foreach (var expr in item.Descendants().Where(x => x.Name.LocalName is "Value" or "Hidden" or "FilterExpression" or "GroupExpression" or "SortExpression" or "ToolTip"))
            {
                if (!expr.Value.TrimStart().StartsWith('=')) continue;
                var expression = expr.Value.Trim();
                var purpose = expr.Name.LocalName switch { "Hidden" => "rdl_visibility_uses", "FilterExpression" => "rdl_filter_uses", "GroupExpression" => "rdl_group_uses", "SortExpression" => "rdl_sort_uses", _ => "rdl_expression_uses" };
                entry.Attributes["expression:" + expr.Name.LocalName + ":" + Line(expr)] = expression;
                pending.Add((entry, expression, datasetName, purpose));
            }
        }
        // Dataset/group/tablix filters outside report items also contribute lineage.
        foreach (var expr in root.Descendants().Where(x => x.Name.LocalName is "FilterExpression" or "GroupExpression" or "SortExpression"))
        {
            var owner = expr.Ancestors().FirstOrDefault(a => a.Name.LocalName is "DataSet" or "Tablix" or "Chart" or "Textbox");
            var ownerName = (string?)owner?.Attribute("Name") ?? "";
            var entry = owner?.Name.LocalName == "DataSet" ? datasets.GetValueOrDefault(ownerName) : items.GetValueOrDefault(ownerName);
            if (entry is not null && expr.Value.TrimStart().StartsWith('=')) pending.Add((entry, expr.Value, entry.Attributes.GetValueOrDefault("dataset", ownerName), "rdl_filter_group_sort_uses"));
        }
        foreach (var (entry, expression, dataset, reason) in pending)
        {
            if (string.IsNullOrWhiteSpace(expression)) continue;
            foreach (Match match in ParameterRef.Matches(expression)) if (parameters.TryGetValue(match.Groups[1].Value, out var target)) Link(entry, target, reason, "Parameters!name.Value");
            foreach (Match match in VariableRef.Matches(expression)) if (variables.TryGetValue(match.Groups[1].Value, out var target)) Link(entry, target, reason, "Variables!name.Value");
            foreach (Match match in ReportItemRef.Matches(expression)) if (items.TryGetValue(match.Groups[1].Value, out var target) && target != entry) Link(entry, target, reason, "ReportItems!name.Value");
            var explicitDatasets = LookupDataset.Matches(expression).Cast<Match>().Select(m => m.Groups["name"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var other in explicitDatasets) if (datasets.TryGetValue(other, out var target)) Link(entry, target, "rdl_lookup_uses_dataset", "Lookup/LookupSet dataset argument");
            foreach (Match match in FieldRef.Matches(expression))
            {
                var fieldName = match.Groups[1].Value;
                var names = (dataset is { Length: > 0 } ? new[] { dataset } : Array.Empty<string>()).Concat(explicitDatasets).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (names.Length == 0 && datasets.Count == 1) names = [datasets.Keys.First()];
                var candidates = names.Select(n => fields.GetValueOrDefault(n + "." + fieldName)).Where(f => f is not null).Cast<CodeMapEntry>().Distinct().ToArray();
                if (candidates.Length == 0)
                    candidates = fields.Where(kv => kv.Key.EndsWith("." + fieldName, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).Distinct().ToArray();
                foreach (var target in candidates) Link(entry, target, reason, candidates.Length == 1 ? "Fields!name.Value" : "ambiguous dataset field", candidates.Length == 1 ? "matched" : "candidate");
            }
            if (expression.Contains("Code.", StringComparison.OrdinalIgnoreCase))
                entry.Evidence.Add(new CodeEvidence { Detail = "Custom report Code.* expression; inspect report Code block or external assemblies." });
        }
        var code = Val(root, "Code");
        if (code.Length > 0) report.Attributes["code"] = code;
        return entries;
    }
}
