using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Matdol.Controls;

namespace Matdol.Views;

public sealed class SplashWindow : Window
{
    public const double ArtSize = 192;

    private FrameSet? _still, _frames;
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;
    private readonly SplashArt _art;
    private bool _firstRendered, _animating;

    public event Action? FirstRendered;

    public TimeSpan Elapsed => _clock.Elapsed;
    public bool IsSpinning => _animating;

    public double ExpectedScaling
    {
        get { try { return Screens.Primary?.Scaling ?? 1; } catch { return 1; } }
    }

    public SplashWindow()
    {
        _art = new SplashArt(OnArtRendered);

        Title = AppInfo.Name;
        Icon = AppInfo.Icon;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Opened += (_, _) =>
        {
            if (ActualTransparencyLevel == WindowTransparencyLevel.Transparent) return;
            Background = AppInfo.ViewBackgroundBrush;
            _art.FallbackBackground = true;
        };
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.Manual;
        Width = ArtSize + 96;
        Height = ArtSize + 8;
        Content = _art;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Render, (_, _) =>
        {
            if (_frames == null) return;
            double frameSec = AppInfo.SplashTurn.TotalSeconds / _frames.Count;
            _art.FrameIndex = (int)(_clock.Elapsed.TotalSeconds / frameSec) % _frames.Count;
        });
        Closed += (_, _) => { _timer.Stop(); _frames?.Dispose(); _still?.Dispose(); };
    }

    public void SetStill(FrameSet still) { _still = still; _art.Show(still, 0); }

    public void StartAnimation(FrameSet frames)
    {
        _frames = frames;
        _animating = true;
        _clock.Reset();
        _art.Show(frames, 0);
        _timer.Start();
    }

    private void OnArtRendered()
    {
        if (!_firstRendered) { _firstRendered = true; FirstRendered?.Invoke(); }
        if (_animating && !_clock.IsRunning) _clock.Start();
    }

    private sealed class SplashArt : Control
    {
        private readonly Action _onRendered;
        private readonly MillText _text = new(AppInfo.LabelName, AppInfo.LabelTail);
        private FrameSet? _set;
        private int _index;
        public bool FallbackBackground;

        public SplashArt(Action onRendered) { _onRendered = onRendered; }

        public void Show(FrameSet set, int index) { _set = set; _index = index; InvalidateVisual(); }

        public int FrameIndex
        {
            get => _index;
            set { if (value != _index) { _index = value; InvalidateVisual(); } }
        }

        public override void Render(DrawingContext ctx)
        {
            if (_set == null) return;
            double w = Bounds.Width, ax = (w - ArtSize) / 2;
            if (FallbackBackground)
                ctx.DrawRectangle(AppInfo.ViewBackgroundBrush, null, new RoundedRect(new Rect(Bounds.Size), 16));
            _set.Draw(ctx, this, _index, ax, 0);
            double tx = Math.Min(ax + ArtSize * 0.40, w - _text.Bounds.Width - 6);
            _text.Draw(ctx, tx, ArtSize * 0.855);
            _onRendered();
        }
    }
}
