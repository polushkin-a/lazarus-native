namespace SensorBridge.GpuReaders;

/// <param name="LoadD3d">3D-engine utilization — matches what vendor tools label "D3D Usage".</param>
/// <param name="LoadTotal">All engines summed (3D + copy/compute/video), approximating the
/// vendors' overall "GPU Utilization", which WDDM doesn't publish as one number.</param>
/// <param name="Clock">Instantaneous core clock at poll time, not an average.</param>
/// <param name="MaxClockMhz">Maximum core clock the engine can reach.</param>
/// <param name="PowerW">0 when the driver doesn't report it (AMD leaves D3DKMT's field empty).</param>
public record GpuSnapshot(
    float Temp,
    float LoadD3d,
    float LoadTotal,
    float Clock,
    float VramUsedMb,
    float VramTotalMb,
    float FanRpm = 0,
    float PowerW = 0,
    float MemoryClockMhz = 0,
    float MaxClockMhz = 0);

public interface IGpuReader : IDisposable
{
    GpuSnapshot? Read();

    /// <summary>The card's own name ("AMD Radeon RX 9070 XT"), or null if the source doesn't say.</summary>
    string? DeviceName { get; }
}
