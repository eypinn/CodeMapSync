using CodeMapSync.Options;
using CodeMapSync.Services;
namespace CodeMapSync.Providers;
public sealed class FileSystemSourceProvider : ISourceProvider
{
 public string Type => "filesystem";
 public async Task<string?> GetRemoteVersionAsync(SourceDefinition source, CancellationToken ct)
 {
  if (!Directory.Exists(source.Location)) throw new DirectoryNotFoundException(source.Location);
  var (hash, _) = await SourceHasher.CalculateAsync(source.Location, source, ct);
  return hash;
 }
 public Task<SourceSnapshot> FetchAsync(SourceDefinition source, string targetDirectory, CancellationToken ct)
 {
  if (!Directory.Exists(source.Location)) throw new DirectoryNotFoundException(source.Location);
  Copy(source.Location, targetDirectory, source.ExcludedDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase), ct);
  return Task.FromResult(new SourceSnapshot(null, targetDirectory));
 }
 private static void Copy(string from, string to, HashSet<string> excludes, CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(to);
  foreach (var file in Directory.EnumerateFiles(from)) { ct.ThrowIfCancellationRequested(); File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true); }
  foreach (var dir in Directory.EnumerateDirectories(from))
   if (!excludes.Contains(Path.GetFileName(dir))) Copy(dir, Path.Combine(to, Path.GetFileName(dir)), excludes, ct);
 }
}
