using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Panelith.App.Views;

/// <summary>A small yes-or-no question.</summary>
internal static class ConfirmPrompt
{
    /// <summary>Asks <paramref name="question"/>. Returns true when the user chose <paramref name="yes"/>.</summary>
    public static async Task<bool> AskAsync(Window? owner, string question, string detail, string yes, string no = "Cancel")
    {
        if (owner is null)
        {
            return false;
        }

        var answer = false;
        var dialog = new Window
        {
            Title = "Panelith",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var yesButton = new Button { Content = yes, Classes = { "accent" } };
        var noButton = new Button { Content = no };
        yesButton.Click += (_, _) =>
        {
            answer = true;
            dialog.Close();
        };
        noButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = question, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { noButton, yesButton },
                },
            },
        };

        await dialog.ShowDialog(owner);
        return answer;
    }
}
