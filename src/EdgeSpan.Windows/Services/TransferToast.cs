using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EdgeSpan.Product;

namespace EdgeSpan.Windows.Services;

/// <summary>A small Windows 11 style card for file copy progress.</summary>
public sealed class TransferToast
{
    private Window? _window;
    private Border? _card;
    private TextBlock? _title;
    private TextBlock? _detail;
    private Border? _bar;
    private long _hideAt;
    private readonly Motion.Spring1D _slide = new(Motion.Toast, 18);
    private readonly Motion.Spring1D _fade = new(Motion.Toast, 0);
    private DispatcherTimer? _motion;
    private bool _leaving;

    public void Say(string title, string detail) =>
        Dispatcher.UIThread.Post(() => Show(title, detail, 1, true));

    public void Update(string language, string name, long done, long total, string phase)
    {
        var title = phase switch
        {
            "ready" => Catalog.Get(language, "transferReady"),
            "moved" => Catalog.Get(language, "transferMoved"),
            "failed" => Catalog.Get(language, "transferFailed"),
            _ => Catalog.Get(language, "transferWorking")
        };
        var fraction = total <= 0 ? 1 : Math.Clamp(done / (double)total, 0, 1);
        var detail = name + "  " + Format(done) + " / " + Format(total);
        Dispatcher.UIThread.Post(() => Show(title, detail, fraction, phase is "ready" or "moved" or "failed"));
    }

    private void Show(string title, string detail, double fraction, bool done)
    {
        if (_window == null)
        {
            _title = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };
            _detail = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#C8C8C8")), TextWrapping = TextWrapping.Wrap };
            _bar = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.Parse("#3DDC84")), HorizontalAlignment = HorizontalAlignment.Left };
            var track = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.Parse("#3A3A3A")),
                Child = _bar,
                Margin = new Thickness(0, 8, 0, 0)
            };
            _card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(214, 32, 32, 34)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(16, 14),
                Width = 320,
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 24, OffsetY = 8, Color = Color.FromArgb(70, 0, 0, 0) }),
                Child = new StackPanel { Spacing = 2, Children = { _title, _detail, track } },
                RenderTransform = new TranslateTransform(0, 18),
                Opacity = 0
            };
            _window = new Window
            {
                Content = _card,
                Width = 336,
                Height = 108,
                SystemDecorations = SystemDecorations.None,
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = true,
                CanResize = false,
                Background = Brushes.Transparent,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                WindowStartupLocation = WindowStartupLocation.Manual,
                FontFamily = new FontFamily("avares://EdgeSpan/Assets/Fonts/PlusJakartaSans-Regular.ttf#Plus Jakarta Sans")
            };
            _window.Opened += (_, _) => Place();
            _window.Show();
        }
        _title!.Text = title;
        _detail!.Text = detail;
        _bar!.Width = Math.Max(4, 288 * fraction);
        Place();
        if (!_window.IsVisible)
            _window.Show();
        _leaving = false;
        _slide.Retarget(0);
        _fade.Retarget(1);
        StartMotion();
        _hideAt = Environment.TickCount64 + (done ? 2400 : 20000);
        var token = _hideAt;
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(done ? 2500 : 20000);
            if (token != _hideAt)
                return;
            _leaving = true;
            _slide.Retarget(16);
            _fade.Retarget(0);
            StartMotion();
        });
    }

    private void StartMotion()
    {
        _motion ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _motion.Tick -= Step;
        _motion.Tick += Step;
        _motion.Start();
    }

    private void Step(object? sender, EventArgs e)
    {
        var reduce = false;
        var slideDone = _slide.Step(0.016, reduce);
        var fadeDone = _fade.Step(0.016, reduce);
        if (_card != null)
        {
            _card.Opacity = Math.Clamp(_fade.Value, 0, 1);
            if (_card.RenderTransform is TranslateTransform shift)
                shift.Y = _slide.Value;
        }
        if (!slideDone || !fadeDone)
            return;
        _motion?.Stop();
        if (_leaving)
            _window?.Hide();
    }

    private void Place()
    {
        if (_window == null)
            return;
        var screen = _window.Screens?.Primary;
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1280, 800);
        var scale = screen?.Scaling ?? 1;
        _window.Position = new PixelPoint(
            area.X + area.Width - (int)(352 * scale),
            area.Y + area.Height - (int)(120 * scale));
    }

    private static string Format(long bytes)
    {
        if (bytes < 1024)
            return bytes + " B";
        if (bytes < 1024 * 1024)
            return (bytes / 1024.0).ToString("0.0") + " KB";
        return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
    }
}
