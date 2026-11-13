// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DesktopWidgets;

public partial class App : Application
{
    private Mutex? _mutex;
    private TrayIcon? _tray;
    private AboutWindow? _about;
    private readonly List<WidgetWindow> _windows = new();
    private AppSettings _settings = new();
    private readonly SystemStats _stats = new();
    private DispatcherTimer? _timer;
    private DispatcherTimer? _saveTimer;
    private StatsSnapshot? _lastStats;
    private bool _sampling;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, @"Local\DesktopWidgets.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();

        // Offscreen render mode used to verify the visuals without touching the desktop.
        if (e.Args.Length >= 1 && e.Args[0] == "--preview")
        {
            string dir = e.Args.Length >= 2 ? e.Args[1] : "preview";
            Dispatcher.BeginInvoke(new Action(() => RunPreview(dir)));
            return;
        }

        _tray = new TrayIcon(_settings.StartWithWindows);
        _tray.AddWidgetRequested += AddWidget;
        _tray.StartWithWindowsChanged += SetStartWithWindows;
        _tray.ExitRequested += ExitApp;
        _tray.AboutRequested += ShowAbout;
        _tray.GatherRequested += GatherWidgets;
        SessionEnding += (_, _) => SaveSettings(); // reboot: persist before the windows close

        if (_settings.Widgets.Count == 0)
            SeedFirstRun();

        foreach (var cfg in _settings.Widgets.ToList())
            CreateWindow(cfg);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await SampleAndTickAsync();
        _timer.Start();
        _ = SampleAndTickAsync();
    }

    // ---- widget management ---------------------------------------------

    private void CreateWindow(WidgetConfig cfg)
    {
        var window = new WidgetWindow(cfg);
        window.Changed += ScheduleSave;
        // Only an explicit user close removes a widget from the saved layout. Window.Closed
        // also fires for every widget during shutdown/reboot - deleting configs there wiped
        // the whole layout on every exit.
        window.RemoveRequested += () =>
        {
            _settings.Widgets.Remove(cfg);
            SaveSettings();
        };
        window.Closed += (_, _) => _windows.Remove(window);
        _windows.Add(window);
        window.Show();
        window.EnsureGrabbable();
        window.Tick(DateTime.Now, _lastStats);
    }

    private void AddWidget(WidgetKind kind, ClockMode mode)
    {
        var wa = SystemParameters.WorkArea;
        var def = WidgetWindow.DefaultSize(kind, mode);
        double offset = (_windows.Count % 8) * 26;
        var cfg = new WidgetConfig
        {
            Kind = kind,
            ClockMode = mode,
            Drive = kind == WidgetKind.Disk ? WidgetWindow.DefaultDrive : null,
            Width = def.Width,
            Height = def.Height,
            X = wa.Left + wa.Width / 2 - def.Width / 2 + offset,
            Y = wa.Top + wa.Height / 2 - def.Height / 2 + offset,
        };
        _settings.Widgets.Add(cfg);
        CreateWindow(cfg);
        SaveSettings();
    }

    /// <summary>First run: a tidy row of every widget near the top of the primary screen.</summary>
    private void SeedFirstRun()
    {
        var wa = SystemParameters.WorkArea;
        var wanted = new (WidgetKind kind, ClockMode mode)[]
        {
            (WidgetKind.Clock, ClockMode.Digital),
            (WidgetKind.Date, ClockMode.Digital),
            (WidgetKind.Cpu, ClockMode.Digital),
            (WidgetKind.Gpu, ClockMode.Digital),
            (WidgetKind.Memory, ClockMode.Digital),
        };

        const double gap = 16;
        var list = wanted.ToList();
        // Drop trailing widgets until the row fits the work area.
        while (list.Count > 1 &&
               list.Sum(k => WidgetWindow.DefaultSize(k.kind, k.mode).Width) + gap * (list.Count - 1) > wa.Width - 32)
        {
            list.RemoveAt(list.Count - 1);
        }

        double totalW = list.Sum(k => WidgetWindow.DefaultSize(k.kind, k.mode).Width) + gap * (list.Count - 1);
        double x = wa.Left + Math.Max(16, (wa.Width - totalW) / 2);
        double y = wa.Top + 40;

        foreach (var (kind, mode) in list)
        {
            var def = WidgetWindow.DefaultSize(kind, mode);
            _settings.Widgets.Add(new WidgetConfig
            {
                Kind = kind,
                ClockMode = mode,
                Width = def.Width,
                Height = def.Height,
                X = x,
                Y = y,
            });
            x += def.Width + gap;
        }
    }

    private void GatherWidgets()
    {
        foreach (var window in _windows)
            window.GatherToPrimary();
        SaveSettings();
    }

    private void ShowAbout()
    {
        if (_about != null)
        {
            _about.Activate();
            return;
        }
        _about = new AboutWindow();
        _about.Closed += (_, _) => _about = null;
        _about.Show();
    }

    // ---- sampling ------------------------------------------------------

    private async Task SampleAndTickAsync()
    {
        if (_sampling)
            return;
        _sampling = true;
        try
        {
            var snapshot = await Task.Run(() => _stats.Sample());
            _lastStats = snapshot;
            var now = DateTime.Now;
            foreach (var window in _windows)
                window.Tick(now, snapshot);
        }
        catch
        {
            // A failed sample must never take the app down.
        }
        finally
        {
            _sampling = false;
        }
    }

    // ---- persistence ---------------------------------------------------

    private void ScheduleSave()
    {
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    private void SaveSettings() => _settings.Save();

    private void SetStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enable)
                key.SetValue("DesktopWidgets", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("DesktopWidgets", false);

            _settings.StartWithWindows = enable;
            SaveSettings();
        }
        catch
        {
            _tray?.SetStartWithWindows(_settings.StartWithWindows);
        }
    }

    private void ExitApp()
    {
        if (_exiting)
            return;
        _exiting = true;

        _timer?.Stop();
        _saveTimer?.Stop();
        SaveSettings();

        foreach (var window in _windows.ToList())
            window.Close();

        _tray?.Dispose();
        _stats.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null)
            SaveSettings();
        _stats.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ---- offscreen preview (verification) ------------------------------

    private void RunPreview(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);

            _stats.Sample();
            System.Threading.Thread.Sleep(1100);
            var snapshot = _stats.Sample();

            File.WriteAllText(Path.Combine(dir, "stats.txt"),
                $"CPU: {snapshot.CpuPercent:F1}%\n" +
                $"GPU: {(snapshot.GpuPercent.HasValue ? snapshot.GpuPercent.Value.ToString("F1") + "%" : "unavailable")}\n" +
                $"Memory: {snapshot.MemoryPercent:F1}% ({snapshot.MemoryUsedGB:F1} / {snapshot.MemoryTotalGB:F1} GB)\n" +
                $"Disk {WidgetWindow.DefaultDrive}: {(snapshot.Disks.TryGetValue(WidgetWindow.DefaultDrive, out var disk) && disk.Ready ? $"{disk.UsedPercent:F1}% used ({disk.FreeGB:F1} / {disk.TotalGB:F0} GB free)" : "unavailable")}\n");

            var now = DateTime.Now;
            RenderPreview(dir, "clock-digital.png", WidgetKind.Clock, ClockMode.Digital, 320, 140, now, snapshot);
            RenderPreview(dir, "clock-analog.png", WidgetKind.Clock, ClockMode.Analog, 240, 240, now, snapshot);
            RenderPreview(dir, "date.png", WidgetKind.Date, ClockMode.Digital, 320, 120, now, snapshot);
            RenderPreview(dir, "cpu.png", WidgetKind.Cpu, ClockMode.Digital, 200, 200, now, snapshot);
            RenderPreview(dir, "gpu.png", WidgetKind.Gpu, ClockMode.Digital, 200, 200, now, snapshot);
            RenderPreview(dir, "memory.png", WidgetKind.Memory, ClockMode.Digital, 200, 200, now, snapshot);
            RenderPreview(dir, "disk.png", WidgetKind.Disk, ClockMode.Digital, 200, 200, now, snapshot, WidgetWindow.DefaultDrive);
            RenderAboutPreview(dir);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); } catch { }
        }
        finally
        {
            Shutdown();
        }
    }

    private static void RenderAboutPreview(string dir)
    {
        var about = AboutWindow.CreateContent(() => { }, () => { });
        about.Measure(new Size(420, double.PositiveInfinity));
        int aboutH = (int)Math.Ceiling(about.DesiredSize.Height);
        about.Arrange(new Rect(0, 0, 420, aboutH));
        about.UpdateLayout();
        SavePng(dir, "about.png", about, 420, aboutH);

        var license = LicenseWindow.CreateContent(AboutWindow.LoadLicenseText(), () => { });
        license.Measure(new Size(620, 680));
        license.Arrange(new Rect(0, 0, 620, 680));
        license.UpdateLayout();
        SavePng(dir, "license.png", license, 620, 680);
    }

    private static void SavePng(string dir, string file, Visual visual, int w, int h)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(dir, file));
        encoder.Save(fs);
    }

    private static void RenderPreview(string dir, string file, WidgetKind kind, ClockMode mode,
        int w, int h, DateTime now, StatsSnapshot snapshot, string? drive = null)
    {
        var canvas = new WidgetCanvas { Kind = kind, ClockMode = mode, Now = now, Stats = snapshot, Drive = drive };
        var card = WidgetChrome.CreateCard(canvas);
        card.Width = w;
        card.Height = h;
        card.Measure(new Size(w, h));
        card.Arrange(new Rect(0, 0, w, h));
        card.UpdateLayout();

        SavePng(dir, file, card, w, h);
    }
}
