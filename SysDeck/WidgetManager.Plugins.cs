using SysDeck.Infrastructure;
using SysDeck.Models;
using SysDeck.Plugins;
using SysDeck.Services;

namespace SysDeck;

// Plugins : programmes séparés reliés par WebSocket local, qui ajoutent des actions de tuiles (voir Plugins\PluginHost).
public sealed partial class WidgetManager
{
    private PluginHost? _plugins;

    /// <summary>Hôte des plugins (liste, installation, état pour les Paramètres et les fenêtres de tuiles).</summary>
    public PluginHost PluginHost => _plugins ??= CreatePluginHost();

    private PluginHost CreatePluginHost()
    {
        var host = new PluginHost(AllTiles, SavePluginTiles, _config.Plugins, SaveConfig, () => _deck.AppsGrid());
        host.TilesChanged += () =>
        {
            if (!_disposed) RefreshTiles();
        };
        host.StateChanged += () =>
        {
            if (!_disposed) RefreshTiles(); // tuiles atténuées ou non selon que leur plugin tourne
        };
        TileRuntime.Plugins = host;
        return host;
    }

    /// <summary>Lance les plugins activés (au démarrage de SysDeck).</summary>
    private void StartPlugins()
    {
        try
        {
            PluginHost.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("Démarrage des plugins en échec", ex);
        }
    }

    /// <summary>Réglages d'une tuile changés par son plugin : la tuile peut être sur la Switch (config) ou dans la barre.</summary>
    private void SavePluginTiles()
    {
        SaveConfig();
        _shortcutStore.Save(_shortcuts);
    }

    private void RunPluginTile(ShortcutItem item)
    {
        if (PluginHost.Press(item)) return;
        var info = PluginHost.Find(item.PluginId);
        ShowWarning(info switch
        {
            null => $"Le plugin de « {item.Name} » n'est pas installé.",
            { Status: PluginStatus.Disabled } => $"Le plugin « {info.Name} » est désactivé : active-le dans Paramètres › Plugins.",
            { Status: PluginStatus.Crashed or PluginStatus.Invalid } => $"Le plugin « {info.Name} » ne fonctionne pas :\n{info.Detail}",
            _ => $"Le plugin « {info.Name} » démarre encore : réessaie dans un instant."
        });
    }

    private void DisposePlugins()
    {
        TileRuntime.Plugins = null;
        _plugins?.Dispose();
    }
}
