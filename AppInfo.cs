using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Matdol.Controls;

namespace Matdol;

public static class AppInfo
{
    public const string Name = "matdol";
    public const string LabelName = "matdol";
    public const string LabelTail = "image viewer";
    public const string Tagline = "grind every pixel";

    public const string DscPluginUrl = "https://github.com/tjaykim/matdol-dsc";

    public const bool ShowDonate = true;
    public const string DonateUrl = "https://github.com/sponsors/tjaykim";

    public static string UserPluginDir =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "matdol", "plugins");

    public static string Version
    {
        get { var v = typeof(AppInfo).Assembly.GetName().Version; return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}"; }
    }
    public static string BuildDate =>
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "";
    public static string VersionText => BuildDate.Length > 0 ? $"(ver. {Version} / {BuildDate})" : $"(ver. {Version})";

    public static readonly Color ViewBackground = Color.Parse("#1F2D31");
    public static readonly IBrush ViewBackgroundBrush = new SolidColorBrush(ViewBackground);
    public static readonly IBrush[] SplitBackgroundBrushes =
    {
        new SolidColorBrush(Color.Parse("#24343A")),
        new SolidColorBrush(Color.Parse("#1F2D31")),
        new SolidColorBrush(Color.Parse("#1A2629")),
        new SolidColorBrush(Color.Parse("#151F22")),
    };

    public const int AnimFrames = 72;
    public static readonly TimeSpan SlowTurn = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan SplashTurn = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan SplashTime = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan HelpMilkFillTime = TimeSpan.FromMinutes(90);
    public static readonly TimeSpan IntroTime = TimeSpan.FromSeconds(SlowTurn.TotalSeconds / 2);

    private static Uri Asset(string rel) => new($"avares://matdol/Assets/{rel}");

    private static WindowIcon? _icon;
    public static WindowIcon Icon => _icon ??= new WindowIcon(AssetLoader.Open(Asset("matdol.ico")));

    private static FrameSet? _empty, _help;
    public static FrameSet EmptySlotFrames => _empty ??= new FrameSet(LoadFrames("Empty64"), 64);
    public static FrameSet HelpFrames => _help ??= new FrameSet(LoadFrames("Help128"), 128);
    public static FrameSet LoadSplashStill(double scale) =>
        new(new[] { new Bitmap(AssetLoader.Open(Asset(scale <= 1.25 ? "Splash/still_192.webp" : "Splash/still_384.webp"))) }, 192);

    public static FrameSet LoadSplashFrames() => new(LoadFrames("Loading"), 192);

    public static Task PreloadAsync(double scale) => Task.WhenAll(
        Task.Run(() => { try { EmptySlotFrames.Prepare(scale); } catch { } }),
        Task.Run(() => { try { HelpFrames.Prepare(scale); } catch { } }));

    private static Bitmap[] LoadFrames(string folder)
    {
        var frames = new Bitmap[AnimFrames];
        for (int i = 0; i < AnimFrames; i++)
            frames[i] = new Bitmap(AssetLoader.Open(Asset($"{folder}/frame_{i:000}.webp")));
        return frames;
    }
}
