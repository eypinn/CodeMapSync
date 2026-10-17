using CodeMapSync.Options;
namespace CodeMapSync.Providers;
public interface ISourceProvider
{
 string Type { get; }
 Task<string?> GetRemoteVersionAsync(SourceDefinition source, CancellationToken ct);
 Task<SourceSnapshot> FetchAsync(SourceDefinition source, string targetDirectory, CancellationToken ct);
}
public sealed record SourceSnapshot(string? NativeRevision, string LocalPath);
