using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using SysDeck.Infrastructure;

namespace SysDeck.Controls;

/// <summary>Pastille de couleur cliquable : ouvre le sélecteur de couleur Windows et écrit la couleur en hexadécimal.</summary>
public sealed class ColorSwatch : System.Windows.Controls.Primitives.ButtonBase
{
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex), typeof(string), typeof(ColorSwatch),
        new FrameworkPropertyMetadata("#FFFFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string Hex { get => (string)GetValue(HexProperty); set => SetValue(HexProperty, value); }

    protected override void OnClick()
    {
        base.OnClick();

        var current = Services.ThemeService.Parse(Hex, Colors.White);
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            // Le sélecteur Windows oublie ses « couleurs personnalisées » à la fermeture : elles sont gardées ici.
            CustomColors = CustomColors.Load()
        };
        var ok = dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK;
        CustomColors.Save(dialog.CustomColors);
        if (ok)
            // Le sélecteur WinForms ignore la transparence : on garde l'alpha d'origine pour qu'une
            // couleur translucide ne devienne pas opaque.
            Hex = Color.FromArgb(current.A, dialog.Color.R, dialog.Color.G, dialog.Color.B).ToString();
    }

    /// <summary>Les 16 couleurs personnalisées du sélecteur (format Windows 0x00BBGGRR), dans custom-colors.json.</summary>
    internal static class CustomColors
    {
        private static int[]? _cache;
        private static string FilePath => Path.Combine(AppPaths.DataDir, "custom-colors.json");

        public static int[] Load()
        {
            if (_cache is not null) return (int[])_cache.Clone();
            try
            {
                if (File.Exists(FilePath) && JsonSerializer.Deserialize<int[]>(File.ReadAllText(FilePath)) is { } saved)
                    _cache = saved;
            }
            catch (Exception ex)
            {
                Log.Warn($"Couleurs personnalisées illisibles ({ex.Message})");
            }
            _cache ??= Enumerable.Repeat(0xFFFFFF, 16).ToArray(); // cases blanches, comme Windows
            return (int[])_cache.Clone();
        }

        public static void Save(int[] colors)
        {
            if (_cache is not null && colors.SequenceEqual(_cache)) return;
            _cache = (int[])colors.Clone();
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_cache));
            }
            catch (Exception ex)
            {
                Log.Warn($"Couleurs personnalisées non enregistrées ({ex.Message})");
            }
        }
    }
}
