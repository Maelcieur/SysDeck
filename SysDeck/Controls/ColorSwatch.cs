using System.Windows;
using System.Windows.Media;

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
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B)
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            // Le sélecteur WinForms ignore la transparence : on garde l'alpha d'origine pour qu'une
            // couleur translucide ne devienne pas opaque.
            Hex = Color.FromArgb(current.A, dialog.Color.R, dialog.Color.G, dialog.Color.B).ToString();
    }
}
