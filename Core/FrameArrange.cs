using System;

namespace Matdol.Core;

public static class FrameArrange
{
    public static (int mulW, int mulH) RawFrameMultiplier(this FrameLayout l) => l switch
    {
        FrameLayout.StereoSideBySide => (2, 1),
        FrameLayout.StereoTopBottom => (1, 2),
        _ => (1, 1),
    };

    public static string ShortName(this FrameLayout l) => l switch
    {
        FrameLayout.FieldSeparated => "fields",
        FrameLayout.StereoSideBySide => "3D SBS",
        FrameLayout.StereoTopBottom => "3D TB",
        _ => "",
    };

    public static (int splitX, int splitY) Splits(this FrameLayout l, int w, int h) => l switch
    {
        FrameLayout.StereoSideBySide => (w / 2, 0),
        FrameLayout.StereoTopBottom => (0, h / 2),
        FrameLayout.FieldSeparated => (0, (h + 1) / 2),
        _ => (0, 0),
    };

    public static FrameData Arrange(FrameData f, FrameLayout l)
    {
        if (l != FrameLayout.FieldSeparated || f.Height < 2) return f;
        var o = new FrameData
        {
            Width = f.Width, Height = f.Height, Format = f.Format, MaxValue = f.MaxValue,
            Matrix = f.Matrix, LimitedRange = f.LimitedRange,
            PlaneW = (int[])f.PlaneW.Clone(), PlaneH = (int[])f.PlaneH.Clone(),
        };
        int planes = f.Format.PlaneCount();
        for (int p = 0; p < planes; p++)
        {
            var src = f.Planes[p];
            int rowLen = f.Format.IsPlanar() ? f.PlaneW[p] : f.Width * 3;
            int rows = f.PlaneH[p];
            var dst = new ushort[src.Length];
            int half = (rows + 1) / 2;
            for (int r = 0; r < rows; r++)
            {
                int to = (r & 1) == 0 ? r >> 1 : half + (r >> 1);
                Array.Copy(src, (long)r * rowLen, dst, (long)to * rowLen, rowLen);
            }
            o.Planes[p] = dst;
        }
        return o;
    }

    public readonly record struct Locus(
        int StoredX, int StoredY,
        int RegionX, int RegionY,
        int RegionW, int RegionH,
        string Region);

    public static Locus Locate(FrameLayout l, int w, int h, int x, int y)
    {
        var (sx, sy) = l.Splits(w, h);
        switch (l)
        {
            case FrameLayout.StereoSideBySide:
                return x < sx ? new(x, y, x, y, sx, h, "L") : new(x, y, x - sx, y, w - sx, h, "R");
            case FrameLayout.StereoTopBottom:
                return y < sy ? new(x, y, x, y, w, sy, "L") : new(x, y, x, y - sy, w, h - sy, "R");
            case FrameLayout.FieldSeparated:
                return y < sy ? new(x, 2 * y, x, y, w, sy, "top")
                              : new(x, 2 * (y - sy) + 1, x, y - sy, w, h - sy, "bottom");
            default:
                return new(x, y, x, y, w, h, "");
        }
    }

    public static (int bx, int by, int index) Block(in Locus p, int gridW, int gridH)
    {
        gridW = Math.Max(1, gridW); gridH = Math.Max(1, gridH);
        int bx = p.RegionX / gridW, by = p.RegionY / gridH;
        int perRow = (p.RegionW + gridW - 1) / gridW;
        return (bx, by, by * perRow + bx);
    }
}
