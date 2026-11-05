using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using SysDeck.Controls;
using SysDeck.Services;

namespace SysDeck;

// Personnaliser › onglet Boutons (style de chaque bouton), et sections « Périphériques » (Sortie audio) et
// « Applications » (Volume par application) de l'onglet Éléments.
public partial class SettingsWindow
{
    // ---------- Onglet Boutons ----------

    private sealed record ButtonRow(ButtonPartInfo Info, ButtonPartStyle Style)
    {
        public string Label => Info.Label;
        public bool HoverEnabled => Info.HasHover;
        public string HoverTip => Info.HasHover
            ? "Fond au survol (pointillés = couleur d'accent du thème)"
            : "Sans effet sur ce bouton : il s'éclaircit simplement au survol";
    }

    private readonly List<(ButtonPartStyle Style, PropertyChangedEventHandler Handler)> _buttonSubscriptions = new();

    /// <summary>Une ligne par bouton du module choisi ; chaque changement s'applique en direct (bureau ou Switch).</summary>
    private void BuildButtonRows()
    {
        foreach (var (style, handler) in _buttonSubscriptions)
            style.PropertyChanged -= handler;
        _buttonSubscriptions.Clear();

        if (SelectedModule is not { } module || !ButtonPart.ByModule.TryGetValue(module.Id, out var parts))
        {
            _buttonRows = new();
            ButtonPartsList.ItemsSource = null;
            return;
        }

        var appearance = CurrentAppearance!;
        var rows = new List<ButtonRow>();
        foreach (var part in parts)
        {
            var style = appearance.GetButton(part.Key);
            PropertyChangedEventHandler handler = (_, _) => OnModuleChanged(module.Id, appearance, nameof(ModuleAppearance.Buttons));
            style.PropertyChanged += handler;
            _buttonSubscriptions.Add((style, handler));
            rows.Add(new ButtonRow(part, style));
        }
        _buttonRows = rows;
        ButtonPartsList.ItemsSource = rows;
    }

    private void ButtonIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ButtonRow row }) return;
        var pick = IconPicker.Choose(this, row.Label, row.Style.Glyph, row.Style.Foreground, CreateEmojiPickerForChoice);
        if (pick is null) return;

        var previous = row.Style.Glyph;
        row.Style.Glyph = pick.Value;
        if (pick.Color is not null) row.Style.Foreground = pick.Color; // la couleur du symbole est celle du texte du bouton
        IconPicker.Release(previous, pick.Value);
    }

    private void ResetButtonRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ButtonRow row }) return;
        var previous = row.Style.Glyph;
        row.Style.Reset();
        IconPicker.Release(previous, null);
    }

    private void ResetButtons_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentAppearance is not { } appearance) return;
        ResetButtonsOf(appearance);
        BuildButtonRows();
    }

    /// <summary>Remet tous les boutons d'une apparence à leur rendu d'origine (et supprime les images d'icônes devenues inutiles).</summary>
    private static void ResetButtonsOf(ModuleAppearance appearance)
    {
        foreach (var style in appearance.Buttons.Values.ToList())
            IconPicker.Release(style.Glyph, null);
        appearance.ResetButtons();
    }

    // ---------- Sortie audio › Éléments › Périphériques ----------

    /// <summary>Nom court et icône d'une sortie audio ; chaque changement est appliqué tout de suite aux widgets.</summary>
    private sealed class AudioDeviceRow : INotifyPropertyChanged
    {
        private readonly Action<AudioDeviceRow> _changed;
        private string? _name;
        private string? _icon;
        private string? _iconColor;

        public AudioDeviceRow(string id, string originalName, bool connected, AudioDeviceCustom? custom, Action<AudioDeviceRow> changed)
        {
            Id = id;
            OriginalName = originalName;
            StatusText = connected ? "Connectée" : "Non connectée";
            _name = custom?.Name;
            _icon = custom?.Icon;
            _iconColor = custom?.IconColor;
            _changed = changed;
        }

        public string Id { get; }
        public string OriginalName { get; }
        public string StatusText { get; }

        public string? Name { get => _name; set { _name = value; Raise(nameof(Name)); _changed(this); } }
        public string? Icon => _icon;
        public string? IconColor => _iconColor;

        public void SetIcon(string? icon, string? color)
        {
            _icon = icon;
            _iconColor = color;
            Raise(nameof(Icon));
            Raise(nameof(IconColor));
            _changed(this);
        }

        public void Reset()
        {
            _name = null;
            _icon = null;
            _iconColor = null;
            Raise(nameof(Name));
            Raise(nameof(Icon));
            Raise(nameof(IconColor));
            _changed(this);
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void BuildAudioDeviceRows()
    {
        var shown = SelectedModule?.Id == "audio";
        AudioDevicesSection.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown)
        {
            AudioDevicesList.ItemsSource = null;
            return;
        }

        void Changed(AudioDeviceRow row) => _widgetManager.SetAudioDeviceCustom(row.Id, row.OriginalName, row.Name, row.Icon, row.IconColor);

        var saved = _widgetManager.Config.AudioDevices;
        var devices = _widgetManager.GetAudioDevices();
        var rows = devices
            .Select(d => new AudioDeviceRow(d.Id, d.Name, true, saved.GetValueOrDefault(d.Id), Changed))
            .ToList();
        // Périphériques déjà personnalisés mais débranchés : on peut encore modifier ou effacer leur réglage.
        rows.AddRange(saved
            .Where(kv => devices.All(d => d.Id != kv.Key))
            .Select(kv => new AudioDeviceRow(kv.Key, kv.Value.OriginalName ?? kv.Key, false, kv.Value, Changed)));

        AudioDevicesList.ItemsSource = rows;
        AudioDevicesEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AudioDeviceIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AudioDeviceRow row }) return;
        var pick = IconPicker.Choose(this, row.OriginalName, row.Icon, row.IconColor, CreateEmojiPickerForChoice);
        if (pick is not null) row.SetIcon(pick.Value, pick.Color);
    }

    private void AudioDeviceReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AudioDeviceRow row }) row.Reset();
    }

    // ---------- Volume par application › Éléments › Applications ----------

    private sealed class AppIconRow : INotifyPropertyChanged
    {
        private string? _icon;
        private string? _iconColor;

        public AppIconRow(string appKey, string displayName, ImageSource? autoIcon, AppIconCustom? custom)
        {
            AppKey = appKey;
            DisplayName = displayName;
            AutoIcon = autoIcon;
            _icon = custom?.Icon;
            _iconColor = custom?.IconColor;
        }

        public string AppKey { get; }
        public string DisplayName { get; }
        public ImageSource? AutoIcon { get; }
        public string? Icon => _icon;
        public string? IconColor => _iconColor;

        public bool ShowAuto => AutoIcon is not null && string.IsNullOrEmpty(_icon);
        public bool ShowFallback => AutoIcon is null && string.IsNullOrEmpty(_icon);
        public string StatusText => string.IsNullOrEmpty(_icon) ? "Logo automatique" : "Icône choisie";

        public void SetIcon(string? icon, string? color)
        {
            _icon = icon;
            _iconColor = color;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void BuildAppIconRows()
    {
        var shown = SelectedModule?.Id == "volumemixer";
        AppIconsSection.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown)
        {
            AppIconsList.ItemsSource = null;
            return;
        }

        var saved = _widgetManager.Config.AppIcons;
        var seen = _widgetManager.GetSeenApps();
        var rows = seen
            .Select(a => new AppIconRow(a.AppKey, a.DisplayName, a.Icon, saved.GetValueOrDefault(a.AppKey)))
            .ToList();
        // Applications déjà personnalisées mais pas vues depuis le lancement.
        rows.AddRange(saved
            .Where(kv => seen.All(a => a.AppKey != kv.Key))
            .Select(kv => new AppIconRow(kv.Key, kv.Value.DisplayName ?? kv.Key, null, kv.Value)));

        AppIconsList.ItemsSource = rows.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        AppIconsEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AppIconRow row }) return;
        var pick = IconPicker.Choose(this, row.DisplayName, row.Icon, row.IconColor, CreateEmojiPickerForChoice);
        if (pick is null) return;
        _widgetManager.SetAppIcon(row.AppKey, row.DisplayName, pick.Value, pick.Color);
        row.SetIcon(pick.Value, pick.Color);
    }

    private void AppIconReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AppIconRow row }) return;
        _widgetManager.SetAppIcon(row.AppKey, row.DisplayName, null, null);
        row.SetIcon(null, null);
    }
}
