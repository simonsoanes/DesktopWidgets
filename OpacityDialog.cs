// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopWidgets;

/// <summary>Small slider dialog for a custom widget opacity (15-100%).</summary>
public sealed class OpacityDialog : Window
{
    public OpacityDialog(double current, Action<double> apply)
    {
        AboutWindow.ConfigureDialog(this, 320);
        InputBindings.Add(new KeyBinding(new RelayCommand(Close), Key.Escape, ModifierKeys.None));

        var panel = new StackPanel { Margin = new Thickness(20) };

        var header = new TextBlock
        {
            Text = "Custom opacity",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = WidgetChrome.TextBrush,
            Cursor = Cursors.SizeAll,
        };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        };
        panel.Children.Add(header);

        var value = new TextBlock
        {
            Text = $"{current * 100:F0}%",
            FontSize = 13,
            Foreground = WidgetChrome.DimBrush,
            Margin = new Thickness(0, 10, 0, 4),
        };
        panel.Children.Add(value);

        var slider = new Slider
        {
            Minimum = 15,
            Maximum = 100,
            Value = Math.Clamp(current * 100, 15, 100),
            TickFrequency = 5,
            Foreground = WidgetChrome.AccentBrush,
        };
        slider.ValueChanged += (_, e) => value.Text = $"{e.NewValue:F0}%";
        panel.Children.Add(slider);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(AboutWindow.MakeButton("Cancel", Close));
        var ok = AboutWindow.MakeButton("OK", () => { apply(slider.Value / 100.0); Close(); });
        ok.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        Content = WidgetChrome.CreateCard(panel);
    }
}
