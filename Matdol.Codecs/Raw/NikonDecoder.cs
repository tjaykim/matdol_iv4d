using System;
using Matdol.Codecs.Tiff;

namespace Matdol.Codecs.Raw;

internal static class NikonDecoder
{
    private static readonly byte[][] Trees =
    {
        new byte[] { 0,1,5,1,1,1,1,1,1,2,0,0,0,0,0,0, 5,4,3,6,2,7,1,0,8,9,11,10,12 },
        new byte[] { 0,1,5,1,1,1,1,1,1,2,0,0,0,0,0,0, 0x39,0x5a,0x38,0x27,0x16,5,4,3,2,1,0,11,12,12 },
        new byte[] { 0,1,4,2,3,1,2,0,0,0,0,0,0,0,0,0, 5,4,6,3,7,2,8,1,9,0,10,11,12 },
        new byte[] { 0,1,4,3,1,1,1,1,1,2,0,0,0,0,0,0, 5,6,4,7,8,3,9,2,1,0,10,11,12,13,14 },
        new byte[] { 0,1,5,1,1,1,1,1,1,1,2,0,0,0,0,0, 8,0x5c,0x4b,0x3a,0x29,7,6,5,4,3,2,1,0,13,14 },
        new byte[] { 0,1,4,2,2,3,1,2,0,0,0,0,0,0,0,0, 7,6,8,5,9,4,10,3,11,12,2,0,1,13,14 },
    };

    private static ushort[] Build(byte[] t)
    {
        var lut = new ushort[1 << 16];
        int code = 0, k = 16;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < t[len - 1]; i++, k++)
            {
                int v = k < t.Length ? t[k] : 0;
                int shift = 16 - len, start = code << shift, n = 1 << shift;
                if (start + n <= lut.Length) lut.AsSpan(start, n).Fill((ushort)(len << 8 | v));
                code++;
            }
            code <<= 1;
        }
        return lut;
    }

    public static (ushort[] data, string kind) Decode(byte[] f, int off, int len, int w, int h, int bps, TiffReader mr, TiffEntry meta)
    {
        int mp = (int)(mr.Base + meta.Offset);
        int Get2(int p) => mr.LittleEndian ? f[p] | f[p + 1] << 8 : f[p] << 8 | f[p + 1];
        int ver0 = f[mp], ver1 = f[mp + 1];
        int p = mp + 2;
        if (ver0 == 0x49 || ver1 == 0x58) p += 2110;
        int tree = 0;
        if (ver0 == 0x46) tree = 2;
        if (bps == 14) tree += 3;
        var vpred = new int[2, 2];
        for (int i = 0; i < 4; i++) { vpred[i >> 1, i & 1] = (short)Get2(p); p += 2; }
        int max = 1 << bps & 0x7FFF;
        var curve = new ushort[0x10000];
        for (int i = 0; i < curve.Length; i++) curve[i] = (ushort)i;
        int csize = Get2(p); p += 2;
        int step = csize > 1 ? max / (csize - 1) : 0;
        int split = 0;
        if (ver0 == 0x44 && ver1 == 0x20 && step > 0)
        {
            for (int i = 0; i < csize; i++) curve[i * step] = (ushort)Get2(p + 2 * i);
            for (int i = 0; i < max; i++)
            {
                int a = i - i % step;
                curve[i] = (ushort)((curve[a] * (step - i % step) + curve[Math.Min(0xFFFF, a + step)] * (i % step)) / step);
            }
            split = Get2(mp + 562);
        }
        else if (ver0 != 0x46 && csize <= 0x4001)
        {
            for (int i = 0; i < csize; i++) curve[i] = (ushort)Get2(p + 2 * i);
            max = csize;
        }
        while (max > 2 && curve[max - 2] == curve[max - 1]) max--;
        var lut = Build(Trees[tree]);
        string kind = (tree % 3 == 2 ? "lossless" : "lossy") + $" Huffman {bps}-bit";

        var o = new ushort[(long)w * h];
        int pos = off, end = Math.Min(f.Length, off + len);
        ulong buf = 0; int cnt = 0;
        var hpred = new int[2];
        int min = 0;
        for (int row = 0; row < h; row++)
        {
            if (split > 0 && row == split) { lut = Build(Trees[tree + 1]); min = 16; max += min << 1; }
            for (int col = 0; col < w; col++)
            {
                while (cnt <= 40) { ulong b = pos < end ? f[pos++] : 0u; buf |= b << (56 - cnt); cnt += 8; }
                ushort e = lut[(int)(buf >> 48)];
                int l = e >> 8;
                if (l == 0) throw new CodecFormatException($"NEF: bad Huffman code at row {row}");
                buf <<= l; cnt -= l;
                int i = e & 0xFF;
                int ln = i & 15, shl = i >> 4;
                int diff;
                if (ln == 0) diff = 0;
                else
                {
                    int nb = ln - shl, bits = 0;
                    if (nb > 0) { bits = (int)(buf >> (64 - nb)); buf <<= nb; cnt -= nb; }
                    diff = ((bits << 1) + 1) << shl >> 1;
                    if ((diff & (1 << (ln - 1))) == 0) diff -= (1 << ln) - (shl == 0 ? 1 : 0);
                }
                if (col < 2) hpred[col] = vpred[row & 1, col] += diff;
                else hpred[col & 1] += diff;
                int v = (short)hpred[col & 1];
                o[(long)row * w + col] = curve[Math.Clamp(v, 0, 0x3FFF)];
            }
        }
        return (o, kind);
    }
}
