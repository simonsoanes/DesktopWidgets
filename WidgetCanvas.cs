// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DesktopWidgets;

public enum WidgetKind { Clock, Date, Cpu, Gpu, Memory }
public enum ClockMode { Digital, Analog }

/// <summary>
/// Draws every widget kind with pure vector rendering, so widgets stay crisp at any size.
/// The host window feeds it the current time and the latest stats sample.
/// </summary>
public sealed class WidgetCanvas : FrameworkElement
{
    private WidgetKind _kind = WidgetKind.Clock;
    private ClockMode _clockMode = ClockMode.Digital;
    private DateTime _now = DateTime.Now;
    private StatsSnapshot? _stats;

    public WidgetKind Kind
    {
        get => _kind;
        set { _kind = value; InvalidateVisual(); }
    }

    public ClockMode ClockMode
    {
        get => _clockMode;
        set { _clockMode = value; InvalidateVisual(); }
    }

    public DateTime Now
    {
        get => _now;
        set { _now = value; InvalidateVisual(); }
    }

    public StatsSnapshot? Stats
    {
        get => _stats;
        set { _stats = value; InvalidateVisual(); }
    }

    // ---- palette -------------------------------------------------------

    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8)));
    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)));
    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x52)));
    private static readonly Brush FaceBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush SecondBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)));
    private static readonly Brush CpuBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)));
    private static readonly Brush GpuBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x7E, 0xE7, 0x87)));
    private static readonly Brush MemoryBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD2, 0xA8, 0xFF)));

    private static T Frozen<T>(T freezable) where T : System.Windows.Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Brush AccentFor(WidgetKind kind) => kind switch
    {
        WidgetKind.Cpu => CpuBrush,
        WidgetKind.Gpu => GpuBrush,
        WidgetKind.Memory => MemoryBrush,
        _ => AccentBrush,
    };

    // ---- rendering -----------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 8 || h < 8)
            return;

        switch (Kind)
        {
            case WidgetKind.Clock:
                if (ClockMode == ClockMode.Analog)
                    RenderAnalogClock(dc, w, h);
                else
                    RenderDigitalClock(dc, w, h);
                break;
            case WidgetKind.Date:
                RenderDate(dc, w, h);
                break;
            default:
                RenderGauge(dc, w, h);
                break;
        }
    }

    private void RenderDigitalClock(DrawingContext dc, double w, double h)
    {
        string text = Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        var ft = FitText(text, w - 20, h - 16, FontWeights.Light, TextBrush);
        DrawCentered(dc, ft, w / 2, h / 2);
    }

    private void RenderAnalogClock(DrawingContext dc, double w, double h)
    {
        double r = Math.Min(w, h) / 2 - 8;
        if (r < 8)
            return;
        var c = new Point(w / 2, h / 2);

        // Face
        dc.DrawEllipse(FaceBrush, new Pen(TrackBrush, Math.Max(1.5, r * 0.03)), c, r, r);

        // Tick marks
        for (int i = 0; i < 60; i++)
        {
            bool major = i % 5 == 0;
            double angle = i * 6.0 * Math.PI / 180.0;
            var dir = new Vector(Math.Sin(angle), -Math.Cos(angle));
            double outer = r * 0.92;
            double len = major ? r * 0.13 : r * 0.05;
            double thickness = major ? Math.Max(2, r * 0.035) : Math.Max(1, r * 0.015);
            var pen = new Pen(major ? TextBrush : DimBrush, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            dc.DrawLine(pen, c + dir * (outer - len), c + dir * outer);
        }

        // Hands (second hand sweeps smoothly when the host runs a fast timer)
        double seconds = Now.Second + Now.Millisecond / 1000.0;
        double minutes = Now.Minute + seconds / 60.0;
        double hours = Now.Hour % 12 + minutes / 60.0;

        DrawHand(dc, c, hours * 30.0, r * 0.50, Math.Max(3, r * 0.060), TextBrush, r * 0.12);
        DrawHand(dc, c, minutes * 6.0, r * 0.72, Math.Max(2.5, r * 0.045), TextBrush, r * 0.14);
        DrawHand(dc, c, seconds * 6.0, r * 0.82, Math.Max(1.2, r * 0.020), SecondBrush, r * 0.20);

        double cap = Math.Max(2.5, r * 0.05);
        dc.DrawEllipse(TextBrush, null, c, cap, cap);
        dc.DrawEllipse(SecondBrush, null, c, cap * 0.5, cap * 0.5);
    }

    private static void DrawHand(DrawingContext dc, Point c, double angleDeg, double length, double thickness, Brush brush, double tail)
    {
        double a = angleDeg * Math.PI / 180.0;
        var dir = new Vector(Math.Sin(a), -Math.Cos(a));
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        dc.DrawLine(pen, c - dir * tail, c + dir * length);
    }

    private void RenderDate(DrawingContext dc, double w, double h)
    {
        string dayName = Now.ToString("dddd", CultureInfo.CurrentCulture);
        string fullDate = Now.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

        double availW = w - 20;
        var ftDay = FitText(dayName, availW, h * 0.30, FontWeights.SemiBold, AccentBrush);
        var ftDate = FitText(fullDate, availW, h * 0.42, FontWeights.Normal, TextBrush);

        double gap = h * 0.05;
        double totalH = ftDay.Height + gap + ftDate.Height;
        double y = (h - totalH) / 2;

        DrawCentered(dc, ftDay, w / 2, y + ftDay.Height / 2);
        DrawCentered(dc, ftDate, w / 2, y + ftDay.Height + gap + ftDate.Height / 2);
    }

    private void RenderGauge(DrawingContext dc, double w, double h)
    {
        (double percent, bool available, string? subLabel) = Kind switch
        {
            WidgetKind.Cpu => (Stats?.CpuPercent ?? 0, Stats != null, null),
            WidgetKind.Gpu => (Stats?.GpuPercent ?? 0, Stats?.GpuPercent != null, null),
            WidgetKind.Memory => (Stats?.MemoryPercent ?? 0, Stats != null,
                Stats != null ? $"{Stats.MemoryUsedGB:F1} / {Stats.MemoryTotalGB:F0} GB" : null),
            _ => (0, false, null),
        };

        double pad = 10;
        double labelH = Math.Max(14, h * 0.14);
        double gaugeH = h - labelH - pad * 1.5;
        double thick = Math.Max(5, Math.Min(w, gaugeH) * 0.075);
        double r = Math.Min(w / 2 - pad, gaugeH / 2) - thick / 2;
        if (r < 6)
            return;
        var c = new Point(w / 2, pad + gaugeH / 2);

        var trackPen = new Pen(TrackBrush, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, trackPen, ArcGeometry(c, r, 135, 270));

        if (available && percent > 0.5)
        {
            var valuePen = new Pen(AccentFor(Kind), thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, valuePen, ArcGeometry(c, r, 135, 270 * Math.Clamp(percent, 0, 100) / 100));
        }

        // Centre value
        string centerText = available ? $"{percent:F0}%" : "N/A";
        double subH = subLabel != null ? r * 0.30 : 0;
        var ftCenter = FitText(centerText, r * 1.35, r * 0.75, FontWeights.Light, available ? TextBrush : DimBrush);
        double centerY = c.Y - subH * 0.35;
        DrawCentered(dc, ftCenter, c.X, centerY);

        if (subLabel != null)
        {
            var ftSub = FitText(subLabel, r * 1.5, r * 0.28, FontWeights.Normal, DimBrush);
            DrawCentered(dc, ftSub, c.X, centerY + ftCenter.Height * 0.55 + ftSub.Height * 0.45);
        }

        // Bottom label
        string label = Kind switch
        {
            WidgetKind.Cpu => "CPU",
            WidgetKind.Gpu => "GPU",
            WidgetKind.Memory => "MEMORY",
            _ => "",
        };
        var ftLabel = FitText(label, w - 2 * pad, labelH, FontWeights.SemiBold, DimBrush);
        DrawCentered(dc, ftLabel, w / 2, h - pad / 2 - labelH / 2);
    }

    // ---- helpers -------------------------------------------------------

    private static StreamGeometry ArcGeometry(Point center, double radius, double startDeg, double sweepDeg)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointAt(center, radius, startDeg), false, false);
            ctx.ArcTo(PointAt(center, radius, startDeg + sweepDeg),
                new Size(radius, radius), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Point PointAt(Point c, double r, double deg)
    {
        double rad = deg * Math.PI / 180.0;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    private FormattedText MakeText(string text, double size, FontWeight weight, Brush brush)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    /// <summary>Scales text to fit a box, measuring once at a reference size.</summary>
    private FormattedText FitText(string text, double maxW, double maxH, FontWeight weight, Brush brush, double maxSize = 400)
    {
        if (maxW < 4 || maxH < 4)
            maxW = maxH = 4;
        var probe = MakeText(text, 100, weight, brush);
        double scale = Math.Min(maxW / probe.Width, maxH / probe.Height);
        double size = Math.Clamp(100 * scale, 6, maxSize);
        return MakeText(text, size, weight, brush);
    }

    private static void DrawCentered(DrawingContext dc, FormattedText ft, double cx, double cy)
    {
        dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
    }
}
