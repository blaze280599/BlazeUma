using System.Diagnostics;

namespace BlazeUma.Cli;

/// <summary>
/// The picks another commit's advisor makes on the same logs: checks the ref out in a temporary git worktree, runs
/// its own <c>replay</c> there and reads the pick after "->" (or the logged pick when it didn't flip) from its
/// output, whose format hasn't changed since the replay harness was added.
/// </summary>
internal static class ReplayBaseline
{
    /// <summary>Picks per log file name, in record order, as (turn, kind, pick); null with an error printed on failure.</summary>
    public static Dictionary<string, List<(int Turn, string Kind, string Pick)>>? Run(string gitRef, IEnumerable<string> paths, string db)
    {
        string? root = Git(Environment.CurrentDirectory, "rev-parse", "--show-toplevel")?.Trim();
        if (root is null) return Fail("--baseline needs to run inside the BlazeUma git repository");
        string dir = Path.Combine(Path.GetTempPath(), $"blazeuma-baseline-{Guid.NewGuid():N}");
        if (Git(root, "worktree", "add", "--detach", dir, gitRef) is null) return Fail($"could not check out {gitRef}");
        try
        {
            Console.Error.WriteLine($"building and replaying {gitRef}…");
            var args = new List<string> { "run", "--project", Path.Combine("src", "BlazeUma.Cli"), "--", "replay", "--db", db };
            args.AddRange(paths.Select(Path.GetFullPath));
            var (code, output) = Exec(dir, "dotnet", args);
            if (code != 0) return Fail($"{gitRef}'s replay failed (exit {code}):\n{output}");
            return Parse(output);
        }
        finally
        {
            Git(root, "worktree", "remove", "--force", dir);
        }
    }

    private static Dictionary<string, List<(int, string, string)>> Parse(string output)
    {
        var picks = new Dictionary<string, List<(int, string, string)>>();
        List<(int Turn, string Kind, string Pick)>? current = null;
        foreach (string line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.EndsWith(".jsonl") && !line.StartsWith(' ')) picks[line] = current = [];
            else if (current is not null && line.TrimStart().StartsWith("-> ") && current.Count > 0)
                current[^1] = current[^1] with { Pick = line.TrimStart()[3..] };
            else if (current is not null && line.Length > 17 && int.TryParse(line[..5], out int turn))
                current.Add((turn, line[6..11].Trim(), line[17..]));
        }
        return picks;
    }

    private static Dictionary<string, List<(int, string, string)>>? Fail(string message)
    {
        Console.Error.WriteLine(message);
        return null;
    }

    private static string? Git(string dir, params string[] args)
    {
        var (code, output) = Exec(dir, "git", args);
        return code == 0 ? output : null;
    }

    private static (int Code, string Output) Exec(string dir, string file, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(file) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        info.StandardOutputEncoding = System.Text.Encoding.UTF8;
        foreach (string a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, process.ExitCode == 0 ? output : output + error.Result);
    }
}
