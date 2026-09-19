using System.Diagnostics;
using System.Net.NetworkInformation;

namespace OBDim.NetworkProbe;

/// <summary>
/// Probe (e): resource cost of a 1 Hz full-table polling cadence.
/// Measures 100 iterations of (a) the four owner-PID connection tables and
/// (b) NetworkInterface enumeration + IPv4 statistics, reporting p50/p95/max.
/// </summary>
internal static class PerfProbe
{
    private const int Iterations = 100;

    public static int Run()
    {
        ProbeContext.Header("perf");

        var tableSamples = new double[Iterations];
        var rowCounts = new int[4];
        for (var i = 0; i < Iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            var r1 = ConnectionTableProbe.RunTableQuiet(2, true);
            var r2 = ConnectionTableProbe.RunTableQuiet(23, true);
            var r3 = ConnectionTableProbe.RunTableQuiet(2, false);
            var r4 = ConnectionTableProbe.RunTableQuiet(23, false);
            sw.Stop();
            tableSamples[i] = sw.Elapsed.TotalMilliseconds;
            if (i == Iterations - 1)
                rowCounts = [r1, r2, r3, r4];
        }

        Report("connection-tables(tcp4+tcp6+udp4+udp6)", tableSamples);
        Console.WriteLine(
            $"  last-iteration rows tcp4={rowCounts[0]} tcp6={rowCounts[1]} udp4={rowCounts[2]} udp6={rowCounts[3]}");

        var ifaceSamples = new double[Iterations];
        for (var i = 0; i < Iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    _ = nic.GetIPv4Statistics();
                }
                catch
                {
                    // counter read failures are recorded by the interfaces probe; cost is what matters here
                }
            }
            sw.Stop();
            ifaceSamples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Report("interface-enumeration+ipv4-stats", ifaceSamples);
        Console.WriteLine(
            "note: numbers are the marginal cost of one 1 Hz snapshot's table+counter reads, " +
            "excluding aggregation, history bookkeeping, and UI.");
        return 0;
    }

    private static void Report(string label, double[] samples)
    {
        Array.Sort(samples);
        var mean = samples.Average();
        Console.WriteLine(
            $"{label}: n={samples.Length} min={samples[0]:F3}ms p50={Percentile(samples, 0.50):F3}ms " +
            $"p95={Percentile(samples, 0.95):F3}ms max={samples[^1]:F3}ms mean={mean:F3}ms");
    }

    private static double Percentile(double[] sorted, double p)
    {
        var rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
