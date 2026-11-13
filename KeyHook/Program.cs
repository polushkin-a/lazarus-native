using System.Runtime.InteropServices;

// KeyHook — Lazarus' own low-level keyboard-hook helper, spawned by the Electron main process ONLY while
// the Actions shortcut recorder is focused. It installs a system-wide WH_KEYBOARD_LL hook and SWALLOWS
// every key (returns 1), so OS-global combos — Win+D, Ctrl+Shift+Esc — are captured for recording instead
// of firing their Windows action. Each key event is streamed to stdout as `vkCode,DOWN|UP`; the parent
// builds the combo. Because we block the modifier down-events too, the OS never sees the chord at all, so
// there's no Start-menu-on-Win-release quirk to work around.
//
// This is OUR binary (vs. the third-party node-global-key-listener exe Defender quarantined as a keylogger)
// precisely so it can be code-signed for release. Safety: the hook blocks ALL keys while alive, so it must
// never outlive the recording — it exits the instant the parent's stdin pipe closes (parent died) and hard-
// caps its own lifetime. Killing the process also removes the hook (Windows unhooks on process exit).
internal static class Program
{
    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION = 0;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const uint WM_QUIT = 0x0012;
    private const int MAX_LIFETIME_MS = 120_000; // hard cap: never leave the keyboard globally swallowed

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kept in a static field so the GC can't collect the delegate the OS is calling back into.
    private static readonly HookProc _proc = HookCallback;
    private static IntPtr _hook = IntPtr.Zero;
    private static uint _mainThreadId;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    private static void Main()
    {
        _mainThreadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Console.Error.WriteLine($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        // Watchdog 1: the parent (Electron) closing its pipe — a normal stop OR a crash — ends stdin, so we
        // quit and unhook. Without this a parent crash would leave every key swallowed system-wide.
        var stdinWatch = new Thread(() =>
        {
            try { while (Console.In.ReadLine() != null) { } } catch { }
            Quit();
        }) { IsBackground = true };
        stdinWatch.Start();

        // Watchdog 2: a hard lifetime cap, in case stdin never EOFs (belt and suspenders).
        var lifetime = new Thread(() =>
        {
            Thread.Sleep(MAX_LIFETIME_MS);
            Quit();
        }) { IsBackground = true };
        lifetime.Start();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWindowsHookEx(_hook);
    }

    private static void Quit() => PostThreadMessage(_mainThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            int msg = (int)wParam;
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (down || up)
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                Console.Out.WriteLine($"{k.vkCode},{(down ? "DOWN" : "UP")}");
                Console.Out.Flush();
                return (IntPtr)1; // swallow — the OS sees nothing while we record
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
