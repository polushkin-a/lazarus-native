// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of the MPL was
// not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Runtime.InteropServices;

/// <summary>
/// The network adapters worth reporting: real hardware that is present — not the light-weight filter drivers stacked on
/// it (QoS Packet Scheduler, WFP filters, "Native WiFi Filter Driver"), WAN miniports, tunnels, or adapters that are
/// installed but not plugged in. Since .NET 10 the runtime lists every interface Windows has, and LibreHardwareMonitor
/// reports a sensor set for each — ~30 "adapters" with NaN utilization instead of the 2–4 real ones. Windows says which
/// is which in its interface table (GetIfTable2: HardwareInterface / FilterInterface / OperStatus). If that table can't
/// be read, nothing is filtered — better every adapter than none.
/// </summary>
internal static class NetAdapters
{
    // MIB_IF_ROW2 on x64 (netioapi.h): 1352 bytes; the table's rows start 8 bytes in (ULONG NumEntries + padding).
    private const int RowSize = 1352;
    private const int FirstRow = 8;
    private const int AliasOffset = 28; // after NET_LUID (8), NET_IFINDEX (4), GUID (16); WCHAR[257]
    private const int FlagsOffset = 1152; // InterfaceAndOperStatusFlags (bit 0 HardwareInterface, bit 1 FilterInterface)
    private const int OperStatusOffset = 1156;
    private const int IfOperStatusNotPresent = 6;

    private static HashSet<string>? _real;
    private static long? _readAt; // null = never read
    private const int RefreshMs = 10_000; // adapters come and go (a USB dongle) — re-read now and then, not every tick

    [DllImport("iphlpapi.dll")]
    private static extern int GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    /// <summary>Is this LibreHardwareMonitor network "hardware" (named by its alias, e.g. "Wi-Fi 2") a real adapter?</summary>
    public static bool IsReal(string name)
    {
        var now = Environment.TickCount64;
        if (_readAt is not { } at || now - at > RefreshMs)
        {
            _real = Read();
            _readAt = now;
        }
        return _real is null || _real.Contains(name);
    }

    private static HashSet<string>? Read()
    {
        if (GetIfTable2(out var table) != 0) return null;
        try
        {
            var count = Marshal.ReadInt32(table);
            var real = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++)
            {
                var row = table + FirstRow + i * RowSize;
                var flags = Marshal.ReadByte(row, FlagsOffset);
                var hardware = (flags & 0x1) != 0;
                var filter = (flags & 0x2) != 0;
                var present = Marshal.ReadInt32(row, OperStatusOffset) != IfOperStatusNotPresent;
                if (hardware && !filter && present && Marshal.PtrToStringUni(row + AliasOffset) is { Length: > 0 } alias)
                    real.Add(alias);
            }
            return real.Count > 0 ? real : null; // nothing recognised = the layout isn't what we think: don't filter
        }
        finally
        {
            FreeMibTable(table);
        }
    }
}
