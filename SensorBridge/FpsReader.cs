using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing.Session;

namespace SensorBridge;

/// <summary>
/// Measures FPS/frametime the same way PresentMon does: listens to the Windows WDDM present
/// event stream over ETW (Microsoft-Windows-DXGI, PresentStart) instead of injecting into games.
/// Nothing touches game memory, so it's anti-cheat safe. Requires an elevated process (ETW kernel
/// sessions do) — SensorBridge already runs elevated for the CPU sensors.
///
/// ponytail: DXGI only, which covers D3D9/11/12 titles; Vulkan/OpenGL present through Dxgkrnl —
/// add that provider if those matter.
/// </summary>
public sealed class FpsReader : IDisposable
{
    // Microsoft-Windows-DXGI provider; event 42 = PresentStart (one per presented frame).
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private const int PresentStartEventId = 42;

    /// <summary>
    /// Averaging window for FPS/frametime — deliberately WIDER than the 250ms poll period.
    ///
    /// PresentMon and HWiNFO use 1000ms; at our poll rate that overlaps by 75% and the value
    /// crawls toward each change instead of showing it. 500ms was measured as the sweet spot.
    ///
    /// Narrowing it to match the tick sounds right and measures worse: 8 of 19 samples repeated
    /// versus 3 at 500ms. ETW delivers frames in buffers, not continuously, so a window as short
    /// as the poll period sometimes lands between deliveries with nothing new in it — and reports
    /// the previous value. The wider window always spans a delivery.
    ///
    /// So the overlap isn't the enemy; an empty window is. Anyone tempted to "fix" this by
    /// matching window to tick: it was tried, it's worse.
    ///
    /// This only moves FPS/frametime. The percentile lows keep the full history (~1000 frames):
    /// a "1% low" over half a second would be a different, far twitchier claim.
    /// </summary>
    private const double AvgWindowMs = 500;

    // Anything slower than this isn't a frame, it's a gap in presenting (alt-tab, minimise).
    private const double MaxFrametimeMs = 1000;

    // How long a silent process is kept before its tracking state is discarded. Comfortably longer
    // than a loading screen or an alt-tab, so a paused game keeps its history and its percentile
    // lows; short enough that a closed one doesn't linger.
    private const double ForgetAfterSeconds = 60;

    private readonly ConcurrentDictionary<int, FrameState> _byPid = new();
    private TraceEventSession? _session;
    private Thread? _thread;

    // Everything on a desktop presents frames — the browser, the shell, this dashboard. Without a
    // filter the "busiest presenter" is whatever chrome tab is animating, and the tile reports a
    // confident 58 FPS while no game is running. Those numbers are worse than "n/a": they look real.
    //
    // A deny-list, not an allow-list: games are unbounded, non-games are a short known set. A miss
    // here shows a wrong number, so anything routinely presenting at speed belongs in it.
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows shell
        "explorer", "dwm", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost",
        "TextInputHost", "ApplicationFrameHost", "SystemSettings", "LockApp", "SearchApp",
        // Us
        "Lazarus", "electron", "SensorBridge",
        // Browsers
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "yandex",
        // Electron/Chromium apps
        "Discord", "Slack", "Telegram", "Code", "WhatsApp", "Spotify", "Notion", "Obsidian",
        // Launchers and capture — these idle at high frame rates and would mask the game
        "steam", "steamwebhelper", "EpicGamesLauncher", "Battle.net", "GalaxyClient",
        "RiotClientUX", "EADesktop", "obs64", "obs32", "nvcontainer",
    };

    // Resolving a PID costs a syscall, so remember the verdict. A PID can be recycled, but a
    // wrongly-cached name only mislabels one process until it stops presenting.
    private readonly ConcurrentDictionary<int, bool> _isGame = new();

    // Enough frames for a meaningful 0.1% bucket (needs >=1000 samples to hold even one frame);
    // at 144 fps this is roughly the last 7 seconds.
    private const int HistorySize = 1000;

    private sealed class FrameState
    {
        public DateTime LastPresentUtc;

        // Ring buffer of frame timestamps (session-relative ms), written from the ETW thread and
        // read from the telemetry loop — guard both with this lock. Storing the timestamps rather
        // than a smoothed frametime is what lets us average over a fixed window: an EMA weights
        // recent frames and hides the occasional long one, which read ~17% low against HWiNFO.
        public readonly double[] History = new double[HistorySize];
        public int Count;
        public int Next;
        public readonly object Lock = new();

        /// <summary>Frames presented within the last <paramref name="windowMs"/>, for picking the busiest process.</summary>
        public int FramesInWindow(double windowMs)
        {
            lock (Lock)
            {
                if (Count < 2) return 0;
                int last = (Next - 1 + HistorySize) % HistorySize;
                double newest = History[last];
                int n = 0;
                for (int i = 0; i < Count; i++)
                {
                    int idx = (last - i + HistorySize) % HistorySize;
                    if (newest - History[idx] > windowMs) break;
                    n++;
                }
                return n;
            }
        }
    }

    public void Start()
    {
        _thread = new Thread(RunSession) { IsBackground = true, Name = "etw-fps" };
        _thread.Start();
    }

    private void RunSession()
    {
        try
        {
            // A unique session name so a leftover session from a crashed run doesn't collide;
            // StopOnDispose tears the kernel session down so it doesn't leak system-wide.
            _session = new TraceEventSession("LazarusFpsSession") { StopOnDispose = true };

            // ETW hands a buffer over only once it fills (or when its own ~1s timer fires), so the
            // default buffer made frames arrive in clumps: the stream ran at 2Hz while FPS repeated
            // the same value two or three ticks running. 8KB fills in well under the 500ms tick at
            // gaming frame rates, which is what makes every sample carry a fresh number. The cost is
            // more frequent handoffs — cheap next to a PresentStart record we barely decode.
            //
            // 4KB was measured too: it only matters if the tick drops to 250ms, and that trade
            // (3.8Hz with a third of samples stale) is worse than 2Hz that never repeats.
            _session.BufferQuantumKB = 8;
            _session.BufferSizeMB = 4;

            _session.EnableProvider(DxgiProvider);

            // AllEvents rather than Dynamic: we only need the PID and timestamp, so there's no
            // reason to pay for manifest-based payload decoding on every presented frame.
            _session.Source.AllEvents += ev =>
            {
                if (ev.ProviderGuid != DxgiProvider || (int)ev.ID != PresentStartEventId) return;
                RegisterFrame(ev.ProcessID, ev.TimeStampRelativeMSec);
            };

            _session.Source.Process(); // blocks until the session is disposed
        }
        catch
        {
            // ETW unavailable (not elevated, session limit, etc.) — FPS just stays null.
        }
    }

    /// <summary>False for the shell, browsers and our own window — see <see cref="NotGames"/>.</summary>
    private bool IsGame(int pid) => _isGame.GetOrAdd(pid, static id =>
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(id);
            return !NotGames.Contains(p.ProcessName);
        }
        catch
        {
            // Already exited, or protected. Not something we can attribute frames to.
            return false;
        }
    });

    private void RegisterFrame(int pid, double timestampMs)
    {
        if (!IsGame(pid)) return;

        var state = _byPid.GetOrAdd(pid, _ => new FrameState());
        state.LastPresentUtc = DateTime.UtcNow;

        lock (state.Lock)
        {
            state.History[state.Next] = timestampMs;
            state.Next = (state.Next + 1) % HistorySize;
            if (state.Count < HistorySize) state.Count++;
        }
    }

    /// <param name="High99Pct">Average FPS of the fastest 1% of frames — HWiNFO calls this "99%".</param>
    public record FpsSample(float Fps, float FrametimeMs, float? Low1Pct, float? Low01Pct, float? High99Pct);

    /// <summary>
    /// Frame stats of whichever process is rendering hardest — the game — or null if nothing is.
    ///
    /// Deliberately not the foreground process: the dashboard is meant to be read on a phone or a
    /// second screen, so the game's FPS has to survive alt-tabbing away from it.
    /// </summary>
    public FpsSample? Read()
    {
        var now = DateTime.UtcNow;
        FrameState? state = null;
        int bestFrames = 0;

        foreach (var (pid, candidate) in _byPid)
        {
            // Skip anything that stopped presenting (closed, minimised) — a stale entry would
            // otherwise report a frozen FPS forever.
            if ((now - candidate.LastPresentUtc).TotalSeconds > 1)
            {
                // And forget it once it's long gone. Both dictionaries are keyed by PID and
                // nothing ever removed from them, so a session spanning an evening of launching
                // games accumulated an entry per process — plus, Windows recycles PIDs: a cached
                // "not a game" verdict would then silently mute a real game that inherited the
                // number. Dropping dead entries fixes the leak and the staleness together.
                if ((now - candidate.LastPresentUtc).TotalSeconds > ForgetAfterSeconds)
                {
                    _byPid.TryRemove(pid, out _);
                    _isGame.TryRemove(pid, out _);
                }
                continue;
            }

            int n = candidate.FramesInWindow(AvgWindowMs);
            if (n > bestFrames) { bestFrames = n; state = candidate; }
        }

        if (state is null) return null;

        double[] stamps;
        lock (state.Lock)
        {
            if (state.Count < 2) return null;
            stamps = new double[state.Count];
            // Unwrap the ring into chronological order.
            int start = state.Count < HistorySize ? 0 : state.Next;
            for (int i = 0; i < state.Count; i++)
                stamps[i] = state.History[(start + i) % HistorySize];
        }

        // Average over a fixed window, the way PresentMon/HWiNFO report it: frames in the window
        // divided by its span, rather than a smoothed per-frame value.
        double newest = stamps[^1];
        int from = 0;
        for (int i = stamps.Length - 1; i >= 0; i--)
        {
            if (newest - stamps[i] > AvgWindowMs) { from = i + 1; break; }
        }
        int frames = stamps.Length - from - 1; // intervals, not samples
        if (frames < 1) return null;

        double spanMs = newest - stamps[from];
        if (spanMs <= 0) return null;

        float frametime = (float)(spanMs / frames);
        float fps = (float)(frames / spanMs * 1000);

        // Percentile lows need a longer tail than the 1s average window, so they use the whole
        // history (~7s at 144fps) — that's also what makes a 0.1% bucket possible at all.
        // Gaps where the app stopped presenting (minimised, alt-tabbed) are not frames: left in,
        // they land straight in the slowest 1% and drag it far below what it really was.
        var intervals = new List<double>(stamps.Length);
        for (int i = 1; i < stamps.Length; i++)
        {
            double dt = stamps[i] - stamps[i - 1];
            if (dt > 0 && dt <= MaxFrametimeMs) intervals.Add(dt);
        }

        var arr = intervals.ToArray();
        return new FpsSample(fps, frametime,
            LowFps(arr, 0.01), LowFps(arr, 0.001), HighFps(arr, 0.01));
    }

    /// <summary>Average FPS of the fastest N% of frames — the mirror of LowFps.</summary>
    private static float? HighFps(double[] history, double fraction)
    {
        int take = (int)(history.Length * fraction);
        if (take < 1) return null;

        var sorted = (double[])history.Clone();
        Array.Sort(sorted); // fastest frames first
        double sum = 0;
        for (int i = 0; i < take; i++) sum += sorted[i];
        double avg = sum / take;
        return avg > 0 ? (float)(1000.0 / avg) : null;
    }

    /// <summary>
    /// "N% low" = the average FPS of the slowest N% of recent frames: sort frametimes descending
    /// (slowest first), average that slice, convert to FPS. Null until enough frames exist for the
    /// bucket to hold at least one.
    /// </summary>
    private static float? LowFps(double[] history, double fraction)
    {
        int take = (int)(history.Length * fraction);
        if (take < 1) return null;

        history = (double[])history.Clone(); // sorting in place would corrupt the caller's array
        Array.Sort(history);
        Array.Reverse(history); // slowest frames first
        double sum = 0;
        for (int i = 0; i < take; i++) sum += history[i];
        double avg = sum / take;
        return avg > 0 ? (float)(1000.0 / avg) : null;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _thread?.Join(1000);
    }
}
