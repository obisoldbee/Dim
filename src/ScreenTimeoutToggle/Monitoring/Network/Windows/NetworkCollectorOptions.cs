namespace OBDim.Monitoring.Network.Windows;

/// <summary>
/// Resource bounds of contract §9. The frozen production values live in
/// <see cref="Default"/>; tests construct small bounds via <c>with</c> to exercise the
/// truncation paths without allocating thousands of rows.
/// </summary>
public sealed record NetworkCollectorOptions
{
    /// <summary>interfaces ≤ 128.</summary>
    public int MaxInterfaces { get; init; } = 128;

    /// <summary>apps ≤ 2000.</summary>
    public int MaxApps { get; init; } = 2000;

    /// <summary>connections per app ≤ 2000 (directional observations).</summary>
    public int MaxConnectionsPerApp { get; init; } = 2000;

    /// <summary>processes per app ≤ 256.</summary>
    public int MaxProcessesPerApp { get; init; } = 256;

    /// <summary>history points per series ≤ 7200.</summary>
    public int HistoryMaxPoints { get; init; } = 7200;

    /// <summary>history time window ≤ 7200000 ms (2 hours).</summary>
    public long HistoryWindowMs { get; init; } = 7_200_000;

    public static readonly NetworkCollectorOptions Default = new();
}
