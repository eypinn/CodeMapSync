namespace CodeMapSync.Models;
public sealed class CodeMapDocument
{
 public string SchemaVersion { get; set; } = "1.1";
 public string ProjectId { get; set; } = "";
 public string ProjectName { get; set; } = "";
 public string SourceId { get; set; } = "";
 public string SourceRevision { get; set; } = "";
 public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
 public List<CodeMapEntry> Entries { get; set; } = [];
}
public sealed class CodeMapEntry
{
 public string UniqueId { get; set; } = "";
 public string ProjectId { get; set; } = "";
 public string ProjectName { get; set; } = "";
 public string Key { get; set; } = "";
 public List<string> SearchKeys { get; set; } = [];
 public string? Label { get; set; }
 public string Kind { get; set; } = "unknown";
 public string Language { get; set; } = "unknown";
 public string FilePath { get; set; } = "";
 public int? StartLineIndex { get; set; }
 public int? EndLineIndex { get; set; }
 public string? EndpointName { get; set; }
 public string? HttpMethod { get; set; }
 public string? Route { get; set; }
 public string? RequestUri { get; set; }
 public string? DatabaseServer { get; set; }
 public string? DatabaseName { get; set; }
 public string? SchemaName { get; set; }
 public string? TableName { get; set; }
 public List<string> RelatedMapObjectIds { get; set; } = [];
 public List<CodeMapRelation> Relations { get; set; } = [];
 public List<CodeMapIncomingRelation> IncomingRelations { get; set; } = [];
 public string ResolutionStatus { get; set; } = "discovered";
 public List<CodeEvidence> Evidence { get; set; } = [];
 public Dictionary<string, string> Attributes { get; set; } = [];
}
public sealed class CodeMapRelation
{
 public string TargetId { get; set; } = "";
 public string RelationType { get; set; } = "references";
 public string Status { get; set; } = "candidate";
 public string? DiscoveryMethod { get; set; }
 public string? MatchStrength { get; set; }
 public double? MatchScore { get; set; }
}
// A reverse index of original directed relations; the relation type and evidence are preserved.
public sealed class CodeMapIncomingRelation
{
 public string SourceId { get; set; } = "";
 public string RelationType { get; set; } = "references";
 public string Status { get; set; } = "candidate";
 public string? DiscoveryMethod { get; set; }
 public string? MatchStrength { get; set; }
 public double? MatchScore { get; set; }
}
public sealed class CodeEvidence
{
 public string FilePath { get; set; } = "";
 public int? LineIndex { get; set; }
 public string? Detail { get; set; }
}
