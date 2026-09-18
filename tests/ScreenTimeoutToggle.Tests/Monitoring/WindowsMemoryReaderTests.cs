using OBDim.Monitoring.Infrastructure;
using OBDim.Monitoring.Models;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>A05 口径 sanity: real API numbers must be physically plausible and mutually consistent.</summary>
public class WindowsMemoryReaderTests
{
    [Fact]
    public void Read_ReturnsConsistentPlausibleNumbers()
    {
        using var reader = new WindowsMemoryReader();
        var sample = reader.Read(out var error);

        Assert.Null(error);
        Assert.NotNull(sample);
        var s = sample!;

        // Physical: total > 1 GiB on any real machine; available ≤ total.
        Assert.True(s.PhysicalTotalBytes > (1UL << 30), $"total={s.PhysicalTotalBytes}");
        Assert.True(s.PhysicalAvailableBytes <= s.PhysicalTotalBytes);
        Assert.True(s.PhysicalUsedBytes <= s.PhysicalTotalBytes);

        // Commit: limit > total, current ≤ limit (commit limit includes the page file).
        Assert.True(s.CommitLimitBytes > 0);
        Assert.True(s.CommitTotalBytes <= s.CommitLimitBytes, $"commit={s.CommitTotalBytes} limit={s.CommitLimitBytes}");

        // Low-memory signal is a tri-state that we successfully queried (not unknown).
        Assert.NotNull(s.LowMemorySignal);
    }

    [Fact]
    public void Read_Twice_ProducesAdvancingTimestamps()
    {
        using var reader = new WindowsMemoryReader();
        var a = reader.Read(out _);
        var b = reader.Read(out _);
        Assert.True(b!.SampledAtUtc >= a!.SampledAtUtc);
    }
}

public class MemoryHistoryBufferTests
{
    private static MemorySample Sample(int secondsAgo, ulong available = 4_000_000_000) =>
        new()
        {
            SampledAtUtc = DateTimeOffset.UtcNow.AddSeconds(-secondsAgo),
            PhysicalTotalBytes = 16_000_000_000,
            PhysicalAvailableBytes = available,
            CommitTotalBytes = 8_000_000_000,
            CommitLimitBytes = 32_000_000_000,
            LowMemorySignal = false,
        };

    /// <summary>The count bound holds however long the samples pretend to be.</summary>
    [Fact]
    public void MoreThanHourOfSamples_CountStaysAtOrBelowCapacity()
    {
        var buffer = new MemoryHistoryBuffer(capacity: 10, retention: TimeSpan.FromHours(1));
        for (var i = 0; i < 100; i++)
        {
            buffer.Add(Sample(secondsAgo: 0));
        }
        Assert.True(buffer.Count <= 10);
    }

    [Fact]
    public void SamplesOlderThanRetention_AreDropped()
    {
        var buffer = new MemoryHistoryBuffer(capacity: 100, retention: TimeSpan.FromHours(1));
        buffer.Add(Sample(secondsAgo: (int)TimeSpan.FromHours(2).TotalSeconds));
        buffer.Add(Sample(secondsAgo: 10));
        var snapshot = buffer.SnapshotWithVersion().Samples;

        Assert.Single(snapshot);
        Assert.Equal(10, (DateTimeOffset.UtcNow - snapshot[0].SampledAtUtc).TotalSeconds, 1);
    }

    /// <summary>A06: 睡眠/缺测 = absent samples; the buffer must NOT fabricate continuity.</summary>
    [Fact]
    public void GapInSampling_IsVisibleAsMissingSamples()
    {
        var buffer = new MemoryHistoryBuffer(capacity: 100, retention: TimeSpan.FromHours(1));
        buffer.Add(Sample(secondsAgo: 3000));
        buffer.Add(Sample(secondsAgo: 10));
        var snapshot = buffer.SnapshotWithVersion().Samples;

        Assert.Equal(2, snapshot.Count);
        var gap = snapshot[1].SampledAtUtc - snapshot[0].SampledAtUtc;
        Assert.True(gap > TimeSpan.FromMinutes(5), $"gap={gap}");
    }

    [Fact]
    public void Latest_ReturnsMostRecent()
    {
        var buffer = new MemoryHistoryBuffer();
        buffer.Add(Sample(secondsAgo: 100));
        var latest = Sample(secondsAgo: 5, available: 5_000_000_000);
        buffer.Add(latest);
        Assert.Equal(latest, buffer.Latest);
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var buffer = new MemoryHistoryBuffer();
        buffer.Add(Sample(secondsAgo: 10));
        buffer.Clear();
        Assert.Equal(0, buffer.Count);
    }
}
