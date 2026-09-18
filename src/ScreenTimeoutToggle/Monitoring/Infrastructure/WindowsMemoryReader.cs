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

        // GetPerformanceInfo binds cb to the size the OS was built with: the published
        // layout below measures 104 bytes on x64 and a 96-byte request is rejected with
        // ERROR_BAD_LENGTH (probe on 10.0.26200, see WindowsMemoryAbiTests).
        var perf = new Native.PERFORMANCE_INFORMATION
        {
            cb = (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>(),
        };
        if (!Native.GetPerformanceInfo(ref perf, perf.cb))
        {
            error = $"GetPerformanceInfo failed (win32:{Marshal.GetLastWin32Error()})";
            return null;
        }

        ulong commitTotalPages = (ulong)perf.CommitTotal;
        ulong commitLimitPages = (ulong)perf.CommitLimit;
        ulong pageSize = (ulong)perf.PageSize;

        // Page counts are meaningless without a real page size — a zero here would turn
        // every commit figure into a fabricated 0, so the sample is reported as failed.
        if (pageSize == 0)
        {
            error = "GetPerformanceInfo returned PageSize=0";
            return null;
        }

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
        /// The layout Microsoft publishes for PERFORMANCE_INFORMATION: ten SIZE_T counters
        /// after <c>cb</c>, then three DWORDs. PageSize is a SIZE_T that sits between
        /// KernelNonPaged and HandleCount — NOT a trailing DWORD, which is what the former
        /// "V1" declaration claimed on the strength of nothing but a 96-byte size.
        /// 104 bytes on x64 (4+4 pad, then 9×8 through KernelNonPaged@72, PageSize@80,
        /// HandleCount@88, ProcessCount@92, ThreadCount@96, padded to 104).
        /// All fields the reader consumes are SIZE_T, so the x64 alignment story is the only
        /// one this program can be built against; the app publishes win-x64.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct PERFORMANCE_INFORMATION
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
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("psapi.dll", SetLastError = true)]
        internal static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION lpPerformanceInformation, uint cb);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateMemoryResourceNotification(int notificationType);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);
    }
}

/// <summary>
/// The history plus the number it is cached under. A renderer that keys its geometry on
/// count-and-last-timestamp alone will happily keep a stale curve when a sample is corrected
/// in place, so every content change — including a same-timestamp replacement — advances this.
/// </summary>
public sealed record MemoryHistorySnapshot(long Version, IReadOnlyList<MemorySample> Samples);

/// <summary>
/// Bounded memory history: two independent limits, count and age, because either alone is
/// sufficient to lose the other (a stall leaves 1600 ancient samples; a flood of 1-second
/// reads would fill 1600 well inside the retention window). Gaps — sleep, disabled sampling,
/// read failures — are simply absent; the trend chart renders them as breaks and never
/// interpolates. All methods are thread-safe: the coordinator samples on one timer thread
/// while the UI reads on another.
/// </summary>
public sealed class MemoryHistoryBuffer
{
    /// <summary>
    /// Five selectable windows up to two hours, sampled every five seconds: 1441 points fill
    /// the longest one with both bounds inclusive. The headroom above that is deliberate —
    /// with only 1441 slots the count bound would start evicting the oldest sample of the
    /// 2-hour window the moment a single read arrives early.
    /// </summary>
    public const int DefaultCapacity = 1600;

    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(2);

    private readonly object _lock = new();
    private readonly List<MemorySample> _samples = [];
    private readonly int _capacity;
    private readonly TimeSpan _retention;
    private long _version;

    public MemoryHistoryBuffer(int capacity = DefaultCapacity, TimeSpan? retention = null)
    {
        _capacity = capacity;
        _retention = retention ?? DefaultRetention;
    }

    /// <summary>
    /// Records one sample. A sample carrying a timestamp already in the history replaces it:
    /// a re-read of an instant is a correction, not a second observation, and appending it
    /// would both duplicate the point and consume a slot.
    /// </summary>
    public void Add(MemorySample sample)
    {
        lock (_lock)
        {
            var existing = _samples.FindIndex(s => s.SampledAtUtc == sample.SampledAtUtc);
            if (existing >= 0)
            {
                if (_samples[existing].Equals(sample)) return;
                _samples[existing] = sample;
            }
            else
            {
                _samples.Add(sample);
            }

            _version++;
            Trim(DateTimeOffset.UtcNow);
        }
    }

    public MemoryHistorySnapshot SnapshotWithVersion()
    {
        lock (_lock)
        {
            Trim(DateTimeOffset.UtcNow);
            return new MemoryHistorySnapshot(_version, [.. _samples]);
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

    public long Version
    {
        get { lock (_lock) return _version; }
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
        lock (_lock)
        {
            if (_samples.Count == 0) return;
            _samples.Clear();
            _version++;
        }
    }

    /// <summary>Removes samples beyond capacity OR older than retention — the two bounds of A06.</summary>
    private void Trim(DateTimeOffset now)
    {
        var cutoff = now - _retention;
        var start = 0;
        while (start < _samples.Count && _samples[start].SampledAtUtc < cutoff) start++;
        if (start > 0)
        {
            _samples.RemoveRange(0, start);
            _version++;
        }
        if (_samples.Count > _capacity)
        {
            _samples.RemoveRange(0, _samples.Count - _capacity);
            _version++;
        }
    }
}
