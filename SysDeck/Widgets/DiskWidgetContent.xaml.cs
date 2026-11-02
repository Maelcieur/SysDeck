using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SysDeck.Controls;
using SysDeck.Services;

namespace SysDeck.Widgets;

public partial class DiskWidgetContent : UserControl
{
    // Les lecteurs sont mis à jour sur place plutôt que remplacés à chaque rafraîchissement : sinon
    // chaque jauge serait recréée et rejouerait son animation de remplissage depuis zéro.
    private readonly ObservableCollection<DriveRow> _rows = new();

    public DiskWidgetContent()
    {
        InitializeComponent();
        DrivesList.ItemsSource = _rows;
    }

    /// <summary>Forme des jauges : "Ring" (anneaux côte à côte) ou "Bar" (lignes horizontales empilées).</summary>
    private bool? _isBar;

    public void SetGaugeStyle(string? style)
    {
        var bar = style == "Bar";
        // Appelé à chaque changement d'apparence du module : on ne régénère les jauges (et ne rejoue
        // leur animation) que si la forme change vraiment.
        if (_isBar == bar) return;
        _isBar = bar;
        DrivesList.ItemsPanel = (ItemsPanelTemplate)Resources[bar ? "BarPanel" : "RingPanel"];
        DrivesList.ItemTemplate = (DataTemplate)Resources[bar ? "BarTemplate" : "RingTemplate"];

        // Les éléments déplaçables (un par disque) sont recréés : la fenêtre doit les rechercher à nouveau.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            () => RaiseEvent(new RoutedEventArgs(Movable.ElementsChangedEvent)));
    }

    public void Update(IReadOnlyList<DriveUsage> drives)
    {
        if (!_rows.Select(r => r.Name).SequenceEqual(drives.Select(d => d.Name)))
        {
            _rows.Clear();
            foreach (var d in drives)
                _rows.Add(new DriveRow(d.Name));

            // Chaque disque est un élément déplaçable : la fenêtre doit les rechercher une fois générés.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                () => RaiseEvent(new RoutedEventArgs(Movable.ElementsChangedEvent)));
        }

        for (var i = 0; i < drives.Count; i++)
            _rows[i].Set(drives[i]);
    }

    public sealed class DriveRow : INotifyPropertyChanged
    {
        public DriveRow(string name) => Name = name;

        public string Name { get; }
        public string Key => $"drive:{Name}";
        public float UsedGb { get; private set; }
        public float TotalGb { get; private set; }
        public float LoadPercent { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Set(DriveUsage usage)
        {
            // Appelé toutes les ~1,5 s : on ne notifie que ce qui a changé, pour ne pas faire
            // réévaluer toutes les liaisons de la ligne (jauge comprise) quand le disque est au repos.
            if (UsedGb != usage.UsedGb) { UsedGb = usage.UsedGb; Raise(nameof(UsedGb)); }
            if (TotalGb != usage.TotalGb) { TotalGb = usage.TotalGb; Raise(nameof(TotalGb)); }
            if (LoadPercent != usage.LoadPercent) { LoadPercent = usage.LoadPercent; Raise(nameof(LoadPercent)); }
        }

        private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
