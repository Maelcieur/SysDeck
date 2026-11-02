using static SysDeck.Controls.DrawingHelpers;
using System.Windows;
using System.Windows.Media;

namespace SysDeck.Controls;

/// <summary>
/// Barres d'égaliseur au dégradé du thème. Trois formes : « Bars » (barres verticales), « Mirror »
/// (symétriques autour d'une ligne centrale) et « Circle » (anneau). Les niveaux reçus sont lissés :
/// montée rapide, retombée douce, et un repère de crête qui redescend lentement (formes barres et miroir).
/// </summary>
public sealed class SpectrumVisualizer : FrameworkElement
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(string), typeof(SpectrumVisualizer),
        new FrameworkPropertyMetadata("Bars", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StartColorProperty = DependencyProperty.Register(
        nameof(StartColor), typeof(Color), typeof(SpectrumVisualizer),
        new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xE0, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndColorProperty = DependencyProperty.Register(
        nameof(EndColor), typeof(Color), typeof(SpectrumVisualizer),
        new FrameworkPropertyMetadata(Color.FromRgb(0x8B, 0x5C, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowEnabledProperty = DependencyProperty.Register(
        nameof(GlowEnabled), typeof(bool), typeof(SpectrumVisualizer),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>"Bars", "Mirror" ou "Circle".</summary>
    public string Mode { get => (string)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public Color StartColor { get => (Color)GetValue(StartColorProperty); set => SetValue(StartColorProperty, value); }
    public Color EndColor { get => (Color)GetValue(EndColorProperty); set => SetValue(EndColorProperty, value); }
    public bool GlowEnabled { get => (bool)GetValue(GlowEnabledProperty); set => SetValue(GlowEnabledProperty, value); }

    private double[] _levels = Array.Empty<double>();
    private double[] _peaks = Array.Empty<double>();
    private int[] _peakHold = Array.Empty<int>();

    private const double Attack = 0.65;   // part de l'écart rattrapée à chaque image en montée
    private const double Decay = 0.82;    // facteur de retombée par image
    private const int PeakHoldFrames = 12; // ~0,4 s à 30 images/s
    private const double PeakFall = 0.018;

    /// <summary>Nouveaux niveaux bruts (0 à 1), lissés puis dessinés.</summary>
    public void Push(double[] raw)
    {
        if (_levels.Length != raw.Length)
        {
            _levels = new double[raw.Length];
            _peaks = new double[raw.Length];
            _peakHold = new int[raw.Length];
        }

        for (var i = 0; i < raw.Length; i++)
        {
            _levels[i] = raw[i] > _levels[i]
                ? _levels[i] + (raw[i] - _levels[i]) * Attack
                : _levels[i] * Decay;

            if (_levels[i] >= _peaks[i])
            {
                _peaks[i] = _levels[i];
                _peakHold[i] = PeakHoldFrames;
            }
            else if (_peakHold[i] > 0)
            {
                _peakHold[i]--;
            }
            else
            {
                _peaks[i] = Math.Max(_levels[i], _peaks[i] - PeakFall);
            }
        }
        InvalidateVisual();
    }

    /// <summary>Vrai tant que des barres sont encore visibles (l'appelant peut ralentir sinon).</summary>
    public bool IsActive
    {
        get
        {
            // Boucle plutôt que LINQ : appelé à chaque image par le minuteur du widget.
            for (var i = 0; i < _levels.Length; i++)
                if (_levels[i] > 0.002 || _peaks[i] > 0.002) return true;
            return false;
        }
    }

    // Brosses et plumes mises en cache (figées) : elles ne dépendent que des couleurs du thème,
    // inutile de les reconstruire ~30 fois par seconde.
    private Color _cacheStart, _cacheEnd;
    private bool _cacheValid;
    private LinearGradientBrush? _fillBars, _fillMirror;
    private SolidColorBrush? _glowBrush, _peakBrush;
    private Pen[] _ringGlowPens = Array.Empty<Pen>(), _ringPens = Array.Empty<Pen>();
    private double _ringThickness;
    private static readonly Pen RingBasePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))), 1));

    private void EnsureColorCache()
    {
        if (_cacheValid && _cacheStart == StartColor && _cacheEnd == EndColor) return;
        _cacheStart = StartColor;
        _cacheEnd = EndColor;
        _cacheValid = true;

        // Dégradé vertical : couleur de début en bas, de fin en haut (au centre vers les bords en miroir).
        _fillBars = Frozen(new LinearGradientBrush(EndColor, StartColor, 90));
        _fillMirror = Frozen(new LinearGradientBrush(new GradientStopCollection
        {
            new(EndColor, 0), new(StartColor, 0.5), new(EndColor, 1)
        }, 90));
        _glowBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, StartColor.R, StartColor.G, StartColor.B)));
        _peakBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xD0, EndColor.R, EndColor.G, EndColor.B)));
        _ringPens = Array.Empty<Pen>(); // dépendent aussi des couleurs : à refaire
        _ringGlowPens = Array.Empty<Pen>();
    }

    /// <summary>Plumes de l'anneau : une par barre (couleur selon la position), refaites si le nombre ou l'épaisseur change.</summary>
    private void EnsureRingPens(int count, double thickness)
    {
        if (_ringPens.Length == count && Math.Abs(_ringThickness - thickness) < 0.01) return;
        _ringThickness = thickness;
        _ringPens = new Pen[count];
        _ringGlowPens = new Pen[count];
        for (var i = 0; i < count; i++)
        {
            var t = i < count / 2 ? i / (count / 2.0) : (count - i) / (count / 2.0); // aller-retour : pas de cassure de couleur
            var color = Lerp(StartColor, EndColor, t);
            _ringGlowPens[i] = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x35, color.R, color.G, color.B))), thickness + 3)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            _ringPens[i] = Frozen(new Pen(Frozen(new SolidColorBrush(color)), thickness)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var count = _levels.Length;
        if (w <= 0 || h <= 0 || count == 0) return;

        switch (Mode)
        {
            case "Circle": RenderCircle(dc, w, h, count); break;
            case "Mirror": RenderBars(dc, w, h, count, mirror: true); break;
            default: RenderBars(dc, w, h, count, mirror: false); break;
        }
    }

    private void RenderBars(DrawingContext dc, double w, double h, int count, bool mirror)
    {
        var slot = w / count;
        var gap = Math.Clamp(slot * 0.28, 1, 4);
        var barWidth = Math.Max(1, slot - gap);
        var radius = Math.Min(barWidth / 2, 2.5);

        EnsureColorCache();
        var fill = mirror ? _fillMirror! : _fillBars!;
        var glow = GlowEnabled ? _glowBrush : null;
        var peakBrush = _peakBrush!;

        var mid = h / 2;
        for (var i = 0; i < count; i++)
        {
            var x = i * slot + gap / 2;
            var level = _levels[i];
            var barHeight = Math.Max(1.5, level * (mirror ? mid : h));

            var rect = mirror
                ? new Rect(x, mid - barHeight, barWidth, barHeight * 2)
                : new Rect(x, h - barHeight, barWidth, barHeight);

            // Halo : la même barre, un peu plus large et transparente, dessous.
            if (glow is not null && level > 0.05)
                dc.DrawRoundedRectangle(glow, null, Rect.Inflate(rect, 1.5, 1.5), radius + 1, radius + 1);
            dc.DrawRoundedRectangle(fill, null, rect, radius, radius);

            if (_peaks[i] > 0.02)
            {
                var peakY = mirror ? mid - _peaks[i] * mid : h - _peaks[i] * h;
                dc.DrawRectangle(peakBrush, null, new Rect(x, Math.Max(0, peakY - 2), barWidth, 1.5));
                if (mirror)
                    dc.DrawRectangle(peakBrush, null, new Rect(x, Math.Min(h - 1.5, mid + _peaks[i] * mid + 0.5), barWidth, 1.5));
            }
        }
    }

    private void RenderCircle(DrawingContext dc, double w, double h, int count)
    {
        var cx = w / 2;
        var cy = h / 2;
        var outer = Math.Min(w, h) / 2 - 2;
        var inner = outer * 0.42;
        var maxLength = outer - inner;
        var thickness = Math.Clamp(2 * Math.PI * inner / count * 0.55, 1.5, 6);

        EnsureColorCache();
        EnsureRingPens(count, thickness);

        // Anneau de base discret
        dc.DrawEllipse(null, RingBasePen, new Point(cx, cy), inner - 3, inner - 3);

        for (var i = 0; i < count; i++)
        {
            // Départ en haut, sens horaire ; couleur du dégradé selon la position sur le cercle.
            var angle = -Math.PI / 2 + 2 * Math.PI * i / count;
            var length = Math.Max(1.5, _levels[i] * maxLength);
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            var from = new Point(cx + cos * inner, cy + sin * inner);
            var to = new Point(cx + cos * (inner + length), cy + sin * (inner + length));

            if (GlowEnabled && _levels[i] > 0.05)
                dc.DrawLine(_ringGlowPens[i], from, to);
            dc.DrawLine(_ringPens[i], from, to);
        }
    }
}
