using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Matdol.Controls;

public sealed class FrameSet : IDisposable
{
    private readonly Bitmap[] _src;
    private Bitmap?[] _scaled;
    private double _scale;

    public double Size { get; }
    public int Count => _src.Length;

    public FrameSet(Bitmap[] src, double sizeDip)
    {
        _src = src; Size = sizeDip; _scaled = new Bitmap?[src.Length];
    }

    private Bitmap Get(int i, double scale)
    {
        if (Math.Abs(scale - _scale) > 1e-3)
        {
            foreach (var b in _scaled) b?.Dispose();
            _scaled = new Bitmap?[_src.Length]; _scale = scale;
        }
        int px = Math.Max(1, (int)Math.Round(Size * scale));
        var src = _src[i];
        if (src.PixelSize.Width == px) return src;
        return _scaled[i] ??= src.CreateScaledBitmap(new PixelSize(px, px), BitmapInterpolationMode.HighQuality);
    }

    public void Prepare(double scale)
    {
        for (int i = 0; i < _src.Length; i++) Get(i, scale);
    }

    public void Draw(DrawingContext ctx, Visual owner, int i, double x, double y) =>
        DrawAt(ctx, i, x, y, TopLevel.GetTopLevel(owner)?.RenderScaling ?? 1);

    public void DrawAt(DrawingContext ctx, int i, double x, double y, double scale)
    {
        var bmp = Get(((i % Count) + Count) % Count, scale);
        double sx = Math.Round(x * scale) / scale, sy = Math.Round(y * scale) / scale;
        double d = bmp.PixelSize.Width / scale;
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            ctx.DrawImage(bmp, new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height), new Rect(sx, sy, d, d));
    }

    public void Dispose()
    {
        foreach (var b in _scaled) b?.Dispose();
        foreach (var b in _src) b.Dispose();
    }
}

public sealed class MillText
{
    private static readonly IBrush NameBrush = new SolidColorBrush(Color.Parse("#7CC8FF"));
    private static readonly IBrush TailBrush = new SolidColorBrush(Color.Parse("#CFE9FF"));
    private static readonly IBrush OutlineBrush = new SolidColorBrush(Color.Parse("#0E1E2C"));
    private static readonly IBrush ShadowBrush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));

    private readonly Geometry _name, _tail;
    private readonly double _tailX, _scale;

    public MillText(string name, string tail, double scale = 1)
    {
        _scale = scale;
        static FormattedText T(string s, FontWeight w, double size) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, w), size, Brushes.White);
        var n = T(name + " ", FontWeight.Bold, 15 * scale);
        var t = T(tail, FontWeight.Medium, 13 * scale);
        _name = n.BuildGeometry(new Point(0, 0)) ?? new StreamGeometry();
        _tail = t.BuildGeometry(new Point(0, n.Baseline - t.Baseline)) ?? new StreamGeometry();
        _tailX = n.WidthIncludingTrailingWhitespace;
    }

    public Rect Bounds => _name.Bounds.Union(_tail.Bounds.Translate(new Vector(_tailX, 0)));

    public void Draw(DrawingContext ctx, double x, double y)
    {
        var b = Bounds;
        x = Math.Round(x - b.X); y = Math.Round(y - b.Y);
        var outline = new Pen(OutlineBrush, 3 * Math.Max(0.7, _scale), lineJoin: PenLineJoin.Round);
        var shadow = new Pen(ShadowBrush, 3.5 * Math.Max(0.7, _scale), lineJoin: PenLineJoin.Round);
        void D(double dx, double dy, IBrush? nf, IBrush? tf, IPen? pen)
        {
            using (ctx.PushTransform(Matrix.CreateTranslation(x + dx, y + dy))) ctx.DrawGeometry(nf, pen, _name);
            using (ctx.PushTransform(Matrix.CreateTranslation(x + dx + _tailX, y + dy))) ctx.DrawGeometry(tf, pen, _tail);
        }
        D(1.2 * _scale, 1.8 * _scale, ShadowBrush, ShadowBrush, shadow);
        D(0, 0, null, null, outline);
        D(0, 0, NameBrush, TailBrush, null);
    }
}

public sealed class MillSpinner : Control
{
    private FrameSet? _frames;
    private DispatcherTimer? _timer;
    private readonly Stopwatch _clock = new();
    private int _index;

    public TimeSpan Turn { get; set; } = TimeSpan.FromSeconds(4);

    public FrameSet? Frames
    {
        get => _frames;
        set { _frames = value; InvalidateMeasure(); InvalidateVisual(); }
    }

    protected override Size MeasureOverride(Size availableSize) => _frames == null ? default : new Size(_frames.Size, _frames.Size);

    public void Start()
    {
        if (_frames == null) return;
        _clock.Restart();
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) =>
        {
            if (_frames == null) return;
            int i = (int)(_clock.Elapsed.TotalSeconds / Turn.TotalSeconds * _frames.Count) % _frames.Count;
            if (i != _index) { _index = i; InvalidateVisual(); }
        });
        _timer.Start();
    }

    public void Stop() { _timer?.Stop(); _clock.Stop(); }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Stop(); base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext ctx) => _frames?.Draw(ctx, this, _index, 0, 0);
}

public sealed class MillLabel : Control
{
    private MillText? _text;
    public MillText? Text { get => _text; set { _text = value; InvalidateMeasure(); InvalidateVisual(); } }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_text == null) return default;
        var b = _text.Bounds;
        return new Size(Math.Ceiling(b.Width + 6), Math.Ceiling(b.Height + 6));
    }

    public override void Render(DrawingContext ctx) => _text?.Draw(ctx, 2, 2);
}
