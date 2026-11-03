using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SysDeck.Controls;
using SysDeck.Services;

namespace SysDeck;

// Recherche dans les Paramètres : index construit à l'exécution en parcourant l'arbre logique des pages.
// Ajouter un réglage à l'index : rien à faire si sa carte (Border au style Card) a un CardTitle ; sinon poser
// SettingsSearch.Title, et SettingsSearch.Keywords (synonymes) / SettingsSearch.Module (carte propre à un module).
public partial class SettingsWindow
{
    /// <summary>Où un résultat doit mener : page, module et onglet de Personnaliser (sous « Modules »), sous-onglet du thème.</summary>
    private sealed record SearchContext(
        RadioButton Nav, string Prefix, bool Customize = false, RadioButton? Tab = null, string? TabLabel = null,
        RadioButton? GlobalTab = null, Func<string, bool>? ModuleFilter = null);

    private sealed class SearchEntry
    {
        public SearchEntry(string title, string path, string text, string keywords, SearchContext context, string? module,
            FrameworkElement? target)
        {
            Title = title;
            Path = path;
            Context = context;
            Module = module;
            Target = target;
            TitleNorm = SettingsSearch.Tokenize(title);
            KeywordsNorm = TitleNorm + SettingsSearch.Tokenize(keywords);
            FullNorm = KeywordsNorm + SettingsSearch.Tokenize(text);
        }

        public string Title { get; }
        public string Path { get; }
        public SearchContext Context { get; }
        public string? Module { get; }
        /// <summary>Élément à faire défiler et à mettre en évidence (null : on ouvre seulement la page).</summary>
        public FrameworkElement? Target { get; }
        public string TitleNorm { get; }
        public string KeywordsNorm { get; }
        public string FullNorm { get; }
    }

    private List<SearchEntry>? _searchIndex;

    private static string ModuleLabel(string id)
        => WidgetManager.AllWidgets.FirstOrDefault(w => w.Id == id)?.Label ?? id;

    // ---------- Index ----------

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            if (child is ItemsControl or ThemeEditor) continue; // contenu généré / éditeur indexé à part
            foreach (var inner in Descendants(child)) yield return inner;
        }
    }

    /// <summary>Titre de section (SectionHeader) placé juste avant la carte dans son conteneur, s'il y en a un.</summary>
    private static string? HeaderBefore(FrameworkElement card, Style headerStyle)
    {
        if (LogicalTreeHelper.GetParent(card) is not Panel panel) return null;
        var index = panel.Children.IndexOf(card);
        return index > 0 && panel.Children[index - 1] is ContentControl header
            && ReferenceEquals(header.Style, headerStyle) && header.Content is string title ? title : null;
    }

    private List<SearchEntry> BuildSearchIndex()
    {
        var list = new List<SearchEntry>();
        var cardStyle = (Style)FindResource("Card");
        var titleStyle = (Style)FindResource("CardTitle");
        var headerStyle = (Style)FindResource("SectionHeader");
        var navLabels = new Dictionary<RadioButton, (string Group, string Label, string Keywords)>
        {
            [NavModules] = ("Modules", "Modules", "liste afficher masquer personnaliser"),
            [NavDock] = ("Modules", "Barre de raccourcis", "dock lancement rapide"),
            [NavTheme] = ("Apparence", "Thème", "thème global couleurs polices carte jauges animations"),
            [NavProfiles] = ("Apparence", "Profils", "profils thèmes prêts préréglages"),
            [NavSwitch] = ("Appareils", "Switch", "écran câble usb"),
            [NavIntegrations] = ("Applications", "Applications connectées", "applications connectées intégrations obs studio websocket stream enregistrement twitch clip marqueur tchat bouclier compte voicemod voix soundboard"),
            [NavAlerts] = ("Notifications", "Alertes", "notifications seuils température batterie quota"),
            [NavCorrection] = ("Outils", "Correction de texte", "correction ia gemini orthographe"),
            [NavEmoji] = ("Outils", "Émojis", "emoji sélecteur"),
            [NavGeneral] = ("Système", "Général", "avancé fps éléments masqués réinitialiser"),
            [NavEnergy] = ("Système", "Énergie", "électricité prix consommation puissance kwh"),
        };

        // Pages elles-mêmes, puis modules (affichage et personnalisation).
        foreach (var (nav, (group, label, keywords)) in navLabels)
            list.Add(new SearchEntry(label, group, "", keywords, new SearchContext(nav, label), null, null));
        foreach (var widget in WidgetManager.AllWidgets)
        {
            list.Add(new SearchEntry(widget.Label, "Modules", "", "afficher masquer module",
                new SearchContext(NavModules, "Modules"), null, null));
            if (widget.Id == "shortcuts") continue;
            list.Add(new SearchEntry(widget.Label, $"Modules › {widget.Label} › Taille", "", "personnaliser apparence module",
                new SearchContext(NavModules, "Modules", true, TabSizeButton, "Taille"), widget.Id, null));
        }

        // Une carte est une Border au style Card, ou fondée sur lui (RowCard).
        bool IsCard(Border border) => border.Style is { } style && (ReferenceEquals(style, cardStyle) || ReferenceEquals(style.BasedOn, cardStyle));

        void AddCard(FrameworkElement card, SearchContext ctx, string title, string text, string keywords, string? module,
            FrameworkElement? target = null)
        {
            var path = ctx.Customize
                ? string.Join(" › ", new[] { "Modules", module is null ? "Personnaliser" : ModuleLabel(module), ctx.TabLabel })
                : ctx.Prefix;
            // Le libellé du module fait partie des mots cherchés : « électricité » mène à la carte du prix.
            var moduleWords = module is null ? "" : ModuleLabel(module);
            list.Add(new SearchEntry(title, path, text, $"{keywords} {moduleWords}", ctx, module, target ?? card));
        }

        void AddBorderCard(Border card, SearchContext ctx)
        {
            var module = SettingsSearch.GetModule(card);
            var keywords = SettingsSearch.GetKeywords(card) ?? "";
            var forcedTitle = SettingsSearch.GetTitle(card);
            var titles = Descendants(card).OfType<ContentControl>()
                .Where(c => ReferenceEquals(c.Style, titleStyle) && c.Content is string).ToList();
            var editors = Descendants(card).OfType<ThemeEditor>().ToList();

            var text = new System.Text.StringBuilder();
            // Carte d'un onglet de style (interrupteur « Style propre à ce module » + éditeur) : seule la carte de
            // l'éditeur est indexée, sinon deux résultats identiques mèneraient au même endroit.
            if (editors.Count > 0)
            {
                // rien à indexer pour la carte elle-même
            }
            else if (forcedTitle is not null || titles.Count <= 1)
            {
                SettingsSearch.CollectText(card, text);
                var title = forcedTitle ?? titles.FirstOrDefault()?.Content as string ?? HeaderBefore(card, headerStyle);
                if (title is not null) AddCard(card, ctx, title, text.ToString(), keywords, module);
            }
            else
            {
                // Carte à plusieurs titres (ex. Consommation électrique) : une entrée par titre, avec le texte
                // qui le suit jusqu'au titre suivant, pour ne pas faire sortir la carte entière à chaque mot.
                for (var i = 0; i < titles.Count; i++)
                {
                    text.Clear();
                    if (LogicalTreeHelper.GetParent(titles[i]) is Panel panel)
                    {
                        for (var j = panel.Children.IndexOf(titles[i]) + 1; j < panel.Children.Count; j++)
                        {
                            if (panel.Children[j] is ContentControl next && titles.Contains(next)) break;
                            SettingsSearch.CollectText(panel.Children[j], text);
                        }
                    }
                    // Le premier titre représente la carte ; les suivants font défiler jusqu'à leur titre.
                    AddCard(card, ctx, (string)titles[i].Content, text.ToString(), i == 0 ? keywords : "", module,
                        i == 0 ? card : titles[i]);
                }
            }

            // Éditeurs de thème logés dans la carte (onglets de style d'un module).
            foreach (var editor in editors)
                AddEditorCards(editor, ctx);
        }

        void AddEditorCards(ThemeEditor editor, SearchContext ctx)
        {
            foreach (var (card, title) in editor.GetSearchCards())
            {
                var text = new System.Text.StringBuilder();
                SettingsSearch.CollectText(card, text);
                AddCard(card, ctx, title, text.ToString(), SettingsSearch.GetKeywords(card) ?? "", null);
            }
        }

        void Walk(DependencyObject node, SearchContext ctx)
        {
            switch (node)
            {
                case ThemeEditor editor:
                    AddEditorCards(editor, ctx);
                    return;
                case Border border when IsCard(border):
                    AddBorderCard(border, ctx);
                    return;
                case ItemsControl:
                    return;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                Walk(child, ctx);
        }

        Walk(PageProfiles, new SearchContext(NavProfiles, "Profils"));
        Walk(PageModules, new SearchContext(NavModules, "Modules"));
        Walk(PageDock, new SearchContext(NavDock, "Barre de raccourcis"));
        Walk(SwitchSettingsPanel, new SearchContext(NavSwitch, "Switch › Réglages", GlobalTab: SwitchTabSettings));
        Walk(PageIntegrations, new SearchContext(NavIntegrations, "Applications connectées"));
        Walk(PageCorrection, new SearchContext(NavCorrection, "Correction de texte"));
        Walk(PageEmoji, new SearchContext(NavEmoji, "Émojis"));
        Walk(PageAlerts, new SearchContext(NavAlerts, "Alertes"));
        Walk(PageGeneral, new SearchContext(NavGeneral, "Général"));
        Walk(PageEnergy, new SearchContext(NavEnergy, "Énergie"));

        // Thème : un éditeur par sous-onglet.
        foreach (var (button, editor, label) in new[]
        {
            (GlobalTabColorsButton, GlobalThemeEditor, "Couleurs et polices"),
            (GlobalTabCardButton, GlobalCardEditor, "Carte"),
            (GlobalTabGaugesButton, GlobalGaugesEditor, "Jauges"),
            (GlobalTabAnimationsButton, GlobalAnimationsEditor, "Animations"),
        })
            AddEditorCards(editor, new SearchContext(NavTheme, $"Thème › {label}", GlobalTab: button));

        // Personnaliser : un contexte par onglet (Style : une section à la fois) ; les parties sans objet pour
        // le module courant imposent un filtre.
        foreach (var (panel, button, label, filter) in new (FrameworkElement, RadioButton, string, Func<string, bool>?)[]
        {
            (TabSize, TabSizeButton, "Taille", null),
            (TabElements, TabElementsButton, "Éléments", null),
            (TabTexts, TabTextsButton, "Textes", id => Controls.TextPart.ByModule.ContainsKey(id)),
            (TabButtons, TabButtonsButton, "Boutons", id => ModuleCapabilities.HasButtons(id)),
            (StyleColorsSection, TabStyleButton, "Style", null),
            (StyleCardSection, TabStyleButton, "Style", null),
            (StyleGaugesSection, TabStyleButton, "Style", id => ModuleCapabilities.HasAnyGaugeOption(id)),
            (StyleAnimationsSection, TabStyleButton, "Style", null),
        })
            Walk(panel, new SearchContext(NavModules, "Modules", true, button, label, ModuleFilter: filter));

        return list;
    }

    // ---------- Recherche ----------

    private List<SearchEntry> RunSearch(string query)
    {
        _searchIndex ??= BuildSearchIndex();
        var terms = SettingsSearch.Tokenize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(term => " " + term).ToArray(); // début de mot
        if (terms.Length == 0) return new();

        // Titre d'abord, puis mots-clés, puis texte de la carte ; à rang égal, l'ordre des pages.
        return _searchIndex
            .Select((entry, order) => (entry, order, rank: terms.All(entry.TitleNorm.Contains) ? 0
                : terms.All(entry.KeywordsNorm.Contains) ? 1
                : terms.All(entry.FullNorm.Contains) ? 2 : -1))
            .Where(x => x.rank >= 0)
            .OrderBy(x => x.rank).ThenBy(x => x.order)
            .Take(40)
            .Select(x => x.entry)
            .ToList();
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || SearchResultsScroll is null) return;

        var query = SearchInput.Text.Trim();
        var active = query.Length > 0;
        SearchClearButton.Visibility = SearchInput.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NavScroller.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        SearchResultsScroll.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active) return;

        var results = RunSearch(query);
        SearchResultsList.ItemsSource = results;
        SearchEmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchResultsScroll.ScrollToTop();
    }

    private void ClearSearch() => SearchInput.Text = "";

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        ClearSearch();
        SearchInput.Focus();
    }

    private void SearchInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SearchResultsList.ItemsSource is List<SearchEntry> { Count: > 0 } results)
        {
            OpenSearchEntry(results[0]);
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+F place le curseur dans la recherche ; Échap l'efface depuis n'importe où.</summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is HotkeyBox) return; // saisie d'un raccourci : on ne la perturbe pas

        if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SetNavCollapsed(!_navCollapsed, save: true);
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_navCollapsed) SetNavCollapsed(false, save: true); // la recherche est dans le menu
            SearchInput.Focus();
            SearchInput.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchInput.Text.Length > 0)
        {
            ClearSearch();
            e.Handled = true;
        }
    }

    private void SearchResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SearchEntry entry }) OpenSearchEntry(entry);
    }

    // ---------- Ouverture d'un résultat ----------

    private void OpenSearchEntry(SearchEntry entry)
    {
        ClearSearch(); // le menu revient et montre la page ouverte
        var ctx = entry.Context;

        NavigateTo(ctx.Nav, ctx.Customize);
        if (ctx.GlobalTab is not null) ctx.GlobalTab.IsChecked = true;

        if (ctx.Tab is not null)
        {
            // Carte propre à un module : on sélectionne ce module. Onglet sans objet pour le module courant
            // (ex. Jauges sur un module sans jauge) : on bascule sur le premier module qui en a.
            var rows = ModuleSelector.Items.Cast<ModuleRow>().ToList();
            var wanted = entry.Module is not null ? rows.FirstOrDefault(m => m.Id == entry.Module)
                : ctx.ModuleFilter is { } filter && SelectedModule is { } current && !filter(current.Id)
                    ? rows.FirstOrDefault(m => filter(m.Id))
                    : null;
            if (wanted is not null && !ReferenceEquals(wanted, SelectedModule)) ModuleSelector.SelectedItem = wanted;
            ctx.Tab.IsChecked = true;
        }

        if (entry.Target is { } target)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RevealAndFlash(target));
    }

    private static bool IsInsideThemeEditor(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null; node = LogicalTreeHelper.GetParent(node))
            if (node is ThemeEditor) return true;
        return false;
    }

    /// <summary>Fait défiler jusqu'à l'élément puis le met brièvement en évidence.</summary>
    private void RevealAndFlash(FrameworkElement target)
    {
        UpdateLayout();

        // Carte réservée à l'autre cible (bureau / Switch) : on bascule sur la cible où elle existe.
        // Un éditeur de style masqué (« Suit le thème global ») n'est pas lié à la cible : on n'y touche pas.
        if (!target.IsVisible && PageCustomize.IsVisible && !IsInsideThemeEditor(target))
        {
            (_customizeSwitch ? TargetDesktop : TargetSwitch).IsChecked = true;
            UpdateLayout();
        }
        if (!target.IsVisible) return;

        DependencyObject? parent = target;
        while (parent is not null and not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
        if (parent is ScrollViewer { Content: Visual content } scroller)
        {
            var top = target.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
            scroller.ScrollToVerticalOffset(Math.Max(0, top - 14));
            scroller.UpdateLayout();
        }
        FlashAdorner.Flash(target);
    }
}
