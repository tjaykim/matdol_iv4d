using System;
using System.Collections.Generic;
using System.Linq;
using Matdol.Codecs.Tiff;

namespace Matdol.Codecs.Raw;

public enum CameraRawMode
{
    Preview,
    Sensor,
    HalfRgb,
}

public enum CameraRawFamily { Canon, Nikon, Sony, Dng, Fujifilm, Olympus, Panasonic, Pentax, Other }

public sealed class SensorData
{
    public int Width, Height, Bits;
    public ushort[] Data = Array.Empty<ushort>();
    public int[,]? Pattern;
    public int Spp = 1;
    public double[,] Black = new double[2, 2];
    public double White;
    public double[] Wb = { 1, 1, 1 };
    public string WbSource = "none";
    public int AreaL, AreaT, AreaR, AreaB;
    public string Format = "", Camera = "", PatternSource = "";
}

public static class CameraRaw
{
    public static readonly (CameraRawFamily Family, string Name, string[] Extensions)[] Families =
    {
        (CameraRawFamily.Canon, "Canon", new[] { ".cr2", ".cr3", ".crw" }),
        (CameraRawFamily.Nikon, "Nikon", new[] { ".nef", ".nrw" }),
        (CameraRawFamily.Sony, "Sony", new[] { ".arw", ".srf", ".sr2" }),
        (CameraRawFamily.Dng, "Adobe DNG", new[] { ".dng" }),
        (CameraRawFamily.Fujifilm, "Fujifilm", new[] { ".raf" }),
        (CameraRawFamily.Olympus, "Olympus / OM", new[] { ".orf" }),
        (CameraRawFamily.Panasonic, "Panasonic / Leica", new[] { ".rw2", ".rwl", ".raw" }),
        (CameraRawFamily.Pentax, "Pentax / Ricoh", new[] { ".pef" }),
        (CameraRawFamily.Other, "others (preview only)", new[] { ".srw", ".3fr", ".iiq", ".x3f", ".kdc", ".dcr", ".mrw", ".erf", ".mos", ".mef", ".nrw" }),
    };

    public static CameraRawFamily? FamilyOfExtension(string ext)
    {
        ext = ext.ToLowerInvariant();
        foreach (var f in Families) if (f.Extensions.Contains(ext)) return f.Family;
        return null;
    }

    public static bool SupportsSensor(CameraRawFamily f) => f is CameraRawFamily.Dng or CameraRawFamily.Canon or CameraRawFamily.Nikon;

    public static bool LooksLikeCameraRaw(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && ((head[0] == 'I' && head[1] == 'I' && head[2] == 'U' && head[3] == 0) || TiffReader.HasSignature(head)
                             || (head[0] == 'I' && head[1] == 'I' && head[2] == 'R' && (head[3] == 'O' || head[3] == 'S')));

    public sealed class PreviewJpeg
    {
        public int Offset, Length, Width, Height;
        public string Source = "";
    }

    public static PreviewJpeg? FindPreview(byte[] f)
    {
        var cands = new List<PreviewJpeg>();
        void Add(long off, long len, string src)
        {
            if (off <= 0 || len <= 16 || off + len > f.Length) return;
            var d = JpegDims(f, (int)off, (int)len);
            if (d == null) return;
            cands.Add(new PreviewJpeg { Offset = (int)off, Length = (int)len, Width = d.Value.w, Height = d.Value.h, Source = src });
        }
        try
        {
            int b = TiffBase(f);
            if (b >= 0)
            {
                var r = new TiffReader(f, b);
                var all = r.ReadChain(r.FirstIfd).SelectMany(i => i.Walk()).ToList();
                foreach (var ifd in all)
                {
                    if (ifd.Has(513) && ifd.Has(514)) Add(b + ifd.Long(513), ifd.Long(514), "JPEGInterchangeFormat");
                    int comp = (int)ifd.Long(259, 1);
                    if ((comp == 6 || comp == 7) && ifd.Has(273))
                    {
                        var so = ifd.Longs(273); var sc = ifd.Longs(279);
                        if (so.Length == 1 && sc.Length == 1) Add(b + so[0], sc[0], "JPEG strip");
                    }
                }
            }
        }
        catch (CodecFormatException) { }
        if (cands.Count == 0 || cands.Max(c => (long)c.Width * c.Height) < 640 * 480)
        {
            for (int i = 0; i + 3 < f.Length && cands.Count < 64; i++)
            {
                if (f[i] != 0xFF || f[i + 1] != 0xD8 || f[i + 2] != 0xFF) continue;
                int end = JpegEnd(f, i);
                if (end > i) { Add(i, end - i, "embedded JPEG"); i = end - 1; }
            }
        }
        return cands.Where(c => c.Width >= 32 && c.Height >= 32).OrderByDescending(c => (long)c.Width * c.Height).ThenByDescending(c => c.Length).FirstOrDefault();
    }

    private static (int w, int h)? JpegDims(byte[] f, int off, int len)
    {
        int end = off + len, p = off + 2;
        if (f[off] != 0xFF || f[off + 1] != 0xD8) return null;
        while (p + 4 <= end)
        {
            if (f[p] != 0xFF) return null;
            int m = f[p + 1];
            if (m == 0xFF) { p++; continue; }
            if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { p += 2; continue; }
            int l = f[p + 2] << 8 | f[p + 3];
            if (m is 0xC0 or 0xC1 or 0xC2)
            {
                if (p + 9 > end) return null;
                int h = f[p + 5] << 8 | f[p + 6], w = f[p + 7] << 8 | f[p + 8];
                return w > 0 && h > 0 ? (w, h) : null;
            }
            if (m >= 0xC3 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC) return null;
            if (m == 0xDA || m == 0xD9) return null;
            p += 2 + l;
        }
        return null;
    }

    private static int JpegEnd(byte[] f, int off)
    {
        int p = off + 2;
        while (p + 4 <= f.Length)
        {
            if (f[p] != 0xFF) return -1;
            int m = f[p + 1];
            if (m == 0xFF) { p++; continue; }
            if (m == 0xD9) return p + 2;
            if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { p += 2; continue; }
            int l = f[p + 2] << 8 | f[p + 3];
            if (l < 2) return -1;
            p += 2 + l;
            if (m == 0xDA)
            {
                while (p + 1 < f.Length)
                {
                    if (f[p] == 0xFF && f[p + 1] != 0 && !(f[p + 1] >= 0xD0 && f[p + 1] <= 0xD7) && f[p + 1] != 0xFF) break;
                    p++;
                }
            }
        }
        return -1;
    }

    private static int TiffBase(byte[] f)
    {
        if (f.Length < 8) return -1;
        if ((f[0] == 'I' && f[1] == 'I') || (f[0] == 'M' && f[1] == 'M')) return 0;
        return -1;
    }

    public static SensorData DecodeSensor(byte[] f)
    {
        if (TiffBase(f) != 0) throw new CodecFormatException("RAW: sensor data is supported for DNG, Canon CR2 and Nikon NEF only");
        var r = new TiffReader(f);
        var chain = r.ReadChain(r.FirstIfd);
        if (chain.Count == 0) throw new CodecFormatException("RAW: no IFD");
        var ifd0 = chain[0];
        string make = ifd0.Text(271), model = ifd0.Text(272);
        string camera = (make + " " + model).Trim();
        SensorData s;
        if (ifd0.Has(50706)) s = Dng(r, chain);
        else if (f.Length > 10 && f[8] == 'C' && f[9] == 'R') s = Cr2(r, chain);
        else if (make.StartsWith("NIKON", StringComparison.OrdinalIgnoreCase)) s = Nef(r, chain);
        else throw new CodecFormatException($"RAW: sensor data for this camera is not supported ({(camera.Length > 0 ? camera : "unknown")})");
        s.Camera = camera;
        return s;
    }

    private static SensorData Dng(TiffReader r, List<TiffIfd> chain)
    {
        var all = chain.SelectMany(i => i.Walk()).ToList();
        var raw = all.FirstOrDefault(i => (i.Long(254) & 1) == 0 && i.Long(262) == 32803 && TiffDecoder.HasImage(i))
               ?? all.FirstOrDefault(i => (i.Long(254) & 1) == 0 && i.Long(262) == 34892 && TiffDecoder.HasImage(i))
               ?? throw new CodecFormatException("DNG: no raw image IFD");
        int comp = (int)raw.Long(259, 1);
        if (comp == 34892) throw new CodecFormatException("DNG: lossy (JPEG) DNG raw data is not supported");
        var smp = TiffDecoder.ReadSamples(raw);
        var s = new SensorData { Width = smp.Width, Height = smp.Height, Bits = smp.OutBits, Data = smp.Data, Spp = smp.Spp, Format = "DNG " + smp.Compression };
        var lin = raw.Longs(50712);
        if (lin.Length > 1)
        {
            for (long i = 0; i < s.Data.Length; i++) s.Data[i] = (ushort)Math.Min(65535, lin[Math.Min(lin.Length - 1, s.Data[i])]);
            s.Format += " · linearized";
        }
        var white = raw.Doubles(50717);
        s.White = white.Length > 0 ? white[0] : (1 << s.Bits) - 1;
        if (white.Length > 0 && s.White >= 1) s.Bits = Math.Clamp((int)Math.Ceiling(Math.Log2(s.White + 1)), 1, 16);
        var bl = raw.Doubles(50714); var bld = raw.Longs(50713);
        int br = bld.Length >= 2 ? (int)bld[0] : 1, bc = bld.Length >= 2 ? (int)bld[1] : 1;
        for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++)
            s.Black[y, x] = bl.Length == 0 ? 0 : bl[Math.Min(bl.Length - 1, (y % br) * bc + (x % bc))];
        var aa = raw.Longs(50829);
        if (aa.Length == 4) { s.AreaT = (int)aa[0]; s.AreaL = (int)aa[1]; s.AreaB = (int)aa[2]; s.AreaR = (int)aa[3]; }
        else { s.AreaR = s.Width; s.AreaB = s.Height; }
        if (raw.Long(262) == 32803)
        {
            var dim = raw.Longs(33421); var pat = raw.Bytes(33422);
            if (dim.Length == 2 && dim[0] == 2 && dim[1] == 2 && pat.Length >= 4)
            {
                s.Pattern = new int[2, 2];
                for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++)
                    s.Pattern[(y + s.AreaT) & 1, (x + s.AreaL) & 1] = Math.Min(2, (int)pat[y * 2 + x]);
                s.PatternSource = "CFAPattern";
            }
            else throw new CodecFormatException("DNG: only 2×2 CFA patterns are supported");
        }
        var n = chain[0].Doubles(50728);
        if (n.Length >= 3 && n.All(v => v > 0)) { s.Wb = new[] { n[1] / n[0], 1.0, n[1] / n[2] }; s.WbSource = "as shot"; }
        return s;
    }

    private static SensorData Cr2(TiffReader r, List<TiffIfd> chain)
    {
        var raw = chain.Count >= 4 ? chain[3] : throw new CodecFormatException("CR2: raw IFD not found");
        long off = raw.Long(273), cnt = raw.Long(279);
        if (!r.InRange(off, cnt) || cnt <= 0) throw new CodecFormatException("CR2: bad raw strip");
        var lj = LosslessJpeg.Decode(r.Data.AsSpan((int)off, (int)cnt));
        var slices = raw.Longs(0xC640);
        int jw = lj.Width * lj.Components, jh = lj.Height;
        int rawH = jh, rawW = jw;
        long total = (long)jw * jh;
        if (slices.Length >= 3 && slices[0] >= 0 && slices[1] > 0 && slices[2] > 0) rawW = (int)(slices[0] * slices[1] + slices[2]);
        rawH = (int)(total / rawW);
        var d = new ushort[(long)rawW * rawH];
        if (slices.Length >= 3 && slices[1] > 0)
        {
            long n0 = slices[0], w0 = slices[1], wl = slices[2];
            for (long k = 0; k < total; k++)
            {
                long i = k / (w0 * rawH);
                bool last = i >= n0;
                if (last) i = n0;
                long j = k - i * (w0 * rawH);
                long sw = last ? wl : w0;
                long row = j / sw, col = j % sw + i * w0;
                if (row < rawH && col < rawW) d[row * rawW + col] = lj.Data[k];
            }
        }
        else Array.Copy(lj.Data, d, Math.Min(d.Length, lj.Data.Length));
        var s = new SensorData { Width = rawW, Height = rawH, Bits = lj.Precision, Data = d, Format = $"Canon CR2 · lossless JPEG ({lj.Components} comp, {(slices.Length >= 3 ? slices[0] + 1 : 1)} slices)" };
        s.White = (1 << s.Bits) - 1;
        s.AreaL = 0; s.AreaT = 0; s.AreaR = rawW; s.AreaB = rawH;
        var mn = CanonMakerNote(r, chain[0]);
        if (mn != null)
        {
            var si = mn.Longs(0xE0);
            if (si.Length >= 9 && si[7] > si[5] && si[8] > si[6] && si[7] < rawW && si[8] < rawH)
            { s.AreaL = (int)si[5]; s.AreaT = (int)si[6]; s.AreaR = (int)si[7] + 1; s.AreaB = (int)si[8] + 1; }
            var cd = mn.Longs(0x4001);
            foreach (int idx in new[] { 63, 25, 34, 71, 69 })
            {
                if (cd.Length < idx + 4) continue;
                double R = cd[idx], G1 = cd[idx + 1], G2 = cd[idx + 2], B = cd[idx + 3];
                if (G1 > 256 && G1 < 8192 && Math.Abs(G1 - G2) <= G1 * 0.05 && R > 64 && B > 64 && R < 16384 && B < 16384)
                { s.Wb = new[] { R / G1, 1.0, B / G1 }; s.WbSource = "as shot"; break; }
            }
        }
        MaskedBlack(s);
        GuessPattern(s, "Canon");
        return s;
    }

    private static TiffIfd? CanonMakerNote(TiffReader r, TiffIfd ifd0)
    {
        try
        {
            long exif = ifd0.Long(0x8769);
            if (exif <= 0) return null;
            var e = r.ReadIfd(exif, out _);
            var mn = e.Get(0x927C);
            if (mn == null) return null;
            return r.ReadIfd(mn.Offset, out _);
        }
        catch (CodecFormatException) { return null; }
    }

    private static SensorData Nef(TiffReader r, List<TiffIfd> chain)
    {
        var all = chain.SelectMany(i => i.Walk()).ToList();
        var raw = all.FirstOrDefault(i => (i.Long(254) & 1) == 0 && i.Long(262) == 32803 && TiffDecoder.HasImage(i))
               ?? throw new CodecFormatException("NEF: raw image IFD not found");
        int comp = (int)raw.Long(259, 1);
        int w = (int)raw.Long(256), h = (int)raw.Long(257), bps = (int)raw.Long(258, 12);
        var mn = NikonMakerNote(r, chain[0]);
        SensorData s;
        if (comp == 34713)
        {
            if (mn == null) throw new CodecFormatException("NEF: maker note not found");
            long off = raw.Long(273), cnt = raw.Long(279);
            if (!r.InRange(off, Math.Max(1, cnt))) throw new CodecFormatException("NEF: bad raw strip");
            var meta = mn.Get(0x96) ?? throw new CodecFormatException("NEF: compression table (0x96) not found");
            var (data, kind) = NikonDecoder.Decode(r.Data, (int)off, (int)Math.Min(cnt, r.Data.Length - off), w, h, bps, mn.R, meta);
            s = new SensorData { Width = w, Height = h, Bits = bps, Data = data, Format = $"Nikon NEF · {kind}" };
        }
        else
        {
            var smp = TiffDecoder.ReadSamples(raw);
            s = new SensorData { Width = smp.Width, Height = smp.Height, Bits = Math.Min(16, bps), Data = smp.Data, Format = "Nikon NEF · " + smp.Compression };
        }
        s.White = (1 << s.Bits) - 1;
        s.AreaR = s.Width; s.AreaB = s.Height;
        var dim = raw.Longs(0x828D); var pat = raw.Bytes(0x828E);
        if (dim.Length == 2 && dim[0] == 2 && dim[1] == 2 && pat.Length >= 4)
        {
            s.Pattern = new int[2, 2];
            for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++) s.Pattern[y, x] = Math.Min(2, (int)pat[y * 2 + x]);
            s.PatternSource = "CFAPattern";
        }
        if (mn != null)
        {
            var wb = mn.Get(0x0C);
            if (wb != null && wb.Count >= 2)
            {
                double rr = mn.R.ReadDouble(wb, 0), bb = mn.R.ReadDouble(wb, 1);
                if (rr > 0.1 && rr < 16 && bb > 0.1 && bb < 16) { s.Wb = new[] { rr, 1.0, bb }; s.WbSource = "as shot"; }
            }
            var bl = mn.Get(0x3D);
            if (bl != null && bl.Count >= 4)
            {
                double sc = s.Bits < 14 ? 1.0 / (1 << (14 - s.Bits)) : 1;
                for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++) s.Black[y, x] = mn.R.ReadInteger(bl, y * 2 + x) * sc;
            }
        }
        if (s.Pattern == null) GuessPattern(s, "Nikon");
        return s;
    }

    private static TiffIfd? NikonMakerNote(TiffReader r, TiffIfd ifd0)
    {
        try
        {
            long exif = ifd0.Long(0x8769);
            if (exif <= 0) return null;
            var e = r.ReadIfd(exif, out _);
            var mn = e.Get(0x927C);
            if (mn == null) return null;
            int p = (int)(r.Base + mn.Offset);
            if (p + 18 < r.Data.Length && r.Data[p] == 'N' && r.Data[p + 1] == 'i' && r.Data[p + 2] == 'k' && r.Data[p + 3] == 'o' && r.Data[p + 4] == 'n')
            {
                var mr = new TiffReader(r.Data, p + 10);
                return mr.ReadIfd(mr.FirstIfd, out _);
            }
            return null;
        }
        catch (CodecFormatException) { return null; }
    }

    private static void MaskedBlack(SensorData s)
    {
        int x1 = s.AreaL - 4, y0 = s.AreaT, y1 = s.AreaB;
        var sum = new double[2, 2]; var cnt = new long[2, 2];
        if (x1 >= 8)
        {
            for (int y = y0; y < y1; y++)
                for (int x = 2; x < x1; x++) { sum[y & 1, x & 1] += s.Data[(long)y * s.Width + x]; cnt[y & 1, x & 1]++; }
        }
        else if (s.AreaT - 4 >= 8)
        {
            for (int y = 2; y < s.AreaT - 4; y++)
                for (int x = s.AreaL; x < s.AreaR; x++) { sum[y & 1, x & 1] += s.Data[(long)y * s.Width + x]; cnt[y & 1, x & 1]++; }
        }
        for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++) s.Black[y, x] = cnt[y, x] > 0 ? sum[y, x] / cnt[y, x] : 0;
    }

    private static void GuessPattern(SensorData s, string maker)
    {
        var mean = new double[2, 2]; var cnt = new long[2, 2];
        double sumD0 = 0, sumD1 = 0; long nd = 0;
        int step = Math.Max(2, (s.AreaB - s.AreaT) / 400 * 2);
        for (int y = (s.AreaT + 1) & ~1; y + 1 < s.AreaB; y += step)
            for (int x = (s.AreaL + 1) & ~1; x + 1 < s.AreaR; x += 2)
            {
                double a = s.Data[(long)y * s.Width + x] - s.Black[y & 1, x & 1], b = s.Data[(long)y * s.Width + x + 1] - s.Black[y & 1, (x + 1) & 1];
                double c = s.Data[(long)(y + 1) * s.Width + x] - s.Black[(y + 1) & 1, x & 1], d = s.Data[(long)(y + 1) * s.Width + x + 1] - s.Black[(y + 1) & 1, (x + 1) & 1];
                mean[y & 1, x & 1] += a; mean[y & 1, (x + 1) & 1] += b; mean[(y + 1) & 1, x & 1] += c; mean[(y + 1) & 1, (x + 1) & 1] += d;
                sumD0 += Math.Abs(a - d); sumD1 += Math.Abs(b - c); nd++;
            }
        if (nd == 0) { s.Pattern = new[,] { { 0, 1 }, { 1, 2 } }; s.PatternSource = "assumed RGGB"; return; }
        for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++) mean[y, x] /= nd;
        bool gMain = sumD0 < sumD1;
        var p = new int[2, 2];
        (int y, int x) o1, o2;
        if (gMain) { p[0, 0] = 1; p[1, 1] = 1; o1 = (0, 1); o2 = (1, 0); }
        else { p[0, 1] = 1; p[1, 0] = 1; o1 = (0, 0); o2 = (1, 1); }
        double wr = s.Wb[0], wbB = s.Wb[2];
        double m1 = mean[o1.y, o1.x], m2 = mean[o2.y, o2.x];
        double e1 = Math.Abs(Math.Log(Math.Max(1e-6, m1 * wr) / Math.Max(1e-6, m2 * wbB)));
        double e2 = Math.Abs(Math.Log(Math.Max(1e-6, m2 * wr) / Math.Max(1e-6, m1 * wbB)));
        bool o1IsR = s.WbSource != "none" ? e1 <= e2 : m1 < m2;
        p[o1.y, o1.x] = o1IsR ? 0 : 2; p[o2.y, o2.x] = o1IsR ? 2 : 0;
        s.Pattern = p;
        s.PatternSource = $"estimated ({maker})";
    }

    public static string PatternName(int[,] p)
    {
        const string c = "RGB";
        return $"{c[p[0, 0]]}{c[p[0, 1]]}{c[p[1, 0]]}{c[p[1, 1]]}";
    }

    public static CodecImage ToSensorImage(SensorData s)
    {
        int bits = Math.Clamp(s.Bits, 1, 16);
        var details = Describe(s);
        if (s.Spp >= 3)
        {
            int n = s.Width * s.Height;
            var pl = new ushort[3][]; for (int k = 0; k < 3; k++) { pl[k] = new ushort[n]; for (int i = 0; i < n; i++) pl[k][i] = s.Data[(long)i * s.Spp + k]; }
            return new CodecImage
            {
                Width = s.Width, Height = s.Height, BitDepth = bits, Model = CodecColorModel.Rgb, Planes = pl,
                PlaneWidth = new[] { s.Width, s.Width, s.Width }, PlaneHeight = new[] { s.Height, s.Height, s.Height },
                Summary = $"{s.Format} · linear RGB {bits}-bit", Details = details,
            };
        }
        return new CodecImage
        {
            Width = s.Width, Height = s.Height, BitDepth = bits, Model = CodecColorModel.Gray, Planes = new[] { s.Data },
            PlaneWidth = new[] { s.Width }, PlaneHeight = new[] { s.Height },
            Summary = $"{s.Format} · sensor CFA {(s.Pattern != null ? PatternName(s.Pattern) : "?")} {bits}-bit", Details = details,
        };
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Describe(SensorData s)
    {
        var d = new List<KeyValuePair<string, string>>
        {
            new("Camera", s.Camera),
            new("Format", s.Format),
            new("Raw size", $"{s.Width} x {s.Height}, {s.Bits}-bit"),
            new("Active area", $"x {s.AreaL}..{s.AreaR - 1}, y {s.AreaT}..{s.AreaB - 1}"),
            new("Black", $"{s.Black[0, 0]:0.#} {s.Black[0, 1]:0.#} {s.Black[1, 0]:0.#} {s.Black[1, 1]:0.#}"),
            new("White", $"{s.White:0}"),
            new("WB (R G B)", $"{s.Wb[0]:0.###} {s.Wb[1]:0.###} {s.Wb[2]:0.###} ({s.WbSource})"),
        };
        if (s.Pattern != null) d.Add(new("CFA (at 0,0)", $"{PatternName(s.Pattern)} ({s.PatternSource})"));
        return d;
    }

    public static CodecImage ToHalfRgb(SensorData s)
    {
        if (s.Spp >= 3) return ToSensorImage(s);
        var pat = s.Pattern ?? throw new CodecFormatException("RAW: CFA pattern unknown");
        int x0 = (s.AreaL + 1) & ~1, y0 = (s.AreaT + 1) & ~1;
        int w = (s.AreaR - x0) / 2, h = (s.AreaB - y0) / 2;
        if (w <= 0 || h <= 0) throw new CodecFormatException("RAW: empty active area");
        var R = new ushort[w * h]; var G = new ushort[w * h]; var B = new ushort[w * h];
        double wmax = Math.Max(s.Wb[0], Math.Max(s.Wb[1], s.Wb[2]));
        var gamma = new ushort[4097];
        for (int i = 0; i <= 4096; i++)
        {
            double v = i / 4096.0;
            v = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            gamma[i] = (ushort)Math.Round(v * 65535);
        }
        double wmin = Math.Min(s.Wb[0], Math.Min(s.Wb[1], s.Wb[2]));
        var range = new double[2, 2];
        for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++) range[y, x] = Math.Max(1, s.White - s.Black[y, x]);
        System.Threading.Tasks.Parallel.For(0, h, oy =>
        {
            var acc = new double[3]; var n = new int[3];
            for (int ox = 0; ox < w; ox++)
            {
                Array.Clear(acc); Array.Clear(n);
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int y = y0 + oy * 2 + dy, x = x0 + ox * 2 + dx;
                        int py = y & 1, px = x & 1, c = pat[py, px];
                        double v = (s.Data[(long)y * s.Width + x] - s.Black[py, px]) / range[py, px];
                        acc[c] += v; n[c]++;
                    }
                int o = oy * w + ox;
                ushort Out(int c) { double v = n[c] > 0 ? acc[c] / n[c] * s.Wb[c] / wmin : 0; v = Math.Clamp(v, 0, 1); return gamma[(int)(v * 4096 + 0.5)]; }
                R[o] = Out(0); G[o] = Out(1); B[o] = Out(2);
            }
        });
        _ = wmax;
        return new CodecImage
        {
            Width = w, Height = h, BitDepth = 16, Model = CodecColorModel.Rgb, Planes = new[] { R, G, B },
            PlaneWidth = new[] { w, w, w }, PlaneHeight = new[] { h, h, h },
            Summary = $"{s.Format} · half-size RGB from CFA {PatternName(pat)} · WB {s.WbSource} · sRGB gamma", Details = Describe(s),
        };
    }
}
