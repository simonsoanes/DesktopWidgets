// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Runtime.InteropServices;

namespace DesktopWidgets;

/// <summary>One sample of every system metric the widgets display.</summary>
public sealed record StatsSnapshot(
    double CpuPercent,
    double? GpuPercent,
    double MemoryPercent,
    double MemoryUsedGB,
    double MemoryTotalGB,
    IReadOnlyDictionary<string, DiskSample> Disks);

/// <summary>One drive's capacity sample; Ready=false when the volume is absent (card reader, unplugged USB).</summary>
public sealed record DiskSample(double UsedPercent, double FreeGB, double TotalGB, bool Ready);

/// <summary>Which metrics the live widgets actually need; everything else is not sampled at all.</summary>
public readonly record struct StatsNeeds(bool Cpu, bool Gpu, bool Memory, bool Disk);

/// <summary>
/// Samples CPU (GetSystemTimes), physical memory (GlobalMemoryStatusEx) and GPU
/// (the "GPU Engine" performance counters that ship with WDDM 2.x drivers).
/// Sampling is cheap enough to run once a second on a background thread.
/// </summary>
public sealed class SystemStats : IDisposable
{
    private ulong _prevIdle;
    private ulong _prevTotal;
    private bool _hasPrev;

    private readonly object _gpuLock = new();
    private IntPtr _pdhQuery;
    private IntPtr _pdhCounter;
    private int _gpuRetryTicks;

    private Dictionary<string, DiskSample> _disks = new(StringComparer.OrdinalIgnoreCase);
    private int _ticksSinceDiskRefresh = 5; // force a refresh on first sample (int.MaxValue would overflow)
    private static readonly Dictionary<string, DiskSample> EmptyDisks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Samples everything (used by the offscreen preview).</summary>
    public StatsSnapshot Sample() => Sample(new StatsNeeds(true, true, true, true));

    /// <summary>Samples only the metrics some live widget actually displays.</summary>
    public StatsSnapshot Sample(StatsNeeds needs)
    {
        double cpu = 0;
        if (needs.Cpu)
            cpu = SampleCpu();
        else
            _hasPrev = false; // re-prime the delta when a CPU widget appears

        (double memPercent, double usedGB, double totalGB) = needs.Memory ? SampleMemory() : (0, 0, 0);

        double? gpu = null;
        if (needs.Gpu)
            gpu = SampleGpu();
        else
            lock (_gpuLock) { DisposeGpu(); } // release the PDH query when no GPU widget remains

        Dictionary<string, DiskSample> disks = EmptyDisks;
        if (needs.Disk)
        {
            if (++_ticksSinceDiskRefresh >= 5)
            {
                _ticksSinceDiskRefresh = 0;
                _disks = SampleDisks();
            }
            disks = _disks;
        }
        return new StatsSnapshot(cpu, gpu, memPercent, usedGB, totalGB, disks);
    }

    private double SampleCpu()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        // Kernel time includes idle time.
        ulong total = kernel.ToUInt64() + user.ToUInt64();
        ulong idleTicks = idle.ToUInt64();

        double result = 0;
        if (_hasPrev)
        {
            ulong totalDelta = total - _prevTotal;
            ulong idleDelta = idleTicks - _prevIdle;
            if (totalDelta > 0)
                result = (1.0 - (double)idleDelta / totalDelta) * 100.0;
        }

        _prevTotal = total;
        _prevIdle = idleTicks;
        _hasPrev = true;
        return Math.Clamp(result, 0, 100);
    }

    private static (double percent, double usedGB, double totalGB) SampleMemory()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref m))
            return (0, 0, 0);

        const double gb = 1024.0 * 1024.0 * 1024.0;
        double total = m.ullTotalPhys / gb;
        double used = (m.ullTotalPhys - m.ullAvailPhys) / gb;
        return (m.dwMemoryLoad, used, total);
    }

    private double? SampleGpu()
    {
        lock (_gpuLock)
        {
            if (_pdhQuery == IntPtr.Zero)
            {
                if (_gpuRetryTicks > 0)
                {
                    _gpuRetryTicks--;
                    return null;
                }
                if (!OpenGpuQuery())
                {
                    _gpuRetryTicks = 30; // category absent (no WDDM 2.x driver): retry occasionally
                    return null;
                }
            }

            // One collect refreshes every instance of the wildcard query. The old
            // per-counter NextValue() loop is what burned a whole core on busy machines.
            if (Native.PdhCollectQueryData(_pdhQuery) != 0)
            {
                DisposeGpu();
                _gpuRetryTicks = 10;
                return null;
            }

            uint bufferSize = 0, itemCount = 0;
            uint status = Native.PdhGetFormattedCounterArrayW(_pdhCounter, Native.PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, IntPtr.Zero);
            if (status != Native.PDH_MORE_DATA || bufferSize == 0)
                return 0;

            IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                status = Native.PdhGetFormattedCounterArrayW(_pdhCounter, Native.PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, buffer);
                if (status != 0)
                    return 0;

                // Instances are per-process, per-engine ("..._engtype_3D", "..._engtype_VideoDecode", ...).
                // Sum each engine type, then report the busiest one - the same idea Task Manager uses.
                var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                int stride = Marshal.SizeOf<Native.PDH_FMT_COUNTERVALUE_ITEM_W>();
                for (int i = 0; i < itemCount; i++)
                {
                    var item = Marshal.PtrToStructure<Native.PDH_FMT_COUNTERVALUE_ITEM_W>(buffer + i * stride);
                    if (item.FmtValue.CStatus != 0)
                        continue; // stale instance (process exited)
                    string name = Marshal.PtrToStringUni(item.szName) ?? "";
                    int idx = name.LastIndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                    string engine = idx >= 0 ? name[(idx + 8)..] : "other";
                    perEngine[engine] = perEngine.TryGetValue(engine, out var sum) ? sum + item.FmtValue.dblValue : item.FmtValue.dblValue;
                }

                if (perEngine.Count == 0)
                    return 0;
                return Math.Clamp(perEngine.Values.Max(), 0, 100);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private bool OpenGpuQuery()
    {
        if (Native.PdhOpenQuery(null, IntPtr.Zero, out _pdhQuery) != 0)
        {
            _pdhQuery = IntPtr.Zero;
            return false;
        }
        if (Native.PdhAddEnglishCounterW(_pdhQuery, "\\GPU Engine(*)\\Utilization Percentage", IntPtr.Zero, out _pdhCounter) != 0)
        {
            Native.PdhCloseQuery(_pdhQuery);
            _pdhQuery = IntPtr.Zero;
            _pdhCounter = IntPtr.Zero;
            return false;
        }
        Native.PdhCollectQueryData(_pdhQuery); // prime the wildcard instance list
        return true;
    }

    private static Dictionary<string, DiskSample> SampleDisks()
    {
        const double gb = 1024.0 * 1024.0 * 1024.0;
        var disks = new Dictionary<string, DiskSample>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;
                try
                {
                    if (!drive.IsReady)
                    {
                        disks[drive.Name] = new DiskSample(0, 0, 0, false);
                        continue;
                    }
                    double total = drive.TotalSize / gb;
                    double free = drive.TotalFreeSpace / gb;
                    double used = total > 0 ? (total - free) / total * 100.0 : 0;
                    disks[drive.Name] = new DiskSample(Math.Clamp(used, 0, 100), free, total, true);
                }
                catch
                {
                    // Volume vanished mid-sample - report it as unavailable.
                    disks[drive.Name] = new DiskSample(0, 0, 0, false);
                }
            }
        }
        catch
        {
            // Drive enumeration can fail during device churn; keep the previous sample.
        }
        return disks;
    }

    private void DisposeGpu()
    {
        if (_pdhQuery != IntPtr.Zero)
        {
            Native.PdhCloseQuery(_pdhQuery);
            _pdhQuery = IntPtr.Zero;
            _pdhCounter = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        lock (_gpuLock)
        {
            DisposeGpu();
        }
    }
}
