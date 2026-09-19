using System.Diagnostics;

namespace OBDim.NetworkProbe;

/// <summary>
/// Probe (c): process identity. Verifies Process.StartTime readability across all
/// processes without elevation, and documents the PID-reuse scenario that makes
/// pid+startTime the required joint identity in the contract.
/// </summary>
internal static class ProcessProbe
{
    public static int Run()
    {
        ProbeContext.Header("process");
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"error: GetProcesses threw {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        var total = 0;
        var startTimeOk = 0;
        var startTimeDenied = 0;
        var nameOk = 0;
        var nameDenied = 0;
        var samples = new List<string>();

        foreach (var proc in processes)
        {
            total++;
            string? name = null;
            try
            {
                name = proc.ProcessName;
                nameOk++;
            }
            catch
            {
                nameDenied++;
            }

            try
            {
                var start = proc.StartTime;
                startTimeOk++;
                if (samples.Count < 8 && name is not null)
                    samples.Add($"pid={proc.Id} name={name} start-utc={start.ToUniversalTime():O}");
            }
            catch
            {
                startTimeDenied++;
            }

            proc.Dispose();
        }

        Console.WriteLine($"processes-total={total}");
        Console.WriteLine($"process-name readable={nameOk} denied={nameDenied}");
        Console.WriteLine($"start-time readable={startTimeOk} denied={startTimeDenied}");
        foreach (var s in samples)
            Console.WriteLine($"  sample {s}");
        Console.WriteLine(
            "note: PID reuse — Windows recycles PIDs immediately after exit; only pid+startTime " +
            "jointly identifies a process instance. Contract ProcessIdentity requires both.");
        return 0;
    }
}
