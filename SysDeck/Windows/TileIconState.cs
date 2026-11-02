using System.Windows;
using SysDeck.Controls;
using SysDeck.Infrastructure;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck;

/// <summary>
/// Icône et couleurs en cours de choix dans une fenêtre de création de tuile : aperçu, ouverture de
/// <see cref="IconChooserWindow"/>, puis copie de l'image choisie dans le dossier géré par SysDeck à la validation.
/// </summary>
public sealed class TileIconState
{
    public string? Glyph, ImagePath, IconColor, TileColor;
    private string? _existingImage; // déjà copiée par SysDeck (modification) : réutilisée telle quelle

    public void Load(ShortcutItem item)
    {
        Glyph = item.IconGlyph;
        ImagePath = _existingImage = item.CustomIconPath;
        IconColor = item.IconColor;
        TileColor = item.TileColor;
    }

    /// <summary>Tuile servant d'aperçu : <paramref name="defaultGlyph"/> tant que rien n'est choisi, sinon l'icône détectée du chemin.</summary>
    public ShortcutItem Preview(string? defaultGlyph, string? path = null) => new()
    {
        Path = path ?? string.Empty,
        IconGlyph = Glyph ?? (ImagePath is null ? defaultGlyph : null),
        CustomIconPath = ImagePath,
        IconColor = IconColor,
        TileColor = TileColor
    };

    /// <summary>Ouvre le sélecteur d'icône ; faux si annulé.</summary>
    public bool Choose(Window owner, string name, Func<EmojiPickerWindow> createEmojiPicker, string? defaultGlyph, string? path = null)
    {
        var chooser = new IconChooserWindow(owner, name, createEmojiPicker, Preview(defaultGlyph, path));
        if (chooser.ShowDialog() != true || chooser.Result is not { } choice) return false;

        IconColor = choice.IconColor;
        TileColor = choice.TileColor;
        switch (choice.Kind)
        {
            case IconChoiceKind.Keep: break;
            case IconChoiceKind.Image: ImagePath = choice.Value; Glyph = null; break;
            case IconChoiceKind.Glyph: Glyph = choice.Value; ImagePath = null; break;
            default: Glyph = ImagePath = null; break;
        }
        return true;
    }

    /// <summary>
    /// Copie l'image choisie et renseigne l'icône de <paramref name="item"/>. Faux (avec message) si la copie échoue.
    /// Sans choix, <paramref name="defaultGlyph"/> sert de symbole (null = icône détectée du chemin).
    /// </summary>
    public bool ApplyTo(ShortcutItem item, Window owner, string? defaultGlyph)
    {
        var imageCopy = ImagePath == _existingImage ? _existingImage : null;
        if (ImagePath is not null && imageCopy is null)
        {
            try
            {
                imageCopy = CustomIconStore.SaveCopy(ImagePath);
            }
            catch (Exception ex)
            {
                Log.Warn("Copie de l'icône impossible", ex);
                MessageBox.Show(owner, $"Impossible d'utiliser cette image :\n{ex.Message}", "SysDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        item.IconGlyph = Glyph ?? (imageCopy is null ? defaultGlyph : null);
        item.CustomIconPath = imageCopy;
        item.IconColor = IconColor;
        item.TileColor = TileColor;
        return true;
    }
}
