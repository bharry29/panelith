# Command line

`panelith` is the command-line companion to the app. It shares the same configuration, screens and drivers, and is the way to drive a panel on Linux. It is built from source:

```bash
dotnet run --project src/Panelith.Cli -- <command>
```

or published once with `dotnet publish src/Panelith.Cli -c Release`.

## Commands

| Command | What it does |
| --- | --- |
| `devices` | Lists the panels plugged in right now, the profile each matched, and the stable key to pin it by in `panelith.json`. Panels that are recognised but cannot be driven yet are listed with the reason. |
| `run` | Draws a screen on every attached panel until Ctrl+C. |
| `preview` | Renders a screen to a PNG without a panel. |
| `profiles` | Lists every panel model Panelith knows, its protocol and how well it is tested. |
| `theme list` | Lists the screens Panelith can find. |
| `theme validate <theme>` | Checks a screen for errors and warnings. |
| `sensors [filter]` | Lists every sensor on this machine and the theme values they resolve to. |
| `doctor` | Checks that this machine can open a panel and prints the fix when it cannot. |

Every command accepts `-v` for discovery and sensor diagnostics.

### run

```bash
panelith run --theme aurora
```

| Option | Meaning |
| --- | --- |
| `-t`, `--theme` | Screen id or folder. Defaults to the one in `panelith.json`. |
| `--fps` | Frame rate cap. Defaults to the panel's own limit. |
| `--rotate` | `0`, `90`, `180` or `270` degrees clockwise. |
| `--profile` | Force a panel profile, such as `thermalright.frozen-magic-hid`. |
| `--demo` | Use simulated sensors. |
| `--virtual`, `-o`, `--width`, `--height` | Also drive the built-in virtual panel, writing PNG frames to a folder. |
| `--seconds` | Stop after this long. |

### preview

```bash
panelith preview --theme neon --width 1920 --height 480 --output neon.png
panelith preview --trofeo
```

`--trofeo` also writes 1280×480 and 1920×480 versions. Previews use simulated sensors.

## Linux

A normal user usually cannot open USB panels until udev allows it:

```bash
sudo panelith doctor --install-udev-rules
```

Then unplug and replug the panel. `doctor` also checks that libusb is installed (`sudo apt install libusb-1.0-0` on Debian and Ubuntu). Sensors come from `/sys/class/hwmon` and `/proc`.
