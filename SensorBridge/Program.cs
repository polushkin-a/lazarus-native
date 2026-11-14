// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of the MPL was
// not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
using LibreHardwareMonitor.Hardware;
using SensorBridge;
using SensorBridge.GpuReaders;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

// GPU sensors come exclusively from the vendor SDKs (ADLX/NVAPI) below — LibreHardwareMonitor
// is CPU/RAM only. Leaving its GPU support on made it open the AMD ADL session first, which
// starved ADLX's own GPU enumeration (GetGPUs returned zero).
var computer = new Computer
{
    IsCpuEnabled = true,
    IsGpuEnabled = false,       // GPU comes from WDDM/D3DKMT (WindowsGpuReader), not LHM
    IsMemoryEnabled = true,
    IsMotherboardEnabled = true, // VRM/chipset temps, voltages, chassis fans
    IsStorageEnabled = true,     // drive temps, health, activity
    IsNetworkEnabled = true,     // up/down throughput
    IsControllerEnabled = true,  // AIO/fan controllers
};

// Init the vendor GPU reader before LibreHardwareMonitor opens, so nothing else holds the
// driver first.
// Vendor SDKs first (they include temp/clock); Windows WDDM perf counters as the universal
// fallback for load/VRAM when the vendor path is unavailable (e.g. driver too new for ADL/ADLX).
IGpuReader? gpuReader = AmdAdlReader.TryCreate()
    ?? AmdAdlxGpuReader.TryCreate()
    ?? (IGpuReader?)NvidiaGpuReader.TryCreate()
    ?? WindowsGpuReader.TryCreate();
Console.Error.WriteLine($"GPU reader: {gpuReader?.GetType().Name ?? "none"}");

// Throttling/limit state that LHM can't see without reading MSRs — Windows already publishes it.
WindowsPerfReader? perfReader = WindowsPerfReader.TryCreate();

// Latency to a public resolver. Runs on its own timer so the telemetry loop never waits on it.
var pingReader = new PingReader();
pingReader.Start();

// FPS/frametime of the foreground game via ETW present events (--once skips it; the ETW
// session needs a moment to warm up and there's no second sample in one-shot mode anyway).
FpsReader? fpsReader = null;
if (!args.Contains("--once"))
{
    fpsReader = new FpsReader();
    fpsReader.Start();
}

computer.Open();

var updateVisitor = new UpdateVisitor();
var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var runOnce = args.Contains("--once");

// When launched by the Electron host, --port tells us where to stream
// telemetry over loopback TCP (stdio doesn't survive UAC elevation cleanly).
StreamWriter? socketWriter = null;
var portIdx = Array.IndexOf(args, "--port");
if (portIdx >= 0 && portIdx + 1 < args.Length && int.TryParse(args[portIdx + 1], out var port))
{
    for (int attempt = 0; attempt < 10 && socketWriter is null; attempt++)
    {
        try
        {
            var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            socketWriter = new StreamWriter(client.GetStream()) { AutoFlush = true };
        }
        catch
        {
            Thread.Sleep(300);
        }
    }
    if (socketWriter is null && !runOnce) return;
}

if (args.Contains("--report"))
{
    computer.Accept(updateVisitor);
    Console.WriteLine(computer.GetReport());
    return;
}

if (args.Contains("--debug"))
{
    for (int i = 0; i < 5; i++)
    {
        computer.Accept(updateVisitor);
        Thread.Sleep(500);
    }
    foreach (var hardware in computer.Hardware)
    {
        Console.WriteLine($"== {hardware.HardwareType} :: {hardware.Name} ==");
        foreach (var sensor in hardware.Sensors)
        {
            Console.WriteLine($"  [{sensor.SensorType}] {sensor.Name} = {sensor.Value}");
        }
        foreach (var sub in hardware.SubHardware)
        {
            Console.WriteLine($"  -- sub: {sub.HardwareType} :: {sub.Name} --");
            foreach (var sensor in sub.Sensors)
            {
                Console.WriteLine($"    [{sensor.SensorType}] {sensor.Name} = {sensor.Value}");
            }
        }
    }
    return;
}

// 2 Hz — measured, not chosen. FPS is the one value fast enough to look broken when it updates
// slowly, so the rate was pushed until it stopped paying:
//
//   tick 500 / quantum 8KB -> 2.0 Hz, every sample a fresh number   <- here
//   tick 250 / quantum 8KB -> 3.5 Hz, 8 of 19 samples repeated
//   tick 250 / quantum 4KB -> 3.8 Hz, 3 of 19 samples repeated
//
// The ceiling is ETW, not this loop: frames arrive in buffer deliveries, so a faster tick just
// re-reports a window it already sent. Twice the rate carrying a third stale samples is worse
// than half the rate always telling the truth — a repeat reads as a frozen counter, which is the
// exact complaint this was meant to fix.
const int TickMs = 500;

// But LibreHardwareMonitor is not refreshed every tick. Its Update() is the expensive half of
// this loop (SMART polling dominates), and what it owns — temperatures, drive health, fan
// speeds — has thermal inertia measured in seconds, so a second-old reading is identical.
// Frame stats, GPU and perf counters are already in memory: they cost nothing to re-read and
// are exactly what needs to be fresh.
const int HardwareEveryNTicks = 2;

var clock = new System.Diagnostics.Stopwatch();

for (long tick = 0; ; tick++)
{
    clock.Restart();

    if (tick % HardwareEveryNTicks == 0) computer.Accept(updateVisitor);

    var payload = new TelemetryPayload(
        OnlyFinite(BuildMetrics(computer, gpuReader, fpsReader, perfReader, pingReader)),
        BuildDevices(computer, gpuReader));
    var json = JsonSerializer.Serialize(payload, jsonOptions);
    Console.WriteLine(json);
    Console.Out.Flush();

    if (socketWriter is not null)
    {
        try
        {
            socketWriter.WriteLine(json);
        }
        catch (IOException)
        {
            // The host is gone. Exit rather than linger: we run elevated, so an unelevated
            // Electron cannot kill us — a survivor would hold the ETW session and the port,
            // and keep the exe locked against the next build. Leaving is our own job.
            break;
        }
    }

    if (runOnce) break;

    // Sleep the remainder of the period, not a flat TickMs. Collecting a payload costs ~80ms
    // (LHM's refresh dominates), and sleeping 500 on top of it made the real rate 1.7Hz, not 2 —
    // the drift lands exactly on FPS, the one value that needs to keep up.
    var spent = clock.ElapsedMilliseconds;
    if (spent < TickMs) Thread.Sleep((int)(TickMs - spent));
}

gpuReader?.Dispose();
fpsReader?.Dispose();
perfReader?.Dispose();
pingReader.Dispose();

// Emits every sensor as a flat "path -> value" catalog (hardware.cpu.temperature.core_tctl_tdie),
// rather than a fixed struct: whatever the machine exposes shows up, and clients can subscribe to
// the exact keys they want. LHM covers CPU/RAM/motherboard/storage/network; GPU comes from
// WDDM/D3DKMT and frame stats from ETW.
static Dictionary<string, object?> BuildMetrics(Computer computer, IGpuReader? gpuReader, FpsReader? fpsReader, WindowsPerfReader? perfReader, PingReader pingReader)
{
    var m = new Dictionary<string, object?>();

    // When a machine has several of the same component (3 drives, 4 NICs, physical vs virtual
    // memory), qualify the key with the device's own name rather than an index: it says what the
    // thing actually is, and it stays stable if enumeration order changes.
    foreach (var group in Reported(computer).GroupBy(h => h.HardwareType))
    {
        var list = group.ToList();
        foreach (var hw in list)
        {
            string prefix = $"hardware.{Slug(group.Key.ToString())}";
            if (list.Count > 1) prefix += $".{Slug(hw.Name)}";
            AddSensors(m, prefix, hw);
        }
    }

    perfReader?.Read(m);

    // Always present, null while a probe is failing — a widget bound to it should read "n/a"
    // rather than vanish when the network drops.
    m["network.ping"] = pingReader.Read() is { } ping ? Round(ping) : null;

    float? gpuLoad = null;
    if (gpuReader?.Read() is { } gpu)
    {
        gpuLoad = gpu.LoadD3d;
        m["hardware.gpu.temperature.core"] = Round(gpu.Temp);
        // Verified against HWiNFO: our summed 3D engine tracks its "D3D Usage" (61.6 vs 61.0).
        m["hardware.gpu.load.d3d"] = Round(gpu.LoadD3d);
        // Overall chip utilization only exists if a vendor SDK reported it; WDDM can't derive it.
        if (gpu.LoadTotal > 0) m["hardware.gpu.load.total"] = Round(gpu.LoadTotal);
        // Instantaneous clock sampled once per tick while the real one swings thousands of times
        // a second, so it sits between HWiNFO's "GPU Clock" (2529) and "Effective" (1417) and
        // matches neither — it's a snapshot, not an average.
        m["hardware.gpu.clock.core"] = Round(gpu.Clock);
        m["hardware.gpu.clock.core_max"] = Round(gpu.MaxClockMhz);
        m["hardware.gpu.clock.memory"] = Round(gpu.MemoryClockMhz); // data rate; vendor tools may halve this
        // smalldata = megabytes (LHM's convention); data = gigabytes. VRAM is reported in MB.
        m["hardware.gpu.smalldata.vram_used"] = Round(gpu.VramUsedMb);
        m["hardware.gpu.smalldata.vram_total"] = Round(gpu.VramTotalMb);
        m["hardware.gpu.smalldata.vram_free"] = Round(gpu.VramTotalMb - gpu.VramUsedMb);
        m["hardware.gpu.load.vram"] = gpu.VramTotalMb > 0 ? Round(gpu.VramUsedMb / gpu.VramTotalMb * 100) : null;
        m["hardware.gpu.fan.core"] = Round(gpu.FanRpm);
        // AMD's driver leaves D3DKMT's power field empty — emit the key only when it's real,
        // rather than publishing a permanent 0 W that reads as a working sensor.
        if (gpu.PowerW > 0) m["hardware.gpu.power.total"] = Round(gpu.PowerW);
    }

    // Always publish the gaming keys, null when nothing is presenting — a widget bound to
    // gaming.fps should read "n/a" while you're on the desktop, not vanish and reappear.
    //
    // FpsReader already ignores the shell, browsers and our own window by name. This is the
    // backstop for the ones no list can know — a new Electron app, a launcher we haven't met:
    // a game does not run on an idle GPU. Deliberately low (5%), because the point is to reject
    // a desktop at rest, not to judge how hard a game works: a menu at 60fps or a CPU-bound
    // title can sit near 20%, and those are still games.
    // Only applied when the GPU reader actually reported a load; a null reading must not
    // silently blank FPS that ETW measured correctly.
    const float IdleGpuPercent = 5;
    var f = gpuLoad is { } gl && gl < IdleGpuPercent ? null : fpsReader?.Read();

    m["gaming.fps"] = f is null ? null : Round(f.Fps);
    m["gaming.frametime"] = f is null ? null : Round(f.FrametimeMs, 1);
    m["gaming.fps_1pct_low"] = f?.Low1Pct is { } l1 ? Round(l1) : null;
    m["gaming.fps_01pct_low"] = f?.Low01Pct is { } l01 ? Round(l01) : null;
    m["gaming.fps_99pct"] = f?.High99Pct is { } h99 ? Round(h99) : null;

    return m;
}

/// <summary>
/// Maps each metric prefix to the hardware it came from ("hardware.cpu" -> "AMD Ryzen 7 7800X3D").
/// The names aren't in the metric paths — those use slugs so keys stay stable — so clients get
/// them here, to label a column or a widget with the actual device.
/// </summary>
static Dictionary<string, string> BuildDevices(Computer computer, IGpuReader? gpuReader)
{
    var d = new Dictionary<string, string>();

    foreach (var group in Reported(computer).GroupBy(h => h.HardwareType))
    {
        var list = group.ToList();
        foreach (var hw in list)
        {
            string prefix = $"hardware.{Slug(group.Key.ToString())}";
            if (list.Count > 1) prefix += $".{Slug(hw.Name)}";
            d[prefix] = hw.Name;
        }
    }

    if (gpuReader?.DeviceName is { } gpuName) d["hardware.gpu"] = gpuName;
    // Which GPU path won the fallback chain (AmdAdl/AmdAdlx/Nvidia/Windows) — diagnostic only, so the
    // desktop can report why some GPU metrics are present or absent on a given machine.
    if (gpuReader is not null) d["gpu.reader"] = gpuReader.GetType().Name;

    return d;
}

// The hardware worth reporting: everything LibreHardwareMonitor found, except network "adapters" that aren't real
// ones (filter drivers, WAN miniports, absent adapters — NetAdapters.cs).
static IEnumerable<IHardware> Reported(Computer computer) =>
    computer.Hardware.Where(h => h.HardwareType != HardwareType.Network || NetAdapters.IsReal(h.Name));

static void AddSensors(Dictionary<string, object?> m, string prefix, IHardware hardware)
{
    foreach (var s in hardware.Sensors)
    {
        if (s.Value is not float v) continue;
        m[$"{prefix}.{Slug(s.SensorType.ToString())}.{Slug(s.Name)}"] = Round(v, 2);
    }
    // Sub-hardware (a board's SuperIO chip, a CPU's memory controller) reports under the parent —
    // its sensor names are already distinct, so they flatten into the same namespace.
    foreach (var sub in hardware.SubHardware)
        AddSensors(m, prefix, sub);
}

static double Round(float v, int digits = 0) => Math.Round(v, digits);

// A sensor that reads NaN or ±Infinity (a division by zero inside a driver or a counter) has no reading: it goes out
// as null. JSON can't carry those values at all — one of them used to throw out of the serializer and stop the whole
// stream. Each such sensor is named once on stderr, so it can be tracked down.
static Dictionary<string, object?> OnlyFinite(Dictionary<string, object?> m)
{
    var bad = m.Where(kv => kv.Value is double d && !double.IsFinite(d)).ToList(); // not while enumerating m
    foreach (var (key, value) in bad)
    {
        m[key] = null;
        if (NonFinite.Seen.Add(key)) Console.Error.WriteLine($"non-finite reading, sent as null: {key} = {value}");
    }
    return m;
}

// "Core (Tctl/Tdie)" -> "core_tctl_tdie", "CPU Core #1" -> "cpu_core_1"
static string Slug(string name)
{
    var sb = new System.Text.StringBuilder(name.Length);
    foreach (char c in name.ToLowerInvariant())
        sb.Append(char.IsLetterOrDigit(c) ? c : '_');
    return string.Join('_', sb.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
}

/// <param name="Metrics">Flat "sensor path -> value" catalog.</param>
/// <param name="Devices">Metric prefix -> the hardware's own name, for labelling.</param>
record TelemetryPayload(Dictionary<string, object?> Metrics, Dictionary<string, string> Devices);

/// <summary>Sensors already reported as non-finite (OnlyFinite) — each is named once, not every tick.</summary>
static class NonFinite
{
    public static readonly HashSet<string> Seen = new();
}

class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);

    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (IHardware sub in hardware.SubHardware)
            sub.Accept(this);
    }

    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
