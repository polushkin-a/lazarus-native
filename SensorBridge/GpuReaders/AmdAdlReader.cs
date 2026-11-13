// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of the MPL was
// not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Runtime.InteropServices;

namespace SensorBridge.GpuReaders;

/// <summary>
/// Reads AMD GPU sensors via the legacy ADL "PMLog" streaming API (atiadlxx.dll, ships with the
/// driver). This is the path the LibreHardwareMonitor GUI uses for current-gen (RDNA) cards —
/// the newer ADLX API's GetGPUs() returns zero on this hardware, and ADL's one-shot
/// New_QueryPMLogData_Get returns NOT_SUPPORTED, but the Device_Create → PMLog_Start streaming
/// path works: the driver publishes a shared-memory block we re-read each tick.
/// Sensor indices/struct layouts are from AMD's official ADL SDK (adl_defines.h / adl_structures.h).
/// </summary>
public sealed class AmdAdlReader : IGpuReader
{
    private const int AdlMaxPath = 256;
    private const int PmLogMaxSensors = 256;
    private const int AmdVendorId = 1002;

    // ADL_PMLOG_SENSORS indices (AMD adl_defines.h). Terminator for the sensor list is 0.
    private const ushort SensorMaxTypes = 0;
    private const int SensorGfxClock = 1;      // MHz
    private const int SensorTempEdge = 8;      // °C
    private const int SensorActivityGfx = 19;  // %

    private delegate IntPtr MainMemoryAllocDelegate(int size);
    private static readonly MainMemoryAllocDelegate MainMemoryAlloc = Marshal.AllocHGlobal;

    [DllImport("atiadlxx.dll")]
    private static extern int ADL2_Main_Control_Create(MainMemoryAllocDelegate callback, int enumConnectedAdapters, out IntPtr context);
    [DllImport("atiadlxx.dll")]
    private static extern int ADL2_Main_Control_Destroy(IntPtr context);
    [DllImport("atiadlxx.dll")]
    private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, ref int numAdapters);
    [DllImport("atiadlxx.dll")]
    private static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr context, IntPtr adapterInfoArray, int sizeBytes);
    [DllImport("atiadlxx.dll")]
    private static extern int ADL2_Adapter_DedicatedVRAMUsage_Get(IntPtr context, int adapterIndex, out int vramUsedMb);
    [DllImport("atiadlxx.dll")]
    private static extern int ADL2_Adapter_MemoryInfo2_Get(IntPtr context, int adapterIndex, out ADLMemoryInfo2 memInfo);
    [DllImport("atiadlxx.dll")]
    private static extern int ADL2_Device_PMLog_Device_Create(IntPtr context, int adapterIndex, ref int device);
    [DllImport("atiadlxx.dll")]
    private static extern int ADL2_Device_PMLog_Device_Destroy(IntPtr context, int device);
    [DllImport("atiadlxx.dll")]
    private static extern int ADL2_Adapter_PMLog_Support_Get(IntPtr context, int adapterIndex, ref ADLPMLogSupportInfo support);
    [DllImport("atiadlxx.dll")]
    private static extern int ADL2_Adapter_PMLog_Start(IntPtr context, int adapterIndex, ref ADLPMLogStartInput input, ref ADLPMLogStartOutput output, int device);
    [DllImport("atiadlxx.dll")]
    private static extern int ADL2_Adapter_PMLog_Stop(IntPtr context, int adapterIndex, int device);

    [StructLayout(LayoutKind.Sequential)]
    private struct ADLPMLogSupportInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = PmLogMaxSensors)] public ushort[] usSensors;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public int[] ulReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ADLPMLogStartInput
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = PmLogMaxSensors)] public ushort[] usSensors;
        public uint ulSampleRate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)] public int[] ulReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ADLPMLogStartOutput
    {
        public IntPtr pLoggingAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 14)] public int[] ulReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ADLPMLogData
    {
        public uint ulVersion;
        public uint ulActiveSampleRate;
        public ulong ulLastUpdated;
        // ulValues[256][2] flattened: pairs of (sensorId, value).
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = PmLogMaxSensors * 2)] public uint[] ulValues;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = PmLogMaxSensors)] public uint[] ulReserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct ADLAdapterInfo
    {
        public int Size;
        public int AdapterIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string UDID;
        public int BusNumber;
        public int DeviceNumber;
        public int FunctionNumber;
        public int VendorID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string AdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string DisplayName;
        public int Present;
        public int Exist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string DriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string DriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string PNPString;
        public int OSDisplayIndex;
    }

    // Larger in the SDK; we only read iMemorySize (first field), which is enough for total VRAM.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct ADLMemoryInfo2
    {
        public long iMemorySize;              // bytes
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)] public string strMemoryType;
        public long iMemoryBandwidth;
        public long iHyperMemorySize;
        public long iInvisibleMemorySize;
        public long iVisibleMemorySize;
    }

    private readonly IntPtr _context;
    private readonly int _adapterIndex;
    private readonly int _device;
    private readonly IntPtr _loggingAddress;
    private readonly float _totalVramMb;

    public string? DeviceName { get; }

    private AmdAdlReader(IntPtr context, int adapterIndex, int device, IntPtr loggingAddress, float totalVramMb, string? deviceName)
    {
        _context = context;
        _adapterIndex = adapterIndex;
        _device = device;
        _loggingAddress = loggingAddress;
        _totalVramMb = totalVramMb;
        DeviceName = deviceName;
    }

    public static AmdAdlReader? TryCreate()
    {
        IntPtr context = IntPtr.Zero;
        try
        {
            if (ADL2_Main_Control_Create(MainMemoryAlloc, 1, out context) != 0 || context == IntPtr.Zero)
                return null;

            int numAdapters = 0;
            if (ADL2_Adapter_NumberOfAdapters_Get(context, ref numAdapters) != 0 || numAdapters == 0)
            {
                ADL2_Main_Control_Destroy(context);
                return null;
            }

            var adapters = ReadAdapters(context, numAdapters);

            // One GPU exposes several adapter entries (one per display path), and a Ryzen desktop ALSO
            // exposes its basic iGPU ("AMD Radeon(TM) Graphics", ~512 MB) which now answers PMLog too —
            // so among all PMLog-capable AMD adapters pick the one with the MOST VRAM (the real discrete
            // card), not the first that answers, or we'd stream the idle iGPU's zeros. Vendor id is ±1002.
            int chosen = -1;
            int device = 0;
            string? chosenName = null;
            float bestVram = -1f;
            var support = new ADLPMLogSupportInfo { usSensors = new ushort[PmLogMaxSensors], ulReserved = new int[16] };
            foreach (var a in adapters)
            {
                if (Math.Abs(a.VendorID) != AmdVendorId) continue;

                int dev = 0;
                int dc = ADL2_Device_PMLog_Device_Create(context, a.AdapterIndex, ref dev);
                var sup = new ADLPMLogSupportInfo { usSensors = new ushort[PmLogMaxSensors], ulReserved = new int[16] };
                int sg = ADL2_Adapter_PMLog_Support_Get(context, a.AdapterIndex, ref sup);
                float vram = ADL2_Adapter_MemoryInfo2_Get(context, a.AdapterIndex, out ADLMemoryInfo2 m) == 0
                    ? m.iMemorySize / (1024f * 1024f)
                    : 0;
                if (dc == 0 && sg == 0 && sup.usSensors[0] != SensorMaxTypes && vram > bestVram)
                {
                    if (device != 0) ADL2_Device_PMLog_Device_Destroy(context, device); // drop the previous pick
                    chosen = a.AdapterIndex;
                    device = dev;
                    support = sup;
                    chosenName = a.AdapterName;
                    bestVram = vram;
                }
                else if (dev != 0)
                {
                    ADL2_Device_PMLog_Device_Destroy(context, dev);
                }
            }

            if (chosen < 0)
            {
                ADL2_Main_Control_Destroy(context);
                return null;
            }

            var input = new ADLPMLogStartInput { usSensors = new ushort[PmLogMaxSensors], ulReserved = new int[15], ulSampleRate = 1000 };
            int n = 0;
            while (n < PmLogMaxSensors - 1 && support.usSensors[n] != SensorMaxTypes)
            {
                input.usSensors[n] = support.usSensors[n];
                n++;
            }
            input.usSensors[n] = SensorMaxTypes;

            var output = new ADLPMLogStartOutput { ulReserved = new int[14] };
            int st = ADL2_Adapter_PMLog_Start(context, chosen, ref input, ref output, device);
            if (st != 0 || output.pLoggingAddress == IntPtr.Zero)
            {
                ADL2_Device_PMLog_Device_Destroy(context, device);
                ADL2_Main_Control_Destroy(context);
                return null;
            }

            return new AmdAdlReader(context, chosen, device, output.pLoggingAddress, bestVram, chosenName);
        }
        catch (DllNotFoundException)
        {
            return null; // no AMD driver — expected
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AdlPM] TryCreate ex: {ex.GetType().Name}: {ex.Message}");
            if (context != IntPtr.Zero) ADL2_Main_Control_Destroy(context);
            return null;
        }
    }

    public GpuSnapshot? Read()
    {
        try
        {
            var data = Marshal.PtrToStructure<ADLPMLogData>(_loggingAddress);
            float vramUsed = 0;
            if (ADL2_Adapter_DedicatedVRAMUsage_Get(_context, _adapterIndex, out int usedMb) == 0)
                vramUsed = usedMb;

            return new GpuSnapshot(
                Temp: SensorValue(data, SensorTempEdge),
                // ADL reports one overall activity figure, not per-engine — report it as both.
                LoadD3d: SensorValue(data, SensorActivityGfx),
                LoadTotal: SensorValue(data, SensorActivityGfx),
                Clock: SensorValue(data, SensorGfxClock),
                VramUsedMb: vramUsed,
                VramTotalMb: _totalVramMb
            );
        }
        catch
        {
            return null;
        }
    }

    private static ADLAdapterInfo[] ReadAdapters(IntPtr context, int numAdapters)
    {
        int elementSize = Marshal.SizeOf<ADLAdapterInfo>();
        IntPtr buffer = Marshal.AllocHGlobal(elementSize * numAdapters);
        var adapters = new ADLAdapterInfo[numAdapters];
        try
        {
            if (ADL2_Adapter_AdapterInfo_Get(context, buffer, elementSize * numAdapters) == 0)
                for (int i = 0; i < numAdapters; i++)
                    adapters[i] = Marshal.PtrToStructure<ADLAdapterInfo>(buffer + i * elementSize);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return adapters;
    }

    // ulValues is a flat list of (sensorId, value) pairs, terminated by sensorId 0.
    private static float SensorValue(ADLPMLogData data, int sensorId)
    {
        if (data.ulValues is null) return 0;
        for (int k = 0; k + 1 < data.ulValues.Length; k += 2)
        {
            if (data.ulValues[k] == SensorMaxTypes) break;
            if (data.ulValues[k] == sensorId) return data.ulValues[k + 1];
        }
        return 0;
    }

    public void Dispose()
    {
        if (_context == IntPtr.Zero) return;
        try
        {
            ADL2_Adapter_PMLog_Stop(_context, _adapterIndex, _device);
            ADL2_Device_PMLog_Device_Destroy(_context, _device);
        }
        catch { /* best effort */ }
        ADL2_Main_Control_Destroy(_context);
    }
}
