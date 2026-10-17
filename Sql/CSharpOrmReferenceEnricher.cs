using CodeMapSync.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace CodeMapSync.Sql;

/// <summary>
/// Static, syntax-only EF Core and Dapper reference discovery. Does not execute SQL or load assemblies.
/// References are candidates until a SQL snapshot is matched.
/// </summary>
public sealed class CSharpOrmReferenceEnricher
{
    private static readonly HashSet<string> DapperMethods = new(StringComparer.Ordinal)
    {
        "Query", "QueryAsync", "QueryFirst", "QueryFirstAsync", "QueryFirstOrDefault", "QueryFirstOrDefaultAsync",
        "QuerySingle", "QuerySingleAsync", "QuerySingleOrDefault", "QuerySingleOrDefaultAsync",
        "QueryMultiple", "QueryMultipleAsync", "Execute", "ExecuteAsync", "ExecuteScalar", "ExecuteScalarAsync",
        "ExecuteReader", "ExecuteReaderAsync", "QueryUnbufferedAsync"
    };
    private static readonly Regex ObjectReferences = new(
        @"\b(?:FROM|JOIN|INTO|UPDATE|MERGE\s+INTO|EXEC(?:UTE)?)\s+(?<name>(?:\[[^\]]+\]|[A-Za-z_][\w]*)(?:\.(?:\[[^\]]+\]|[A-Za-z_][\w]*))?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SelectObject = new(@"\b(?:SELECT|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public void Enrich(CodeMapDocument map, IReadOnlyDictionary<string, string> csharpFiles)
    {
        var roots = csharpFiles.ToDictionary(x => x.Key, x => CSharpSyntaxTree.ParseText(x.Value).GetRoot(), StringComparer.OrdinalIgnoreCase);
        var entities = new Dictionary<string, (string SqlName, bool IsView, string Discovery)>(StringComparer.OrdinalIgnoreCase);
        var dbsets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Collect EF model configuration across files before linking usage sites.
        foreach (var (_, root) in roots)
        {
            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var entityName = cls.Identifier.ValueText;
                foreach (var attribute in cls.AttributeLists.SelectMany(x => x.Attributes))
                {
                    if (attribute.Name.ToString().Split('.').Last() is not ("Table" or "TableAttribute")) continue;
                    var args = attribute.ArgumentList?.Arguments;
                    var table = args?.FirstOrDefault()?.Expression is { } exp ? Literal(exp) : null;
                    var schema = args?.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "Schema")?.Expression is { } sch ? Literal(sch) : null;
                    if (!string.IsNullOrWhiteSpace(table)) entities[entityName] = (Qualified(schema, table), false, "efcore-table-attribute");
                }
            }
            foreach (var prop in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (!TryDbSetType(prop.Type, out var entity)) continue;
                var dbsetName = prop.Identifier.ValueText;
                dbsets[dbsetName] = entity;
                if (!entities.ContainsKey(entity)) entities[entity] = (dbsetName, false, "efcore-dbset-convention");
            }
            // Resolve EF Core Fluent API configurations, including multiline chains:
            // modelBuilder.Entity<CityView>().HasNoKey().ToView("CitiesVw", "dbo")
            // and var entity = modelBuilder.Entity<CityView>(); entity.ToView(...).
            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax member) continue;
                var operation = member.Name.Identifier.ValueText;
                if (operation is not ("ToTable" or "ToView")) continue;
                if (call.ArgumentList.Arguments.Count == 0) continue;
                var name = Literal(call.ArgumentList.Arguments[0].Expression);
                if (string.IsNullOrWhiteSpace(name)) continue;
                var schemaArg = call.ArgumentList.Arguments.Skip(1).FirstOrDefault(a =>
                    a.NameColon is null || a.NameColon.Name.Identifier.ValueText == "schema");
                var schema = schemaArg is null ? null : Literal(schemaArg.Expression);
                if (call.ArgumentList.Arguments.Count > 1 && schemaArg is not null && schema is null) continue;
                var entity = FindFluentEntity(member.Expression, call);
                if (entity is null) continue;
                entities[entity] = (Qualified(schema, name), operation == "ToView", "efcore-fluent-api-mapping");
            }
        }
        foreach (var (dbset, entity) in dbsets.ToArray())
            if (!entities.ContainsKey(entity)) entities[entity] = (dbset, false, "efcore-dbset-convention");

        // Bind the actual class CodeMap entry to its SQL lookup candidate.
        // File paths may be rooted differently between the source scanner and Roslyn.
        foreach (var (file, root) in roots)
        foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var name = cls.Identifier.ValueText;
            var sameName = map.Entries.Where(e => e.Kind == "class" &&
                string.Equals(e.Key, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var classEntry = sameName.FirstOrDefault(e => SamePath(e.FilePath, file))
                ?? (sameName.Length == 1 ? sameName[0] : null);
            if (classEntry is null) continue;
            // Only actual EF entities are mapped; controllers/services/DbContexts are not tables.
            if (!entities.TryGetValue(name, out var mapping)) continue;
            Add(classEntry, mapping.IsView ? "efView:" : "efTable:", mapping.SqlName, mapping.Discovery);
        }

        foreach (var (file, root) in roots)
        {
            var owners = map.Entries.Where(e => e.FilePath == file && e.Kind is "method" or "endpoint" && e.StartLineIndex.HasValue && e.EndLineIndex.HasValue).ToArray();
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var owner = Owner(method, owners);
                if (owner is null) continue;
                foreach (var member in method.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    // dbContext.Cities, this.Cities, and bare Cities: match only a declared DbSet name.
                    if (!dbsets.TryGetValue(member.Name.Identifier.ValueText, out var entity)) continue;
                    var mapping = entities.TryGetValue(entity, out var configured)
                        ? configured : (member.Name.Identifier.ValueText, false, "efcore-dbset");
                    Add(owner, mapping.Item2 ? "efView:" : "efTable:", mapping.Item1, "efcore-dbset");
                    LinkEntity(owner, map, entity);
                }
                foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (call.Expression is not MemberAccessExpressionSyntax member) continue;
                    var name = member.Name.Identifier.ValueText;
                    if (name is "Set" && member.Name is GenericNameSyntax setType && setType.TypeArgumentList.Arguments.Count == 1)
                    {
                        var entity = ShortName(setType.TypeArgumentList.Arguments[0].ToString());
                        var mapping = entities.TryGetValue(entity, out var configured)
                            ? configured : (entity, false, "efcore-set");
                        Add(owner, mapping.Item2 ? "efView:" : "efTable:", mapping.Item1, "efcore-set");
                        LinkEntity(owner, map, entity);
                    }
                    if (!DapperMethods.Contains(name)) continue;
                    var first = call.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                    if (first is null) continue;
                    var sql = ResolveSql(first, method, call.SpanStart);
                    if (sql is null) continue;
                    var commandTypeStoredProc = call.ArgumentList.Arguments.Any(a => a.Expression.ToString().Contains("CommandType.StoredProcedure", StringComparison.Ordinal));
                    if (commandTypeStoredProc)
                    {
                        var procedure = Normalize(sql);
                        if (IsObjectName(procedure)) Add(owner, "dapperProcedure:", procedure, "dapper-stored-procedure");
                        continue;
                    }
                    // Do not interpret unknown/dynamic SQL as a concrete dependency.
                    foreach (Match match in ObjectReferences.Matches(sql))
                    {
                        var objectName = Normalize(match.Groups["name"].Value);
                        if (!IsObjectName(objectName)) continue;
                        var verb = match.Value.TrimStart().Split(' ', '\t', '\r', '\n')[0];
                        if (verb.Equals("EXEC", StringComparison.OrdinalIgnoreCase) || verb.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase))
                            Add(owner, "dapperProcedure:", objectName, "dapper-exec-literal");
                        else
                            Add(owner, "dapperSql:", objectName, "dapper-sql-literal");
                    }
                    foreach (Match function in Regex.Matches(sql,
                        @"(?i)(?<name>(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\.(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*))\s*\(",
                        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                    {
                        var functionName = Normalize(function.Groups["name"].Value);
                        if (IsObjectName(functionName)) Add(owner, "sqlFunction:", functionName, "dapper-sql-function");
                    }
                    // Bare command name without CommandType is not proof of a procedure call.
                }
            }
        }
    }

    private static string? FindFluentEntity(ExpressionSyntax receiver, InvocationExpressionSyntax call)
    {
        // Nested invocation chain: .HasNoKey().ToView(...) / .ToTable(...)
        foreach (var invocation in receiver.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is MemberAccessExpressionSyntax member &&
                member.Name is GenericNameSyntax generic &&
                generic.Identifier.ValueText == "Entity" &&
                generic.TypeArgumentList.Arguments.Count == 1)
                return ShortName(generic.TypeArgumentList.Arguments[0].ToString());
        }
        // modelBuilder.Entity<CityView>(entity => entity.ToView("CitiesVw", "dbo"))
        if (receiver is IdentifierNameSyntax lambdaParameter)
        {
            var lambda = call.Ancestors().OfType<LambdaExpressionSyntax>().FirstOrDefault();
            var parameterName = lambda switch
            {
                SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
                ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1
                    => parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
                _ => null
            };
            if (parameterName == lambdaParameter.Identifier.ValueText && lambda is not null)
            {
                var enclosingEntity = lambda.Ancestors().OfType<InvocationExpressionSyntax>()
                    .FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax m &&
                        m.Name is GenericNameSyntax g && g.Identifier.ValueText == "Entity" &&
                        g.TypeArgumentList.Arguments.Count == 1);
                if (enclosingEntity?.Expression is MemberAccessExpressionSyntax entityMember &&
                    entityMember.Name is GenericNameSyntax entityGeneric)
                    return ShortName(entityGeneric.TypeArgumentList.Arguments[0].ToString());
            }
        }
        // Local EntityTypeBuilder variable, within the same containing method/lambda block.
        if (receiver is IdentifierNameSyntax variable)
        {
            var scope = call.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
            var declarations = scope?.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(v => v.SpanStart < call.SpanStart &&
                    v.Identifier.ValueText == variable.Identifier.ValueText && v.Initializer is not null)
                .OrderByDescending(v => v.SpanStart);
            var initializer = declarations?.FirstOrDefault()?.Initializer?.Value;
            if (initializer is not null)
                return FindFluentEntity(initializer, call);
        }
        return null;
    }

    private static bool SamePath(string a, string b)
    {
        static string Canonical(string path) => path.Replace('\\', '/').TrimStart('/');
        a = Canonical(a); b = Canonical(b);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
            a.EndsWith("/" + b, StringComparison.OrdinalIgnoreCase) ||
            b.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase);
    }

    private static void LinkEntity(CodeMapEntry owner, CodeMapDocument map, string entity)
    {
        var matches = map.Entries.Where(e => e.Kind == "class" &&
            string.Equals(e.Key, entity, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return;
        var target = matches[0];
        if (owner.Relations.Any(r => r.TargetId == target.UniqueId && r.RelationType == "uses_entity")) return;
        owner.Relations.Add(new CodeMapRelation
        {
            TargetId = target.UniqueId, RelationType = "uses_entity", Status = "candidate",
            DiscoveryMethod = "efcore-dbcontext-access", MatchStrength = "strong", MatchScore = 0.9
        });
        if (!owner.RelatedMapObjectIds.Contains(target.UniqueId)) owner.RelatedMapObjectIds.Add(target.UniqueId);
    }

    private static CodeMapEntry? Owner(MethodDeclarationSyntax method, CodeMapEntry[] entries)
    {
        var start = method.GetLocation().GetLineSpan().StartLinePosition.Line;
        return entries.FirstOrDefault(e => e.StartLineIndex == start && e.Key.EndsWith("." + method.Identifier.ValueText, StringComparison.Ordinal))
            ?? entries.FirstOrDefault(e => e.StartLineIndex == start);
    }
    private static void Add(CodeMapEntry entry, string prefix, string name, string method)
    {
        var key = prefix + name;
        if (!entry.Attributes.ContainsKey(key)) entry.Attributes[key] = method;
    }
    private static bool TryDbSetType(TypeSyntax type, out string entity)
    {
        entity = "";
        var generic = type.DescendantNodesAndSelf().OfType<GenericNameSyntax>().FirstOrDefault(g => g.Identifier.ValueText == "DbSet" && g.TypeArgumentList.Arguments.Count == 1);
        if (generic is null) return false;
        entity = ShortName(generic.TypeArgumentList.Arguments[0].ToString());
        return true;
    }
    private static string ShortName(string name) => name.Split('.').Last().TrimEnd('?');
    private static string Qualified(string? schema, string table) => string.IsNullOrWhiteSpace(schema) ? table : schema + "." + table;
    private static string Normalize(string name) => name.Replace("[", "").Replace("]", "").Trim();
    private static bool IsObjectName(string value) => Regex.IsMatch(value, @"^[A-Za-z_][\w]*(\.[A-Za-z_][\w]*)?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static string? Literal(ExpressionSyntax expression) => expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression) ? lit.Token.ValueText : null;
    private static string? ResolveSql(ExpressionSyntax expression, MethodDeclarationSyntax method, int position, int depth = 0)
    {
        if (depth > 4) return null;
        if (Literal(expression) is { } direct) return direct;
        if (expression is IdentifierNameSyntax name)
        {
            var declaration = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(v => v.SpanStart < position && v.Identifier.ValueText == name.Identifier.ValueText && v.Initializer is not null)
                .OrderByDescending(v => v.SpanStart).FirstOrDefault();
            if (declaration?.Initializer?.Value is { } initializer) return ResolveSql(initializer, method, declaration.SpanStart, depth + 1);
        }
        return null;
    }
}
