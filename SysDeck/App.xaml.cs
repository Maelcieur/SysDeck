using System.Windows;
using System.Windows.Threading;
using SysDeck.Infrastructure;
using SysDeck.Services.ConsoleMode;

namespace SysDeck;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private WidgetManager? _widgetManager;
    private TrayIconService? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private DateTime _lastErrorDialog = DateTime.MinValue;
    private bool _startupComplete;
    // Activation (second lancement) reçue avant que le gestionnaire de modules existe : traitée une fois prêt.
    private bool _openSettingsWhenReady;

    /// <summary>
    /// Vrai dès que SysDeck se ferme (Quitter, fermeture de session) : WPF ferme alors lui-même les
    /// fenêtres des modules, ce qui ne doit pas être pris pour un redémarrage d'Explorer.
    /// </summary>
    public static bool IsExiting { get; private set; }

    /// <summary>Quitte SysDeck (menu de l'icône, bouton des Paramètres).</summary>
    public static void Quit()
    {
        IsExiting = true;
        Current.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        RegisterGlobalErrorHandlers();

        try
        {
            // AppPaths.DataDir crée le dossier de données (variable SYSDECK_DATA_DIR invalide, droits...) :
            // sans ce try, l'erreur sortirait comme un plantage muet au démarrage.
            _ = AppPaths.DataDir;
            _singleInstance = new SingleInstance(() => Dispatcher.BeginInvoke(OpenSettings));
        }
        catch (Exception ex)
        {
            var folder = Environment.GetEnvironmentVariable("SYSDECK_DATA_DIR");
            Log.Error("Dossier de données inutilisable", ex);
            MessageBox.Show($"SysDeck ne peut pas utiliser son dossier de données"
                            + (string.IsNullOrWhiteSpace(folder) ? "" : $" (« {folder} », défini par SYSDECK_DATA_DIR)")
                            + $" :\n{ex.Message}\n\nVérifiez que le dossier existe, est accessible en écriture, ou supprimez la variable SYSDECK_DATA_DIR.",
                "SysDeck", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (!_singleInstance.IsFirst)
        {
            // SysDeck tourne déjà : on lui demande d'ouvrir ses Paramètres et on s'arrête là.
            _singleInstance.ActivateFirstInstance();
            Shutdown();
            return;
        }

        Log.Info($"Démarrage de SysDeck {typeof(App).Assembly.GetName().Version} (données : {AppPaths.DataDir})");

        // Mode console interrompu (plantage, coupure) : écrans, icônes et audio sont remis AVANT de créer les modules.
        var consoleService = new ConsoleModeService();
        try
        {
            if (consoleService.RecoverIfNeeded()) Log.Info("Mode console : configuration d'avant le plantage restaurée.");
        }
        catch (Exception ex)
        {
            Log.Warn("Mode console : reprise au démarrage en échec", ex);
        }

        try
        {
            _widgetManager = new WidgetManager(consoleService);
        }
        catch (Exception ex)
        {
            Log.Error("Échec du démarrage", ex);
            MessageBox.Show($"SysDeck n'a pas pu démarrer :\n{ex.Message}\n\nDétails dans {AppPaths.LogDir}\\sysdeck.log",
                "SysDeck", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _trayIcon = new TrayIconService();
        _trayIcon.SettingsRequested += OpenSettings;
        _trayIcon.ExitRequested += Quit;
        SessionEnding += (_, _) => IsExiting = true;
        _trayIcon.LayoutEditToggleRequested += () => _widgetManager.SetLayoutEditMode(!_widgetManager.LayoutEditMode);
        _widgetManager.LayoutEditModeChanged += enabled => _trayIcon.SetLayoutEditChecked(enabled);
        _widgetManager.CustomizeRequested += id =>
        {
            OpenSettings();
            _settingsWindow?.ShowModule(id);
        };

        // « SysDeck.exe --settings » ouvre directement les Paramètres (raccourci, tests).
        if (_openSettingsWhenReady || e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            OpenSettings();
        // « --edit-layout » démarre en mode édition de la disposition des modules.
        if (e.Args.Contains("--edit-layout", StringComparer.OrdinalIgnoreCase))
            _widgetManager.SetLayoutEditMode(true);

        _widgetManager.EnsureTvDisabledIfConfigured();

        _startupComplete = true;
    }

    /// <summary>
    /// Toute exception non rattrapée est journalisée (pile complète). Celles du thread d'interface sont absorbées
    /// une fois SysDeck démarré : un widget qui échoue ne doit pas faire disparaître tous les autres. Pendant le
    /// démarrage, ou pour une erreur fatale (mémoire...), on n'absorbe rien : continuer à moitié initialisé
    /// cacherait le vrai problème.
    /// </summary>
    private void RegisterGlobalErrorHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Exception non gérée (interface)", args.Exception);
            if (!_startupComplete || args.Exception is OutOfMemoryException or AccessViolationException or System.Runtime.InteropServices.SEHException)
                return; // non absorbée : WPF termine l'application après la journalisation
            args.Handled = true;

            // Une erreur qui se répète à chaque rafraîchissement ne doit pas ouvrir une fenêtre par seconde.
            if (DateTime.Now - _lastErrorDialog < TimeSpan.FromMinutes(1)) return;
            _lastErrorDialog = DateTime.Now;
            MessageBox.Show($"Une erreur inattendue s'est produite, SysDeck continue de fonctionner.\n\n{args.Exception.Message}\n\nDétails dans {AppPaths.LogDir}\\sysdeck.log",
                "SysDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Exception non observée (tâche)", args.Exception);
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error($"Exception fatale (arrêt : {args.IsTerminating})", args.ExceptionObject as Exception);
    }

    private void OpenSettings()
    {
        if (_widgetManager is null)
        {
            _openSettingsWhenReady = true;
            return;
        }

        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_widgetManager);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _widgetManager?.Dispose();
            _trayIcon?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Erreur pendant la fermeture", ex);
        }
        finally
        {
            _singleInstance?.Dispose();
            if (_widgetManager is not null) Log.Info("Arrêt de SysDeck");
        }
        base.OnExit(e);
    }
}
