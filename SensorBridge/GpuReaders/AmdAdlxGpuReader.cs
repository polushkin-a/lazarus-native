// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of the MPL was
// not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
using ADLXWrapper;

namespace SensorBridge.GpuReaders;

/// <summary>
/// Reads GPU sensors via AMD's ADLX SDK (amdadlx64.dll, ships with the AMD driver).
/// Unlike LibreHardwareMonitor's raw register access, ADLX is the vendor-sanctioned,
/// multi-client-safe API — it doesn't compete with other monitoring tools for exclusive
/// driver access, and it supports current-gen (RDNA3/4) cards that LibreHardwareMonitor's
/// reverse-engineered register maps haven't caught up to yet.
/// </summary>
public sealed class AmdAdlxGpuReader : IGpuReader
{
    private readonly AmdAdlx.IADLXPerformanceMonitoringServices _perf;
    private readonly AmdAdlx.IADLXGPU _gpu;
    private readonly uint _totalVramMb;
    private readonly IntPtr _adlContext;

    public string? DeviceName => _gpu.Name();

    private AmdAdlxGpuReader(AmdAdlx.IADLXPerformanceMonitoringServices perf, AmdAdlx.IADLXGPU gpu, uint totalVramMb, IntPtr adlContext)
    {
        _perf = perf;
        _gpu = gpu;
        _totalVramMb = totalVramMb;
        _adlContext = adlContext;
    }

    public static AmdAdlxGpuReader? TryCreate()
    {
        IntPtr adlContext = IntPtr.Zero;
        try
        {
            // ADLX's own GetGPUList() comes back empty unless bootstrapped from a legacy
            // ADL adapter context — plain AmdAdlx.Initialize() alone doesn't enumerate GPUs.
            adlContext = AdlContext.Create();
            var initStatus = adlContext != IntPtr.Zero
                ? AmdAdlx.InitializeFromAdl(adlContext)
                : AmdAdlx.Initialize();
            if (initStatus != AmdAdlx.ADLXResult.ADLX_OK)
            {
                AdlContext.Destroy(adlContext);
                return null;
            }

            AmdAdlx.IADLXSystem system = AmdAdlx.GetSystemServices();
            List<AmdAdlx.GPU> gpuList = system.GetGPUList();

            // Prefer the discrete GPU; fall back to the first entry if none is flagged discrete
            // (multi-GPU AMD laptops/desktops otherwise report the iGPU alongside it).
            AmdAdlx.GPU? target = gpuList.FirstOrDefault(g => g.Type == AmdAdlx.GPUType.GPUTYPE_DISCRETE)
                ?? gpuList.FirstOrDefault();
            if (target is null)
            {
                AmdAdlx.Terminate();
                AdlContext.Destroy(adlContext);
                return null;
            }

            AmdAdlx.IADLXGPU adlxGpu = system.GetADLXGPUByUniqueId(target.UniqueId);
            if (adlxGpu is null)
            {
                AmdAdlx.Terminate();
                AdlContext.Destroy(adlContext);
                return null;
            }

            uint totalVramMb = adlxGpu.TotalVRAM();

            AmdAdlx.IADLXPerformanceMonitoringServices perf = system.GetPerformanceMonitoringServices();
            perf.StartPerformanceMetricsTracking();

            return new AmdAdlxGpuReader(perf, adlxGpu, totalVramMb, adlContext);
        }
        catch (DllNotFoundException)
        {
            // No AMD driver/GPU on this machine — expected, not an error.
            AdlContext.Destroy(adlContext);
            return null;
        }
        catch
        {
            AdlContext.Destroy(adlContext);
            return null;
        }
    }

    public GpuSnapshot? Read()
    {
        try
        {
            using AmdAdlx.IADLXGPUMetrics? metrics = _perf.GetCurrentGPUMetrics(_gpu);
            if (metrics is null) return null;

            return new GpuSnapshot(
                Temp: (float)metrics.GPUTemperature(),
                // ADLX reports one overall usage figure, not per-engine — report it as both.
                LoadD3d: (float)metrics.GPUUsage(),
                LoadTotal: (float)metrics.GPUUsage(),
                Clock: metrics.GPUClockSpeed(),
                VramUsedMb: metrics.GPUVRAM(),
                VramTotalMb: _totalVramMb
            );
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            _perf.StopPerformanceMetricsTracking();
        }
        catch { /* best effort */ }

        _gpu.Dispose();
        _perf.Dispose();
        AmdAdlx.Terminate();
        AdlContext.Destroy(_adlContext);
    }
}
