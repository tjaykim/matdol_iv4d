using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Matdol.Core;

namespace Matdol.Views;

public sealed class NotesWindow : Window
{
    private static NotesWindow? _open;

    public static void ShowFor(Window owner)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new NotesWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show(owner);
    }

    private static IEnumerable<(string Title, string[] Lines)> Sections() => new[]
    {
        ("Slots", new[]
        {
            "A = selected slot: files open here; shown in solo layout",
            "B = compare base for diff (−) / average (+); set with Ctrl+click or the right-click menu",
        }),
        ("Format panel (bottom-left button)", new[]
        {
            "pixel format · bit depth · image size: headerless raw files only (other formats read them from the file)",
            "frame layout: progressive · separate fields (even ↑ / odd ↓) · stereo 3D side-by-side / top-and-bottom (raw only, size = one view)",
            "color matrix (YUV only): BT.601 · BT.709 · BT.2020 · SMPTE 240M · YCoCg, full / limited range",
            "camera raw (enabled when the selected slot holds a raw file): embedded preview JPEG · sensor CFA data · half-size RGB",
        }),
        ("File formats", new[]
        {
            "raw YUV / RGB / gray (.yuv .rgb .raw .bin …): any file without a known header is shown as raw binary",
            "Y4M (YUV4MPEG2): size · 4:2:0 / 4:2:2 / 4:4:4 / mono · 8~16 bit read from the header; plays like raw YUV",
            "PNG · JPEG · BMP · WebP · GIF · PNM · DPX · TIFF (incl. 16-bit, float, multi-page)",
            "camera raw: preview for all makers; sensor data / half-size RGB for Adobe DNG, Canon CR2, Nikon NEF",
            "HEIC / HEIF / AVIF: decoded by the OS (" + SystemCodecs.Name + ") — " + SystemCodecs.InstallHint,
            "not yet widely tested: .raw / .y4m, camera raw, DPX and some TIFF variants may still have undiscovered issues — reports with a sample file are welcome (GitHub Issues)",
        }),
        ("VESA DSC (.dsc) plugin", DscLines()),
        ("Other", new[]
        {
            "zoom 1X = one image pixel per screen pixel (display scaling is compensated)",
            "numpad digits and + / − work the same as the main keys",
            "settings (panel pins, camera raw mode) are kept in the user settings folder: matdol/settings.json",
        }),
    };

    private static string[] DscLines()
    {
        if (DscPlugin.IsAvailable)
            return new[]
            {
                $"installed{(DscPlugin.Version is { } v ? $" (ver. {v})" : "")}: {DscPlugin.FoundPath}",
                "source & updates: " + AppInfo.DscPluginUrl,
            };
        var how = new[]
        {
            "build: install the .NET 8 SDK, then run \"dotnet build src/Matdol.Dsc -c Release\" in the repository " + AppInfo.DscPluginUrl,
            "install: copy src/Matdol.Dsc/bin/Release/net8.0/Matdol.Dsc.dll into " + AppInfo.UserPluginDir + " (or next to the matdol executable), restart matdol",
        };
        var status = DscPlugin.FoundPath != null
            ? "found but not usable — " + DscPlugin.MissingReason
            : "not installed (optional) — without it, .dsc files open as raw binary. Published as source code only (no prebuilt files)";
        return new[] { status }.Concat(how).ToArray();
    }

    private NotesWindow()
    {
        Title = $"{AppInfo.Name} — formats & notes";
        Icon = AppInfo.Icon;
        Width = 720; Height = 560; MinWidth = 420; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppInfo.ViewBackgroundBrush;

        IBrush head = new SolidColorBrush(Color.Parse("#7CC8FF")), body = new SolidColorBrush(Color.Parse("#E6ECEF"));
        var stack = new StackPanel { Margin = new Thickness(20, 14, 20, 16), Spacing = 4 };
        foreach (var (title, lines) in Sections())
        {
            stack.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = head, Margin = new Thickness(0, 10, 0, 2) });
            foreach (var l in lines)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*"), Margin = new Thickness(6, 1, 0, 1) };
                row.Children.Add(new TextBlock { Text = "•", FontSize = 12, Foreground = body });
                var t = new TextBlock { Text = l, FontSize = 12, Foreground = body, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(t, 1); row.Children.Add(t);
                stack.Children.Add(row);
            }
        }
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Close();
        var dock = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        dock.Children.Add(close);
        dock.Children.Add(new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = dock;
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
    }
}
