using Avalonia.Controls;
using Avalonia.Interactivity;
using Panelith.App.ViewModels;

namespace Panelith.App.Views;

public sealed partial class ScreensView : UserControl
{
    public ScreensView() => InitializeComponent();

    private void OpenFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScreensViewModel screens)
        {
            SettingsViewModel.OpenFolder(screens.UserScreensFolder);
        }
    }
}
