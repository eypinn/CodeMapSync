using System.Security.Cryptography;
using System.Text;
using CodeMapSync.Options;
namespace CodeMapSync.Services;

public static class SourceHasher
{
    public static async Task<(string Hash, List<SourceFile> Files)> CalculateAsync(string root, SourceDefinition source, CancellationToken ct)
    {
        var files = new List<SourceFile>();
        var excluded = source.ExcludedDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Split('/').Any(excluded.Contains)) continue;
            await using var fs = File.OpenRead(path);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            files.Add(new SourceFile(relative, digest, new FileInfo(path).Length));
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var text = string.Join("\n", files.Select(f => $"{f.Path}\t{f.Hash}"));
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(), files);
    }
}
public sealed record SourceFile(string Path, string Hash, long Size);
