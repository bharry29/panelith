using Panelith.Devices.Profiles;
using Xunit;

namespace Panelith.Devices.Tests.Profiles;

public sealed class PanelProfileRegistryTests
{
    [Fact]
    public void EveryBuiltInProfileIsValid()
    {
        var registry = PanelProfileRegistry.Load();

        Assert.Empty(registry.Problems);
        Assert.NotEmpty(registry.Profiles);
    }

    [Fact]
    public void BuiltInProfileIdsAreUnique()
    {
        var registry = PanelProfileRegistry.Load();

        var ids = registry.Profiles.Select(p => p.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void FindsTheTrofeoVisionByItsUsbIds()
    {
        var registry = PanelProfileRegistry.Load();

        var matches = registry.FindByUsb(0x0416, 0x5408);

        var profile = Assert.Single(matches);
        Assert.Equal("thermalright.trofeo-vision", profile.Id);
        Assert.Equal(PanelProbeFamily.ThermalrightLy, profile.Probe);
        Assert.Equal(PanelPixelFormat.Jpeg, profile.PixelFormat);
    }

    [Fact]
    public void PrefersTheTrofeo686WhenItSharesAUsbIdWithTheFrozenMagicCooler()
    {
        var registry = PanelProfileRegistry.Load();

        var matches = registry.FindByUsb(0x0416, 0x5302);

        Assert.Equal("thermalright.trofeo-vision-6.86", matches[0].Id);
        Assert.Contains(matches, profile => profile.Id == "thermalright.frozen-magic-hid");
    }

    [Fact]
    public void PrefersTheSerialNumberMatchWhenOneUsbIdCoversSeveralPanels()
    {
        var registry = PanelProfileRegistry.Load();

        // The CH340 bridge at 1a86:5722 is shared by Turing revision A and the
        // XuanFang revision B panels; only the serial number tells them apart.
        var xuanfang = registry.FindByUsb(0x1A86, 0x5722, "2017-2-25");
        var turing = registry.FindByUsb(0x1A86, 0x5722, "USB35INCHIPSV2");
        var unknown = registry.FindByUsb(0x1A86, 0x5722, "something-else");

        Assert.Equal("xuanfang.3.5", xuanfang[0].Id);
        Assert.Equal("turing.smart-screen-3.5", turing[0].Id);
        Assert.Equal("turing.usbmonitor", unknown[0].Id);
    }

    [Fact]
    public void UserProfilesOverrideBuiltInsWithTheSameId()
    {
        var directory = Directory.CreateTempSubdirectory("panelith-profiles");
        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "override.json"),
                """
                {
                  "id": "thermalright.trofeo-vision",
                  "vendor": "Thermalright",
                  "model": "Trofeo Vision 9.16\"",
                  "driver": "thermalright.ly",
                  "usb": [{ "vendor": "0x0416", "product": "0x5408" }],
                  "width": 1920,
                  "height": 480,
                  "pixelFormat": "jpeg",
                  "transport": "usbBulk"
                }
                """);

            var registry = PanelProfileRegistry.Load(directory.FullName);
            var profile = registry.Find("thermalright.trofeo-vision");

            Assert.NotNull(profile);
            Assert.True(profile.IsUserDefined);
            Assert.Equal(1920, profile.Width);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AMalformedUserProfileIsReportedWithoutLosingTheOthers()
    {
        var directory = Directory.CreateTempSubdirectory("panelith-profiles");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "broken.json"), "{ not json");
            File.WriteAllText(
                Path.Combine(directory.FullName, "fine.json"),
                """
                {
                  "id": "test.panel",
                  "vendor": "Test",
                  "model": "Panel",
                  "driver": "generic",
                  "usb": [{ "vendor": "0x1234", "product": "0x5678" }],
                  "width": 320,
                  "height": 240
                }
                """);

            var registry = PanelProfileRegistry.Load(directory.FullName);

            Assert.Contains(registry.Problems, p => p.Contains("broken.json", StringComparison.Ordinal));
            Assert.NotNull(registry.Find("test.panel"));
            Assert.NotNull(registry.Find("thermalright.trofeo-vision"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ProfilesRoundTripThroughSerialization()
    {
        var registry = PanelProfileRegistry.Load();

        foreach (var original in registry.Profiles)
        {
            var json = PanelProfileRegistry.Serialize(original);
            var restored = Assert.Single(PanelProfileRegistry.Deserialize(json));

            Assert.Equal(json, PanelProfileRegistry.Serialize(restored));
        }
    }

    [Fact]
    public void ValidationRejectsAProfileNoDeviceCouldEverMatch()
    {
        var profile = new PanelProfile
        {
            Id = "bad.profile",
            Vendor = "Test",
            Model = "Panel",
            Width = 320,
            Height = 240,
        };

        Assert.Contains(profile.Validate(), p => p.Contains("no USB ids", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidationCatchesAFrameThatCannotFitTheDeclaredPayloadLimit()
    {
        var profile = new PanelProfile
        {
            Id = "bad.profile",
            Vendor = "Test",
            Model = "Panel",
            Width = 1920,
            Height = 480,
            PixelFormat = PanelPixelFormat.Rgb565BigEndian,
            MaxPayloadBytes = 1024,
            Usb = [new UsbMatch("0x1234", "0x5678")],
        };

        Assert.Contains(profile.Validate(), p => p.Contains("exceeds the declared", StringComparison.Ordinal));
    }

    [Fact]
    public void KnownUsbIdsCoversEveryMatchableProfile()
    {
        var registry = PanelProfileRegistry.Load();

        var ids = registry.KnownUsbIds;

        Assert.Contains((0x0416, 0x5408), ids);
        Assert.Contains((0x1A86, 0x5722), ids);
        Assert.Contains((0x454D, 0x4E41), ids);
    }
}
