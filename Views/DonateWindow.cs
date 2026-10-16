using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Matdol.Views;

public sealed class DonateWindow : Window
{
    private static DonateWindow? _open;

    public static void ShowFor(Window owner, Action<string> openUrl)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new DonateWindow(openUrl);
        _open.Closed += (_, _) => _open = null;
        _open.Show(owner);
    }

    private static readonly IBrush Heart = new SolidColorBrush(Color.Parse("#5FEBDD"));
    private static readonly IBrush Body = new SolidColorBrush(Color.Parse("#E6ECEF"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8FA0AA"));

    private DonateWindow(Action<string> openUrl)
    {
        Title = $"{AppInfo.Name} — donate";
        Icon = AppInfo.Icon;
        Width = 440; SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppInfo.ViewBackgroundBrush;

        var stack = new StackPanel { Margin = new Thickness(22, 16, 22, 4), Spacing = 8 };

        var title = new TextBlock { FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Body };
        title.Inlines = new InlineCollection { new Run("♥  ") { Foreground = Heart }, new Run($"support {AppInfo.Name}") };
        stack.Children.Add(title);

        Para(stack, $"{AppInfo.Name} is free, with no ads and no locked features.");
        Para(stack, $"I also tinker with other software, image processing algorithms and games. " +
                    $"More donations, more of my evenings go to {AppInfo.Name}. Probably.");
        Para(stack, "A donation is just a thank-you — it doesn't unlock anything.", Dim);

        bool ready = AppInfo.DonateUrl.Length > 0;
        var donate = new Button
        {
            Content = ready ? "♥  donate (GitHub Sponsors)" : "donation page — coming soon",
            IsEnabled = ready,
            Padding = new Thickness(14, 6),
            Margin = new Thickness(0, 6, 0, 0),
        };
        if (ready)
        {
            ToolTip.SetTip(donate, AppInfo.DonateUrl);
            donate.Click += (_, _) => openUrl(AppInfo.DonateUrl);
        }
        stack.Children.Add(donate);

        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Close();

        var dock = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        dock.Children.Add(close);
        dock.Children.Add(stack);
        Content = dock;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    private static void Para(StackPanel s, string text, IBrush? brush = null) =>
        s.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = brush ?? Body, TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
}
