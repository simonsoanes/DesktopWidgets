// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopWidgets;

public sealed class WidgetConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public WidgetKind Kind { get; set; } = WidgetKind.Clock;
    public ClockMode ClockMode { get; set; } = ClockMode.Digital;
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
    public double Width { get; set; }
    public double Height { get; set; }
    /// <summary>Background (idle) opacity - what the widget fades to when not interacted with.</summary>
    public double Opacity { get; set; } = 0.95;
    /// <summary>Foreground opacity while the mouse is over the widget, it is dragged, or its menu is open.</summary>
    public double HoverOpacity { get; set; } = 1.0;
    /// <summary>Opacity of the drawn content (digits, hands, gauges) only - the card keeps its own opacity.</summary>
    public double ContentOpacity { get; set; } = 1.0;
    /// <summary>While grip-resizing, snap width/height to other widgets' sizes and the size presets.</summary>
    public bool SnapSizes { get; set; } = true;
    /// <summary>Drive root for Disk widgets (e.g. "C:\\"); null means the system drive.</summary>
    public string? Drive { get; set; }
}

public sealed class AppSettings
{
    public List<WidgetConfig> Widgets { get; set; } = new();
    public bool StartWithWindows { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopWidgets");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (settings != null)
                    return settings;
            }
        }
        catch
        {
            // Corrupt settings should never stop the app from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Best effort - a locked file must not crash the app.
        }
    }
}
