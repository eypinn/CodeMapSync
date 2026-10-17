using CodeMapSync.Options;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
namespace CodeMapSync.Providers;
public sealed class GitSourceProvider : ISourceProvider
{
 public string Type => "git";
 private static CredentialsHandler? Credential(SourceDefinition s)
 {
  if (string.IsNullOrWhiteSpace(s.CredentialEnvironmentVariable)) return null;
  var token = Environment.GetEnvironmentVariable(s.CredentialEnvironmentVariable)
   ?? throw new InvalidOperationException($"Credential environment variable missing: {s.CredentialEnvironmentVariable}");
  return (_, _, _) => new UsernamePasswordCredentials { Username = string.IsNullOrEmpty(s.Username) ? "git" : s.Username, Password = token };
 }
 public Task<string?> GetRemoteVersionAsync(SourceDefinition s, CancellationToken ct) => Task.Run(() =>
 {
  ct.ThrowIfCancellationRequested();
  var branch = "refs/heads/" + s.Branch;
  var item = Repository.ListRemoteReferences(s.Location, Credential(s))
    .FirstOrDefault(x => x.CanonicalName == branch)
    ?? throw new InvalidOperationException($"Remote branch not found: {branch}");
  return (string?)item.TargetIdentifier;
 }, ct);
 public Task<SourceSnapshot> FetchAsync(SourceDefinition s, string targetDirectory, CancellationToken ct) => Task.Run(() =>
 {
  ct.ThrowIfCancellationRequested();
  var options = new CloneOptions { BranchName = s.Branch, IsBare = false };
  options.FetchOptions.CredentialsProvider = Credential(s);
  Repository.Clone(s.Location, targetDirectory, options);
  using var repo = new Repository(targetDirectory);
  return new SourceSnapshot(repo.Head.Tip?.Sha, targetDirectory);
 }, ct);
}
