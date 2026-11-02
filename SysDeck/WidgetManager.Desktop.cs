using System.Windows.Controls;
using System.Windows.Threading;

namespace SysDeck;

// Édition directe sur le bureau : menu clic droit de chaque module et redimensionnement à la molette.
public sealed partial class WidgetManager
{
    // Mêmes bornes que les curseurs Largeur / Hauteur de la page Personnaliser.
    private const double MinCardWidth = 80, MaxCardWidth = 900, MinCardHeight = 50, MaxCardHeight = 900;

    // Ctrl + molette envoie une rafale de changements : une seule écriture de config.json à la fin.
    private DispatcherTimer? _desktopSaveTimer;

    private void SaveConfigSoon()
    {
        if (_desktopSaveTimer is null)
        {
            _desktopSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _desktopSaveTimer.Tick += (_, _) =>
            {
                _desktopSaveTimer.Stop();
                SaveConfig();
            };
        }
        _desktopSaveTimer.Stop();
        _desktopSaveTimer.Start();
    }

    /// <summary>« Personnaliser ce module… » choisi sur le bureau : la fenêtre Paramètres s'ouvre sur ce module.</summary>
    public event Action<string>? CustomizeRequested;

    /// <summary>Module masqué ou verrouillage changé depuis le bureau : la fenêtre Paramètres se remet à jour.</summary>
    public event Action? DesktopStateChanged;

    /// <summary>Menu du module sur le bureau, reconstruit à chaque ouverture pour refléter l'état courant.</summary>
    private ContextMenu BuildWidgetMenu(string id)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => FillWidgetMenu(menu, id);
        return menu;
    }

    private void FillWidgetMenu(ContextMenu menu, string id)
    {
        menu.Items.Clear();

        MenuItem Item(string header, Action action, bool isChecked = false)
        {
            var item = new MenuItem { Header = header, IsCheckable = isChecked, IsChecked = isChecked };
            item.Click += (_, _) => action();
            return item;
        }

        var hasCard = id != "shortcuts"; // la barre de raccourcis a sa propre page de réglages et sa propre taille
        if (hasCard)
        {
            menu.Items.Add(Item("Personnaliser ce module…", () => CustomizeRequested?.Invoke(id)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Agrandir (Ctrl + molette)", () => ResizeWidget(id, 1.1)));
            menu.Items.Add(Item("Réduire", () => ResizeWidget(id, 1 / 1.1)));
            menu.Items.Add(Item("Taille automatique", () => ResetWidgetSize(id), GetAppearance(id).AutoSize));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Textes plus grands", () => ScaleWidgetText(id, 1.1)));
            menu.Items.Add(Item("Textes plus petits", () => ScaleWidgetText(id, 1 / 1.1)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Modifier la disposition des éléments", () => SetLayoutEditMode(!LayoutEditMode), LayoutEditMode));
        }
        menu.Items.Add(Item("Verrouiller la position des modules", () =>
        {
            SetWidgetsLocked(!_config.WidgetsLocked);
            DesktopStateChanged?.Invoke();
        }, _config.WidgetsLocked));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Masquer ce module", () =>
        {
            ToggleWidget(id, false);
            DesktopStateChanged?.Invoke();
        }));
    }

    /// <summary>
    /// Agrandit ou réduit la carte d'un facteur ; en taille automatique, on part de la taille affichée
    /// (passage en taille manuelle, contenu mis à l'échelle).
    /// </summary>
    public void ResizeWidget(string id, double factor)
    {
        if (id == "shortcuts" || !_windows.TryGetValue(id, out var window)) return;
        var appearance = GetAppearance(id);
        if (appearance.AutoSize)
        {
            var size = window.CurrentCardSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            appearance.Width = size.Width;
            appearance.Height = size.Height;
            appearance.AutoSize = false;
        }

        // Les deux côtés suivent le même facteur, dans la limite des bornes : les proportions restent proches.
        var f = Math.Clamp(factor, Math.Max(MinCardWidth / appearance.Width, MinCardHeight / appearance.Height),
            Math.Min(MaxCardWidth / appearance.Width, MaxCardHeight / appearance.Height));
        appearance.Width = Math.Round(Math.Clamp(appearance.Width * f, MinCardWidth, MaxCardWidth));
        appearance.Height = Math.Round(Math.Clamp(appearance.Height * f, MinCardHeight, MaxCardHeight));
        ApplyModuleAppearance(id);
        SaveConfigSoon();
    }

    public void ResetWidgetSize(string id)
    {
        var appearance = GetAppearance(id);
        if (appearance.AutoSize) return;
        appearance.AutoSize = true;
        ApplyModuleAppearance(id);
        SaveConfig();
    }

    private void ScaleWidgetText(string id, double factor)
    {
        var appearance = GetAppearance(id);
        appearance.FontScale = Math.Round(Math.Clamp(appearance.FontScale * factor, 0.4, 3), 2);
        ApplyModuleAppearance(id);
        SaveConfig();
    }
}
