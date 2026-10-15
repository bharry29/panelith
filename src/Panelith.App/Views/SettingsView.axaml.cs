using Avalonia.Controls;
using Avalonia.Input;

namespace Panelith.App.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e) => HotkeyCapture.OnKeyDown(sender, e);
}
