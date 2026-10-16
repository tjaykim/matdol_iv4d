using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Matdol.Controls;

public delegate void FillBgraDelegate(Span<byte> dst);

public sealed class ImageView : Control
{
    public const double MinZoom = 1.0 / 16, MaxZoom = 256;

    private WriteableBitmap? _bitmap;
    private int _imgW, _imgH;
    private double _zoom = 1;
    private double _ox, _oy;
    private double _rs = 1;
    private double Rs => TopLevel.GetTopLevel(this)?.RenderScaling is { } r && r > 0 ? r : 1;
    private double Dz => _zoom / _rs;
    private bool _dragging;
    private Point _dragStart; private double _dragOx, _dragOy;

    public static FrameSet? EmptyFrames { get; set; }
    public static TimeSpan EmptyTurn { get; set; } = TimeSpan.FromSeconds(4);
    public static TimeSpan EmptyIntroTime { get; set; } = TimeSpan.FromSeconds(2);
    public static IBrush ViewBackground { get; set; } = Brushes.Black;
    public IBrush? Backdrop { get; set; }

    private enum EmptyAnim { None, Intro, Hover }
    private EmptyAnim _anim;
    private Avalonia.Threading.DispatcherTimer? _animTimer;
    private readonly System.Diagnostics.Stopwatch _animClock = new();
    private int _animFrame;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private Rect EmptyIconRect()
    {
        double s = EmptyFrames?.Size ?? 64;
        return new Rect(Math.Round((Bounds.Width - s) / 2), Math.Round((Bounds.Height - s) / 2), s, s);
    }

    public void PlayEmptyIntro() => StartEmptyAnim(EmptyAnim.Intro);

    private void StartEmptyAnim(EmptyAnim mode)
    {
        if (EmptyFrames == null || _bitmap != null) return;
        _anim = mode;
        _animClock.Restart();
        _animTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(15), Avalonia.Threading.DispatcherPriority.Render, (_, _) => TickEmptyAnim());
        _animTimer.Start();
        TickEmptyAnim();
    }

    private void StopEmptyAnim()
    {
        _animTimer?.Stop(); _animClock.Stop();
        _anim = EmptyAnim.None;
        if (_animFrame != 0) { _animFrame = 0; InvalidateVisual(); }
    }

    private void TickEmptyAnim()
    {
        var fs = EmptyFrames;
        if (fs == null || _bitmap != null || _anim == EmptyAnim.None) { StopEmptyAnim(); return; }
        double sec = _animClock.Elapsed.TotalSeconds;
        int n = fs.Count, f;
        if (_anim == EmptyAnim.Intro)
        {
            if (sec >= EmptyIntroTime.TotalSeconds) { StopEmptyAnim(); return; }
            f = n / 2 + (int)(sec / EmptyIntroTime.TotalSeconds * (n - n / 2));
        }
        else f = (int)(sec / EmptyTurn.TotalSeconds * n) % n;
        if (f != _animFrame) { _animFrame = f; InvalidateVisual(); }
    }

    private void UpdateEmptyHover(Point? p)
    {
        if (_bitmap != null || EmptyFrames == null || Spinning) return;
        bool over = p is { } q && EmptyIconRect().Contains(q);
        Cursor = over ? HandCursor : null;
        if (over && _anim != EmptyAnim.Hover) StartEmptyAnim(EmptyAnim.Hover);
        else if (!over && _anim == EmptyAnim.Hover) StopEmptyAnim();
    }

    private bool _busy;
    private Avalonia.Threading.DispatcherTimer? _busyTimer;
    private readonly System.Diagnostics.Stopwatch _busyClock = new();
    private int _busyFrame;
    public static TimeSpan BusyTurn { get; set; } = TimeSpan.FromSeconds(2);

    private bool _working;

    public bool Busy
    {
        get => _busy;
        set { if (_busy == value) return; bool was = Spinning; _busy = value; SpinChanged(was); }
    }

    public bool Working
    {
        get => _working;
        set { if (_working == value) return; bool was = Spinning; _working = value; SpinChanged(was); }
    }

    private bool Spinning => _busy || _working;

    private void SpinChanged(bool was)
    {
        bool now = Spinning;
        if (now && !was)
        {
            StopEmptyAnim();
            _busyFrame = 0; _busyClock.Restart();
            _busyTimer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(15), Avalonia.Threading.DispatcherPriority.Render, (_, _) =>
            {
                var fs = EmptyFrames; if (fs == null) return;
                int f = (int)(_busyClock.Elapsed.TotalSeconds / BusyTurn.TotalSeconds * fs.Count) % fs.Count;
                if (f != _busyFrame) { _busyFrame = f; InvalidateVisual(); }
            });
            _busyTimer.Start();
            Cursor = new Cursor(StandardCursorType.Wait);
        }
        else if (!now && was) { _busyTimer?.Stop(); _busyClock.Stop(); Cursor = null; }
        InvalidateVisual();
    }

    private static readonly IBrush BusyBackdrop = new SolidColorBrush(Color.FromArgb(150, 0x10, 0x18, 0x1B));

    private void DrawBusy(DrawingContext ctx)
    {
        if (!Spinning || EmptyFrames == null) return;
        var r = EmptyIconRect();
        if (r.X < 0 || r.Y < 0) return;
        if (_bitmap != null) ctx.DrawRectangle(BusyBackdrop, null, new RoundedRect(r.Inflate(14), 16));
        EmptyFrames.Draw(ctx, this, _busyFrame, r.X, r.Y);
    }

    public bool HasImage => _bitmap != null;

    public bool ShowGrid { get; set; }
    public int GridW { get; set; } = 8;
    public int GridH { get; set; } = 8;
    public int SplitX { get; set; }
    public int SplitY { get; set; }
    public double Zoom => _zoom;

    public event Action<(int x, int y)?, Point>? HoverPixelChanged;
    public event Action? ZoomChanged;
    public event Action<ImageView>? ViewChanged;
    private bool _applying;
    public event Action<ImageView>? Clicked;
    public event Action<ImageView>? SelectB;
    public event Action<ImageView>? OpenRequested;
    public event Action<ImageView>? RightClicked;

    public ImageView()
    {
        ClipToBounds = true;
        Focusable = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public void SetFrame(int w, int h, FillBgraDelegate fillBgra)
    {
        if (_bitmap == null) { Cursor = null; StopEmptyAnim(); }
        if (_bitmap == null || _imgW != w || _imgH != h)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _imgW = w; _imgH = h;
            ClampOffset();
        }
        using (var fb = _bitmap.Lock())
        {
            unsafe { fillBgra(new Span<byte>((void*)fb.Address, fb.RowBytes * h)); }
        }
        InvalidateVisual();
    }

    public byte[]? CopyBgra()
    {
        if (_bitmap == null) return null;
        var buf = new byte[_imgW * _imgH * 4];
        using var fb = _bitmap.Lock();
        unsafe { new Span<byte>((void*)fb.Address, buf.Length).CopyTo(buf); }
        return buf;
    }

    public void FitToView()
    {
        if (_imgW == 0 || Bounds.Width <= 0) { _zoom = 1; _ox = _oy = 0; return; }
        _rs = Rs;
        double z = Math.Min(Bounds.Width * _rs / _imgW, Bounds.Height * _rs / _imgH);
        _zoom = Math.Clamp(z >= 1 ? 1 : z, MinZoom, 1);
        _ox = _oy = 0; ClampOffset(); ZoomChanged?.Invoke(); InvalidateVisual();
    }

    public void ResetZoom() => SetZoom(1);

    public void SetZoom(double zoom) => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), Quantize(zoom) / _zoom);

    public static double Quantize(double z) => Math.Pow(2, Math.Round(Math.Log2(Math.Clamp(z, MinZoom, MaxZoom))));

    public void ZoomAt(Point p, double factor)
    {
        double nz = Quantize(_zoom * factor);
        if (Math.Abs(nz - _zoom) < 1e-9) return;
        _rs = Rs;
        double ix = _ox + p.X / Dz, iy = _oy + p.Y / Dz;
        _zoom = nz;
        _ox = ix - p.X / Dz; _oy = iy - p.Y / Dz;
        ClampOffset(); ZoomChanged?.Invoke(); InvalidateVisual(); RaiseViewChanged();
    }

    private void ClampOffset()
    {
        double vw = Bounds.Width / Dz, vh = Bounds.Height / Dz;
        _ox = vw >= _imgW ? -Math.Round((vw - _imgW) / 2 * _zoom) / _zoom : Math.Clamp(_ox, 0, _imgW - vw);
        _oy = vh >= _imgH ? -Math.Round((vh - _imgH) / 2 * _zoom) / _zoom : Math.Clamp(_oy, 0, _imgH - vh);
    }

    private void SyncScaling()
    {
        double r = Rs;
        if (Math.Abs(r - _rs) < 1e-6) return;
        _rs = r; ClampOffset();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _rs = Rs; ClampOffset();
    }

    public void ApplyView(double zoom, double ox, double oy)
    {
        _applying = true;
        _rs = Rs;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom); _ox = ox; _oy = oy;
        ClampOffset(); ZoomChanged?.Invoke(); InvalidateVisual();
        _applying = false;
    }
    public (double zoom, double ox, double oy) GetView() => (_zoom, _ox, _oy);
    private void RaiseViewChanged() { if (!_applying) ViewChanged?.Invoke(this); }

    public void Clear()
    {
        _bitmap?.Dispose(); _bitmap = null; _imgW = _imgH = 0; StopEmptyAnim(); InvalidateVisual();
    }

    public Rect? VisibleImageRect()
    {
        if (_bitmap == null || _imgW == 0 || _imgH == 0) return null;
        double z = Dz;
        var r = new Rect(-_ox * z, -_oy * z, _imgW * z, _imgH * z).Intersect(new Rect(Bounds.Size));
        return r.Width > 0 && r.Height > 0 ? r : null;
    }

    public (int x, int y)? ScreenToImage(Point p)
    {
        if (_imgW == 0) return null;
        double z = Dz;
        int x = (int)Math.Floor(_ox + p.X / z), y = (int)Math.Floor(_oy + p.Y / z);
        return (x >= 0 && y >= 0 && x < _imgW && y < _imgH) ? (x, y) : null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _rs = Rs; ClampOffset(); InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var pt = e.GetCurrentPoint(this);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (pt.Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2 && !ctrl) { _dragging = false; OpenRequested?.Invoke(this); }
            else if (!ctrl && _bitmap == null && EmptyFrames != null && EmptyIconRect().Contains(pt.Position))
            {
                Clicked?.Invoke(this); _dragging = false; OpenRequested?.Invoke(this);
            }
            else if (ctrl) SelectB?.Invoke(this);
            else { Clicked?.Invoke(this); _dragging = true; _dragStart = pt.Position; _dragOx = _ox; _dragOy = _oy; e.Pointer.Capture(this); }
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        UpdateEmptyHover(p);
        if (_dragging)
        {
            _ox = _dragOx - (p.X - _dragStart.X) / Dz;
            _oy = _dragOy - (p.Y - _dragStart.Y) / Dz;
            ClampOffset(); InvalidateVisual(); RaiseViewChanged();
        }
        HoverPixelChanged?.Invoke(ScreenToImage(p), p);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragging) { _dragging = false; e.Pointer.Capture(null); }
        if (e.InitialPressMouseButton == MouseButton.Right && !e.KeyModifiers.HasFlag(KeyModifiers.Control)) { RightClicked?.Invoke(this); e.Handled = true; }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        UpdateEmptyHover(null);
        HoverPixelChanged?.Invoke(null, default);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(this), e.Delta.Y > 0 ? 2 : 0.5);
        e.Handled = true;
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Backdrop ?? ViewBackground, new Rect(Bounds.Size));
        if (_bitmap == null)
        {
            var r = EmptyIconRect();
            if (Spinning) DrawBusy(ctx);
            else if (EmptyFrames != null && r.X >= 0 && r.Y >= 0)
                EmptyFrames.Draw(ctx, this, _animFrame, r.X, r.Y);
            return;
        }

        SyncScaling();
        double z = Dz, rs = _rs;
        double vw = Bounds.Width / z, vh = Bounds.Height / z;
        double sx = Math.Max(0, _ox), sy = Math.Max(0, _oy);
        double ex = Math.Min(_imgW, _ox + vw), ey = Math.Min(_imgH, _oy + vh);
        if (ex <= sx || ey <= sy) { DrawBusy(ctx); return; }
        var src = new Rect(sx, sy, ex - sx, ey - sy);
        var dst = new Rect((sx - _ox) * z, (sy - _oy) * z, src.Width * z, src.Height * z);
        ctx.DrawImage(_bitmap, src, dst);

        if (ShowGrid && GridW > 0 && GridH > 0)
        {
            const double MinGap = 4;
            int stepX = GridW, kx = 0, stepY = GridH, ky = 0;
            while (stepX * _zoom < MinGap && kx < 12) { stepX *= 2; kx++; }
            while (stepY * _zoom < MinGap && ky < 12) { stepY *= 2; ky++; }
            byte alpha = (byte)Math.Max(40, 100 - Math.Max(kx, ky) * 15);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 255, 200, 0)), 1 / rs);
            foreach (var (o, e) in Regions(SplitX, _imgW))
                for (int gx = o + Math.Max(0, ((int)sx - o) / stepX) * stepX; gx <= Math.Min(e, ex); gx += stepX)
                {
                    double X = PixelLine(gx - _ox);
                    ctx.DrawLine(pen, new Point(X, dst.Top), new Point(X, dst.Bottom));
                }
            foreach (var (o, e) in Regions(SplitY, _imgH))
                for (int gy = o + Math.Max(0, ((int)sy - o) / stepY) * stepY; gy <= Math.Min(e, ey); gy += stepY)
                {
                    double Y = PixelLine(gy - _oy);
                    ctx.DrawLine(pen, new Point(dst.Left, Y), new Point(dst.Right, Y));
                }
        }

        if (SplitX > 0 || SplitY > 0)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(170, 0x17, 0xA2, 0xB8)), 1 / rs);
            if (SplitX > 0 && SplitX < _imgW && SplitX >= sx && SplitX <= ex)
            {
                double X = PixelLine(SplitX - _ox);
                ctx.DrawLine(pen, new Point(X, dst.Top), new Point(X, dst.Bottom));
            }
            if (SplitY > 0 && SplitY < _imgH && SplitY >= sy && SplitY <= ey)
            {
                double Y = PixelLine(SplitY - _oy);
                ctx.DrawLine(pen, new Point(dst.Left, Y), new Point(dst.Right, Y));
            }
        }
        DrawBusy(ctx);
    }

    private double PixelLine(double d) => (Math.Round(d * _zoom) + 0.5) / _rs;

    private static (int start, int end)[] Regions(int split, int len) =>
        split > 0 && split < len ? new[] { (0, split), (split, len) } : new[] { (0, len) };
}
