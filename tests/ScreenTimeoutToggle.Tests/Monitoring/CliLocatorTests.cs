using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using OBDim.Monitoring.Providers;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// A07: an entry that EXISTS but cannot be launched safely (.ps1, a bare POSIX shim, an
/// unparsable npm shim) is a different problem from "the CLI was never installed" — the
/// user gets different instructions. The three providers used to collapse every locate
/// error into CliNotFound, which made ProviderErrorKind.UnsupportedEntry dead code.
/// </summary>
public class LocateFailureClassificationTests
{
    private static readonly DateTimeOffset Attempted = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("cli.locate_unsupported_entry")]
    [InlineData("cli.locate_shim_unparsable")]
    [InlineData("cli.locate_shim_too_large")]
    [InlineData("cli.locate_shim_unreadable")]
    public void ShimAndEntryErrors_ClassifyAsUnsupportedEntry(string error)
    {
        var snapshot = SnapshotFactory.LocateFailure(
            ProviderId.MiniMax, new CliLocation { Error = error }, Attempted);

        Assert.Equal(ProviderErrorKind.UnsupportedEntry, snapshot.Error);
        Assert.Equal("monitor.error.unsupported_entry", snapshot.ErrorMessage);
        Assert.False(snapshot.IdentityVerified);
    }

    [Fact]
    public void NeverInstalled_StaysCliNotFound()
    {
        var snapshot = SnapshotFactory.LocateFailure(
            ProviderId.Codex, new CliLocation { Error = "cli.locate_not_found" }, Attempted);

        Assert.Equal(ProviderErrorKind.CliNotFound, snapshot.Error);
        Assert.Equal("monitor.error.cli_not_installed", snapshot.ErrorMessage);
    }

    [Fact]
    public void ConfiguredPathGone_StaysCliNotFound_WithItsOwnMessage()
    {
        var snapshot = SnapshotFactory.LocateFailure(
            ProviderId.Ark, new CliLocation { Error = "cli.locate_configured_missing" }, Attempted);

        Assert.Equal(ProviderErrorKind.CliNotFound, snapshot.Error);
        Assert.Equal("monitor.error.configured_path_missing", snapshot.ErrorMessage);
    }
}

public class CliLocatorTests
{
    [Fact]
    public void ExplicitConfiguredPath_MissingFile_IsHardError()
    {
        var result = CliLocator.Locate(@"C:\definitely\not\here\codex.exe", "codex");
        Assert.False(result.Found);
        Assert.Equal("cli.locate_configured_missing", result.Error);
    }

    [Fact]
    public void ExeOnPath_IsFound()
    {
        // cmd.exe exists on every Windows box the suite can run on.
        var result = CliLocator.Locate(null, "cmd");
        Assert.True(result.Found, $"cmd.exe should resolve via PATH; error={result.Error}");
        Assert.EndsWith("cmd.exe", result.ExePath!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.NodeScriptPath);
    }

    [Fact]
    public void NonExistentName_IsNotFound_NotGuessed()
    {
        var result = CliLocator.Locate(null, "definitely-not-a-cli-8f3a");
        Assert.False(result.Found);
        Assert.Equal("cli.locate_not_found", result.Error);
    }

    [Fact]
    public void Ps1Entry_IsUnsupported()
    {
        var result = CliLocator.ResolveEntry(@"C:\tools\fake.ps1", "--version");
        Assert.False(result.Found);
        Assert.Equal("cli.locate_unsupported_entry", result.Error);
    }

    [Fact]
    public void ShebangScript_IsUnsupported()
    {
        var path = Path.Combine(Path.GetTempPath(), $"obdim-shim-{Guid.NewGuid():N}");
        File.WriteAllText(path, "#!/usr/bin/env node\necho hi\n");
        try
        {
            var result = CliLocator.ResolveEntry(path, "--version");
            Assert.False(result.Found);
            Assert.Equal("cli.locate_unsupported_entry", result.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// npm .cmd shim parsing — the launch path that avoids cmd.exe entirely. Synthetic shims
/// mirror npm's real format (verified against this machine's mmx.cmd / arkcli.cmd).
/// </summary>
public class NodeShimParserTests
{
    [Fact]
    public void QuotedForm_IsParsed()
    {
        var shim = Path.Combine(Path.GetTempPath(), $"obdim-shim-{Guid.NewGuid():N}.cmd");
        var script = Path.Combine(Path.GetTempPath(), $"obdim-script-{Guid.NewGuid():N}.js");
        File.WriteAllText(script, "// entry\n");
        File.WriteAllText(shim,
            "@ECHO off\r\n" +
            "@IF EXIST \"%~dp0\\node.exe\" (\r\n" +
            "  \"%~dp0\\node.exe\"  \"%~dp0\\real-script.js\" %*\r\n" +
            ") ELSE (\r\n" +
            "  node \"%~dp0\\real-script.js\" %*\r\n" +
            ")\r\n");
        try
        {
            // The shim points at "%~dp0\real-script.js" which does not exist in temp —
            // but a sibling script named real-script.js must be resolved via %~dp0.
            var realSibling = Path.Combine(Path.GetTempPath(), "real-script.js");
            File.WriteAllText(realSibling, "// sibling\n");
            try
            {
                var ok = NodeShimParser.TryParseCmdShim(shim, out var scriptPath, out var error);
                Assert.True(ok, $"error={error}");
                Assert.Equal(realSibling, scriptPath);
            }
            finally
            {
                File.Delete(realSibling);
            }
        }
        finally
        {
            File.Delete(shim);
            File.Delete(script);
        }
    }

    [Fact]
    public void UnquotedForm_IsParsed()
    {
        var shim = Path.Combine(Path.GetTempPath(), $"obdim-shim-{Guid.NewGuid():N}.cmd");
        var script = Path.Combine(Path.GetTempPath(), $"obdim-unq-{Guid.NewGuid():N}.js");
        File.WriteAllText(script, "// entry\n");
        File.WriteAllText(shim, $"@node.exe \"{script}\" %*\r\n");
        try
        {
            var ok = NodeShimParser.TryParseCmdShim(shim, out var scriptPath, out var error);
            Assert.True(ok, $"error={error}");
            Assert.Equal(script, scriptPath);
        }
        finally
        {
            File.Delete(shim);
            File.Delete(script);
        }
    }

    /// <summary>Newer npm shim style: %dp0% set via CALL :find_dp0 instead of %~dp0.</summary>
    [Fact]
    public void PercentDp0Form_IsParsed()
    {
        var shim = Path.Combine(Path.GetTempPath(), $"obdim-shim-{Guid.NewGuid():N}.cmd");
        var script = Path.Combine(Path.GetTempPath(), $"real-script-{Guid.NewGuid():N}.js");
        File.WriteAllText(script, "// entry\n");
        var shimDir = Path.GetDirectoryName(shim)!;
        File.WriteAllText(shim,
            "@ECHO off\r\n" +
            "GOTO start\r\n" +
            ":find_dp0\r\n" +
            "SET dp0=%~dp0\r\n" +
            "EXIT /b\r\n" +
            ":start\r\n" +
            "SETLOCAL\r\n" +
            "CALL :find_dp0\r\n" +
            $"endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\"  \"%dp0%\\{Path.GetFileName(script)}\" %*\r\n");
        try
        {
            var ok = NodeShimParser.TryParseCmdShim(shim, out var scriptPath, out var error);
            Assert.True(ok, $"error={error}");
            Assert.Equal(script, scriptPath);
        }
        finally
        {
            File.Delete(shim);
            File.Delete(script);
        }
    }

    /// <summary>mmx ships an .mjs entry — the shim target must accept it.</summary>
    [Fact]
    public void MjsEntry_IsParsed()
    {
        var shim = Path.Combine(Path.GetTempPath(), $"obdim-shim-{Guid.NewGuid():N}.cmd");
        var script = Path.Combine(Path.GetTempPath(), $"real-entry-{Guid.NewGuid():N}.mjs");
        File.WriteAllText(script, "// entry\n");
        var shimDir = Path.GetDirectoryName(shim)!;
        File.WriteAllText(shim,
            "@ECHO off\r\n" +
            "GOTO start\r\n" +
            ":find_dp0\r\n" +
            "SET dp0=%~dp0\r\n" +
            "EXIT /b\r\n" +
            ":start\r\n" +
            "SETLOCAL\r\n" +
            "CALL :find_dp0\r\n" +
            $"endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\"  \"%dp0%\\{Path.GetFileName(script)}\" %*\r\n");
        try
        {
            var ok = NodeShimParser.TryParseCmdShim(shim, out var scriptPath, out var error);
            Assert.True(ok, $"error={error}");
            Assert.Equal(script, scriptPath);
        }
        finally
        {
            File.Delete(shim);
            File.Delete(script);
        }
    }

    [Fact]
    public void UnparsableShim_ReportsError_NotGuesses()
    {
        var shim = Path.Combine(Path.GetTempPath(), $"obdim-shim-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(shim, "rem nothing useful here\r\n");
        try
        {
            var ok = NodeShimParser.TryParseCmdShim(shim, out _, out var error);
            Assert.False(ok);
            Assert.NotNull(error);
        }
        finally
        {
            File.Delete(shim);
        }
    }

    [Fact]
    public void ShimPointingAtMissingScript_Fails()
    {
        var shim = Path.Combine(Path.GetTempPath(), $"obdim-shim-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(shim, $"@node.exe \"{Path.Combine(Path.GetTempPath(), "missing-{Guid.NewGuid():N}.js")}\" %*\r\n");
        try
        {
            var ok = NodeShimParser.TryParseCmdShim(shim, out _, out _);
            Assert.False(ok);
        }
        finally
        {
            File.Delete(shim);
        }
    }
}
