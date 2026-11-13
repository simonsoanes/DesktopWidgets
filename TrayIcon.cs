// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopWidgets;

/// <summary>
/// The notification-area icon and its menu. This is the app's only chrome -
/// the widgets themselves live on the desktop with no taskbar presence.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Bitmap _iconBitmap;
    private readonly Icon _icon;
    private readonly IntPtr _hIcon;
    private readonly ToolStripMenuItem _startItem;

    public event Action<WidgetKind, ClockMode>? AddWidgetRequested;
    public event Action<bool>? StartWithWindowsChanged;
    public event Action? AboutRequested;
    public event Action? ExitRequested;

    public TrayIcon(bool startWithWindows)
    {
        _iconBitmap = DrawIconBitmap();
        _hIcon = _iconBitmap.GetHicon();
        _icon = Icon.FromHandle(_hIcon);

        var menu = new ContextMenuStrip();

        var add = new ToolStripMenuItem("Add widget");
        add.DropDownItems.Add("Clock (digital)", null, (_, _) => AddWidgetRequested?.Invoke(WidgetKind.Clock, ClockMode.Digital));
        add.DropDownItems.Add("Clock (analog)", null, (_, _) => AddWidgetRequested?.Invoke(WidgetKind.Clock, ClockMode.Analog));
        add.DropDownItems.Add("Date", null, (_, _) => AddWidgetRequested?.Invoke(WidgetKind.Date, ClockMode.Digital));
        add.DropDownItems.Add("CPU usage", null, (_, _) => AddWidgetRequested?.Invoke(WidgetKind.Cpu, ClockMode.Digital));
        add.DropDownItems.Add("GPU usage", null, (_, _) => AddWidgetRequested?.Invoke(WidgetKind.Gpu, ClockMode.Digital));
        add.DropDownItems.Add("Memory usage", null, (_, _) => AddWidgetRequested?.Invoke(WidgetKind.Memory, ClockMode.Digital));
        menu.Items.Add(add);
        menu.Items.Add(new ToolStripSeparator());

        _startItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = startWithWindows,
            CheckOnClick = true,
        };
        _startItem.CheckedChanged += (_, _) => StartWithWindowsChanged?.Invoke(_startItem.Checked);
        menu.Items.Add(_startItem);

        menu.Items.Add("About Desktop Widgets", null, (_, _) => AboutRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Desktop Widgets",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    public void SetStartWithWindows(bool value)
    {
        if (_startItem.Checked != value)
            _startItem.Checked = value;
    }

    internal static Bitmap DrawIconBitmap()
    {
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var bg = new SolidBrush(Color.FromArgb(28, 32, 43));
        g.FillEllipse(bg, 1, 1, 30, 30);
        using var ring = new Pen(Color.FromArgb(76, 194, 255), 2f);
        g.DrawEllipse(ring, 2, 2, 28, 28);

        using var hand = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(hand, 16, 16, 16, 8);
        g.DrawLine(hand, 16, 16, 22, 18);

        using var second = new Pen(Color.FromArgb(255, 107, 107), 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(second, 16, 16, 12, 22);

        return bmp;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero)
            DestroyIcon(_hIcon);
        _iconBitmap.Dispose();
    }
}
