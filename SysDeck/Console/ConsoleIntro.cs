using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NAudio.Wave;
using SysDeck.Infrastructure;
using WpfImage = System.Windows.Controls.Image;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

using Rectangle = System.Windows.Shapes.Rectangle;
using Path = System.Windows.Shapes.Path;

namespace SysDeck.ConsoleMode;

/// <summary>
/// Animation de lancement du Mode console (~8 s). Versions intégrées : « élégante » (brume, rayons, emblème original)
/// ou « cyberpunk » (terminal, glitch, extinction CRT), avec une bande-son synthétisée. Aucun logo de marque.
/// Version personnalisée : les images d'un dossier choisi par l'utilisateur, chacune avec le son du même nom.
/// </summary>
public static class ConsoleIntro
{
    private const double W = 1920, H = 1080;
    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
    private static readonly string[] SoundExt = { ".wav", ".mp3", ".m4a", ".wma", ".aac" };

    /// <summary>Joue l'intro dans <paramref name="host"/> ; se termine à la fin ou dès que <paramref name="ct"/> est annulé (passer).</summary>
    /// <summary>Style d'intro (et de bruitages) : « cyber », « elegant », ou « auto » = cyberpunk avec un thème glitch.</summary>
    public static bool IsCyber(string? style, ConsoleTheme theme) => style == "cyber" || (style is null or "" or "auto" && theme.Glitch);

    public static async Task PlayAsync(Grid host, ConsoleTheme theme, string? folder, CancellationToken ct, string? audioDeviceId = null, string? style = "auto")
    {
        var stage = new Grid { Width = W, Height = H, ClipToBounds = true };
        var view = new Viewbox { Stretch = Stretch.UniformToFill, Child = stage };
        var root = new Grid { Background = Brushes.Black };
        root.Children.Add(view);
        host.Children.Clear();
        host.Children.Add(root);
        host.Visibility = Visibility.Visible;

        var sounds = new List<IDisposable>();
        try
        {
            var items = string.IsNullOrWhiteSpace(folder) ? null : CustomItems(folder);
            if (items is { Count: > 0 })
                await PlayCustomAsync(stage, items, sounds, audioDeviceId, ct);
            else
            {
                // Le son est calculé AVANT de lancer les animations : image et son partent ensemble.
                var cyber = IsCyber(style, theme);
                var pcm = await Task.Run(() => cyber ? IntroSound.BuildCyber() : IntroSound.Build(), ct);
                var total = cyber ? BuildCyber(stage, theme) : BuildBuiltIn(stage, theme);
                if (IntroSound.Play(pcm, audioDeviceId) is { } player) sounds.Add(player);
                await Task.Delay(TimeSpan.FromSeconds(total), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Intro du mode console", ex);
        }

        // Sortie en fondu (aussi quand on passe l'intro).
        root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(700)) { EasingFunction = Out });
        await Task.Delay(720);
        lock (sounds)
            foreach (var s in sounds) try { s.Dispose(); } catch { }
        host.Children.Clear();
        host.Visibility = Visibility.Collapsed;
    }

    // ---------- Intro intégrée ----------

    private static Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);
    private static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };

    private static DoubleAnimation Anim(double from, double to, double at, double dur, IEasingFunction? ease = null) =>
        new(from, to, S(dur)) { BeginTime = S(at), EasingFunction = ease ?? Out, FillBehavior = FillBehavior.HoldEnd };

    private static double BuildBuiltIn(Grid stage, ConsoleTheme theme)
    {
        var accent = theme.Accent;
        var soft = Color.FromRgb((byte)((accent.R + 255 * 2) / 3), (byte)((accent.G + 255 * 2) / 3), (byte)((accent.B + 255 * 2) / 3));
        var all = new Grid { Width = W, Height = H, RenderTransformOrigin = new Point(0.5, 0.5) };
        stage.Children.Add(new Rectangle { Fill = Brushes.Black });
        stage.Children.Add(all);
        var rnd = new Random(21);

        // 1. Brouillard lumineux qui monte du bas, puis lueur centrale (dégradés plein écran : aucun bord visible).
        var fog = new Rectangle
        {
            Opacity = 0,
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0, accent.R, accent.G, accent.B), 0), new(Color.FromArgb(0, accent.R, accent.G, accent.B), 0.45),
                new(Color.FromArgb(0x40, accent.R, accent.G, accent.B), 0.85), new(Color.FromArgb(0x70, accent.R, accent.G, accent.B), 1),
            }, 90),
        };
        fog.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, 0.4, 2.5, new SineEase()));
        all.Children.Add(fog);
        var glowBrush = new RadialGradientBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B)) { RadiusX = 0.25, RadiusY = 0.35 };
        var glow = new Rectangle { Fill = glowBrush, Opacity = 0 };
        glow.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, 2.0, 2.0));
        glowBrush.BeginAnimation(RadialGradientBrush.RadiusXProperty, Anim(0.25, 0.6, 2.0, 5.5, new SineEase()));
        glowBrush.BeginAnimation(RadialGradientBrush.RadiusYProperty, Anim(0.35, 0.8, 2.0, 5.5, new SineEase()));
        all.Children.Add(glow);

        // Poussières en suspension (petits points flous qui dérivent).
        var dust = new Canvas { Width = W, Height = H };
        for (var i = 0; i < 45; i++)
        {
            var s = 2 + rnd.NextDouble() * 5;
            var e = new Ellipse { Width = s, Height = s, Opacity = 0, Fill = new RadialGradientBrush(Color.FromArgb(0xCC, soft.R, soft.G, soft.B), Color.FromArgb(0, soft.R, soft.G, soft.B)) };
            var tr = new TranslateTransform();
            e.RenderTransform = tr;
            var at = 0.6 + rnd.NextDouble() * 2;
            e.BeginAnimation(UIElement.OpacityProperty, Anim(0, 0.25 + rnd.NextDouble() * 0.5, at, 1.6));
            tr.BeginAnimation(TranslateTransform.YProperty, Anim(0, -80 - rnd.NextDouble() * 140, at, 8, new SineEase()));
            tr.BeginAnimation(TranslateTransform.XProperty, Anim(0, (rnd.NextDouble() - 0.5) * 120, at, 8, new SineEase()));
            System.Windows.Controls.Canvas.SetLeft(e, rnd.NextDouble() * W);
            System.Windows.Controls.Canvas.SetTop(e, 300 + rnd.NextDouble() * (H - 300));
            dust.Children.Add(e);
        }
        all.Children.Add(dust);

        // 2. Trait de lumière, puis éventail de rayons depuis le centre.
        var line = new Rectangle
        {
            Width = 1400, Height = 2, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5),
            Fill = new LinearGradientBrush(new GradientStopCollection { new(Colors.Transparent, 0), new(Colors.White, 0.5), new(Colors.Transparent, 1) }, 0),
            Effect = new DropShadowEffect { Color = accent, BlurRadius = 24, ShadowDepth = 0 },
        };
        var lineScale = new ScaleTransform(0, 1);
        line.RenderTransform = lineScale;
        lineScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0, 1, 1.0, 0.9));
        var lf = new DoubleAnimationUsingKeyFrames();
        lf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(S(1.0))));
        lf.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(S(1.2))));
        lf.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(S(2.0))));
        lf.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(S(2.8)), new SineEase()));
        line.BeginAnimation(UIElement.OpacityProperty, lf);
        all.Children.Add(line);

        var rays = new Grid { Width = W, Height = H, Opacity = 0 };
        for (var i = 0; i < 9; i++)
        {
            var angle = -48 + i * 12;
            var ray = new Rectangle
            {
                Width = 3 + (i % 3), Height = 900, RenderTransformOrigin = new Point(0.5, 1), VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -360, 0, 0),
                Fill = new LinearGradientBrush(Color.FromArgb(0, soft.R, soft.G, soft.B), Color.FromArgb(0x55, soft.R, soft.G, soft.B), 90),
                RenderTransform = new RotateTransform(angle),
            };
            ray.BeginAnimation(UIElement.OpacityProperty, Anim(0.3, 1, 1.9 + i * 0.05, 1.2, new SineEase()));
            rays.Children.Add(ray);
        }
        var rf = new DoubleAnimationUsingKeyFrames();
        rf.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(S(1.8))));
        rf.KeyFrames.Add(new EasingDoubleKeyFrame(0.8, KeyTime.FromTimeSpan(S(2.6)), new SineEase()));
        rf.KeyFrames.Add(new LinearDoubleKeyFrame(0.8, KeyTime.FromTimeSpan(S(3.6))));
        rf.KeyFrames.Add(new EasingDoubleKeyFrame(0.15, KeyTime.FromTimeSpan(S(5.0)), new SineEase()));
        rays.BeginAnimation(UIElement.OpacityProperty, rf);
        var raysRot = new RotateTransform(0, W / 2, H / 2);
        rays.RenderTransform = raysRot;
        raysRot.BeginAnimation(RotateTransform.AngleProperty, Anim(-6, 6, 1.8, 6.5, new SineEase { EasingMode = EasingMode.EaseInOut }));
        all.Children.Add(rays);

        // 3. Emblème : deux cartes superposées tracées au trait, puis remplies (effet verre).
        var mark = new Canvas { Width = 220, Height = 220, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 380), RenderTransformOrigin = new Point(0.5, 0.5) };
        foreach (var (x, y, delay) in new[] { (30.0, 10.0, 0.0), (70.0, 50.0, 0.25) })
        {
            const double cw = 120, ch = 160, th = 3;
            var perim = (2 * (cw + ch) - (8 - 2 * Math.PI) * 18) / th;
            var card = new Rectangle
            {
                Width = cw, Height = ch, RadiusX = 18, RadiusY = 18, StrokeThickness = th,
                Stroke = new LinearGradientBrush(Colors.White, soft, 45),
                StrokeDashArray = new DoubleCollection { perim, perim }, StrokeDashOffset = perim, StrokeDashCap = PenLineCap.Round,
                Fill = new LinearGradientBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B), Color.FromArgb(0x10, 255, 255, 255), 60) { Opacity = 0 },
                Effect = new DropShadowEffect { Color = accent, BlurRadius = 30, ShadowDepth = 0, Opacity = 0.8 },
            };
            card.BeginAnimation(Shape.StrokeDashOffsetProperty, Anim(perim, 0, 2.4 + delay, 1.2, new SineEase { EasingMode = EasingMode.EaseInOut }));
            card.Fill.BeginAnimation(Brush.OpacityProperty, Anim(0, 1, 3.4 + delay, 0.8));
            System.Windows.Controls.Canvas.SetLeft(card, x);
            System.Windows.Controls.Canvas.SetTop(card, y - 20);
            mark.Children.Add(card);
        }
        var markScale = new ScaleTransform();
        mark.RenderTransform = markScale;
        markScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1.25, 1.4, 2.4, 2.5, new SineEase()));
        markScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1.25, 1.4, 2.4, 2.5, new SineEase()));
        all.Children.Add(mark);

        // 4. Le nom : se resserre (lettres espacées -> normales) en passant du flou au net.
        var shine = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        var s0 = new GradientStop(Colors.White, -0.4);
        var s1 = new GradientStop(Colors.White, -0.25);
        var s2 = new GradientStop(soft, -0.1);
        shine.GradientStops.Add(new GradientStop(soft, 0));
        shine.GradientStops.Add(s0);
        shine.GradientStops.Add(s1);
        shine.GradientStops.Add(s2);
        shine.GradientStops.Add(new GradientStop(Color.FromRgb(0xE8, 0xEC, 0xF2), 1));
        var word = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 230, 0, 0), Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
        var nameBlur = new BlurEffect { Radius = 26 };
        var name = new TextBlock { Text = "SysDeck", FontSize = 150, FontWeight = FontWeights.Light, Foreground = shine, HorizontalAlignment = HorizontalAlignment.Center, Effect = nameBlur };
        word.Children.Add(name);
        var rule = new Rectangle
        {
            Width = 520, Height = 1.5, Margin = new Thickness(0, 10, 0, 0), RenderTransformOrigin = new Point(0.5, 0.5),
            Fill = new LinearGradientBrush(new GradientStopCollection { new(Colors.Transparent, 0), new(soft, 0.5), new(Colors.Transparent, 1) }, 0),
        };
        var ruleScale = new ScaleTransform(0, 1);
        rule.RenderTransform = ruleScale;
        ruleScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0, 1, 5.0, 0.9));
        word.Children.Add(rule);
        var sub = new TextBlock
        {
            Text = "M O D E    C O N S O L E", FontSize = 24, FontWeight = FontWeights.Normal, Opacity = 0,
            Foreground = B(Color.FromRgb(0xB8, 0xC0, 0xCE)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0),
        };
        sub.BeginAnimation(UIElement.OpacityProperty, Anim(0, 0.8, 5.4, 1.0));
        word.Children.Add(sub);
        var wordScale = new ScaleTransform();
        word.RenderTransform = wordScale;
        word.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, 4.4, 1.0, new SineEase()));
        wordScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1.18, 1, 4.4, 1.6));
        wordScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1.04, 1, 4.4, 1.6));
        nameBlur.BeginAnimation(BlurEffect.RadiusProperty, Anim(26, 0, 4.4, 1.2));
        all.Children.Add(word);

        // 5. Reflet qui balaie le nom (dégradé qui traverse les lettres).
        s0.BeginAnimation(GradientStop.OffsetProperty, Anim(-0.4, 1.1, 5.8, 1.1, new SineEase { EasingMode = EasingMode.EaseInOut }));
        s1.BeginAnimation(GradientStop.OffsetProperty, Anim(-0.25, 1.25, 5.8, 1.1, new SineEase { EasingMode = EasingMode.EaseInOut }));
        s2.BeginAnimation(GradientStop.OffsetProperty, Anim(-0.1, 1.4, 5.8, 1.1, new SineEase { EasingMode = EasingMode.EaseInOut }));

        // 6. Légère avancée de caméra avant le fondu vers le menu (fondu fait par PlayAsync).
        var allScale = new ScaleTransform();
        all.RenderTransform = allScale;
        allScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1, 1.04, 5.5, 2.8, new SineEase()));
        allScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1, 1.04, 5.5, 2.8, new SineEase()));
        return 8.0;
    }

    // ---------- Intro cyberpunk ----------

    /// <summary>
    /// Démarrage façon terminal, grille néon, « SysDeck » en trois couleurs qui se recalent, sauts de glitch,
    /// puis extinction façon écran cathodique. ~8 s.
    /// </summary>
    private static double BuildCyber(Grid stage, ConsoleTheme theme)
    {
        var magenta = theme.Glitch ? theme.Accent : Color.FromRgb(0xFF, 0x2A, 0x6D);
        var cyan = Color.FromRgb(0x05, 0xD9, 0xE8);
        var mono = new FontFamily("Consolas");
        var rnd = new Random(77);
        var all = new Grid { Width = W, Height = H, RenderTransformOrigin = new Point(0.5, 0.5) };
        stage.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromRgb(0x05, 0x02, 0x0A)) });
        stage.Children.Add(all);

        // Lignes de terminal (apparaissent une par une), puis se brouillent.
        var boot = new StackPanel { Margin = new Thickness(140, 150, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var lines = new[]
        {
            "> SYSDECK OS 1.13  //  INIT", "> LINK GPU ............ OK", "> AUDIO HDMI .......... OK", "> PAD XINPUT .......... OK",
            "> NET TV .............. OK", "> LOADING INTERFACE", "> [████████████████████] 100%",
        };
        for (var i = 0; i < lines.Length; i++)
        {
            var at = 0.35 + i * 0.22;
            var tb = new TextBlock { Text = lines[i], FontFamily = mono, FontSize = 30, Foreground = B(i == lines.Length - 1 ? magenta : cyan), Opacity = 0, Margin = new Thickness(0, 0, 0, 8) };
            var f = new DoubleAnimationUsingKeyFrames();
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(S(0))));
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(S(at))));
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(S(at + 0.04))));
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(S(at + 0.08))));
            // Brouillage puis disparition (2.2 - 2.5 s).
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(S(2.2 + i * 0.01))));
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.8, KeyTime.FromTimeSpan(S(2.28))));
            f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(S(2.4 + i * 0.015))));
            tb.BeginAnimation(UIElement.OpacityProperty, f);
            var tr = new TranslateTransform();
            tb.RenderTransform = tr;
            var j = new DoubleAnimationUsingKeyFrames();
            j.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(S(0))));
            j.KeyFrames.Add(new DiscreteDoubleKeyFrame((rnd.NextDouble() - 0.5) * 60, KeyTime.FromTimeSpan(S(2.2))));
            j.KeyFrames.Add(new DiscreteDoubleKeyFrame((rnd.NextDouble() - 0.5) * 120, KeyTime.FromTimeSpan(S(2.3))));
            tr.BeginAnimation(TranslateTransform.XProperty, j);
            boot.Children.Add(tb);
        }
        all.Children.Add(boot);

        // Fond : dégradé sobre du noir (haut) au gris (bas), qui apparaît quand le terminal se brouille.
        var floor = new Rectangle
        {
            Opacity = 0,
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0x00, 0x00, 0x00), 0), new(Color.FromRgb(0x14, 0x14, 0x17), 0.5), new(Color.FromRgb(0x3A, 0x3A, 0x40), 1),
            }, 90),
        };
        floor.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, 1.7, 1.2));
        all.Children.Add(floor);

        // « SysDeck » : trois couches (cyan, magenta, blanc) qui tremblent puis se recalent.
        var title = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 160) };
        foreach (var (color, amp, z) in new[] { (cyan, 26.0, 0), (magenta, -26.0, 1), (Colors.White, 0.0, 2) })
        {
            var t = new TextBlock
            {
                Text = "SysDeck", FontFamily = mono, FontSize = 170, FontWeight = FontWeights.Bold, Foreground = B(color),
                Opacity = 0, Effect = z == 2 ? new DropShadowEffect { Color = magenta, BlurRadius = 45, ShadowDepth = 0, Opacity = 0.9 } : null,
            };
            var op = new DoubleAnimationUsingKeyFrames();
            op.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(S(0))));
            op.KeyFrames.Add(new DiscreteDoubleKeyFrame(z == 2 ? 1 : 0.75, KeyTime.FromTimeSpan(S(2.6))));
            op.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(S(2.68))));
            op.KeyFrames.Add(new DiscreteDoubleKeyFrame(z == 2 ? 1 : 0.7, KeyTime.FromTimeSpan(S(2.75))));
            t.BeginAnimation(UIElement.OpacityProperty, op);
            var tr = new TranslateTransform();
            t.RenderTransform = tr;
            var x = new DoubleAnimationUsingKeyFrames();
            x.KeyFrames.Add(new DiscreteDoubleKeyFrame(amp, KeyTime.FromTimeSpan(S(2.6))));
            x.KeyFrames.Add(new DiscreteDoubleKeyFrame(-amp * 0.6, KeyTime.FromTimeSpan(S(2.72))));
            x.KeyFrames.Add(new DiscreteDoubleKeyFrame(amp * 0.4, KeyTime.FromTimeSpan(S(2.84))));
            x.KeyFrames.Add(new EasingDoubleKeyFrame(amp == 0 ? 0 : amp * 0.12, KeyTime.FromTimeSpan(S(3.4)), Out));
            // Sauts de glitch plus tard.
            foreach (var g in new[] { 4.2, 5.6 })
            {
                x.KeyFrames.Add(new DiscreteDoubleKeyFrame(amp * 0.12, KeyTime.FromTimeSpan(S(g))));
                x.KeyFrames.Add(new DiscreteDoubleKeyFrame(amp * 1.1 + (z == 2 ? 14 : 0), KeyTime.FromTimeSpan(S(g + 0.04))));
                x.KeyFrames.Add(new DiscreteDoubleKeyFrame(-amp * 0.7 - (z == 2 ? 8 : 0), KeyTime.FromTimeSpan(S(g + 0.09))));
                x.KeyFrames.Add(new DiscreteDoubleKeyFrame(amp * 0.12, KeyTime.FromTimeSpan(S(g + 0.15))));
            }
            tr.BeginAnimation(TranslateTransform.XProperty, x);
            title.Children.Add(t);
        }
        var titleGlow = new DoubleAnimationUsingKeyFrames { BeginTime = S(3.4), Duration = S(1.6), RepeatBehavior = RepeatBehavior.Forever };
        titleGlow.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(S(0))));
        titleGlow.KeyFrames.Add(new LinearDoubleKeyFrame(0.86, KeyTime.FromTimeSpan(S(0.8))));
        titleGlow.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(S(1.6))));
        title.BeginAnimation(UIElement.OpacityProperty, titleGlow);
        all.Children.Add(title);

        // « // MODE CONSOLE » tapé lettre par lettre.
        var sub = new TextBlock { FontFamily = mono, FontSize = 34, Foreground = B(cyan), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 130, 0, 0) };
        const string subText = "// MODE CONSOLE";
        var typing = new StringAnimationUsingKeyFrames();
        for (var i = 0; i <= subText.Length; i++)
            typing.KeyFrames.Add(new DiscreteStringKeyFrame(subText[..i] + (i < subText.Length ? "_" : ""), KeyTime.FromTimeSpan(S(3.5 + i * 0.05))));
        sub.BeginAnimation(TextBlock.TextProperty, typing);
        all.Children.Add(sub);

        // Barres de parasites aux moments forts.
        var bars = new Canvas { Width = W, Height = H, IsHitTestVisible = false };
        foreach (var g in new[] { 2.2, 2.6, 4.2, 5.6, 7.3 })
            for (var i = 0; i < 7; i++)
            {
                var bar = new Rectangle
                {
                    Width = 150 + rnd.NextDouble() * 1100, Height = 3 + rnd.NextDouble() * 24, Opacity = 0,
                    Fill = B(Color.FromArgb((byte)(0x70 + rnd.Next(0x70)), (i % 2 == 0 ? cyan : magenta).R, (i % 2 == 0 ? cyan : magenta).G, (i % 2 == 0 ? cyan : magenta).B)),
                };
                var start = g + rnd.NextDouble() * 0.12;
                var f = new DoubleAnimationUsingKeyFrames();
                f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(S(0))));
                f.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(S(start))));
                f.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(S(start + 0.05 + rnd.NextDouble() * 0.1))));
                bar.BeginAnimation(UIElement.OpacityProperty, f);
                System.Windows.Controls.Canvas.SetLeft(bar, rnd.NextDouble() * (W - 300));
                System.Windows.Controls.Canvas.SetTop(bar, rnd.NextDouble() * H);
                bars.Children.Add(bar);
            }
        all.Children.Add(bars);

        // Lignes de balayage par-dessus tout (motif répété).
        stage.Children.Add(new Rectangle
        {
            IsHitTestVisible = false,
            Fill = new DrawingBrush(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), null, new RectangleGeometry(new Rect(0, 0, 4, 1))))
            { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 4, 4), ViewportUnits = BrushMappingMode.Absolute },
        });

        // Extinction façon écran cathodique : l'image se tasse en une ligne lumineuse.
        var crt = new ScaleTransform(1, 1);
        all.RenderTransform = crt;
        var squash = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        squash.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(S(7.35))));
        squash.KeyFrames.Add(new EasingDoubleKeyFrame(0.006, KeyTime.FromTimeSpan(S(7.6)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        crt.BeginAnimation(ScaleTransform.ScaleYProperty, squash);
        var flash = new Rectangle { Height = 6, Fill = Brushes.White, Opacity = 0, VerticalAlignment = VerticalAlignment.Center, Effect = new DropShadowEffect { Color = cyan, BlurRadius = 40, ShadowDepth = 0 } };
        var ff = new DoubleAnimationUsingKeyFrames();
        ff.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(S(0))));
        ff.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(S(7.55))));
        ff.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(S(7.95))));
        flash.BeginAnimation(UIElement.OpacityProperty, ff);
        stage.Children.Add(flash);
        return 7.9;
    }

    // ---------- Intro personnalisée (dossier de l'utilisateur) ----------

    private static readonly string[] VideoExt = { ".mp4", ".wmv", ".mov", ".m4v", ".avi" };

    /// <summary>Élément de l'intro : image (+ son du même nom) ou vidéo (avec son propre son).</summary>
    private sealed record CustomItem(string File, bool IsVideo, string? Sound, double Seconds);

    /// <summary>
    /// Images et vidéos triées par nom. Image : son associé = même nom (01.png + 01.wav), durée = durée du son
    /// (0,8 à 6 s), sinon 1,4 s. Vidéo : jouée en entier (30 s max) avec son propre son.
    /// </summary>
    private static List<CustomItem> CustomItems(string folder)
    {
        var list = new List<CustomItem>();
        try
        {
            if (!Directory.Exists(folder)) return list;
            var files = Directory.GetFiles(folder);
            static string Ext(string f) => System.IO.Path.GetExtension(f).ToLowerInvariant();
            foreach (var file in files.Where(f => ImageExt.Contains(Ext(f)) || VideoExt.Contains(Ext(f)))
                                      .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (VideoExt.Contains(Ext(file))) { list.Add(new CustomItem(file, true, null, 30)); continue; }
                var stem = System.IO.Path.GetFileNameWithoutExtension(file);
                var sound = files.FirstOrDefault(f => SoundExt.Contains(Ext(f))
                                                      && System.IO.Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.OrdinalIgnoreCase));
                var secs = 1.4;
                if (sound != null)
                {
                    try { using var r = new AudioFileReader(sound); secs = Math.Clamp(r.TotalTime.TotalSeconds, 0.8, 6); }
                    catch (Exception ex) { Log.WarnOnce("intro-snd-" + sound, "Intro : son illisible", ex); sound = null; }
                }
                list.Add(new CustomItem(file, false, sound, secs));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Intro : dossier personnalisé illisible", ex);
        }
        return list;
    }

    /// <summary>Joue les éléments l'un après l'autre ; chacun arrive pendant le fondu du précédent.</summary>
    private static async Task PlayCustomAsync(Grid stage, List<CustomItem> items, List<IDisposable> sounds, string? deviceId, CancellationToken ct)
    {
        stage.Children.Add(new Rectangle { Fill = Brushes.Black });
        await Task.Delay(300, ct);
        FrameworkElement? previous = null;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            FrameworkElement? shown = item.IsVideo
                ? await ShowVideoAsync(stage, item, ct)
                : ShowImage(stage, item, sounds, deviceId);
            if (shown == null) continue;
            if (previous != null) FadeOutAndRemove(stage, previous);
            previous = shown;
            if (!item.IsVideo) await Task.Delay(S(item.Seconds), ct);
            else await WaitVideoEndAsync((MediaElement)((Grid)shown).Children[0], item.Seconds, ct);
        }
        if (previous != null) FadeOutAndRemove(stage, previous);
        await Task.Delay(450, ct);
    }

    private static FrameworkElement? ShowImage(Grid stage, CustomItem item, List<IDisposable> sounds, string? deviceId)
    {
        var bmp = Load(item.File);
        if (bmp == null) return null;
        var img = new WpfImage { Source = bmp, Stretch = Stretch.Uniform, MaxWidth = 1100, MaxHeight = 640 };
        var box = new Grid { Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        box.Children.Add(img);
        var scale = new ScaleTransform();
        box.RenderTransform = scale;
        box.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, 0, 0.3));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.9, 1.06, 0, item.Seconds + 0.4));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.9, 1.06, 0, item.Seconds + 0.4));
        stage.Children.Add(box);
        if (item.Sound != null) PlaySound(item.Sound, sounds, deviceId);
        return box;
    }

    /// <summary>Vidéo plein cadre ; son joué par Windows sur la sortie par défaut (la TV, gardée par le mode console).</summary>
    private static async Task<FrameworkElement?> ShowVideoAsync(Grid stage, CustomItem item, CancellationToken ct)
    {
        var media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform,
            Source = new Uri(item.File), Volume = 1,
        };
        var box = new Grid { Opacity = 0, Background = Brushes.Black };
        box.Children.Add(media);
        var opened = new TaskCompletionSource<bool>();
        media.MediaOpened += (_, _) => opened.TrySetResult(true);
        media.MediaFailed += (_, e) => { Log.WarnOnce("intro-video-" + item.File, "Intro : vidéo illisible", e.ErrorException); opened.TrySetResult(false); };
        stage.Children.Add(box);
        media.Play();
        var done = await Task.WhenAny(opened.Task, Task.Delay(4000, ct));
        if (done != opened.Task || !opened.Task.Result)
        {
            media.Close();
            stage.Children.Remove(box);
            return null;
        }
        box.BeginAnimation(UIElement.OpacityProperty, Anim(0, 1, 0, 0.35));
        return box;
    }

    private static async Task WaitVideoEndAsync(MediaElement media, double maxSeconds, CancellationToken ct)
    {
        var ended = new TaskCompletionSource<bool>();
        media.MediaEnded += (_, _) => ended.TrySetResult(true);
        var length = media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan.TotalSeconds : maxSeconds;
        var wait = Math.Min(maxSeconds, length);
        // On enchaîne 0,35 s avant la fin pour un fondu croisé avec l'élément suivant.
        await Task.WhenAny(ended.Task, Task.Delay(S(Math.Max(0.5, wait - 0.35)), ct));
        ct.ThrowIfCancellationRequested();
    }

    private static void FadeOutAndRemove(Grid stage, FrameworkElement el)
    {
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
        a.Completed += (_, _) =>
        {
            if (el is Grid g && g.Children.Count > 0 && g.Children[0] is MediaElement m) m.Close();
            stage.Children.Remove(el);
        };
        el.BeginAnimation(UIElement.OpacityProperty, a);
    }

    private static void PlaySound(string file, List<IDisposable> sounds, string? deviceId)
    {
        try
        {
            var reader = new AudioFileReader(file);
            var output = IntroSound.CreateOutput(deviceId);
            output.Init(reader);
            output.Play();
            lock (sounds) sounds.Add(new Disposer(() => { try { output.Stop(); } catch { } output.Dispose(); reader.Dispose(); }));
        }
        catch (Exception ex) { Log.WarnOnce("intro-play-" + file, "Intro : lecture impossible", ex); }
    }

    private sealed class Disposer(Action a) : IDisposable { public void Dispose() => a(); }

    private static BitmapImage? Load(string path)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception ex)
        {
            Log.WarnOnce("intro-img-" + path, "Intro : image illisible", ex);
            return null;
        }
    }
}
