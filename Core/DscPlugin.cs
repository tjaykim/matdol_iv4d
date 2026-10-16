using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Matdol.Codecs;

namespace Matdol.Core;

public static class DscPlugin
{
    public const string FileName = "Matdol.Dsc.dll";
    private static bool _probed;
    private static Assembly? _asm;
    private static Type? _file, _lib, _legacy;
    private static string? _loadError;

    public static string? FoundPath { get { Probe(); return _found; } }
    private static string? _found;

    public static bool IsAvailable { get { Probe(); return _file != null || _legacy != null; } }

    public static string? Version
    {
        get
        {
            Probe();
            try
            {
                var t = _lib ?? _legacy;
                var p = t?.GetProperty("Version", BindingFlags.Public | BindingFlags.Static) ?? t?.GetProperty("LibraryVersion", BindingFlags.Public | BindingFlags.Static);
                return p?.GetValue(null) as string ?? _asm?.GetName().Version?.ToString(3);
            }
            catch { return null; }
        }
    }

    public static string MissingReason { get { Probe(); return _loadError ?? $"DSC plugin not installed ({FileName} not found next to matdol or in {AppInfo.UserPluginDir})"; } }

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "plugins"), AppInfo.UserPluginDir })
        {
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) continue;
            _found = path;
            try
            {
                _asm = Assembly.LoadFrom(path);
                _file = _asm.GetType("Matdol.Dsc.DscFile");
                _lib = _asm.GetType("Matdol.Dsc.DscLibrary");
                _legacy = _file == null ? _asm.GetType("Matdol.Dsc.DscDecoder") : null;
                if (_file == null && _legacy == null) _loadError = $"DSC plugin: {FileName} has no Matdol.Dsc.DscFile (incompatible version?)";
                return;
            }
            catch (Exception ex) { _loadError = $"DSC plugin load failed ({ex.GetType().Name}: {ex.Message})"; }
        }
    }

    private static readonly ConditionalWeakTable<byte[], object> _files = new();

    private static object Open(byte[] file)
    {
        if (_files.TryGetValue(file, out var f)) return f;
        var m = _file!.GetMethod("FromBytes", BindingFlags.Public | BindingFlags.Static, new[] { typeof(byte[]) })
                ?? throw new CodecFormatException("DSC plugin: DscFile.FromBytes not found (incompatible plugin version?)");
        f = Invoke(() => m.Invoke(null, new object[] { file }))!;
        _files.AddOrUpdate(file, f);
        return f;
    }

    public static int CountFrames(byte[] file)
    {
        Probe();
        if (_file != null) { var f = Open(file); return (int)_file.GetProperty("FrameCount")!.GetValue(f)!; }
        return (int)CallLegacy("CountFrames", new[] { typeof(byte[]) }, file)!;
    }

    public static CodecImage Decode(byte[] file, int frameIndex)
    {
        Probe();
        if (_file == null) return DecodeLegacy(file, frameIndex);
        var f = Open(file);
        var dec = _file.GetMethod("Decode", new[] { typeof(int) }) ?? throw new CodecFormatException("DSC plugin: DscFile.Decode not found");
        object pic = Invoke(() => dec.Invoke(f, new object[] { frameIndex }))!;
        var t = pic.GetType();
        object? P(string n) => t.GetProperty(n)?.GetValue(pic);
        var planes = ((IEnumerable)P("Planes")!).Cast<ushort[]>().ToArray();
        var pw = ((IEnumerable)P("PlaneWidths")!).Cast<int>().ToArray();
        var ph = ((IEnumerable)P("PlaneHeights")!).Cast<int>().ToArray();
        string layout = P("Layout")?.ToString() ?? "";
        var notes = P("Notes") is IEnumerable ns ? ns.Cast<object>().Select(o => o.ToString() ?? "").Where(s => s.Length > 0).ToList() : new List<string>();
        string summary = (P("Summary") as string ?? "VESA DSC") + (notes.Count > 0 ? " · " + string.Join(" · ", notes) : "");
        IReadOnlyList<KeyValuePair<string, string>> details = Array.Empty<KeyValuePair<string, string>>();
        try
        {
            var pps = _file.GetMethod("ParametersOf", new[] { typeof(int) })?.Invoke(f, new object[] { frameIndex });
            if (pps?.GetType().GetMethod("Describe", Type.EmptyTypes)?.Invoke(pps, null) is IReadOnlyList<KeyValuePair<string, string>> d) details = d;
        }
        catch { }
        return new CodecImage
        {
            Width = (int)P("Width")!,
            Height = (int)P("Height")!,
            BitDepth = (int)P("BitsPerComponent")!,
            Model = ModelOf(layout),
            Planes = planes, PlaneWidth = pw, PlaneHeight = ph,
            Summary = summary, Details = details,
        };
    }

    private static CodecColorModel ModelOf(string name) => name switch
    {
        "Rgb" => CodecColorModel.Rgb,
        "YCbCr444" => CodecColorModel.YCbCr444,
        "YCbCr422" => CodecColorModel.YCbCr422,
        "YCbCr420" => CodecColorModel.YCbCr420,
        _ => throw new CodecFormatException("DSC plugin: unknown plane layout " + name),
    };

    public static bool HasSignature(ReadOnlySpan<byte> head) =>
        head.Length >= 4 && head[0] == (byte)'D' && head[1] == (byte)'S' && head[2] == (byte)'C' && head[3] == (byte)'F';

    private static object? Invoke(Func<object?> call)
    {
        if (!IsAvailable) throw new CodecFormatException(MissingReason);
        try { return call(); }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { throw new CodecFormatException(ex.InnerException.Message); }
    }

    private static object? CallLegacy(string method, Type[] sig, params object[] args)
    {
        var d = _legacy ?? throw new CodecFormatException(MissingReason);
        var m = d.GetMethod(method, BindingFlags.Public | BindingFlags.Static, sig)
                ?? throw new CodecFormatException($"DSC plugin: {method} not found (incompatible plugin version?)");
        return Invoke(() => m.Invoke(null, args));
    }

    private static CodecImage DecodeLegacy(byte[] file, int frameIndex)
    {
        object img = CallLegacy("Decode", new[] { typeof(byte[]), typeof(int) }, file, frameIndex)!;
        var t = img.GetType();
        T Get<T>(string name) => (T)t.GetProperty(name)!.GetValue(img)!;
        return new CodecImage
        {
            Width = Get<int>("Width"), Height = Get<int>("Height"), BitDepth = Get<int>("BitDepth"),
            Model = ModelOf(Get<object>("Model").ToString() ?? ""),
            Planes = Get<ushort[][]>("Planes"), PlaneWidth = Get<int[]>("PlaneWidth"), PlaneHeight = Get<int[]>("PlaneHeight"),
            Summary = Get<string>("Summary"), Details = Get<IReadOnlyList<KeyValuePair<string, string>>>("Details"),
        };
    }
}
