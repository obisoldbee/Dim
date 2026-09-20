using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OBDim.Monitoring.Network.Windows;

/// <summary>
/// Process identity resolution with the frozen degradation chain (probe report §c):
/// <list type="number">
/// <item>Process.GetProcessById + StartTime — the main path; readable for user-session
/// processes (≈61% overall, all the usual attribution targets).</item>
/// <item>NtQuerySystemInformation(SystemProcessInformation) — the kernel enumeration
/// carries image name, CreateTime and parent pid for EVERY live process including
/// protected/other-session ones that refuse the Process API (≈39% of processes).
/// This replaces the WMI fallback without any third-party package.</item>
/// <item>null — the caller synthesizes an honest fallback identity (never a fake
/// startTime=0; see WindowsNetworkCollector).</item>
/// </list>
/// The NtQuery snapshot is refreshed once per collect cycle (<see cref="BeginCycle"/>),
/// not per pid.
/// </summary>
public sealed class ProcessIdentityResolver : IProcessIdentityProvider
{
    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const uint StatusSuccess = 0;

    // SYSTEM_PROCESS_INFORMATION x64 offsets (well-documented layout).
    private const int OffNextEntry = 0;    // ULONG
    private const int OffCreateTime = 32;  // LARGE_INTEGER (FILETIME)
    private const int OffImageNameLength = 56; // USHORT of UNICODE_STRING
    private const int OffImageNameBuffer = 64; // PWSTR of UNICODE_STRING
    private const int OffUniqueProcessId = 80; // HANDLE
    private const int OffInheritedFromPid = 88; // HANDLE

    private Dictionary<int, KernelProcessInfo>? _kernelSnapshot;

    private readonly record struct KernelProcessInfo(string? ImageName, long CreateTimeMs, int? ParentPid);

    public void BeginCycle() => _kernelSnapshot = null; // lazily re-read on first miss

    public ProcessIdentityInfo? Resolve(int pid)
    {
        if (pid < 0) return null;

        // Tier 1: Process API.
        try
        {
            using var proc = Process.GetProcessById(pid);
            var startMs = new DateTimeOffset(proc.StartTime.ToUniversalTime(), TimeSpan.Zero)
                .ToUnixTimeMilliseconds();
            string? path = null;
            try
            {
                path = proc.MainModule?.FileName;
            }
            catch
            {
                // MainModule is denied for many processes; path stays null (unknown ≠ fabricated).
            }
            return new ProcessIdentityInfo(pid, startMs, proc.ProcessName, ParentPid: null, path);
        }
        catch (ArgumentException)
        {
            return null; // process is gone (stale row, short-lived process — probe report §b)
        }
        catch
        {
            // StartTime denied (protected / other-session process): fall through to the kernel tier.
        }

        // Tier 2: NtQuerySystemInformation snapshot.
        _kernelSnapshot ??= ReadKernelSnapshot();
        if (_kernelSnapshot is not null && _kernelSnapshot.TryGetValue(pid, out var info) && info.CreateTimeMs > 0)
        {
            var name = info.ImageName is { Length: > 0 } full
                ? Path.GetFileName(full)
                : null;
            return new ProcessIdentityInfo(pid, info.CreateTimeMs, name ?? $"pid-{pid}", info.ParentPid, info.ImageName);
        }

        return null;
    }

    private static Dictionary<int, KernelProcessInfo>? ReadKernelSnapshot()
    {
        var size = 1 << 16;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var ret = Native.NtQuerySystemInformation(
                    SystemProcessInformation, buffer, size, out var needed);
                if (ret == StatusInfoLengthMismatch)
                {
                    size = needed > 0 ? needed + (1 << 16) : size * 2;
                    continue;
                }
                if (ret != StatusSuccess) return null;

                var map = new Dictionary<int, KernelProcessInfo>();
                var current = buffer;
                while (true)
                {
                    var next = Marshal.ReadInt32(current, OffNextEntry);
                    var createRaw = Marshal.ReadInt64(current, OffCreateTime);
                    var pid = (int)Marshal.ReadIntPtr(current, OffUniqueProcessId);
                    var parent = (int)Marshal.ReadIntPtr(current, OffInheritedFromPid);

                    string? image = null;
                    var nameLen = Marshal.ReadInt16(current, OffImageNameLength);
                    if (nameLen > 0)
                    {
                        var namePtr = Marshal.ReadIntPtr(current, OffImageNameBuffer);
                        if (namePtr != IntPtr.Zero)
                            image = Marshal.PtrToStringUni(namePtr, nameLen / 2);
                    }

                    long createMs = 0;
                    if (createRaw > 0)
                    {
                        try
                        {
                            createMs = new DateTimeOffset(DateTime.FromFileTimeUtc(createRaw), TimeSpan.Zero)
                                .ToUnixTimeMilliseconds();
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            createMs = 0; // unrepresentable tick value: treat as unknown
                        }
                    }

                    map[pid] = new KernelProcessInfo(image, createMs, parent > 0 ? parent : null);

                    if (next == 0) break;
                    current = IntPtr.Add(current, next);
                }
                return map;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    private static class Native
    {
        [DllImport("ntdll.dll")]
        internal static extern uint NtQuerySystemInformation(
            int systemInformationClass, IntPtr systemInformation, int systemInformationLength,
            out int returnLength);
    }
}
