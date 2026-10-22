using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SonicTrack.VintageAmpMeters;

public partial class FloatingOverlayWindow : Window
{
    internal MeterGauge[] Gauges { get; }

    internal FloatingOverlayWindow(double shadowStrength, double meterSize, double spacing, bool showScaleNumbers)
    {
        InitializeComponent();
        Gauges = new[]
        {
            CreateGauge("低音"),
            CreateGauge("中音"),
            CreateGauge("高音")
        };
        foreach (MeterGauge gauge in Gauges)
            LayoutCanvas.Children.Add(gauge);
        SetShadowStrength(shadowStrength);
        SetScaleNumbers(showScaleNumbers);
        SetLayout(meterSize, spacing);
    }

    internal void SetLayout(double meterSize, double spacing)
    {
        meterSize = Math.Clamp(meterSize, 120, 400);
        spacing = Math.Clamp(spacing, 0, 80);
        double scaleDiameter = meterSize * 0.608;
        double pitch = scaleDiameter + spacing;
        LayoutCanvas.Width = meterSize + pitch * 2;
        LayoutCanvas.Height = meterSize;
        for (int i = 0; i < Gauges.Length; i++)
        {
            MeterGauge gauge = Gauges[i];
            gauge.Width = meterSize;
            gauge.Height = meterSize;
            Canvas.SetLeft(gauge, i * pitch);
            Canvas.SetTop(gauge, 0);
        }
        Width = LayoutCanvas.Width + 32;
        Height = meterSize + 32;
    }

    internal void SetShadowStrength(double strength)
    {
        foreach (MeterGauge gauge in Gauges)
        {
            gauge.ShadowStrength = strength;
            gauge.InvalidateVisual();
        }
    }

    internal void SetScaleNumbers(bool show)
    {
        foreach (MeterGauge gauge in Gauges)
        {
            gauge.ShowScaleNumbers = show;
            gauge.InvalidateVisual();
        }
    }

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private static MeterGauge CreateGauge(string title) => new()
    {
        Title = title,
        OverlayOnly = true,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
}
