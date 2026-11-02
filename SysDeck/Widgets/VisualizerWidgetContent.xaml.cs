using System.Windows.Threading;
using SysDeck.Services;

namespace SysDeck.Widgets;

/// <summary>
/// Module Visualiseur : capte le son de sortie pendant qu'il est affiché (et seulement à ce moment-là),
/// et rafraîchit les barres ~30 fois par seconde.
/// </summary>
public partial class VisualizerWidgetContent : UserControl
{
    private readonly AudioSpectrumService _spectrum = new();
    private readonly DispatcherTimer _frameTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private int _barCount = 32;
    private double _sensitivity = 1;

    public VisualizerWidgetContent()
    {
        InitializeComponent();
        _frameTimer.Tick += (_, _) => OnFrame();
        // Casque branché, sortie changée depuis Windows ou depuis le module Sortie audio : on suit.
        _deviceTimer.Tick += (_, _) =>
        {
            _spectrum.FollowDefaultDevice();
            UpdateDeviceName();
        };

        Loaded += (_, _) =>
        {
            _spectrum.Start();
            UpdateDeviceName();
            _frameTimer.Start();
            _deviceTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            _frameTimer.Stop();
            _deviceTimer.Stop();
            _spectrum.Stop();
        };
    }

    /// <summary>Forme ("Bars", "Mirror", "Circle"), nombre de barres et sensibilité, depuis la personnalisation du module.</summary>
    public void ApplySettings(string style, int barCount, double sensitivity)
    {
        _barCount = Math.Clamp(barCount, 8, 96);
        _sensitivity = Math.Clamp(sensitivity, 0.3, 3);
        Spectrum.Mode = style;

        // L'anneau a besoin d'une zone carrée ; les barres d'une bande large et basse.
        if (style == "Circle")
        {
            Spectrum.Width = Spectrum.Height = 170;
            Spectrum.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        }
        else
        {
            Spectrum.ClearValue(WidthProperty);
            Spectrum.Height = 80;
            Spectrum.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        }
    }

    private static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Silence complet et barres retombées : on ne redessine plus rien et on ralentit le minuteur
    /// (5 tests/s suffisent pour détecter la reprise du son) au lieu de rendre 30 images/s inutiles.
    /// </summary>
    private void OnFrame()
    {
        var bands = _spectrum.ComputeBands(_barCount, _sensitivity);
        var silent = true;
        foreach (var b in bands)
            if (b > 0.002) { silent = false; break; }

        if (silent && !Spectrum.IsActive)
        {
            if (_frameTimer.Interval != IdleInterval) _frameTimer.Interval = IdleInterval;
            return;
        }

        if (_frameTimer.Interval != ActiveInterval) _frameTimer.Interval = ActiveInterval;
        Spectrum.Push(bands);
    }

    private void UpdateDeviceName() => DeviceText.Text = _spectrum.DeviceName ?? "Aucune sortie audio";
}
