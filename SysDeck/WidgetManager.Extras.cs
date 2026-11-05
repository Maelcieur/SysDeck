using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using SysDeck.Services;
using SysDeck.Widgets;

namespace SysDeck;

/// <summary>Modules Horloges, Calendrier, Météo et Températures : horloge à la seconde, météo, historique des températures.</summary>
public sealed partial class WidgetManager
{
    /// <summary>Modules ajoutés en 1.12 : masqués tant qu'on ne les a pas affichés (pas d'apparition surprise).</summary>
    private static readonly HashSet<string> HiddenByDefault = new() { "tileclock", "analogclock", "calendar", "weather", "temperatures", "airquality", "simhub", "sensorbars", "moon", "sun" };

    private readonly DispatcherTimer _clockTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly List<(double? Cpu, double? Gpu)> _temperatureHistory = new();
    private WeatherService? _weather;
    private SimHubService? _simHub;
    private int _lastClockSecond = -1;

    public WeatherService Weather => _weather!;

    private void StartExtraWidgets()
    {
        _weather = new WeatherService(_config.Weather);
        _weather.Updated += PushWeather;
        _simHub = new SimHubService(_config.WidgetExtras);
        _simHub.Updated += PushSimHub;
        _config.Weather.PropertyChanged += OnExtraSettingsChanged;
        _config.WidgetExtras.PropertyChanged += OnExtraSettingsChanged;
        _config.SensorBars.PropertyChanged += OnExtraSettingsChanged;
        _clockTimer.Tick += (_, _) => TickClocks();
        _clockTimer.Start();
    }

    private void StopExtraWidgets()
    {
        _clockTimer.Stop();
        _weather?.Dispose();
        _simHub?.Dispose();
    }

    private void OnExtraSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExtraWidgetSettings.SimHubPort)) _simHub?.Reconnect();
        _configStore.Save(_config);
        RefreshExtraWidgets();
    }

    /// <summary>Horloges et calendrier : mis à jour dès que la seconde change (le minuteur tourne au quart de seconde).</summary>
    private void TickClocks()
    {
        var now = DateTime.Now;
        if (now.Second == _lastClockSecond) return;
        _lastClockSecond = now.Second;
        foreach (var widget in Live<UIElement>().OfType<ITickingWidget>()) widget.Tick(now);
    }

    /// <summary>Réglages et dernières valeurs envoyés aux modules (nouvellement affichés ou réglages changés).</summary>
    private void RefreshExtraWidgets()
    {
        if (_weather is null) return;
        foreach (var clock in Live<TileClockWidgetContent>()) clock.Apply(_config.WidgetExtras);
        foreach (var bars in Live<SensorBarsWidgetContent>())
        {
            bars.Apply(_config.SensorBars);
            if (_lastSensors is not null) bars.Update(_lastSensors);
        }
        foreach (var chart in Live<TemperatureWidgetContent>())
        {
            chart.Apply(_config.WidgetExtras);
            if (!chart.HasHistory && _temperatureHistory.Count > 0) chart.Load(_temperatureHistory);
        }
        _weather.Enabled = Live<WeatherWidgetContent>().Any() || Live<AirQualityWidgetContent>().Any() || Live<SunWidgetContent>().Any();
        PushWeather();
        if (_simHub is not null)
        {
            _simHub.Enabled = Live<SimHubWidgetContent>().Any();
            PushSimHub();
        }
        _lastClockSecond = -1;
        TickClocks();
    }

    private void PushWeather()
    {
        if (_weather is null) return;
        foreach (var w in Live<WeatherWidgetContent>()) w.Update(_weather.Current, _weather.Error);
        foreach (var a in Live<AirQualityWidgetContent>()) a.Update(_weather.Air, _weather.Error);
        foreach (var s in Live<SunWidgetContent>()) s.Update(_weather.Current, _weather.Error);
        // Hémisphère sud : la Lune y est vue « à l'envers » (éclairée à gauche quand elle croît).
        foreach (var m in Live<MoonWidgetContent>()) m.SouthernHemisphere = _config.Weather.Latitude is < 0;
    }

    private void PushSimHub()
    {
        if (_simHub is null) return;
        foreach (var s in Live<SimHubWidgetContent>()) s.Update(_simHub.Current, _simHub.Status);
    }

    /// <summary>Dernières mesures (catalogue complet), pour le module Barres de capteurs et le choix des données.</summary>
    private Dictionary<string, SensorReading>? _lastSensors;

    public IReadOnlyCollection<SensorReading> AvailableSensors => (IReadOnlyCollection<SensorReading>?)_lastSensors?.Values ?? Array.Empty<SensorReading>();

    private void PushSensorBars(HardwareSnapshot snapshot)
    {
        var all = new Dictionary<string, SensorReading>();
        foreach (var r in SensorCatalog.Derived(snapshot)) all[r.Id] = r;
        foreach (var r in snapshot.Sensors ?? Array.Empty<SensorReading>()) all.TryAdd(r.Id, r);
        _lastSensors = all;
        foreach (var bars in Live<SensorBarsWidgetContent>()) bars.Update(all);
    }

    /// <summary>Nouvelle lecture des capteurs : un point de plus sur les courbes de températures.</summary>
    private void PushTemperatures(float? cpu, float? gpu)
    {
        _temperatureHistory.Add((cpu, gpu));
        if (_temperatureHistory.Count > Controls.TemperatureChart.Capacity) _temperatureHistory.RemoveAt(0);
        foreach (var chart in Live<TemperatureWidgetContent>()) chart.Push(cpu, gpu);
    }
}
