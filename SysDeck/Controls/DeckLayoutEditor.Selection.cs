using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysDeck.Deck;
using SysDeck.Models;

namespace SysDeck.Controls;

/// <summary>
/// Tuile sélectionnée dans l'aperçu (configurée dans l'inspecteur des Paramètres) et dépôt d'une action de la
/// bibliothèque sur une case de la grille, comme dans le logiciel du Stream Deck.
/// </summary>
public sealed partial class DeckLayoutEditor
{
    /// <summary>Format des données glissées depuis la bibliothèque d'actions.</summary>
    public const string ActionDataFormat = "SysDeckTileAction";

    private ShortcutItem? _selectedTile;
    private readonly Border _selectMark = new()
    {
        IsHitTestVisible = false,
        BorderThickness = new Thickness(5),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF)),
        Visibility = Visibility.Collapsed
    };

    /// <summary>Tuile cliquée dans l'aperçu ; null quand on clique ailleurs.</summary>
    public event Action<ShortcutItem?>? TileSelected;

    /// <summary>Double-clic sur une tuile : ouvrir sa configuration.</summary>
    public event Action<ShortcutItem>? TileActivated;

    /// <summary>Action de la bibliothèque déposée : données glissées et case visée (null = première case libre).</summary>
    public event Action<object, (int Col, int Row)?>? ActionDropped;

    /// <summary>Tuile entourée dans l'aperçu.</summary>
    public ShortcutItem? SelectedTile
    {
        get => _selectedTile;
        set
        {
            _selectedTile = value;
            SyncTileSelection();
        }
    }

    private void SelectTile(ShortcutItem? item)
    {
        if (ReferenceEquals(item, _selectedTile)) return;
        SelectedTile = item;
        TileSelected?.Invoke(item);
    }

    /// <summary>Recale le cadre de sélection sur la case de la tuile (déplacée, page changée, bloc redimensionné).</summary>
    private void SyncTileSelection()
    {
        if (_deck is null || _selectedTile is null || _deck.Settings.Find("apps") is not { Visible: true } element)
        {
            _selectMark.Visibility = Visibility.Collapsed;
            return;
        }
        var (columns, rows) = _deck.AppsGrid();
        if (!TileGrid.Arrange(_deck.CurrentAppsSubpage(), columns, rows).Cells.TryGetValue(_selectedTile, out var cell))
        {
            _selectMark.Visibility = Visibility.Collapsed;
            return;
        }
        var layout = GridLayout(element);
        const double outset = 4;
        if (!_overlay.Children.Contains(_selectMark)) _overlay.Children.Add(_selectMark);
        _selectMark.Width = TileSizeNow + 2 * outset;
        _selectMark.Height = TileSizeNow + 2 * outset;
        _selectMark.CornerRadius = new CornerRadius(TileSizeNow * 0.2);
        Canvas.SetLeft(_selectMark, element.X + layout.Left(cell.Col) + TileGrid.TileMargin - outset);
        Canvas.SetTop(_selectMark, element.Y + layout.Top(cell.Row) + TileGrid.TileMargin - outset);
        Panel.SetZIndex(_selectMark, 900);
        _selectMark.Visibility = Visibility.Visible;
    }

    // ---------- Dépôt d'une action ----------

    private void InitActionDrop()
    {
        _overlay.AllowDrop = true;
        _overlay.DragOver += Overlay_DragOver;
        _overlay.DragLeave += (_, _) => HideDropMark();
        _overlay.Drop += Overlay_Drop;
    }

    /// <summary>Case de la grille sous le point (repère de l'aperçu) ; null hors de la grille ou sans bloc d'applications.</summary>
    private (int Col, int Row)? CellAt(Point point)
    {
        if (_deck?.Settings.Find("apps") is not { Visible: true } element) return null;
        var layout = GridLayout(element);
        var col = layout.ColAt(point.X - element.X);
        var row = layout.RowAt(point.Y - element.Y);
        var (columns, rows) = _deck.AppsGrid();
        return col < 0 || row < 0 || col >= columns || row >= rows ? null : (col, row);
    }

    private void Overlay_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ActionDataFormat)) return; // fichiers : gérés par le conteneur
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        if (CellAt(e.GetPosition(_overlay)) is { } cell && _deck?.Settings.Find("apps") is { } element)
        {
            var size = TileGrid.CellSize(TileSizeNow);
            if (!_overlay.Children.Contains(_dropMark)) _overlay.Children.Add(_dropMark);
            _dropMark.Width = size - 2 * TileGrid.TileMargin;
            _dropMark.Height = size - 2 * TileGrid.TileMargin;
            _dropMark.CornerRadius = new CornerRadius(TileSizeNow * 0.16);
            var layout = GridLayout(element);
            Canvas.SetLeft(_dropMark, element.X + layout.Left(cell.Col) + TileGrid.TileMargin);
            Canvas.SetTop(_dropMark, element.Y + layout.Top(cell.Row) + TileGrid.TileMargin);
            Panel.SetZIndex(_dropMark, 1000);
            _dropMark.Visibility = Visibility.Visible;
        }
        else HideDropMark();
    }

    private void Overlay_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(ActionDataFormat) is not { } action) return;
        e.Handled = true;
        var cell = CellAt(e.GetPosition(_overlay));
        HideDropMark();
        // Après le glisser-déposer : la fenêtre de création éventuelle ne doit pas s'ouvrir pendant l'opération OLE.
        Dispatcher.BeginInvoke(() => ActionDropped?.Invoke(action, cell));
    }

    private void HideDropMark()
    {
        _dropMark.Visibility = Visibility.Collapsed;
        _overlay.Children.Remove(_dropMark);
    }
}
