# Third-party software

SensorBridge uses the components below; each keeps its own license (full texts in [licenses/](licenses/)).
KeyHook uses only the .NET runtime.

| Component                                                         | License                                         | Source                                                       |
| ----------------------------------------------------------------- | ----------------------------------------------- | ------------------------------------------------------------ |
| LibreHardwareMonitorLib 0.9.6                                     | MPL-2.0 (`LICENSE`)                             | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| adlxwrapper (`SensorBridge/Vendor/AmdAdlx.cs`) — © Emerson Pinter | MPL-2.0 (`SensorBridge/Vendor/AmdAdlx.LICENSE.txt`) | https://github.com/epinter/adlxwrapper                       |
| NvAPIWrapper.Net 0.8.1.101 — © Soroush Falahati 2018              | LGPL-3.0 (`licenses/LGPL-3.0.txt`, `licenses/GPL-3.0.txt`) | https://github.com/falahati/NvAPIWrapper                     |
| Microsoft.Diagnostics.Tracing.TraceEvent 3.2.8                    | MIT                                             | https://github.com/microsoft/perfview                        |
| Microsoft.Extensions.Logging.Abstractions 10.0.15                 | MIT                                             | https://github.com/dotnet/runtime                            |
| System.Diagnostics.PerformanceCounter 10.0.15                     | MIT                                             | https://github.com/dotnet/runtime                            |
| .NET 10 runtime (self-contained)                                  | MIT                                             | https://github.com/dotnet/runtime                            |

LibreHardwareMonitorLib and NvAPIWrapper are used unmodified, as separate DLLs next to `SensorBridge.exe`; you may
replace them with your own builds of the same versions.
