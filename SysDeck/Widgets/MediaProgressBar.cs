using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SysDeck.Controls;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck.Widgets;

/// <summary>
/// Barre de temps du lecteur : temps écoulé à gauche, durée à droite, et barre « Line » (fine), « Thick » (épaisse,
/// avec curseur rond) ou « Segments ». Un clic sur la barre demande d'aller à cet endroit du morceau.
/// </summary>
public sealed class MediaProgressBar : StackPanel
{
    private const int SegmentCount = 32;

    private readonly Grid _bar = new() { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
    private readonly ColumnDefinition _done = new() { Width = new GridLength(0, GridUnitType.Star) };
    private readonly ColumnDefinition _left = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly TextBlock _elapsed, _total;
    private string _style = "Line";
    private double _ratio;
    private TimeSpan _duration;

    /// <summary>Clic sur la barre : fraction du morceau visée (0 à 1).</summary>
    public event Action<double>? SeekRequested;

    public MediaProgressBar()
    {
        _elapsed = TimeText();
        _total = TimeText();
        _total.HorizontalAlignment = HorizontalAlignment.Right;
        var times = new Grid { Margin = new Thickness(0, 5, 0, 0), Children = { _elapsed, _total } };
        Children.Add(_bar);
        Children.Add(times);
        _bar.MouseLeftButtonUp += (_, e) =>
        {
            if (_bar.ActualWidth <= 0 || _duration <= TimeSpan.Zero) return;
            e.Handled = true;
            SeekRequested?.Invoke(Math.Clamp(e.GetPosition(_bar).X / _bar.ActualWidth, 0, 1));
        };
        SetStyle("Line");
    }

    private static TextBlock TimeText()
    {
        var t = WidgetUi.Text("time", 10.5, sub: true, number: true);
        t.Text = "0:00";
        return t;
    }

    public void SetStyle(string style)
    {
        _style = style;
        Build();
    }

    public void SetProgress(TimeSpan position, TimeSpan duration)
    {
        _duration = duration;
        _elapsed.Text = Format(position);
        _total.Text = duration > TimeSpan.Zero ? Format(duration) : "--:--";
        var ratio = duration > TimeSpan.Zero ? Math.Clamp(position / duration, 0, 1) : 0;
        if (Math.Abs(ratio - _ratio) < 0.0005 && _bar.Children.Count > 0) return;
        _ratio = ratio;
        Fill();
    }

    public static string Format(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private void Build()
    {
        _bar.Children.Clear();
        _bar.ColumnDefinitions.Clear();
        _bar.Height = _style switch { "Thick" => 16, "Segments" => 10, _ => 10 };
        if (_style != "Segments")
        {
            _bar.ColumnDefinitions.Add(_done);
            _bar.ColumnDefinitions.Add(_left);
        }
        Fill();
    }

    private void Fill()
    {
        _bar.Children.Clear();
        if (_style == "Segments")
        {
            var grid = new UniformGrid { Rows = 1, Columns = SegmentCount, VerticalAlignment = VerticalAlignment.Center };
            var lit = (int)Math.Round(_ratio * SegmentCount);
            for (var i = 0; i < SegmentCount; i++)
            {
                var seg = new Border { Height = 7, Margin = new Thickness(i == 0 ? 0 : 1.5, 0, 0, 0), CornerRadius = new CornerRadius(2) };
                seg.SetResourceReference(Border.BackgroundProperty, i < lit ? "GaugeGradientBrush" : "TileStrongBrush");
                grid.Children.Add(seg);
            }
            _bar.Children.Add(grid);
            return;
        }

        var thick = _style == "Thick";
        var height = thick ? 6 : 3;
        _done.Width = new GridLength(Math.Max(_ratio, 0.0001), GridUnitType.Star);
        _left.Width = new GridLength(Math.Max(1 - _ratio, 0.0001), GridUnitType.Star);

        var track = new Border { Height = height, CornerRadius = new CornerRadius(height / 2.0), VerticalAlignment = VerticalAlignment.Center };
        track.SetResourceReference(Border.BackgroundProperty, "TileStrongBrush");
        Grid.SetColumnSpan(track, 2);
        var done = new Border { Height = height, CornerRadius = new CornerRadius(height / 2.0), VerticalAlignment = VerticalAlignment.Center };
        done.SetResourceReference(Border.BackgroundProperty, "GaugeGradientBrush");
        _bar.Children.Add(track);
        _bar.Children.Add(done);
        if (thick)
        {
            // Curseur rond au bout de la partie écoulée.
            var knob = new Ellipse { Width = 14, Height = 14, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -7, 0) };
            knob.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.5 };
            _bar.Children.Add(knob);
        }
    }
}
