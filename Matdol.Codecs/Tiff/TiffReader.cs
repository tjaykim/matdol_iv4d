using System;
using System.Collections.Generic;
using System.Text;

namespace Matdol.Codecs.Tiff;

public sealed class TiffEntry
{
    public ushort Tag;
    public ushort Type;
    public long Count;
    public long Offset;
}

public sealed class TiffIfd
{
    public readonly Dictionary<ushort, TiffEntry> Entries = new();
    public readonly TiffReader R;
    public long Position;
    public readonly List<TiffIfd> Sub = new();
    internal TiffIfd(TiffReader r, long pos) { R = r; Position = pos; }

    public bool Has(ushort tag) => Entries.ContainsKey(tag);
    public TiffEntry? Get(ushort tag) => Entries.TryGetValue(tag, out var e) ? e : null;
    public long Long(ushort tag, long def = 0) => Entries.TryGetValue(tag, out var e) && e.Count > 0 ? R.ReadInteger(e, 0) : def;
    public long[] Longs(ushort tag) => Entries.TryGetValue(tag, out var e) ? R.ReadIntegers(e) : Array.Empty<long>();
    public double[] Doubles(ushort tag) => Entries.TryGetValue(tag, out var e) ? R.ReadDoubles(e) : Array.Empty<double>();
    public string Text(ushort tag) => Entries.TryGetValue(tag, out var e) ? R.ReadString(e) : "";
    public byte[] Bytes(ushort tag) => Entries.TryGetValue(tag, out var e) ? R.ReadBytes(e) : Array.Empty<byte>();

    public IEnumerable<TiffIfd> Walk()
    {
        yield return this;
        foreach (var s in Sub) foreach (var x in s.Walk()) yield return x;
    }
}

public sealed class TiffReader
{
    public readonly byte[] Data;
    public readonly int Base;
    public readonly bool LittleEndian;
    public readonly bool Big;
    public readonly long FirstIfd;

    public TiffReader(byte[] data, int baseOffset = 0)
    {
        Data = data; Base = baseOffset;
        if (data.Length < baseOffset + 8) throw new CodecFormatException("TIFF: file too short");
        if (data[baseOffset] == 'I' && data[baseOffset + 1] == 'I') LittleEndian = true;
        else if (data[baseOffset] == 'M' && data[baseOffset + 1] == 'M') LittleEndian = false;
        else throw new CodecFormatException("TIFF: bad byte order mark");
        int magic = U16(2);
        if (magic == 43)
        {
            Big = true;
            if (U16(4) != 8) throw new CodecFormatException("BigTIFF: unsupported offset size");
            FirstIfd = (long)U64(8);
        }
        else
        {
            if (magic != 42 && magic != 0x55 && magic != 0x4F52 && magic != 0x5352) throw new CodecFormatException($"TIFF: bad magic {magic}");
            FirstIfd = U32(4);
        }
    }

    public TiffReader(byte[] data, int baseOffset, bool littleEndian)
    {
        Data = data; Base = baseOffset; LittleEndian = littleEndian; Big = false; FirstIfd = 0;
    }

    public static bool HasSignature(ReadOnlySpan<byte> h) =>
        h.Length >= 4 && ((h[0] == 'I' && h[1] == 'I' && (h[2] == 42 || h[2] == 43) && h[3] == 0) ||
                          (h[0] == 'M' && h[1] == 'M' && h[2] == 0 && (h[3] == 42 || h[3] == 43)));

    private int Pos(long off, int len)
    {
        long p = Base + off;
        if (off < 0 || p < 0 || p + len > Data.Length) throw new CodecFormatException("TIFF: offset out of range");
        return (int)p;
    }
    public bool InRange(long off, long len) => off >= 0 && Base + off + len <= Data.Length && len >= 0;
    public int U8(long off) => Data[Pos(off, 1)];
    public int U16(long off) { int p = Pos(off, 2); return LittleEndian ? Data[p] | Data[p + 1] << 8 : Data[p] << 8 | Data[p + 1]; }
    public uint U32(long off)
    {
        int p = Pos(off, 4);
        return LittleEndian ? (uint)(Data[p] | Data[p + 1] << 8 | Data[p + 2] << 16 | Data[p + 3] << 24)
                            : (uint)(Data[p] << 24 | Data[p + 1] << 16 | Data[p + 2] << 8 | Data[p + 3]);
    }
    public ulong U64(long off)
    {
        ulong a = U32(off), b = U32(off + 4);
        return LittleEndian ? a | b << 32 : a << 32 | b;
    }

    private static int TypeSize(int type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 or 16 or 17 or 18 => 8,
        _ => 0,
    };

    public TiffIfd ReadIfd(long off, out long next)
    {
        var ifd = new TiffIfd(this, off);
        long n = Big ? (long)U64(off) : U16(off);
        if (n > 4096) throw new CodecFormatException("TIFF: too many directory entries");
        long p = off + (Big ? 8 : 2);
        int esz = Big ? 20 : 12, inl = Big ? 8 : 4;
        for (long i = 0; i < n; i++, p += esz)
        {
            var e = new TiffEntry { Tag = (ushort)U16(p), Type = (ushort)U16(p + 2) };
            e.Count = Big ? (long)U64(p + 4) : U32(p + 4);
            int ts = TypeSize(e.Type);
            if (ts == 0 || e.Count < 0 || e.Count > int.MaxValue / 8) continue;
            long bytes = e.Count * ts;
            long vpos = p + (Big ? 12 : 8);
            e.Offset = bytes <= inl ? vpos : (Big ? (long)U64(vpos) : U32(vpos));
            ifd.Entries[e.Tag] = e;
        }
        next = Big ? (long)U64(p) : U32(p);
        return ifd;
    }

    public List<TiffIfd> ReadChain(long first, int maxIfds = 512)
    {
        var list = new List<TiffIfd>();
        var seen = new HashSet<long>();
        long off = first;
        while (off != 0 && seen.Add(off) && list.Count < maxIfds && InRange(off, 2))
        {
            TiffIfd ifd;
            try { ifd = ReadIfd(off, out off); } catch (CodecFormatException) { break; }
            ReadSubIfds(ifd, seen, 0);
            list.Add(ifd);
        }
        return list;
    }

    private void ReadSubIfds(TiffIfd ifd, HashSet<long> seen, int depth)
    {
        if (depth > 3) return;
        foreach (var so in ifd.Longs(0x14A))
        {
            if (!seen.Add(so) || !InRange(so, 2)) continue;
            try
            {
                var s = ReadIfd(so, out _);
                ReadSubIfds(s, seen, depth + 1);
                ifd.Sub.Add(s);
            }
            catch (CodecFormatException) { }
        }
    }

    public long ReadInteger(TiffEntry e, long i)
    {
        int ts = TypeSize(e.Type);
        long o = e.Offset + i * ts;
        return e.Type switch
        {
            1 or 7 or 2 => U8(o),
            6 => (sbyte)U8(o),
            3 => U16(o),
            8 => (short)U16(o),
            4 or 13 => U32(o),
            9 => (int)U32(o),
            16 or 18 => (long)U64(o),
            17 => (long)U64(o),
            5 => U32(o + 4) == 0 ? 0 : U32(o) / U32(o + 4),
            10 => (int)U32(o + 4) == 0 ? 0 : (int)U32(o) / (int)U32(o + 4),
            11 => (long)BitConverter.Int32BitsToSingle((int)U32(o)),
            12 => (long)BitConverter.Int64BitsToDouble((long)U64(o)),
            _ => 0,
        };
    }

    public double ReadDouble(TiffEntry e, long i)
    {
        int ts = TypeSize(e.Type);
        long o = e.Offset + i * ts;
        return e.Type switch
        {
            5 => U32(o + 4) == 0 ? 0 : (double)U32(o) / U32(o + 4),
            10 => (int)U32(o + 4) == 0 ? 0 : (double)(int)U32(o) / (int)U32(o + 4),
            11 => BitConverter.Int32BitsToSingle((int)U32(o)),
            12 => BitConverter.Int64BitsToDouble((long)U64(o)),
            _ => ReadInteger(e, i),
        };
    }

    public long[] ReadIntegers(TiffEntry e)
    {
        if (!InRange(e.Offset, e.Count * Math.Max(1, TypeSize(e.Type)))) return Array.Empty<long>();
        var a = new long[e.Count];
        for (long i = 0; i < e.Count; i++) a[i] = ReadInteger(e, i);
        return a;
    }

    public double[] ReadDoubles(TiffEntry e)
    {
        if (!InRange(e.Offset, e.Count * Math.Max(1, TypeSize(e.Type)))) return Array.Empty<double>();
        var a = new double[e.Count];
        for (long i = 0; i < e.Count; i++) a[i] = ReadDouble(e, i);
        return a;
    }

    public byte[] ReadBytes(TiffEntry e)
    {
        long len = e.Count * Math.Max(1, TypeSize(e.Type));
        if (!InRange(e.Offset, len)) return Array.Empty<byte>();
        var b = new byte[len];
        Array.Copy(Data, Base + e.Offset, b, 0, len);
        return b;
    }

    public string ReadString(TiffEntry e)
    {
        var b = ReadBytes(e);
        int n = Array.IndexOf(b, (byte)0); if (n < 0) n = b.Length;
        return Encoding.ASCII.GetString(b, 0, n).Trim();
    }
}
