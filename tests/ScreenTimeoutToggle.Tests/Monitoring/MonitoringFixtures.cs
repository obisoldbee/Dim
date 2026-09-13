namespace OBDim.Tests;

/// <summary>Loads desensitized JSON fixtures from the test output directory.</summary>
internal static class MonitoringFixtures
{
    public static string Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Monitoring", fileName);
        return File.ReadAllText(path);
    }
}
