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
    private NativeMenuItem? _listItem;
    private NativeMenuItem? _backItem;
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
        _listItem = new NativeMenuItem { Menu = new NativeMenu() };
        menu.Add(_listItem);
        _backItem = new NativeMenuItem { Menu = new NativeMenu() };
        menu.Add(_backItem);
        var reconnect = new NativeMenuItem(EdgeSpan.Product.Catalog.Get(_model?.Host.Settings.Language ?? "en", "reconnect"));
        reconnect.Click += (_, _) => _model?.ReconnectCommand.Execute(null);
        menu.Add(reconnect);
        menu.Opening += (_, _) => FillWindowList();
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
            _sendItem.IsVisible = true; // always available; failures explain themselves (e.g. handoff turned off)
        }
        FillWindowList();
    }

    private void FillWindowList()
    {
        if (_listItem?.Menu == null || _model == null)
            return;
        var lang = _model.Host.Settings.Language;
        _listItem.Header = EdgeSpan.Product.Catalog.Get(lang, "trayWindowList");
        _listItem.Menu.Items.Clear();
        foreach (var (hwnd, title) in Services.HandoffHost.ListWindows())
        {
            var item = new NativeMenuItem(title);
            var target = hwnd;
            item.Click += (_, _) => _model?.SendWindowHandle(target);
            _listItem.Menu.Items.Add(item);
        }
        if (_listItem.Menu.Items.Count == 0)
            _listItem.Menu.Items.Add(new NativeMenuItem(EdgeSpan.Product.Catalog.Get(lang, "trayWindowNone")) { IsEnabled = false });
        if (_backItem?.Menu == null)
            return;
        // "Transferred windows": everything currently on a device, each with Bring back / Close.
        _backItem.Header = EdgeSpan.Product.Catalog.Get(lang, "transferredTitle");
        _backItem.Menu.Items.Clear();
        var sent = _model.TransferredWindows();
        foreach (var (id, title, _) in sent)
        {
            var row = new NativeMenuItem(title) { Menu = new NativeMenu() };
            var back = new NativeMenuItem(EdgeSpan.Product.Catalog.Get(lang, "bringBack"));
            back.Click += (_, _) => _model?.BringBackWindow(id);
            var close = new NativeMenuItem(EdgeSpan.Product.Catalog.Get(lang, "closeWindow"));
            close.Click += (_, _) => _model?.CloseTransferredWindow(id);
            row.Menu.Items.Add(back);
            row.Menu.Items.Add(close);
            _backItem.Menu.Items.Add(row);
        }
        if (sent.Count > 1)
        {
            var all = new NativeMenuItem(EdgeSpan.Product.Catalog.Get(lang, "bringBackAll"));
            all.Click += (_, _) => { foreach (var w in _model!.TransferredWindows()) _model.BringBackWindow(w.Id); };
            _backItem.Menu.Items.Add(all);
        }
        if (sent.Count == 0)
            _backItem.Menu.Items.Add(new NativeMenuItem(EdgeSpan.Product.Catalog.Get(lang, "transferredNone")) { IsEnabled = false });
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
