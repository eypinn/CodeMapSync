using CodeMapSync.Models;
namespace CodeMapSync.Analysis;
public interface ICodeAnalyzer
{
 bool Supports(string path);
 IEnumerable<CodeMapEntry> Analyze(string sourceId, string projectName, string relativePath, string content);
}
public static class MapIds
{
 public static string Make(string source, string path, string kind, string key, int line) =>
  Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{source}|{path}|{kind}|{key}|{line}")))[..24].ToLowerInvariant();
 public static CodeMapEntry New(string source, string project, string path, string language, string kind, string key, int line) => new()
 {
  UniqueId = Make(source, path, kind, key, line), ProjectId = source, ProjectName = project,
  FilePath = path, Language = language, Kind = kind, Key = key,
  StartLineIndex = line, EndLineIndex = line, SearchKeys = [key],
  Evidence = [new CodeEvidence { FilePath = path, LineIndex = line, Detail = "syntax discovery" }]
 };
}
