using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using EdgeSpan.Windows.Services;
using EdgeSpan.Windows.ViewModels;
using EdgeSpan.Windows.Views;

namespace EdgeSpan.Windows;

public sealed class App : Application
{
    private SessionRuntime? _runtime;
    private TrayIcon? _tray;
    private NativeMenuItem? _openItem;
    private NativeMenuItem? _switchItem;
    private NativeMenuItem? _quitItem;
    private NativeMenuItem? _sendItem;
    private MainWindow? _window;
    private MainViewModel? _model;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _runtime = new SessionRuntime();
            _model = new MainViewModel(_runtime);
            _model.LanguageChanged += RefreshTray;
            _model.ChromeChanged += RefreshTray;
            _window = new MainWindow(_model);
            desktop.MainWindow = _window;
            desktop.ShutdownRequested += (_, _) => _runtime.Dispose();
            InstallTray();
            RefreshTray();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void InstallTray()
    {
        if (_window == null)
            return;
        using var stream = AssetLoader.Open(new Uri("avares://EdgeSpan/Assets/icon.png"));
        _openItem = new NativeMenuItem();
        _switchItem = new NativeMenuItem();
        _quitItem = new NativeMenuItem();
        _sendItem = new NativeMenuItem();
        _sendItem.Click += (_, _) => _model?.SendWindowToTablet();
        _openItem.Click += (_, _) => ShowWindow();
        _switchItem.Click += (_, _) => _model?.CycleCommand.Execute(null);
        _quitItem.Click += (_, _) =>
        {
            _window.AllowExit();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        };
        var menu = new NativeMenu();
        menu.Add(_openItem);
        menu.Add(_switchItem);
        menu.Add(_sendItem);
        menu.Add(_quitItem);
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(stream),
            Menu = menu,
            IsVisible = true
        };
        _tray.Clicked += (_, _) => ShowWindow();
        var icons = new TrayIcons { _tray };
        TrayIcon.SetIcons(this, icons);
    }

    private void RefreshTray()
    {
        if (_model == null || _tray == null || _openItem == null || _quitItem == null || _switchItem == null)
            return;
        _tray.ToolTipText = _model.ProductName;
        _openItem.Header = _model.ShowWindowText;
        _switchItem.Header = _model.TraySwitchText;
        _quitItem.Header = _model.QuitText;
        if (_sendItem != null)
        {
            _sendItem.Header = EdgeSpan.Product.Catalog.Format(_model.Host.Settings.Language, "traySendWindow", _model.Host.Settings.HotkeySendWindow);
            _sendItem.IsVisible = EdgeSpan.Layout.HandoffPolicy.AllowsPcToPhone(_model.Host.Settings.Handoff);
        }
    }

    private void ShowWindow()
    {
        if (_window == null)
            return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}
