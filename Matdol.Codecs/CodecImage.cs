using System;
using System.Collections.Generic;

namespace Matdol.Codecs;

public enum CodecColorModel
{
    Rgb,
    YCbCr444,
    YCbCr422,
    YCbCr420,
    Gray,
}

public enum CodecMatrixHint { Unknown, Bt601, Bt709, Bt2020 }

public sealed class CodecImage
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitDepth { get; init; }
    public CodecColorModel Model { get; init; }
    public ushort[][] Planes { get; init; } = Array.Empty<ushort[]>();
    public int[] PlaneWidth { get; init; } = Array.Empty<int>();
    public int[] PlaneHeight { get; init; } = Array.Empty<int>();

    public bool? LimitedRangeHint { get; init; }
    public CodecMatrixHint MatrixHint { get; init; }

    public string Summary { get; init; } = "";
    public IReadOnlyList<KeyValuePair<string, string>> Details { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    public int MaxValue => (1 << BitDepth) - 1;
}

public sealed class CodecFormatException : Exception
{
    public CodecFormatException(string message) : base(message) { }
}
