// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of the MPL was
// not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace MediaBridge;

/// <summary>
/// The default output's volume and mute (Core Audio, IAudioEndpointVolume): sent whenever they change — from here or
/// anywhere else (the tray slider, a keyboard's volume keys) — and again when the default device changes.
/// </summary>
sealed class VolumeWatch(Action<JsonObject> emit) : IAudioEndpointVolumeCallback, IMMNotificationClient, IDisposable
{
    const int Render = 0, Multimedia = 1;
    static readonly Guid Ours = Guid.NewGuid(); // the event context of our own changes
    readonly object gate = new();
    IMMDeviceEnumerator? devices;
    IAudioEndpointVolume? endpoint;

    public void Start()
    {
        devices = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        Check(devices.RegisterEndpointNotificationCallback(this));
        Bind();
    }

    void Bind()
    {
        lock (gate)
        {
            if (endpoint is not null)
            {
                endpoint.UnregisterControlChangeNotify(this);
                endpoint = null;
            }
            if (devices is null) return;
            if (devices.GetDefaultAudioEndpoint(Render, Multimedia, out var device) != 0)
            {
                emit(new JsonObject { ["t"] = "error", ["message"] = "volume: no output device" });
                return;
            }
            var iid = typeof(IAudioEndpointVolume).GUID;
            Check(device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var o));
            endpoint = (IAudioEndpointVolume)o;
            Check(endpoint.RegisterControlChangeNotify(this));
        }
        Send();
    }

    void Send()
    {
        lock (gate)
        {
            if (endpoint is null) return;
            endpoint.GetMasterVolumeLevelScalar(out var level);
            endpoint.GetMute(out var muted);
            emit(new JsonObject { ["t"] = "volume", ["v"] = Math.Round(level * 100), ["muted"] = muted });
        }
    }

    public void Set(double percent)
    {
        lock (gate)
        {
            var ctx = Ours;
            endpoint?.SetMasterVolumeLevelScalar((float)Math.Clamp(percent / 100, 0, 1), ref ctx);
        }
    }

    public void ToggleMute()
    {
        lock (gate)
        {
            if (endpoint is null) return;
            endpoint.GetMute(out var muted);
            var ctx = Ours;
            endpoint.SetMute(!muted, ref ctx);
        }
    }

    // Callbacks come on Windows' threads: answer at once, do the work elsewhere.
    public int OnNotify(IntPtr data)
    {
        Task.Run(Send);
        return 0;
    }

    public int OnDefaultDeviceChanged(int flow, int role, string? id)
    {
        if (flow == Render && role == Multimedia) Task.Run(Bind);
        return 0;
    }

    public int OnDeviceStateChanged(string? id, int state) => 0;
    public int OnDeviceAdded(string? id) => 0;
    public int OnDeviceRemoved(string? id) => 0;
    public int OnPropertyValueChanged(string? id, PropertyKey key) => 0;

    public void Dispose()
    {
        lock (gate)
        {
            endpoint?.UnregisterControlChangeNotify(this);
            devices?.UnregisterEndpointNotificationCallback(this);
            endpoint = null;
            devices = null;
        }
    }

    static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
}

// Core Audio COM interfaces — only the vtable slots used, in their declared order (the rest of a vtable may be left
// out after the last one called).

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumerator;

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    [PreserveSig] int GetChannelCount(out uint n);
    [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
    [PreserveSig] int GetMasterVolumeLevel(out float db);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
    [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr data);
}

[StructLayout(LayoutKind.Sequential)]
struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string? id, int state);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string? id, PropertyKey key);
}
