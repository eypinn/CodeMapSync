using Azure.Identity;
using Azure.Storage.Blobs;
using CodeMapSync.Options;
using CodeMapSync.Models;
using System.Text.Json;
namespace CodeMapSync.Services;
public sealed class BlobSnapshotStore
{
 private readonly BlobContainerClient _container;
 private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
 public BlobSnapshotStore(SyncOptions options)
 {
  var client = !string.IsNullOrWhiteSpace(options.StorageConnectionString)
   ? new BlobServiceClient(options.StorageConnectionString)
   : new BlobServiceClient(new Uri(options.StorageServiceUri ?? throw new InvalidOperationException("Set Sync:StorageServiceUri")), new DefaultAzureCredential());
  _container = client.GetBlobContainerClient(options.ContainerName);
 }
 public async Task<SourceManifest?> ReadManifestAsync(string id, CancellationToken ct)
 {
  var blob = _container.GetBlobClient($"sources/{id}/manifest.json");
  if (!(await blob.ExistsAsync(ct)).Value) return null;
  var response = await blob.DownloadContentAsync(ct);
  return response.Value.Content.ToObjectFromJson<SourceManifest>(Json);
 }
 public async Task UploadAsync(string id, string root, SourceManifest manifest, CancellationToken ct)
 {
  await _container.CreateIfNotExistsAsync(cancellationToken: ct);
  var prefix = $"sources/{id}/snapshots/{manifest.ContentHash}/";
  foreach (var file in manifest.Files)
  {
   var path = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
   if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe relative path");
   var blob = _container.GetBlobClient(prefix + file.Path);
   if ((await blob.ExistsAsync(ct)).Value) continue;
   await using var stream = File.OpenRead(path);
   await blob.UploadAsync(stream, overwrite: true, cancellationToken: ct);
  }
  await _container.GetBlobClient($"sources/{id}/manifest.json")
   .UploadAsync(BinaryData.FromObjectAsJson(manifest, Json), overwrite: true, cancellationToken: ct);
 }
 public async Task<CodeMapDocument?> ReadCodeMapAsync(string id, string revision, CancellationToken ct)
 {
  var blob = _container.GetBlobClient($"codemaps/{id}/{revision}/map.json");
  if (!(await blob.ExistsAsync(ct)).Value) return null;
  var response = await blob.DownloadContentAsync(ct);
  return response.Value.Content.ToObjectFromJson<CodeMapDocument>(Json);
 }
 public async Task<CodeMapDocument?> ReadLatestCodeMapAsync(string id, CancellationToken ct)
 {
  var latest=_container.GetBlobClient($"codemaps/{id}/latest.json");
  if (!(await latest.ExistsAsync(ct)).Value) return null;
  var content=await latest.DownloadContentAsync(ct);
  using var json=JsonDocument.Parse(content.Value.Content.ToString());
  if (!json.RootElement.TryGetProperty("revision",out var revision)) return null;
  return await ReadCodeMapAsync(id,revision.GetString() ?? "",ct);
 }
 public async Task<List<CodeMapDocument>> ListLatestCodeMapsAsync(CancellationToken ct)
 {
  var maps = new List<CodeMapDocument>();
  await foreach (var blob in _container.GetBlobsAsync(prefix: "codemaps/", cancellationToken: ct))
  {
   if (!blob.Name.EndsWith("/latest.json", StringComparison.OrdinalIgnoreCase)) continue;
   var id = blob.Name["codemaps/".Length..^"/latest.json".Length];
   if (string.IsNullOrWhiteSpace(id) || id.Contains('/')) continue;
   var map = await ReadLatestCodeMapAsync(id, ct);
   if (map is not null) maps.Add(map);
  }
  return maps;
 }
 public async Task UploadCodeMapAsync(string id, string revision, CodeMapDocument map, CancellationToken ct)
 {
  await _container.CreateIfNotExistsAsync(cancellationToken: ct);
  await _container.GetBlobClient($"codemaps/{id}/{revision}/map.json")
   .UploadAsync(BinaryData.FromObjectAsJson(map, Json), overwrite: true, cancellationToken: ct);
  await _container.GetBlobClient($"codemaps/{id}/latest.json")
   .UploadAsync(BinaryData.FromObjectAsJson(new { revision, mapPath = $"codemaps/{id}/{revision}/map.json", indexedAtUtc = DateTimeOffset.UtcNow }, Json), overwrite: true, cancellationToken: ct);
 }
}
public sealed class SourceManifest
{
 public string SourceId { get; set; } = "";
 public string SourceType { get; set; } = "";
 public string? AnalyzerVersion { get; set; }
 public string? NativeRevision { get; set; }
 public string ContentHash { get; set; } = "";
 public DateTimeOffset SyncedAtUtc { get; set; }
 public List<SourceFile> Files { get; set; } = [];
}
