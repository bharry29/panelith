# Protocols

Panelith's device code in [`src/Panelith.Devices`](../src/Panelith.Devices) is a clean-room implementation. It was written from published *descriptions* of each protocol (byte layouts, command codes, handshakes and pixel encodings) collected by the hardware reverse-engineering community. No source code was copied from those projects, several of which are GPL-licensed. Facts about which byte means what are not copyrightable; their expression here is original. The [NOTICE](../NOTICE) file credits the projects whose documentation informed this work.

## Thermalright

Thermalright's TRCC software uses several wire families, chosen by the panel's controller:

| Family | Transport | Used by |
| --- | --- | --- |
| **LY bulk** | USB bulk endpoints | Trofeo Vision 9.16" and 11.3". A handshake returns the panel's identity and size; frames are JPEG, sent in chunks. The LY1 firmware revision uses a different command byte and unpadded chunks. |
| **HID** | USB HID reports | Trofeo Vision 6.86" (JPEG) and Frozen Magic and SCENIC cooler screens (raw RGB565, some column-major with a terminator word per column). |
| **Raw bulk** | USB bulk | Grand Vision and Core Matrix. Needs a two-second settle after opening before the first frame. |
| **SCSI** | USB mass storage, vendor SCSI command `0xF5` | Frozen Warframe. On Linux the `usb-storage` driver is detached first. |

The panel's model and sub-model, returned in the handshake, select the screen size from the same tables TRCC uses.

## Turing Smart Screen and relatives

Turing, XuanFang, Kipye and UsbMonitor panels connect through a USB serial chip. Revisions A to D differ in command sets, padding and brightness handling; Panelith picks the revision from the USB serial number and the panel's first reply. Full-screen updates over serial are slow (around 1 frame per second on the 3.5"), so Panelith sends only the parts of the picture that changed.

## Common behaviour

All drivers share the same engine: frames are rendered once per tick, frames identical to the last one are skipped, the frame rate is capped at the panel's limit, and a supervisor reopens a panel after it is unplugged, after sleep, or after a protocol error, backing off between attempts.

The virtual panel (`panelith.virtual`) exercises the same render and send loop with no hardware, which is how the test suite covers it.
