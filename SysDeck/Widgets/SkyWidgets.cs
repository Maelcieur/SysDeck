using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDeck.Services;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck.Widgets;

/// <summary>
/// Lune : photo de la face visible (carte couleur LROC de la NASA projetée en disque, Assets/Moon/moon.png), léger
/// assombrissement du bord, puis l'ombre de la phase (terminateur elliptique) avec un faible clair de Terre.
/// </summary>
public sealed class MoonView : FrameworkElement
{
    public static readonly DependencyProperty AgeProperty = DependencyProperty.Register(nameof(Age), typeof(double), typeof(MoonView),
        new FrameworkPropertyMetadata(MoonPhase.SynodicMonth / 2, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SouthernProperty = DependencyProperty.Register(nameof(Southern), typeof(bool), typeof(MoonView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Âge de la Lune en jours.</summary>
    public double Age { get => (double)GetValue(AgeProperty); set => SetValue(AgeProperty, value); }

    /// <summary>Vue depuis l'hémisphère sud : image retournée.</summary>
    public bool Southern { get => (bool)GetValue(SouthernProperty); set => SetValue(SouthernProperty, value); }

    private static readonly Lazy<ImageSource?> Photo = new(LoadPhoto);

    private static ImageSource? LoadPhoto()
    {
        try
        {
            using var stream = typeof(MoonView).Assembly.GetManifestResourceStream("moon.png");
            if (stream is null) return null;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Warn($"Image de la Lune illisible ({ex.Message})");
            return null;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = size / 2 * 0.86;
        var lit = MoonPhase.Illumination(Age);

        // Halo, plus fort quand la Lune est pleine.
        var halo = new RadialGradientBrush(Color.FromArgb((byte)(20 + 60 * lit), 0xFF, 0xF6, 0xE0), Color.FromArgb(0, 0xFF, 0xF6, 0xE0));
        dc.DrawEllipse(halo, null, c, r * 1.16, r * 1.16);

        dc.PushClip(new EllipseGeometry(c, r, r));
        // Vue du sud : la Lune apparaît tête en bas.
        if (Southern) dc.PushTransform(new RotateTransform(180, c.X, c.Y));
        if (Photo.Value is { } photo) dc.DrawImage(photo, new Rect(c.X - r, c.Y - r, r * 2, r * 2));
        else dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xC9, 0xC5, 0xBB)), null, c, r, r);
        if (Southern) dc.Pop();

        // Assombrissement du bord.
        var limb = new RadialGradientBrush
        {
            GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.72), new GradientStop(Color.FromArgb(0x50, 0, 0, 0), 1) }
        };
        dc.DrawEllipse(limb, null, c, r, r);

        // Ombre de la phase, avec un clair de Terre très léger.
        var disc = new EllipseGeometry(c, r, r);
        var shadow = new CombinedGeometry(GeometryCombineMode.Exclude, disc, LitGeometry(c, r));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0xE8, 0x07, 0x09, 0x0E)), null, shadow);
        dc.Pop();
    }

    /// <summary>Partie éclairée : demi-disque côté Soleil, fermé par le terminateur (demi-ellipse).</summary>
    private Geometry LitGeometry(Point c, double r)
    {
        var phase = 2 * Math.PI * Age / MoonPhase.SynodicMonth;
        var cos = Math.Cos(phase);              // 1 nouvelle, 0 quartier, -1 pleine
        var rx = Math.Max(0.001, r * Math.Abs(cos));
        var top = new Point(c.X, c.Y - r);
        var bottom = new Point(c.X, c.Y + r);
        var figure = new PathFigure { StartPoint = top, IsClosed = true };
        // Bord éclairé : côté droit quand la Lune croît (hémisphère nord).
        figure.Segments.Add(new ArcSegment(bottom, new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        // Terminateur : bombé vers la partie éclairée (croissant) ou vers l'ombre (gibbeuse).
        figure.Segments.Add(new ArcSegment(top, new Size(rx, r), 0, false,
            cos > 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise, true));
        var geometry = new PathGeometry { Figures = { figure } };
        // Décroissante, ou vue du sud : éclairée de l'autre côté.
        if (!MoonPhase.Waxing(Age) ^ Southern) geometry.Transform = new ScaleTransform(-1, 1, c.X, c.Y);
        return geometry;
    }
}

/// <summary>Phases de la Lune : la Lune du jour, le nom de la phase, l'éclairement, l'âge et les prochaines grandes phases.</summary>
public sealed class MoonWidgetContent : UserControl, ITickingWidget
{
    private readonly MoonView _moon = new() { Width = 136, Height = 136 };
    private readonly TextBlock _phase, _details, _next;
    private DateTime _shown;

    public MoonWidgetContent()
    {
        Width = 340;
        _phase = WidgetUi.Text("phase", 19, weight: FontWeights.SemiBold);
        _details = WidgetUi.Text("details", 12, sub: true);
        _details.Margin = new Thickness(0, 4, 0, 0);
        _next = WidgetUi.Text("next", 11.5);
        _next.Margin = new Thickness(0, 10, 0, 0);
        _next.TextTrimming = TextTrimming.None;
        var caption = WidgetUi.Caption("LUNE", "title");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), Children = { caption, _phase, _details, _next } };
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
        Grid.SetColumn(text, 1);
        grid.Children.Add(_moon);
        grid.Children.Add(text);
        Content = grid;
        Tick(DateTime.Now);
    }

    public bool SouthernHemisphere
    {
        get => _moon.Southern;
        set => _moon.Southern = value;
    }

    /// <summary>Recalculée une fois par minute (la Lune change lentement).</summary>
    public void Tick(DateTime now)
    {
        if (now - _shown < TimeSpan.FromMinutes(1) && now >= _shown) return;
        _shown = now;
        var age = MoonPhase.Age(now.ToUniversalTime());
        _moon.Age = age;
        _phase.Text = MoonPhase.Name(age);
        _details.Text = $"Éclairée à {MoonPhase.Illumination(age) * 100:0} %  ·  âge {age:0.0} jours";
        var upcoming = new[] { (0.0, "Nouvelle lune"), (0.25, "Premier quartier"), (0.5, "Pleine lune"), (0.75, "Dernier quartier") }
            .Select(p => (Date: MoonPhase.Next(now.ToUniversalTime(), p.Item1), Name: p.Item2))
            .OrderBy(p => p.Date)
            .Take(3);
        _next.Text = string.Join("\n", upcoming.Select(p => $"{p.Name} : {WidgetUi.Capitalize(p.Date.ToString("ddd d MMM, HH:mm", WidgetUi.Fr))}"));
    }
}

/// <summary>Course du soleil : arc pointillé du lever au coucher, partie déjà parcourue, soleil à sa place actuelle.</summary>
public sealed class SunArc : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(SunArc),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(SunArc),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Avancement de la journée : 0 au lever, 1 au coucher (en dehors : nuit).</summary>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public SunArc() => SetResourceReference(AccentProperty, "GaugeGradientBrush");

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        const double pad = 14;
        var horizon = h - 8;
        var cx = w / 2;
        var rx = w / 2 - pad;
        var ry = horizon - 16;

        var line = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1);
        dc.DrawLine(line, new Point(0, horizon), new Point(w, horizon));

        // Arc complet en pointillés.
        var dashed = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1.5) { DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0) };
        dc.DrawGeometry(null, dashed, Arc(cx, horizon, rx, ry, 0, 1));

        var p = Progress;
        var day = p is >= 0 and <= 1;
        if (day && p > 0.001)
        {
            // Partie parcourue, en couleur d'accent.
            dc.DrawGeometry(null, new Pen(Accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(cx, horizon, rx, ry, 0, p));
        }

        var t = Math.Clamp(p, 0, 1);
        var angle = Math.PI * (1 - t);
        var sun = new Point(cx + rx * Math.Cos(angle), horizon - ry * Math.Sin(angle));
        if (day)
        {
            var glow = new RadialGradientBrush(Color.FromArgb(0x90, 0xFF, 0xC4, 0x3D), Color.FromArgb(0, 0xFF, 0xC4, 0x3D));
            dc.DrawEllipse(glow, null, sun, 22, 22);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x45)), null, sun, 9, 9);
        }
        else
        {
            // Nuit : soleil éteint, posé sur l'horizon du côté où il se lèvera ou s'est couché.
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xC8, 0x45)), null, sun, 7, 7);
        }
    }

    private static Geometry Arc(double cx, double horizon, double rx, double ry, double from, double to)
    {
        Point At(double t)
        {
            var a = Math.PI * (1 - t);
            return new Point(cx + rx * Math.Cos(a), horizon - ry * Math.Sin(a));
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(from), false, false);
            var steps = Math.Max(2, (int)((to - from) * 60));
            for (var i = 1; i <= steps; i++) ctx.LineTo(At(from + (to - from) * i / steps), true, true);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>Lever et coucher du soleil (ville de la météo) : course du soleil, heures, durée du jour, prochain événement.</summary>
public sealed class SunWidgetContent : UserControl, ITickingWidget
{
    private readonly TextBlock _place, _sunrise, _sunset, _details, _message;
    private readonly SunArc _arc = new() { Height = 104, Margin = new Thickness(0, 6, 0, 4) };
    private readonly FrameworkElement _body;
    private IReadOnlyList<WeatherDay> _days = Array.Empty<WeatherDay>();
    private DateTime _shown;

    public SunWidgetContent()
    {
        Width = 320;
        _place = WidgetUi.Caption("SOLEIL", "title");
        _sunrise = WidgetUi.Text("times", 22, number: true);
        _sunset = WidgetUi.Text("times", 22, number: true);
        _sunset.HorizontalAlignment = HorizontalAlignment.Right;
        _details = WidgetUi.Text("details", 12, sub: true);
        _details.HorizontalAlignment = HorizontalAlignment.Center;
        _details.Margin = new Thickness(0, 8, 0, 0);
        _message = WidgetUi.Text("message", 12, sub: true);
        _message.TextWrapping = TextWrapping.Wrap;
        _message.Margin = new Thickness(0, 8, 0, 0);

        var riseLabel = WidgetUi.Text("details", 11, sub: true);
        riseLabel.Text = "↑ Lever";
        var setLabel = WidgetUi.Text("details", 11, sub: true);
        setLabel.Text = "Coucher ↓";
        setLabel.HorizontalAlignment = HorizontalAlignment.Right;
        var left = new StackPanel { Children = { riseLabel, _sunrise } };
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { setLabel, _sunset } };
        var times = new Grid { Children = { left, right } };
        _body = new StackPanel { Children = { _arc, times, _details } };
        Content = new StackPanel { Children = { _place, _body, _message } };
        Update(null, "Recherche des heures du soleil…");
    }

    public void Update(WeatherSnapshot? weather, string? error)
    {
        _days = weather?.Days ?? Array.Empty<WeatherDay>();
        var ok = _days.Any(d => d.Sunrise is not null && d.Sunset is not null);
        _body.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        _message.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        _message.Text = error ?? "Heures du soleil indisponibles.";
        if (weather is not null && !string.IsNullOrEmpty(weather.Place)) _place.Text = "SOLEIL · " + weather.Place.ToUpper(WidgetUi.Fr);
        _shown = default;
        Tick(DateTime.Now);
    }

    public void Tick(DateTime now)
    {
        if (now - _shown < TimeSpan.FromSeconds(30) && now >= _shown) return;
        _shown = now;
        var today = _days.FirstOrDefault(d => d.Date.Date == now.Date) ?? _days.FirstOrDefault();
        if (today?.Sunrise is not { } rise || today.Sunset is not { } set) return;
        var tomorrow = _days.FirstOrDefault(d => d.Date.Date == now.Date.AddDays(1));

        _sunrise.Text = rise.ToString("HH:mm", WidgetUi.Fr);
        _sunset.Text = set.ToString("HH:mm", WidgetUi.Fr);
        _arc.Progress = (now - rise).TotalMinutes / Math.Max(1, (set - rise).TotalMinutes);

        var length = set - rise;
        string next;
        if (now < rise) next = $"Lever dans {Duration(rise - now)}";
        else if (now < set) next = $"Coucher dans {Duration(set - now)}";
        else next = tomorrow?.Sunrise is { } r2 ? $"Lever demain à {r2:HH:mm}" : "Bonne nuit";
        _details.Text = $"Durée du jour {(int)length.TotalHours} h {length.Minutes:00}  ·  {next}";
    }

    private static string Duration(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00}" : $"{Math.Max(1, (int)t.TotalMinutes)} min";
}
