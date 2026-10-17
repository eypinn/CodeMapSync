using CodeMapSync.Options;
namespace CodeMapSync.Discovery;
public interface ISourceDiscoveryProvider
{
 string Type { get; }
 Task<IReadOnlyList<SourceDefinition>> DiscoverAsync(SourceDefinition group, CancellationToken ct);
}
