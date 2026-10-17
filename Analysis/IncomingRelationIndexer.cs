using CodeMapSync.Models;

namespace CodeMapSync.Analysis;

/// <summary>Materializes the reverse index of all known outgoing edges across all loaded maps.
/// It never invents a call or changes the original direction or confidence.</summary>
public sealed class IncomingRelationIndexer
{
    public int Rebuild(IReadOnlyCollection<CodeMapDocument> maps)
    {
        var entries = maps.SelectMany(m => m.Entries).ToArray();
        var byId = entries.GroupBy(e => e.UniqueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) entry.IncomingRelations.Clear();
        foreach (var source in entries)
        {
            // Relations is canonical; RelatedMapObjectIds is a legacy projection and
            // must not create invented, untyped edges.
            foreach (var relation in source.Relations)
            {
                if (!byId.TryGetValue(relation.TargetId, out var targets)) continue;
                foreach (var target in targets)
                {
                    if (target.IncomingRelations.Any(r =>
                        string.Equals(r.SourceId, source.UniqueId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.RelationType, relation.RelationType, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.DiscoveryMethod, relation.DiscoveryMethod, StringComparison.OrdinalIgnoreCase))) continue;
                    target.IncomingRelations.Add(new CodeMapIncomingRelation
                    {
                        SourceId = source.UniqueId,
                        RelationType = relation.RelationType,
                        Status = relation.Status,
                        DiscoveryMethod = relation.DiscoveryMethod,
                        MatchStrength = relation.MatchStrength,
                        MatchScore = relation.MatchScore
                    });
                }
            }
        }
        foreach (var entry in entries)
            entry.IncomingRelations = entry.IncomingRelations
                .OrderBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.RelationType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.DiscoveryMethod, StringComparer.OrdinalIgnoreCase).ToList();
        return entries.Sum(e => e.IncomingRelations.Count);
    }
}
