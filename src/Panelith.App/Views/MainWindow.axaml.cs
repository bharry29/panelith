using Avalonia.Controls;

namespace Panelith.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Replaces the start-up spinner with an explanation.</summary>
    public void ShowStartupError(string message)
    {
        LoadingBar.IsVisible = false;
        LoadingText.Text = $"Panelith could not start: {message}";
    }
}
