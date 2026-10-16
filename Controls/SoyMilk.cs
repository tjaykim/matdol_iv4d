using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Matdol.Controls;

public sealed class SoyMilkSim
{
    public static readonly Color Milk = Color.Parse("#F6EFDC");
    public static readonly Color MilkDeep = Color.Parse("#E2D2AE");
    private const double MeanInterval = 1.2;
    private const double FormTime = 0.75;
    private const double Gravity = 900;

    public sealed class Drop { public double X, Y, TipY, Vy, R, Age; public bool Falling; }
    public sealed class Speck { public double X, Y, Vx, Vy, R; }
    private sealed class Ripple { public double X, Amp, Age; }

    private readonly Random _rnd = new();
    private readonly List<Ripple> _ripples = new();
    private double _t, _nextDrop;

    public readonly List<Drop> Drops = new();
    public readonly List<Speck> Splashes = new();
    public readonly List<Speck> Bubbles = new();
    public double Level { get; private set; }
    public Size Box { get; set; }
    public Point[] Tips { get; set; } = Array.Empty<Point>();

    public SoyMilkSim() => Reset();

    public void Reset()
    {
        Level = 0; _t = 0; _nextDrop = 0.8;
        Drops.Clear(); Splashes.Clear(); Bubbles.Clear(); _ripples.Clear();
    }

    private double Depth => Box.Height * Level;

    public double SurfaceY(double x)
    {
        double amp = Math.Min(1, Depth / 14.0);
        double y = Box.Height - Depth
                   + amp * (1.7 * Math.Sin(x * 0.029 + _t * 1.20) + 1.0 * Math.Sin(x * 0.067 - _t * 0.85) + 0.7 * Math.Sin(x * 0.012 + _t * 0.45));
        foreach (var r in _ripples)
        {
            double d = x - r.X, w = 10 + 45 * r.Age;
            y += r.Amp * Math.Exp(-r.Age * 2.0) * Math.Exp(-d * d / (2 * w * w)) * Math.Cos(d * 0.22 - r.Age * 8);
        }
        return y;
    }

    public void Step(double dt)
    {
        _t += dt;
        double fillTime = AppInfo.HelpMilkFillTime.TotalSeconds;

        _nextDrop -= dt;
        if (_nextDrop <= 0 && Tips.Length > 0)
        {
            _nextDrop = MeanInterval * (0.6 + 0.8 * _rnd.NextDouble());
            double p = _rnd.NextDouble();
            var tip = Tips[p < 0.6 || Tips.Length == 1 ? 0 : p < 0.8 || Tips.Length == 2 ? 1 : 2];
            if (tip.Y < SurfaceY(tip.X) - 4)
            {
                if (!Drops.Exists(d => !d.Falling)) Drops.Add(new Drop { X = tip.X, TipY = tip.Y, Y = tip.Y });
            }
            else
            {
                Level = Math.Min(1, Level + MeanInterval / fillTime);
                if (Level < 1) Bubbles.Add(new Speck { X = tip.X + (_rnd.NextDouble() - 0.5) * 6, Y = tip.Y + 4, R = 1.2 + _rnd.NextDouble(), Vy = -(10 + 10 * _rnd.NextDouble()) });
            }
        }

        for (int i = Drops.Count - 1; i >= 0; i--)
        {
            var d = Drops[i];
            d.Age += dt;
            if (!d.Falling)
            {
                d.R = 3.3 * Math.Min(1, d.Age / FormTime);
                d.Y = d.TipY + d.R * 0.9;
                if (d.Age >= FormTime) { d.Falling = true; d.Vy = 15; }
                continue;
            }
            d.Vy += Gravity * dt; d.Y += d.Vy * dt;
            double s = SurfaceY(d.X);
            if (d.Y + d.R < s) continue;
            Drops.RemoveAt(i);
            Level = Math.Min(1, Level + MeanInterval / fillTime);
            if (_ripples.Count >= 6) _ripples.RemoveAt(0);
            _ripples.Add(new Ripple { X = d.X, Amp = 2.6 });
            int n = 2 + _rnd.Next(3);
            for (int k = 0; k < n; k++)
                Splashes.Add(new Speck
                {
                    X = d.X, Y = s - 1, R = 0.9 + 0.7 * _rnd.NextDouble(),
                    Vx = (_rnd.NextDouble() < 0.5 ? -1 : 1) * (25 + 70 * _rnd.NextDouble()),
                    Vy = -(80 + 110 * _rnd.NextDouble()),
                });
            if (Depth > 16 && _rnd.NextDouble() < 0.35)
                Bubbles.Add(new Speck { X = d.X + (_rnd.NextDouble() - 0.5) * 10, Y = s + 8 + 6 * _rnd.NextDouble(), R = 1 + _rnd.NextDouble(), Vy = -(6 + 8 * _rnd.NextDouble()) });
        }

        for (int i = Splashes.Count - 1; i >= 0; i--)
        {
            var p = Splashes[i];
            p.Vy += Gravity * dt; p.X += p.Vx * dt; p.Y += p.Vy * dt;
            if (p.Vy > 0 && p.Y >= SurfaceY(p.X)) Splashes.RemoveAt(i);
        }
        for (int i = Bubbles.Count - 1; i >= 0; i--)
        {
            var b = Bubbles[i];
            b.Y += b.Vy * dt; b.X += Math.Sin(_t * 3 + b.Vy) * 4 * dt;
            if (b.Y <= SurfaceY(b.X) + 1.5)
            {
                Bubbles.RemoveAt(i);
                if (_ripples.Count < 6) _ripples.Add(new Ripple { X = b.X, Amp = 0.7 });
            }
        }
        for (int i = _ripples.Count - 1; i >= 0; i--)
            if ((_ripples[i].Age += dt) > 2.5) _ripples.RemoveAt(i);
    }
}

public sealed class SoyMilkPool : Control
{
    private static readonly IBrush Body = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(0xF0, SoyMilkSim.Milk.R, SoyMilkSim.Milk.G, SoyMilkSim.Milk.B), 0),
                          new GradientStop(Color.FromArgb(0xF4, SoyMilkSim.MilkDeep.R, SoyMilkSim.MilkDeep.G, SoyMilkSim.MilkDeep.B), 1) },
    };
    private static readonly IPen Shine = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 250)), 1.4);
    private static readonly IPen BubblePen = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xC9, 0xB7, 0x8C)), 0.8);
    private static readonly IBrush BubbleFill = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 250));

    public SoyMilkSim? Sim { get; set; }
    public double CornerRadius { get; set; } = 9;

    public override void Render(DrawingContext ctx)
    {
        var sim = Sim; if (sim == null || sim.Box.Width <= 0) return;
        double w = Bounds.Width, h = Bounds.Height;
        using var clip = ctx.PushClip(new RoundedRect(new Rect(Bounds.Size), CornerRadius));

        const double Step = 6;
        var body = new StreamGeometry();
        var line = new StreamGeometry();
        using (var g = body.Open())
        using (var l = line.Open())
        {
            g.BeginFigure(new Point(0, h + 2), true);
            for (double x = 0; ; x += Step)
            {
                double xx = Math.Min(x, w);
                var pt = new Point(xx, sim.SurfaceY(xx));
                g.LineTo(pt);
                if (x == 0) l.BeginFigure(pt, false); else l.LineTo(pt);
                if (xx >= w) break;
            }
            g.LineTo(new Point(w, h + 2));
            g.EndFigure(true);
            l.EndFigure(false);
        }
        ctx.DrawGeometry(Body, null, body);
        ctx.DrawGeometry(null, Shine, line);
        foreach (var b in sim.Bubbles) ctx.DrawEllipse(BubbleFill, BubblePen, new Point(b.X, b.Y), b.R, b.R);
    }
}

public sealed class SoyMilkDrops : Control
{
    private static readonly IBrush DropFill = new SolidColorBrush(SoyMilkSim.Milk);
    private static readonly IPen DropPen = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0xB8, 0xA5, 0x7A)), 0.7);
    private static readonly IBrush Gleam = new SolidColorBrush(Color.FromArgb(0xD0, 255, 255, 255));

    public SoyMilkSim? Sim { get; set; }

    public override void Render(DrawingContext ctx)
    {
        var sim = Sim; if (sim == null) return;
        using var clip = ctx.PushClip(new Rect(Bounds.Size));
        foreach (var d in sim.Drops)
        {
            if (d.R <= 0.2) continue;
            double rx = d.Falling ? d.R * 0.82 : d.R * 0.9;
            double ry = d.Falling ? d.R * (1.25 + Math.Min(0.8, d.Vy / 900)) : d.R;
            var c = new Point(d.X, d.Y);
            if (!d.Falling)
                ctx.DrawRectangle(DropFill, null, new Rect(d.X - rx * 0.35, d.TipY - 0.5, rx * 0.7, Math.Max(0, d.Y - d.TipY)));
            ctx.DrawEllipse(DropFill, DropPen, c, rx, ry);
            ctx.DrawEllipse(Gleam, null, new Point(d.X - rx * 0.35, d.Y - ry * 0.35), rx * 0.25, ry * 0.22);
        }
        foreach (var p in sim.Splashes) ctx.DrawEllipse(DropFill, null, new Point(p.X, p.Y), p.R, p.R);
    }
}
