using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using SysDeck.Controls;
using SysDeck.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck.Widgets;

/// <summary>Petits outils communs aux modules dessinés en code (textes liés aux couleurs et polices du thème).</summary>
internal static class WidgetUi
{
    public static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static TextBlock Caption(string text, string key)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(FrameworkElement.StyleProperty, "WidgetCaption");
        TextPart.SetKey(t, key);
        return t;
    }

    /// <summary>Texte de couleur du thème (TextBrush ou SubTextBrush), police des chiffres si number.</summary>
    public static TextBlock Text(string key, double size, bool sub = false, bool number = false, FontWeight? weight = null)
    {
        var t = new TextBlock { FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, sub ? "SubTextBrush" : "TextBrush");
        if (number) t.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont");
        TextPart.SetKey(t, key);
        return t;
    }

    public static Brush Brush(string hex, Color fallback)
    {
        var b = new SolidColorBrush(ThemeService.Parse(hex, fallback));
        b.Freeze();
        return b;
    }

    public static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Fr) + s[1..];
}

/// <summary>Modules mis à jour chaque seconde par le gestionnaire (horloges, calendrier).</summary>
public interface ITickingWidget
{
    void Tick(DateTime now);
}

// ---------------------------------------------------------------------------------------------------------

/// <summary>Horloge à cases : heures, minutes (et secondes) en chiffres blancs sur des cases colorées, séparées par « : ».</summary>
public sealed class TileClockWidgetContent : UserControl, ITickingWidget
{
    private readonly Border[] _tiles = new Border[3];
    private readonly TextBlock[] _digits = new TextBlock[3];
    private readonly UIElement _secondsColon;

    public TileClockWidgetContent()
    {
        Height = 108;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < 3; i++)
        {
            if (i > 0)
            {
                var colon = new TextBlock
                {
                    Text = ":", FontSize = 50, FontWeight = FontWeights.Bold, Margin = new Thickness(5, -8, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                colon.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                colon.SetResourceReference(TextBlock.FontFamilyProperty, "NumberFont");
                row.Children.Add(colon);
                if (i == 2) _secondsColon = colon;
            }
            _digits[i] = new TextBlock
            {
                Text = "00", FontSize = 54, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Bahnschrift SemiBold")
            };
            TextPart.SetKey(_digits[i], "digits");
            _tiles[i] = new Border
            {
                Width = 92, Height = 92, CornerRadius = new CornerRadius(16), Child = _digits[i],
                // Léger reflet en haut de la case, comme une touche.
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0, 1.5, 0, 0)
            };
            row.Children.Add(_tiles[i]);
        }
        _secondsColon ??= new TextBlock();
        Content = row;
        Apply(new ExtraWidgetSettings());
        Tick(DateTime.Now);
    }

    public void Apply(ExtraWidgetSettings settings)
    {
        var c = ThemeService.Parse(settings.TileClockColor, Color.FromRgb(0xF7, 0x93, 0x1E));
        var fill = new LinearGradientBrush(Lighten(c, 0.12), c, 90);
        fill.Freeze();
        foreach (var tile in _tiles) tile.Background = fill;
        var seconds = settings.TileClockSeconds ? Visibility.Visible : Visibility.Collapsed;
        _tiles[2].Visibility = seconds;
        _secondsColon.Visibility = seconds;
    }

    public void Tick(DateTime now)
    {
        _digits[0].Text = now.ToString("HH", WidgetUi.Fr);
        _digits[1].Text = now.ToString("mm", WidgetUi.Fr);
        _digits[2].Text = now.ToString("ss", WidgetUi.Fr);
    }

    private static Color Lighten(Color c, double amount)
        => Color.FromArgb(c.A, (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));
}

// ---------------------------------------------------------------------------------------------------------

/// <summary>Horloge ronde à aiguilles (cadran gradué, trotteuse de la couleur d'accent) avec la date en dessous.</summary>
public sealed class AnalogClockWidgetContent : UserControl, ITickingWidget
{
    private const double D = 170, C = D / 2;
    private readonly RotateTransform _hour = new(0, C, C), _minute = new(0, C, C), _second = new(0, C, C);
    private readonly TextBlock _date;

    public AnalogClockWidgetContent()
    {
        Width = 180;
        var canvas = new Canvas { Width = D, Height = D, HorizontalAlignment = HorizontalAlignment.Center };
        var face = new Ellipse { Width = D, Height = D, StrokeThickness = 1.5 };
        face.SetResourceReference(Shape.FillProperty, "TileBrush");
        face.SetResourceReference(Shape.StrokeProperty, "TileStrongBrush");
        canvas.Children.Add(face);

        for (var i = 0; i < 60; i++)
        {
            var major = i % 5 == 0;
            var a = i * Math.PI / 30;
            var outer = C - 6;
            var inner = outer - (major ? 11 : 4);
            var tick = new Line
            {
                X1 = C + Math.Sin(a) * inner, Y1 = C - Math.Cos(a) * inner, X2 = C + Math.Sin(a) * outer, Y2 = C - Math.Cos(a) * outer,
                StrokeThickness = major ? 2.6 : 1.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
            tick.SetResourceReference(Shape.StrokeProperty, major ? "TextBrush" : "SubTextBrush");
            canvas.Children.Add(tick);
        }

        canvas.Children.Add(Hand(46, 6, "TextBrush", _hour));
        canvas.Children.Add(Hand(66, 4, "TextBrush", _minute));
        canvas.Children.Add(Hand(72, 2, "GaugeGradientBrush", _second, tail: 14));
        var hub = new Ellipse { Width = 10, Height = 10 };
        hub.SetResourceReference(Shape.FillProperty, "GaugeGradientBrush");
        Canvas.SetLeft(hub, C - 5);
        Canvas.SetTop(hub, C - 5);
        canvas.Children.Add(hub);

        _date = WidgetUi.Text("date", 12.5, sub: true, weight: FontWeights.SemiBold);
        _date.HorizontalAlignment = HorizontalAlignment.Center;
        _date.Margin = new Thickness(0, 8, 0, 0);
        Content = new StackPanel { Children = { canvas, _date } };
        Tick(DateTime.Now);
    }

    private static Line Hand(double length, double thickness, string brush, RotateTransform rotation, double tail = 0)
    {
        var hand = new Line
        {
            X1 = C, Y1 = C + tail, X2 = C, Y2 = C - length, StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = rotation
        };
        hand.SetResourceReference(Shape.StrokeProperty, brush);
        return hand;
    }

    public void Tick(DateTime now)
    {
        _second.Angle = now.Second * 6;
        _minute.Angle = now.Minute * 6 + now.Second * 0.1;
        _hour.Angle = now.Hour % 12 * 30 + now.Minute * 0.5;
        _date.Text = WidgetUi.Capitalize(now.ToString("dddd d MMMM", WidgetUi.Fr));
    }
}

// ---------------------------------------------------------------------------------------------------------

/// <summary>Calendrier du mois : semaine du lundi au dimanche, aujourd'hui dans une pastille de la couleur d'accent.</summary>
public sealed class CalendarWidgetContent : UserControl, ITickingWidget
{
    private readonly TextBlock _title;
    private readonly UniformGrid _days = new() { Columns = 7, Rows = 6 };
    private DateTime _shown;

    public CalendarWidgetContent()
    {
        Width = 250;
        _title = WidgetUi.Text("month", 15, weight: FontWeights.SemiBold);
        var header = new UniformGrid { Columns = 7, Margin = new Thickness(0, 10, 0, 4) };
        foreach (var d in new[] { "L", "M", "M", "J", "V", "S", "D" })
        {
            var t = WidgetUi.Text("weekday", 10.5, sub: true, weight: FontWeights.SemiBold);
            t.Text = d;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            header.Children.Add(t);
        }
        Content = new StackPanel { Children = { _title, header, _days } };
        Tick(DateTime.Now);
    }

    public void Tick(DateTime now)
    {
        if (now.Date == _shown) return;
        _shown = now.Date;
        _title.Text = WidgetUi.Capitalize(now.ToString("MMMM yyyy", WidgetUi.Fr));
        _days.Children.Clear();
        var first = new DateTime(now.Year, now.Month, 1);
        var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7)); // lundi de la première semaine
        for (var i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            var today = day == now.Date;
            var text = WidgetUi.Text("day", 12, sub: day.Month != now.Month || day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                number: true, weight: today ? FontWeights.Bold : FontWeights.Normal);
            text.Text = day.Day.ToString(WidgetUi.Fr);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            if (day.Month != now.Month) text.Opacity = 0.4;
            var cell = new Grid { Height = 26 };
            if (today)
            {
                var dot = new Ellipse { Width = 25, Height = 25 };
                dot.SetResourceReference(Shape.FillProperty, "GaugeGradientBrush");
                cell.Children.Add(dot);
                text.Foreground = Brushes.White;
            }
            cell.Children.Add(text);
            _days.Children.Add(cell);
        }
    }
}

// ---------------------------------------------------------------------------------------------------------

/// <summary>Météo : icône, température, état du ciel, ressenti, humidité, vent, puis les prévisions des jours suivants.</summary>
public sealed class WeatherWidgetContent : UserControl
{
    private readonly TextBlock _place, _temp, _desc, _details, _message;
    private readonly WeatherIcon _icon = new() { Width = 64, Height = 64 };
    private readonly UniformGrid _forecast = new() { Columns = 4, Margin = new Thickness(0, 10, 0, 0) };
    private readonly FrameworkElement _body;

    public WeatherWidgetContent()
    {
        Width = 290;
        _place = WidgetUi.Caption("MÉTÉO", "title");
        _temp = WidgetUi.Text("temp", 42, number: true);
        _temp.Margin = new Thickness(8, 0, 10, 0);
        _temp.VerticalAlignment = VerticalAlignment.Center;
        _desc = WidgetUi.Text("desc", 13, weight: FontWeights.SemiBold);
        _details = WidgetUi.Text("details", 11, sub: true);
        _details.TextWrapping = TextWrapping.Wrap;
        _message = WidgetUi.Text("message", 12, sub: true);
        _message.TextWrapping = TextWrapping.Wrap;
        _message.Margin = new Thickness(0, 8, 0, 0);

        var now = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(_icon, Dock.Left);
        DockPanel.SetDock(_temp, Dock.Left);
        now.Children.Add(_icon);
        now.Children.Add(_temp);
        now.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _desc, _details } });
        _body = new StackPanel { Children = { now, _forecast } };
        Content = new StackPanel { Children = { _place, _body, _message } };
        Update(null, "Recherche de la météo…");
    }

    public void Update(WeatherSnapshot? weather, string? error)
    {
        _message.Text = error ?? "";
        _message.Visibility = weather is null && error is not null ? Visibility.Visible : Visibility.Collapsed;
        _body.Visibility = weather is null ? Visibility.Collapsed : Visibility.Visible;
        if (weather is null) return;

        _place.Text = string.IsNullOrEmpty(weather.Place) ? "MÉTÉO" : weather.Place.ToUpper(WidgetUi.Fr);
        _icon.Kind = WeatherService.Kind(weather.Code);
        _icon.IsDay = weather.IsDay;
        _temp.Text = $"{Math.Round(weather.Temperature):0}°";
        _desc.Text = WeatherService.Describe(weather.Code);
        _details.Text = $"Ressenti {Math.Round(weather.FeelsLike):0}° · {weather.Humidity} %\nVent {Math.Round(weather.Wind):0} km/h";

        _forecast.Children.Clear();
        foreach (var day in weather.Days.Take(4))
        {
            var name = WidgetUi.Text("day", 10.5, sub: true, weight: FontWeights.SemiBold);
            name.Text = day.Date.Date == DateTime.Today ? "Auj." : WidgetUi.Capitalize(day.Date.ToString("ddd", WidgetUi.Fr));
            name.HorizontalAlignment = HorizontalAlignment.Center;
            var range = WidgetUi.Text("range", 11.5, number: true);
            range.Text = $"{Math.Round(day.Max):0}° / {Math.Round(day.Min):0}°";
            range.HorizontalAlignment = HorizontalAlignment.Center;
            _forecast.Children.Add(new StackPanel
            {
                Children = { name, new WeatherIcon { Width = 28, Height = 28, Kind = WeatherService.Kind(day.Code), Margin = new Thickness(0, 3, 0, 3) }, range }
            });
        }
    }
}

// ---------------------------------------------------------------------------------------------------------

/// <summary>Températures du processeur et de la carte graphique : valeurs actuelles et courbes réunies sur un même graphe.</summary>
public sealed class TemperatureWidgetContent : UserControl
{
    private readonly TemperatureChart _chart = new() { Height = 110, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _cpu, _gpu;
    private readonly Ellipse _cpuDot = new() { Width = 9, Height = 9, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _gpuDot = new() { Width = 9, Height = 9, Margin = new Thickness(14, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };

    public TemperatureWidgetContent()
    {
        Width = 300;
        _cpu = WidgetUi.Text("cpu", 13, number: true, weight: FontWeights.SemiBold);
        _gpu = WidgetUi.Text("gpu", 13, number: true, weight: FontWeights.SemiBold);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _cpuDot, _cpu, _gpuDot, _gpu } };
        var header = new DockPanel();
        DockPanel.SetDock(legend, Dock.Right);
        header.Children.Add(legend);
        header.Children.Add(WidgetUi.Caption("TEMPÉRATURES", "title"));
        Content = new StackPanel { Children = { header, _chart } };
        Apply(new ExtraWidgetSettings());
        Show(null, null);
    }

    public bool HasHistory => _chart.Count > 0;

    public void Apply(ExtraWidgetSettings settings)
    {
        _chart.CpuColor = ThemeService.Parse(settings.CpuCurveColor, Color.FromRgb(0x00, 0xE0, 0xFF));
        _chart.GpuColor = ThemeService.Parse(settings.GpuCurveColor, Color.FromRgb(0xFF, 0x5A, 0x36));
        _cpuDot.Fill = new SolidColorBrush(_chart.CpuColor);
        _gpuDot.Fill = new SolidColorBrush(_chart.GpuColor);
    }

    public void Load(IEnumerable<(double? Cpu, double? Gpu)> history) => _chart.Load(history);

    public void Push(double? cpu, double? gpu)
    {
        _chart.Push(cpu, gpu);
        Show(cpu, gpu);
    }

    private void Show(double? cpu, double? gpu)
    {
        _cpu.Text = $"CPU {(cpu is { } c ? $"{c:0}°" : "--")}";
        _gpu.Text = $"GPU {(gpu is { } g ? $"{g:0}°" : "--")}";
    }
}
