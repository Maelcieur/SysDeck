using System.Windows;

namespace SysDeck.Controls;

/// <param name="DefaultText">
/// Texte d'origine pour les textes fixes (titres, libellés), qui peuvent alors être renommés ;
/// null pour les valeurs mises à jour par le module (températures, pourcentages, noms…).
/// </param>
public sealed record TextPartInfo(string Key, string Label, string? DefaultText = null)
{
    public bool IsEditable => DefaultText is not null;
}

/// <summary>
/// Marque un texte d'un module (TextPart.Key="name", "load"…) pour qu'il puisse être affiché/masqué,
/// changer de police, de taille, de couleur et, pour les textes fixes, de libellé, depuis la
/// personnalisation du module. Dans une liste (disques, périphériques…), le réglage s'applique au
/// même texte de chaque élément.
/// </summary>
public static class TextPart
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(TextPart), new PropertyMetadata(null));

    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);
    public static void SetKey(DependencyObject d, string? value) => d.SetValue(KeyProperty, value);

    private static TextPartInfo[] Processor(string title, params TextPartInfo[] extra) => new TextPartInfo[]
    {
        new("title", "Titre", title),
        new("name", "Nom du composant"),
        new("load", "Pourcentage d'utilisation"),
        new("loadLabel", "Libellé « CHARGE »", "CHARGE"),
        new("temp", "Température"),
        new("tempLabel", "Libellé « TEMP. »", "TEMP."),
    }.Concat(extra).ToArray();

    /// <summary>Textes personnalisables de chaque module, dans l'ordre d'affichage.</summary>
    public static readonly Dictionary<string, TextPartInfo[]> ByModule = new()
    {
        ["cpu"] = Processor("PROCESSEUR",
            new("clock", "Fréquence (valeur)"),
            new("clockLabel", "Libellé « Fréquence · MHz »", "Fréquence · MHz")),
        ["gpu"] = Processor("CARTE GRAPHIQUE",
            new("fps", "Images par seconde (valeur)"),
            new("fpsLabel", "Libellé « FPS »", "FPS"),
            new("gpuClock", "Fréquence GPU (valeur)"),
            new("gpuClockLabel", "Libellé « GPU · MHz »", "GPU · MHz"),
            new("memClock", "Fréquence mémoire (valeur)"),
            new("memClockLabel", "Libellé « Mémoire · MHz »", "Mémoire · MHz"),
            new("vram", "Mémoire vidéo utilisée (valeur)"),
            new("vramLabel", "Libellé « VRAM · Go »", "VRAM · Go")),
        ["ram"] = new TextPartInfo[]
        {
            new("title", "Titre", "MÉMOIRE (RAM)"),
            new("load", "Pourcentage d'utilisation"),
            new("loadLabel", "Libellé « UTILISÉE »", "UTILISÉE"),
            new("used", "Mémoire utilisée (valeur)"),
            new("usedLabel", "Libellé « Go utilisés »", "Go utilisés"),
            new("free", "Mémoire libre (valeur)"),
            new("freeLabel", "Libellé « Go libres »", "Go libres"),
            new("total", "Total"),
        },
        ["disk"] = new TextPartInfo[]
        {
            new("title", "Titre", "DISQUES"),
            new("drive", "Lettre du disque"),
            new("percent", "Pourcentage"),
            new("usage", "Espace utilisé / total"),
        },
        ["mouse"] = new TextPartInfo[]
        {
            new("title", "Titre", "SOURIS"),
            new("battery", "Pourcentage de batterie"),
            new("status", "État (en charge / sur batterie)"),
        },
        ["network"] = new TextPartInfo[]
        {
            new("title", "Titre", "RÉSEAU"),
            new("download", "Débit de réception"),
            new("upload", "Débit d'envoi"),
        },
        ["media"] = new TextPartInfo[]
        {
            new("track", "Titre du morceau"),
            new("artist", "Artiste / source"),
        },
        ["volumemixer"] = new TextPartInfo[]
        {
            new("title", "Titre", "VOLUME PAR APPLICATION"),
            new("app", "Nom des applications"),
        },
        ["audio"] = new TextPartInfo[]
        {
            new("title", "Titre", "SORTIE AUDIO"),
            new("device", "Nom des périphériques"),
        },
        ["visualizer"] = new TextPartInfo[]
        {
            new("title", "Titre", "VISUALISEUR"),
            new("device", "Sortie audio écoutée"),
        },
        ["claude"] = new TextPartInfo[]
        {
            new("title", "Titre", "CLAUDE"),
            new("plan", "Abonnement (Pro, Max…)"),
            new("sessionLabel", "Libellé « Session 5 h »", "Session 5 h"),
            new("sessionPercent", "Pourcentage de la session"),
            new("sessionReset", "Réinitialisation de la session"),
            new("weekLabel", "Libellé « Semaine »", "Semaine"),
            new("weekPercent", "Pourcentage de la semaine"),
            new("weekReset", "Réinitialisation de la semaine"),
            new("status", "Message d'état (non connecté, jeton expiré)"),
        },
        ["power"] = new TextPartInfo[]
        {
            new("title", "Titre", "CONSOMMATION"),
            new("watts", "Puissance (W)"),
            new("wattsLabel", "Libellé « PUISSANCE »", "PUISSANCE"),
            new("session", "Coût depuis l'allumage"),
            new("sessionLabel", "Durée d'allumage"),
            new("today", "Coût du jour"),
            new("todayLabel", "Libellé « Aujourd'hui »", "Aujourd'hui"),
            new("month", "Coût du mois"),
            new("monthLabel", "Libellé « Ce mois-ci »", "Ce mois-ci"),
            new("usageToday", "Heures d'utilisation du jour (jour / soir)"),
            new("lifetime", "Coût depuis la mise en service"),
            new("lifetimeLabel", "Date, heures et kWh depuis la mise en service"),
            new("usageLifetime", "Heures jour / soir depuis la mise en service"),
            new("projection", "Estimation mensuelle"),
            new("details", "Détail CPU / GPU / autres"),
        },
    };

    /// <summary>Vrai si ce texte du module peut être renommé (texte fixe, jamais réécrit par le module).</summary>
    public static bool IsEditable(string moduleId, string key)
        => ByModule.TryGetValue(moduleId, out var parts) && parts.Any(p => p.Key == key && p.IsEditable);
}
