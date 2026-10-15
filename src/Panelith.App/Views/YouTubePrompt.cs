using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Panelith.App.Services;
using Panelith.Engine;

namespace Panelith.App.Views;

/// <summary>Asks for a YouTube link and downloads the video, after a one-time note about the user's responsibility.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class YouTubePrompt
{
    private const string Responsibility =
        "Panelith downloads the video with yt-dlp, an open-source tool included with it, so you can loop it on your panel. " +
        "Only use videos you have the right to copy: your own, ones licensed for reuse (such as Creative Commons), or ones whose owner allows it. " +
        "Downloading may be against YouTube's Terms of Service, and copyright law applies where you live. You are responsible for what you download. " +
        "The video stays on this PC and is never uploaded anywhere.";

    /// <summary>
    /// Shows the prompt. Returns the downloaded file, inside <paramref name="folder"/>,
    /// and the video's title, or null when the user cancelled.
    /// </summary>
    public static async Task<(string File, string? Title)?> AskAsync(Window owner, AppPaths paths, string folder)
    {
        var store = new AppSettingsStore(paths);
        var accepted = store.Load().YouTubeAccepted;
        (string File, string? Title)? result = null;
        CancellationTokenSource? download = null;

        var dialog = new Window
        {
            Title = "Video from YouTube",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var url = new TextBox { PlaceholderText = "https://www.youtube.com/watch?v=...", MaxLength = 2048 };
        var agree = new CheckBox { Content = "I understand, and I will only download videos I have the right to use.", IsVisible = !accepted };
        var note = new Border
        {
            Classes = { "banner" },
            IsVisible = !accepted,
            Child = new TextBlock { Text = Responsibility, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
        };
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, IsVisible = false };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.Gray };
        var update = new Button { Content = "Update downloader", IsVisible = false };
        var go = new Button { Content = "Download", Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel" };

        void Refresh() => go.IsEnabled = download is null && (accepted || agree.IsChecked == true) && YouTubeDownloader.ParseVideoUrl(url.Text) is not null;
        url.TextChanged += (_, _) => Refresh();
        agree.IsCheckedChanged += (_, _) => Refresh();
        Refresh();

        cancel.Click += (_, _) =>
        {
            if (download is { } running)
            {
                running.Cancel();
                return;
            }

            dialog.Close();
        };
        dialog.Closing += (_, e) =>
        {
            if (download is { } running)
            {
                running.Cancel();
                e.Cancel = true;
            }
        };

        update.Click += async (_, _) =>
        {
            update.IsEnabled = false;
            status.Text = "Updating the downloader...";
            try
            {
                status.Text = await YouTubeDownloader.UpdateAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }

            update.IsEnabled = true;
        };

        go.Click += async (_, _) =>
        {
            if (YouTubeDownloader.ParseVideoUrl(url.Text) is not { } video)
            {
                return;
            }

            if (!accepted)
            {
                accepted = true;
                try
                {
                    store.Save(store.Load() with { YouTubeAccepted = true });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Asked again next time; nothing else depends on it.
                }
            }

            var token = new CancellationTokenSource();
            download = token;
            Refresh();
            url.IsEnabled = false;
            note.IsVisible = agree.IsVisible = update.IsVisible = false;
            progress.IsVisible = true;
            progress.Value = 0;
            status.Text = "Downloading...";
            try
            {
                result = await YouTubeDownloader.DownloadAsync(
                    video,
                    folder,
                    new Progress<double>(p =>
                    {
                        progress.Value = p;
                        status.Text = $"Downloading... {p:P0}";
                    }),
                    token.Token);
                token.Dispose();
                download = null;
                dialog.Close();
                return;
            }
            catch (OperationCanceledException)
            {
                status.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                // A download has many ways to fail, and the dialog is the only
                // place that can say so; it must not become a crash.
                status.Text = ex.Message;
                update.IsVisible = !WindowsIntegration.IsElevated;
            }

            token.Dispose();
            download = null;
            progress.IsVisible = false;
            url.IsEnabled = true;
            Refresh();
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "Make a video screen from YouTube", FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Paste the link to a single video. Panelith downloads it, then you choose the part to loop.", TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = $"It gets the sharpest version Windows can play ({(YouTubeDownloader.PrefersAv1 ? "up to 4K" : "up to 1080p; add the free AV1 Video Extension from the Microsoft Store for 4K")}) and keeps it in {MediaFolders.Downloads}.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = Brushes.Gray,
                },
                url,
                note,
                agree,
                progress,
                status,
                new DockPanel
                {
                    Children =
                    {
                        update,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancel, go },
                        },
                    },
                },
            },
        };

        if (!YouTubeDownloader.IsAvailable)
        {
            status.Text = "This copy of Panelith has no video downloader. Reinstall Panelith to get it back.";
            url.IsEnabled = false;
            go.IsEnabled = false;
        }

        await dialog.ShowDialog(owner);
        return result;
    }
}
