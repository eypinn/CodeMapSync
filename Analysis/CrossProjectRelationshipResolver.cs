using CodeMapSync.Models;
using System.Text.RegularExpressions;
namespace CodeMapSync.Analysis;

public sealed class CrossProjectRelationshipResolver
{
 public int Resolve(IReadOnlyCollection<CodeMapDocument> maps)
 {
  var entries = maps.SelectMany(m => m.Entries).ToArray();
  var endpoints = entries.Where(x => x.Kind == "endpoint" && !string.IsNullOrWhiteSpace(x.Route)).ToArray();
  var byId = entries.GroupBy(x => x.UniqueId).ToDictionary(x => x.Key, x => x.First());
  var count = 0;
  foreach (var request in entries.Where(x => (x.Kind is "client" or "http_request") && !string.IsNullOrWhiteSpace(x.RequestUri ?? x.Route)))
  {
   var uri = request.RequestUri ?? request.Route!;
   var matches = endpoints.Where(endpoint => request.ProjectId != endpoint.ProjectId)
    .Select(endpoint => (endpoint, match: HttpRouteMatcher.Match(uri, request.HttpMethod, endpoint.Route!, endpoint.HttpMethod)))
    .Where(x => x.match.Score > 0).ToArray();
   foreach (var (endpoint, match) in matches)
   {
    count += Link(request, endpoint, match);
    if (request.Attributes.TryGetValue("callerId", out var callerId) && byId.TryGetValue(callerId, out var caller))
    {
     count += Link(caller, request, new HttpRouteMatcher.Result("strong", 1.0));
     count += Link(caller, endpoint, match);
    }
   }
  }
  return count;
 }
 private static int Link(CodeMapEntry from, CodeMapEntry to, HttpRouteMatcher.Result match)
 {
  const string type = "http_calls";
  var relation = from.Relations.FirstOrDefault(r => r.TargetId == to.UniqueId && r.RelationType == type);
  if (relation is null)
  {
   relation = new CodeMapRelation { TargetId = to.UniqueId, RelationType = type };
   from.Relations.Add(relation);
  }
  var changed = relation.MatchScore != match.Score || relation.MatchStrength != match.Strength || relation.DiscoveryMethod != "cross-project-http-route";
  relation.Status = match.Strength == "strong" ? "matched" : "candidate";
  relation.DiscoveryMethod = "cross-project-http-route";
  relation.MatchScore = match.Score;
  relation.MatchStrength = match.Strength;
  if (!from.RelatedMapObjectIds.Contains(to.UniqueId)) { from.RelatedMapObjectIds.Add(to.UniqueId); changed = true; }
  return changed ? 1 : 0;
 }
}

internal static class HttpRouteMatcher
{
 internal readonly record struct Result(string Strength, double Score);
 internal static Result Match(string request, string? requestVerb, string endpoint, string? endpointVerb)
 {
  if (!string.IsNullOrWhiteSpace(requestVerb) && !string.IsNullOrWhiteSpace(endpointVerb) &&
      !string.Equals(requestVerb, endpointVerb, StringComparison.OrdinalIgnoreCase)) return new("none", 0);
  var a = Segments(request);
  var b = Segments(endpoint);
  if (a.Length == 0 || b.Length == 0) return new("none", 0);
  if (SegmentsMatch(a,b)) return new("strong", 1.0);
  // Common optional convention: an API endpoint may have an /api prefix.
  if (a.Length > 1 && a[0].Equals("api", StringComparison.OrdinalIgnoreCase) && SegmentsMatch(a[1..], b)) return new("weak", 0.70);
  if (b.Length > 1 && b[0].Equals("api", StringComparison.OrdinalIgnoreCase) && SegmentsMatch(a, b[1..])) return new("weak", 0.70);
  return new("none", 0);
 }
 private static string[] Segments(string value)
 {
  if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) value = uri.AbsolutePath;
  value = value.Split('?', '#')[0];
  return value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
 }
 private static bool SegmentsMatch(string[] request, string[] endpoint)
 {
  if (request.Length != endpoint.Length) return false;
  for (var i = 0; i < request.Length; i++)
  {
   var segment = endpoint[i];
   if (segment.StartsWith('{') && segment.EndsWith('}'))
   {
    var constraint = segment[1..^1].Split(':')[0];
    if (constraint.StartsWith("**", StringComparison.Ordinal)) return false;
    if (request[i].Length == 0) return false;
   }
   else if (!string.Equals(request[i], segment, StringComparison.OrdinalIgnoreCase)) return false;
  }
  return true;
 }
}
