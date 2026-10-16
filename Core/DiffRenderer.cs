using System;

namespace Matdol.Core;

public enum DiffMode { Diff = 0, Img1 = 1, Img2 = 2, Avg = 3 }

public sealed class DiffSettings
{
    public DiffMode Mode = DiffMode.Diff;
    public int Threshold = 0;
    public int Gain = 1;
    public bool BlackWhite;
    public int FrameOffset = 0;
    public bool MidCenter;

    public DiffSettings Clone() => (DiffSettings)MemberwiseClone();
}

public sealed class ComparePlan
{
    public bool Rgb { get; }
    public int Depth { get; }
    public int Max { get; }
    public int W { get; }
    public int H { get; }
    public FrameData Proto { get; }

    public ComparePlan(FrameData self, FrameData refB)
    {
        static bool yuvSpace(FrameData f) => f.Format.IsYuv() || f.Format.IsGray();
        Rgb = !(yuvSpace(self) && yuvSpace(refB));
        bool converts = Rgb && (self.Format.IsYuv() || refB.Format.IsYuv());
        Depth = converts ? 8 : Math.Min(self.BitDepth, refB.BitDepth);
        Max = (1 << Depth) - 1;
        W = Math.Min(self.Width, refB.Width);
        H = Math.Min(self.Height, refB.Height);
        var yuvSrc = self.Format.IsYuv() ? self : refB;
        Proto = new FrameData
        {
            Width = 1, Height = 1, MaxValue = Max,
            Format = Rgb ? RawPixelFormat.Rgb24i
                   : self.Format.IsGray() && refB.Format.IsGray() ? RawPixelFormat.Gray : RawPixelFormat.Yuv444p,
            Matrix = yuvSrc.Matrix, LimitedRange = yuvSrc.LimitedRange,
        };
    }

    public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;

    public (int c0, int c1, int c2) Sample(FrameData f, int x, int y)
    {
        var (c0, c1, c2) = f.GetPixel(x, y);
        if (Rgb && f.Format.IsYuv())
        {
            var (r, g, b) = f.YuvToRgb8(c0, c1, c2);
            return (r, g, b);
        }
        if (!Rgb && f.Format.IsGray() && !Proto.Format.IsGray()) { c1 = c2 = (f.MaxValue + 1) >> 1; }
        int sh = Math.Max(0, f.BitDepth - Depth);
        return (c0 >> sh, c1 >> sh, c2 >> sh);
    }

    public string Describe() => (Rgb ? "RGB" : Proto.Format.IsGray() ? "Gray" : "YUV") + $" {Depth}bit";
}

public static class DiffRenderer
{
    private const int OutsideDim = 90;

    public static unsafe void ToBgra(FrameData self, FrameData refB, DiffSettings s, ChannelView view, bool tint, Span<byte> dst)
    {
        var plan = new ComparePlan(self, refB);
        int w = self.Width, h = self.Height, max = plan.Max;
        bool yuv = !plan.Rgb;
        fixed (byte* d0 = dst)
        {
            for (int y = 0; y < h; y++)
            {
                byte* d = d0 + (long)y * w * 4;
                for (int x = 0; x < w; x++, d += 4)
                {
                    byte r, g, bl;
                    if (!plan.Contains(x, y))
                    {
                        var (sr, sg, sb) = self.GetDisplayRgb(x, y, view, tint);
                        r = (byte)(sr * OutsideDim >> 8); g = (byte)(sg * OutsideDim >> 8); bl = (byte)(sb * OutsideDim >> 8);
                        d[0] = bl; d[1] = g; d[2] = r; d[3] = 255;
                        continue;
                    }
                    var (b0, b1, b2) = plan.Sample(refB, x, y);
                    var (a0, a1, a2) = plan.Sample(self, x, y);
                    switch (s.Mode)
                    {
                        case DiffMode.Img1: (r, g, bl) = plan.Proto.ConvertToDisplay(b0, b1, b2, view, tint); break;
                        case DiffMode.Img2: (r, g, bl) = plan.Proto.ConvertToDisplay(a0, a1, a2, view, tint); break;
                        case DiffMode.Avg: (r, g, bl) = plan.Proto.ConvertToDisplay((a0 + b0 + 1) >> 1, (a1 + b1 + 1) >> 1, (a2 + b2 + 1) >> 1, view, tint); break;
                        default:
                        {
                            int s0 = a0 - b0, s1 = a1 - b1, s2 = a2 - b2;
                            int d0v = Math.Abs(s0), d1v = Math.Abs(s1), d2v = Math.Abs(s2);
                            if (s.BlackWhite)
                            {
                                bool hit = view switch
                                {
                                    ChannelView.Ch0 => d0v > s.Threshold,
                                    ChannelView.Ch1 => d1v > s.Threshold,
                                    ChannelView.Ch2 => d2v > s.Threshold,
                                    _ => d0v > s.Threshold || d1v > s.Threshold || d2v > s.Threshold,
                                };
                                r = g = bl = hit ? (byte)255 : (byte)0;
                            }
                            else
                            {
                                int e0, e1, e2;
                                if (s.MidCenter) { e0 = ScaleMid(s0, s, max); e1 = ScaleMid(s1, s, max); e2 = ScaleMid(s2, s, max); }
                                else { e0 = Scale(d0v, s, max); e1 = Scale(d1v, s, max); e2 = Scale(d2v, s, max); }
                                if (view == ChannelView.All)
                                {
                                    if (yuv) r = g = bl = (byte)e0;
                                    else { r = (byte)e0; g = (byte)e1; bl = (byte)e2; }
                                }
                                else
                                {
                                    byte v = (byte)(view == ChannelView.Ch0 ? e0 : view == ChannelView.Ch1 ? e1 : e2);
                                    if (tint && !yuv)
                                        (r, g, bl) = (view == ChannelView.Ch0 ? v : (byte)0, view == ChannelView.Ch1 ? v : (byte)0, view == ChannelView.Ch2 ? v : (byte)0);
                                    else r = g = bl = v;
                                }
                            }
                            break;
                        }
                    }
                    d[0] = bl; d[1] = g; d[2] = r; d[3] = 255;
                }
            }
        }
    }

    private static int Scale(int diff, DiffSettings s, int max)
    {
        if (diff < s.Threshold) return 0;
        long v = (long)diff * s.Gain * 255 / max;
        return (int)Math.Min(255, v);
    }

    private static int ScaleMid(int diff, DiffSettings s, int max)
    {
        long mid = (max + 1) >> 1;
        long v = Math.Abs(diff) < s.Threshold ? mid : Math.Clamp(diff * (long)s.Gain + mid, 0, max);
        return (int)((v * 255 + max / 2) / max);
    }
}
