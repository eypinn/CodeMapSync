using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMapSync.Options;
namespace CodeMapSync.Discovery;

// GitHub public repository discovery. Does not need a GitHub token.
public sealed class GitHubDiscoveryProvider(IHttpClientFactory factory, ILogger<GitHubDiscoveryProvider> logger) : ISourceDiscoveryProvider
{
 public string Type => "github";
 public async Task<IReadOnlyList<SourceDefinition>> DiscoverAsync(SourceDefinition group, CancellationToken ct)
 {
  var settings = group.Discovery ?? throw new InvalidOperationException($"Discovery settings missing for {group.Id}");
  if (!Regex.IsMatch(settings.Owner, "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$"))
   throw new InvalidOperationException("Invalid GitHub owner");
  if (settings.MaxRepositories is < 1 or > 10000) throw new InvalidOperationException("MaxRepositories must be 1..10000");
  var kind = settings.OwnerType.ToLowerInvariant() switch
  {
   "user" => "users",
   "organization" or "org" => "orgs",
   _ => throw new InvalidOperationException("OwnerType must be user or organization")
  };
  using var client = factory.CreateClient("github-discovery");
  var discovered = new List<SourceDefinition>();
  for (var page = 1; page <= 100; page++)
  {
   using var response = await client.GetAsync($"https://api.github.com/{kind}/{settings.Owner}/repos?type=public&per_page=100&page={page}", ct);
   if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests)
    throw new HttpRequestException("GitHub API rate limit or access restriction. Retry later or configure authenticated discovery.");
   response.EnsureSuccessStatusCode();
   await using var stream = await response.Content.ReadAsStreamAsync(ct);
   using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
   var rows = json.RootElement;
   if (rows.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Unexpected GitHub response");
   foreach (var item in rows.EnumerateArray())
   {
    if (item.GetProperty("private").GetBoolean()) continue;
    var name = item.GetProperty("name").GetString()!;
    if (!Matches(name, settings.IncludeRepositories) || Matches(name, settings.ExcludeRepositories)) continue;
    if (!settings.IncludeForks && item.GetProperty("fork").GetBoolean()) continue;
    if (!settings.IncludeArchived && item.GetProperty("archived").GetBoolean()) continue;
    var cloneUrl = item.GetProperty("clone_url").GetString()!;
    if (!Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com")
     throw new InvalidOperationException("Unexpected clone URL host");
    var branch = item.TryGetProperty("default_branch", out var b) ? b.GetString() : null;
    if (string.IsNullOrWhiteSpace(branch)) continue;
    // A deterministic, blob-path-safe source id; no collisions between similarly named repos.
    var identity = item.GetProperty("full_name").GetString()!;
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToLowerInvariant())))[..16].ToLowerInvariant();
    discovered.Add(new SourceDefinition
    {
     Id = $"{group.Id}-{hash}", Name = identity, Type = "git", Location = cloneUrl,
     Branch = branch, Enabled = true, ExcludedDirectories = [..group.ExcludedDirectories]
    });
    if (discovered.Count >= settings.MaxRepositories)
    {
     logger.LogWarning("GitHub group {GroupId} reached MaxRepositories={Max}", group.Id, settings.MaxRepositories);
     return discovered;
    }
   }
   if (rows.GetArrayLength() < 100) break;
  }
  return discovered;
 }
 private static bool Matches(string name, IEnumerable<string> patterns) => patterns.Any(pattern =>
  Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
}
