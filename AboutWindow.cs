// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopWidgets;

/// <summary>
/// About dialog in the app's dark card style: version, copyright and licence,
/// with the full Apache-2.0 text available offline from an embedded resource.
/// Unlike the widgets this is a normal, activatable, topmost window.
/// </summary>
public sealed class AboutWindow : Window
{
    internal const string ProjectUrl = "https://github.com/simonsoanes/DesktopWidgets";

    public AboutWindow()
    {
        ConfigureDialog(this, 420);
        Content = CreateContent(() => new LicenseWindow().Show(), Close);
        InputBindings.Add(new KeyBinding(new RelayCommand(Close), Key.Escape, ModifierKeys.None));
    }

    internal static void ConfigureDialog(Window window, double width)
    {
        window.WindowStyle = WindowStyle.None;
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;
        window.ShowInTaskbar = false;
        window.Topmost = true;
        window.ResizeMode = ResizeMode.NoResize;
        window.SizeToContent = SizeToContent.Height;
        window.Width = width;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    internal static string VersionString =>
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0)).ToString(3);

    internal static string CopyrightString =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
        ?? "Copyright 2026 Simon Soanes";

    /// <summary>The full licence text, embedded at build time so it works offline.</summary>
    public static string LoadLicenseText()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DesktopWidgets.LICENSE.txt");
        if (stream == null)
            return "License text is not embedded in this build.";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Builds the dialog card; also used by the offscreen preview renderer.</summary>
    public static Border CreateContent(Action viewLicense, Action close)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Cursor = Cursors.SizeAll,
        };
        header.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed)
                return;
            var window = Window.GetWindow(header);
            if (window != null)
            {
                try { window.DragMove(); } catch (InvalidOperationException) { }
            }
        };
        header.Children.Add(new Image
        {
            Source = IconSource(),
            Width = 48,
            Height = 48,
            Margin = new Thickness(0, 0, 14, 0),
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = "Desktop Widgets",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = WidgetChrome.TextBrush,
        });
        titles.Children.Add(new TextBlock
        {
            Text = $"Version {VersionString}",
            FontSize = 13,
            Foreground = WidgetChrome.DimBrush,
            Margin = new Thickness(0, 2, 0, 0),
        });
        header.Children.Add(titles);
        panel.Children.Add(header);

        panel.Children.Add(new TextBlock
        {
            Text = "Always-behind desktop widgets for Windows: clock, date, CPU, GPU and memory.",
            FontSize = 13,
            Foreground = WidgetChrome.DimBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = CopyrightString,
            FontSize = 13,
            Foreground = WidgetChrome.TextBrush,
            Margin = new Thickness(0, 10, 0, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Licensed under the Apache License, Version 2.0.",
            FontSize = 13,
            Foreground = WidgetChrome.TextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        panel.Children.Add(MakeLinkLine(ProjectUrl, 10));
        panel.Children.Add(MakeLinkLine("https://www.apache.org/licenses/LICENSE-2.0", 4));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        buttons.Children.Add(MakeButton("View license", viewLicense));
        var closeButton = MakeButton("Close", close);
        closeButton.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(closeButton);
        panel.Children.Add(buttons);

        return WidgetChrome.CreateCard(panel);
    }

    internal static Border MakeButton(string label, Action onClick)
    {
        var button = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = WidgetChrome.ButtonBrush,
            Padding = new Thickness(14, 7, 14, 7),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = WidgetChrome.TextBrush,
            },
        };
        button.MouseEnter += (_, _) => button.Background = WidgetChrome.ButtonHoverBrush;
        button.MouseLeave += (_, _) => button.Background = WidgetChrome.ButtonBrush;
        button.MouseLeftButtonUp += (_, _) => onClick();
        return button;
    }

    private static TextBlock MakeLinkLine(string url, double topMargin)
    {
        var line = new TextBlock { FontSize = 12, Margin = new Thickness(0, topMargin, 0, 0) };
        var link = new Hyperlink(new Run(url)) { Foreground = WidgetChrome.AccentBrush };
        link.RequestNavigate += (_, e) =>
        {
            e.Handled = true;
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // No browser available - the embedded licence text is still one click away.
            }
        };
        line.Inlines.Add(link);
        return line;
    }

    private static ImageSource IconSource()
    {
        using var bitmap = TrayIcon.DrawIconBitmap();
        IntPtr hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            Native.DeleteObject(hBitmap);
        }
    }
}

/// <summary>Scrollable viewer for the full embedded Apache-2.0 licence text.</summary>
public sealed class LicenseWindow : Window
{
    public LicenseWindow()
    {
        AboutWindow.ConfigureDialog(this, 620);
        SizeToContent = SizeToContent.Manual;
        Height = 680;
        Content = CreateContent(AboutWindow.LoadLicenseText(), Close);
        InputBindings.Add(new KeyBinding(new RelayCommand(Close), Key.Escape, ModifierKeys.None));
    }

    public static Border CreateContent(string licenseText, Action close)
    {
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Apache License 2.0",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = WidgetChrome.TextBrush,
            Margin = new Thickness(0, 0, 0, 12),
        };
        Grid.SetRow(title, 0);
        grid.Children.Add(title);

        var box = new TextBox
        {
            Text = licenseText,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1A, 0x22)),
            Foreground = WidgetChrome.TextBrush,
            CaretBrush = WidgetChrome.TextBrush,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(10),
        };
        Grid.SetRow(box, 1);
        grid.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(AboutWindow.MakeButton("Close", close));
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        return WidgetChrome.CreateCard(grid);
    }
}
