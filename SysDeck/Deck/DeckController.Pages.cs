using System.Windows;
using System.Windows.Media;
using SysDeck.Models;
using SysDeck.Services;

namespace SysDeck.Deck;

/// <summary>Disposition, pages et applications de la page de <see cref="DeckController"/>.</summary>
public sealed partial class DeckController
{
    // ---------- Disposition ----------

    private void AddDefaultElements()
    {
        _settings.Elements.AddRange(new[]
        {
            new DeckElement { Id = "clock", X = 20, Y = 20, Width = 400, Height = 200 },
            new DeckElement { Id = "media", X = 440, Y = 20, Width = 420, Height = 200 },
            new DeckElement { Id = "cpu", X = 880, Y = 20, Width = 380, Height = 200 },
            new DeckElement { Id = "gpu", X = 20, Y = 240, Width = 400, Height = 220 },
            new DeckElement { Id = "ram", X = 440, Y = 240, Width = 420, Height = 220 },
            new DeckElement { Id = "network", X = 880, Y = 240, Width = 380, Height = 220 },
            new DeckElement { Id = "shortcuts", X = 20, Y = 480, Width = 1240, Height = 220 },
        });
    }

    public bool IsShown(string id) => _settings.Find(id)?.Visible == true;

    /// <summary>Affiche ou retire un élément ; un nouvel élément est posé dans la première place libre.</summary>
    public void SetVisible(string id, bool visible)
    {
        var element = _settings.Find(id);
        if (element is null)
        {
            if (!visible) return;
            element = new DeckElement { Id = id };
            if (id is "shortcuts" or "apps") { element.Width = 1240; element.Height = 160; }
            (element.X, element.Y) = FindFreeSpot(element.Width, element.Height);
            _settings.Elements.Add(element);
        }
        element.Visible = visible;
        Sync();
    }

    /// <summary>Fait passer l'élément devant les autres.</summary>
    public void BringToFront(string id)
    {
        if (_settings.Find(id) is not { } element) return;
        _settings.Elements.Remove(element);
        _settings.Elements.Add(element);
        Sync();
    }

    public void Place(string id) => Screen.Place(id);

    // ---------- Pages ----------

    /// <summary>La page affichée a changé (glissement sur la Switch ou choix dans les Paramètres).</summary>
    public event Action? PageChanged;

    /// <summary>Affiche une page (sur la Switch et dans l'aperçu des Paramètres).</summary>
    public void SetPage(int index)
    {
        index = Math.Clamp(index, 0, _settings.Pages.Count - 1);
        if (index == _settings.CurrentPage) return;
        _settings.CurrentPage = index;
        Sync();
        PageChanged?.Invoke();
    }

    /// <summary>Ajoute une page vide après la page affichée et l'affiche.</summary>
    public void AddPage()
    {
        var index = _settings.CurrentPage + 1;
        _settings.Pages.Insert(index, new DeckPage { Name = $"Page {_settings.Pages.Count + 1}" });
        _settings.CurrentPage = index;
        Sync();
        PageChanged?.Invoke();
    }

    /// <summary>Supprime la page affichée (jamais la dernière restante).</summary>
    public void RemovePage()
    {
        if (_settings.Pages.Count <= 1) return;
        _settings.Pages.RemoveAt(_settings.CurrentPage);
        _settings.CurrentPage = Math.Min(_settings.CurrentPage, _settings.Pages.Count - 1);
        Sync();
        PageChanged?.Invoke();
    }

    /// <summary>Déplace la page affichée d'un cran vers la gauche (-1) ou la droite (+1).</summary>
    public void MovePage(int offset)
    {
        var from = _settings.CurrentPage;
        var to = from + offset;
        if (to < 0 || to >= _settings.Pages.Count) return;
        (_settings.Pages[from], _settings.Pages[to]) = (_settings.Pages[to], _settings.Pages[from]);
        _settings.CurrentPage = to;
        Screen.Sync(); // mêmes éléments : seuls les points de page changent
        PageChanged?.Invoke();
    }

    private void Sync()
    {
        if (Screen.Sync()) ContentsChanged?.Invoke();
    }

    private (double X, double Y) FindFreeSpot(double w, double h)
    {
        var taken = _settings.Elements.Where(e => e.Visible).Select(e => new Rect(e.X, e.Y, e.Width, e.Height)).ToList();
        for (double y = 20; y + h <= DeckScreen.ScreenHeight; y += 20)
            for (double x = 20; x + w <= DeckScreen.ScreenWidth; x += 20)
            {
                var candidate = new Rect(x, y, w, h);
                if (!taken.Any(r => r.IntersectsWith(candidate))) return (x, y);
            }
        return (20, 20);
    }

    public void ApplyBackground() => Screen.ApplyBackground();

    public void ApplyAppearance(string id) => Screen.ApplyAppearance(id);

    /// <summary>Apparence du module sur la Switch ; au premier accès, copie de celle du bureau.</summary>
    public ModuleAppearance GetAppearance(string id)
    {
        if (!_settings.Modules.TryGetValue(id, out var appearance))
            _settings.Modules[id] = appearance = _desktopAppearance(id).Clone();
        return appearance;
    }

    /// <summary>Remplace l'apparence Switch du module par une copie de celle du bureau ; renvoie la nouvelle.</summary>
    public ModuleAppearance CopyAppearanceFromDesktop(string id)
    {
        var appearance = _settings.Modules[id] = _desktopAppearance(id).Clone();
        Screen.ApplyAppearance(id);
        return appearance;
    }

    /// <summary>Éléments déplaçables du module affiché sur la Switch (vide s'il n'y est pas).</summary>
    public IReadOnlyList<string> GetElementKeys(string id) => Screen.CardOf(id)?.MovableKeys ?? Array.Empty<string>();

    public void SetMediaArtwork(ImageSource? artwork) => Screen.SetMediaArtwork(artwork);

    public void ApplyAllAppearances() => Screen.ApplyAllAppearances();

    public void RefreshShortcuts()
    {
        foreach (var panel in Screen.TilePanels) panel.Rebuild();
    }

    public void ApplyClockOpacity() => Screen.ApplyClockOpacity();

    public void ApplyAppsOpacity() => Screen.ApplyAppsOpacity();

    // ---------- Applications de la page ----------

    /// <summary>
    /// Ajoute des exécutables ou raccourcis à la page affichée (sans doublon) et affiche l'élément
    /// « Applications de la page » s'il ne l'est pas encore. Renvoie le nombre d'applications ajoutées.
    /// </summary>
    public int AddApps(IEnumerable<string> paths) => AddApps(paths, out _);

    /// <param name="notAdded">Nombre d'applications refusées faute de case libre dans la grille.</param>
    public int AddApps(IEnumerable<string> paths, out int notAdded)
    {
        var apps = _settings.Page.Apps;
        var added = 0;
        notAdded = 0;
        foreach (var path in paths)
        {
            if (apps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new ShortcutItem { Name = System.IO.Path.GetFileNameWithoutExtension(path), Path = path };
            if (!AddToGrid(item)) { notAdded++; continue; }
            added++;
        }
        if (added == 0) return 0;
        if (!IsShown("apps")) SetVisible("apps", true);
        RefreshShortcuts();
        return added;
    }

    /// <summary>
    /// Ajoute une tuile toute faite à la page affichée (première case libre) et affiche l'élément « apps ».
    /// Renvoie faux, sans rien ajouter, si la grille est pleine.
    /// </summary>
    public bool AddApp(ShortcutItem app)
    {
        if (!AddToGrid(app)) return false;
        if (!IsShown("apps")) SetVisible("apps", true);
        RefreshShortcuts();
        return true;
    }

    /// <summary>Colonnes et lignes de la grille d'applications d'après la taille du bloc et des tuiles.</summary>
    public (int Columns, int Rows) AppsGrid()
    {
        var element = _settings.Find("apps");
        // Élément pas encore créé : il le sera à la taille par défaut (voir SetVisible).
        var width = element?.Width ?? 1240;
        var height = element?.Height ?? 160;
        var tile = Math.Clamp(_settings.TileSize, 60, 300);
        return (TileGrid.Columns(Math.Max(DeckScreen.MinElementSize, width), tile), TileGrid.Rows(Math.Max(DeckScreen.MinElementSize, height), tile));
    }

    /// <summary>Vrai s'il reste une case libre dans la grille d'applications de la page.</summary>
    public bool AppsGridHasRoom()
    {
        var (columns, rows) = AppsGrid();
        return TileGrid.FirstFree(_settings.Page.Apps, columns, rows) is not null;
    }

    /// <summary>Tuiles de la page sans case dans la grille actuelle (non affichées sur la Switch).</summary>
    public IReadOnlyCollection<ShortcutItem> HiddenApps()
    {
        var (columns, rows) = AppsGrid();
        return TileGrid.Arrange(_settings.Page.Apps, columns, rows).Hidden;
    }

    private bool AddToGrid(ShortcutItem item)
    {
        var apps = _settings.Page.Apps;
        var (columns, rows) = AppsGrid();
        TileGrid.Commit(apps, columns, rows);
        if (TileGrid.FirstFree(apps, columns, rows) is not { } cell) return false;
        item.GridCol = cell.Col;
        item.GridRow = cell.Row;
        apps.Add(item);
        return true;
    }

    public void RemoveApp(ShortcutItem app)
    {
        _settings.Page.Apps.Remove(app);
        RefreshShortcuts();
    }

    /// <summary>Échange une application avec sa voisine dans l'ordre de lecture de la grille (-1 = précédente), cases vides comprises.</summary>
    public void MoveApp(ShortcutItem app, int delta)
    {
        var (columns, rows) = AppsGrid();
        if (TileGrid.Step(_settings.Page.Apps, columns, rows, app, delta)) RefreshShortcuts();
    }

    /// <summary>Pose une application sur une case (échange si elle est occupée) ; renvoie vrai si quelque chose a changé.</summary>
    public bool MoveAppToCell(ShortcutItem app, int col, int row)
    {
        var (columns, rows) = AppsGrid();
        if (!TileGrid.MoveOrSwap(_settings.Page.Apps, columns, rows, app, col, row)) return false;
        RefreshShortcuts();
        return true;
    }
}
