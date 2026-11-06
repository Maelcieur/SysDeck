using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SysDeck.Deck;
using SysDeck.Models;

namespace SysDeck.Controls;

/// <summary>Grille visible sur le bloc d'applications et glisser-déposer des tuiles d'une case à l'autre.</summary>
public sealed partial class DeckLayoutEditor
{
    private const double TileDragThreshold = 6;

    private ShortcutItem? _tileDrag;
    private DeckElement? _tileElement;
    private Point _tileStart;
    private bool _tileMoved;
    private (int Col, int Row)? _tileTarget;
    private readonly Border _dropMark = new()
    {
        IsHitTestVisible = false,
        BorderThickness = new Thickness(3),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0xE0, 0xFF)),
        Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0xE0, 0xFF)),
        Visibility = Visibility.Collapsed
    };

    private double TileSizeNow => Math.Clamp(_deck?.Settings.TileSize ?? 120, 60, 300);

    /// <summary>Ajoute la grille visible sous le cadre du bloc d'applications.</summary>
    private void AddGridLines(DeckElement element)
    {
        _overlay.Children.Add(new TileGridLines(() => TileSizeNow, () => _deck?.AppsGrid()) { Tag = element });
    }

    /// <summary>Cases du bloc d'applications, calculées comme sur l'écran de la Switch.</summary>
    private TileLayout GridLayout(DeckElement element)
    {
        var (columns, rows) = _deck!.AppsGrid();
        return TileGrid.Layout(Math.Max(MinSize, element.Width), Math.Max(MinSize, element.Height), columns, rows, TileSizeNow);
    }

    /// <summary>Redessine la grille (taille des tuiles modifiée).</summary>
    public void RefreshGrid() => SyncGridLines();

    /// <summary>Recale les grilles sur le rectangle de leur bloc (déplacement, redimensionnement).</summary>
    private void SyncGridLines()
    {
        foreach (var lines in _overlay.Children.OfType<TileGridLines>())
        {
            if (lines.Tag is not DeckElement element) continue;
            Canvas.SetLeft(lines, element.X);
            Canvas.SetTop(lines, element.Y);
            lines.Width = Math.Max(MinSize, element.Width);
            lines.Height = Math.Max(MinSize, element.Height);
            lines.InvalidateVisual();
        }
        SyncTileSelection();
    }

    /// <summary>Début d'un glisser de tuile si le clic tombe sur une tuile du bloc d'applications.</summary>
    private bool TryBeginTileDrag(DeckElement element, Point overlayPoint)
    {
        if (_deck is null || element.Id != "apps") return false;
        var layout = GridLayout(element);
        var col = layout.ColAt(overlayPoint.X - element.X);
        var row = layout.RowAt(overlayPoint.Y - element.Y);
        var (columns, rows) = _deck.AppsGrid();
        if (col < 0 || row < 0 || col >= columns || row >= rows) return false;
        var item = TileGrid.Arrange(_deck.CurrentAppsSubpage(), columns, rows).At(col, row);
        if (item is null) return false;

        _tileDrag = item;
        _tileElement = element;
        _tileStart = overlayPoint;
        _tileMoved = false;
        _tileTarget = null;
        return true;
    }

    private void UpdateTileDrag(Point overlayPoint)
    {
        if (_tileDrag is null || _tileElement is null || _deck is null) return;
        if (!_tileMoved && (overlayPoint - _tileStart).Length < TileDragThreshold) return;
        _tileMoved = true;

        var cell = TileGrid.CellSize(TileSizeNow);
        var layout = GridLayout(_tileElement);
        var col = layout.ColAt(overlayPoint.X - _tileElement.X);
        var row = layout.RowAt(overlayPoint.Y - _tileElement.Y);
        var (columns, rows) = _deck.AppsGrid();
        if (col < 0 || row < 0 || col >= columns || row >= rows)
        {
            _tileTarget = null;
            _dropMark.Visibility = Visibility.Collapsed;
            return;
        }
        _tileTarget = (col, row);
        if (!_overlay.Children.Contains(_dropMark)) _overlay.Children.Add(_dropMark);
        _dropMark.Width = cell - 2 * TileGrid.TileMargin;
        _dropMark.Height = cell - 2 * TileGrid.TileMargin;
        _dropMark.CornerRadius = new CornerRadius(TileSizeNow * 0.16);
        Canvas.SetLeft(_dropMark, _tileElement.X + layout.Left(col) + TileGrid.TileMargin);
        Canvas.SetTop(_dropMark, _tileElement.Y + layout.Top(row) + TileGrid.TileMargin);
        Panel.SetZIndex(_dropMark, 1000);
        _dropMark.Visibility = Visibility.Visible;
    }

    private void EndTileDrag()
    {
        var item = _tileDrag;
        var target = _tileTarget;
        var moved = _tileMoved;
        _tileDrag = null;
        _tileElement = null;
        _tileTarget = null;
        _dropMark.Visibility = Visibility.Collapsed;
        _overlay.Children.Remove(_dropMark);
        if (_overlay.IsMouseCaptured) _overlay.ReleaseMouseCapture();
        if (item is not null) SelectTile(item); // clic simple ou tuile déplacée : elle devient la tuile configurée
        if (!moved || item is null || target is not { } cell || _deck is null) return;
        if (_deck.MoveAppToCell(item, cell.Col, cell.Row)) LayoutChanged?.Invoke();
        SyncTileSelection();
    }
}

/// <summary>Cases de la grille d'applications (pointillés discrets), dessinées d'après la taille du bloc.</summary>
internal sealed class TileGridLines : FrameworkElement
{
    private readonly Func<double> _tileSize;
    private static readonly Pen LinePen = CreatePen();

    private readonly Func<(int Columns, int Rows)?> _grid;

    /// <param name="grid">Colonnes et lignes réelles de la grille (colonnes choisies comprises) ; null = d'après la taille.</param>
    public TileGridLines(Func<double> tileSize, Func<(int Columns, int Rows)?> grid)
    {
        _tileSize = tileSize;
        _grid = grid;
        IsHitTestVisible = false;
    }

    private static Pen CreatePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), 2) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var tile = Math.Clamp(_tileSize(), 60, 300);
        var cell = TileGrid.CellSize(tile);
        var (columns, rows) = _grid() ?? (TileGrid.Columns(ActualWidth, tile), TileGrid.Rows(ActualHeight, tile));
        var layout = TileGrid.Layout(ActualWidth, ActualHeight, columns, rows, tile);
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                dc.DrawRoundedRectangle(null, LinePen,
                    new Rect(layout.Left(c) + TileGrid.TileMargin, layout.Top(r) + TileGrid.TileMargin, tile, tile), tile * 0.16, tile * 0.16);
    }
}
