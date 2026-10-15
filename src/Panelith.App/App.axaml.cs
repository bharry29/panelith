using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Panelith.App.Services;
using Panelith.App.ViewModels;
using Panelith.App.Views;
using Panelith.Engine;

namespace Panelith.App;

public sealed partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private PanelithService? _service;
    private AppLog? _log;
    private AppSettingsStore? _store;
    private TrayIcon? _tray;
    private bool _quitting;
    private readonly ErrorBurst _errors = new(count: 3, window: TimeSpan.FromSeconds(60));
    private bool _offeredRestart;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        _desktop = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var paths = AppPaths.Default;
        var migratedFrom = AppPaths.MigrateLegacy(paths.Root);
        _store = new AppSettingsStore(paths);
        var settings = _store.Load();
        _log = new AppLog(paths.LogsDirectory);
        var crashLog = _log.CreateLogger("Panelith.App");
        if (migratedFrom is not null)
        {
            crashLog.LogInformation("Moved settings from {From} to {To}.", migratedFrom, paths.Root);
        }
        if (WindowsIntegration.MigrateLegacyStartup())
        {
            crashLog.LogInformation("Moved start with Windows from RigGlass to Panelith.");
        }
        if (_store.LastQuarantined is { } badSettings)
        {
            crashLog.LogWarning("app.json was damaged and was moved to {Path}; default settings are in use.", badSettings);
        }

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            crashLog.LogError(e.Exception, "Unexpected error in the window: {Detail}", e.Exception.ToString());
            e.Handled = true;
            if (_errors.Record(DateTime.UtcNow))
            {
                Dispatcher.UIThread.Post(OfferRestart);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            crashLog.LogError(e.Exception, "Unexpected background error: {Detail}", e.Exception.ToString());
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // The process is going down; get the reason onto disk first.
            crashLog.LogCritical("Panelith is closing after an unexpected error: {Detail}", e.ExceptionObject.ToString());
            _log.Flush();
        };

        _window = new MainWindow();
        _window.Closing += OnWindowClosing;
        var minimized = Program.Arguments.Contains(WindowsIntegration.MinimizedArgument) || settings.StartMinimized;
        if (!minimized)
        {
            desktop.MainWindow = _window;
        }

        CreateTrayIcon();
        WatchForSecondLaunch();
        _ = StartAsync(settings);
    }

    private async Task StartAsync(AppSettings settings)
    {
        try
        {
            _log!.CreateLogger("Panelith.App").LogInformation("Starting on the UI thread: {OnUi}", Dispatcher.UIThread.CheckAccess());
            MediaFolders.ClearLeftovers();
            var service = await PanelithService.StartAsync(_log!, settings.DemoSensors);

            // Start-up runs before Avalonia installs its synchronisation context,
            // so the await above can resume on the thread pool. Timers and
            // bindings must be created on the UI thread.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _service = service;
                _viewModel = new MainViewModel(service, _store!, settings) { IsWindowVisible = _window!.IsVisible };
                _viewModel.ElevationRequested += (_, _) => RestartElevated();
                _viewModel.ProfilesChanged += (_, _) => RebuildTrayMenu();
                _viewModel.ProfileController.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(Services.Activity.ProfileController.Decision))
                    {
                        RebuildTrayMenu();
                    }
                };
                _window.DataContext = _viewModel;
                RebuildTrayMenu();
            });
        }
        catch (Exception ex)
        {
            _log?.CreateLogger("Panelith.App").LogError(ex, "Panelith could not start: {Detail}", ex.ToString());
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _window!.ShowStartupError(ex.Message);
                _window.Show();
            });
        }
    }

    private void CreateTrayIcon()
    {
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Panelith/Assets/panelith.ico"))),
            ToolTipText = "Panelith",
        };
        _tray.Clicked += (_, _) => ShowWindow();
        RebuildTrayMenu();
        TrayIcon.SetIcons(this, [_tray]);
    }

    /// <summary>Rebuilds the tray menu, listing every profile with the active one ticked.</summary>
    private void RebuildTrayMenu()
    {
        if (_tray is null)
        {
            return;
        }

        var open = new NativeMenuItem("Open Panelith");
        open.Click += (_, _) => ShowWindow();
        var next = new NativeMenuItem("Next screen");
        next.Click += (_, _) => _viewModel?.NextScreenCommand.Execute(null);
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => _ = QuitAsync();

        var menu = new NativeMenu { open, next };
        if (_viewModel?.ProfileController is { } profiles)
        {
            var submenu = new NativeMenu();
            var automatic = new NativeMenuItem("Automatic")
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = profiles.Automatic && !profiles.IsPinned,
            };
            automatic.Click += (_, _) =>
            {
                profiles.SetAutomatic(true);
                profiles.Pin(null);
            };
            submenu.Add(automatic);
            submenu.Add(new NativeMenuItemSeparator());
            foreach (var profile in profiles.Set.Profiles)
            {
                var id = profile.Id;
                var item = new NativeMenuItem(profile.Name)
                {
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = profiles.Active?.Id == id,
                };
                item.Click += (_, _) => profiles.Pin(id);
                submenu.Add(item);
            }

            var label = profiles.Active is { } active ? $"Profile: {active.Name}" : "Profile";
            menu.Add(new NativeMenuItem(label) { Menu = submenu });
        }

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);
        _tray.Menu = menu;
        _tray.ToolTipText = _viewModel?.ProfileController.Active is { } current ? $"Panelith · {current.Name}" : "Panelith";
    }

    private void WatchForSecondLaunch()
    {
        if (Program.ShowSignal is not { } signal)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            while (!_quitting)
            {
                try
                {
                    if (signal.WaitOne(TimeSpan.FromSeconds(1)))
                    {
                        Dispatcher.UIThread.Post(ShowWindow);
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "Panelith second-launch watcher",
        };
        thread.Start();
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        if (_viewModel is not null)
        {
            _viewModel.IsWindowVisible = true;
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting)
        {
            return;
        }

        var closeToTray = _viewModel?.Settings.CloseToTray ?? true;
        if (closeToTray && _service is not null)
        {
            e.Cancel = true;
            _window!.Hide();
            if (_viewModel is not null)
            {
                _viewModel.IsWindowVisible = false;
            }

            return;
        }

        e.Cancel = true;
        _ = QuitAsync();
    }

    /// <summary>After repeated window errors, offers a clean restart instead of limping on.</summary>
    private async void OfferRestart()
    {
        if (_offeredRestart || _quitting)
        {
            return;
        }

        _offeredRestart = true;
        try
        {
            var restart = await RecoveryPrompt.AskAsync(_window is { IsVisible: true } ? _window : null);
            if (restart && Environment.ProcessPath is { } exe)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, WindowsIntegration.RestartArgument) { UseShellExecute = false });
                await QuitAsync();
            }
        }
        catch (Exception ex)
        {
            // This runs because something already went wrong; it cannot be
            // allowed to go wrong again on the way out.
            _log?.CreateLogger("Panelith.App").LogWarning("Panelith could not restart itself: {Message}", ex.Message);
        }
    }

    private void RestartElevated()
    {
        if (WindowsIntegration.RelaunchElevated())
        {
            _ = QuitAsync();
        }
    }

    private async Task QuitAsync()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        _viewModel?.Dispose();
        if (_service is not null)
        {
            await _service.DisposeAsync();
        }

        _log?.Dispose();
        _window?.Close();
        _desktop?.Shutdown();
    }
}
