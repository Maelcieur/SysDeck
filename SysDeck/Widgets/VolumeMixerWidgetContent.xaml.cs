using System.Windows;
using System.Windows.Controls;
using SysDeck.Controls;
using SysDeck.Services;

namespace SysDeck.Widgets;

public partial class VolumeMixerWidgetContent : UserControl
{
    public event Action<int, int>? VolumeChangeRequested; // processId, pourcentage
    public event Action<AudioSessionInfo>? MuteToggleRequested;
    public event Action<AudioSessionInfo>? HideRequested;
    public event Action<AudioSessionInfo>? ChangeIconRequested;

    /// <summary>Forme des réglages de volume : "Vertical", "Horizontal", "Circle", "Half" ou "Segments".</summary>
    public static readonly DependencyProperty VolumeShapeProperty = DependencyProperty.Register(
        nameof(VolumeShape), typeof(string), typeof(VolumeMixerWidgetContent), new PropertyMetadata("Vertical"));

    public string VolumeShape { get => (string)GetValue(VolumeShapeProperty); set => SetValue(VolumeShapeProperty, value); }

    private bool _isDragging;

    public VolumeMixerWidgetContent()
    {
        InitializeComponent();
        // Contrôle retiré en plein glisser : DragCompleted ne viendrait jamais, ne pas bloquer le rafraîchissement.
        Unloaded += (_, _) => _isDragging = false;
    }

    /// <param name="force">Vrai après une action de l'utilisateur (muet…) : son résultat doit s'afficher tout de suite.</param>
    public void Update(IReadOnlyList<AudioSessionInfo> sessions, bool force = false)
    {
        // Ne remplace pas la liste pendant que l'utilisateur fait glisser un réglage (il « sauterait »)
        // ni pendant qu'il a la souris dessus : un clic sur muet ou ✕ tomberait sur un bouton en train
        // d'être recréé et serait perdu. Le rafraîchissement reprend dès que la souris s'éloigne.
        if (_isDragging || (IsMouseOver && !force)) return;

        SessionsList.ItemsSource = sessions;
    }

    private void Volume_DragStarted(object? sender, EventArgs e) => _isDragging = true;
    private void Volume_DragCompleted(object? sender, EventArgs e) => _isDragging = false;

    private void Volume_UserValueChanged(object? sender, EventArgs e)
    {
        if (sender is VolumeControl { Tag: AudioSessionInfo info } control)
            VolumeChangeRequested?.Invoke(info.ProcessId, (int)control.Value);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudioSessionInfo info })
            MuteToggleRequested?.Invoke(info);
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudioSessionInfo info })
            HideRequested?.Invoke(info);
    }

    /// <summary>Menu clic droit d'une application : choisir son icône.</summary>
    private void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: AudioSessionInfo info })
            ChangeIconRequested?.Invoke(info);
    }
}
