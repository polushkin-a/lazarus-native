# lazarus-native

The two Windows helper programs of [Lazarus](https://github.com/polushkin-a/lazarus-releases) — a PC telemetry
dashboard and control deck driven from your phone. They are the parts of Lazarus that need more trust than an
ordinary app: one reads hardware sensors with administrator rights, the other briefly listens to the keyboard. So
their source is open, here, and the release binaries are code-signed — you can read exactly what you are trusting.

The rest of Lazarus (the Electron app, the phone app) is a separate, closed-source product; it starts these two
programs and talks to them as described below. Nothing else of Lazarus runs elevated or touches the keyboard.

## SensorBridge

Reads the PC's sensors twice a second and streams them to Lazarus.

- **Why administrator rights:** the CPU and motherboard sensors (temperatures, clocks, fans, voltages) are read through
  [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)'s kernel driver, and FPS /
  frametime of the game in front through Windows ETW events — both need elevation (the manifest asks for it, so Windows
  shows a UAC prompt).
- **GPU:** the vendors' own SDKs — NVAPI for NVIDIA, ADLX / ADL for AMD (they ship with the driver) — and Windows' WDDM
  performance counters as the fallback.
- **What it sends, and where:** one line of JSON per tick, `path → value` (e.g. `hardware.cpu.temperature.core_tctl_tdie`),
  over TCP to `127.0.0.1` on the port Lazarus passes with `--port`. Nothing leaves the machine.
- **The only other network traffic:** an ICMP ping to `1.1.1.1` (Cloudflare's resolver) for the "ping" tile.
- **Writes no files**, reads no documents, touches no registry. It exits by itself when Lazarus closes the connection:
  an elevated process can't be killed by the non-elevated app, so leaving is its own job.
- `SensorBridge.exe --report` prints LibreHardwareMonitor's full sensor report and exits.

## KeyHook

Records a keyboard shortcut for a Lazarus tile — including combinations Windows would otherwise act on itself
(Win+D, Ctrl+Shift+Esc).

- **When it runs:** only while the shortcut field in Lazarus's tile settings is focused. Lazarus starts it then and
  stops it when the field loses focus.
- **What it does:** installs a low-level keyboard hook (`WH_KEYBOARD_LL`) and writes each key event to its own standard
  output as `vkCode,DOWN|UP` — to Lazarus, which built the shortcut from it. It **blocks** every key while it runs, so the
  combination you press doesn't also fire in Windows.
- **Safety caps:** it exits the moment Lazarus goes away (its stdin closes), and after 2 minutes no matter what — the
  keyboard is never left captured. Windows removes the hook when the process exits.
- **No network, no files**, no administrator rights (it runs as you).

This is the kind of program antivirus software treats with suspicion — a keyboard hook looks like a keylogger. That
is why it is a separate, small (~130 lines), open, signed program: read [`KeyHook/Program.cs`](KeyHook/Program.cs).

## Build

Requires the .NET 8 SDK on Windows.

```powershell
dotnet publish SensorBridge/SensorBridge.csproj -c Release -r win-x64 --self-contained -o publish/SensorBridge
dotnet publish KeyHook/KeyHook.csproj -c Release -r win-x64 --self-contained -o publish/KeyHook
```

CI builds every push and pull request the same way (`.github/workflows/build.yml`) and keeps the binaries with their
SHA-256 sums as artifacts. How the release binaries are signed: [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).

## License

Copyright (c) 2026 Alexander Polushkin. [Mozilla Public License 2.0](LICENSE): use it, change it, ship it — also inside
closed software — but if you distribute a changed version, the changes to these files stay open under the same
license. Third-party libraries are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Improvements are welcome as pull requests.
