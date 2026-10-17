using CodeMapSync.Models;
using CodeMapSync.Sql;
using CodeMapSync.Options;
using System.Text.RegularExpressions;
namespace CodeMapSync.Analysis;
public sealed class CodeMapBuilder(IEnumerable<ICodeAnalyzer> analyzers)
{
 public async Task<CodeMapDocument> BuildAsync(SourceDefinition source, string root, string revision, CancellationToken ct)
 {
  var doc = new CodeMapDocument { SourceId = source.Id, SourceRevision = revision, ProjectId = source.Id, ProjectName = source.Name };
  foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
  {
   ct.ThrowIfCancellationRequested();
   var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
   if (rel.Split('/').Any(x => source.ExcludedDirectories.Contains(x, StringComparer.OrdinalIgnoreCase))) continue;
   var analyzer = analyzers.FirstOrDefault(x => x.Supports(path));
   if (analyzer is null || new FileInfo(path).Length > 2_000_000) continue;
   try
   {
    var content = await File.ReadAllTextAsync(path, ct);
    doc.Entries.AddRange(analyzer.Analyze(source.Id, source.Name, rel, content));
   }
   catch (Exception ex) when (ex is not OperationCanceledException)
   {
    // Malformed or unreadable files are skipped; the build continues.
    System.Diagnostics.Trace.WriteLine($"Skipped {rel}: {ex.Message}");
   }
  }
  var sqlEnricher = new CSharpSqlReferenceEnricher();
  var ormFiles = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
  {
   ct.ThrowIfCancellationRequested();
   var rel = Path.GetRelativePath(root,path).Replace('\\','/');
   if (rel.Split('/').Any(x=>source.ExcludedDirectories.Contains(x,StringComparer.OrdinalIgnoreCase))) continue;
   if (new FileInfo(path).Length>2_000_000) continue;
   var content = await File.ReadAllTextAsync(path,ct);
   sqlEnricher.Enrich(doc,rel,content);
   ormFiles[rel] = content;
  }
  new CSharpOrmReferenceEnricher().Enrich(doc,ormFiles);
  Resolve(doc);
  return doc;
 }
 private static void Link(CodeMapEntry from, CodeMapEntry to, string type, string status, string discovery)
 {
  if (from.RelatedMapObjectIds.Contains(to.UniqueId)) return;
  from.RelatedMapObjectIds.Add(to.UniqueId);
  from.Relations.Add(new CodeMapRelation { TargetId = to.UniqueId, RelationType = type, Status = status, DiscoveryMethod = discovery });
 }
 private static void Resolve(CodeMapDocument doc)
 {
  var endpoints = doc.Entries.Where(x => x.Kind == "endpoint" && x.Route is not null).ToArray();
  foreach (var req in doc.Entries.Where(x => (x.Kind is "client" or "http_request") && (x.RequestUri ?? x.Route) is not null))
  {
   foreach (var endpoint in endpoints)
   {
    if (req.HttpMethod is not null && endpoint.HttpMethod is not null && req.HttpMethod != endpoint.HttpMethod) continue;
    if (HttpRouteMatcher.Match(req.RequestUri ?? req.Route!, req.HttpMethod, endpoint.Route!, endpoint.HttpMethod).Score > 0) Link(req, endpoint, "http_calls", "candidate", "route-template-match");
   }
  }
  // A DOM assignment and an HTTP call in the same file are candidates, not proven data-flow.
  foreach (var binding in doc.Entries.Where(x => x.Kind == "ui_binding"))
  {
   foreach (var req in doc.Entries.Where(x => (x.Kind is "client" or "http_request") && x.FilePath == binding.FilePath && Math.Abs((x.StartLineIndex ?? 0) - (binding.StartLineIndex ?? 0)) <= 25))
    Link(binding, req, "possibly_populated_by", "candidate", "same-file-nearby");
  }
  foreach (var label in doc.Entries.Where(x => x.Kind == "ui_label" && x.Attributes.ContainsKey("for")))
  {
   foreach (var binding in doc.Entries.Where(x => x.Kind == "ui_binding" && x.Key == label.Attributes["for"] && x.FilePath == label.FilePath))
    Link(label, binding, "targets_dom_element", "candidate", "html-for-id");
  }
  var methods = doc.Entries.Where(x => x.Kind is "method" or "endpoint").ToArray();
  foreach (var method in methods)
  {
   foreach (var call in method.Attributes.Where(x => x.Key.StartsWith("call:")).Select(x => x.Value))
   {
    var name = call.Split('.').Last();
    foreach (var target in methods.Where(x => x != method && x.SearchKeys.Contains(name)))
     Link(method, target, "calls", "candidate", "syntax-name-match");
   }
  }
 }
 private static bool RouteMatch(string request, string template)
 {
  var path = Uri.TryCreate(request, UriKind.Absolute, out var uri) ? uri.AbsolutePath : request.Split('?')[0];
  var segments = template.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
  var pattern = "^/" + string.Join("/", segments.Select(segment => segment.StartsWith('{') && segment.EndsWith('}') ? "[^/]+" : Regex.Escape(segment))) + "/?$";
  try { return Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
  catch (ArgumentException) { return false; }
 }
}
