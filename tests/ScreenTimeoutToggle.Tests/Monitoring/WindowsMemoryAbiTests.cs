using System.Runtime.InteropServices;
using OBDim.Monitoring.Infrastructure;
using Xunit;

namespace OBDim.Tests.Monitoring;

/// <summary>
/// R32 / AM01: the commit figures are <c>pages × PageSize</c>, so a single wrong field width
/// or offset turns every number on the memory page into a plausible-looking lie that no
/// screenshot would reveal. These tests pin the declaration to the layout Microsoft
/// publishes — ten SIZE_T counters after <c>cb</c>, then three DWORDs — and cross-check the
/// page→byte multiplication against GlobalMemoryStatusEx, which reports the same two
/// quantities already in bytes. The OS itself is asked which structure size it accepts.
/// </summary>
public class WindowsMemoryAbiTests
{
    private const uint ErrorBadLength = 24;

    private static int OffsetOf(string field) =>
        (int)Marshal.OffsetOf(typeof(WindowsMemoryReader.Native.PERFORMANCE_INFORMATION), field);

    /// <summary>
    /// PageSize must be pointer-sized and sit between KernelNonPaged and HandleCount. A DWORD
    /// PageSize fails the width check; a PageSize moved to the end of the struct fails the
    /// ordering check. Both were live declarations in this file's history.
    /// </summary>
    [Fact]
    public void PerformanceInformation_Layout_MatchesPublishedMicrosoftDefinition()
    {
        Assert.Equal(0, OffsetOf("cb"));
        Assert.Equal(8, OffsetOf("CommitTotal"));
        Assert.Equal(16, OffsetOf("CommitLimit"));
        Assert.Equal(24, OffsetOf("CommitPeak"));
        Assert.Equal(32, OffsetOf("PhysicalTotal"));
        Assert.Equal(40, OffsetOf("PhysicalAvailable"));
        Assert.Equal(48, OffsetOf("SystemCache"));
        Assert.Equal(56, OffsetOf("KernelTotal"));
        Assert.Equal(64, OffsetOf("KernelPaged"));
        Assert.Equal(72, OffsetOf("KernelNonPaged"));
        Assert.Equal(80, OffsetOf("PageSize"));
        Assert.Equal(88, OffsetOf("HandleCount"));
        Assert.Equal(92, OffsetOf("ProcessCount"));
        Assert.Equal(96, OffsetOf("ThreadCount"));
        Assert.Equal(104, Marshal.SizeOf<WindowsMemoryReader.Native.PERFORMANCE_INFORMATION>());

        // Same facts restated without hard-coded offsets, so the guard is not x64-only.
        Assert.Equal(IntPtr.Size, OffsetOf("PageSize") - OffsetOf("KernelNonPaged"));
        Assert.Equal(IntPtr.Size, OffsetOf("HandleCount") - OffsetOf("PageSize"));
        Assert.Equal(4, OffsetOf("ProcessCount") - OffsetOf("HandleCount"));
    }

    /// <summary>
    /// The OS decides which size is valid: the published layout is accepted, while a 96-byte
    /// request — the size of the old "legacy Windows 10" declaration — comes back
    /// ERROR_BAD_LENGTH. That is the evidence that the 96-byte variant was dead code rather
    /// than a fallback, and why nothing here claims Windows 10 compatibility.
    /// </summary>
    [Fact]
    public void GetPerformanceInfo_AcceptsPublishedSize_RejectsNinetySix()
    {
        var info = new WindowsMemoryReader.Native.PERFORMANCE_INFORMATION
        {
            cb = (uint)Marshal.SizeOf<WindowsMemoryReader.Native.PERFORMANCE_INFORMATION>(),
        };
        Assert.True(WindowsMemoryReader.Native.GetPerformanceInfo(
            ref info, (uint)Marshal.SizeOf<WindowsMemoryReader.Native.PERFORMANCE_INFORMATION>()));

        var wrong = new WindowsMemoryReader.Native.PERFORMANCE_INFORMATION { cb = 96 };
        Assert.False(WindowsMemoryReader.Native.GetPerformanceInfo(ref wrong, 96));
        Assert.Equal(ErrorBadLength, (uint)Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// 页转字节 end-to-end. If PageSize were read from the wrong offset the multiplication
    /// would miss by orders of magnitude, so agreeing within 1% is a real check. Measured
    /// agreement on Windows 10.0.26200 x64 was exact (ratio 1.000000 for both quantities).
    /// </summary>
    [Fact]
    public void PagesTimesPageSize_AgreeWithGlobalMemoryStatusEx()
    {
        var perf = new WindowsMemoryReader.Native.PERFORMANCE_INFORMATION
        {
            cb = (uint)Marshal.SizeOf<WindowsMemoryReader.Native.PERFORMANCE_INFORMATION>(),
        };
        Assert.True(WindowsMemoryReader.Native.GetPerformanceInfo(
            ref perf, (uint)Marshal.SizeOf<WindowsMemoryReader.Native.PERFORMANCE_INFORMATION>()));

        var memEx = new WindowsMemoryReader.Native.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<WindowsMemoryReader.Native.MEMORYSTATUSEX>(),
        };
        Assert.True(WindowsMemoryReader.Native.GlobalMemoryStatusEx(ref memEx));

        ulong pageSize = (ulong)perf.PageSize;
        Assert.True(pageSize > 0, "PageSize must be real; the reader refuses to assume 4096");
        Assert.True((pageSize & (pageSize - 1)) == 0, $"PageSize={pageSize} is not a power of two");

        RatioWithinOnePercent((ulong)perf.CommitLimit * pageSize, memEx.ullTotalPageFile);
        RatioWithinOnePercent((ulong)perf.PhysicalTotal * pageSize, memEx.ullTotalPhys);
    }

    /// <summary>
    /// The sampler feeds the UI, so it has to satisfy the same cross-check — a change that
    /// keeps the struct correct but mis-wires the multiplication still fails here.
    /// </summary>
    [Fact]
    public void Reader_DerivedValues_AgreeWithGlobalMemoryStatusEx()
    {
        using var reader = new WindowsMemoryReader();
        var sample = reader.Read(out var error);

        Assert.Null(error);
        Assert.NotNull(sample);

        var memEx = new WindowsMemoryReader.Native.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<WindowsMemoryReader.Native.MEMORYSTATUSEX>(),
        };
        Assert.True(WindowsMemoryReader.Native.GlobalMemoryStatusEx(ref memEx));

        RatioWithinOnePercent(sample!.PhysicalAvailableBytes, memEx.ullAvailPhys);
        Assert.Equal(memEx.ullTotalPhys, sample.PhysicalTotalBytes);

        // Commit limit spans physical RAM plus the page file, and commit can never exceed it.
        Assert.True(sample.CommitLimitBytes > sample.PhysicalTotalBytes);
        Assert.True(sample.CommitTotalBytes <= sample.CommitLimitBytes);
        Assert.True(sample.PhysicalAvailableBytes <= sample.PhysicalTotalBytes);
    }

    private static void RatioWithinOnePercent(ulong actual, ulong expected)
    {
        Assert.True(expected > 0, "expected a non-zero baseline from GlobalMemoryStatusEx");
        var ratio = (double)actual / expected;
        Assert.True(Math.Abs(ratio - 1.0) <= 0.01, $"actual={actual} expected={expected} ratio={ratio:F6}");
    }
}
