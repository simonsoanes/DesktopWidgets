// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopWidgets;

/// <summary>
/// A transparent, borderless widget window that is pinned to the bottom of the Z-order
/// (the opposite of always-on-top): it never covers other windows and never steals focus.
/// </summary>
public sealed class WidgetWindow : Window
{
    private readonly WidgetConfig _cfg;
    private readonly WidgetCanvas _canvas;
    private readonly Thumb _grip;
    private DispatcherTimer? _fastTimer;
    private bool _loaded;
    private bool _dragging;
    private bool _menuOpen;
    private IntPtr _hwnd;
    private Native.POINT _dragStartCursor;
    private Native.RECT _dragStartRect;

    /// <summary>All live widget windows, so dragging can snap against the others.</summary>
    internal static readonly List<WidgetWindow> All = new();

    /// <summary>Raised (debounced by the app) whenever position/size/options change.</summary>
    public event Action? Changed;

    /// <summary>Raised only when the user closes this widget from its menu - never on app shutdown.</summary>
    public event Action? RemoveRequested;

    public WidgetConfig Config => _cfg;

    public WidgetWindow(WidgetConfig cfg)
    {
        _cfg = cfg;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        MinWidth = 90;
        MinHeight = 70;
        Title = $"Desktop Widgets - {cfg.Kind}";
        Opacity = Math.Clamp(cfg.Opacity, 0.15, 1.0);
        cfg.HoverOpacity = Math.Clamp(cfg.HoverOpacity, 0.15, 1.0);
        cfg.ContentOpacity = Math.Clamp(cfg.ContentOpacity, 0.15, 1.0);

        if (double.IsNaN(cfg.Width) || cfg.Width <= 0 || double.IsNaN(cfg.Height) || cfg.Height <= 0)
        {
            var def = DefaultSize(cfg.Kind, cfg.ClockMode);
            cfg.Width = def.Width;
            cfg.Height = def.Height;
        }
        Width = Math.Max(MinWidth, cfg.Width);
        Height = Math.Max(MinHeight, cfg.Height);

        // Clamp into the virtual screen so a widget saved on a now-disconnected monitor stays reachable.
        double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        Left = double.IsNaN(cfg.X) ? vx + vw / 2 - Width / 2 : Math.Clamp(cfg.X, vx, vx + vw - Width);
        Top = double.IsNaN(cfg.Y) ? vy + vh / 2 - Height / 2 : Math.Clamp(cfg.Y, vy, vy + vh - Height);
        cfg.X = Left;
        cfg.Y = Top;

        _canvas = new WidgetCanvas { Kind = cfg.Kind, ClockMode = cfg.ClockMode, Drive = cfg.Drive ?? DefaultDrive };
        _canvas.Opacity = Math.Clamp(cfg.ContentOpacity, 0.15, 1.0);

        _grip = new Thumb
        {
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 3, 3),
            Cursor = Cursors.SizeNWSE,
            Opacity = 0.3,
            Template = BuildGripTemplate(),
        };
        _grip.DragDelta += OnGripDrag;
        _grip.DragCompleted += (_, _) => { SettlePosition(); Changed?.Invoke(); };

        var grid = new Grid();
        grid.Children.Add(_canvas);
        grid.Children.Add(_grip);

        Content = WidgetChrome.CreateCard(grid);

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseRightButtonUp += OnMouseRightButtonUp;
        MouseEnter += (_, _) => { _grip.Opacity = 1.0; ApplyOpacity(); };
        MouseLeave += (_, _) => { _grip.Opacity = 0.3; ApplyOpacity(); };
        LocationChanged += (_, _) => SyncGeometry();
        SizeChanged += (_, _) => SyncGeometry();
        Loaded += (_, _) => { _loaded = true; UpdateClockTimer(); };
        Closed += (_, _) => { _fastTimer?.Stop(); All.Remove(this); };
        StateChanged += (_, _) =>
        {
            // Win+D / Win+M try to minimise everything; a desktop widget should stay put.
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
        };

        All.Add(this);
        UpdateClockTimer();
    }

    public static Size DefaultSize(WidgetKind kind, ClockMode mode) => kind switch
    {
        WidgetKind.Clock => mode == ClockMode.Analog ? new Size(200, 200) : new Size(260, 120),
        WidgetKind.Date => new Size(280, 120),
        _ => new Size(170, 170),
    };

    internal static string DefaultDrive => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

    /// <summary>Called once a second by the app with the shared time and stats sample.</summary>
    public void Tick(DateTime now, StatsSnapshot? stats)
    {
        _canvas.Now = now;
        _canvas.Stats = stats;
    }

    // ---- always-behind interop ----------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _hwnd = hwnd;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, exStyle | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);

        Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_WINDOWPOSCHANGING:
            {
                var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
                if ((pos.flags & Native.SWP_NOZORDER) == 0)
                    pos.hwndInsertAfter = Native.HWND_BOTTOM; // never rise above other windows
                pos.flags |= Native.SWP_NOACTIVATE;
                Marshal.StructureToPtr(pos, lParam, false);
                break;
            }
            case Native.WM_DISPLAYCHANGE:
                // Monitor unplugged / resolution changed: never leave a widget unreachable.
                SettlePosition();
                Changed?.Invoke();
                break;
            case Native.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE); // clicking a widget must not focus it
            case Native.WM_SYSCOMMAND:
                if ((wParam.ToInt64() & 0xFFF0) == Native.SC_MINIMIZE)
                {
                    handled = true; // survive Win+D / Win+M
                    return IntPtr.Zero;
                }
                break;
        }
        return IntPtr.Zero;
    }

    // ---- interaction ---------------------------------------------------

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || e.OriginalSource is Thumb)
            return;
        // Manual drag (not DragMove) so positions can be snapped while moving.
        _dragging = true;
        Native.GetCursorPos(out _dragStartCursor);
        Native.GetWindowRect(_hwnd, out _dragStartRect);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
            return;
        Native.GetCursorPos(out var cursor);
        // Physical pixels end to end: cursor, window rects and monitor work areas share
        // one coordinate space, so dragging and snapping behave identically at any DPI.
        double left = _dragStartRect.Left + (cursor.X - _dragStartCursor.X);
        double top = _dragStartRect.Top + (cursor.Y - _dragStartCursor.Y);
        SnapPosition(ref left, ref top);
        MovePhysical(left, top);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging)
            return;
        _dragging = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        SettlePosition();
        Changed?.Invoke();
        ApplyOpacity();
    }

    // ---- magnetic snapping ----------------------------------------------

    private const double SnapThresholdDip = 10;
    private const double SnapGapDip = 8;
    internal static readonly double[] SizeFactors = { 0.75, 1.0, 1.25, 1.5 };

    /// <summary>Pulls a dragged position (physical px) onto nearby widget edges, docks and screen edges.</summary>
    private void SnapPosition(ref double left, ref double top)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double threshold = SnapThresholdDip * scale;
        double gap = SnapGapDip * scale;
        var rect = PhysicalRect();
        double w = rect.Width, h = rect.Height;

        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var other in All)
        {
            if (other == this)
                continue;
            var o = other.PhysicalRect();
            xs.Add(o.Left);                        // left edges align
            xs.Add(o.Right - w);                   // right edges align
            xs.Add(o.Left + o.Width / 2 - w / 2);  // centres align
            xs.Add(o.Right + gap);                 // dock to its right
            xs.Add(o.Left - w - gap);              // dock to its left
            ys.Add(o.Top);
            ys.Add(o.Bottom - h);
            ys.Add(o.Top + o.Height / 2 - h / 2);
            ys.Add(o.Bottom + gap);
            ys.Add(o.Top - h - gap);
        }
        foreach (var wa in WorkAreasPhysical())
        {
            xs.Add(wa.Left);
            xs.Add(wa.Right - w);
            ys.Add(wa.Top);
            ys.Add(wa.Bottom - h);
        }
        left = SnapAxis(left, xs, threshold);
        top = SnapAxis(top, ys, threshold);
        ResolveOverlaps(ref left, ref top);
    }

    private static double SnapAxis(double value, List<double> candidates, double threshold)
    {
        double best = value;
        double bestDistance = threshold + 1;
        foreach (double candidate in candidates)
        {
            double distance = Math.Abs(candidate - value);
            if (distance <= threshold && distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>
    /// Cards are solid, border included: push the position out of any other widget along
    /// the axis of least penetration, so snapping or dragging can never overlap them.
    /// </summary>
    private void ResolveOverlaps(ref double left, ref double top)
    {
        var rect = PhysicalRect();
        double w = rect.Width, h = rect.Height;
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var other in All)
            {
                if (other == this)
                    continue;
                var o = other.PhysicalRect();
                bool overlaps = left < o.Right && o.Left < left + w && top < o.Bottom && o.Top < top + h;
                if (!overlaps)
                    continue;
                double pushLeft = o.Left - w - left;
                double pushRight = o.Right - left;
                double pushUp = o.Top - h - top;
                double pushDown = o.Bottom - top;
                double minX = Math.Abs(pushLeft) < Math.Abs(pushRight) ? pushLeft : pushRight;
                double minY = Math.Abs(pushUp) < Math.Abs(pushDown) ? pushUp : pushDown;
                if (Math.Abs(minX) < Math.Abs(minY))
                    left += minX;
                else
                    top += minY;
            }
        }
    }

    // ---- physical-pixel geometry (DPI-proof) -----------------------------

    private Rect PhysicalRect()
    {
        Native.GetWindowRect(_hwnd, out var r);
        return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    private void MovePhysical(double left, double top)
    {
        Native.SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(left), (int)Math.Round(top), 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>Work areas of every connected monitor, in physical pixels.</summary>
    private static List<Rect> WorkAreasPhysical()
    {
        var areas = new List<Rect>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var wa = screen.WorkingArea;
            areas.Add(new Rect(wa.Left, wa.Top, wa.Width, wa.Height));
        }
        if (areas.Count == 0)
        {
            var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            areas.Add(new Rect(wa.Left, wa.Top, wa.Width, wa.Height));
        }
        return areas;
    }

    // ---- off-screen safety ----------------------------------------------

    /// <summary>
    /// A widget must always stay grabbable: if it no longer overlaps any connected
    /// monitor by a usable margin (unplugged display, resolution change), pull it back
    /// onto the nearest one. A widget sitting on a second monitor is left alone.
    /// </summary>
    public void EnsureGrabbable()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var rect = PhysicalRect();
        var areas = WorkAreasPhysical();
        foreach (var wa in areas)
        {
            var overlap = Rect.Intersect(rect, wa);
            if (overlap.Width >= 48 * scale && overlap.Height >= 32 * scale)
                return; // still reachable on some monitor
        }

        Rect nearest = areas[0];
        double best = double.MaxValue;
        var centre = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        foreach (var wa in areas)
        {
            var wc = new Point(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
            double distance = (wc - centre).LengthSquared;
            if (distance < best)
            {
                best = distance;
                nearest = wa;
            }
        }
        ClampInto(nearest);
    }

    /// <summary>Tray rescue: bring this widget back onto the primary screen.</summary>
    public void GatherToPrimary()
    {
        var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        ClampInto(new Rect(wa.Left, wa.Top, wa.Width, wa.Height));
    }

    private void ClampInto(Rect wa)
    {
        var rect = PhysicalRect();
        double left = Math.Clamp(rect.Left, wa.Left, Math.Max(wa.Left, wa.Right - rect.Width));
        double top = Math.Clamp(rect.Top, wa.Top, Math.Max(wa.Top, wa.Bottom - rect.Height));
        MovePhysical(left, top);
    }

    /// <summary>Off-screen clamp + overlap push-out after a drag, resize or display change.</summary>
    private void SettlePosition()
    {
        EnsureGrabbable();
        var rect = PhysicalRect();
        double left = rect.Left, top = rect.Top;
        ResolveOverlaps(ref left, ref top);
        MovePhysical(left, top);
    }

    private void OnGripDrag(object sender, DragDeltaEventArgs e)
    {
        double w = Math.Max(MinWidth, Width + e.HorizontalChange);
        double h = Math.Max(MinHeight, Height + e.VerticalChange);
        if (_cfg.SnapSizes)
        {
            w = SnapAxis(w, SizeCandidates(true), 8);
            h = SnapAxis(h, SizeCandidates(false), 8);
        }
        Width = w;
        Height = h;
    }

    private List<double> SizeCandidates(bool width)
    {
        var list = new List<double>();
        foreach (var other in All)
        {
            if (other != this)
                list.Add(width ? other.Width : other.Height);
        }
        foreach (double factor in SizeFactors)
        {
            var preset = PresetSize(_cfg.Kind, _cfg.ClockMode, factor);
            list.Add(width ? preset.Width : preset.Height);
        }
        return list;
    }

    private void SyncGeometry()
    {
        if (!_loaded)
            return;
        _cfg.X = Left;
        _cfg.Y = Top;
        _cfg.Width = Width;
        _cfg.Height = Height;
        Changed?.Invoke();
    }

    private void UpdateClockTimer()
    {
        bool wantFast = _cfg.Kind == WidgetKind.Clock && _cfg.ClockMode == ClockMode.Analog;
        if (wantFast)
        {
            if (_fastTimer == null)
            {
                _fastTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(50), // 20 fps: smooth sweep, a third less render cost
                };
                _fastTimer.Tick += (_, _) => _canvas.Now = DateTime.Now;
            }
            if (_loaded)
                _fastTimer.Start();
        }
        else
        {
            _fastTimer?.Stop();
        }
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();

        if (_cfg.Kind == WidgetKind.Clock)
        {
            menu.Items.Add(MakeChoice("Analog", _cfg.ClockMode == ClockMode.Analog, () => SetClockMode(ClockMode.Analog)));
            menu.Items.Add(MakeChoice("Digital", _cfg.ClockMode == ClockMode.Digital, () => SetClockMode(ClockMode.Digital)));
            menu.Items.Add(new Separator());
        }

        if (_cfg.Kind == WidgetKind.Disk)
        {
            var drives = new MenuItem { Header = "Drive" };
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
                    continue;
                string root = drive.Name;
                string label;
                try { label = drive.VolumeLabel; } catch { label = ""; }
                string header = string.IsNullOrEmpty(label) ? root : $"{root} {label}";
                drives.Items.Add(MakeChoice(header,
                    string.Equals(_cfg.Drive ?? DefaultDrive, root, StringComparison.OrdinalIgnoreCase), () =>
                    {
                        _cfg.Drive = root;
                        _canvas.Drive = root;
                        Changed?.Invoke();
                    }));
            }
            menu.Items.Add(drives);
            menu.Items.Add(new Separator());
        }

        var opacity = new MenuItem { Header = "Opacity" };
        opacity.Items.Add(BuildOpacityMenu("Foreground (mouse over)", () => _cfg.HoverOpacity, v => SetOpacity(v, true)));
        opacity.Items.Add(BuildOpacityMenu("Background (idle)", () => _cfg.Opacity, v => SetOpacity(v, false)));
        opacity.Items.Add(BuildOpacityMenu("Content (digits/hands)", () => _cfg.ContentOpacity, SetContentOpacity));
        menu.Items.Add(opacity);

        var sizes = new MenuItem { Header = "Size" };
        foreach (var (label, factor) in new[] { ("Small (75%)", 0.75), ("Default (100%)", 1.0), ("Large (125%)", 1.25), ("Extra large (150%)", 1.5) })
        {
            var target = PresetSize(_cfg.Kind, _cfg.ClockMode, factor);
            sizes.Items.Add(MakeChoice(label,
                Math.Abs(Width - target.Width) < 1 && Math.Abs(Height - target.Height) < 1,
                () => ApplySize(target)));
        }
        menu.Items.Add(sizes);
        menu.Items.Add(MakeChoice("Snap sizes while resizing", _cfg.SnapSizes, () =>
        {
            _cfg.SnapSizes = !_cfg.SnapSizes;
            Changed?.Invoke();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Close widget", Command = new RelayCommand(() => { RemoveRequested?.Invoke(); Close(); }) });

        menu.PlacementTarget = this;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; ApplyOpacity(); };
        ApplyOpacity();
        menu.IsOpen = true;
    }

    private void SetContentOpacity(double value)
    {
        value = Math.Clamp(value, 0.15, 1.0);
        _cfg.ContentOpacity = value;
        _canvas.Opacity = value;
        Changed?.Invoke();
    }

    private void ApplySize(Size size)
    {
        Width = Math.Max(MinWidth, size.Width);
        Height = Math.Max(MinHeight, size.Height);
        SettlePosition();
        Changed?.Invoke();
    }

    internal static Size PresetSize(WidgetKind kind, ClockMode mode, double factor)
    {
        var def = DefaultSize(kind, mode);
        return new Size(def.Width * factor, def.Height * factor);
    }

    private void SetOpacity(double value, bool foreground)
    {
        value = Math.Clamp(value, 0.15, 1.0);
        if (foreground)
            _cfg.HoverOpacity = value;
        else
            _cfg.Opacity = value;
        ApplyOpacity();
        Changed?.Invoke();
    }

    /// <summary>Fades between background (idle) and foreground (hover/drag/menu) opacity.</summary>
    private void ApplyOpacity()
    {
        bool foreground = IsMouseOver || _dragging || _menuOpen;
        double target = Math.Clamp(foreground ? _cfg.HoverOpacity : _cfg.Opacity, 0.15, 1.0);
        if (Math.Abs(Opacity - target) < 0.001)
            return;
        BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(120)));
    }

    private static MenuItem BuildOpacityMenu(string header, Func<double> current, Action<double> apply)
    {
        var menu = new MenuItem { Header = header };
        foreach (int pct in new[] { 100, 85, 70, 55, 40, 25 })
        {
            double value = pct / 100.0;
            menu.Items.Add(MakeChoice($"{pct}%", Math.Abs(current() - value) < 0.01, () => apply(value)));
        }
        menu.Items.Add(new Separator());
        string title = header.StartsWith("Foreground") ? "Custom foreground opacity"
            : header.StartsWith("Background") ? "Custom background opacity"
            : "Custom content opacity";
        menu.Items.Add(new MenuItem
        {
            Header = "Custom...",
            Command = new RelayCommand(() => new OpacityDialog(current(), apply, title).Show()),
        });
        return menu;
    }

    private void SetClockMode(ClockMode mode)
    {
        _cfg.ClockMode = mode;
        _canvas.ClockMode = mode;
        UpdateClockTimer();
        Changed?.Invoke();
    }

    private static MenuItem MakeChoice(string header, bool isChecked, Action action)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }

    private static ControlTemplate BuildGripTemplate()
    {
        var template = new ControlTemplate(typeof(Thumb));
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(System.Windows.Controls.Panel.BackgroundProperty, Brushes.Transparent);

        var path = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        path.SetValue(System.Windows.Shapes.Path.DataProperty,
            Geometry.Parse("M 13,3 L 3,13 M 13,8 L 8,13"));
        path.SetValue(System.Windows.Shapes.Path.StrokeProperty,
            new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)));
        path.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 1.6);
        path.SetValue(System.Windows.Shapes.Path.StrokeStartLineCapProperty, PenLineCap.Round);
        path.SetValue(System.Windows.Shapes.Path.StrokeEndLineCapProperty, PenLineCap.Round);
        path.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        path.SetValue(VerticalAlignmentProperty, VerticalAlignment.Stretch);

        grid.AppendChild(path);
        template.VisualTree = grid;
        return template;
    }
}

/// <summary>The rounded translucent card every widget sits on (shared with the preview renderer).</summary>
internal static class WidgetChrome
{
    internal static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8));
    internal static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2));
    internal static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
    internal static readonly Brush ButtonBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x40));
    internal static readonly Brush ButtonHoverBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x52));

    public static Border CreateCard(FrameworkElement content) => new()
    {
        CornerRadius = new CornerRadius(14),
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x20, 0x2B)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        Child = content,
    };
}

/// <summary>Minimal ICommand for context-menu entries.</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    public RelayCommand(Action execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}
