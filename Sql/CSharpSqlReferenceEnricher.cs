using Microsoft.CodeAnalysis;
using CodeMapSync.Models;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeMapSync.Sql;

// Syntax-only extraction. Literal SQL object names are candidates, never proof of executed SQL.
public sealed class CSharpSqlReferenceEnricher
{
    private static readonly HashSet<string> SqlMethods = new(StringComparer.OrdinalIgnoreCase)
    { "FromSqlRaw", "FromSqlInterpolated", "FromSql", "ExecuteSqlRaw", "ExecuteSqlInterpolated",
      "ExecuteSql", "SqlQueryRaw", "SqlQuery", "ExecuteSqlCommand", "ExecuteSqlCommandAsync" };
    public void Enrich(CodeMapDocument map, string filePath, string content)
    {
        var tree=CSharpSyntaxTree.ParseText(content);
        var root=tree.GetRoot();
        var entries=map.Entries.Where(e=>e.FilePath==filePath && e.StartLineIndex.HasValue && e.EndLineIndex.HasValue && e.Kind is "method" or "endpoint").ToArray();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name=invocation.Expression switch
            {
                MemberAccessExpressionSyntax member=>member.Name.Identifier.ValueText,
                IdentifierNameSyntax identifier=>identifier.Identifier.ValueText,
                _=>""
            };
            if (!SqlMethods.Contains(name)) continue;
            var arg=invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            if (arg is not LiteralExpressionSyntax literal || !literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression)) continue;
            var sql=literal.Token.ValueText;
            var line=tree.GetLineSpan(invocation.Span).StartLinePosition.Line;
            var owner=entries.Where(e=>e.StartLineIndex<=line && e.EndLineIndex>=line)
                .OrderBy(e=>(e.EndLineIndex??0)-(e.StartLineIndex??0)).FirstOrDefault();
            if (owner is null) continue;
            // Never store full SQL literals; capture only exact, safely parseable object references.
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(sql,
                @"(?i)\b(?:EXEC(?:UTE)?|FROM|JOIN|INTO|UPDATE)\s+(?<name>(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)(?:\.(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*))?)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
            {
                var obj=m.Groups["name"].Value.Replace("[","").Replace("]","");
                var verb = m.Value.TrimStart().Split(' ', '\t', '\r', '\n')[0];
                var key = (verb.Equals("EXEC", StringComparison.OrdinalIgnoreCase) || verb.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase)
                    ? "sqlProcedure:" : "sqlReference:") + obj;
                if (!owner.Attributes.ContainsKey(key)) owner.Attributes[key]=name;
            }
            // SQL functions used in SELECT expressions or table-valued FROM clauses.
            // Only schema-qualified calls are considered, to avoid confusing built-ins.
            foreach (System.Text.RegularExpressions.Match function in System.Text.RegularExpressions.Regex.Matches(sql,
                @"(?i)(?<name>(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\.(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*))\s*\(",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            {
                var obj = function.Groups["name"].Value.Replace("[", "").Replace("]", "");
                var key = "sqlFunction:" + obj;
                if (!owner.Attributes.ContainsKey(key)) owner.Attributes[key] = name;
            }
        }
    }
}
