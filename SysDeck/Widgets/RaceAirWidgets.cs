using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using SysDeck.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck.Widgets;

/// <summary>Qualité de l'air : indice européen dans un anneau coloré selon l'échelle officielle, niveau, polluants principaux.</summary>
public sealed class AirQualityWidgetContent : UserControl
{
    private readonly TextBlock _place, _aqi, _level, _message;
    private readonly Ellipse _ring = new() { Width = 78, Height = 78, StrokeThickness = 6 };
    private readonly TextBlock[] _values = new TextBlock[4];
    private readonly FrameworkElement _body;

    public AirQualityWidgetContent()
    {
        Width = 270;
        _place = WidgetUi.Caption("QUALITÉ DE L'AIR", "title");
        _aqi = WidgetUi.Text("aqi", 28, number: true);
        _aqi.HorizontalAlignment = HorizontalAlignment.Center;
        _aqi.VerticalAlignment = VerticalAlignment.Center;
        _level = WidgetUi.Text("level", 16, weight: FontWeights.SemiBold);
        var scale = WidgetUi.Text("level", 11, sub: true);
        scale.Text = "Indice européen (0 = excellent)";
        _message = WidgetUi.Text("message", 12, sub: true);
        _message.TextWrapping = TextWrapping.Wrap;
        _message.Margin = new Thickness(0, 8, 0, 0);

        var now = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var gauge = new Grid { Children = { _ring, _aqi } };
        DockPanel.SetDock(gauge, Dock.Left);
        now.Children.Add(gauge);
        now.Children.Add(new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _level, scale } });

        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 12, 0, 0) };
        var names = new[] { "PM2.5", "PM10", "NO₂", "O₃" };
        for (var i = 0; i < 4; i++)
        {
            var name = WidgetUi.Text("pollutant", 10.5, sub: true, weight: FontWeights.SemiBold);
            name.Text = names[i];
            name.HorizontalAlignment = HorizontalAlignment.Center;
            _values[i] = WidgetUi.Text("pollutant", 14, number: true);
            _values[i].HorizontalAlignment = HorizontalAlignment.Center;
            var unit = WidgetUi.Text("pollutant", 9, sub: true);
            unit.Text = "µg/m³";
            unit.HorizontalAlignment = HorizontalAlignment.Center;
            grid.Children.Add(new StackPanel { Children = { name, _values[i], unit } });
        }
        _body = new StackPanel { Children = { now, grid } };
        Content = new StackPanel { Children = { _place, _body, _message } };
        Update(null, "Recherche de la qualité de l'air…");
    }

    public void Update(AirQualitySnapshot? air, string? error)
    {
        _message.Text = air is null ? error ?? "Qualité de l'air indisponible pour cette ville." : "";
        _message.Visibility = air is null ? Visibility.Visible : Visibility.Collapsed;
        _body.Visibility = air is null ? Visibility.Collapsed : Visibility.Visible;
        if (air is null) return;

        _place.Text = string.IsNullOrEmpty(air.Place) ? "QUALITÉ DE L'AIR" : "AIR · " + air.Place.ToUpper(WidgetUi.Fr);
        var (label, color) = WeatherService.AirLevel(air.Aqi);
        _aqi.Text = air.Aqi.ToString(WidgetUi.Fr);
        _level.Text = label;
        _ring.Stroke = WidgetUi.Brush("#FF" + color[1..], Colors.Gray);
        double?[] values = { air.Pm25, air.Pm10, air.No2, air.O3 };
        for (var i = 0; i < 4; i++) _values[i].Text = values[i] is { } v ? Math.Round(v).ToString(WidgetUi.Fr) : "--";
    }
}

/// <summary>SimHub : rapport engagé en grand, vitesse, barre de régime (vert → rouge), temps au tour, position et carburant.</summary>
public sealed class SimHubWidgetContent : UserControl
{
    private readonly TextBlock _game, _gear, _speed, _current, _last, _best, _info, _message;
    private readonly Border _rpmTrack = new() { Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border _rpmBar = new() { CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private readonly FrameworkElement _body;
    private double _rpmRatio;

    public SimHubWidgetContent()
    {
        Width = 320;
        _game = WidgetUi.Caption("SIMHUB", "title");
        _gear = WidgetUi.Text("gear", 64, number: true, weight: FontWeights.Bold);
        _gear.Width = 80;
        _gear.TextAlignment = TextAlignment.Center;
        _gear.VerticalAlignment = VerticalAlignment.Center;
        _speed = WidgetUi.Text("speed", 40, number: true);
        var unit = WidgetUi.Text("speed", 12, sub: true);
        unit.Text = "km/h";
        _current = WidgetUi.Text("lap", 15, number: true, weight: FontWeights.SemiBold);
        _last = WidgetUi.Text("lap", 12, number: true);
        _best = WidgetUi.Text("lap", 12, number: true);
        _info = WidgetUi.Text("info", 12, sub: true);
        _info.Margin = new Thickness(0, 8, 0, 0);
        _message = WidgetUi.Text("message", 12, sub: true);
        _message.TextWrapping = TextWrapping.Wrap;
        _message.Margin = new Thickness(0, 8, 0, 0);

        _rpmTrack.SetResourceReference(Border.BackgroundProperty, "TileStrongBrush");
        _rpmTrack.Child = _rpmBar;
        _rpmTrack.SizeChanged += (_, _) => LayoutRpm();

        var top = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var gearBox = new Border { CornerRadius = new CornerRadius(12), Child = _gear, Margin = new Thickness(0, 0, 14, 0), Padding = new Thickness(0, 0, 0, 4) };
        gearBox.SetResourceReference(Border.BackgroundProperty, "TileBrush");
        DockPanel.SetDock(gearBox, Dock.Left);
        top.Children.Add(gearBox);
        var laps = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Children = { Row("Tour", _current), Row("Dernier", _last), Row("Meilleur", _best) }
        };
        DockPanel.SetDock(laps, Dock.Right);
        top.Children.Add(laps);
        top.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _speed, unit } });

        _body = new StackPanel { Children = { top, _rpmTrack, _info } };
        Content = new StackPanel { Children = { _game, _body, _message } };
        Update(null, "Connexion à SimHub…");
    }

    private static FrameworkElement Row(string label, TextBlock value)
    {
        var name = WidgetUi.Text("lap", 10.5, sub: true);
        name.Text = label;
        name.Width = 58;
        name.VerticalAlignment = VerticalAlignment.Center;
        value.TextAlignment = TextAlignment.Right;
        value.MinWidth = 72;
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1), Children = { name, value } };
    }

    public void Update(SimHubData? data, string? status)
    {
        var running = data is { Running: true };
        _body.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _message.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _game.Text = data is { Game.Length: > 0 } ? data.Game.ToUpper(WidgetUi.Fr) : "SIMHUB";
        if (!running)
        {
            _message.Text = status ?? "En attente d'un jeu de course…";
            _rpmRatio = 0;
            return;
        }
        _gear.Text = data!.Gear;
        _speed.Text = Math.Round(data.Speed).ToString(WidgetUi.Fr);
        _current.Text = SimHubService.FormatLap(data.CurrentLap);
        _last.Text = SimHubService.FormatLap(data.LastLap);
        _best.Text = SimHubService.FormatLap(data.BestLap);
        var info = new List<string>();
        if (data.Position is { } p) info.Add($"Position {p}");
        if (data.Fuel is { } f) info.Add(data.FuelPercent is { } pc ? $"Carburant {f:0.0} L ({pc:0} %)" : $"Carburant {f:0.0} L");
        _info.Text = string.Join("  ·  ", info);
        _rpmRatio = data.MaxRpm > 0 ? Math.Clamp(data.Rpm / data.MaxRpm, 0, 1) : 0;
        LayoutRpm();
    }

    private void LayoutRpm()
    {
        _rpmBar.Width = _rpmTrack.ActualWidth * _rpmRatio;
        // Vert, puis jaune, puis rouge près du rupteur.
        _rpmBar.Background = _rpmRatio switch
        {
            >= 0.92 => WidgetUi.Brush("#FFFF3B30", Colors.Red),
            >= 0.75 => WidgetUi.Brush("#FFFFC83D", Colors.Gold),
            _ => WidgetUi.Brush("#FF3DDC84", Colors.LimeGreen)
        };
    }
}
