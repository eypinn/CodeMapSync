using System.Diagnostics;
namespace CodeMapSync.Services;

public static class ProcessRunner
{
    public static async Task<string> RunAsync(string file, IEnumerable<string> args, string? cwd, CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var p = new Process { StartInfo = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        }};
        foreach (var arg in args) p.StartInfo.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var pair in environment) p.StartInfo.Environment[pair.Key] = pair.Value;
        if (!p.Start()) throw new InvalidOperationException($"Unable to start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var output = await stdout;
        var error = await stderr;
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} exited {p.ExitCode}: {error[..Math.Min(error.Length, 1000)]}");
        return output.Trim();
    }
}
