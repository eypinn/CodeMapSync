using CodeMapSync.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
namespace CodeMapSync.Analysis;
public sealed class CSharpAnalyzer : ICodeAnalyzer
{
 public bool Supports(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
 public IEnumerable<CodeMapEntry> Analyze(string sourceId, string project, string path, string content)
 {
  var root = CSharpSyntaxTree.ParseText(content).GetRoot();
  foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
  {
   var entry = MapIds.New(sourceId, project, path, "csharp", "class", cls.Identifier.Text, Line(cls));
   entry.EndLineIndex = cls.GetLocation().GetLineSpan().EndLinePosition.Line;
   yield return entry;
  }
  foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
  {
   var cls = method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
   var key = (cls is null ? "" : cls.Identifier.Text + ".") + method.Identifier.Text;
   var entry = MapIds.New(sourceId, project, path, "csharp", "method", key, Line(method));
   entry.EndLineIndex = method.GetLocation().GetLineSpan().EndLinePosition.Line;
   entry.SearchKeys.Add(method.Identifier.Text);
   entry.Attributes["class"] = cls?.Identifier.Text ?? "";
   foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
   {
    var callName = call.Expression.ToString();
    if (callName.Length > 250) continue;
    entry.Attributes[$"call:{Line(call)}:{entry.Attributes.Count}"] = callName;
   }
   var attributes = method.AttributeLists.SelectMany(x => x.Attributes).ToList();
   var http = attributes.FirstOrDefault(x => x.Name.ToString().StartsWith("Http", StringComparison.OrdinalIgnoreCase));
   var routeAttr = attributes.FirstOrDefault(x => x.Name.ToString().EndsWith("Route", StringComparison.OrdinalIgnoreCase));
   var controllerRoute = cls?.AttributeLists.SelectMany(x => x.Attributes)
     .FirstOrDefault(x => x.Name.ToString().EndsWith("Route", StringComparison.OrdinalIgnoreCase));
   if (http is not null || routeAttr is not null || (cls?.Identifier.Text.EndsWith("Controller") == true && method.Modifiers.Any(x => x.Text == "public")))
   {
    entry.Kind = "endpoint";
    entry.EndpointName = key;
    entry.HttpMethod = http is null ? null : http.Name.ToString()[4..].ToUpperInvariant();
    var actionRoute = Literal(http) ?? Literal(routeAttr) ?? "";
    var prefix = Literal(controllerRoute) ?? "[controller]";
    prefix = prefix.Replace("[controller]", cls?.Identifier.Text.Replace("Controller", "") ?? "", StringComparison.OrdinalIgnoreCase);
    entry.Route = "/" + (actionRoute.StartsWith("/") ? actionRoute.TrimStart('/') : (prefix.Trim('/') + "/" + actionRoute.Trim('/'))).Trim('/');
    entry.SearchKeys.Add(entry.Route);
   }
   yield return entry;
   // Preserve statically identifiable outgoing HTTP calls as client entries.
   foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
   {
    if (!TryHttpCall(call, method, out var verb, out var url)) continue;
    var req = MapIds.New(sourceId, project, path, "csharp", "client", $"{key}:{verb}:{url}", Line(call));
    req.HttpMethod = verb;
    req.RequestUri = url;
    req.SearchKeys.Add(url);
    req.Attributes["callerId"] = entry.UniqueId;
    req.Attributes["callerName"] = key;
    yield return req;
   }
  }
  // Minimal API registrations may occur in Program.cs, extension methods or elsewhere.
  foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
  {
   var name = InvocationName(call);
   var verb = name switch { "MapGet" => "GET", "MapPost" => "POST", "MapPut" => "PUT", "MapDelete" => "DELETE", "MapPatch" => "PATCH", _ => null };
   if (verb is null || call.ArgumentList.Arguments.Count == 0) continue;
   if (call.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax routeLiteral || routeLiteral.RawKind != (int)SyntaxKind.StringLiteralExpression) continue;
   var route = routeLiteral.Token.ValueText;
   var endpoint = MapIds.New(sourceId, project, path, "csharp", "endpoint", $"{verb} {route}", Line(call));
   endpoint.HttpMethod = verb;
   endpoint.Route = "/" + route.Trim('/');
   endpoint.EndpointName = $"{verb} {endpoint.Route}";
   endpoint.SearchKeys.Add(endpoint.Route);
   yield return endpoint;
  }
 }
 private static string InvocationName(InvocationExpressionSyntax call) => call.Expression switch
 {
  MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
  SimpleNameSyntax simple => simple.Identifier.ValueText,
  _ => ""
 };
 private static bool TryHttpCall(InvocationExpressionSyntax call, MethodDeclarationSyntax method, out string verb, out string route)
 {
  verb = route = "";
  var name = InvocationName(call);
  verb = name switch
  {
   "GetAsync" or "GetFromJsonAsync" or "GetStringAsync" or "GetStreamAsync" or "GetByteArrayAsync" => "GET",
   "PostAsync" or "PostAsJsonAsync" => "POST",
   "PutAsync" or "PutAsJsonAsync" => "PUT",
   "DeleteAsync" or "DeleteFromJsonAsync" => "DELETE",
   "PatchAsync" or "PatchAsJsonAsync" => "PATCH",
   _ => ""
  };
  if (name == "SendAsync")
  {
   // SendAsync(request): follow local request declaration or assignment.
   var argument = call.ArgumentList.Arguments.FirstOrDefault()?.Expression;
   if (argument is not null)
   {
    var request = ResolveLocalExpression(argument, method, call.SpanStart);
    if (request is ObjectCreationExpressionSyntax creation && creation.Type.ToString().EndsWith("HttpRequestMessage", StringComparison.Ordinal))
     return TryRequestMessage(creation.ArgumentList?.Arguments, method, call.SpanStart, out verb, out route);
    if (request is ImplicitObjectCreationExpressionSyntax implicitCreation)
     return TryRequestMessage(implicitCreation.ArgumentList?.Arguments, method, call.SpanStart, out verb, out route);
   }
   return false;
  }
  if (verb.Length == 0 || call.ArgumentList.Arguments.Count == 0) return false;
  route = ResolveUri(call.ArgumentList.Arguments[0].Expression, method, call.SpanStart) ?? "";
  return !string.IsNullOrWhiteSpace(route);
 }
 private static bool TryRequestMessage(SeparatedSyntaxList<ArgumentSyntax>? args, MethodDeclarationSyntax method, int position, out string verb, out string route)
 {
  verb = route = "";
  if (args is null || args.Value.Count < 2) return false;
  var methodExpression = args.Value[0].Expression.ToString();
  verb = methodExpression switch
  {
   "HttpMethod.Get" => "GET", "HttpMethod.Post" => "POST", "HttpMethod.Put" => "PUT",
   "HttpMethod.Delete" => "DELETE", "HttpMethod.Patch" => "PATCH", "HttpMethod.Head" => "HEAD", "HttpMethod.Options" => "OPTIONS", _ => ""
  };
  route = ResolveUri(args.Value[1].Expression, method, position) ?? "";
  return verb.Length > 0 && route.Length > 0;
 }
 private static string? ResolveUri(ExpressionSyntax expression, MethodDeclarationSyntax method, int position, int depth = 0)
 {
  if (depth > 5) return null;
  expression = ResolveLocalExpression(expression, method, position);
  if (expression is LiteralExpressionSyntax literal && literal.RawKind == (int)SyntaxKind.StringLiteralExpression)
   return literal.Token.ValueText;
  if (expression is InterpolatedStringExpressionSyntax interpolated && interpolated.Contents.All(x => x is InterpolatedStringTextSyntax))
   return string.Concat(interpolated.Contents.OfType<InterpolatedStringTextSyntax>().Select(x => x.TextToken.ValueText));
  if (expression is ObjectCreationExpressionSyntax creation && creation.Type.ToString().EndsWith("Uri", StringComparison.Ordinal))
   return creation.ArgumentList?.Arguments.Count > 0 ? ResolveUri(creation.ArgumentList.Arguments[0].Expression, method, position, depth + 1) : null;
  if (expression is ImplicitObjectCreationExpressionSyntax implicitCreation)
   return implicitCreation.ArgumentList?.Arguments.Count > 0 ? ResolveUri(implicitCreation.ArgumentList.Arguments[0].Expression, method, position, depth + 1) : null;
  if (expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression))
  {
   var left = ResolveUri(binary.Left, method, position, depth + 1);
   var right = ResolveUri(binary.Right, method, position, depth + 1);
   return left is not null && right is not null ? left + right : null;
  }
  return null;
 }
 private static ExpressionSyntax ResolveLocalExpression(ExpressionSyntax expression, MethodDeclarationSyntax method, int position)
 {
  if (expression is not IdentifierNameSyntax identifier) return expression;
  var name = identifier.Identifier.ValueText;
  // Only earlier declarations in the same method; deliberately avoid guessing unknown values.
  var assignment = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
   .Where(x => x.SpanStart < position && x.Left is IdentifierNameSyntax left && left.Identifier.ValueText == name)
   .OrderByDescending(x => x.SpanStart).FirstOrDefault();
  if (assignment is not null) return assignment.Right;
  var declaration = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
   .Where(x => x.SpanStart < position && x.Identifier.ValueText == name && x.Initializer is not null)
   .OrderByDescending(x => x.SpanStart).FirstOrDefault();
  return declaration?.Initializer?.Value ?? expression;
 }
 private static string? Literal(AttributeSyntax? attr) => attr?.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax lit ? lit.Token.ValueText : null;
 private static int Line(Microsoft.CodeAnalysis.SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line;
}
