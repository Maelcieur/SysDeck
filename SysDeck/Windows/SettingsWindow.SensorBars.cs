using System.Windows;
using System.Windows.Controls;
using SysDeck.Controls;
using SysDeck.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SysDeck;

/// <summary>Personnalisation du module Barres de capteurs : liste des données (capteur, nom, maximum, couleur).</summary>
public partial class SettingsWindow
{
    private SensorBarsSettings SensorBars => _widgetManager.Config.SensorBars;

    private void BuildSensorBarsEditor()
    {
        SensorBarsItems.Children.Clear();
        // Mesures connues (dernière lecture) ; un capteur choisi mais absent reste proposé sous son identifiant.
        var available = _widgetManager.AvailableSensors.OrderBy(r => r.Id.StartsWith("sysdeck/") ? 0 : 1).ThenBy(r => r.Group).ThenBy(r => r.Name).ToList();
        if (available.All(r => r.Id.StartsWith("sysdeck/")))
            SensorBarsItems.Children.Add(new TextBlock
            {
                Text = "Affiche le module (bureau ou Switch) pour que tous les capteurs (ventilateurs, tensions, fréquences…) apparaissent dans la liste.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("Muted"), Margin = new Thickness(0, 4, 0, 8)
            });

        foreach (var item in SensorBars.Items.ToList())
        {
            var sensor = new ComboBox { Width = 300, Height = 30, VerticalContentAlignment = VerticalAlignment.Center, IsTextSearchEnabled = true };
            foreach (var r in available) sensor.Items.Add(new ComboBoxItem { Content = $"{r.Label} ({SensorCatalog.Format(r.Value, r.Unit)})", Tag = r.Id });
            if (sensor.Items.Cast<ComboBoxItem>().FirstOrDefault(c => (string)c.Tag == item.SensorId) is { } selected) sensor.SelectedItem = selected;
            else if (item.SensorId.Length > 0)
            {
                var missing = new ComboBoxItem { Content = $"{item.SensorId} (absent pour l'instant)", Tag = item.SensorId };
                sensor.Items.Insert(0, missing);
                sensor.SelectedItem = missing;
            }
            sensor.SelectionChanged += (_, _) =>
            {
                if (sensor.SelectedItem is ComboBoxItem { Tag: string id }) item.SensorId = id;
                SensorBars.ItemsChanged();
            };

            var label = new TextBox { Width = 90, Height = 30, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, Text = item.Label, MaxLength = 16, ToolTip = "Nom affiché au-dessus de la barre" };
            label.LostFocus += (_, _) => { item.Label = label.Text.Trim(); SensorBars.ItemsChanged(); };

            var max = new TextBox
            {
                Width = 64, Height = 30, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center,
                Text = item.Max > 0 ? item.Max.ToString("0.##") : "", ToolTip = "Valeur qui remplit la barre (vide : 100 pour % et °C, 3000 tr/min…)"
            };
            max.LostFocus += (_, _) =>
            {
                item.Max = double.TryParse(max.Text.Replace('.', ','), out var m) && m > 0 ? m : 0;
                SensorBars.ItemsChanged();
            };

            var color = new ColorSwatch { Style = (Style)FindResource("ColorDot"), Hex = item.Color, Margin = new Thickness(10, 0, 0, 0), ToolTip = "Couleur de la barre" };
            var hexDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ColorSwatch.HexProperty, typeof(ColorSwatch));
            hexDescriptor.AddValueChanged(color, (_, _) => { item.Color = color.Hex; SensorBars.ItemsChanged(); });

            var up = SmallButton("", "Monter");
            up.Click += (_, _) => MoveSensorBar(item, -1);
            var remove = SmallButton("", "Retirer");
            remove.Click += (_, _) =>
            {
                SensorBars.Items.Remove(item);
                SensorBars.ItemsChanged();
                BuildSensorBarsEditor();
            };

            SensorBarsItems.Children.Add(new Border
            {
                Style = (Style)FindResource("RowBorder"),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { sensor, label, max, color, up, remove }
                }
            });
        }
    }

    private static Button SmallButton(string glyph, string tip) => new()
    {
        Content = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        Margin = new Thickness(8, 0, 0, 0), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center
    };

    private void MoveSensorBar(SensorBarItem item, int offset)
    {
        var list = SensorBars.Items;
        var from = list.IndexOf(item);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= list.Count) return;
        (list[from], list[to]) = (list[to], list[from]);
        SensorBars.ItemsChanged();
        BuildSensorBarsEditor();
    }

    private void SensorBarAdd_Click(object sender, RoutedEventArgs e)
    {
        string[] palette = { "#FF00E0FF", "#FF3DDC84", "#FFFFB238", "#FFB06BFF", "#FFFF5A8C", "#FF4FA8FF" };
        var used = SensorBars.Items.Select(i => i.SensorId).ToHashSet();
        var next = _widgetManager.AvailableSensors.FirstOrDefault(r => !used.Contains(r.Id));
        SensorBars.Items.Add(new SensorBarItem
        {
            SensorId = next?.Id ?? SensorCatalog.CpuLoad,
            Color = palette[SensorBars.Items.Count % palette.Length]
        });
        SensorBars.ItemsChanged();
        BuildSensorBarsEditor();
    }
}
