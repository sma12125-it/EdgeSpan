using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
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
        Map.PlacementChanged += (id, monitor, edge, anchor, corner, offset) => model.PlaceDevice(id, monitor, edge, anchor, corner, offset);
        Map.TileSelected += model.SelectDeviceById;
        model.IdentifyRequested += Identify;
        // Wheel/touchpad scrolling anywhere on a page (cards, expanders, the device list, number boxes, the
        // Arrange map) always scrolls the page: inner controls with their own ScrollViewer/spinner used to
        // swallow the wheel, so the page looked stuck. Tunnel handler = runs before them.
        Pages.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<ComboBox>(true) is { IsDropDownOpen: true })
                return;
            var y = Pages.Offset.Y - e.Delta.Y * 64;
            var max = Math.Max(0, Pages.Extent.Height - Pages.Viewport.Height);
            Pages.Offset = new Vector(Pages.Offset.X, Math.Clamp(y, 0, max));
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
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
        // Windows 11 type ramp: Segoe UI Variable (system font), Vazirmatn for Persian.
        FontFamily = new FontFamily(rtl
            ? $"{vazir}, Segoe UI Variable Text, Segoe UI, {jakarta}"
            : $"Segoe UI Variable Text, Segoe UI, {jakarta}, {vazir}, Inter");
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = _model.FollowSystemTheme
                ? ThemeVariant.Default
                : _model.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        Title = "EdgeSpan";
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            // Solid Win11 Settings neutrals (#F3F3F3 / #202020). Mica was dropped: it tints the window with the
            // wallpaper colour, which produced the cream/peach background on warm wallpapers.
            TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                var backdrop = 1; // DWMSBT_NONE
                DwmSetWindowAttribute(hwnd, 38, ref backdrop, 4);
                var dark = _model.IsDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
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
        // Win11 nav: full pane >= 1080 px, icon-only rail 640-1080 px, top bar below 640 px.
        var narrow = width < 640;
        var compact = !narrow && width < 1080;
        var navW = narrow ? 0 : compact ? 64 : 292;
        RootGrid.ColumnDefinitions[0].Width = new GridLength(navW);
        SideNav.IsVisible = !narrow;
        SideNav.Classes.Set("compact", compact);
        SideNav.Padding = compact ? new Thickness(6, 16, 6, 16) : new Thickness(12, 16, 8, 16);
        NavHeader.IsVisible = !compact;
        TopNav.IsVisible = narrow;
        // 24-36 px page margins (16 on very narrow windows), content centred with a 1000 px max width.
        var side = width < 560 ? 16 : width < 1100 ? 24 : 36;
        // Margins live on the content, not ScrollViewer.Padding: the padding is not part of the scroll extent,
        // so the last card on long pages (Settings) was clipped and could not be scrolled into view.
        Pages.Padding = default;
        PageStack.Margin = new Thickness(side, 0, side, 48);
        PageStack.MaxWidth = 1000 + side * 2;
        TitleRow.Margin = new Thickness(side, narrow ? 12 : 22, side, 14);
        var content = Math.Min(1000, width - navW - side * 2 - 16);
        // Arrange map scales with the available width (keeps roughly the monitor aspect).
        MapHost.Height = Math.Clamp(content * 0.42, 180, 340);
        // Setting rows: below ~620 px of content the control drops under its title instead of overflowing.
        StackRows(content < 620);
    }

    private bool? _stacked;

    private void StackRows(bool stacked)
    {
        if (_stacked == stacked)
            return;
        _stacked = stacked;
        foreach (var border in PageStack.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("setting")))
        {
            if (border.Child is not Grid grid || grid.ColumnDefinitions.Count != 3)
                continue;
            var ctrl = grid.Children.OfType<WrapPanel>().FirstOrDefault(p => p.Classes.Contains("ctrl"));
            if (ctrl == null)
                continue;
            if (grid.RowDefinitions.Count == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }
            Grid.SetRow(ctrl, stacked ? 1 : 0);
            Grid.SetColumn(ctrl, stacked ? 1 : 2);
            ctrl.Margin = stacked ? new Thickness(0, 8, 0, 0) : default;
            ctrl.HorizontalAlignment = stacked ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Stretch;
        }
    }

    private void Identify()
    {
        var monitors = _model.Monitors;
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var w = new Window
            {
                SystemDecorations = SystemDecorations.None, Topmost = true, ShowActivated = false, CanResize = false,
                Width = 140, Height = 140, Background = new SolidColorBrush(Color.Parse("#E6202020")),
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(m.X + 48, m.Bottom - 48 - (int)(140 * m.Dpi / 96.0)),
                Content = new TextBlock
                {
                    Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 84, Foreground = Brushes.White,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                }
            };
            w.Show();
            DispatcherTimer.RunOnce(w.Close, TimeSpan.FromSeconds(2.5));
        }
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
