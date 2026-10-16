using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;

namespace Matdol.Core;

public sealed class FrameData
{
    public int Width, Height;
    public RawPixelFormat Format;
    public int MaxValue = 255;
    public ushort[][] Planes = new ushort[3][];
    public int[] PlaneW = new int[3], PlaneH = new int[3];

    public YuvMatrix Matrix = YuvMatrix.Bt709;
    public bool LimitedRange = true;

    public int BitDepth => MaxValue <= 255 ? 8 : MaxValue <= 1023 ? 10 : MaxValue <= 4095 ? 12 : 16;
    public int HexDigits => MaxValue <= 255 ? 2 : MaxValue <= 4095 ? 3 : 4;

    private byte[]? _lut;
    private byte To8(int v)
    {
        if (MaxValue == 255) return (byte)v;
        _lut ??= BuildLut();
        return _lut[Math.Clamp(v, 0, MaxValue)];
    }
    private byte[] BuildLut()
    {
        var lut = new byte[MaxValue + 1];
        for (int i = 0; i <= MaxValue; i++) lut[i] = (byte)((i * 255 + MaxValue / 2) / MaxValue);
        return lut;
    }

    public (int c0, int c1, int c2) GetPixel(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1); y = Math.Clamp(y, 0, Height - 1);
        return GetPixelFast(x, y);
    }

    private (int, int, int) GetPixelFast(int x, int y)
    {
        if (Format.IsGray()) { int v = Planes[0][y * PlaneW[0] + x]; return (v, v, v); }
        if (!Format.IsPlanar())
        {
            int i = (y * Width + x) * 3;
            var p = Planes[0];
            return Format == RawPixelFormat.Rgb24i ? (p[i], p[i + 1], p[i + 2]) : (p[i + 2], p[i + 1], p[i]);
        }
        var (sx, sy) = Format.ChromaShift();
        int c0 = Planes[0][y * PlaneW[0] + x];
        int c1 = Planes[1][(y >> sy) * PlaneW[1] + (x >> sx)];
        int c2 = Planes[2][(y >> sy) * PlaneW[2] + (x >> sx)];
        return Format switch
        {
            RawPixelFormat.Bgr24p => (c2, c1, c0),
            RawPixelFormat.Gbr24p => (c2, c0, c1),
            _ => (c0, c1, c2),
        };
    }

    public (byte r, byte g, byte b) GetDisplayRgb(int x, int y, ChannelView view = ChannelView.All, bool colorChannel = false)
    {
        var (c0, c1, c2) = GetPixel(x, y);
        return ConvertToDisplay(c0, c1, c2, view, colorChannel);
    }

    public (byte r, byte g, byte b) ConvertToDisplay(int c0, int c1, int c2, ChannelView view, bool colorChannel)
    {
        if (Format.IsYuv() && (view == ChannelView.All || (colorChannel && view != ChannelView.Ch0)))
        {
            int mid = (MaxValue + 1) >> 1;
            if (view == ChannelView.Ch1) { c0 = mid; c2 = mid; }
            else if (view == ChannelView.Ch2) { c0 = mid; c1 = mid; }
            return YuvToRgb8(c0, c1, c2);
        }
        byte a0 = To8(c0), a1 = To8(c1), a2 = To8(c2);
        if (view == ChannelView.All || Format.IsGray()) return (a0, a1, a2);
        byte v = view == ChannelView.Ch0 ? a0 : view == ChannelView.Ch1 ? a1 : a2;
        if (colorChannel && !Format.IsYuv())
            return (view == ChannelView.Ch0 ? v : (byte)0, view == ChannelView.Ch1 ? v : (byte)0, view == ChannelView.Ch2 ? v : (byte)0);
        return (v, v, v);
    }

    public unsafe void ToBgra(Span<byte> dst, ChannelView view, bool colorChannel)
    {
        fixed (byte* d0 = dst)
        {
            for (int y = 0; y < Height; y++)
            {
                byte* d = d0 + (long)y * Width * 4;
                for (int x = 0; x < Width; x++, d += 4)
                {
                    var (c0, c1, c2) = GetPixelFast(x, y);
                    var (r, g, b) = ConvertToDisplay(c0, c1, c2, view, colorChannel);
                    d[0] = b; d[1] = g; d[2] = r; d[3] = 255;
                }
            }
        }
    }

    private int[]? _cY, _cRU, _cRV, _cGU, _cGV, _cBU, _cBV;
    private (YuvMatrix, bool, int) _cKey;

    public (byte r, byte g, byte b) YuvToRgb8(int y, int u, int v)
    {
        if (_cY == null || _cKey != (Matrix, LimitedRange, MaxValue)) BuildCsc();
        y = Math.Clamp(y, 0, MaxValue); u = Math.Clamp(u, 0, MaxValue); v = Math.Clamp(v, 0, MaxValue);
        int yy = _cY![y];
        int r = (yy + _cRU![u] + _cRV![v] + 32768) >> 16;
        int g = (yy + _cGU![u] + _cGV![v] + 32768) >> 16;
        int b = (yy + _cBU![u] + _cBV![v] + 32768) >> 16;
        return ((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
    }

    private void BuildCsc()
    {
        int n = MaxValue + 1;
        double s = n / 256.0;
        double mid = n / 2.0;
        double yOff = LimitedRange ? 16 * s : 0, yRng = LimitedRange ? 219 * s : MaxValue;
        double cRng = LimitedRange ? 224 * s : MaxValue;
        double rU, rV, gU, gV, bU, bV;
        if (Matrix == YuvMatrix.YCoCg)
        {
            rU = 1; rV = -1; gU = 0; gV = 1; bU = -1; bV = -1;
        }
        else
        {
            var (kr, kb) = Matrix.Coefficients(); double kg = 1 - kr - kb;
            rU = 0; rV = 2 * (1 - kr);
            gU = -2 * kb * (1 - kb) / kg; gV = -2 * kr * (1 - kr) / kg;
            bU = 2 * (1 - kb); bV = 0;
        }
        const double F = 255.0 * 65536;
        _cY = new int[n]; _cRU = new int[n]; _cRV = new int[n]; _cGU = new int[n]; _cGV = new int[n]; _cBU = new int[n]; _cBV = new int[n];
        for (int i = 0; i < n; i++)
        {
            double yn = (i - yOff) / yRng, cn = (i - mid) / cRng;
            _cY[i] = (int)Math.Round(yn * F);
            _cRU[i] = (int)Math.Round(rU * cn * F); _cRV[i] = (int)Math.Round(rV * cn * F);
            _cGU[i] = (int)Math.Round(gU * cn * F); _cGV[i] = (int)Math.Round(gV * cn * F);
            _cBU[i] = (int)Math.Round(bU * cn * F); _cBV[i] = (int)Math.Round(bV * cn * F);
        }
        _cKey = (Matrix, LimitedRange, MaxValue);
    }
}

public interface IFrameSource : IDisposable
{
    string Path { get; }
    int Width { get; }
    int Height { get; }
    int FrameCount { get; }
    RawPixelFormat Format { get; }
    int BitDepth { get; }
    FrameData ReadFrame(int index);
    IFrameSource Reopen();
}

public sealed class RawFrameSource : IFrameSource
{
    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public int FrameCount { get; }
    public RawPixelFormat Format { get; }
    public int BitDepth { get; }
    private readonly long _frameBytes;

    public RawFrameSource(string path, RawPixelFormat format, int width, int height, int bitDepth = 8)
    {
        Path = path; Format = format; Width = width; Height = height; BitDepth = bitDepth;
        _frameBytes = format.FrameBytes(width, height, bitDepth);
        long size = new FileInfo(path).Length;
        FrameCount = (int)Math.Max(1, size / _frameBytes);
    }

    public FrameData ReadFrame(int index)
    {
        index = Math.Clamp(index, 0, FrameCount - 1);
        var f = new FrameData { Width = Width, Height = Height, Format = Format, MaxValue = (1 << BitDepth) - 1 };
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(index * _frameBytes, SeekOrigin.Begin);

        var (sx, sy) = Format.ChromaShift();
        int planes = Format.PlaneCount();
        for (int p = 0; p < planes; p++)
        {
            int pw = p == 0 ? Width : Width >> sx;
            int ph = p == 0 ? Height : Height >> sy;
            int samples = pw * ph * (Format.IsPlanar() ? 1 : 3);
            f.PlaneW[p] = pw; f.PlaneH[p] = ph;
            f.Planes[p] = ReadSamples(fs, samples, BitDepth > 8, lenient: true);
        }
        return f;
    }

    internal static ushort[] ReadSamples(Stream s, int count, bool wide, bool bigEndian = false, bool lenient = false)
    {
        var dst = new ushort[count];
        if (!wide)
        {
            var buf = new byte[count];
            if (lenient) s.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false); else s.ReadExactly(buf);
            for (int i = 0; i < count; i++) dst[i] = buf[i];
        }
        else
        {
            var buf = new byte[count * 2];
            if (lenient) s.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false); else s.ReadExactly(buf);
            if (bigEndian) for (int i = 0; i < count; i++) dst[i] = (ushort)((buf[2 * i] << 8) | buf[2 * i + 1]);
            else for (int i = 0; i < count; i++) dst[i] = (ushort)(buf[2 * i] | (buf[2 * i + 1] << 8));
        }
        return dst;
    }

    public IFrameSource Reopen() => new RawFrameSource(Path, Format, Width, Height, BitDepth);
    public void Dispose() { }
}

public sealed class ImageFileSource : IFrameSource
{
    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public int FrameCount => 1;
    public RawPixelFormat Format => RawPixelFormat.Rgb24i;
    public int BitDepth => 8;
    private readonly FrameData _frame;

    public ImageFileSource(string path)
    {
        Path = path;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bmp = SKBitmap.Decode(fs) ?? throw new InvalidDataException("image decode failed: " + path);
        Width = bmp.Width; Height = bmp.Height;
        var rgb = new ushort[Width * Height * 3];
        var px = bmp.Pixels;
        for (int i = 0, j = 0; i < px.Length; i++, j += 3)
        { rgb[j] = px[i].Red; rgb[j + 1] = px[i].Green; rgb[j + 2] = px[i].Blue; }
        _frame = new FrameData { Width = Width, Height = Height, Format = Format };
        _frame.Planes[0] = rgb; _frame.PlaneW[0] = Width; _frame.PlaneH[0] = Height;
    }

    public FrameData ReadFrame(int index) => _frame;
    public IFrameSource Reopen() => new ImageFileSource(Path);
    public void Dispose() { }
}

public sealed class PnmFileSource : IFrameSource
{
    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public int FrameCount => 1;
    public RawPixelFormat Format { get; }
    public int BitDepth => _frame.BitDepth;
    private readonly FrameData _frame;

    public PnmFileSource(string path)
    {
        Path = path;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var magic = ReadToken(fs);
        if (magic.Length != 2 || magic[0] != 'P' || magic[1] < '1' || magic[1] > '6')
            throw new InvalidDataException("not a PNM header: " + magic);
        int kind = magic[1] - '0';
        Width = int.Parse(ReadToken(fs)); Height = int.Parse(ReadToken(fs));
        int maxval = (kind == 1 || kind == 4) ? 1 : int.Parse(ReadToken(fs));

        bool color = kind == 3 || kind == 6;
        Format = color ? RawPixelFormat.Rgb24i : RawPixelFormat.Gray;
        int n = Width * Height * (color ? 3 : 1);
        ushort[] data;

        switch (kind)
        {
            case 1:
                data = new ushort[n];
                for (int i = 0; i < n; i++) { int c; do c = fs.ReadByte(); while (c != '0' && c != '1' && c != -1); data[i] = (ushort)(c == '1' ? 0 : 1); }
                break;
            case 4:
                data = new ushort[n];
                int rowBytes = (Width + 7) / 8;
                var row = new byte[rowBytes];
                for (int y = 0; y < Height; y++)
                {
                    fs.ReadExactly(row);
                    for (int x = 0; x < Width; x++) data[y * Width + x] = (ushort)(((row[x >> 3] >> (7 - (x & 7))) & 1) == 1 ? 0 : 1);
                }
                break;
            case 2: case 3:
                data = new ushort[n];
                for (int i = 0; i < n; i++) data[i] = (ushort)int.Parse(ReadToken(fs));
                break;
            default:
                data = RawFrameSource.ReadSamples(fs, n, maxval > 255, bigEndian: true);
                break;
        }

        _frame = new FrameData { Width = Width, Height = Height, Format = Format, MaxValue = maxval };
        _frame.Planes[0] = data; _frame.PlaneW[0] = Width; _frame.PlaneH[0] = Height;
    }

    private static string ReadToken(Stream s)
    {
        var sb = new StringBuilder();
        int c;
        while ((c = s.ReadByte()) != -1)
        {
            if (c == '#') { while (c != '\n' && c != '\r' && c != -1) c = s.ReadByte(); continue; }
            if (char.IsWhiteSpace((char)c)) { if (sb.Length > 0) break; continue; }
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    public FrameData ReadFrame(int index) => _frame;
    public IFrameSource Reopen() => new PnmFileSource(Path);
    public void Dispose() { }
}

public static class SizeEstimator
{
    public static (int w, int h)? Estimate(string path, RawPixelFormat fmt, int bitDepth, int mulW = 1, int mulH = 1)
    {
        long size = new FileInfo(path).Length;
        var cands = new List<(int w, int h)>();
        foreach (var p in SizePreset.Table)
        {
            int w = p.Width * mulW, h = p.Height * mulH;
            if (size % fmt.FrameBytes(w, h, bitDepth) == 0) cands.Add((w, h));
        }
        if (cands.Count == 0) return null;
        if (cands.Count == 1) return cands[0];

        (int w, int h)? best = null; double bestScore = double.MaxValue;
        foreach (var c in cands)
        {
            var frame = new RawFrameSource(path, fmt, c.w, c.h, bitDepth).ReadFrame(0);
            double score = Roughness(frame);
            if (score < bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    private static double Roughness(FrameData f)
    {
        long sum = 0; int n = 0;
        int step = Math.Max(1, f.Height / 256);
        for (int y = 1; y < f.Height; y += step)
            for (int x = 1; x < f.Width; x += 4)
            {
                var (a, _, _) = f.GetPixel(x, y);
                var (l, _, _) = f.GetPixel(x - 1, y);
                var (u, _, _) = f.GetPixel(x, y - 1);
                sum += Math.Abs(a - l) + Math.Abs(a - u); n++;
            }
        return n == 0 ? double.MaxValue : (double)sum / n;
    }
}
