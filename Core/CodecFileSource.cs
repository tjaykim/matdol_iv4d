using System;
using System.Collections.Generic;
using System.IO;
using Matdol.Codecs;
using Matdol.Codecs.Dpx;
using Matdol.Codecs.Raw;
using Matdol.Codecs.Tiff;
using SkiaSharp;

namespace Matdol.Core;

public sealed class CodecFileSource : IFrameSource
{
    public enum Kind { Dpx, Dsc, Tiff, CameraRaw, Heif }

    public string Path { get; }
    public Kind FileKind { get; }
    public int Width { get; }
    public int Height { get; }
    public int FrameCount { get; }
    public RawPixelFormat Format { get; }
    public int BitDepth { get; }
    public string Summary { get; }
    public string? Note { get; }
    public IReadOnlyList<KeyValuePair<string, string>> Details { get; }
    public YuvMatrix? MatrixHint { get; }
    public bool? LimitedRangeHint { get; }

    private readonly byte[]? _file;
    private readonly FrameData _first;
    private (int index, FrameData frame)? _cache;

    public CameraRawMode RawMode { get; }
    public CameraRawFamily RawFamily { get; }
    public string RawFamilyName => Array.Find(CameraRaw.Families, x => x.Family == RawFamily).Name ?? "camera";
    public bool RawSensorSupported => CameraRaw.SupportsSensor(RawFamily);

    public CodecFileSource(string path, Kind kind, CameraRawMode? rawMode = null)
    {
        Path = path; FileKind = kind;
        RawMode = rawMode ?? AppSettings.Current.RawMode;
        RawFamily = CameraRaw.FamilyOfExtension(System.IO.Path.GetExtension(path)) ?? CameraRawFamily.Other;
        CodecImage img;
        FrameCount = 1;
        switch (kind)
        {
            case Kind.Dsc:
                {
                    byte[] bytes = ReadShared(path);
                    FrameCount = DscPlugin.CountFrames(bytes);
                    img = DscPlugin.Decode(bytes, 0);
                    if (FrameCount > 1) _file = bytes;
                    break;
                }
            case Kind.Tiff:
                {
                    byte[] bytes = ReadShared(path);
                    try
                    {
                        FrameCount = TiffDecoder.CountPages(bytes);
                        img = TiffDecoder.Decode(bytes, 0, DecodeJpeg);
                        if (FrameCount > 1) _file = bytes;
                    }
                    catch (CodecFormatException ex) when (!OperatingSystem.IsLinux())
                    {
                        img = SystemCodecs.Decode(path);
                        Note = $"TIFF: {ex.Message} → decoded by {SystemCodecs.Name}";
                        FrameCount = 1;
                    }
                    break;
                }
            case Kind.CameraRaw:
                img = DecodeCameraRaw(path, RawMode, out var note);
                Note = note;
                break;
            case Kind.Heif:
                img = SystemCodecs.Decode(path);
                img = Relabel(img, "HEIF/AVIF (" + SystemCodecs.Name + ") · " + img.Summary);
                break;
            default:
                img = DpxDecoder.Decode(ReadShared(path));
                break;
        }
        _first = ToFrame(img);
        Width = img.Width; Height = img.Height;
        Format = _first.Format;
        BitDepth = img.BitDepth;
        Summary = img.Summary;
        Details = img.Details;
        MatrixHint = img.MatrixHint switch
        {
            CodecMatrixHint.Bt601 => YuvMatrix.Bt601,
            CodecMatrixHint.Bt709 => YuvMatrix.Bt709,
            CodecMatrixHint.Bt2020 => YuvMatrix.Bt2020,
            _ => null,
        };
        LimitedRangeHint = img.LimitedRangeHint;
    }

    public FrameData ReadFrame(int index)
    {
        index = Math.Clamp(index, 0, FrameCount - 1);
        if (index == 0 || _file == null) return _first;
        if (_cache is { } c && c.index == index) return c.frame;
        var img = FileKind == Kind.Tiff ? TiffDecoder.Decode(_file, index, DecodeJpeg) : DscPlugin.Decode(_file, index);
        var f = ToFrame(img);
        _cache = (index, f);
        return f;
    }

    public IFrameSource Reopen() => new CodecFileSource(Path, FileKind, RawMode);
    public CodecFileSource ReopenAs(CameraRawMode mode) => new(Path, FileKind, mode);
    public void Dispose() { }

    private static readonly string[] TiffExt = { ".tif", ".tiff" };

    public static Kind? Detect(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        Span<byte> head = stackalloc byte[16];
        int got = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            got = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch { }
        var h = head[..got];

        if (ext == ".dpx") return Kind.Dpx;
        if (ext == ".dsc") return Kind.Dsc;
        if (Array.IndexOf(TiffExt, ext) >= 0) return Kind.Tiff;
        if (SystemCodecs.IsHeifExtension(path)) return Kind.Heif;
        if (CameraRaw.FamilyOfExtension(ext) is not null)
        {
            if (ext == ".raw") return CameraRaw.LooksLikeCameraRaw(h) ? Kind.CameraRaw : null;
            return Kind.CameraRaw;
        }
        if (RawFormatInfo.IsRawExtension(path) || RawFormatInfo.IsImageExtension(path) || RawFormatInfo.IsPnmExtension(path)) return null;
        if (h.Length >= 4)
        {
            if (DpxDecoder.HasSignature(h)) return Kind.Dpx;
            if (DscPlugin.HasSignature(h)) return Kind.Dsc;
            if (TiffReader.HasSignature(h)) return Kind.Tiff;
            if (SystemCodecs.HasHeifSignature(h)) return Kind.Heif;
        }
        return null;
    }

    private static CodecImage DecodeCameraRaw(string path, CameraRawMode mode, out string? note)
    {
        note = null;
        byte[] f = ReadShared(path);
        var famId = CameraRaw.FamilyOfExtension(System.IO.Path.GetExtension(path)) ?? CameraRawFamily.Other;
        string fam = Array.Find(CameraRaw.Families, x => x.Family == famId).Name ?? "camera";
        if (famId == CameraRawFamily.Other) fam = "camera";
        string? sensorErr = null;
        if (mode != CameraRawMode.Preview)
        {
            try
            {
                var s = CameraRaw.DecodeSensor(f);
                return mode == CameraRawMode.Sensor ? CameraRaw.ToSensorImage(s) : CameraRaw.ToHalfRgb(s);
            }
            catch (CodecFormatException ex) { sensorErr = ex.Message; }
        }
        var p = CameraRaw.FindPreview(f);
        if (p != null)
        {
            var px = DecodeJpeg(f.AsSpan(p.Offset, p.Length).ToArray());
            if (px != null)
            {
                if (sensorErr != null) note = sensorErr + " → embedded preview";
                return FromPixels(px, $"{fam} RAW · embedded preview JPEG {p.Width}x{p.Height} (8-bit, camera processed)");
            }
        }
        if (!OperatingSystem.IsLinux())
        {
            var img = SystemCodecs.Decode(path);
            note = (sensorErr ?? "no embedded preview") + " → " + SystemCodecs.Name;
            return Relabel(img, $"{fam} RAW · {img.Summary}");
        }
        throw new CodecFormatException(sensorErr ?? "no embedded preview found");
    }

    public static JpegPixels? DecodeJpeg(byte[] jpeg)
    {
        using var bmp = SKBitmap.Decode(jpeg);
        if (bmp == null) return null;
        bool gray = bmp.ColorType == SKColorType.Gray8;
        int w = bmp.Width, h = bmp.Height, ch = gray ? 1 : 3;
        var d = new byte[(long)w * h * ch];
        var px = bmp.Pixels;
        for (int i = 0; i < px.Length; i++)
        {
            if (gray) d[i] = px[i].Red;
            else { d[3 * i] = px[i].Red; d[3 * i + 1] = px[i].Green; d[3 * i + 2] = px[i].Blue; }
        }
        return new JpegPixels(w, h, ch, d);
    }

    private static CodecImage FromPixels(JpegPixels px, string summary)
    {
        int n = px.Width * px.Height;
        var planes = new ushort[px.Channels >= 3 ? 3 : 1][];
        for (int c = 0; c < planes.Length; c++)
        {
            var p = new ushort[n];
            for (int i = 0; i < n; i++) p[i] = px.Data[(long)i * px.Channels + c];
            planes[c] = p;
        }
        return new CodecImage
        {
            Width = px.Width, Height = px.Height, BitDepth = 8, Model = planes.Length == 3 ? CodecColorModel.Rgb : CodecColorModel.Gray,
            Planes = planes, PlaneWidth = Array.ConvertAll(planes, _ => px.Width), PlaneHeight = Array.ConvertAll(planes, _ => px.Height),
            Summary = summary,
        };
    }

    private static CodecImage Relabel(CodecImage i, string summary) => new()
    {
        Width = i.Width, Height = i.Height, BitDepth = i.BitDepth, Model = i.Model, Planes = i.Planes, PlaneWidth = i.PlaneWidth,
        PlaneHeight = i.PlaneHeight, MatrixHint = i.MatrixHint, LimitedRangeHint = i.LimitedRangeHint, Summary = summary, Details = i.Details,
    };

    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = fs.Length;
        if (len > int.MaxValue) throw new InvalidDataException("file too large (over 2 GB).");
        var buf = new byte[len];
        fs.ReadExactly(buf);
        return buf;
    }

    private static FrameData ToFrame(CodecImage img)
    {
        var fmt = img.Model switch
        {
            CodecColorModel.Rgb => RawPixelFormat.Rgb24p,
            CodecColorModel.YCbCr444 => RawPixelFormat.Yuv444p,
            CodecColorModel.YCbCr422 => RawPixelFormat.Yuv422p,
            CodecColorModel.YCbCr420 => RawPixelFormat.Yuv420p,
            _ => RawPixelFormat.Gray,
        };
        var f = new FrameData { Width = img.Width, Height = img.Height, Format = fmt, MaxValue = img.MaxValue };
        for (int i = 0; i < img.Planes.Length && i < 3; i++)
        {
            f.Planes[i] = img.Planes[i];
            f.PlaneW[i] = img.PlaneWidth[i];
            f.PlaneH[i] = img.PlaneHeight[i];
        }
        return f;
    }
}
