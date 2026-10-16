using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Matdol.Codecs.Raw;

namespace Matdol.Core;

public sealed class AppSettings
{
    public bool PinTopPanel { get; set; }
    public bool PinBottomPanel { get; set; }

    public CameraRawMode RawMode { get; set; } = CameraRawMode.Preview;

    public bool ShowSplash { get; set; } = true;
    public bool FitWindowToFirstImage { get; set; } = true;

    private static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "matdol", "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static AppSettings Load()
    {
        try
        {
            var p = FilePath;
            if (File.Exists(p)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p), Json) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public static void Save()
    {
        try
        {
            var p = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(Current, Json));
        }
        catch { }
    }
}
