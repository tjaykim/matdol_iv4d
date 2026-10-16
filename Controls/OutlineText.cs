using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Matdol.Controls;

public sealed class OutlineText : Control
{
    private static readonly Typeface Mono = new(new FontFamily("Consolas,Menlo,DejaVu Sans Mono,monospace"));
    private static readonly IBrush Halo = new SolidColorBrush(Color.FromArgb(235, 0, 0, 0));
    public static readonly IBrush DefaultBrush = new SolidColorBrush(Color.Parse("#FFFFFFFF"));

    private readonly List<(string text, IBrush? brush)> _segs = new();
    private FormattedText? _ft, _shadow;
    private double _builtFor = -1;

    public double FontSize { get; set; } = 13.5;

    public void SetSegments(IEnumerable<(string text, IBrush? brush)> segs)
    {
        _segs.Clear(); _segs.AddRange(segs);
        _ft = _shadow = null;
        InvalidateMeasure(); InvalidateVisual();
    }

    private void Build(double maxWidth)
    {
        if (_ft != null && Math.Abs(_builtFor - maxWidth) < 0.5) return;
        string all = string.Concat(_segs.ConvertAll(s => s.text));
        _ft = new FormattedText(all, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, FontSize, DefaultBrush);
        _shadow = new FormattedText(all, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, FontSize, Halo);
        int pos = 0;
        foreach (var (text, brush) in _segs)
        {
            if (brush != null && text.Length > 0) _ft.SetForegroundBrush(brush, pos, text.Length);
            pos += text.Length;
        }
        if (!double.IsInfinity(maxWidth) && maxWidth > 8)
        {
            _ft.MaxTextWidth = _shadow.MaxTextWidth = maxWidth - 4;
            _ft.MaxLineCount = _shadow.MaxLineCount = 1;
            _ft.Trimming = _shadow.Trimming = TextTrimming.CharacterEllipsis;
        }
        _builtFor = maxWidth;
    }

    protected override Size MeasureOverride(Size available)
    {
        Build(available.Width);
        return new Size(Math.Min(_ft!.WidthIncludingTrailingWhitespace + 4, available.Width), _ft.Height + 4);
    }

    public override void Render(DrawingContext ctx)
    {
        if (_segs.Count == 0) return;
        Build(_builtFor < 0 ? Bounds.Width : _builtFor);
        var o = new Point(2, 2);
        ctx.DrawText(_shadow!, o + new Vector(1.6, 1.8));
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (dx != 0 || dy != 0) ctx.DrawText(_shadow!, o + new Vector(dx, dy));
        ctx.DrawText(_ft!, o);
    }
}
