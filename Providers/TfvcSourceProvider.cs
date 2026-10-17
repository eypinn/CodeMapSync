using CodeMapSync.Options;
using System.Net.Http.Headers;
using System.Text.Json;
namespace CodeMapSync.Providers;
// TFVC via Azure DevOps/TFS REST: requires ServerUrl and $/project/path Location.
public sealed class TfvcSourceProvider(IHttpClientFactory factory) : ISourceProvider
{
 public string Type => "tfvc";
 private HttpClient Client(SourceDefinition s)
 {
  if (string.IsNullOrWhiteSpace(s.ServerUrl)) throw new InvalidOperationException("TFVC ServerUrl required");
  var c = factory.CreateClient();
  if (!string.IsNullOrWhiteSpace(s.CredentialEnvironmentVariable))
  {
   var pat = Environment.GetEnvironmentVariable(s.CredentialEnvironmentVariable) ?? throw new InvalidOperationException("TFVC token env var missing");
   c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + pat)));
  }
  return c;
 }
 private static string Api(SourceDefinition s, string path, bool recursion = false) =>
  s.ServerUrl!.TrimEnd('/') + "/_apis/tfvc/items?path=" + Uri.EscapeDataString(path) +
  (recursion ? "&recursionLevel=Full" : "") + "&api-version=7.0";
 public async Task<string?> GetRemoteVersionAsync(SourceDefinition s, CancellationToken ct)
 {
  using var c = Client(s);
  using var response = await c.GetAsync(Api(s, s.Location), ct);
  response.EnsureSuccessStatusCode();
  using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
  var root = json.RootElement;
  return root.TryGetProperty("changesetId", out var v) ? v.ToString() : null;
 }
 public async Task<SourceSnapshot> FetchAsync(SourceDefinition s, string targetDirectory, CancellationToken ct)
 {
  using var c = Client(s);
  using var response = await c.GetAsync(Api(s, s.Location, true), ct);
  response.EnsureSuccessStatusCode();
  using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
  if (!json.RootElement.TryGetProperty("value", out var items)) throw new InvalidOperationException("Unexpected TFVC items response");
  Directory.CreateDirectory(targetDirectory);
  var basePath = s.Location.TrimEnd('/') + "/";
  foreach (var item in items.EnumerateArray())
  {
   ct.ThrowIfCancellationRequested();
   if (item.TryGetProperty("isFolder", out var folder) && folder.GetBoolean()) continue;
   var path = item.GetProperty("path").GetString()!;
   if (!path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) continue;
   var relative = path[basePath.Length..].Replace('/', Path.DirectorySeparatorChar);
   if (relative.Split(Path.DirectorySeparatorChar).Any(x => s.ExcludedDirectories.Contains(x, StringComparer.OrdinalIgnoreCase))) continue;
   var output = Path.GetFullPath(Path.Combine(targetDirectory, relative));
   if (!output.StartsWith(Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe TFVC path");
   Directory.CreateDirectory(Path.GetDirectoryName(output)!);
   using var content = await c.GetAsync(Api(s, path) + "&download=true", ct);
   content.EnsureSuccessStatusCode();
   await using var fs = File.Create(output);
   await content.Content.CopyToAsync(fs, ct);
  }
  return new SourceSnapshot(await GetRemoteVersionAsync(s, ct), targetDirectory);
 }
}
