using System.Windows;
using SysDeck.Controls;
using SysDeck.Infrastructure;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck;

/// <summary>
/// Ouvre <see cref="IconChooserWindow"/> pour un réglage à icône unique (bouton de widget, sortie audio, application
/// du mixeur) et traduit son résultat en une valeur : symbole Segoe, émoji ou chemin d'une copie de l'image
/// (dossier géré par SysDeck), avec sa couleur. Null = aucune icône (rendu d'origine).
/// </summary>
public static class IconPicker
{
    /// <param name="Value">Symbole, émoji ou chemin d'image ; null = icône d'origine.</param>
    /// <param name="Color">Couleur du symbole (hexadécimal) ; null = couleur du thème.</param>
    public sealed record Pick(string? Value, string? Color);

    /// <summary>Null si l'utilisateur annule (ou si l'image choisie ne peut pas être copiée).</summary>
    public static Pick? Choose(Window owner, string name, string? currentValue, string? currentColor,
        Func<EmojiPickerWindow> createEmojiPicker, bool centerOnScreen = false)
    {
        var current = new ShortcutItem
        {
            Name = name,
            IconGlyph = currentValue is not null && !ShortcutIcons.IsImagePath(currentValue) ? currentValue : null,
            CustomIconPath = currentValue is not null && ShortcutIcons.IsImagePath(currentValue) ? currentValue : null,
            IconColor = currentColor
        };
        var chooser = new IconChooserWindow(owner, name, createEmojiPicker, current);
        if (centerOnScreen) chooser.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (chooser.ShowDialog() != true || chooser.Result is not { } choice) return null;

        switch (choice.Kind)
        {
            case IconChoiceKind.Reset:
                return new Pick(null, null);
            case IconChoiceKind.Glyph:
                return new Pick(choice.Value, choice.IconColor);
            case IconChoiceKind.Image:
                try
                {
                    return new Pick(CustomIconStore.SaveCopy(choice.Value!), null);
                }
                catch (Exception ex)
                {
                    Log.Warn("Copie de l'icône impossible", ex);
                    MessageBox.Show(owner, $"Impossible d'utiliser cette image :\n{ex.Message}", "SysDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }
            default: // Keep : seule la couleur a pu changer
                return new Pick(currentValue, choice.IconColor);
        }
    }

    /// <summary>Supprime la copie d'image gérée par SysDeck qui n'est plus utilisée (jamais un fichier extérieur).</summary>
    public static void Release(string? previous, string? current)
    {
        if (previous is not null && previous != current && ShortcutIcons.IsImagePath(previous))
            CustomIconStore.TryDelete(previous);
    }
}
