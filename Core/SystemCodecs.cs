using System;
using System.IO;
using System.Runtime.InteropServices;
using Matdol.Codecs;

namespace Matdol.Core;

public static class SystemCodecs
{
    public static string Name =>
        OperatingSystem.IsWindows() ? "Windows Imaging Component" :
        OperatingSystem.IsMacOS() ? "macOS ImageIO" : "libheif (system)";

    public static bool IsHeifExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".heic" or ".heif" or ".hif" or ".heics" or ".heifs" or ".avif" or ".avifs";
    }

    public static bool HasHeifSignature(ReadOnlySpan<byte> h)
    {
        if (h.Length < 12 || h[4] != 'f' || h[5] != 't' || h[6] != 'y' || h[7] != 'p') return false;
        var brand = System.Text.Encoding.ASCII.GetString(h.Slice(8, 4));
        return brand is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "hevm" or "hevs" or "mif1" or "msf1" or "avif" or "avis" or "mif2";
    }

    public static CodecImage Decode(string path)
    {
        if (OperatingSystem.IsWindows()) return Wic.Decode(path);
        if (OperatingSystem.IsMacOS()) return ImageIO.Decode(path);
        if (OperatingSystem.IsLinux()) return LibHeif.Decode(path);
        throw new CodecFormatException("no system image decoder on this platform");
    }

    public static string InstallHint =>
        OperatingSystem.IsWindows() ? "install \"HEIF Image Extensions\" + \"HEVC Video Extensions\" (AVIF: \"AV1 Video Extension\") from Microsoft Store" :
        OperatingSystem.IsMacOS() ? "macOS decodes HEIC/AVIF natively" :
        "install libheif with its HEVC/AV1 plugins (e.g. apt install libheif1 libheif-plugin-libde265 libheif-plugin-dav1d)";

    private static CodecImage Rgb(int w, int h, ushort[] r, ushort[] g, ushort[] b, int bits, string summary)
    {
        if (bits == 16)
        {
            bool mul257 = true, shl8 = true;
            foreach (var p in new[] { r, g, b })
                for (int i = 0; i < p.Length && (mul257 || shl8); i++) { if (p[i] % 257 != 0) mul257 = false; if ((p[i] & 0xFF) != 0) shl8 = false; }
            if (mul257 || shl8)
            {
                foreach (var p in new[] { r, g, b }) for (int i = 0; i < p.Length; i++) p[i] = (ushort)(mul257 ? p[i] / 257 : p[i] >> 8);
                bits = 8;
            }
        }
        return new CodecImage
        {
            Width = w, Height = h, BitDepth = bits, Model = CodecColorModel.Rgb, Planes = new[] { r, g, b },
            PlaneWidth = new[] { w, w, w }, PlaneHeight = new[] { h, h, h },
            Summary = $"{summary} {bits}-bit RGB",
        };
    }

    private static unsafe class Wic
    {
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
        [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint ctx, in Guid iid, out IntPtr ppv);

        private static readonly Guid ClsidFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
        private static readonly Guid IidFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
        private static readonly Guid Fmt64bppRGBA = new("6fddc324-4e03-4bfe-b185-3d77768dc916");

        private static void** V(IntPtr p) => *(void***)p;
        private static void Release(IntPtr p) { if (p != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(p)[2])(p); }
        private static void Check(int hr, string what)
        {
            if (hr < 0)
            {
                string hint = (uint)hr == 0x88982F50 ? " — no decoder installed for this format (" + InstallHint + ")" : "";
                throw new CodecFormatException($"WIC {what} failed (0x{(uint)hr:X8}){hint}");
            }
        }

        public static CodecImage Decode(string path)
        {
            CoInitializeEx(IntPtr.Zero, 0x0);
            IntPtr factory = IntPtr.Zero, decoder = IntPtr.Zero, frame = IntPtr.Zero, conv = IntPtr.Zero;
            try
            {
                Check(CoCreateInstance(ClsidFactory, IntPtr.Zero, 1  , IidFactory, out factory), "factory");
                fixed (char* wpath = path)
                {
                    var create = (delegate* unmanaged[Stdcall]<IntPtr, char*, Guid*, uint, int, IntPtr*, int>)V(factory)[3];
                    Check(create(factory, wpath, null, 0x80000000  , 0  , &decoder), "open");
                }
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(decoder)[13])(decoder, 0, &frame), "frame");
                uint w, h;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, uint*, int>)V(frame)[3])(frame, &w, &h), "size");
                if (w == 0 || h == 0 || (long)w * h > 1L << 29) throw new CodecFormatException("WIC: bad image size");
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(factory)[10])(factory, &conv), "converter");
                Guid dst = Fmt64bppRGBA;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int, IntPtr, double, int, int>)V(conv)[8])(conv, frame, &dst, 0, IntPtr.Zero, 0.0, 0), "convert");
                int stride = checked((int)w * 8);
                var buf = new ushort[(long)w * h * 4];
                fixed (ushort* pb = buf)
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, void*, uint, uint, byte*, int>)V(conv)[7])(conv, null, (uint)stride, (uint)(buf.Length * 2), (byte*)pb), "copy");
                int n = (int)(w * h);
                var r = new ushort[n]; var g = new ushort[n]; var b = new ushort[n];
                for (int i = 0; i < n; i++) { r[i] = buf[4 * i]; g[i] = buf[4 * i + 1]; b[i] = buf[4 * i + 2]; }
                return Rgb((int)w, (int)h, r, g, b, 16, "WIC");
            }
            finally { Release(conv); Release(frame); Release(decoder); Release(factory); }
        }
    }

    private static unsafe class ImageIO
    {
        private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string IIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
        [StructLayout(LayoutKind.Sequential)] private struct CGRect { public double X, Y, W, H; }

        [DllImport(CF)] private static extern IntPtr CFDataCreate(IntPtr alloc, byte* bytes, nint length);
        [DllImport(CF)] private static extern void CFRelease(IntPtr cf);
        [DllImport(IIO)] private static extern IntPtr CGImageSourceCreateWithData(IntPtr data, IntPtr options);
        [DllImport(IIO)] private static extern nint CGImageSourceGetCount(IntPtr src);
        [DllImport(IIO)] private static extern IntPtr CGImageSourceCreateImageAtIndex(IntPtr src, nint index, IntPtr options);
        [DllImport(CG)] private static extern nint CGImageGetWidth(IntPtr img);
        [DllImport(CG)] private static extern nint CGImageGetHeight(IntPtr img);
        [DllImport(CG)] private static extern nint CGImageGetBitsPerComponent(IntPtr img);
        [DllImport(CG)] private static extern IntPtr CGColorSpaceCreateDeviceRGB();
        [DllImport(CG)] private static extern void CGColorSpaceRelease(IntPtr cs);
        [DllImport(CG)] private static extern IntPtr CGBitmapContextCreate(void* data, nint w, nint h, nint bpc, nint bpr, IntPtr cs, uint info);
        [DllImport(CG)] private static extern void CGContextDrawImage(IntPtr ctx, CGRect rect, IntPtr img);
        [DllImport(CG)] private static extern void CGContextRelease(IntPtr ctx);
        [DllImport(CG)] private static extern void CGImageRelease(IntPtr img);

        public static CodecImage Decode(string path)
        {
            var bytes = File.ReadAllBytes(path);
            IntPtr data = IntPtr.Zero, src = IntPtr.Zero, img = IntPtr.Zero, cs = IntPtr.Zero, ctx = IntPtr.Zero;
            try
            {
                fixed (byte* pb = bytes) data = CFDataCreate(IntPtr.Zero, pb, bytes.Length);
                src = CGImageSourceCreateWithData(data, IntPtr.Zero);
                if (src == IntPtr.Zero || CGImageSourceGetCount(src) < 1) throw new CodecFormatException("ImageIO: unsupported file");
                img = CGImageSourceCreateImageAtIndex(src, 0, IntPtr.Zero);
                if (img == IntPtr.Zero) throw new CodecFormatException("ImageIO: decode failed");
                int w = (int)CGImageGetWidth(img), h = (int)CGImageGetHeight(img);
                bool deep = CGImageGetBitsPerComponent(img) > 8;
                cs = CGColorSpaceCreateDeviceRGB();
                int n = w * h;
                var r = new ushort[n]; var g = new ushort[n]; var b = new ushort[n];
                if (deep)
                {
                    var buf = new ushort[(long)n * 4];
                    fixed (ushort* p = buf)
                    {
                        ctx = CGBitmapContextCreate(p, w, h, 16, w * 8, cs, 5 | (1 << 12));
                        if (ctx == IntPtr.Zero) throw new CodecFormatException("ImageIO: bitmap context failed");
                        CGContextDrawImage(ctx, new CGRect { W = w, H = h }, img);
                    }
                    for (int i = 0; i < n; i++) { r[i] = buf[4 * i]; g[i] = buf[4 * i + 1]; b[i] = buf[4 * i + 2]; }
                    return Rgb(w, h, r, g, b, 16, "ImageIO");
                }
                else
                {
                    var buf = new byte[(long)n * 4];
                    fixed (byte* p = buf)
                    {
                        ctx = CGBitmapContextCreate(p, w, h, 8, w * 4, cs, 5 | (4 << 12));
                        if (ctx == IntPtr.Zero) throw new CodecFormatException("ImageIO: bitmap context failed");
                        CGContextDrawImage(ctx, new CGRect { W = w, H = h }, img);
                    }
                    for (int i = 0; i < n; i++) { r[i] = buf[4 * i]; g[i] = buf[4 * i + 1]; b[i] = buf[4 * i + 2]; }
                    return Rgb(w, h, r, g, b, 8, "ImageIO");
                }
            }
            finally
            {
                if (ctx != IntPtr.Zero) CGContextRelease(ctx);
                if (cs != IntPtr.Zero) CGColorSpaceRelease(cs);
                if (img != IntPtr.Zero) CGImageRelease(img);
                if (src != IntPtr.Zero) CFRelease(src);
                if (data != IntPtr.Zero) CFRelease(data);
            }
        }
    }

    private static unsafe class LibHeif
    {
        [StructLayout(LayoutKind.Sequential)] private struct HeifError { public int Code, Subcode; public IntPtr Message; }

        private static IntPtr _lib; private static bool _probed;
        private static IntPtr Lib()
        {
            if (_probed) return _lib;
            _probed = true;
            foreach (var n in new[] { "libheif.so.1", "libheif.so" })
                if (NativeLibrary.TryLoad(n, out _lib)) return _lib;
            return _lib = IntPtr.Zero;
        }
        private static IntPtr F(string name) => NativeLibrary.GetExport(Lib(), name);

        private static void Check(HeifError e, string what)
        {
            if (e.Code != 0)
            {
                string msg = e.Message != IntPtr.Zero ? Marshal.PtrToStringAnsi(e.Message) ?? "" : "";
                throw new CodecFormatException($"libheif {what}: {msg} (code {e.Code}/{e.Subcode})");
            }
        }

        public static CodecImage Decode(string path)
        {
            if (Lib() == IntPtr.Zero) throw new CodecFormatException("libheif not found — " + InstallHint);
            var alloc = (delegate* unmanaged<IntPtr>)F("heif_context_alloc");
            var free = (delegate* unmanaged<IntPtr, void>)F("heif_context_free");
            var read = (delegate* unmanaged<IntPtr, byte*, IntPtr, HeifError>)F("heif_context_read_from_file");
            var primary = (delegate* unmanaged<IntPtr, IntPtr*, HeifError>)F("heif_context_get_primary_image_handle");
            var lumaBits = (delegate* unmanaged<IntPtr, int>)F("heif_image_handle_get_luma_bits_per_pixel");
            var decode = (delegate* unmanaged<IntPtr, IntPtr*, int, int, IntPtr, HeifError>)F("heif_decode_image");
            var plane = (delegate* unmanaged<IntPtr, int, int*, byte*>)F("heif_image_get_plane_readonly");
            var width = (delegate* unmanaged<IntPtr, int, int>)F("heif_image_get_width");
            var height = (delegate* unmanaged<IntPtr, int, int>)F("heif_image_get_height");
            var relImg = (delegate* unmanaged<IntPtr, void>)F("heif_image_release");
            var relHandle = (delegate* unmanaged<IntPtr, void>)F("heif_image_handle_release");
            const int ColorspaceRgb = 1, ChromaRgb = 10, ChromaRrggbbLe = 14, ChannelInterleaved = 10;

            IntPtr ctx = alloc(), handle = IntPtr.Zero, img = IntPtr.Zero;
            try
            {
                var p8 = System.Text.Encoding.UTF8.GetBytes(path + "\0");
                fixed (byte* pp = p8) Check(read(ctx, pp, IntPtr.Zero), "read");
                Check(primary(ctx, &handle), "primary image");
                int bits = Math.Clamp(lumaBits(handle), 8, 16);
                bool deep = bits > 8;
                Check(decode(handle, &img, ColorspaceRgb, deep ? ChromaRrggbbLe : ChromaRgb, IntPtr.Zero), "decode");
                int stride;
                byte* src = plane(img, ChannelInterleaved, &stride);
                int w = width(img, ChannelInterleaved), h = height(img, ChannelInterleaved);
                if (src == null || w <= 0 || h <= 0) throw new CodecFormatException("libheif: no image plane");
                int n = w * h;
                var r = new ushort[n]; var g = new ushort[n]; var b = new ushort[n];
                for (int y = 0; y < h; y++)
                {
                    byte* row = src + (long)y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (deep) { ushort* q = (ushort*)(row + x * 6); r[i] = q[0]; g[i] = q[1]; b[i] = q[2]; }
                        else { byte* q = row + x * 3; r[i] = q[0]; g[i] = q[1]; b[i] = q[2]; }
                    }
                }
                if (!deep) return Rgb(w, h, r, g, b, 8, "libheif");
                return new CodecImage
                {
                    Width = w, Height = h, BitDepth = bits, Model = CodecColorModel.Rgb, Planes = new[] { r, g, b },
                    PlaneWidth = new[] { w, w, w }, PlaneHeight = new[] { h, h, h }, Summary = $"libheif {bits}-bit RGB",
                };
            }
            finally
            {
                if (img != IntPtr.Zero) relImg(img);
                if (handle != IntPtr.Zero) relHandle(handle);
                free(ctx);
            }
        }
    }
}
