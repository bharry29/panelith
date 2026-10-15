using System.Globalization;
using Panelith.Expressions;
using Panelith.Monitoring.Hub;
using Panelith.Sensors;

namespace Panelith.App.ViewModels;

/// <summary>Reads well-known sensors by alias for the dashboard.</summary>
internal static class SensorAccess
{
    public static SensorReading? Read(SensorHub hub, string alias) =>
        hub.Aliases.TryGetValue(alias, out var id) && hub.Current.TryGet(id, out var reading) && reading.HasValue
            ? reading
            : null;

    public static double? Number(SensorHub hub, string alias) => Read(hub, alias)?.Value;

    public static string Format(SensorHub hub, string alias, string missing = "--") =>
        Read(hub, alias) is { } reading
            ? reading.Kind == SensorKind.Text ? reading.Text ?? missing : ValueFormatter.FormatWithUnit(reading.Value!.Value, reading.Kind)
            : missing;

    public static string Whole(double? value, string suffix) =>
        value is { } v ? v.ToString("0", CultureInfo.InvariantCulture) + suffix : "--";
}
