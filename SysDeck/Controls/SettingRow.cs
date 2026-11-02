using System.Windows;
using System.Windows.Controls;

namespace SysDeck.Controls;

/// <summary>
/// Ligne de réglage des Paramètres : libellé (et description grise) à gauche, contrôle (Content) à droite.
/// Le gabarit est défini dans SettingsWindow.xaml. <see cref="Stacked"/> place le contrôle sous le libellé
/// (listes de puces, champs larges). Lu par l'index de recherche (voir SettingsSearch.CollectText).
/// </summary>
public class SettingRow : ContentControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty StackedProperty = DependencyProperty.Register(
        nameof(Stacked), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));

    public string? Label { get => (string?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    /// <summary>Vrai : le contrôle passe sous le libellé, sur toute la largeur.</summary>
    public bool Stacked { get => (bool)GetValue(StackedProperty); set => SetValue(StackedProperty, value); }
}
