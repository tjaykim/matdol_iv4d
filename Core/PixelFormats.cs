namespace Matdol.Core;

public enum RawPixelFormat
{
    Yuv420p, Yuv422p, Yuv444p,
    Rgb24i, Bgr24i,
    Rgb24p, Bgr24p, Gbr24p,
    Gray,
}

public enum ChannelView { All = 0, Ch0 = 1, Ch1 = 2, Ch2 = 3 }

public enum YuvMatrix { Bt601, Bt709, Bt2020, Smpte240M, YCoCg }

public static class YuvMatrixInfo
{
    public static string DisplayName(this YuvMatrix m) => m switch
    {
        YuvMatrix.Bt601 => "BT.601 (SD)",
        YuvMatrix.Bt709 => "BT.709 (HD)",
        YuvMatrix.Bt2020 => "BT.2020 NCL (UHD)",
        YuvMatrix.Smpte240M => "SMPTE 240M",
        YuvMatrix.YCoCg => "YCoCg",
        _ => m.ToString(),
    };

    public static string ShortName(this YuvMatrix m) => m switch
    {
        YuvMatrix.Bt601 => "BT.601", YuvMatrix.Bt709 => "BT.709", YuvMatrix.Bt2020 => "BT.2020",
        YuvMatrix.Smpte240M => "240M", YuvMatrix.YCoCg => "YCoCg", _ => m.ToString(),
    };

    public static (double kr, double kb) Coefficients(this YuvMatrix m) => m switch
    {
        YuvMatrix.Bt601 => (0.299, 0.114),
        YuvMatrix.Bt709 => (0.2126, 0.0722),
        YuvMatrix.Bt2020 => (0.2627, 0.0593),
        YuvMatrix.Smpte240M => (0.212, 0.087),
        _ => (0, 0),
    };
}

public static class RawFormatInfo
{
    public static bool IsYuv(this RawPixelFormat f) => f <= RawPixelFormat.Yuv444p;
    public static bool IsGray(this RawPixelFormat f) => f == RawPixelFormat.Gray;
    public static bool IsPlanar(this RawPixelFormat f) => f != RawPixelFormat.Rgb24i && f != RawPixelFormat.Bgr24i;
    public static int PlaneCount(this RawPixelFormat f) => f.IsGray() ? 1 : f.IsPlanar() ? 3 : 1;

    public static (int shiftX, int shiftY) ChromaShift(this RawPixelFormat f) => f switch
    {
        RawPixelFormat.Yuv420p => (1, 1),
        RawPixelFormat.Yuv422p => (1, 0),
        _ => (0, 0),
    };

    public static long FrameBytes(this RawPixelFormat f, int w, int h, int bitDepth = 8)
    {
        var (sx, sy) = f.ChromaShift();
        long y = (long)w * h;
        long c = (long)(w >> sx) * (h >> sy);
        long samples = f.IsGray() ? y : y + 2 * c;
        return samples * (bitDepth > 8 ? 2 : 1);
    }

    public static string DisplayName(this RawPixelFormat f) => f switch
    {
        RawPixelFormat.Yuv420p => "YUV 4:2:0 planar",
        RawPixelFormat.Yuv422p => "YUV 4:2:2 planar",
        RawPixelFormat.Yuv444p => "YUV 4:4:4 planar",
        RawPixelFormat.Rgb24i => "RGB interleaved",
        RawPixelFormat.Bgr24i => "BGR interleaved",
        RawPixelFormat.Rgb24p => "RGB planar",
        RawPixelFormat.Bgr24p => "BGR planar",
        RawPixelFormat.Gbr24p => "GBR planar",
        RawPixelFormat.Gray => "Gray (Y only)",
        _ => f.ToString(),
    };

    public static string ShortName(this RawPixelFormat f) => f switch
    {
        RawPixelFormat.Yuv420p => "YUV420",
        RawPixelFormat.Yuv422p => "YUV422",
        RawPixelFormat.Yuv444p => "YUV444",
        RawPixelFormat.Rgb24i or RawPixelFormat.Rgb24p => "RGB",
        RawPixelFormat.Bgr24i or RawPixelFormat.Bgr24p => "BGR",
        RawPixelFormat.Gbr24p => "GBR",
        RawPixelFormat.Gray => "Gray",
        _ => f.ToString(),
    };

    public static RawPixelFormat? GuessFromExtension(string path, RawPixelFormat current)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".yuv" => current.IsYuv() ? current : RawPixelFormat.Yuv422p,
            ".y" => RawPixelFormat.Gray,
            ".raw" => current.IsYuv() ? RawPixelFormat.Rgb24p : current,
            ".rgb" => current == RawPixelFormat.Rgb24i ? current : RawPixelFormat.Rgb24p,
            ".bgr" => current == RawPixelFormat.Bgr24i ? current : RawPixelFormat.Bgr24p,
            ".gbr" => RawPixelFormat.Gbr24p,
            _ => null,
        };
    }

    public static bool IsRawExtension(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".yuv" or ".y" or ".rgb" or ".bgr" or ".gbr" or ".raw";
    }

    public static bool IsImageExtension(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".bmp" or ".dib" or ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".png" or ".webp" or ".gif" or ".ico" or ".wbmp";
    }

    public static bool IsPnmExtension(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".ppm" or ".pgm" or ".pbm" or ".pnm";
    }
}

public enum FrameLayout
{
    Progressive,
    FieldSeparated,
    StereoSideBySide,
    StereoTopBottom,
}

public sealed record SizePreset(string Name, int Width, int Height)
{
    public static readonly SizePreset[] Table =
    {
        new("QCIF", 176, 144), new("CIF", 352, 288), new("QVGA", 320, 240), new("VGA", 640, 480),
        new("D1", 720, 480), new("4CIF", 704, 576), new("HD (720p)", 1280, 720), new("HD (768p)", 1366, 768),
        new("Full-HD (1080p)", 1920, 1080), new("Full-HD (1088p)", 1920, 1088),
        new("Ultra-HD (4K)", 3840, 2160),
    };
    public override string ToString() => $"{Name}  {Width}x{Height}";
}
