# Third-party licenses

Panelith is distributed with the components below, each under its own license. Panelith's own license ([PolyForm Noncommercial 1.0.0](../LICENSE)) does not change their terms. [NOTICE](../NOTICE) has the attributions those licenses require.

## Bundled programs and native libraries

| Component | License | Use |
| --- | --- | --- |
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) | Unlicense | Downloads YouTube videos for video screens, run as a separate process |
| [QuickJS-ng](https://github.com/quickjs-ng/quickjs) | MIT | The JavaScript runtime yt-dlp needs for YouTube |
| [PawnIO setup](https://github.com/namazso/PawnIO) | GPL-2.0 | Hardware-access driver, installed only when the user chooses it |
| [libusb](https://github.com/libusb/libusb) 1.0.30 | LGPL-2.1, loaded dynamically and replaceable | USB access |
| [Inter](https://github.com/rsms/inter) | SIL Open Font License 1.1 | Default font |

## NuGet packages

| Package | License |
| --- | --- |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter and their platform packages | MIT |
| Avalonia.Angle.Windows.Natives (ANGLE) | BSD-3-Clause |
| CommunityToolkit.Mvvm | MIT |
| SkiaSharp and its native assets | MIT |
| HarfBuzzSharp and its native assets | MIT |
| HidSharp | Apache-2.0 |
| LibreHardwareMonitorLib | MPL-2.0 |
| BlackSharp.Core, DiskInfoToolkit, RAMSPDToolkit-NDD (used by LibreHardwareMonitor) | MPL-2.0 |
| NetSparkleUpdater.SparkleUpdater, NetSparkleUpdater.Chaos.NaCl | MIT |
| Vortice.MediaFoundation, Vortice.DirectX, Vortice.Mathematics | MIT |
| SharpGen.Runtime, SharpGen.Runtime.COM | MIT |
| MicroCom.Runtime, Tmds.DBus.Protocol | MIT |
| Microsoft.Extensions.* , System.CommandLine, System.IO.Ports, System.Management, System.CodeDom | MIT |
| Mono.Posix.NETStandard | MIT |

MPL-2.0 components are used unmodified; their source is available from the linked projects. The .NET runtime included in the self-contained build is MIT-licensed.

To regenerate this inventory after changing dependencies, read the `license` element of each package listed in `src/Panelith.App/obj/project.assets.json`.
