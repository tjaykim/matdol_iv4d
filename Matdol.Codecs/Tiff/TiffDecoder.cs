using System;
using System.Collections.Generic;
using System.Linq;
using Matdol.Codecs.Raw;

namespace Matdol.Codecs.Tiff;

public sealed record JpegPixels(int Width, int Height, int Channels, byte[] Data);
public delegate JpegPixels? JpegDecodeHook(byte[] jpeg);

public static class TiffDecoder
{
    public static bool HasSignature(ReadOnlySpan<byte> head) => TiffReader.HasSignature(head);

    public static List<TiffIfd> Pages(TiffReader r)
    {
        var chain = r.ReadChain(r.FirstIfd);
        var pages = chain.Where(i => HasImage(i) && (i.Long(254) & 1) == 0).ToList();
        if (pages.Count == 0) pages = chain.Where(HasImage).ToList();
        if (pages.Count == 0) throw new CodecFormatException("TIFF: no image data");
        return pages;
    }

    internal static bool HasImage(TiffIfd i) => (i.Has(273) || i.Has(324)) && i.Long(256) > 0 && i.Long(257) > 0;

    public static int CountPages(byte[] file) => Pages(new TiffReader(file)).Count;

    public static CodecImage Decode(byte[] file, int page = 0, JpegDecodeHook? jpeg = null)
    {
        var r = new TiffReader(file);
        var pages = Pages(r);
        page = Math.Clamp(page, 0, pages.Count - 1);
        var img = DecodeIfd(pages[page], jpeg, applyOrientation: true);
        if (pages.Count > 1)
            return With(img, img.Summary + $" · page {page + 1}/{pages.Count}");
        return img;
    }

    private static CodecImage With(CodecImage i, string summary) => new()
    {
        Width = i.Width, Height = i.Height, BitDepth = i.BitDepth, Model = i.Model, Planes = i.Planes,
        PlaneWidth = i.PlaneWidth, PlaneHeight = i.PlaneHeight, LimitedRangeHint = i.LimitedRangeHint,
        MatrixHint = i.MatrixHint, Summary = summary, Details = i.Details,
    };

    public sealed class Samples
    {
        public int Width, Height, Spp, Bits;
        public int SampleFormat = 1;
        public int OutBits;
        public ushort[] Data = Array.Empty<ushort>();
        public bool JpegRgb;
        public string Compression = "";
        public string Layout = "";
    }

    public static Samples ReadSamples(TiffIfd ifd, JpegDecodeHook? jpeg = null)
    {
        var R = ifd.R;
        int w = (int)ifd.Long(256), h = (int)ifd.Long(257);
        if (w <= 0 || h <= 0 || (long)w * h > 1L << 30) throw new CodecFormatException($"TIFF: bad image size {w}x{h}");
        int spp = (int)Math.Max(1, ifd.Long(277, 1));
        var bpsArr = ifd.Longs(258);
        int bits = bpsArr.Length > 0 ? (int)bpsArr[0] : 1;
        int fmt = (int)ifd.Long(339, 1);
        int comp = (int)ifd.Long(259, 1);
        int planar = (int)ifd.Long(284, 1);
        int pred = (int)ifd.Long(317, 1);
        if (spp > 16) throw new CodecFormatException("TIFF: too many samples per pixel");
        if (bits < 1 || bits > 64) throw new CodecFormatException($"TIFF: unsupported bits per sample {bits}");
        if (fmt == 3 && bits != 16 && bits != 24 && bits != 32 && bits != 64) throw new CodecFormatException($"TIFF: unsupported float size {bits}");
        var s = new Samples
        {
            Width = w, Height = h, Spp = spp, Bits = bits, SampleFormat = fmt,
            OutBits = fmt == 3 || bits > 16 ? 16 : bits,
            Compression = comp switch
            {
                1 => "uncompressed", 5 => "LZW", 8 or 32946 => "Deflate", 32773 => "PackBits", 7 => "JPEG", 6 => "old JPEG",
                2 => "CCITT RLE", 3 => "CCITT G3", 4 => "CCITT G4", 34712 => "JPEG 2000", 50000 => "ZSTD", 34925 => "LZMA",
                34892 => "lossy JPEG (DNG)", 50001 => "WebP", 52546 => "JPEG XL", _ => $"compression {comp}",
            } + (pred == 2 ? " + predictor" : pred == 3 ? " + FP predictor" : ""),
        };
        s.Data = new ushort[(long)w * h * spp];

        if (comp == 6) { ReadOldJpeg(ifd, jpeg, s); return s; }
        if (comp is not (1 or 5 or 8 or 32946 or 32773 or 7))
            throw new CodecFormatException("TIFF: unsupported compression (" + s.Compression + ")");

        bool tiled = ifd.Has(322) && ifd.Has(324);
        int cw, ch;
        long[] offs, cnts;
        if (tiled)
        {
            cw = (int)ifd.Long(322); ch = (int)ifd.Long(323);
            offs = ifd.Longs(324); cnts = ifd.Longs(325);
            s.Layout = $"tiled {cw}×{ch}";
        }
        else
        {
            cw = w; ch = (int)Math.Min(h, ifd.Long(278, h)); if (ch <= 0) ch = h;
            offs = ifd.Longs(273); cnts = ifd.Longs(279);
            s.Layout = offs.Length > 1 ? $"{offs.Length} strips" : "1 strip";
        }
        if (cw <= 0 || ch <= 0) throw new CodecFormatException("TIFF: bad strip/tile size");
        int across = (w + cw - 1) / cw, down = (h + ch - 1) / ch;
        int planes = planar == 2 ? spp : 1, chunkSpp = planar == 2 ? 1 : spp;
        if (planar == 2 && spp > 1) s.Layout += ", planar";
        int perPlane = across * down;
        if (offs.Length < perPlane * planes) throw new CodecFormatException("TIFF: missing strip/tile offsets");
        if (cnts.Length < offs.Length)
        {
            var c2 = new long[offs.Length];
            for (int i = 0; i < offs.Length; i++) c2[i] = i < cnts.Length ? cnts[i] : Math.Max(0, R.Data.Length - R.Base - offs[i]);
            cnts = c2;
        }
        byte[]? jpegTables = comp == 7 && ifd.Has(347) ? ifd.Bytes(347) : null;

        for (int pl = 0; pl < planes; pl++)
            for (int ty = 0; ty < down; ty++)
                for (int tx = 0; tx < across; tx++)
                {
                    int k = pl * perPlane + ty * across + tx;
                    long off = offs[k], cnt = cnts[k];
                    if (cnt <= 0 || !R.InRange(off, 1)) continue;
                    cnt = Math.Min(cnt, R.Data.Length - R.Base - off);
                    int rows = tiled ? ch : Math.Min(ch, h - ty * ch);
                    int x0 = tx * cw, y0 = ty * ch;
                    int bodyBase = (int)(R.Base + off);
                    if (comp == 7)
                    {
                        DecodeJpegChunk(R.Data.AsSpan(bodyBase, (int)cnt), jpegTables, jpeg, s, x0, y0, cw, rows, chunkSpp, pl);
                        continue;
                    }
                    long rowBytes = ((long)cw * chunkSpp * bits + 7) / 8;
                    int expected = (int)(rowBytes * rows);
                    byte[] raw = comp switch
                    {
                        1 => Slice(R.Data, bodyBase, (int)cnt, expected),
                        5 => TiffCompression.Lzw(R.Data.AsSpan(bodyBase, (int)cnt), expected),
                        32773 => TiffCompression.PackBits(R.Data.AsSpan(bodyBase, (int)cnt), expected),
                        _ => TiffCompression.Deflate(R.Data, bodyBase, (int)cnt, expected),
                    };
                    Unpack(raw, (int)rowBytes, rows, cw, chunkSpp, bits, fmt, pred, R.LittleEndian, s, x0, y0, pl);
                }
        return s;
    }

    private static byte[] Slice(byte[] d, int pos, int cnt, int expected)
    {
        var b = new byte[expected];
        Array.Copy(d, pos, b, 0, Math.Min(cnt, expected));
        return b;
    }

    private static void Unpack(byte[] raw, int rowBytes, int rows, int cw, int cspp, int bits, int fmt, int pred, bool le,
                               Samples s, int x0, int y0, int plane)
    {
        int n = cw * cspp;
        var line = new long[n];
        var flt = fmt == 3 ? new double[n] : null;
        int spp = s.Spp, w = s.Width, h = s.Height;
        int bytesPer = bits / 8;
        for (int r = 0; r < rows; r++)
        {
            int y = y0 + r;
            if (y >= h) break;
            int ro = r * rowBytes;
            if (fmt == 3)
            {
                if (pred == 3) FloatPredictorRow(raw, ro, n, bytesPer, flt!, cspp);
                else for (int i = 0; i < n; i++) flt![i] = ReadFloat(raw, ro + i * bytesPer, bytesPer, le);
                if (pred == 2) { }
            }
            else
            {
                if (bits == 8) for (int i = 0; i < n; i++) line[i] = raw[ro + i];
                else if (bits == 16)
                    for (int i = 0; i < n; i++) { int q = ro + 2 * i; line[i] = le ? raw[q] | raw[q + 1] << 8 : raw[q] << 8 | raw[q + 1]; }
                else if (bits == 32)
                    for (int i = 0; i < n; i++)
                    {
                        int q = ro + 4 * i;
                        line[i] = le ? (uint)(raw[q] | raw[q + 1] << 8 | raw[q + 2] << 16 | raw[q + 3] << 24)
                                     : (uint)(raw[q] << 24 | raw[q + 1] << 16 | raw[q + 2] << 8 | raw[q + 3]);
                    }
                else
                {
                    long bp = (long)ro * 8;
                    for (int i = 0; i < n; i++, bp += bits) line[i] = ReadBits(raw, bp, bits);
                }
                if (pred == 2)
                {
                    long mask = bits >= 64 ? -1 : (1L << bits) - 1;
                    for (int i = cspp; i < n; i++) line[i] = (line[i] + line[i - cspp]) & mask;
                }
                if (fmt == 2 && bits < 64) for (int i = 0; i < n; i++) line[i] ^= 1L << (bits - 1);
            }
            for (int x = 0; x < cw; x++)
            {
                int X = x0 + x;
                if (X >= w) break;
                long dst = ((long)y * w + X) * spp + plane;
                for (int c = 0; c < cspp; c++)
                {
                    int i = x * cspp + c;
                    ushort v;
                    if (fmt == 3) { double f = flt![i]; v = (ushort)(double.IsNaN(f) ? 0 : Math.Clamp(f, 0, 1) * 65535 + 0.5); }
                    else if (bits > 16) v = (ushort)(line[i] >> (bits - 16));
                    else v = (ushort)line[i];
                    s.Data[dst + c] = v;
                }
            }
        }
    }

    private static long ReadBits(byte[] b, long bitPos, int n)
    {
        long v = 0;
        for (int k = 0; k < n; )
        {
            int byteIdx = (int)(bitPos >> 3), bit = (int)(bitPos & 7);
            if (byteIdx >= b.Length) { v <<= n - k; break; }
            int take = Math.Min(8 - bit, n - k);
            int bitsVal = (b[byteIdx] >> (8 - bit - take)) & ((1 << take) - 1);
            v = (v << take) | (long)bitsVal;
            k += take; bitPos += take;
        }
        return v;
    }

    private static double ReadFloat(byte[] b, int p, int bytes, bool le)
    {
        if (p + bytes > b.Length) return 0;
        ulong u = 0;
        for (int i = 0; i < bytes; i++) u = le ? u | (ulong)b[p + i] << (8 * i) : u << 8 | b[p + i];
        return FromBits(u, bytes);
    }

    private static double FromBits(ulong u, int bytes) => bytes switch
    {
        2 => (double)BitConverter.UInt16BitsToHalf((ushort)u),
        3 => BitConverter.Int32BitsToSingle((int)(u << 8)),
        4 => BitConverter.Int32BitsToSingle((int)u),
        _ => BitConverter.Int64BitsToDouble((long)u),
    };

    private static void FloatPredictorRow(byte[] raw, int ro, int n, int bytesPer, double[] dst, int stride)
    {
        int wc = n * bytesPer;
        if (ro + wc > raw.Length) { Array.Clear(dst); return; }
        var t = new byte[wc];
        Array.Copy(raw, ro, t, 0, wc);
        for (int i = stride; i < wc; i++) t[i] = (byte)(t[i] + t[i - stride]);
        for (int i = 0; i < n; i++)
        {
            ulong u = 0;
            for (int k = 0; k < bytesPer; k++) u = u << 8 | t[k * n + i];
            dst[i] = FromBits(u, bytesPer);
        }
    }

    private static void DecodeJpegChunk(ReadOnlySpan<byte> body, byte[]? tables, JpegDecodeHook? hook, Samples s,
                                        int x0, int y0, int cw, int rows, int cspp, int plane)
    {
        if (IsLossless(body, tables))
        {
            var lj = LosslessJpeg.Decode(Merge(tables, body));
            s.OutBits = s.Bits = Math.Max(1, lj.Precision);
            long total = (long)lj.Width * lj.Components;
            int tileRowSamples = cw * cspp;
            for (long k = 0; k < lj.Data.Length; k++)
            {
                long ty = k / tileRowSamples, tx = k % tileRowSamples;
                int y = (int)(y0 + ty), x = (int)(x0 + tx / cspp), c = (int)(tx % cspp);
                if (ty >= rows) break;
                if (y >= s.Height || x >= s.Width) continue;
                s.Data[((long)y * s.Width + x) * s.Spp + plane + c] = lj.Data[k];
            }
            _ = total;
            return;
        }
        if (hook == null) throw new CodecFormatException("TIFF: JPEG compression needs an image decoder");
        var px = hook(Merge(tables, body).ToArray()) ?? throw new CodecFormatException("TIFF: JPEG tile decode failed");
        s.JpegRgb = px.Channels >= 3;
        s.OutBits = s.Bits = 8;
        for (int r = 0; r < Math.Min(rows, px.Height); r++)
        {
            int y = y0 + r; if (y >= s.Height) break;
            for (int x = 0; x < Math.Min(cw, px.Width); x++)
            {
                int X = x0 + x; if (X >= s.Width) break;
                long d = ((long)y * s.Width + X) * s.Spp + plane;
                int q = (r * px.Width + x) * px.Channels;
                for (int c = 0; c < Math.Min(cspp, px.Channels); c++) s.Data[d + c] = px.Data[q + c];
            }
        }
    }

    private static bool IsLossless(ReadOnlySpan<byte> body, byte[]? tables)
    {
        static int Sof(ReadOnlySpan<byte> b)
        {
            for (int i = 0; i + 1 < b.Length; i++)
                if (b[i] == 0xFF && b[i + 1] >= 0xC0 && b[i + 1] <= 0xCF && b[i + 1] != 0xC4 && b[i + 1] != 0xC8 && b[i + 1] != 0xCC)
                    return b[i + 1];
            return -1;
        }
        int m = Sof(body.Slice(0, Math.Min(body.Length, 4096)));
        return m == 0xC3;
    }

    private static ReadOnlySpan<byte> Merge(byte[]? tables, ReadOnlySpan<byte> body)
    {
        if (tables == null || tables.Length < 4) return body;
        int tl = tables.Length;
        if (tables[tl - 2] == 0xFF && tables[tl - 1] == 0xD9) tl -= 2;
        int skip = body.Length >= 2 && body[0] == 0xFF && body[1] == 0xD8 ? 2 : 0;
        var m = new byte[tl + body.Length - skip];
        Array.Copy(tables, m, tl);
        body.Slice(skip).CopyTo(m.AsSpan(tl));
        return m;
    }

    private static void ReadOldJpeg(TiffIfd ifd, JpegDecodeHook? hook, Samples s)
    {
        long o = ifd.Long(513), n = ifd.Long(514);
        if (hook == null || o <= 0 || n <= 0 || !ifd.R.InRange(o, n)) throw new CodecFormatException("TIFF: old-style JPEG not supported");
        var px = hook(ifd.R.Data.AsSpan((int)(ifd.R.Base + o), (int)n).ToArray()) ?? throw new CodecFormatException("TIFF: JPEG decode failed");
        s.JpegRgb = px.Channels >= 3; s.OutBits = s.Bits = 8;
        for (int y = 0; y < Math.Min(s.Height, px.Height); y++)
            for (int x = 0; x < Math.Min(s.Width, px.Width); x++)
                for (int c = 0; c < Math.Min(s.Spp, px.Channels); c++)
                    s.Data[((long)y * s.Width + x) * s.Spp + c] = px.Data[(y * px.Width + x) * px.Channels + c];
    }

    public static CodecImage DecodeIfd(TiffIfd ifd, JpegDecodeHook? jpeg, bool applyOrientation, string label = "TIFF")
    {
        int pred = (int)ifd.Long(317, 1), spp0 = (int)Math.Max(1, ifd.Long(277, 1));
        var bpsArr = ifd.Longs(258);
        int photo = (int)ifd.Long(262, spp0 >= 3 ? 2 : 1);
        int comp = (int)ifd.Long(259, 1);
        var sub = ifd.Longs(530);
        int sh = sub.Length >= 2 ? (int)sub[0] : 2, sv = sub.Length >= 2 ? (int)sub[1] : 2;
        CodecImage img;
        Samples? smp = null;
        if (photo == 6 && comp != 7 && comp != 6 && (sh > 1 || sv > 1)) img = DecodeYcbcrSubsampled(ifd, sh, sv);
        else
        {
            smp = ReadSamples(ifd, jpeg);
            img = Interpret(ifd, smp, photo);
        }
        if (applyOrientation)
        {
            int orient = (int)ifd.Long(274, 1);
            if (orient > 1 && orient <= 8) img = Orient(img, orient);
        }
        string bitsTxt = smp == null ? $"{(bpsArr.Length > 0 ? bpsArr[0] : 8)}-bit"
            : smp.SampleFormat == 3 ? $"{smp.Bits}-bit float" : smp.SampleFormat == 2 ? $"{smp.Bits}-bit signed" : $"{smp.Bits}-bit";
        string photoTxt = photo switch
        {
            0 => "gray (white = 0)", 1 => "gray", 2 => "RGB", 3 => "palette", 4 => "mask", 5 => "CMYK", 6 => "YCbCr",
            8 => "CIELab", 32803 => "CFA", 34892 => "linear raw", _ => $"photometric {photo}",
        };
        string compTxt = smp?.Compression ?? (comp == 1 ? "uncompressed" : $"compression {comp}");
        var details = new List<KeyValuePair<string, string>>
        {
            new("Size", $"{ifd.Long(256)} x {ifd.Long(257)}"),
            new("Samples/pixel", spp0.ToString()),
            new("Bits/sample", string.Join(",", bpsArr)),
            new("Photometric", photoTxt),
            new("Compression", compTxt),
        };
        if (smp != null) details.Add(new("Layout", smp.Layout));
        int o2 = (int)ifd.Long(274, 1); if (o2 != 1) details.Add(new("Orientation", o2.ToString()));
        string summary = $"{label} {bitsTxt} {photoTxt} · {compTxt}";
        return new CodecImage
        {
            Width = img.Width, Height = img.Height, BitDepth = img.BitDepth, Model = img.Model, Planes = img.Planes,
            PlaneWidth = img.PlaneWidth, PlaneHeight = img.PlaneHeight, MatrixHint = img.MatrixHint, LimitedRangeHint = img.LimitedRangeHint,
            Summary = summary, Details = details,
        };
    }

    private static CodecImage Interpret(TiffIfd ifd, Samples s, int photo)
    {
        int w = s.Width, h = s.Height, spp = s.Spp, n = w * h;
        int bits = s.OutBits;
        int max = (1 << bits) - 1;
        ushort[] Plane(int c, Func<ushort, ushort>? f = null)
        {
            var p = new ushort[n];
            for (int i = 0; i < n; i++) { ushort v = s.Data[(long)i * spp + c]; p[i] = f == null ? v : f(v); }
            return p;
        }
        CodecImage Gray(ushort[] g, int b) => new() { Width = w, Height = h, BitDepth = b, Model = CodecColorModel.Gray, Planes = new[] { g }, PlaneWidth = new[] { w }, PlaneHeight = new[] { h } };
        CodecImage Rgb(ushort[] r, ushort[] g, ushort[] b, int bd) => new()
        {
            Width = w, Height = h, BitDepth = bd, Model = CodecColorModel.Rgb, Planes = new[] { r, g, b },
            PlaneWidth = new[] { w, w, w }, PlaneHeight = new[] { h, h, h },
        };

        if (s.JpegRgb) return spp >= 3 ? Rgb(Plane(0), Plane(1), Plane(2), 8) : Gray(Plane(0), 8);
        switch (photo)
        {
            case 0: return Gray(Plane(0, v => (ushort)(max - v)), bits);
            case 3:
                {
                    var map = ifd.Longs(320);
                    int entries = 1 << Math.Min(16, s.Bits);
                    if (map.Length < 3 * entries) throw new CodecFormatException("TIFF: bad color map");
                    bool deep = false;
                    for (int i = 0; i < map.Length && !deep; i++) if ((map[i] & 0xFF) != (map[i] >> 8)) deep = true;
                    int sh = deep ? 0 : 8;
                    var r = new ushort[n]; var g = new ushort[n]; var b = new ushort[n];
                    for (int i = 0; i < n; i++)
                    {
                        int idx = Math.Min(entries - 1, s.Data[(long)i * spp]);
                        r[i] = (ushort)(map[idx] >> sh); g[i] = (ushort)(map[entries + idx] >> sh); b[i] = (ushort)(map[2 * entries + idx] >> sh);
                    }
                    return Rgb(r, g, b, deep ? 16 : 8);
                }
            case 5:
                {
                    if (spp < 4) return Gray(Plane(0, v => (ushort)(max - v)), bits);
                    var r = new ushort[n]; var g = new ushort[n]; var b = new ushort[n];
                    for (int i = 0; i < n; i++)
                    {
                        long q = (long)i * spp; int k = max - s.Data[q + 3];
                        r[i] = (ushort)((max - s.Data[q]) * k / max); g[i] = (ushort)((max - s.Data[q + 1]) * k / max); b[i] = (ushort)((max - s.Data[q + 2]) * k / max);
                    }
                    return Rgb(r, g, b, bits);
                }
            case 6:
                {
                    if (spp < 3) return Gray(Plane(0), bits);
                    var ref6 = ifd.Doubles(532);
                    var img = new CodecImage
                    {
                        Width = w, Height = h, BitDepth = bits, Model = CodecColorModel.YCbCr444, Planes = new[] { Plane(0), Plane(1), Plane(2) },
                        PlaneWidth = new[] { w, w, w }, PlaneHeight = new[] { h, h, h }, MatrixHint = MatrixOf(ifd), LimitedRangeHint = LimitedOf(ref6, bits),
                    };
                    return img;
                }
            case 8: throw new CodecFormatException("TIFF: CIELab is not supported");
            default:
                if (spp >= 3 && photo != 1 && photo != 32803) return Rgb(Plane(0), Plane(1), Plane(2), bits);
                return Gray(Plane(0), bits);
        }
    }

    private static CodecMatrixHint MatrixOf(TiffIfd ifd)
    {
        var k = ifd.Doubles(529);
        if (k.Length < 3) return CodecMatrixHint.Bt601;
        if (Math.Abs(k[0] - 0.2126) < 0.002 && Math.Abs(k[2] - 0.0722) < 0.002) return CodecMatrixHint.Bt709;
        if (Math.Abs(k[0] - 0.2627) < 0.002 && Math.Abs(k[2] - 0.0593) < 0.002) return CodecMatrixHint.Bt2020;
        return CodecMatrixHint.Bt601;
    }

    private static bool? LimitedOf(double[] r, int bits)
    {
        if (r.Length < 2) return false;
        double s = ((1 << bits) - 1) / 255.0;
        return Math.Abs(r[0] - 16 * s) < 2 * s && Math.Abs(r[1] - 235 * s) < 2 * s;
    }

    private static CodecImage DecodeYcbcrSubsampled(TiffIfd ifd, int sh, int sv)
    {
        var R = ifd.R;
        int w = (int)ifd.Long(256), h = (int)ifd.Long(257);
        int bits = (int)(ifd.Longs(258).FirstOrDefault(8));
        int comp = (int)ifd.Long(259, 1);
        if (bits != 8 || !(sh is 1 or 2 or 4) || !(sv is 1 or 2 or 4)) throw new CodecFormatException("TIFF: unsupported YCbCr subsampling");
        if (ifd.Has(322)) throw new CodecFormatException("TIFF: tiled subsampled YCbCr not supported");
        int bw = (w + sh - 1) / sh, bh = (h + sv - 1) / sv;
        int unit = sh * sv + 2;
        int rps = (int)Math.Min(h, ifd.Long(278, h)); if (rps <= 0) rps = h;
        var offs = ifd.Longs(273); var cnts = ifd.Longs(279);
        var Y = new ushort[w * h]; var Cb = new ushort[bw * bh]; var Cr = new ushort[bw * bh];
        int blockRowsPerStrip = (rps + sv - 1) / sv;
        for (int si = 0; si < offs.Length; si++)
        {
            int by0 = si * blockRowsPerStrip; if (by0 >= bh) break;
            int brows = Math.Min(blockRowsPerStrip, bh - by0);
            int expected = brows * bw * unit;
            long off = offs[si], cnt = si < cnts.Length ? cnts[si] : expected;
            if (!R.InRange(off, 1)) continue;
            cnt = Math.Min(cnt, R.Data.Length - R.Base - off);
            int p0 = (int)(R.Base + off);
            byte[] raw = comp switch
            {
                1 => Slice(R.Data, p0, (int)cnt, expected),
                5 => TiffCompression.Lzw(R.Data.AsSpan(p0, (int)cnt), expected),
                32773 => TiffCompression.PackBits(R.Data.AsSpan(p0, (int)cnt), expected),
                8 or 32946 => TiffCompression.Deflate(R.Data, p0, (int)cnt, expected),
                _ => throw new CodecFormatException("TIFF: unsupported compression for YCbCr"),
            };
            for (int by = 0; by < brows; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int q = (by * bw + bx) * unit;
                    for (int j = 0; j < sv; j++)
                        for (int i = 0; i < sh; i++)
                        {
                            int y = (by0 + by) * sv + j, x = bx * sh + i;
                            if (x < w && y < h) Y[y * w + x] = raw[q + j * sh + i];
                        }
                    Cb[(by0 + by) * bw + bx] = raw[q + sh * sv];
                    Cr[(by0 + by) * bw + bx] = raw[q + sh * sv + 1];
                }
        }
        var model = (sh, sv) switch { (2, 2) => CodecColorModel.YCbCr420, (2, 1) => CodecColorModel.YCbCr422, _ => CodecColorModel.YCbCr444 };
        if (model == CodecColorModel.YCbCr444 && (sh != 1 || sv != 1))
        {
            var cb2 = new ushort[w * h]; var cr2 = new ushort[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { cb2[y * w + x] = Cb[(y / sv) * bw + x / sh]; cr2[y * w + x] = Cr[(y / sv) * bw + x / sh]; }
            Cb = cb2; Cr = cr2; bw = w; bh = h;
        }
        int pw = model == CodecColorModel.YCbCr444 ? w : w >> 1, ph = model == CodecColorModel.YCbCr420 ? h >> 1 : h;
        if (pw != bw || ph != bh)
        {
            var cb3 = new ushort[pw * ph]; var cr3 = new ushort[pw * ph];
            for (int y = 0; y < ph; y++) for (int x = 0; x < pw; x++) { cb3[y * pw + x] = Cb[Math.Min(y, bh - 1) * bw + Math.Min(x, bw - 1)]; cr3[y * pw + x] = Cr[Math.Min(y, bh - 1) * bw + Math.Min(x, bw - 1)]; }
            Cb = cb3; Cr = cr3; bw = pw; bh = ph;
        }
        return new CodecImage
        {
            Width = w, Height = h, BitDepth = 8, Model = model, Planes = new[] { Y, Cb, Cr },
            PlaneWidth = new[] { w, bw, bw }, PlaneHeight = new[] { h, bh, bh },
            MatrixHint = MatrixOf(ifd), LimitedRangeHint = LimitedOf(ifd.Doubles(532), 8),
        };
    }

    public static CodecImage Orient(CodecImage img, int o)
    {
        if (img.Model is CodecColorModel.YCbCr420 or CodecColorModel.YCbCr422) img = To444(img);
        bool swap = o >= 5;
        int nw = swap ? img.Height : img.Width, nh = swap ? img.Width : img.Height;
        var planes = new ushort[img.Planes.Length][];
        for (int k = 0; k < planes.Length; k++)
        {
            var src = img.Planes[k]; int w = img.Width, h = img.Height;
            var d = new ushort[nw * nh];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int X, Y;
                    switch (o)
                    {
                        case 2: X = w - 1 - x; Y = y; break;
                        case 3: X = w - 1 - x; Y = h - 1 - y; break;
                        case 4: X = x; Y = h - 1 - y; break;
                        case 5: X = y; Y = x; break;
                        case 6: X = h - 1 - y; Y = x; break;
                        case 7: X = h - 1 - y; Y = w - 1 - x; break;
                        default: X = y; Y = w - 1 - x; break;
                    }
                    d[Y * nw + X] = src[y * w + x];
                }
            planes[k] = d;
        }
        return new CodecImage
        {
            Width = nw, Height = nh, BitDepth = img.BitDepth, Model = img.Model, Planes = planes,
            PlaneWidth = planes.Select(_ => nw).ToArray(), PlaneHeight = planes.Select(_ => nh).ToArray(),
            MatrixHint = img.MatrixHint, LimitedRangeHint = img.LimitedRangeHint, Summary = img.Summary, Details = img.Details,
        };
    }

    private static CodecImage To444(CodecImage img)
    {
        int w = img.Width, h = img.Height;
        var planes = new ushort[3][];
        planes[0] = img.Planes[0];
        for (int k = 1; k < 3; k++)
        {
            int pw = img.PlaneWidth[k], ph = img.PlaneHeight[k];
            int sx = pw < w ? 1 : 0, sy = ph < h ? 1 : 0;
            var d = new ushort[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) d[y * w + x] = img.Planes[k][Math.Min(ph - 1, y >> sy) * pw + Math.Min(pw - 1, x >> sx)];
            planes[k] = d;
        }
        return new CodecImage
        {
            Width = w, Height = h, BitDepth = img.BitDepth, Model = CodecColorModel.YCbCr444, Planes = planes,
            PlaneWidth = new[] { w, w, w }, PlaneHeight = new[] { h, h, h }, MatrixHint = img.MatrixHint, LimitedRangeHint = img.LimitedRangeHint,
        };
    }
}
