// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace DesktopWidgets;

/// <summary>One sample of every system metric the widgets display.</summary>
public sealed record StatsSnapshot(
    double CpuPercent,
    double? GpuPercent,
    double MemoryPercent,
    double MemoryUsedGB,
    double MemoryTotalGB);

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
    private Dictionary<string, PerformanceCounter>? _gpuCounters;
    private int _ticksSinceGpuRefresh = int.MaxValue; // force a refresh on first sample
    private bool _gpuChecked;
    private bool _gpuAvailable;

    public StatsSnapshot Sample()
    {
        double cpu = SampleCpu();
        (double memPercent, double usedGB, double totalGB) = SampleMemory();
        double? gpu = SampleGpu();
        return new StatsSnapshot(cpu, gpu, memPercent, usedGB, totalGB);
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
            if (!_gpuChecked)
            {
                _gpuChecked = true;
                try { _gpuAvailable = PerformanceCounterCategory.Exists("GPU Engine"); }
                catch { _gpuAvailable = false; }
            }

            if (!_gpuAvailable)
                return null;

            try
            {
                if (_gpuCounters == null || ++_ticksSinceGpuRefresh >= 10)
                    RefreshGpuCounters();

                if (_gpuCounters == null || _gpuCounters.Count == 0)
                    return 0;

                // Instances are per-process, per-engine ("..._engtype_3D", "..._engtype_VideoDecode", ...).
                // Sum each engine type, then report the busiest one - the same idea Task Manager uses.
                var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var (instance, counter) in _gpuCounters)
                {
                    double value;
                    try { value = counter.NextValue(); }
                    catch { continue; } // process exited; instance is stale until the next refresh

                    int idx = instance.LastIndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                    string engine = idx >= 0 ? instance[(idx + 8)..] : "other";
                    perEngine[engine] = perEngine.TryGetValue(engine, out var sum) ? sum + value : value;
                }

                if (perEngine.Count == 0)
                    return 0;

                return Math.Clamp(perEngine.Values.Max(), 0, 100);
            }
            catch
            {
                // Counters can disappear after a driver reset; retry from scratch next time.
                DisposeGpuCounters();
                _ticksSinceGpuRefresh = int.MaxValue;
                return null;
            }
        }
    }

    private void RefreshGpuCounters()
    {
        DisposeGpuCounters();
        _ticksSinceGpuRefresh = 0;

        var category = new PerformanceCounterCategory("GPU Engine");
        var counters = new Dictionary<string, PerformanceCounter>(StringComparer.OrdinalIgnoreCase);
        foreach (string instance in category.GetInstanceNames())
        {
            if (!instance.Contains("engtype_", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                counter.NextValue(); // prime: the first raw sample is always 0
                counters[instance] = counter;
            }
            catch
            {
                // Instance vanished between enumeration and creation - ignore it.
            }
        }

        _gpuCounters = counters;
    }

    private void DisposeGpuCounters()
    {
        if (_gpuCounters == null)
            return;
        foreach (var counter in _gpuCounters.Values)
        {
            try { counter.Dispose(); } catch { /* ignore */ }
        }
        _gpuCounters = null;
    }

    public void Dispose()
    {
        lock (_gpuLock)
        {
            DisposeGpuCounters();
        }
    }
}
