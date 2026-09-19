using System.Runtime.Versioning;
using System.Security.Principal;

namespace OBDim.NetworkProbe;

/// <summary>Shared probe context: elevation state and a uniform section header.</summary>
[SupportedOSPlatform("windows")]
internal static class ProbeContext
{
    private static readonly Lazy<bool> ElevatedCache = new(DetectElevation);

    public static bool IsElevated => ElevatedCache.Value;

    public static void Header(string section)
    {
        Console.WriteLine($"== {section} ==");
        Console.WriteLine($"elevated={IsElevated} os={Environment.OSVersion.Version} machine64={Environment.Is64BitOperatingSystem}");
    }

    private static bool DetectElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
