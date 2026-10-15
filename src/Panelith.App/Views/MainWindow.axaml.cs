using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Panelith.App.Services;

namespace Panelith.App.Views;

public sealed partial class MainWindow : Window
{
    /// <summary>How much of the window must overlap a screen for a saved position to be reused.</summary>
    private const int MinVisiblePixels = 120;

    private PixelPoint? _normalPosition;
    private Size? _normalSize;
    private bool _maximized;
    private bool _shown;

    public MainWindow()
    {
        InitializeComponent();
        PositionChanged += (_, _) => RecordNormalBounds();
    }

    /// <summary>Replaces the start-up spinner with an explanation.</summary>
    public void ShowStartupError(string message)
    {
        LoadingBar.IsVisible = false;
        LoadingText.Text = $"Panelith could not start: {message}";
    }

    /// <summary>
    /// Opens where the window was last left. A position on a monitor that is
    /// no longer attached is dropped and the window is centred instead.
    /// </summary>
    public void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is null)
        {
            return;
        }

        var width = Math.Max(placement.Width, MinWidth);
        var height = Math.Max(placement.Height, MinHeight);
        Width = width;
        Height = height;
        _normalSize = new Size(width, height);
        _maximized = placement.Maximized;

        var position = new PixelPoint(placement.X, placement.Y);
        if (IsOnAScreen(position, width, height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = position;
            _normalPosition = position;
        }

        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>The window's restored size and position, or null when it has never been shown or restored.</summary>
    public WindowPlacement? CapturePlacement() =>
        (_normalPosition ?? (_shown ? Position : (PixelPoint?)null)) is { } position && (_normalSize ?? (_shown ? ClientSize : (Size?)null)) is { } size
            ? new WindowPlacement(position.X, position.Y, size.Width, size.Height, _maximized)
            : null;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _shown = true;
        RecordNormalBounds();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty)
        {
            RecordNormalBounds();
        }
        else if (change.Property == WindowStateProperty && change.GetNewValue<WindowState>() is var state and (WindowState.Normal or WindowState.Maximized))
        {
            _maximized = state == WindowState.Maximized;
        }
    }

    /// <summary>
    /// Remembers the size and position the window has when it is neither
    /// maximized nor minimized. Windows reports the new size before the new
    /// state, so this waits a moment to see which state the window ended in.
    /// </summary>
    private void RecordNormalBounds() => Dispatcher.UIThread.Post(
        () =>
        {
            if (IsVisible && WindowState == WindowState.Normal)
            {
                _normalPosition = Position;
                _normalSize = ClientSize;
            }
        },
        DispatcherPriority.Background);

    private bool IsOnAScreen(PixelPoint position, double width, double height)
    {
        foreach (var screen in Screens.All)
        {
            var window = new PixelRect(position, PixelSize.FromSize(new Size(width, height), screen.Scaling));
            var visible = screen.WorkingArea.Intersect(window);
            if (visible.Width >= MinVisiblePixels && visible.Height >= MinVisiblePixels / 2)
            {
                return true;
            }
        }

        return false;
    }
}
