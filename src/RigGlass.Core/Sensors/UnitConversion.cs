using System.Globalization;

namespace RigGlass.Sensors;

/// <summary>
/// Converts canonical sensor values into the units a theme asks to display.
/// </summary>
public static class UnitConversion
{
    private static readonly string[] ByteScales = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
    private static readonly string[] RateScales = ["B/s", "KiB/s", "MiB/s", "GiB/s", "TiB/s"];

    /// <summary>
    /// Scales a byte count to the largest binary unit that keeps it above 1,
    /// returning the scaled value and its unit suffix.
    /// </summary>
    public static (double Value, string Suffix) ScaleBytes(double bytes)
    {
        var magnitude = Math.Abs(bytes);
        var index = 0;
        while (magnitude >= 1024d && index < ByteScales.Length - 1)
        {
            magnitude /= 1024d;
            bytes /= 1024d;
            index++;
        }

        return (bytes, ByteScales[index]);
    }

    /// <summary>
    /// Scales a byte-per-second rate to the largest binary unit that keeps it
    /// above 1, returning the scaled value and its unit suffix.
    /// </summary>
    public static (double Value, string Suffix) ScaleRate(double bytesPerSecond)
    {
        var magnitude = Math.Abs(bytesPerSecond);
        var index = 0;
        while (magnitude >= 1024d && index < RateScales.Length - 1)
        {
            magnitude /= 1024d;
            bytesPerSecond /= 1024d;
            index++;
        }

        return (bytesPerSecond, RateScales[index]);
    }

    /// <summary>
    /// Converts a megahertz clock to the largest unit that keeps it above 1,
    /// which in practice means switching to GHz above 1000 MHz.
    /// </summary>
    public static (double Value, string Suffix) ScaleClock(double megahertz) =>
        Math.Abs(megahertz) >= 1000d ? (megahertz / 1000d, "GHz") : (megahertz, "MHz");

    /// <summary>Degrees Celsius to degrees Fahrenheit.</summary>
    public static double CelsiusToFahrenheit(double celsius) => (celsius * 9d / 5d) + 32d;

    /// <summary>Degrees Celsius to kelvin.</summary>
    public static double CelsiusToKelvin(double celsius) => celsius + 273.15d;

    /// <summary>
    /// Formats a duration in seconds as <c>d:hh:mm:ss</c>, dropping leading
    /// components that are zero.
    /// </summary>
    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0d)
        {
            return "--";
        }

        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1d)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)span.TotalDays}d {span.Hours:D2}:{span.Minutes:D2}:{span.Seconds:D2}");
        }

        return span.TotalHours >= 1d
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes:D2}:{span.Seconds:D2}");
    }

    /// <summary>The short symbol conventionally printed after a value of this unit.</summary>
    public static string SymbolFor(SensorUnit unit) => unit switch
    {
        SensorUnit.Celsius => "\u00b0C",
        SensorUnit.Percent => "%",
        SensorUnit.Megahertz => "MHz",
        SensorUnit.Watts => "W",
        SensorUnit.Volts => "V",
        SensorUnit.Amperes => "A",
        SensorUnit.Rpm => "RPM",
        SensorUnit.Bytes => "B",
        SensorUnit.BytesPerSecond => "B/s",
        SensorUnit.Seconds => "s",
        SensorUnit.FramesPerSecond => "FPS",
        _ => string.Empty,
    };
}
