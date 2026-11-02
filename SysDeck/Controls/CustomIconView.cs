using System.Windows;
using System.Windows.Controls;

namespace SysDeck.Controls;

/// <summary>
/// Affiche une icône choisie par l'utilisateur (symbole Segoe, émoji ou image) ; disparaît (Collapsed)
/// quand <see cref="Icon"/> est vide ou introuvable. Sert aux sorties audio, aux logos du mixeur et aux aperçus des Paramètres.
/// </summary>
public sealed class CustomIconView : ContentControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(CustomIconView), new PropertyMetadata(null, (d, _) => ((CustomIconView)d).Rebuild()));

    public static readonly DependencyProperty IconColorProperty = DependencyProperty.Register(
        nameof(IconColor), typeof(string), typeof(CustomIconView), new PropertyMetadata(null, (d, _) => ((CustomIconView)d).Rebuild()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(CustomIconView), new PropertyMetadata(16.0, (d, _) => ((CustomIconView)d).Rebuild()));

    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? IconColor { get => (string?)GetValue(IconColorProperty); set => SetValue(IconColorProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public CustomIconView()
    {
        IsTabStop = false;
        Focusable = false;
        Visibility = Visibility.Collapsed;
    }

    private void Rebuild()
    {
        var icon = ShortcutIcons.CreateCustom(Icon, Size, IconColor);
        Content = icon;
        Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
