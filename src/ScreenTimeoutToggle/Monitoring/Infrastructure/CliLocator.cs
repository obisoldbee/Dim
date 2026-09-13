using System.Diagnostics;

namespace OBDim.Monitoring.Infrastructure;

/// <summary>Result of locating a provider CLI. Exactly one of <see cref="ExePath"/>/<see cref="Error"/> is meaningful.</summary>
public sealed record CliLocation
{
    public string? ExePath { get; init; }

    /// <summary>For npm-style shims: the node script the shim points at, to be launched as `node.exe <script>`.</summary>
    public string? NodeScriptPath { get; init; }

    /// <summary>Version probe argument form, e.g. "--version". Null when unknown.</summary>
    public string? VersionArgs { get; init; }

    public string? Error { get; init; }

    public bool Found => ExePath is not null;
}

/// <summary>
/// Locates provider CLIs. Resolution order (plan §2.2): the user's explicitly configured
/// path (validated — an invalid explicit path is a hard error, never silently replaced
/// by another account's environment), then a PATH search. npm .cmd shims are resolved to
/// their underlying node script so we can launch node.exe directly instead of routing
/// through cmd.exe metacharacter interpretation.
/// </summary>
public static class CliLocator
{
    internal const int MaxShimBytes = 64 * 1024;

    /// <summary>
    /// Locates a CLI. <paramref name="exeName"/> is the bare executable name ("codex",
    /// "mmx", "arkcli"). Windows executable extensions are tried in order.
    /// </summary>
    public static CliLocation Locate(string? configuredPath, string exeName, string? versionArgs = "--version")
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var trimmed = configuredPath.Trim().Trim('"');
            if (!File.Exists(trimmed))
            {
                return new CliLocation
                {
                    Error = "cli.locate_configured_missing",
                    VersionArgs = versionArgs,
                };
            }
            return ResolveEntry(trimmed, versionArgs);
        }

        foreach (var dir in SearchDirectories())
        {
            var candidate = Path.Combine(dir, exeName);
            foreach (var ext in ExecutableExtensions(exeName))
            {
                var full = candidate + ext;
                if (File.Exists(full)) return ResolveEntry(full, versionArgs);
            }
            if (File.Exists(candidate)) return ResolveEntry(candidate, versionArgs);
        }

        return new CliLocation { Error = "cli.locate_not_found", VersionArgs = versionArgs };
    }

    /// <summary>PATH entries of the current user environment, deduplicated, keeping order.</summary>
    private static IEnumerable<string> SearchDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(raw) && Directory.Exists(raw)) yield return raw;
        }
    }

    private static IEnumerable<string> ExecutableExtensions(string exeName)
    {
        // npm shims install three files side by side: "mmx" (sh script), "mmx.cmd", "mmx.ps1".
        // The bare name is a POSIX script that Process.Start cannot execute, so on Windows
        // we must prefer .exe, then the .cmd shim. The .ps1 is deliberately not used.
        if (exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return ["", ".exe"];
        return [".exe", ".cmd", ""];
    }

    /// <summary>
    /// Decides HOW an existing file is launched. A native .exe launches directly; an npm
    /// .cmd shim launches as node.exe + parsed script; anything else (a bare POSIX shim,
    /// a .ps1, an unparsable .cmd) is reported as unsupported instead of being executed
    /// through a guessed interpreter.
    /// </summary>
    public static CliLocation ResolveEntry(string fullPath, string? versionArgs)
    {
        if (fullPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = NodeShimParser.TryParseCmdShim(fullPath, out var scriptPath, out var error);
            if (!parsed)
            {
                return new CliLocation { Error = error ?? "cli.locate_unsupported_entry", VersionArgs = versionArgs };
            }
            return new CliLocation { ExePath = fullPath, NodeScriptPath = scriptPath, VersionArgs = versionArgs };
        }

        if (fullPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
            (!fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && LooksLikeShellScript(fullPath)))
        {
            return new CliLocation { Error = "cli.locate_unsupported_entry", VersionArgs = versionArgs };
        }

        // Bare names and .exe files launch directly. Anything that exists and is not a
        // known script shape goes through CreateProcess (it either runs or fails honestly).
        return new CliLocation { ExePath = fullPath, VersionArgs = versionArgs };
    }

    private static bool LooksLikeShellScript(string path)
    {
        try
        {
            var head = new byte[2];
            using var fs = File.OpenRead(path);
            var read = fs.Read(head, 0, 2);
            return read >= 2 && head[0] == (byte)'#' && head[1] == (byte)'!';
        }
        catch (IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// Parses npm's .cmd shim format to find the underlying node script:
/// <code>
/// @IF EXIST "%~dp0\node.exe" (
///   "%~dp0\node.exe"  "%~dp0\node_modules\minimax-cli\dist\cli.js" %*
/// ) ELSE ( ... )
/// </code>
/// The last quoted token ending in .js before %* is the script path; "%~dp0" prefixes are
/// expanded against the shim's own directory. Launching node.exe + script directly avoids
/// cmd.exe, so no metacharacter can be interpreted as shell syntax — the plan's required
/// "verified node.exe + CLI script entry" (§2.2(3)).
/// </summary>
public static class NodeShimParser
{
    public static bool TryParseCmdShim(string cmdPath, out string scriptPath, out string? error)
    {
        scriptPath = "";
        error = null;

        string content;
        try
        {
            var info = new FileInfo(cmdPath);
            if (info.Length > CliLocator.MaxShimBytes)
            {
                error = "cli.locate_shim_too_large";
                return false;
            }
            content = File.ReadAllText(cmdPath);
        }
        catch (IOException)
        {
            error = "cli.locate_shim_unreadable";
            return false;
        }

        var shimDir = Path.GetDirectoryName(Path.GetFullPath(cmdPath)) ?? "";
        string? best = null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var idx = line.IndexOf(".js", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            // Take the last double-quoted segment on the line that ends in .js.
            var lastQuote = line.LastIndexOf('"');
            var firstQuote = line.LastIndexOf('"', Math.Max(0, lastQuote - 1));
            if (lastQuote > 0 && firstQuote >= 0 && lastQuote > firstQuote)
            {
                var token = line[(firstQuote + 1)..lastQuote];
                if (token.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                {
                    best = ExpandShimPath(token, shimDir);
                    continue;
                }
            }

            // Unquoted form: node.exe C:\path\to\cli.js %*
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var tok in tokens.Reverse())
            {
                if (tok.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && !tok.StartsWith('%'))
                {
                    best = ExpandShimPath(tok.Trim('"'), shimDir);
                    break;
                }
            }
        }

        if (best is null || !File.Exists(best))
        {
            error = "cli.locate_shim_unparsable";
            return false;
        }

        scriptPath = best;
        return true;
    }

    private static string ExpandShimPath(string token, string shimDir)
    {
        // Two npm shim generations exist: "%~dp0\..." (classic) and "%dp0%\..." (the
        // CALL :find_dp0 style this machine ships). Expand both, case-insensitively.
        // The shimDir substitution plus the token's own separator can double the
        // backslash — collapse that again (UNC paths never appear in npm shims).
        var expanded = token
            .Replace("%~dp0", shimDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("%dp0%", shimDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\"", "", StringComparison.Ordinal);
        return expanded;
    }
}
