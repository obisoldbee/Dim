using System.Runtime.CompilerServices;
using OBDim.Services;

namespace OBDim.Tests;

/// <summary>
/// Redirects <see cref="LogService"/> to a throwaway file for the whole test assembly.
/// </summary>
/// <remarks>
/// v1.0.6: <see cref="PowerConfigService"/> logs every invocation, and the tests drive it
/// with fake runners hundreds of times per run. Those synthetic lines used to land in the
/// user's real <c>%AppData%\ScreenTimeoutToggle\log.txt</c>: a single run added hundreds
/// of <c>powercfg attempt</c> lines, which buries real evidence and makes the 1 MB trim
/// discard genuine history first.
/// <para>
/// <see cref="ModuleInitializerAttribute"/> runs before any test code in this assembly, so
/// isolation is on for every test with no per-test boilerplate to forget.
/// </para>
/// </remarks>
internal static class TestLogIsolation
{
    /// <summary>File the test assembly logs to instead of the user's real log.</summary>
    internal static string LogFilePath { get; } =
        Path.Combine(Path.GetTempPath(), "OBDimTests", "log.txt");

    [ModuleInitializer]
    internal static void Initialize()
    {
        var dir = Path.GetDirectoryName(LogFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        LogService.OverridePath = LogFilePath;
    }
}
