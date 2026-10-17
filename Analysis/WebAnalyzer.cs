using System.Text.RegularExpressions;
using CodeMapSync.Models;
namespace CodeMapSync.Analysis;
public sealed class WebAnalyzer : ICodeAnalyzer
{
 private static readonly Regex Label = new(@"<label\b[^>]*?(?:for\s*=\s*['""'](?<for>[^'""']+)['""'])?[^>]*>(?<text>.*?)</label>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
 private static readonly Regex Request = new(@"(?:fetch\s*\(\s*|(?:axios|\$\.get|\$\.post)\s*\(\s*)['""`](?<url>[^'""`]+)['""`]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
 private static readonly Regex DomAssign = new(@"(?:getElementById\s*\(\s*['""'](?<id>[^'""']+)['""']\s*\)|querySelector\s*\(\s*['""']#(?<id2>[^'""']+)['""']\s*\))\s*\.\s*(?:textContent|innerText|innerHTML|value)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
 public bool Supports(string path) => new[] { ".html", ".htm", ".cshtml", ".js", ".jsx", ".ts", ".tsx" }.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));
 public IEnumerable<CodeMapEntry> Analyze(string sourceId, string project, string path, string content)
 {
  var lang = path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ? "razor" : "web";
  foreach (Match match in Label.Matches(content))
  {
   var label = Regex.Replace(match.Groups["text"].Value, "<[^>]+>", "").Trim();
   if (string.IsNullOrWhiteSpace(label)) continue;
   var entry = MapIds.New(sourceId, project, path, lang, "ui_label", label, Line(content, match.Index));
   entry.Label = System.Net.WebUtility.HtmlDecode(label);
   if (match.Groups["for"].Success) { entry.Attributes["for"] = match.Groups["for"].Value; entry.SearchKeys.Add(match.Groups["for"].Value); }
   yield return entry;
  }
  foreach (Match match in Request.Matches(content))
  {
   var url = match.Groups["url"].Value;
   var entry = MapIds.New(sourceId, project, path, lang, "http_request", url, Line(content, match.Index));
   entry.Route = url;
   entry.HttpMethod = match.Value.Contains("post", StringComparison.OrdinalIgnoreCase) ? "POST" : match.Value.Contains("get", StringComparison.OrdinalIgnoreCase) ? "GET" : null;
   yield return entry;
  }
  foreach (Match match in DomAssign.Matches(content))
  {
   var id = match.Groups["id"].Success ? match.Groups["id"].Value : match.Groups["id2"].Value;
   var entry = MapIds.New(sourceId, project, path, lang, "ui_binding", id, Line(content, match.Index));
   entry.Attributes["assignment"] = match.Value;
   yield return entry;
  }
 }
 private static int Line(string s, int offset) { var n = 0; for (var i = 0; i < offset; i++) if (s[i] == '\n') n++; return n; }
}
