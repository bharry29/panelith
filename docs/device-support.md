# Device support

Every panel model Panelith knows is described by a data file in [`src/Panelith.Devices/Profiles/Builtin`](../src/Panelith.Devices/Profiles/Builtin): its USB id, size, pixel format, protocol and quirks. Adding a size of an existing family needs no new code. `panelith profiles` prints the current list.

## How well each panel is supported

- **Verified**: tested end to end on the hardware.
- **Community**: reported working by users.
- **Untested**: the driver follows the published protocol and passes Panelith's protocol tests, but has not been confirmed on the hardware. Reports, good or bad, are very welcome.
- **Recognised only**: Panelith detects the panel and explains why it cannot drive it yet, instead of ignoring it.

| Panel | USB id | Size | Protocol | Status |
| --- | --- | --- | --- | --- |
| Thermalright Trofeo Vision 9.16" / 11.3" (LY) | `0416:5408` | 1920×480 / 1920×400 | Thermalright LY bulk, JPEG | **Verified** on the 9.16" |
| Thermalright Trofeo Vision 9.16" (LY1 firmware) | `0416:5409` | 1920×480 | Thermalright LY bulk, JPEG | Untested |
| Thermalright Trofeo Vision 6.86" | `0416:5302` | 1280×480 | Thermalright HID, JPEG | Community |
| Thermalright Frozen Magic / Frozen Vision (HID) | `0416:5302` | 320×240 | Thermalright HID | Untested |
| Thermalright Frozen Magic SCENIC / AIO 480 | `0418:5303`, `0418:5304` | 320×320 | Thermalright HID | Untested |
| Thermalright Frozen Warframe / Frozen Magic (mass storage) | `0416:5406`, `0402:3922` | 480×480 | Thermalright SCSI | Untested |
| Thermalright Grand Vision / Core Matrix | `87AD:70DB`, `87CD:70DB` | 480×480 | Thermalright raw bulk, JPEG | Untested |
| Turing Smart Screen 3.5" (rev A) | `1A86:5722` | 320×480 | Turing serial | Untested |
| Turing 5" (rev C) | `1D6B:0106` | 480×800 | Turing serial | Untested |
| XuanFang 3.5" (rev B) | `1A86:5722` | 320×480 | Turing serial | Untested |
| Kipye Qiye Smart Display 3.5" (rev D) | `454D:4E41` | 320×480 | Turing serial | Untested |
| UsbMonitor 3.5" / 5" / 7" | `1A86:5722` | various | Turing serial | Untested |
| Turing USB models (2.8" round, 4.6" to 12.3") | `1CBE:*` | various | Turing USB bulk | Recognised only |
| BeadaPanel 2 to 7 | `4E58:1001` | various | BeadaPanel | Recognised only |
| WeAct Studio Display FS V1 3.5" | `1A86:FE0C` | 320×480 | WeAct | Recognised only |

The 9.16" Trofeo Vision sometimes reports itself as 1920×462; the 11.3" is 1920×400. The panel reports its own size in the handshake, and screens adapt to it.

On Windows, the Trofeo's 6.86" HID panel needs no driver, and the 9.16" and 11.3" panels announce themselves as WinUSB devices, so Windows binds its built-in driver the first time they are plugged in.

## One USB id, several products

Some ids are shared by different products. `0416:5302` is used both by the 6.86" Trofeo Vision and by Thermalright's Frozen Magic cooler screens. Panelith prefers the Trofeo for that id; if you have a cooler screen, set `forceProfile` for that panel in `panelith.json` (see [Configuration files](configuration.md#panelithjson)) or run `panelith run --profile thermalright.frozen-magic-hid`.

Turing serial panels share the CH340 id `1A86:5722` and are told apart by their USB serial number and their reply to the first command.

## Why some panels are "recognised only"

- **Turing USB models** use a newer, faster bulk protocol whose command packets are obfuscated with a vendor key.
- **BeadaPanel** publishes an SDK and uses separate USB channels for management and pixels. A full driver is the top item on the [roadmap](roadmap.md).
- **WeAct Display FS** uses its own command set with optional compression.

Protocol notes and USB captures help: see [Contributing](contributing.md#adding-a-panel).
