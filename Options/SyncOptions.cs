namespace CodeMapSync.Options;
public sealed class SyncOptions
{
 public int IntervalMinutes { get; set; } = 60;
 public string WorkspaceRoot { get; set; } = "workspace";
 public string ContainerName { get; set; } = "codemap-source";
 public string? StorageConnectionString { get; set; }
 public string? StorageServiceUri { get; set; }
 public string? TriggerApiKey { get; set; }
 public List<SourceDefinition> Sources { get; set; } = [];
}
public sealed class SourceDefinition
{
 public string Id { get; set; } = "";
 public string Name { get; set; } = "";
 public string Type { get; set; } = "";
 public string Location { get; set; } = "";
 public string Branch { get; set; } = "main";
 public string? ServerUrl { get; set; }
 public string? Username { get; set; }
 public string? CredentialEnvironmentVariable { get; set; }
 public bool Enabled { get; set; } = true;
 public GitHubDiscoveryOptions? Discovery { get; set; }
 public List<string> ExcludedDirectories { get; set; } = [".git", "$tf", "bin", "obj", "node_modules", ".vs"];
}

public sealed class GitHubDiscoveryOptions
{
 public string Owner { get; set; } = "";
 // user or organization; only public repositories are discovered.
 public string OwnerType { get; set; } = "user";
 public List<string> IncludeRepositories { get; set; } = ["*"];
 public List<string> ExcludeRepositories { get; set; } = [];
 public bool IncludeForks { get; set; } = false;
 public bool IncludeArchived { get; set; } = false;
 public int MaxRepositories { get; set; } = 500;
}
