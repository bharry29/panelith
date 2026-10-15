# RigGlass

RigGlass draws a live sensor theme on a USB panel. It was built for the Thermalright Trofeo Vision and also speaks the protocols used by other small USB screens, so one theme is not locked to one brand.

The first screen it knows how to drive:

| Panel | USB id | How it talks |
| --- | --- | --- |
| Trofeo Vision 9.16" and 11.3" | `0416:5408` | USB bulk, JPEG. The panel reports its own size. |
| Trofeo Vision 9.16" (LY1 firmware) | `0416:5409` | USB bulk, JPEG |
| Trofeo Vision 6.86" | `0416:5302` | USB HID, JPEG, 1280×480 |

`0416:5302` is also used by some Thermalright cooler screens. RigGlass prefers the 6.86" panel for that id. If you actually have a cooler screen, pin it:

```bash
rigglass run --profile thermalright.frozen-magic-hid
```

RigGlass is not affiliated with Thermalright, Turing, or any other panel vendor. Close the vendor's own tool (Thermalright TRCC, for example) before RigGlass opens the panel. Those tools keep the device to themselves.

## Run it

You need the .NET 10 SDK.

```bash
dotnet run --project src/RigGlass.Cli -- devices
dotnet run --project src/RigGlass.Cli -- preview --trofeo
dotnet run --project src/RigGlass.Cli -- run
```

`devices` lists what is plugged in and the stable key to pin in `rigglass.json`. `preview` writes a PNG of the bundled `rig` theme, including 1280×480 and 1920×480 Trofeo sizes when you pass `--trofeo`. `run` draws that theme on every attached panel until you press Ctrl+C.

Other commands: `profiles`, `theme list`, `theme validate`, `doctor`.

On Linux the panel is usually invisible to a normal user until udev allows it:

```bash
sudo dotnet run --project src/RigGlass.Cli -- doctor --install-udev-rules
```

Unplug the panel and plug it back in, then run `devices` again. `doctor` also checks that libusb is installed (`sudo apt install libusb-1.0-0` on Debian and Ubuntu).

On Windows the 6.86" panel uses HID and usually needs nothing extra. The 9.16" and 11.3" panels need a WinUSB driver, which the Thermalright installer sets up. Close TRCC before `run`.

Rotation defaults to the panel's native orientation, which is already landscape on a Trofeo. Pass `--rotate 90`, `180`, or `270` if the panel is mounted another way.

## Configuration

Set `RIGGLASS_HOME` to move the whole configuration tree. Otherwise RigGlass uses `$XDG_CONFIG_HOME/rigglass` on Linux, `%APPDATA%\RigGlass` on Windows, and `~/Library/Application Support/RigGlass` on macOS.

`rigglass.json` can pin a theme, a frame rate, or a profile to the stable key printed by `devices`. Themes you add go in the `themes` folder, one directory per theme with a `theme.json` inside. The bundled theme is `themes/rig`. It is drawn at 1280×480 and anchors its side columns, so the same file fills a 1920×480 panel without a second copy.

## Build

```bash
dotnet test RigGlass.slnx
```

The solution is .NET 10. Tests do not need a panel. A virtual panel exercises the same render and send loop the hardware uses.

## What is implemented

- Thermalright LY bulk (Trofeo 9.16" / 11.3"), HID (Trofeo 6.86" and Frozen-series coolers), raw bulk, and SCSI.
- Turing Smart Screen serial revisions, matched by the USB serial number where one chip is sold as several products.
- A data file per panel model, so adding a size does not require a new driver.
- Themes with sensor aliases (`@cpu.temp`, `@gpu.load`, …), anchors, and fit/fill/stretch.
- Linux hwmon and /proc sensors, LibreHardwareMonitor and HWiNFO on Windows, and a synthetic source for previews.
- Reconnect after unplug or sleep, a frame-rate cap, and skipping frames that did not change.

Panels that can be recognised but not yet driven (some Turing USB models, BeadaPanel, WeAct) show up in `devices` with an explanation instead of disappearing.

The protocol code is a clean-room implementation. See `NOTICE`. The license is Apache-2.0.
