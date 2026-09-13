using System.Runtime.InteropServices;
using OBDim.Monitoring.Models;

namespace OBDim.Monitoring.Infrastructure;

/// <summary>Source of real Windows memory numbers. One call = one sample.</summary>
public interface IMemoryReader : IDisposable
{
    /// <summary>Reads one sample. Returns null (with <paramref name="error"/>) when the APIs fail — no fabricated zeros.</summary>
    MemorySample? Read(out string? error);
}

/// <summary>
/// Windows memory sampler:
/// <list type="bullet">
/// <item>GlobalMemoryStatusEx → physical total/available ("used" is derived: total − available, per spec §6).</item>
/// <item>GetPerformanceInfo → commit total/limit in PAGES; multiplied by page size (spec §2.3).</item>
/// <item>CreateMemoryResourceNotification(LowMemoryResourceNotification), polled non-blocking → 触发/未触发/未知.</item>
/// </list>
/// Struct sizes follow the x64 PSAPI layouts; page counts are UIntPtr and multiplied in 64-bit.
/// </summary>
public sealed class WindowsMemoryReader : IMemoryReader, IDisposable
{
    private IntPtr _lowMemoryHandle = IntPtr.Zero;
    private bool _lowMemoryHandleFailed;

    public MemorySample? Read(out string? error)
    {
        error = null;

        // dwLength must be set BEFORE the call — with `out` marshaling it would arrive as
        // zero and GlobalMemoryStatusEx fails with ERROR_INVALID_PARAMETER (win32:87).
        var memEx = new Native.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>(),
        };
        if (!Native.GlobalMemoryStatusEx(ref memEx))
        {
            error = $"GlobalMemoryStatusEx failed (win32:{Marshal.GetLastWin32Error()})";
            return null;
        }

        // GetPerformanceInfo's struct changed with Windows 11 (PageSize became SIZE_T and
        // a Reserved DWORD appeared; verified empirically on 10.0.26200 where the OS
        // accepts cb=104 and rejects 96 with ERROR_BAD_LENGTH). Try the new layout first,
        // fall back to the legacy one so Windows 10 keeps working.
        ulong commitTotalPages, commitLimitPages, pageSize;
        var v2 = new Native.PERFORMANCE_INFORMATION_V2
        {
            cb = (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION_V2>(),
        };
        if (Native.GetPerformanceInfo(ref v2, v2.cb))
        {
            commitTotalPages = (ulong)v2.CommitTotal;
            commitLimitPages = (ulong)v2.CommitLimit;
            pageSize = (ulong)v2.PageSize;
        }
        else
        {
            var v1 = new Native.PERFORMANCE_INFORMATION_V1
            {
                cb = (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION_V1>(),
            };
            if (!Native.GetPerformanceInfo(ref v1, v1.cb))
            {
                error = $"GetPerformanceInfo failed (win32:{Marshal.GetLastWin32Error()})";
                return null;
            }
            commitTotalPages = (ulong)v1.CommitTotal;
            commitLimitPages = (ulong)v1.CommitLimit;
            pageSize = (ulong)v1.PageSize;
        }

        pageSize = pageSize == 0 ? 4096 : pageSize;
        // Commit counters are page counts (SIZE_T); widen to ulong before multiplying.
        var commitTotal = commitTotalPages * pageSize;
        var commitLimit = commitLimitPages * pageSize;

        var lowMemory = QueryLowMemorySignal();

        return new MemorySample
        {
            SampledAtUtc = DateTimeOffset.UtcNow,
            PhysicalTotalBytes = memEx.ullTotalPhys,
            PhysicalAvailableBytes = memEx.ullAvailPhys,
            CommitTotalBytes = commitTotal,
            CommitLimitBytes = commitLimit,
            LowMemorySignal = lowMemory,
        };
    }

    /// <summary>
    /// Non-blocking poll of the low-memory notification: signaled = 低内存触发, timeout =
    /// 未触发, handle failure = 未知. A dedicated kernel handle is created once and reused.
    /// </summary>
    private bool? QueryLowMemorySignal()
    {
        if (_lowMemoryHandle == IntPtr.Zero)
        {
            if (_lowMemoryHandleFailed) return null;
            _lowMemoryHandle = Native.CreateMemoryResourceNotification(Native.MemoryResourceNotificationLow);
            if (_lowMemoryHandle == IntPtr.Zero)
            {
                _lowMemoryHandleFailed = true;
                return null;
            }
        }

        var wait = Native.WaitForSingleObject(_lowMemoryHandle, 0);
        return wait switch
        {
            Native.WAIT_OBJECT_0 => true,
            Native.WAIT_TIMEOUT => false,
            _ => null,
        };
    }

    public void Dispose()
    {
        if (_lowMemoryHandle != IntPtr.Zero)
        {
            Native.CloseHandle(_lowMemoryHandle);
            _lowMemoryHandle = IntPtr.Zero;
        }
    }

    internal static class Native
    {
        internal const uint WAIT_OBJECT_0 = 0x00000000;
        internal const uint WAIT_TIMEOUT = 0x00000102;
        internal const int MemoryResourceNotificationLow = 0;

        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        /// <summary>
        /// Windows 11 layout (verified empirically on 10.0.26200: OS accepts cb=104).
        /// PageSize became SIZE_T and a trailing Reserved DWORD was added.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct PERFORMANCE_INFORMATION_V2
        {
            public uint cb;
            public UIntPtr CommitTotal;
            public UIntPtr CommitLimit;
            public UIntPtr CommitPeak;
            public UIntPtr PhysicalTotal;
            public UIntPtr PhysicalAvailable;
            public UIntPtr SystemCache;
            public UIntPtr KernelTotal;
            public UIntPtr KernelPaged;
            public UIntPtr KernelNonPaged;
            public UIntPtr PageSize;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
            public uint Reserved;
        }

        /// <summary>Legacy (Windows 10 and earlier) layout: PageSize as the last DWORD, size 96 on x64.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct PERFORMANCE_INFORMATION_V1
        {
            public uint cb;
            public UIntPtr CommitTotal;
            public UIntPtr CommitLimit;
            public UIntPtr CommitPeak;
            public UIntPtr PhysicalTotal;
            public UIntPtr PhysicalAvailable;
            public UIntPtr SystemCache;
            public UIntPtr KernelTotal;
            public UIntPtr KernelPaged;
            public UIntPtr KernelNonPaged;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
            public uint PageSize;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("psapi.dll", SetLastError = true)]
        internal static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION_V2 lpPerformanceInformation, uint cb);

        [DllImport("psapi.dll", SetLastError = true)]
        internal static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION_V1 lpPerformanceInformation, uint cb);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateMemoryResourceNotification(int notificationType);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);
    }
}

/// <summary>
/// Bounded one-hour memory history (spec §7): at most <see cref="Capacity"/> samples and
/// never older than the retention window. Gaps (sleep, disabled sampling, read failures)
/// are simply absent — the trend chart renders them as breaks, never interpolated.
/// All methods are thread-safe; the coordinator samples on one timer thread while the UI reads.
/// </summary>
public sealed class MemoryHistoryBuffer
{
    private readonly object _lock = new();
    private readonly List<MemorySample> _samples = [];
    private readonly int _capacity;
    private readonly TimeSpan _retention;

    public MemoryHistoryBuffer(int capacity = 720, TimeSpan? retention = null)
    {
        _capacity = capacity;
        _retention = retention ?? TimeSpan.FromHours(1);
    }

    public void Add(MemorySample sample)
    {
        lock (_lock)
        {
            _samples.Add(sample);
            Trim(DateTimeOffset.UtcNow);
        }
    }

    public MemorySample[] Snapshot()
    {
        lock (_lock)
        {
            Trim(DateTimeOffset.UtcNow);
            return [.. _samples];
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                Trim(DateTimeOffset.UtcNow);
                return _samples.Count;
            }
        }
    }

    public MemorySample? Latest
    {
        get
        {
            lock (_lock)
            {
                return _samples.Count == 0 ? null : _samples[^1];
            }
        }
    }

    public void Clear()
    {
        lock (_lock) _samples.Clear();
    }

    /// <summary>Removes samples beyond capacity OR older than retention — the two bounds from spec A06.</summary>
    private void Trim(DateTimeOffset now)
    {
        var cutoff = now - _retention;
        var start = 0;
        while (start < _samples.Count && _samples[start].SampledAtUtc < cutoff) start++;
        if (start > 0) _samples.RemoveRange(0, start);
        if (_samples.Count > _capacity) _samples.RemoveRange(0, _samples.Count - _capacity);
    }
}
