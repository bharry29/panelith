using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Panelith.App.ViewModels;

namespace Panelith.App.Views;

public sealed partial class ProfilesView : UserControl
{
    public ProfilesView() => InitializeComponent();

    private ProfileEditorViewModel? Editor => (DataContext as ProfilesViewModel)?.Editor;

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e) => HotkeyCapture.OnKeyDown(sender, e);

    private void OnNewAppKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Editor?.AddApp(null);
            e.Handled = true;
        }
    }

    private void OnRunningAppsOpened(object? sender, EventArgs e) => Editor?.RefreshRunningApps();

    private async void OnBrowseApp(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || Editor is not { } editor)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick a program",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Programs") { Patterns = ["*.exe"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            editor.AddApp(path);
        }
    }
}
