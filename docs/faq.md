# FAQ

**Is Panelith free?**
Yes, for any noncommercial use: at home, for hobby projects, research and teaching, and by charities and public institutions. Commercial use (selling it, bundling it with hardware, or using it in a business) needs a license; contact bharry29@gmail.com. See [LICENSE](../LICENSE).

**Is it made by Thermalright?**
No. Panelith is an independent project and is not affiliated with or endorsed by Thermalright, Turing or any other panel vendor.

**Can I run it alongside TRCC?**
Not at the same time. Only one program can use the panel; quit TRCC first.

**Does it slow down games?**
It is designed not to. It only sends frames that changed, caps the frame rate at the panel's limit, prepares videos ahead of time, and draws nothing in its own window while the window is hidden. A [profile](profiles.md) can also lower the frame rate while gaming.

**Does it work on Linux or macOS?**
The desktop app is for Windows. The `panelith` command line runs on Linux too, with hwmon sensors; see [Command line](cli.md).

**Why does it need administrator rights?**
It does not, for most things. Reading CPU temperature, clock and power on most CPUs needs a driver (PawnIO) that only answers programs running as administrator. Everything else works as a standard user.

**Will updates install themselves without asking?**
No. Panelith checks for updates and tells you; you choose when to install. See [Updates](updates.md).

**Why does Windows say the publisher is unknown?**
Panelith is signed, but with its own certificate rather than one bought from a certificate authority, which SmartScreen does not recognise yet. See [Installing](installing.md#windows-says-the-publisher-is-unknown).

**Where are my screens and settings?**
In `%APPDATA%\Panelith`. See [Configuration files](configuration.md).

**Can I share a screen I made?**
Yes: copy its folder from `%APPDATA%\Panelith\themes`. The other person puts it in theirs. Screens use portable sensor names, so they work on other PCs.

**My panel is not supported. Can it be added?**
Possibly. See [Contributing](contributing.md#adding-a-panel) for what helps.
