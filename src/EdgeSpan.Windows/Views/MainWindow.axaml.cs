using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using EdgeSpan.Product;
using EdgeSpan.Session;
using EdgeSpan.Windows.ViewModels;

namespace EdgeSpan.Windows.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _allowExit;

    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        model.LoadFormOnce();
        ApplyChrome();
        model.LanguageChanged += ApplyChrome;
        model.ThemeChanged += ApplyChrome;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Page))
                FadePage();
        };
        Map.PlacementChanged += (id, edge, anchor, corner, offset) => model.PlaceDevice(id, edge, anchor, corner, offset);
        model.CopyRequested += CopyAsync;
        model.RemoteClipboard += text =>
        {
            model.NoteClipboardWrite();
            CopyAsync(text);
        };
        using (var icon = AssetLoader.Open(new Uri("avares://EdgeSpan/Assets/icon.png")))
            Icon = new WindowIcon(icon);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += async (_, _) =>
        {
            model.Pull();
            await PollClipboard();
        };
        timer.Start();
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        Cursor = new Cursor(StandardCursorType.Arrow);
        Opened += (_, _) =>
        {
            ForceArrowCursor();
            ApplyChrome();
        };
        PointerEntered += (_, _) => ForceArrowCursor();
    }

    private void ForceArrowCursor()
    {
        Cursor = new Cursor(StandardCursorType.Arrow);
        if (!OperatingSystem.IsWindows())
            return;
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            return;
        var arrow = LoadCursor(IntPtr.Zero, new IntPtr(32512));
        SetClassLongPtr(hwnd, -12, arrow);
        SetCursor(arrow);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr(IntPtr hwnd, int index, IntPtr value);

    public void AllowExit() => _allowExit = true;

    private void ApplyChrome()
    {
        FlowDirection = Catalog.IsRtl(_model.Host.Settings.Language) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var rtl = Catalog.IsRtl(_model.Host.Settings.Language);
        var jakarta = "avares://EdgeSpan/Assets/Fonts/PlusJakartaSans-Regular.ttf#Plus Jakarta Sans";
        var vazir = "avares://EdgeSpan/Assets/Fonts/Vazirmatn-Regular.ttf#Vazirmatn";
        FontFamily = new FontFamily(rtl ? $"{vazir}, {jakarta}, Inter" : $"{jakarta}, {vazir}, Inter");
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = _model.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        Title = "EdgeSpan";
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None };
            Background = Brushes.Transparent;
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                var backdrop = 2;
                DwmSetWindowAttribute(hwnd, 38, ref backdrop, 4);
            }
        }
    }

    private readonly Motion.Spring1D _page = new(Motion.Standard, 1);
    private DispatcherTimer? _pageTimer;

    private void FadePage()
    {
        if (Pages == null)
            return;
        if (Motion.Reduced(_model.Host.Settings.ReduceMotion, ClientAnimationOff()))
        {
            Pages.Opacity = 1;
            return;
        }
        _page.Nudge(0.78);
        _page.Retarget(1);
        _pageTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _pageTimer.Tick -= PageTick;
        _pageTimer.Tick += PageTick;
        _pageTimer.Start();
    }

    private void PageTick(object? sender, EventArgs e)
    {
        if (_page.Step(0.016, false))
        {
            Pages.Opacity = 1;
            _pageTimer?.Stop();
            return;
        }
        Pages.Opacity = _page.Value;
    }

    private static bool ClientAnimationOff()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        var enabled = 1;
        SystemParametersInfo(0x1042, 0, ref enabled, 0);
        return enabled == 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint ini);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyWidth(double width)
    {
        var narrow = width < 860;
        RootGrid.ColumnDefinitions[0].Width = new GridLength(narrow ? 0 : 248);
        SideNav.IsVisible = !narrow;
        TopNav.IsVisible = narrow;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_model.RecordingHotkey)
        {
            if (e.Key == Key.Escape)
            {
                _model.CancelHotkeyCapture();
                e.Handled = true;
                return;
            }
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                e.Handled = true;
                return;
            }
            var name = HotkeyName(e.Key);
            if (name == null)
            {
                e.Handled = true;
                return;
            }
            var parts = new List<string>();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
            parts.Add(name);
            _model.CommitHotkey(string.Join("+", parts));
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private static string? HotkeyName(Key key) => key switch
    {
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.Tab => "Tab",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Up => "Up",
        Key.Down => "Down",
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F12 => key.ToString(),
        _ => null
    };

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (OperatingSystem.IsWindows() && !_allowExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private async void CopyAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(text);
    }

    private async Task PollClipboard()
    {
        if (!_model.Host.Settings.ClipboardShare || _model.NativeClipboard)
            return;
        if (_model.ConsumeClipboardSuppression())
            return;
        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;
        try
        {
            var text = await clipboard.TryGetTextAsync();
            _model.Host.SetLocalClipboard(text);
        }
        catch (Exception)
        {
        }
    }
}
