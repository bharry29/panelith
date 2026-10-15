using Avalonia.Data.Converters;

namespace Panelith.App.ViewModels;

/// <summary>Small value converters used by the views.</summary>
public static class Converters
{
    /// <summary>Full opacity for true, faded for false.</summary>
    public static IValueConverter DimWhenFalse { get; } =
        new FuncValueConverter<bool, double>(value => value ? 1d : 0.4d);
}
