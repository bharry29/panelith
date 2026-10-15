using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using RigGlass.Devices.Profiles;

namespace RigGlass.Cli.Support;

/// <summary>Linux device permissions for the panels RigGlass knows about.</summary>
public static class UdevRules
{
    /// <summary>The file name installed under <c>/etc/udev/rules.d</c>.</summary>
    public const string FileName = "99-rigglass.rules";

    /// <summary>Where <c>--install-udev-rules</c> writes the rules.</summary>
    public const string InstallPath = "/etc/udev/rules.d/99-rigglass.rules";

    /// <summary>Builds a rules file from the loaded panel catalogue.</summary>
    public static string Build(PanelProfileRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var builder = new StringBuilder();
        builder.AppendLine("# RigGlass — allow the desktop user to open USB sensor panels.");
        builder.AppendLine("# Install with: sudo rigglass doctor --install-udev-rules");
        builder.AppendLine("# Then unplug the panel and plug it back in.");
        builder.AppendLine();

        var seen = new HashSet<(int Vendor, int Product)>();
        foreach (var profile in registry.Profiles)
        {
            foreach (var match in profile.Usb)
            {
                int vendor;
                int product;
                try
                {
                    vendor = match.VendorId;
                    product = match.ProductId;
                }
                catch (PanelProfileException)
                {
                    continue;
                }

                if (!seen.Add((vendor, product)))
                {
                    continue;
                }

                var vendorText = vendor.ToString("x4", CultureInfo.InvariantCulture);
                var productText = product.ToString("x4", CultureInfo.InvariantCulture);
                builder.AppendLine($"# {profile.FullName}");
                builder.AppendLine(
                    $"SUBSYSTEM==\"usb\", ATTR{{idVendor}}==\"{vendorText}\", ATTR{{idProduct}}==\"{productText}\", MODE=\"0660\", TAG+=\"uaccess\", GROUP=\"plugdev\"");
                builder.AppendLine(
                    $"SUBSYSTEM==\"hidraw\", ATTRS{{idVendor}}==\"{vendorText}\", ATTRS{{idProduct}}==\"{productText}\", MODE=\"0660\", TAG+=\"uaccess\", GROUP=\"plugdev\"");
                builder.AppendLine(
                    $"SUBSYSTEM==\"tty\", ATTRS{{idVendor}}==\"{vendorText}\", ATTRS{{idProduct}}==\"{productText}\", MODE=\"0660\", TAG+=\"uaccess\", GROUP=\"plugdev\"");
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    /// <summary>Whether libusb-1.0 can be loaded.</summary>
    public static bool TryLoadLibUsb(out string detail)
    {
        string[] names = OperatingSystem.IsWindows()
            ? ["libusb-1.0.dll", "libusb-1.0"]
            : OperatingSystem.IsMacOS()
                ? ["libusb-1.0.dylib", "libusb-1.0.0.dylib", "libusb-1.0"]
                : ["libusb-1.0.so.0", "libusb-1.0.so", "libusb-1.0"];

        foreach (var name in names)
        {
            if (NativeLibrary.TryLoad(name, out var handle))
            {
                NativeLibrary.Free(handle);
                detail = name;
                return true;
            }
        }

        detail = OperatingSystem.IsLinux()
            ? "libusb-1.0 was not found. Install it (Debian/Ubuntu: sudo apt install libusb-1.0-0) and try again."
            : "libusb-1.0 was not found next to RigGlass or on the system library path.";
        return false;
    }
}
