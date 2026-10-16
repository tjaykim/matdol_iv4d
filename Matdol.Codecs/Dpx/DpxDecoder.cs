using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Matdol.Codecs.Dpx;

public static class DpxDecoder
{
    public static bool HasSignature(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && (head[..4].SequenceEqual("SDPX"u8) || head[..4].SequenceEqual("XPDS"u8));

    public static DpxHeader ReadHeader(ReadOnlySpan<byte> file) => DpxHeader.Parse(file);

    public static CodecImage Decode(byte[] file, DpxReadOptions? options = null)
    {
        options ??= DpxReadOptions.Default;
        var hdr = DpxHeader.Parse(file);
        if (hdr.Elements.Count == 0) throw new CodecFormatException("DPX: no image element.");

        if (TryFindSeparateRgb(hdr, out int er, out int eg, out int eb))
        {
            var r = DecodeElement(file, hdr, er, options, out var nr);
            var g = DecodeElement(file, hdr, eg, options, out _);
            var b = DecodeElement(file, hdr, eb, options, out _);
            var e0 = hdr.Elements[er];
            return Finish(hdr, e0, CodecColorModel.Rgb, new[] { r[0], g[0], b[0] }, OutDepth(e0), nr + " · R/G/B separate elements", 0);
        }

        int idx = Math.Clamp(options.ElementIndex, 0, hdr.Elements.Count - 1);
        var el = hdr.Elements[idx];
        var comps = DecodeElement(file, hdr, idx, options, out string note);
        var model = el.Layout switch
        {
            ComponentLayout.Rgb => CodecColorModel.Rgb,
            ComponentLayout.YCbCr444 => CodecColorModel.YCbCr444,
            ComponentLayout.YCbCr422 => CodecColorModel.YCbCr422,
            _ => CodecColorModel.Gray,
        };
        return Finish(hdr, el, model, comps, OutDepth(el), note, idx);
    }

    private static int OutDepth(DpxElement e) => e.BitDepth >= 32 ? 16 : e.BitDepth;

    private static bool TryFindSeparateRgb(DpxHeader h, out int r, out int g, out int b)
    {
        r = g = b = -1;
        for (int i = 0; i < h.Elements.Count; i++)
        {
            switch (h.Elements[i].Descriptor)
            {
                case 1: if (r < 0) r = i; break;
                case 2: if (g < 0) g = i; break;
                case 3: if (b < 0) b = i; break;
            }
        }
        return r >= 0 && g >= 0 && b >= 0
            && h.Elements[r].BitDepth == h.Elements[g].BitDepth && h.Elements[r].BitDepth == h.Elements[b].BitDepth;
    }

    private static CodecImage Finish(DpxHeader hdr, DpxElement el, CodecColorModel model, ushort[][] planes, int depth, string note, int elementIndex)
    {
        int w = hdr.Width, h = hdr.Height;
        var pw = new int[planes.Length];
        var ph = new int[planes.Length];
        for (int i = 0; i < planes.Length; i++)
        {
            pw[i] = (model == CodecColorModel.YCbCr422 && i > 0) ? (w + 1) / 2 : w;
            ph[i] = h;
        }
        planes = ApplyOrientation(hdr.Orientation, planes, pw, ph, out bool transposed);
        int outW = transposed ? h : w, outH = transposed ? w : h;

        var inv = CultureInfo.InvariantCulture;
        string layoutName = model switch
        {
            CodecColorModel.Rgb => "RGB",
            CodecColorModel.YCbCr444 => "YCbCr 4:4:4",
            CodecColorModel.YCbCr422 => "YCbCr 4:2:2",
            _ => "Gray",
        };
        var details = new List<KeyValuePair<string, string>>(hdr.Describe());
        details.Add(new("decoded element", $"#{elementIndex + 1} — {note}"));

        bool? limited = null;
        if (el.RefLowCode != DpxHeader.Undefined32 && el.RefHighCode != DpxHeader.Undefined32 && el.BitDepth is > 1 and <= 16)
        {
            int max = (1 << el.BitDepth) - 1;
            if (el.RefLowCode == 0 && el.RefHighCode == max) limited = false;
            else if (el.RefLowCode > 0 && el.RefHighCode < max) limited = true;
        }

        return new CodecImage
        {
            Width = outW, Height = outH, BitDepth = depth, Model = model,
            Planes = planes, PlaneWidth = pw, PlaneHeight = ph,
            LimitedRangeHint = limited,
            MatrixHint = el.Colorimetric switch
            {
                5 or 6 => CodecMatrixHint.Bt709,
                7 or 8 or 9 or 10 => CodecMatrixHint.Bt601,
                14 or 15 => CodecMatrixHint.Bt2020,
                _ => CodecMatrixHint.Unknown,
            },
            Summary = $"DPX {(el.BitDepth >= 32 ? el.BitDepth + "-bit float" : el.BitDepth + "-bit")} {layoutName} · {note}",
            Details = details,
        };
    }

    private sealed class Interp
    {
        public bool BigEndian, MsbFirst, Aligned;
        public long EolWords;
        public bool Conventional;
    }

    private sealed class ElementReader
    {
        private readonly byte[] _f;
        private readonly long _base, _len;
        private readonly DpxElement _el;
        private readonly int _spl;
        private readonly bool _bitstream, _f64;
        private int _k;
        private readonly int[] _shiftMsb = new int[4], _shiftLsb = new int[4];
        private readonly uint _mask;
        private readonly int _d;
        public Interp I = new();

        public ElementReader(byte[] file, long offset, DpxElement el, int spl)
        {
            _f = file; _base = offset; _len = file.Length - offset; _el = el; _spl = spl;
            _d = el.BitDepth >= 32 ? 32 : el.BitDepth;
            _mask = _d >= 32 ? 0xFFFFFFFFu : (1u << _d) - 1;
            _f64 = el.BitDepth == 64;
            switch (el.BitDepth)
            {
                case 8: SetSlots(4, 8, 0); break;
                case 16: SetSlots(2, 16, 0); break;
                case 32: case 64: SetSlots(1, 32, 0); break;
                case 12 when el.Packing is 1 or 2: SetSlots(2, 16, el.Packing == 1 ? 4 : 0); break;
                case 10 when el.Packing is 1 or 2:
                    _k = 3;
                    int pad = el.Packing == 1 ? 2 : 0;
                    for (int i = 0; i < 3; i++)
                    {
                        _shiftMsb[i] = 22 - 10 * i - (2 - pad);
                        _shiftLsb[i] = pad + 10 * i;
                    }
                    break;
                default: _bitstream = true; break;
            }
        }

        private void SetSlots(int k, int slotBits, int inner)
        {
            _k = k;
            for (int i = 0; i < k; i++)
            {
                _shiftMsb[i] = 32 - slotBits * (i + 1) + inner;
                _shiftLsb[i] = slotBits * i + inner;
            }
        }

        public bool IsBitstream => _bitstream;
        public int SlotsPerWord => _k;
        public int DatumBits => _d;

        public bool AlignmentMatters => _bitstream ? ((long)_spl * _d) % 32 != 0 : _spl % _k != 0;

        public long WordsPerAlignedLine => _bitstream ? ((long)_spl * _d + 31) / 32 : (_spl + _k - 1) / _k * (_f64 ? 2 : 1);

        public long RequiredBytes(int h)
        {
            long words;
            if (I.Aligned) words = (WordsPerAlignedLine + I.EolWords) * h;
            else words = _bitstream ? ((long)_spl * _d * h + 31) / 32 : ((long)_spl * h + _k - 1) / _k * (_f64 ? 2 : 1);
            return words * 4;
        }

        private uint Word(long wi)
        {
            long p = wi * 4;
            if (p < 0 || p >= _len) return 0;
            if (p + 4 > _len)
            {
                Span<byte> t = stackalloc byte[4];
                t.Clear();
                new ReadOnlySpan<byte>(_f, (int)(_base + p), (int)(_len - p)).CopyTo(t);
                return I.BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(t) : BinaryPrimitives.ReadUInt32LittleEndian(t);
            }
            var s = new ReadOnlySpan<byte>(_f, (int)(_base + p), 4);
            return I.BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
        }

        public void ReadLine(int y, uint[] o)
        {
            if (_bitstream)
            {
                long bit0 = I.Aligned ? y * (WordsPerAlignedLine + I.EolWords) * 32 : (long)y * _spl * _d;
                for (int x = 0; x < _spl; x++)
                {
                    long b = bit0 + (long)x * _d;
                    long wi = b >> 5;
                    int bo = (int)(b & 31);
                    uint w0 = Word(wi);
                    uint w1 = bo + _d > 32 ? Word(wi + 1) : 0;
                    if (I.MsbFirst) o[x] = (uint)((((ulong)w0 << 32) | w1) >> (64 - bo - _d)) & _mask;
                    else o[x] = (uint)((w0 | ((ulong)w1 << 32)) >> bo) & _mask;
                }
                return;
            }

            long n0 = I.Aligned ? y * (WordsPerAlignedLine / (_f64 ? 2 : 1) + I.EolWords) * _k : (long)y * _spl;
            var shifts = I.MsbFirst ? _shiftMsb : _shiftLsb;
            if (_f64)
            {
                for (int x = 0; x < _spl; x++)
                {
                    long n = n0 + x;
                    ulong a = Word(2 * n), b = Word(2 * n + 1);
                    ulong u = I.BigEndian ? (a << 32) | b : (b << 32) | a;
                    o[x] = FloatTo16(BitConverter.Int64BitsToDouble((long)u));
                }
                return;
            }
            for (int x = 0; x < _spl; x++)
            {
                long n = n0 + x;
                uint v = (Word(n / _k) >> shifts[(int)(n % _k)]) & _mask;
                o[x] = _el.BitDepth == 32 ? FloatTo16(BitConverter.Int32BitsToSingle((int)v)) : v;
            }
        }
    }

    private static ushort[][] DecodeElement(byte[] file, DpxHeader hdr, int index, DpxReadOptions opt, out string note)
    {
        var el = hdr.Elements[index];
        if (el.Encoding == 1) throw new CodecFormatException("DPX: RLE-encoded images are not supported.");
        if (el.Layout == ComponentLayout.Unsupported)
            throw new CodecFormatException($"DPX: unsupported descriptor {el.Descriptor}");
        if (el.BitDepth is not (1 or 8 or 10 or 12 or 16 or 32 or 64))
            throw new CodecFormatException($"DPX: unsupported bit depth {el.BitDepth}");

        int w = hdr.Width, h = hdr.Height;
        long offset = el.DataOffset is 0 or DpxHeader.Undefined32 ? hdr.ImageDataOffset : el.DataOffset;
        if (offset <= 0 || offset >= file.Length) throw new CodecFormatException("DPX: invalid image data offset.");
        long avail = file.Length - offset;
        int spl = el.SamplesPerLine(w);
        var rd = new ElementReader(file, offset, el, spl);

        var endians = opt.DataByteOrder switch
        {
            DpxByteOrder.Big => new[] { true },
            DpxByteOrder.Little => new[] { false },
            DpxByteOrder.Header => new[] { hdr.BigEndian },
            _ => hdr.BigEndian ? new[] { true } : new[] { false, true },
        };
        bool filled10 = el.BitDepth == 10 && el.Packing is 1 or 2;
        long eolWords = el.EolPadding is not (0 or DpxHeader.Undefined32) && el.EolPadding % 4 == 0 && el.EolPadding < 1 << 20 ? el.EolPadding / 4 : 0;
        var aligns = new List<bool>();
        if (eolWords > 0 || opt.LinePadding == DpxLinePadding.Pad32) aligns.Add(true);
        else if (opt.LinePadding == DpxLinePadding.None) aligns.Add(false);
        else if (!rd.AlignmentMatters) aligns.Add(true);
        else
        {
            if (filled10) { aligns.Add(true); aligns.Add(false); }
            else { aligns.Add(false); aligns.Add(true); }
        }

        var cands = new List<Interp>();
        foreach (bool be in endians)
        {
            bool convMsb = rd.IsBitstream ? false : filled10 ? true : be;
            var orders = new List<bool> { convMsb };
            bool altAllowed = rd.IsBitstream ? opt.PackedDatumOrder == DpxDatumOrder.Auto
                            : filled10 || (!be && el.BitDepth is 8 or 12 or 16);
            if (rd.IsBitstream && opt.PackedDatumOrder != DpxDatumOrder.Auto) orders[0] = opt.PackedDatumOrder == DpxDatumOrder.MsbFirst;
            else if (altAllowed) orders.Add(!convMsb);
            foreach (bool msb in orders)
                foreach (bool al in aligns)
                    cands.Add(new Interp { BigEndian = be, MsbFirst = msb, Aligned = al, EolWords = al ? eolWords : 0,
                                           Conventional = be == hdr.BigEndian && msb == convMsb && al == aligns[0] });
        }
        var fits = cands.FindAll(c => { rd.I = c; return rd.RequiredBytes(h) - 3 <= avail; });
        if (fits.Count > 0) cands = fits;

        Interp best = cands[0];
        if (cands.Count > 1)
        {
            double bestScore = double.MaxValue;
            foreach (var c in cands)
            {
                rd.I = c;
                double sc = SampledRoughness(rd, el, w, h, spl);
                if (c.Conventional) sc *= 0.8;
                if (sc < bestScore) { bestScore = sc; best = c; }
            }
        }
        rd.I = best;

        var planes = Unpack(rd, el, w, h, spl);
        note = Describe(el, rd, best) + (cands.Count > 1 && !best.Conventional ? " (auto-detected)" : "");
        return planes;
    }

    private static string Describe(DpxElement el, ElementReader rd, Interp i)
    {
        var sb = new StringBuilder();
        sb.Append(el.Packing switch { 0 => "packed", 1 => "filled A", 2 => "filled B", _ => "packing " + el.Packing });
        if (el.BitDepth > 8) sb.Append(i.BigEndian ? ", BE" : ", LE");
        bool seq = !rd.IsBitstream && el.BitDepth != 10 && i.MsbFirst == i.BigEndian;
        if (rd.IsBitstream || el.BitDepth == 10 || !seq) sb.Append(i.MsbFirst ? ", MSB-first" : ", LSB-first");
        if (rd.AlignmentMatters) sb.Append(i.Aligned ? ", 32-bit line align" : ", no line align");
        if (i.EolWords > 0) sb.Append($", eol pad {i.EolWords * 4}B");
        if (el.HasAlpha) sb.Append(", alpha ignored");
        return sb.ToString();
    }

    private static ushort[][] Unpack(ElementReader rd, DpxElement el, int w, int h, int spl)
    {
        var line = new uint[spl];
        var layout = el.Layout;
        int outComps = layout == ComponentLayout.Gray ? 1 : 3;
        int cw = layout == ComponentLayout.YCbCr422 ? (w + 1) / 2 : w;
        var planes = new ushort[outComps][];
        planes[0] = new ushort[w * h];
        for (int i = 1; i < outComps; i++) planes[i] = new ushort[cw * h];
        for (int y = 0; y < h; y++)
        {
            rd.ReadLine(y, line);
            Scatter(el, line, w, planes, y * w, y * cw);
        }
        return planes;
    }

    private static void Scatter(DpxElement el, uint[] line, int w, ushort[][] planes, int row, int crow)
    {
        int nComp = el.ComponentsPerSample;
        switch (el.Layout)
        {
            case ComponentLayout.Gray:
                for (int x = 0; x < w; x++) planes[0][row + x] = (ushort)line[x * nComp];
                break;
            case ComponentLayout.Rgb:
                {
                    var (ir, ig, ib) = el.RgbIndex;
                    for (int x = 0, i = 0; x < w; x++, i += nComp)
                    {
                        planes[0][row + x] = (ushort)line[i + ir];
                        planes[1][row + x] = (ushort)line[i + ig];
                        planes[2][row + x] = (ushort)line[i + ib];
                    }
                    break;
                }
            case ComponentLayout.YCbCr444:
                for (int x = 0, i = 0; x < w; x++, i += nComp)
                {
                    planes[0][row + x] = (ushort)line[i + 1];
                    planes[1][row + x] = (ushort)line[i];
                    planes[2][row + x] = (ushort)line[i + 2];
                }
                break;
            case ComponentLayout.YCbCr422:
                {
                    bool alpha = el.Descriptor == 101;
                    int step = alpha ? 6 : 4, cr = alpha ? 3 : 2, y1 = alpha ? 4 : 3;
                    int cw = (w + 1) / 2;
                    uint At(int k) => k < line.Length ? line[k] : 0u;
                    for (int x2 = 0, i = 0; x2 < cw; x2++, i += step)
                    {
                        planes[1][crow + x2] = (ushort)At(i);
                        planes[0][row + 2 * x2] = (ushort)At(i + 1);
                        planes[2][crow + x2] = (ushort)At(i + cr);
                        if (2 * x2 + 1 < w) planes[0][row + 2 * x2 + 1] = (ushort)At(i + y1);
                    }
                    break;
                }
        }
    }

    private static uint FloatTo16(double v) => double.IsNaN(v) ? 0u : (uint)Math.Round(Math.Clamp(v, 0, 1) * 65535);

    private static double SampledRoughness(ElementReader rd, DpxElement el, int w, int h, int spl)
    {
        int period = el.Descriptor switch { 100 => 4, 101 => 6, _ => el.ComponentsPerSample };
        var a = new uint[spl]; var b = new uint[spl];
        int samples = Math.Min(48, Math.Max(1, h - 1));
        double sum = 0; long n = 0;
        for (int s = 0; s < samples; s++)
        {
            int y = h <= 1 ? 0 : 1 + (int)((long)s * (h - 2) / Math.Max(1, samples - 1));
            rd.ReadLine(Math.Max(0, y - 1), a);
            rd.ReadLine(y, b);
            for (int i = period; i < spl; i++) { sum += Math.Abs((long)b[i] - b[i - period]) + Math.Abs((long)b[i] - a[i]); n++; }
        }
        return n == 0 ? 0 : sum / n;
    }

    private static ushort[][] ApplyOrientation(int o, ushort[][] planes, int[] pw, int[] ph, out bool transposed)
    {
        transposed = false;
        if (o is <= 0 or > 7) return planes;
        bool flipX = o is 1 or 3 or 5 or 7;
        bool flipY = o is 2 or 3 or 6 or 7;
        transposed = o >= 4;
        var result = new ushort[planes.Length][];
        for (int i = 0; i < planes.Length; i++)
        {
            int w = pw[i], h = ph[i];
            var src = planes[i];
            int ow = transposed ? h : w, oh = transposed ? w : h;
            var dst = new ushort[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sx = flipX ? w - 1 - x : x, sy = flipY ? h - 1 - y : y;
                    int dx = transposed ? y : x, dy = transposed ? x : y;
                    dst[dy * ow + dx] = src[sy * w + sx];
                }
            result[i] = dst;
            pw[i] = ow; ph[i] = oh;
        }
        return result;
    }
}

public enum DpxByteOrder { Auto, Header, Big, Little }
public enum DpxDatumOrder { Auto, LsbFirst, MsbFirst }
public enum DpxLinePadding { Auto, None, Pad32 }

public sealed record DpxReadOptions
{
    public static readonly DpxReadOptions Default = new();
    public DpxByteOrder DataByteOrder { get; init; } = DpxByteOrder.Auto;
    public DpxDatumOrder PackedDatumOrder { get; init; } = DpxDatumOrder.Auto;
    public DpxLinePadding LinePadding { get; init; } = DpxLinePadding.Auto;
    public int ElementIndex { get; init; }
}

internal enum ComponentLayout { Unsupported, Gray, Rgb, YCbCr444, YCbCr422 }

public sealed class DpxElement
{
    public uint DataSign, RefLowCode, RefHighCode;
    public float RefLowQuantity, RefHighQuantity;
    public int Descriptor, Transfer, Colorimetric, BitDepth, Packing, Encoding;
    public uint DataOffset, EolPadding, EoiPadding;
    public string Description = "";

    internal ComponentLayout Layout => Descriptor switch
    {
        1 or 2 or 3 or 4 or 6 or 8 => ComponentLayout.Gray,
        50 or 51 or 52 => ComponentLayout.Rgb,
        100 or 101 => ComponentLayout.YCbCr422,
        102 or 103 => ComponentLayout.YCbCr444,
        >= 150 and <= 156 => Descriptor == 150 ? ComponentLayout.Gray : ComponentLayout.Rgb,
        _ => ComponentLayout.Unsupported,
    };

    internal int ComponentsPerSample => Descriptor switch
    {
        51 or 52 or 103 => 4,
        50 or 102 => 3,
        >= 150 and <= 156 => Descriptor - 148,
        _ => 1,
    };

    internal bool HasAlpha => Descriptor is 51 or 52 or 101 or 103;

    internal (int r, int g, int b) RgbIndex => Descriptor == 52 ? (3, 2, 1) : (0, 1, 2);

    internal int SamplesPerLine(int width) => Descriptor switch
    {
        100 => width * 2,
        101 => width * 3,
        _ => width * ComponentsPerSample,
    };

    internal int BitsPerSampleInFile() => BitDepth switch
    {
        12 when Packing != 0 => 16,
        _ => BitDepth,
    };

    public string DescriptorName => Descriptor switch
    {
        0 => "user defined", 1 => "Red", 2 => "Green", 3 => "Blue", 4 => "Alpha", 6 => "Luma (Y)",
        7 => "Color difference", 8 => "Depth (Z)", 9 => "Composite video",
        50 => "RGB", 51 => "RGBA", 52 => "ABGR", 100 => "CbYCrY (4:2:2)", 101 => "CbYACrYA (4:2:2:4)",
        102 => "CbYCr (4:4:4)", 103 => "CbYCrA (4:4:4:4)",
        >= 150 and <= 156 => $"user defined {Descriptor - 148}-component",
        _ => "unknown " + Descriptor,
    };
}

public sealed class DpxHeader
{
    public const uint Undefined32 = 0xFFFFFFFF;
    public bool BigEndian;
    public uint ImageDataOffset, FileSize;
    public string Version = "", FileName = "", CreationTime = "", Creator = "", Project = "", Copyright = "";
    public int Orientation;
    public int Width, Height;
    public List<DpxElement> Elements = new();
    public float FrameRate = float.NaN;

    public static DpxHeader Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < 1408 || !DpxDecoder.HasSignature(file)) throw new CodecFormatException("not a DPX file (no SDPX/XPDS signature).");
        byte[] f = file[..Math.Min(file.Length, 2048)].ToArray();
        var h = new DpxHeader { BigEndian = f[0] == (byte)'S' };
        bool be = h.BigEndian;
        uint U32(int o) => be ? BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(o, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(o, 4));
        int U16(int o) => be ? BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(o, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(o, 2));
        float F32(int o) => BitConverter.Int32BitsToSingle((int)U32(o));
        string Str(int o, int n) { ReadOnlySpan<byte> s = f.AsSpan(o, n); int z = s.IndexOf((byte)0); if (z >= 0) s = s[..z]; return Encoding.ASCII.GetString(s).Trim(); }

        h.ImageDataOffset = U32(4);
        h.Version = Str(8, 8);
        h.FileSize = U32(16);
        h.FileName = Str(36, 100);
        h.CreationTime = Str(136, 24);
        h.Creator = Str(160, 100);
        h.Project = Str(260, 200);
        h.Copyright = Str(460, 200);
        h.Orientation = U16(768);
        if (h.Orientation == 0xFFFF) h.Orientation = 0;
        int count = U16(770);
        uint width = U32(772), height = U32(776);
        if (width == 0 || height == 0 || width > 65536 || height > 65536 || (long)width * height > 1L << 27)
            throw new CodecFormatException($"DPX: invalid image size ({width}x{height}).");
        h.Width = (int)width; h.Height = (int)height;
        if (count is 0 or 0xFFFF) count = 1;
        count = Math.Min(count, 8);
        for (int i = 0; i < count; i++)
        {
            int o = 780 + 72 * i;
            var e = new DpxElement
            {
                DataSign = U32(o),
                RefLowCode = U32(o + 4), RefLowQuantity = F32(o + 8),
                RefHighCode = U32(o + 12), RefHighQuantity = F32(o + 16),
                Descriptor = f[o + 20], Transfer = f[o + 21], Colorimetric = f[o + 22], BitDepth = f[o + 23],
                Packing = U16(o + 24), Encoding = U16(o + 26),
                DataOffset = U32(o + 28), EolPadding = U32(o + 32), EoiPadding = U32(o + 36),
                Description = Str(o + 40, 32),
            };
            if (e.Packing == 0xFFFF) e.Packing = 0;
            if (e.Encoding == 0xFFFF) e.Encoding = 0;
            if (e.Descriptor == 0xFF && e.BitDepth == 0xFF) continue;
            h.Elements.Add(e);
        }
        if (f.Length >= 1920 + 32)
        {
            float fr = F32(1920 + 20);
            if (float.IsFinite(fr) && fr > 0 && fr < 1000) h.FrameRate = fr;
        }
        return h;
    }

    public IReadOnlyList<KeyValuePair<string, string>> Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        var l = new List<KeyValuePair<string, string>>
        {
            new("version", Version),
            new("byte order (header)", BigEndian ? "big-endian (SDPX)" : "little-endian (XPDS)"),
            new("size", $"{Width} x {Height}"),
            new("orientation", Orientation.ToString(inv)),
            new("image elements", Elements.Count.ToString(inv)),
        };
        for (int i = 0; i < Elements.Count; i++)
        {
            var e = Elements[i];
            l.Add(new($"element {i + 1}",
                $"{e.DescriptorName}, {e.BitDepth}-bit, packing {e.Packing}, encoding {e.Encoding}, transfer {e.Transfer}, colorimetric {e.Colorimetric}"
                + (e.RefLowCode != Undefined32 && e.RefHighCode != Undefined32 ? $", ref {e.RefLowCode}..{e.RefHighCode}" : "")));
        }
        if (Creator.Length > 0) l.Add(new("creator", Creator));
        if (CreationTime.Length > 0) l.Add(new("created", CreationTime));
        if (Project.Length > 0) l.Add(new("project", Project));
        if (Copyright.Length > 0) l.Add(new("copyright", Copyright));
        if (!float.IsNaN(FrameRate)) l.Add(new("frame rate", FrameRate.ToString("0.###", inv)));
        return l;
    }
}
