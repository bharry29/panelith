using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Panelith.App.Views;

/// <summary>Asks whether to restart Panelith after it hit several errors in a row.</summary>
internal static class RecoveryPrompt
{
    /// <summary>Shows the prompt. Returns true when the user chose to restart.</summary>
    public static async Task<bool> AskAsync(Window? owner)
    {
        var restart = false;
        var dialog = new Window
        {
            Title = "Panelith",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = owner is null,
        };

        var restartButton = new Button { Content = "Restart Panelith", Classes = { "accent" } };
        var keepButton = new Button { Content = "Keep running" };
        restartButton.Click += (_, _) =>
        {
            restart = true;
            dialog.Close();
        };
        keepButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "Panelith ran into several problems", FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Something in the window keeps failing. Restarting Panelith usually clears it. The details are in the log file in your Panelith folder.",
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { keepButton, restartButton },
                },
            },
        };

        if (owner is not null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }

        return restart;
    }
}
