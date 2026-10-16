using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace Matdol.Views;

public sealed class LegalWindow : Window
{
    public enum Page { License = 0, Licenses = 1, Privacy = 2 }

    private static LegalWindow? _open;

    public static void ShowPage(Window owner, Page page)
    {
        if (_open == null)
        {
            _open = new LegalWindow();
            _open.Closed += (_, _) => _open = null;
            _open.Select(page);
            _open.Show(owner);
        }
        else
        {
            _open.Select(page);
            _open.Activate();
        }
    }

    private static readonly (string Header, string File)[] Pages =
    {
        ("License (MIT)", "LICENSE.txt"),
        ("Third-party licenses", "ThirdPartyNotices.txt"),
        ("Privacy", "Privacy.txt"),
    };

    private readonly TabControl _tabs;
    private readonly TextBox[] _bodies = new TextBox[Pages.Length];
    private readonly bool[] _loaded = new bool[Pages.Length];

    private LegalWindow()
    {
        Title = $"{AppInfo.Name} — legal";
        Icon = AppInfo.Icon;
        Width = 760; Height = 600;
        MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppInfo.ViewBackgroundBrush;

        var mono = new FontFamily("Consolas,Menlo,DejaVu Sans Mono,monospace");
        _tabs = new TabControl { Margin = new Thickness(10, 6, 10, 0) };
        for (int i = 0; i < Pages.Length; i++)
        {
            _bodies[i] = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = mono, FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#E6ECEF")),
                Background = new SolidColorBrush(Color.Parse("#162124")),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 10),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_bodies[i], Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(_bodies[i], Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            _tabs.Items.Add(new TabItem { Header = new TextBlock { Text = Pages[i].Header, FontSize = 14 }, Content = _bodies[i] });
        }
        _tabs.SelectionChanged += (_, _) => LoadBody(_tabs.SelectedIndex);

        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Close();

        var dock = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        dock.Children.Add(close);
        dock.Children.Add(_tabs);
        Content = dock;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    private void Select(Page page)
    {
        _tabs.SelectedIndex = (int)page;
        LoadBody((int)page);
    }

    private void LoadBody(int i)
    {
        if (i < 0 || i >= _bodies.Length || _loaded[i]) return;
        _loaded[i] = true;
        try
        {
            using var s = AssetLoader.Open(new Uri($"avares://matdol/Assets/Legal/{Pages[i].File}"));
            using var r = new StreamReader(s);
            _bodies[i].Text = r.ReadToEnd();
        }
        catch (Exception ex) { _bodies[i].Text = $"({Pages[i].File} could not be loaded: {ex.Message})"; }
        _bodies[i].CaretIndex = 0;
    }
}
