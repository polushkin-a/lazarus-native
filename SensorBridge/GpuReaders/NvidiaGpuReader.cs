using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native.GPU;

namespace SensorBridge.GpuReaders;

/// <summary>
/// Reads GPU sensors via NVIDIA's NVAPI (nvapi64.dll, ships with the NVIDIA driver).
/// Official vendor API, safe alongside other monitoring tools reading the same GPU.
/// </summary>
public sealed class NvidiaGpuReader : IGpuReader
{
    private readonly PhysicalGPU _gpu;

    public string? DeviceName => _gpu.FullName;

    private NvidiaGpuReader(PhysicalGPU gpu)
    {
        _gpu = gpu;
    }

    public static NvidiaGpuReader? TryCreate()
    {
        try
        {
            NVIDIA.Initialize();
            PhysicalGPU[] gpus = PhysicalGPU.GetPhysicalGPUs();
            if (gpus.Length == 0)
            {
                NVIDIA.Unload();
                return null;
            }
            return new NvidiaGpuReader(gpus[0]);
        }
        catch (DllNotFoundException)
        {
            // No NVIDIA driver/GPU on this machine — expected, not an error.
            return null;
        }
        catch
        {
            return null;
        }
    }

    public GpuSnapshot? Read()
    {
        try
        {
            float temp = _gpu.ThermalInformation.ThermalSensors
                .FirstOrDefault(s => s.Target == ThermalSettingsTarget.GPU)?.CurrentTemperature ?? 0;

            float load = _gpu.UsageInformation.GPU?.Percentage ?? 0;
            float clock = _gpu.CurrentClockFrequencies.GraphicsClock.Frequency / 1000f;

            uint totalKb = _gpu.MemoryInformation.DedicatedVideoMemoryInkB;
            uint availableKb = _gpu.MemoryInformation.CurrentAvailableDedicatedVideoMemoryInkB;
            float usedMb = (totalKb - availableKb) / 1024f;
            float totalMb = totalKb / 1024f;

            // NVAPI's GPU domain usage is one overall figure, not per-engine — report it as both.
            return new GpuSnapshot(temp, LoadD3d: load, LoadTotal: load, Clock: clock,
                VramUsedMb: usedMb, VramTotalMb: totalMb);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        NVIDIA.Unload();
    }
}
