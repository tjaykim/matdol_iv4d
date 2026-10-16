using System;
using System.IO;
using System.IO.Compression;

namespace Matdol.Codecs.Tiff;

internal static class TiffCompression
{
    public static byte[] Lzw(ReadOnlySpan<byte> src, int expected)
    {
        var dst = new byte[expected];
        if (src.Length >= 2 && src[0] == 0 && (src[1] & 1) != 0) return LzwOld(src, dst);
        const int Clear = 256, Eoi = 257;
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var first = new byte[4096];
        var length = new short[4096];
        for (int i = 0; i < 256; i++) { prefix[i] = -1; suffix[i] = (byte)i; first[i] = (byte)i; length[i] = 1; }
        int next = 258, width = 9, old = -1, outPos = 0;
        long bitBuf = 0; int bitCnt = 0, inPos = 0;
        var stack = new byte[4096];
        while (true)
        {
            while (bitCnt < width)
            {
                if (inPos >= src.Length) return dst;
                bitBuf = (bitBuf << 8) | src[inPos++]; bitCnt += 8;
            }
            int code = (int)((bitBuf >> (bitCnt - width)) & ((1 << width) - 1));
            bitCnt -= width;
            if (code == Eoi) break;
            if (code == Clear) { next = 258; width = 9; old = -1; continue; }
            int cur;
            if (old == -1)
            {
                if (code > 255) return dst;
                if (outPos < dst.Length) dst[outPos] = (byte)code;
                outPos++; old = code; continue;
            }
            byte firstChar;
            if (code < next) { cur = code; firstChar = first[code]; }
            else if (code == next) { cur = old; firstChar = first[old]; }
            else return dst;
            int sp = 0;
            for (int c = cur; c >= 0 && sp < stack.Length; c = prefix[c]) stack[sp++] = suffix[c];
            for (int i = sp - 1; i >= 0; i--) { if (outPos < dst.Length) dst[outPos] = stack[i]; outPos++; }
            if (code == next) { if (outPos < dst.Length) dst[outPos] = firstChar; outPos++; }
            if (next < 4096)
            {
                prefix[next] = (short)old; suffix[next] = firstChar; first[next] = first[old];
                length[next] = (short)(length[old] + 1);
                next++;
            }
            old = code;
            if (next + 1 >= (1 << width) && width < 12) width++;
            if (outPos >= dst.Length + 4096) break;
        }
        return dst;
    }

    private static byte[] LzwOld(ReadOnlySpan<byte> src, byte[] dst)
    {
        const int Clear = 256, Eoi = 257;
        var prefix = new short[4096]; var suffix = new byte[4096]; var first = new byte[4096];
        for (int i = 0; i < 256; i++) { prefix[i] = -1; suffix[i] = (byte)i; first[i] = (byte)i; }
        int next = 258, width = 9, old = -1, outPos = 0, inPos = 0, bitCnt = 0; long bitBuf = 0;
        var stack = new byte[4096];
        while (true)
        {
            while (bitCnt < width) { if (inPos >= src.Length) return dst; bitBuf |= (long)src[inPos++] << bitCnt; bitCnt += 8; }
            int code = (int)(bitBuf & ((1 << width) - 1)); bitBuf >>= width; bitCnt -= width;
            if (code == Eoi) break;
            if (code == Clear) { next = 258; width = 9; old = -1; continue; }
            if (old == -1) { if (code > 255) return dst; if (outPos < dst.Length) dst[outPos] = (byte)code; outPos++; old = code; continue; }
            int cur; byte fc;
            if (code < next) { cur = code; fc = first[code]; } else if (code == next) { cur = old; fc = first[old]; } else return dst;
            int sp = 0;
            for (int c = cur; c >= 0 && sp < stack.Length; c = prefix[c]) stack[sp++] = suffix[c];
            for (int i = sp - 1; i >= 0; i--) { if (outPos < dst.Length) dst[outPos] = stack[i]; outPos++; }
            if (code == next) { if (outPos < dst.Length) dst[outPos] = fc; outPos++; }
            if (next < 4096) { prefix[next] = (short)old; suffix[next] = fc; first[next] = first[old]; next++; }
            old = code;
            if (next >= (1 << width) && width < 12) width++;
            if (outPos >= dst.Length) break;
        }
        return dst;
    }

    public static byte[] Deflate(byte[] file, int offset, int count, int expected)
    {
        var dst = new byte[expected];
        bool zlib = count >= 2 && (file[offset] & 0x0F) == 8 && ((file[offset] << 8) | file[offset + 1]) % 31 == 0;
        try
        {
            using var ms = new MemoryStream(file, offset, count, writable: false);
            using Stream z = zlib ? new ZLibStream(ms, CompressionMode.Decompress) : new DeflateStream(ms, CompressionMode.Decompress);
            int got = 0;
            while (got < expected) { int n = z.Read(dst, got, expected - got); if (n <= 0) break; got += n; }
        }
        catch (InvalidDataException) { }
        return dst;
    }

    public static byte[] PackBits(ReadOnlySpan<byte> src, int expected)
    {
        var dst = new byte[expected];
        int i = 0, o = 0;
        while (i < src.Length && o < expected)
        {
            int n = (sbyte)src[i++];
            if (n >= 0)
            {
                int c = Math.Min(n + 1, Math.Min(src.Length - i, expected - o));
                src.Slice(i, c).CopyTo(dst.AsSpan(o)); i += n + 1; o += c;
            }
            else if (n != -128)
            {
                if (i >= src.Length) break;
                byte b = src[i++];
                int c = Math.Min(1 - n, expected - o);
                dst.AsSpan(o, c).Fill(b); o += c;
            }
        }
        return dst;
    }
}
