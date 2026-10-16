using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Matdol.Core;

public sealed class Y4mFileSource : IFrameSource
{
    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public int FrameCount => _offsets.Count;
    public RawPixelFormat Format { get; }
    public int BitDepth { get; }
    public string Summary { get; }
    public bool? LimitedRangeHint { get; }

    private readonly List<long> _offsets = new();
    private readonly bool _is411, _alpha;
    private readonly int _cw, _ch;
    private readonly long _frameBytes;

    public static bool HasSignature(ReadOnlySpan<byte> h) =>
        h.Length >= 10 && Encoding.ASCII.GetString(h[..10]) == "YUV4MPEG2 ";

    public static bool IsY4m(string path)
    {
        if (System.IO.Path.GetExtension(path).Equals(".y4m", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            Span<byte> h = stackalloc byte[10];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return fs.ReadAtLeast(h, 10, throwOnEndOfStream: false) == 10 && HasSignature(h);
        }
        catch { return false; }
    }

    public Y4mFileSource(string path)
    {
        Path = path;
        using var fs = Open();
        string header = ReadLine(fs, 4096) ?? throw new InvalidDataException("Y4M: empty file");
        if (!header.StartsWith("YUV4MPEG2")) throw new InvalidDataException("Y4M: no YUV4MPEG2 header");
        int w = 0, h = 0; string cs = "420jpeg", fps = "", inter = "", range = "";
        foreach (var tok in header.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok.Length < 2) continue;
            string v = tok[1..];
            switch (tok[0])
            {
                case 'W': w = int.Parse(v, CultureInfo.InvariantCulture); break;
                case 'H': h = int.Parse(v, CultureInfo.InvariantCulture); break;
                case 'C': cs = v; break;
                case 'F': fps = v.Replace(':', '/'); break;
                case 'I': inter = v; break;
                case 'X': if (v.StartsWith("COLORRANGE=", StringComparison.OrdinalIgnoreCase)) range = v[11..].ToUpperInvariant(); break;
            }
        }
        if (w <= 0 || h <= 0 || (long)w * h > 1L << 28) throw new InvalidDataException($"Y4M: bad size {w}x{h}");
        Width = w; Height = h;

        string c = cs.ToLowerInvariant();
        int bits = 8;
        int pi = c.IndexOf('p');
        if (c.StartsWith("mono") && c.Length > 4 && char.IsDigit(c[4])) bits = int.Parse(c[4..], CultureInfo.InvariantCulture);
        else if (pi >= 3 && pi + 1 < c.Length && char.IsDigit(c[pi + 1])) bits = int.Parse(c[(pi + 1)..], CultureInfo.InvariantCulture);
        if (bits < 8 || bits > 16) throw new InvalidDataException($"Y4M: unsupported bit depth in C{cs}");
        BitDepth = bits;
        (int sx, int sy) sub;
        if (c.StartsWith("mono")) { Format = RawPixelFormat.Gray; sub = (0, 0); }
        else if (c.StartsWith("444")) { Format = RawPixelFormat.Yuv444p; sub = (0, 0); _alpha = c.Contains("alpha"); }
        else if (c.StartsWith("422")) { Format = RawPixelFormat.Yuv422p; sub = (1, 0); }
        else if (c.StartsWith("411")) { Format = RawPixelFormat.Yuv444p; sub = (2, 0); _is411 = true; }
        else if (c.StartsWith("420")) { Format = RawPixelFormat.Yuv420p; sub = (1, 1); }
        else throw new InvalidDataException($"Y4M: unsupported colorspace C{cs}");
        _cw = Format == RawPixelFormat.Gray ? 0 : (w + (1 << sub.sx) - 1) >> sub.sx;
        _ch = Format == RawPixelFormat.Gray ? 0 : (h + (1 << sub.sy) - 1) >> sub.sy;
        int bps = bits > 8 ? 2 : 1;
        _frameBytes = ((long)w * h * (_alpha ? 2 : 1) + 2L * _cw * _ch) * bps;
        LimitedRangeHint = range == "FULL" ? false : range == "LIMITED" ? true : null;

        long len = fs.Length;
        while (fs.Position < len)
        {
            string? fl = ReadLine(fs, 1024);
            if (fl == null || !fl.StartsWith("FRAME")) break;
            long start = fs.Position;
            _offsets.Add(start);
            if (start + _frameBytes > len) break;
            fs.Position = start + _frameBytes;
        }
        if (_offsets.Count == 0) throw new InvalidDataException("Y4M: no frames");

        string chroma = c.StartsWith("420") ? "4:2:0" + (c.Contains("paldv") ? " (paldv)" : c.Contains("mpeg2") ? " (mpeg2)" : "")
                      : c.StartsWith("422") ? "4:2:2" : c.StartsWith("411") ? "4:1:1 → 4:4:4" : c.StartsWith("444") ? "4:4:4" + (_alpha ? " +alpha" : "") : "mono";
        string scan = inter switch { "t" => "interlaced TFF", "b" => "interlaced BFF", "m" => "mixed", _ => "progressive" };
        Summary = $"Y4M {chroma} {bits}-bit" + (fps.Length > 0 ? $" · {fps} fps" : "") + $" · {scan}" + (range.Length > 0 ? $" · {range.ToLowerInvariant()} range" : "");
    }

    private FileStream Open() => new(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static string? ReadLine(Stream s, int max)
    {
        var sb = new StringBuilder();
        int b;
        while ((b = s.ReadByte()) != -1)
        {
            if (b == '\n') return sb.ToString();
            if (sb.Length >= max) throw new InvalidDataException("Y4M: header line too long");
            sb.Append((char)b);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    public FrameData ReadFrame(int index)
    {
        index = Math.Clamp(index, 0, FrameCount - 1);
        int w = Width, h = Height;
        var f = new FrameData { Width = w, Height = h, Format = Format, MaxValue = (1 << BitDepth) - 1 };
        using var fs = Open();
        fs.Position = _offsets[index];
        bool wide = BitDepth > 8;
        f.Planes[0] = RawFrameSource.ReadSamples(fs, w * h, wide, lenient: true); f.PlaneW[0] = w; f.PlaneH[0] = h;
        if (Format == RawPixelFormat.Gray) return f;
        var cb = RawFrameSource.ReadSamples(fs, _cw * _ch, wide, lenient: true);
        var cr = RawFrameSource.ReadSamples(fs, _cw * _ch, wide, lenient: true);
        if (_is411)
        {
            cb = Upsample411(cb, _cw, w, h); cr = Upsample411(cr, _cw, w, h);
            f.PlaneW[1] = f.PlaneW[2] = w; f.PlaneH[1] = f.PlaneH[2] = h;
        }
        else { f.PlaneW[1] = f.PlaneW[2] = _cw; f.PlaneH[1] = f.PlaneH[2] = _ch; }
        f.Planes[1] = cb; f.Planes[2] = cr;
        return f;
    }

    private static ushort[] Upsample411(ushort[] c, int cw, int w, int h)
    {
        var d = new ushort[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) d[y * w + x] = c[y * cw + Math.Min(cw - 1, x >> 2)];
        return d;
    }

    public IFrameSource Reopen() => new Y4mFileSource(Path);
    public void Dispose() { }
}
