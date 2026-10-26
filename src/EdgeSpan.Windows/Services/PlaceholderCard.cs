using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EdgeSpan.Windows.Services;

/// <summary>Optional quiet card left where a window was before it moved to the tablet.</summary>
internal static class PlaceholderCard
{
    private static readonly Dictionary<uint, Window> Cards = new();

    public static void Show(uint id, int left, int top, int width, int height, double scale, string title, string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                Close(id);
                var w = Math.Clamp(width / Math.Max(0.5, scale), 160, 420);
                var h = 72.0;
                var card = new Window
                {
                    SystemDecorations = SystemDecorations.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    CanResize = false,
                    Width = w,
                    Height = h,
                    Background = new SolidColorBrush(Color.Parse("#E6202330")),
                    Position = new PixelPoint(left + Math.Max(0, (int)((width - w * scale) / 2)), top + Math.Max(0, (int)((height - h * scale) / 2))),
                    Content = new StackPanel
                    {
                        Margin = new Thickness(16, 12),
                        Spacing = 2,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = title, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.Parse("#B0B6C8")) }
                        }
                    }
                };
                Cards[id] = card;
                card.Show();
            }
            catch (Exception ex)
            {
                DiagLog.Write("handoff placeholder failed " + ex.Message);
            }
        });
    }

    public static void Close(uint id)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Close(id));
            return;
        }
        if (Cards.Remove(id, out var card))
            card.Close();
    }
}
