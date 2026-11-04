using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SysDeck.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck.Widgets;

/// <summary>
/// Barres de capteurs : une barre par donnée choisie (températures, charges, VRAM, ventilateurs…). À l'horizontale
/// (par défaut) : nom à gauche, barre, valeur à droite ; à la verticale : nom au-dessus, valeur en dessous.
/// Barres en segments, pleines ou en dégradé, couleur par barre, couleur d'alerte.
/// </summary>
public sealed class SensorBarsWidgetContent : UserControl
{
    private sealed record Bar(SensorBarItem Item, TextBlock Label, TextBlock Value, Grid Fill);

    private readonly List<Bar> _bars = new();
    private SensorBarsSettings _settings = new() { Items = SensorBarsSettings.DefaultItems() };
    private IReadOnlyDictionary<string, SensorReading>? _values;

    private bool Horizontal => _settings.Orientation != "Vertical";

    public SensorBarsWidgetContent() => Apply(_settings);

    /// <summary>Réglages changés : les barres sont reconstruites.</summary>
    public void Apply(SensorBarsSettings settings)
    {
        _settings = settings;
        _bars.Clear();
        if (settings.Items.Count == 0)
        {
            var empty = WidgetUi.Text("label", 12, sub: true);
            empty.Text = "Ajoute des données dans la personnalisation du module.";
            Content = empty;
            return;
        }

        var list = new Grid();
        if (Horizontal)
        {
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        for (var i = 0; i < settings.Items.Count; i++)
        {
            var item = settings.Items[i];
            var label = WidgetUi.Text("label", 11.5, sub: true, weight: FontWeights.SemiBold);
            var value = WidgetUi.Text("value", 13, number: true, weight: FontWeights.SemiBold);
            var fill = new Grid();
            var track = new Border
            {
                Width = Horizontal ? settings.BarHeight : settings.BarWidth,
                Height = Horizontal ? settings.BarWidth : settings.BarHeight,
                CornerRadius = new CornerRadius(settings.CornerRadius), ClipToBounds = settings.Style != "Segments", Child = fill,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
            };
            if (settings.Style != "Segments") track.SetResourceReference(Border.BackgroundProperty, "TileStrongBrush");
            var gap = i == 0 ? 0 : settings.Spacing;

            if (Horizontal)
            {
                list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                label.VerticalAlignment = value.VerticalAlignment = VerticalAlignment.Center;
                label.Margin = new Thickness(0, gap, 12, 0);
                track.Margin = new Thickness(0, gap, 0, 0);
                value.Margin = new Thickness(12, gap, 0, 0);
                value.MinWidth = 64;
                value.TextAlignment = TextAlignment.Right;
                Grid.SetRow(label, i);
                Grid.SetRow(track, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(track, 1);
                Grid.SetColumn(value, 2);
                list.Children.Add(label);
                list.Children.Add(track);
                list.Children.Add(value);
            }
            else
            {
                list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                label.HorizontalAlignment = value.HorizontalAlignment = HorizontalAlignment.Center;
                label.Margin = new Thickness(0, 0, 0, 6);
                value.Margin = new Thickness(0, 6, 0, 0);
                var column = new StackPanel { Margin = new Thickness(gap, 0, 0, 0), MinWidth = settings.BarWidth, Children = { label, track, value } };
                Grid.SetColumn(column, i);
                list.Children.Add(column);
            }
            _bars.Add(new Bar(item, label, value, fill));
        }
        Content = list;
        if (_values is not null) Update(_values);
    }

    public void Update(IReadOnlyDictionary<string, SensorReading> values)
    {
        _values = values;
        foreach (var bar in _bars)
        {
            values.TryGetValue(bar.Item.SensorId, out var reading);
            bar.Label.Text = !string.IsNullOrWhiteSpace(bar.Item.Label) ? bar.Item.Label : reading?.Name ?? "?";
            bar.Value.Text = reading is null ? "--" : SensorCatalog.Format(reading.Value, reading.Unit);
            var max = bar.Item.Max > 0 ? bar.Item.Max : SensorCatalog.DefaultMax(reading?.Unit ?? "%");
            var ratio = reading is null ? 0 : Math.Clamp(reading.Value / max, 0, 1);
            Draw(bar, ratio);
        }
    }

    private void Draw(Bar bar, double ratio)
    {
        var s = _settings;
        var horizontal = Horizontal;
        var length = s.BarHeight; // longueur (sens du remplissage)
        var thickness = s.BarWidth;
        var baseColor = ThemeService.Parse(bar.Item.Color, Color.FromRgb(0x00, 0xE0, 0xFF));
        var color = s.Alert && ratio * 100 >= s.AlertPercent ? ThemeService.Parse(s.AlertColor, Colors.Red) : baseColor;
        var brush = Frozen(new SolidColorBrush(color));
        bar.Fill.Children.Clear();
        bar.Fill.Effect = null;

        if (s.Style == "Segments")
        {
            // Blocs alignés depuis le départ (gauche ou bas) ; les blocs éteints restent visibles en très atténué.
            var n = s.Segments;
            var gap = Math.Max(2, length / n * 0.22);
            var size = (length - gap * (n - 1)) / n;
            var lit = (int)Math.Round(ratio * n);
            var dim = Frozen(new SolidColorBrush(Color.FromArgb(0x2E, color.R, color.G, color.B)));
            var panel = new StackPanel
            {
                Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical,
                VerticalAlignment = horizontal ? VerticalAlignment.Center : VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            for (var k = 0; k < n; k++)
            {
                // Vertical : on empile du haut vers le bas, donc le bloc k vaut l'indice n-1-k depuis le bas.
                var index = horizontal ? k : n - 1 - k;
                panel.Children.Add(new Border
                {
                    Width = horizontal ? size : thickness,
                    Height = horizontal ? thickness : size,
                    Margin = horizontal ? new Thickness(k == 0 ? 0 : gap, 0, 0, 0) : new Thickness(0, k == 0 ? 0 : gap, 0, 0),
                    CornerRadius = new CornerRadius(Math.Min(s.CornerRadius, Math.Min(size, thickness) / 2)),
                    Background = index < lit ? brush : dim
                });
            }
            if (s.Glow && lit > 0) panel.Effect = new DropShadowEffect { Color = color, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.6 };
            bar.Fill.Children.Add(panel);
            return;
        }

        var fill = new Border
        {
            Width = horizontal ? length * ratio : double.NaN,
            Height = horizontal ? double.NaN : length * ratio,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = horizontal ? VerticalAlignment.Stretch : VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(s.CornerRadius),
            Background = s.Style == "Gradient"
                ? Frozen(new LinearGradientBrush(Color.FromArgb(0x55, color.R, color.G, color.B), color,
                    horizontal ? new Point(0, 0) : new Point(0, 1), horizontal ? new Point(1, 0) : new Point(0, 0)))
                : brush
        };
        if (!horizontal) fill.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (s.Glow && ratio > 0) bar.Fill.Effect = new DropShadowEffect { Color = color, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.75 };
        bar.Fill.Children.Add(fill);
    }

    private static T Frozen<T>(T brush) where T : Brush
    {
        brush.Freeze();
        return brush;
    }
}
