using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Matdol.Core;

[SupportedOSPlatform("windows")]
public static class Win32Clipboard
{
    private const uint CF_DIB = 8, GMEM_MOVEABLE = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);

    public static void SetDib(byte[] bgra, int w, int h)
    {
        int hdr = Marshal.SizeOf<BITMAPINFOHEADER>();
        int total = hdr + bgra.Length;
        var bih = new BITMAPINFOHEADER
        {
            biSize = (uint)hdr, biWidth = w, biHeight = -h,
            biPlanes = 1, biBitCount = 32, biCompression = 0, biSizeImage = (uint)bgra.Length,
        };
        IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)total);
        if (hMem == IntPtr.Zero) return;
        IntPtr p = GlobalLock(hMem);
        Marshal.StructureToPtr(bih, p, false);
        Marshal.Copy(bgra, 0, p + hdr, bgra.Length);
        GlobalUnlock(hMem);

        if (!OpenClipboard(IntPtr.Zero)) { GlobalFree(hMem); return; }
        try { EmptyClipboard(); if (SetClipboardData(CF_DIB, hMem) == IntPtr.Zero) GlobalFree(hMem); }
        finally { CloseClipboard(); }
    }
}
