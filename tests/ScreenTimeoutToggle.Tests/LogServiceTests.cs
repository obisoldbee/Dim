using OBDim.Services;
using Xunit;

namespace OBDim.Tests;

/// <summary>
/// v1.0.6: tests must not write into the user's real log file.
/// </summary>
public class LogServiceTests
{
    /// <summary>
    /// The assembly-level module initializer must have redirected logging before any test
    /// ran. Without this, every fake powercfg run would append synthetic
    /// "powercfg attempt" lines to %AppData%\ScreenTimeoutToggle\log.txt, drowning the
    /// real evidence the log was meant to capture.
    /// </summary>
    [Fact]
    public void OverridePath_IsSetForTheWholeTestAssembly()
    {
        Assert.False(string.IsNullOrWhiteSpace(LogService.OverridePath));
        Assert.Equal(TestLogIsolation.LogFilePath, LogService.OverridePath);
    }

    /// <summary>
    /// The redirect must point outside the user's real profile, otherwise it is not
    /// isolation at all.
    /// </summary>
    [Fact]
    public void OverridePath_IsNotTheRealUserLog()
    {
        var realLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenTimeoutToggle",
            "log.txt");

        Assert.NotEqual(realLog, LogService.OverridePath);
    }

    /// <summary>
    /// Writing through the redirected path must actually create the temp file, proving the
    /// override is wired into the write path and not just a dormant property.
    /// </summary>
    [Fact]
    public void Log_WritesToTheOverridePath()
    {
        const string marker = "log-service-test-marker";
        var path = LogService.OverridePath!;
        var before = File.Exists(path) ? new FileInfo(path).Length : 0;

        LogService.Info(marker);

        Assert.True(File.Exists(path), $"expected log output at {path}");
        Assert.True(new FileInfo(path).Length >= before);

        // Other test classes log concurrently to this same file, so it can be open for
        // writing right now. FileShare.ReadWrite keeps the assertion from tripping over
        // them (File.ReadAllText would open it share-read and throw).
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        Assert.Contains(marker, reader.ReadToEnd());
    }
}
