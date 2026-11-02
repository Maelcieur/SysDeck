using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SysDeck.Services;

namespace SysDeck.Controls;

// Ombre, survol et apparition de la carte. Tout est posé sur Root (ombre + cadre) et non sur le contenu :
// aucun Effect, aucun pinceau recréé lors du rafraîchissement des valeurs, seuls les changements de réglages
// ou d'état de la souris font du travail.
public partial class ModuleCard
{
    // Marge réservée autour de la carte pour le survol « Lift » : la fenêtre ne rogne pas l'agrandissement.
    private const double LiftMargin = 12;
    private const double LiftScale = 1.02;

    private readonly ScaleTransform _entranceScale = new(1, 1);
    private readonly TranslateTransform _entranceMove = new();
    private readonly ScaleTransform _hoverScale = new(1, 1);
    private readonly ScaleTransform _idleScale = new(1, 1);
    private readonly TranslateTransform _idleMove = new();
    private string _idleEffect = "None";
    private Brush? _pulseBrush; // contour animé de « Contour pulsé », remis après un survol « Lueur »

    private string _hoverEffect = "None";
    private Brush? _hoverBrush;
    // Marge transparente autour de la carte (ombre, survol « Lift »), en pixels. La fenêtre qui héberge la
    // carte s'agrandit d'autant : elle doit se décaler pour que la carte reste à sa place (voir OuterMarginChanged).
    private double _outerMargin;

    /// <summary>Levé avec la nouvelle marge quand elle change ; l'hôte compense alors sa position de (nouvelle − ancienne).</summary>
    public event Action<double>? OuterMarginChanged;

    private void InitEffects()
    {
        Root.RenderTransformOrigin = new Point(0.5, 0.5);
        Root.RenderTransform = new TransformGroup { Children = { _entranceScale, _entranceMove, _hoverScale, _idleScale, _idleMove } };
        Chrome.MouseEnter += (_, _) => SetHover(true);
        Chrome.MouseLeave += (_, _) => SetHover(false);
    }

    /// <summary>
    /// Ombre et survol selon le thème effectif. Bureau seulement : sur la Switch (cadre imposé, rendu en image)
    /// aucune marge n'est disponible et la souris n'existe pas, ces réglages y sont ignorés.
    /// </summary>
    private void ApplyCardEffects(ThemeSettings theme)
    {
        var enabled = _useDefaultChrome && !_fillFrame;

        var shadowSize = enabled && theme.ShadowEnabled ? Math.Clamp(theme.ShadowSize, 0, 80) : 0;
        var showShadow = shadowSize >= 1 && theme.ShadowOpacity > 0;
        Shadow.Visibility = showShadow ? Visibility.Visible : Visibility.Collapsed;
        if (showShadow)
            Shadow.Configure(ThemeService.Parse(theme.ShadowColor, Colors.Black), shadowSize, theme.ShadowOpacity, Math.Max(0, theme.CornerRadius));

        var hover = enabled && theme.HoverEffect is "Lift" or "Glow" or "LiftGlow" ? theme.HoverEffect : "None";
        if (hover != _hoverEffect)
        {
            SetHover(false); // retour à l'état normal avant de changer de mode
            _hoverEffect = hover;
        }
        _hoverBrush = hover is "Glow" or "LiftGlow" ? ThemeService.CreateHoverGlowBrush(theme) : null;

        ApplyIdleEffect(enabled && theme.AnimationsEnabled ? theme.IdleEffect : "None", theme);

        // La marge ne change qu'avec les réglages : la fenêtre ne se redimensionne pas à chaque rafraîchissement.
        var moves = hover is "Lift" or "LiftGlow" || _idleEffect is "Breathe" or "Float";
        var margin = Math.Max(showShadow ? Math.Ceiling(shadowSize) : 0, moves ? LiftMargin : 0);
        if (margin == _outerMargin) return;
        _outerMargin = margin;
        Root.Margin = new Thickness(margin);
        OuterMarginChanged?.Invoke(margin);
    }

    private void SetHover(bool hovered)
    {
        if (_hoverEffect == "None" || _appearance is null)
        {
            // Mode désactivé : s'assure qu'aucun reste de survol ne traîne.
            if (!hovered && _hoverScale.ScaleX != 1) ResetHover();
            return;
        }

        if (_hoverEffect is "Glow" or "LiftGlow")
        {
            if (hovered && _hoverBrush is not null) Chrome.BorderBrush = _hoverBrush;
            else RestoreBorder();
            if (_hoverEffect == "Glow") return;
        }

        var theme = _appearance.EffectiveTheme(_config.Theme!);
        var target = hovered ? LiftScale : 1;
        if (!theme.AnimationsEnabled)
        {
            ResetHover(target);
            return;
        }

        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(150 * theme.AnimationSpeed))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _hoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        _hoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void ResetHover(double scale = 1)
    {
        _hoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _hoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _hoverScale.ScaleX = _hoverScale.ScaleY = scale;
        if (scale == 1) RestoreBorder();
    }

    /// <summary>Contour normal : celui du thème, ou le contour pulsé s'il est actif.</summary>
    private void RestoreBorder()
    {
        if (_pulseBrush is not null) Chrome.BorderBrush = _pulseBrush;
        else Chrome.ClearValue(System.Windows.Controls.Border.BorderBrushProperty);
    }

    /// <summary>Apparition du module à l'ouverture, selon <see cref="ThemeSettings.EntranceStyle"/> (bureau seulement).</summary>
    public void PlayAppearAnimation()
    {
        var theme = _appearance?.EffectiveTheme(_config.Theme!) ?? _config.Theme!;
        var style = theme.EntranceStyle;
        if (!theme.AnimationsEnabled || style == "None") return;

        var duration = TimeSpan.FromMilliseconds(500 * theme.AnimationSpeed);
        var cubic = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Fondu cubique quel que soit AnimationEasing : une courbe élastique ferait vibrer l'opacité.
        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = cubic };
        fade.Completed += (_, _) => Root.BeginAnimation(OpacityProperty, null); // libère la propriété une fois terminé
        Root.BeginAnimation(OpacityProperty, fade);

        // Fin d'animation : la propriété est libérée pour ne pas garder une valeur figée par l'animation.
        void Release(Animatable target, params DependencyProperty[] properties)
        {
            foreach (var property in properties) target.BeginAnimation(property, null);
        }

        switch (style)
        {
            case "Fade":
                break;
            case "Drop":
            {
                var drop = new DoubleAnimation(-40, 0, duration * 1.3) { EasingFunction = new BounceEase { EasingMode = EasingMode.EaseOut, Bounces = 2, Bounciness = 4 } };
                drop.Completed += (_, _) => Release(_entranceMove, TranslateTransform.YProperty);
                _entranceMove.BeginAnimation(TranslateTransform.YProperty, drop);
                break;
            }
            case "Pop":
            {
                var pop = new DoubleAnimation(0.6, 1, duration) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
                pop.Completed += (_, _) => Release(_entranceScale, ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty);
                _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                break;
            }
            case "Flip":
            {
                // Se déplie de la tranche, comme une carte qu'on retourne.
                var flip = new DoubleAnimation(0, 1, duration) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } };
                flip.Completed += (_, _) => Release(_entranceScale, ScaleTransform.ScaleXProperty);
                _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, flip);
                break;
            }
            case "Zoom":
                var zoom = new DoubleAnimation(0.88, 1, duration) { EasingFunction = ThemeService.CreateEasing(theme.AnimationEasing) };
                zoom.Completed += (_, _) =>
                {
                    _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                };
                _entranceScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
                _entranceScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
                break;
            default: // "Slide" et toute valeur inconnue : le fondu glissé d'origine
                var slide = new DoubleAnimation(14, 0, duration) { EasingFunction = cubic };
                slide.Completed += (_, _) => _entranceMove.BeginAnimation(TranslateTransform.YProperty, null);
                _entranceMove.BeginAnimation(TranslateTransform.YProperty, slide);
                break;
        }
    }

    /// <summary>
    /// Animation continue au repos. 20 images/s : le mouvement est lent, et chaque image redessine toute la fenêtre
    /// transparente, inutile d'en faire plus.
    /// </summary>
    private void ApplyIdleEffect(string effect, ThemeSettings theme)
    {
        if (effect is not ("Breathe" or "Float" or "Pulse")) effect = "None";
        if (effect == _idleEffect && effect != "Pulse") return;
        _idleEffect = effect;

        _idleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _idleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _idleMove.BeginAnimation(TranslateTransform.YProperty, null);
        _pulseBrush = null;
        Chrome.ClearValue(System.Windows.Controls.Border.BorderBrushProperty);

        DoubleAnimation Loop(double from, double to, double seconds)
        {
            var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds * Math.Clamp(theme.AnimationSpeed, 0.3, 3)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(animation, 20);
            return animation;
        }

        switch (effect)
        {
            case "Breathe":
                var breathe = Loop(1, 1.015, 2.6);
                _idleScale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
                _idleScale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
                break;
            case "Float":
                _idleMove.BeginAnimation(TranslateTransform.YProperty, Loop(0, -5, 2.2));
                break;
            case "Pulse":
                // Contour aux couleurs des jauges dont l'opacité monte et descend.
                var pulse = (Brush)ThemeService.CreateHoverGlowBrush(theme).CloneCurrentValue();
                pulse.BeginAnimation(Brush.OpacityProperty, Loop(0.25, 1, 1.6));
                Chrome.BorderBrush = _pulseBrush = pulse;
                break;
        }
    }
}
