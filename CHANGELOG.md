# Changelog

Notable changes to Panelith. Versions follow [Semantic Versioning](https://semver.org/), and each section here becomes the notes of its GitHub release.

## [0.1.0] - 2026-10-15

The first public release.

### Panels

- Drives the Thermalright Trofeo Vision (6.86", 9.16" and 11.3"), Thermalright Frozen Magic, Frozen Warframe and Grand Vision cooler screens, and Turing Smart Screen, XuanFang, Kipye and UsbMonitor serial panels. See [Device support](docs/device-support.md) for how well each is tested.
- Recognises BeadaPanel, WeAct Display FS and the newer Turing USB panels and explains why they cannot be driven yet.
- Several panels at once, each with its own screen. Panels reconnect by themselves after unplugging, sleep or errors.
- No vendor software or extra USB driver needed on Windows.

### App

- **Dashboard** with a live preview of every panel and the readings that matter.
- **Screens**: 13 built-in screens that adapt to any panel size, plus wallpapers behind any screen.
- **Video screens** from a file or a YouTube link, trimmed and baked for the panel.
- **Designer** for building screens: text, gauges, bars, graphs, pictures, clips and shapes, with live values, layers and undo.
- **Profiles** that switch the screen, brightness and frame rate by game, app, time of day or idle time, with keyboard shortcuts.
- **Sensors** page listing every reading, from LibreHardwareMonitor, HWiNFO, RivaTuner and Windows, with demo sensors for trying Panelith without hardware access.
- Starts with Windows, lives in the tray, and reopens exactly as it was left.
- **In-app updates**: checks for new versions and installs them with one click. Updates are verified with an Ed25519 signature before they run.

### Command line

- `panelith` lists panels and sensors, drives panels, renders previews, validates screens and diagnoses problems, on Windows and Linux.

### Licensing

- Panelith is now licensed under the PolyForm Noncommercial License 1.0.0. Earlier, unreleased versions were Apache-2.0.

[0.1.0]: https://github.com/bharry29/panelith/releases/tag/v0.1.0
