namespace SysDeck.Services;

/// <summary>Application proposée dans la page « Applications connectées ».</summary>
public sealed record ConnectedAppInfo(string Id, string Title, string BrandSlug, bool Available);

/// <summary>Catalogue et logique pure des applications connectées (testée sans interface).</summary>
public static class ConnectedApps
{
    public const string Obs = "obs", Twitch = "twitch", Voicemod = "voicemod";

    public static readonly IReadOnlyList<ConnectedAppInfo> Catalog = new[]
    {
        new ConnectedAppInfo(Obs, "OBS Studio", "obsstudio", true),
        new ConnectedAppInfo(Twitch, "Twitch", "twitch", true),
        new ConnectedAppInfo(Voicemod, "Voicemod", "", true)
    };

    /// <summary>Liste initiale d'un ancien fichier : les applications déjà configurées.</summary>
    public static List<string> FromSettings(ObsSettings obs, TwitchSettings twitch, VoicemodSettings? voicemod = null)
    {
        var apps = new List<string>();
        if (obs.Enabled || !string.IsNullOrEmpty(obs.PasswordProtected)) apps.Add(Obs);
        if (twitch.Enabled) apps.Add(Twitch);
        if (voicemod?.Enabled == true) apps.Add(Voicemod);
        return apps;
    }

    /// <summary>Applications du catalogue pas encore affichées (y compris celles à venir, non disponibles).</summary>
    public static IReadOnlyList<ConnectedAppInfo> Addable(IEnumerable<string> shown)
    {
        var set = shown.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Catalog.Where(app => !set.Contains(app.Id)).ToList();
    }

    /// <summary>Ajoute une application disponible à la fin de la liste ; faux si elle y est déjà ou n'existe pas.</summary>
    public static bool Add(List<string> shown, string id)
    {
        if (shown.Contains(id, StringComparer.OrdinalIgnoreCase)) return false;
        if (!Catalog.Any(app => app.Available && app.Id == id)) return false;
        shown.Add(id);
        return true;
    }
}
