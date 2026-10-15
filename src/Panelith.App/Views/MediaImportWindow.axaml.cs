using System.Runtime.Versioning;
using Avalonia.Controls;
using Panelith.App.ViewModels;

namespace Panelith.App.Views;

/// <summary>The crop, trim and bake dialog for videos and pictures.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class MediaImportWindow : Window
{
    private string? _result;

    public MediaImportWindow() => InitializeComponent();

    /// <summary>Shows the importer. Returns the screen id made or changed, or null when cancelled.</summary>
    public static async Task<string?> ShowAsync(Window owner, MediaImportViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var window = new MediaImportWindow { DataContext = model };
        model.Finished += (_, id) =>
        {
            window._result = id;
            window.Close();
        };
        window.Closing += (_, e) =>
        {
            if (model.IsBusy)
            {
                model.CancelCommand.Execute(null);
                e.Cancel = true;
            }
        };
        window.Opened += async (_, _) => await model.LoadAsync();
        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            model.Dispose();
        }

        return window._result;
    }
}
