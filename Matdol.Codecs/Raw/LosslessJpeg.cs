using System;

namespace Matdol.Codecs.Raw;

public static class LosslessJpeg
{
    public sealed class Result
    {
        public int Width, Height, Components, Precision;
        public ushort[] Data = Array.Empty<ushort>();
    }

    private sealed class Huff
    {
        public readonly ushort[] Lut = new ushort[1 << 16];
    }

    public static Result Decode(ReadOnlySpan<byte> s)
    {
        if (s.Length < 4 || s[0] != 0xFF || s[1] != 0xD8) throw new CodecFormatException("LJ92: no SOI");
        var huff = new Huff?[4];
        int precision = 0, width = 0, height = 0, nf = 0, restart = 0;
        var compId = new int[4];
        int p = 2;
        while (p + 4 <= s.Length)
        {
            if (s[p] != 0xFF) { p++; continue; }
            int marker = s[p + 1];
            if (marker == 0xFF) { p++; continue; }
            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7)) { p += 2; continue; }
            if (marker == 0xD9) break;
            int len = s[p + 2] << 8 | s[p + 3];
            if (p + 2 + len > s.Length) throw new CodecFormatException("LJ92: truncated segment");
            var seg = s.Slice(p + 4, len - 2);
            switch (marker)
            {
                case 0xC4:
                    {
                        int q = 0;
                        while (q + 17 <= seg.Length)
                        {
                            int tc_th = seg[q] & 0x0F;
                            var bits = seg.Slice(q + 1, 16);
                            int total = 0; for (int i = 0; i < 16; i++) total += bits[i];
                            if (q + 17 + total > seg.Length) throw new CodecFormatException("LJ92: bad DHT");
                            huff[tc_th & 3] = Build(bits, seg.Slice(q + 17, total));
                            q += 17 + total;
                        }
                        break;
                    }
                case 0xC3:
                    precision = seg[0]; height = seg[1] << 8 | seg[2]; width = seg[3] << 8 | seg[4]; nf = seg[5];
                    if (nf < 1 || nf > 4) throw new CodecFormatException("LJ92: bad component count");
                    for (int i = 0; i < nf; i++)
                    {
                        compId[i] = seg[6 + i * 3];
                        int hv = seg[7 + i * 3];
                        if (hv != 0x11) throw new CodecFormatException("LJ92: subsampled components not supported (sRAW?)");
                    }
                    break;
                case 0xC0: case 0xC1: case 0xC2: case 0xC5: case 0xC6: case 0xC7: case 0xC9: case 0xCA: case 0xCB:
                    throw new CodecFormatException("LJ92: not a lossless JPEG");
                case 0xDD: restart = seg[0] << 8 | seg[1]; break;
                case 0xDA:
                    {
                        int ns = seg[0];
                        if (ns != nf || width == 0) throw new CodecFormatException("LJ92: unsupported scan");
                        var tables = new Huff[ns];
                        for (int i = 0; i < ns; i++)
                        {
                            int id = seg[1 + i * 2], td = seg[2 + i * 2] >> 4;
                            int ci = Array.IndexOf(compId, id, 0, nf);
                            if (ci != i) throw new CodecFormatException("LJ92: component order");
                            tables[i] = huff[td & 3] ?? throw new CodecFormatException("LJ92: missing Huffman table");
                        }
                        int pred = seg[1 + ns * 2], pt = seg[3 + ns * 2] & 0x0F;
                        var r = new Result { Width = width, Height = height, Components = nf, Precision = precision };
                        r.Data = Scan(s, p + 2 + len, width, height, nf, tables, pred, pt, precision, restart);
                        return r;
                    }
            }
            p += 2 + len;
        }
        throw new CodecFormatException("LJ92: no scan");
    }

    private static Huff Build(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> vals)
    {
        var h = new Huff();
        int code = 0, k = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < bits[len - 1]; i++, k++)
            {
                int shift = 16 - len, start = code << shift, n = 1 << shift;
                ushort e = (ushort)(len << 8 | vals[k]);
                if (start + n <= h.Lut.Length) h.Lut.AsSpan(start, n).Fill(e);
                code++;
            }
            code <<= 1;
        }
        return h;
    }

    private static ushort[] Scan(ReadOnlySpan<byte> s, int pos, int w, int h, int nf, Huff[] tables, int predSel, int pt, int precision, int restart)
    {
        var o = new ushort[(long)w * h * nf];
        int rowLen = w * nf;
        ulong buf = 0; int cnt = 0; bool marker = false;
        int initial = 1 << (precision - pt - 1);
        int mcus = 0;
        int intervalRow = 0;

        for (int y = 0; y < h; y++)
        {
            int ro = y * rowLen;
            for (int x = 0; x < w; x++)
            {
                if (restart > 0 && mcus == restart)
                {
                    buf = 0; cnt = 0; marker = false;
                    while (pos + 1 < s.Length && !(s[pos] == 0xFF && s[pos + 1] >= 0xD0 && s[pos + 1] <= 0xD7)) pos++;
                    pos += 2;
                    mcus = 0; intervalRow = y;
                }
                bool resetPix = restart > 0 ? mcus == 0 : (x == 0 && y == 0);
                for (int c = 0; c < nf; c++)
                {
                    while (cnt <= 48)
                    {
                        int b = 0;
                        if (!marker && pos < s.Length)
                        {
                            b = s[pos++];
                            if (b == 0xFF)
                            {
                                int b2 = pos < s.Length ? s[pos] : 0;
                                if (b2 == 0) pos++;
                                else { marker = true; pos--; b = 0; }
                            }
                        }
                        buf |= (ulong)b << (56 - cnt); cnt += 8;
                    }
                    ushort e = tables[c].Lut[(int)(buf >> 48)];
                    int len = e >> 8;
                    if (len == 0) throw new CodecFormatException($"LJ92: bad Huffman code at ({x},{y})");
                    buf <<= len; cnt -= len;
                    int ssss = e & 0xFF, diff;
                    if (ssss == 0) diff = 0;
                    else if (ssss == 16) diff = 32768;
                    else
                    {
                        int v = (int)(buf >> (64 - ssss)); buf <<= ssss; cnt -= ssss;
                        diff = v < (1 << (ssss - 1)) ? v - (1 << ssss) + 1 : v;
                    }
                    int idx = ro + x * nf + c, pr;
                    if (resetPix) pr = initial;
                    else if (y == intervalRow) pr = o[idx - nf] >> pt;
                    else if (x == 0) pr = o[idx - rowLen] >> pt;
                    else
                    {
                        int ra = o[idx - nf] >> pt, rb = o[idx - rowLen] >> pt, rc = o[idx - rowLen - nf] >> pt;
                        pr = predSel switch
                        {
                            1 => ra, 2 => rb, 3 => rc,
                            4 => ra + rb - rc,
                            5 => ra + ((rb - rc) >> 1),
                            6 => rb + ((ra - rc) >> 1),
                            7 => (ra + rb) >> 1,
                            _ => ra,
                        };
                    }
                    o[idx] = (ushort)(((pr + diff) & 0xFFFF) << pt);
                }
                mcus++;
            }
        }
        return o;
    }
}
