using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using Panelith.App.Services;
using Panelith.App.ViewModels;

namespace Panelith.App.Views;

public sealed partial class ScreensView : UserControl
{
    private bool _dialogOpen;

    public ScreensView() => InitializeComponent();

    private void OpenFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScreensViewModel screens)
        {
            SettingsViewModel.OpenFolder(screens.UserScreensFolder);
        }
    }

    private void OpenVideosFolder(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(MediaFolders.Root);
        SettingsViewModel.OpenFolder(MediaFolders.Root);
    }

    private void RemoveWallpaper(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScreensViewModel screens && (sender as Control)?.Tag is ScreenCardViewModel card)
        {
            screens.RemoveWallpaperCommand.Execute(card);
        }
    }

    private async void NewVideoScreen(object? sender, RoutedEventArgs e) => await RunAsync(async screens =>
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var file = await PickAsync("Choose a video", new FilePickerFileType("Videos") { Patterns = MediaImportViewModel.VideoPatterns });
        if (file is not null && TopLevel.GetTopLevel(this) is Window owner
            && await MediaImportWindow.ShowAsync(owner, new MediaImportViewModel(screens.Service, file, wallpaperFor: null)) is { } id)
        {
            screens.ShowNew(id);
        }
    });

    private async void NewYouTubeScreen(object? sender, RoutedEventArgs e) => await RunAsync(async screens =>
    {
        if (!OperatingSystem.IsWindows() || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var folder = MediaFolders.NewWorkFolder("download");
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    });

    private async void SetWallpaper(object? sender, RoutedEventArgs e)
    {
        var card = (sender as Control)?.Tag as ScreenCardViewModel;
        await RunAsync(async screens =>
        {
            if (card is null || !OperatingSystem.IsWindows())
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
        });
    }

    private async void DeleteScreen(object? sender, RoutedEventArgs e)
    {
        var card = (sender as Control)?.Tag as ScreenCardViewModel;
        await RunAsync(async screens =>
        {
            if (card is null)
            {
                return;
            }

            var owner = TopLevel.GetTopLevel(this) as Window;
            if (await ConfirmPrompt.AskAsync(owner, $"Delete {card.Name}?", "The screen and its pictures and videos are removed from your screens folder. This cannot be undone.", "Delete")
                && !await screens.DeleteAsync(card))
            {
                await ConfirmPrompt.AskAsync(owner, $"{card.Name} could not be deleted", "One of its files is still in use. Try again in a moment.", "OK");
            }
        });
    }

    /// <summary>
    /// Runs one of the buttons that opens a window. Only one runs at a time, so
    /// a double click cannot start two imports, and a failure is written to the
    /// log instead of reaching the app's last-resort error handler.
    /// </summary>
    private async Task RunAsync(Func<ScreensViewModel, Task> action)
    {
        if (_dialogOpen || DataContext is not ScreensViewModel screens)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            await action(screens);
        }
        catch (Exception ex)
        {
            screens.Service.Log.CreateLogger("Panelith.App.Screens").LogError(ex, "That did not work: {Error}", ex.Message);
        }
        finally
        {
            _dialogOpen = false;
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
