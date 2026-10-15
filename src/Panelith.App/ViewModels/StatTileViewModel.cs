using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Panelith.App.ViewModels;

/// <summary>One live statistic on the dashboard.</summary>
public sealed partial class StatTileViewModel(string title, Color color, double minimum = double.NaN, double maximum = double.NaN)
    : ObservableObject
{
    /// <summary>What the tile measures.</summary>
    public string Title { get; } = title;

    /// <summary>Accent colour of the value and chart.</summary>
    public Color Color { get; } = color;

    /// <summary>The value brush, derived from <see cref="Color"/>.</summary>
    public IBrush Brush { get; } = new SolidColorBrush(color);

    /// <summary>Fixed lower bound of the chart.</summary>
    public double Minimum { get; } = minimum;

    /// <summary>Fixed upper bound of the chart.</summary>
    public double Maximum { get; } = maximum;

    /// <summary>The headline value.</summary>
    [ObservableProperty]
    public partial string Value { get; set; } = "--";

    /// <summary>A secondary line under the value.</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>Recent values for the chart.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<double>? History { get; set; }
}
