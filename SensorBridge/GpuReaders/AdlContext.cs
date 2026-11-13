// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of the MPL was
// not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Runtime.InteropServices;

namespace SensorBridge.GpuReaders;

/// <summary>
/// Minimal P/Invoke binding for a few functions from AMD's legacy ADL API (atiadlxx.dll,
/// ships with the AMD driver — public, documented API, unrelated to the vendored ADLX
/// wrapper). ADLX's GetGPUList() returns an empty list unless initialized from an ADL
/// context that has already enumerated adapters via AmdAdlx.InitializeFromAdl — this
/// creates that context, populates its adapter list, and releases it on shutdown.
/// </summary>
internal static class AdlContext
{
    private const int AdlMaxPath = 256;

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

    [StructLayout(LayoutKind.Sequential)]
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

    public static IntPtr Create()
    {
        if (ADL2_Main_Control_Create(MainMemoryAlloc, 1, out IntPtr context) != 0 || context == IntPtr.Zero)
            return IntPtr.Zero;

        // ADLX's ADL-bridge init only sees adapters that ADL itself has already enumerated —
        // querying AdapterInfo populates ADL's internal list as a side effect.
        int numAdapters = 0;
        if (ADL2_Adapter_NumberOfAdapters_Get(context, ref numAdapters) == 0 && numAdapters > 0)
        {
            int elementSize = Marshal.SizeOf<ADLAdapterInfo>();
            IntPtr buffer = Marshal.AllocHGlobal(elementSize * numAdapters);
            try
            {
                ADL2_Adapter_AdapterInfo_Get(context, buffer, elementSize * numAdapters);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return context;
    }

    public static void Destroy(IntPtr context)
    {
        if (context != IntPtr.Zero)
            ADL2_Main_Control_Destroy(context);
    }
}
