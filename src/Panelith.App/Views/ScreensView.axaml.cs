using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    private async void NewVideoScreen(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScreensViewModel screens || !OperatingSystem.IsWindows())
        {
            return;
        }

        var file = await PickAsync("Choose a video", new FilePickerFileType("Videos") { Patterns = MediaImportViewModel.VideoPatterns });
        if (file is not null && TopLevel.GetTopLevel(this) is Window owner
            && await MediaImportWindow.ShowAsync(owner, new MediaImportViewModel(screens.Service, file, wallpaperFor: null)) is { } id)
        {
            screens.ShowNew(id);
        }
    }

    private async void NewYouTubeScreen(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScreensViewModel screens || !OperatingSystem.IsWindows() || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "Panelith", "download-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (await YouTubePrompt.AskAsync(owner, screens.Service.Runtime.Paths, folder) is { } download
                && await MediaImportWindow.ShowAsync(owner, new MediaImportViewModel(screens.Service, download.File, wallpaperFor: null) { Name = download.Title ?? "YouTube video" }) is { } id)
            {
                screens.ShowNew(id);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async void SetWallpaper(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScreensViewModel screens || (sender as Control)?.Tag is not ScreenCardViewModel card || !OperatingSystem.IsWindows())
        {
            return;
        }

        var file = await PickAsync(
            $"Choose a picture or video for {card.Name}",
            new FilePickerFileType("Pictures and videos") { Patterns = [.. MediaImportViewModel.PicturePatterns, .. MediaImportViewModel.VideoPatterns] },
            new FilePickerFileType("Pictures") { Patterns = MediaImportViewModel.PicturePatterns },
            new FilePickerFileType("Videos") { Patterns = MediaImportViewModel.VideoPatterns });
        if (file is not null && TopLevel.GetTopLevel(this) is Window owner
            && await MediaImportWindow.ShowAsync(owner, new MediaImportViewModel(screens.Service, file, card.Id)) is { } id)
        {
            screens.ShowNew(id);
        }
    }

    private async void DeleteScreen(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScreensViewModel screens && (sender as Control)?.Tag is ScreenCardViewModel card
            && await ConfirmPrompt.AskAsync(TopLevel.GetTopLevel(this) as Window, $"Delete {card.Name}?", "The screen and its pictures and videos are removed from your screens folder. This cannot be undone.", "Delete"))
        {
            screens.Delete(card);
        }
    }

    private async Task<string?> PickAsync(string title, params FilePickerFileType[] types)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = types });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
