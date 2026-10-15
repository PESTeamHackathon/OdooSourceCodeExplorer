using System.Diagnostics;

namespace Chat.Indexer;

internal static class GitRepository
{
    public static async Task<string> RunAsync(string path, CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in new[] { "--no-optional-locks", "-c", "core.fsmonitor=false", "-C", path }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Git.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"Git failed: {error.Trim()}");
        return output;
    }
}
