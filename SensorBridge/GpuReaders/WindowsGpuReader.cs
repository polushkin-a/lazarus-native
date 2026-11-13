using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SensorBridge.GpuReaders;

/// <summary>
/// Reads GPU sensors straight from the Windows WDDM graphics kernel — the same source Task Manager
/// and System Informer use. Works on any vendor (AMD/NVIDIA/Intel) with no SDK, and sidesteps the
/// ADL/ADLX driver breakage on current-gen cards. Load/VRAM come from WDDM perf counters;
/// temperature/clock come from the D3DKMTQueryAdapterInfo kernel thunk (KMTQAITYPE_*PERFDATA),
/// which every WDDM 2.4+ driver must report to the OS.
/// </summary>
public sealed class WindowsGpuReader : IGpuReader
{
    private readonly string _luid;       // the discrete GPU's counter instance suffix, e.g. "luid_0x00000000_0x00014c8e_phys_0"
    private readonly LUID _adapterLuid;  // same LUID parsed for the D3DKMT calls
    private readonly float _totalVramMb;

    // Utilization Percentage is a timer counter: the first NextValue() on a fresh counter is
    // always 0 (no baseline), so we keep the per-process counters open across ticks. Instances
    // come and go as processes touch the GPU, so this map is pruned each read.
    private readonly Dictionary<string, PerformanceCounter> _engineCounters = new();

    public string? DeviceName { get; }

    private WindowsGpuReader(string luid, LUID adapterLuid, float totalVramMb, string? deviceName)
    {
        _luid = luid;
        _adapterLuid = adapterLuid;
        _totalVramMb = totalVramMb;
        DeviceName = deviceName;
    }

    public static WindowsGpuReader? TryCreate()
    {
        try
        {
            // Pick the physical GPU currently holding the most dedicated VRAM — that's the
            // discrete card, not the iGPU. Instance names look like "luid_..._phys_0".
            var mem = new PerformanceCounterCategory("GPU Adapter Memory");
            string? bestLuid = null;
            float bestUsed = -1;
            foreach (string instance in mem.GetInstanceNames())
            {
                float used = ReadCounter("GPU Adapter Memory", "Dedicated Usage", instance);
                if (used > bestUsed)
                {
                    bestUsed = used;
                    bestLuid = instance;
                }
            }
            if (bestLuid is null || !TryParseLuid(bestLuid, out LUID luid)) return null;

            var adapter = ReadBiggestAdapter();
            return new WindowsGpuReader(bestLuid, luid, adapter.vramMb, adapter.name);
        }
        catch
        {
            return null; // perf counters unavailable / disabled
        }
    }

    public GpuSnapshot? Read()
    {
        try
        {
            float vramUsedMb = ReadCounter("GPU Adapter Memory", "Dedicated Usage", _luid) / (1024f * 1024f);

            // Every engine instance for our GPU, across all processes. Instance names look like
            // "pid_<n>_luid_..._phys_0_eng_<n>_engtype_3D".
            var active = new PerformanceCounterCategory("GPU Engine").GetInstanceNames()
                .Where(i => i.Contains(_luid, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (string stale in _engineCounters.Keys.Where(k => !active.Contains(k)).ToList())
            {
                _engineCounters[stale].Dispose();
                _engineCounters.Remove(stale);
            }

            // Summing one engine's per-process utilization reproduces what vendor tools call
            // "D3D Usage" (verified: 61.6 vs HWiNFO's 61.0).
            //
            // Their overall "GPU Utilization" is NOT derivable from these counters: engines run in
            // parallel, so summing overshoots (83 vs 70, and past 100 under load) while the max is
            // just the 3D engine again. It's a chip-occupancy figure only the driver knows — left
            // to the vendor readers rather than invented here.
            float load3d = 0;
            foreach (string instance in active)
            {
                if (!_engineCounters.TryGetValue(instance, out var pc))
                {
                    pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                    pc.NextValue(); // prime the baseline; this tick contributes 0 for a new process
                    _engineCounters[instance] = pc;
                }
                if (instance.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) load3d += pc.NextValue();
            }

            var perf = ReadPerfData();

            return new GpuSnapshot(
                Temp: perf.temp,
                LoadD3d: MathF.Min(load3d, 100),
                LoadTotal: 0, // unavailable via WDDM — see above
                Clock: perf.clock,
                VramUsedMb: vramUsedMb,
                VramTotalMb: _totalVramMb,
                FanRpm: perf.fanRpm,
                PowerW: perf.power,
                MemoryClockMhz: perf.memClock,
                MaxClockMhz: perf.maxClock
            );
        }
        catch
        {
            return null;
        }
    }

    private static float ReadCounter(string category, string counter, string instance)
    {
        // These are instantaneous (PERF_RAW) counters, so a single sample is accurate — no need
        // to hold the counter open across ticks for a rate delta.
        using var pc = new PerformanceCounter(category, counter, instance, readOnly: true);
        return pc.NextValue();
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegGetValue(IntPtr hkey, string subKey, string value, uint flags,
        out uint type, byte[] data, ref uint dataLen);

    private static readonly IntPtr HkeyLocalMachine = new(unchecked((int)0x80000002));
    private const uint RrfRtRegQword = 0x00000040;

    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const uint RrfRtRegSz = 0x00000002;

    // Windows perf counters expose VRAM *usage* but not capacity. The adapter's total dedicated
    // memory lives in the registry as HardwareInformation.qwMemorySize; take the largest across
    // display adapter subkeys (the discrete card). That value has a restrictive ACL that .NET's
    // managed RegistryKey refuses even when elevated, so read it via the native RegGetValue,
    // which honors the process token directly. Returns 0 if unreadable — UI handles 0 total.
    private static float ReadTotalVramMb() => ReadBiggestAdapter().vramMb;

    /// <summary>Name of the display adapter with the most VRAM — i.e. the discrete card.</summary>
    private static string? ReadDeviceName() => ReadBiggestAdapter().name;

    private static (float vramMb, string? name) ReadBiggestAdapter()
    {
        long best = 0;
        string? bestName = null;
        for (int i = 0; i < 16; i++)
        {
            string sub = $@"{DisplayClassKey}\{i:D4}";
            byte[] buf = new byte[8];
            uint len = 8;
            if (RegGetValue(HkeyLocalMachine, sub, "HardwareInformation.qwMemorySize", RrfRtRegQword, out _, buf, ref len) != 0)
                continue;

            long size = BitConverter.ToInt64(buf, 0);
            if (size <= best) continue;

            best = size;
            bestName = ReadString(sub, "DriverDesc");
        }
        return (best / (1024f * 1024f), bestName);
    }

    private static string? ReadString(string subKey, string value)
    {
        uint len = 0;
        if (RegGetValue(HkeyLocalMachine, subKey, value, RrfRtRegSz, out _, null!, ref len) != 0 || len == 0)
            return null;

        byte[] buf = new byte[len];
        if (RegGetValue(HkeyLocalMachine, subKey, value, RrfRtRegSz, out _, buf, ref len) != 0)
            return null;

        return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    // ---- D3DKMT: temperature + core clock from the WDDM kernel ----------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_OPENADAPTERFROMLUID { public LUID AdapterLuid; public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature;   // deci-degrees Celsius: 685 = 68.5 °C
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_NODE_PERFDATA
    {
        public uint NodeOrdinal;
        public uint PhysicalAdapterIndex;
        public ulong Frequency;    // Hz
        public ulong MaxFrequency;
        public ulong MaxFrequencyOC;
        public uint Voltage;
        public uint VoltageMax;
        public uint VoltageMaxOC;
        public ulong MaxTransitionLatency;
    }

    private const int KMTQAITYPE_NODEPERFDATA = 61;
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID arg);
    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO arg);
    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER arg);

    private (float temp, float clock, float fanRpm, float power, float memClock, float maxClock) ReadPerfData()
    {
        var open = new D3DKMT_OPENADAPTERFROMLUID { AdapterLuid = _adapterLuid };
        if (D3DKMTOpenAdapterFromLuid(ref open) != 0 || open.hAdapter == 0)
            return default;

        try
        {
            var perf = Query<D3DKMT_ADAPTER_PERFDATA>(open.hAdapter, KMTQAITYPE_ADAPTERPERFDATA,
                new D3DKMT_ADAPTER_PERFDATA { PhysicalAdapterIndex = 0 });
            // Every node reports the same figure (they share one graphics clock domain), and it
            // jumps between polls — so this is the chip's instantaneous clock, not a per-engine
            // one. Node 0 is therefore as good as any.
            var node = Query<D3DKMT_NODE_PERFDATA>(open.hAdapter, KMTQAITYPE_NODEPERFDATA,
                new D3DKMT_NODE_PERFDATA { NodeOrdinal = 0, PhysicalAdapterIndex = 0 });

            return (
                temp: perf.Temperature / 10f,            // deci-°C → °C
                clock: node.Frequency / 1_000_000f,      // Hz → MHz — instantaneous, see notes below
                fanRpm: perf.FanRPM,
                // AMD leaves Power at 0; NVIDIA/Intel may fill it. Callers treat 0 as "unreported".
                power: perf.Power,
                memClock: perf.MemoryFrequency / 1_000_000f,
                maxClock: node.MaxFrequency / 1_000_000f
            );
        }
        finally
        {
            var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
            D3DKMTCloseAdapter(ref close);
        }
    }

    // Marshals `input` into a native buffer (its fields double as the query's input parameters),
    // runs the query, and reads the driver-filled struct back out.
    private static T Query<T>(uint hAdapter, int type, T input) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(input, buf, false);
            var q = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = hAdapter,
                Type = type,
                pPrivateDriverData = buf,
                PrivateDriverDataSize = (uint)size,
            };
            return D3DKMTQueryAdapterInfo(ref q) == 0 ? Marshal.PtrToStructure<T>(buf) : default;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // Instance name "luid_0x00000000_0x00014C8E_phys_0" → LUID{ High=0x0, Low=0x14C8E }.
    private static bool TryParseLuid(string instance, out LUID luid)
    {
        luid = default;
        var parts = instance.Split('_');
        int i = Array.IndexOf(parts, "luid");
        if (i < 0 || i + 2 >= parts.Length) return false;
        try
        {
            luid.HighPart = Convert.ToInt32(parts[i + 1], 16);
            luid.LowPart = Convert.ToUInt32(parts[i + 2], 16);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var pc in _engineCounters.Values) pc.Dispose();
        _engineCounters.Clear();
    }
}
