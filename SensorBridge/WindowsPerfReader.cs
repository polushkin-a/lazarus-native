// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of the MPL was
// not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Diagnostics;

namespace SensorBridge;

/// <summary>
/// System state that Windows publishes as performance counters but LibreHardwareMonitor doesn't
/// surface: CPU frequency-limit/throttling, and page file usage.
///
/// The hardware throttling flags live in the CPU's MSRs, which user-mode code can't read without
/// a kernel driver — but the Windows power manager already reads them and republishes the result
/// through "Processor Information". That's the legal, driver-free way to see the same thing:
/// "Performance Limit Flags" is non-zero while a limit is active, and "% Performance Limit" drops
/// below 100 when the OS is capping the chip (thermal or PL1/PL2).
/// </summary>
public sealed class WindowsPerfReader : IDisposable
{
    // metric key -> (category, counter, instance)
    private static readonly (string Key, string Category, string Counter, string Instance)[] Counters =
    {
        ("hardware.cpu.clock.actual_frequency", "Processor Information", "Actual Frequency", "_Total"),
        ("hardware.cpu.load.of_maximum_frequency", "Processor Information", "% of Maximum Frequency", "_Total"),
        ("hardware.cpu.load.performance_limit", "Processor Information", "% Performance Limit", "_Total"),
        ("hardware.cpu.load.processor_performance", "Processor Information", "% Processor Performance", "_Total"),
        ("hardware.cpu.factor.performance_limit_flags", "Processor Information", "Performance Limit Flags", "_Total"),
        ("hardware.memory.load.page_file", "Paging File", "% Usage", "_Total"),
    };

    private readonly Dictionary<string, PerformanceCounter> _counters = new();

    public static WindowsPerfReader? TryCreate()
    {
        var reader = new WindowsPerfReader();
        foreach (var (key, category, counter, instance) in Counters)
        {
            try
            {
                var pc = new PerformanceCounter(category, counter, instance, readOnly: true);
                pc.NextValue(); // prime: rate/timer counters need a baseline before a real sample
                reader._counters[key] = pc;
            }
            catch
            {
                // A counter category can be missing or disabled on a given machine — skip just
                // that one rather than losing the whole reader.
            }
        }
        return reader._counters.Count > 0 ? reader : null;
    }

    public void Read(Dictionary<string, object?> metrics)
    {
        foreach (var (key, pc) in _counters)
        {
            try
            {
                metrics[key] = Math.Round(pc.NextValue(), 2);
            }
            catch
            {
                // a counter can vanish (e.g. on suspend) — skip it rather than kill the snapshot
            }
        }
    }

    public void Dispose()
    {
        foreach (var pc in _counters.Values) pc.Dispose();
        _counters.Clear();
    }
}
