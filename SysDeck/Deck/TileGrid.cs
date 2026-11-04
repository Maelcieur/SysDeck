using SysDeck.Models;

namespace SysDeck.Deck;

/// <summary>Résultat du placement des tuiles dans la grille : cases occupées et tuiles sans case.</summary>
public sealed class TileArrangement
{
    public TileArrangement(int columns, int rows, Dictionary<ShortcutItem, (int Col, int Row)> cells, List<ShortcutItem> hidden)
    {
        Columns = columns;
        Rows = rows;
        Cells = cells;
        Hidden = hidden;
    }

    public int Columns { get; }
    public int Rows { get; }

    /// <summary>Case de chaque tuile affichée.</summary>
    public Dictionary<ShortcutItem, (int Col, int Row)> Cells { get; }

    /// <summary>Tuiles qui n'ont plus de case (grille trop petite) : non affichées, mais conservées.</summary>
    public List<ShortcutItem> Hidden { get; }

    public ShortcutItem? At(int col, int row)
    {
        foreach (var (item, cell) in Cells)
            if (cell.Col == col && cell.Row == row) return item;
        return null;
    }
}

/// <summary>
/// Grille des tuiles d'applications de la Switch (logique pure, sans WPF) : nombre de colonnes et de lignes
/// selon la taille du bloc, placement, échange, première case libre.
/// </summary>
public static class TileGrid
{
    public const int MaxColumns = 8;
    public const int MaxRows = 4;

    /// <summary>Marge de chaque côté d'une tuile (espacement entre tuiles = 2 x cette valeur).</summary>
    public const double TileMargin = 6;

    /// <summary>Côté d'une case : tuile + espacement.</summary>
    public static double CellSize(double tileSize) => tileSize + 2 * TileMargin;

    public static int Columns(double width, double tileSize)
        => Math.Clamp((int)Math.Floor(width / CellSize(tileSize) + 1e-6), 1, MaxColumns);

    public static int Rows(double height, double tileSize)
        => Math.Clamp((int)Math.Floor(height / CellSize(tileSize) + 1e-6), 1, MaxRows);

    /// <summary>
    /// Place les tuiles : celles dont la case enregistrée est dans la grille et libre la gardent ; les autres
    /// (sans position, hors grille ou en conflit) prennent la première case libre, dans l'ordre de la liste.
    /// Sans case libre, la tuile est « hors grille ». Ne modifie pas les tuiles.
    /// </summary>
    public static TileArrangement Arrange(IReadOnlyList<ShortcutItem> items, int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        var taken = new bool[columns, rows];
        var cells = new Dictionary<ShortcutItem, (int, int)>();
        var pending = new List<ShortcutItem>();

        foreach (var item in items)
        {
            if (item.GridCol is { } c && item.GridRow is { } r && c >= 0 && c < columns && r >= 0 && r < rows && !taken[c, r])
            {
                taken[c, r] = true;
                cells[item] = (c, r);
            }
            else pending.Add(item);
        }

        var hidden = new List<ShortcutItem>();
        foreach (var item in pending)
        {
            if (FindFree(taken, columns, rows) is { } cell)
            {
                taken[cell.Col, cell.Row] = true;
                cells[item] = cell;
            }
            else hidden.Add(item);
        }
        return new TileArrangement(columns, rows, cells, hidden);
    }

    /// <summary>Tuile en haut à gauche (ordre de lecture) ; null si la page n'en a aucune.</summary>
    public static ShortcutItem? First(IReadOnlyDictionary<ShortcutItem, (int Col, int Row)> cells)
        => cells.OrderBy(c => c.Value.Row).ThenBy(c => c.Value.Col).Select(c => c.Key).FirstOrDefault();

    /// <summary>
    /// Tuile voisine dans une direction (curseur des Joy-Con) : la plus proche de ce côté, en préférant la même ligne
    /// ou colonne ; les cases vides sont sautées. Null s'il n'y a rien de ce côté.
    /// </summary>
    public static ShortcutItem? Neighbor(IReadOnlyDictionary<ShortcutItem, (int Col, int Row)> cells, ShortcutItem from, int dx, int dy)
    {
        if (!cells.TryGetValue(from, out var origin)) return null;
        ShortcutItem? best = null;
        var bestScore = int.MaxValue;
        foreach (var (item, cell) in cells)
        {
            var along = dx != 0 ? (cell.Col - origin.Col) * dx : (cell.Row - origin.Row) * dy;
            if (along <= 0) continue;
            var across = Math.Abs(dx != 0 ? cell.Row - origin.Row : cell.Col - origin.Col);
            var score = along + across * 3;
            if (score < bestScore)
            {
                bestScore = score;
                best = item;
            }
        }
        return best;
    }

    /// <summary>Première case libre en lisant de gauche à droite puis de haut en bas, null si la grille est pleine.</summary>
    public static (int Col, int Row)? FirstFree(IReadOnlyList<ShortcutItem> items, int columns, int rows)
    {
        var arrangement = Arrange(items, columns, rows);
        var taken = new bool[Math.Max(1, columns), Math.Max(1, rows)];
        foreach (var (_, cell) in arrangement.Cells) taken[cell.Col, cell.Row] = true;
        return FindFree(taken, taken.GetLength(0), taken.GetLength(1));
    }

    private static (int Col, int Row)? FindFree(bool[,] taken, int columns, int rows)
    {
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                if (!taken[c, r]) return (c, r);
        return null;
    }

    /// <summary>
    /// Enregistre dans les tuiles les cases calculées par <see cref="Arrange"/> (première modification d'une
    /// ancienne configuration, ou après une réduction). Une tuile hors grille perd sa position et en retrouvera une à l'agrandissement.
    /// </summary>
    public static TileArrangement Commit(IReadOnlyList<ShortcutItem> items, int columns, int rows)
    {
        var arrangement = Arrange(items, columns, rows);
        foreach (var (item, cell) in arrangement.Cells)
        {
            item.GridCol = cell.Col;
            item.GridRow = cell.Row;
        }
        foreach (var item in arrangement.Hidden)
        {
            item.GridCol = null;
            item.GridRow = null;
        }
        return arrangement;
    }

    /// <summary>
    /// Pose la tuile sur la case donnée ; si une autre tuile l'occupe, les deux s'échangent.
    /// Renvoie faux si la case est hors grille ou si la tuile n'est pas affichée.
    /// </summary>
    public static bool MoveOrSwap(IReadOnlyList<ShortcutItem> items, int columns, int rows, ShortcutItem item, int col, int row)
    {
        if (col < 0 || col >= columns || row < 0 || row >= rows) return false;
        var arrangement = Commit(items, columns, rows);
        if (!arrangement.Cells.TryGetValue(item, out var from)) return false;
        if (from == (col, row)) return true;
        if (arrangement.At(col, row) is { } other)
        {
            other.GridCol = from.Col;
            other.GridRow = from.Row;
        }
        item.GridCol = col;
        item.GridRow = row;
        return true;
    }

    /// <summary>
    /// Échange la tuile avec sa voisine dans l'ordre de lecture (delta -1 = précédente, 1 = suivante), cases vides
    /// comprises : une tuile peut ainsi avancer d'une case vide. Renvoie faux si impossible.
    /// </summary>
    public static bool Step(IReadOnlyList<ShortcutItem> items, int columns, int rows, ShortcutItem item, int delta)
    {
        var arrangement = Arrange(items, columns, rows);
        if (!arrangement.Cells.TryGetValue(item, out var cell)) return false;
        var index = cell.Row * columns + cell.Col + delta;
        if (index < 0 || index >= columns * rows) return false;
        return MoveOrSwap(items, columns, rows, item, index % columns, index / columns);
    }
}
