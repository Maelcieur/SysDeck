using System.Security.Cryptography;
using System.Text;

namespace SysDeck.Infrastructure;

/// <summary>
/// Une seule instance de SysDeck par dossier de données : un second lancement (raccourci, démarrage
/// automatique en double) prévient la première, qui ouvre ses Paramètres, puis se ferme. Sans ça, deux
/// instances afficheraient les widgets en double et écriraient les mêmes fichiers en même temps.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle? _registration;

    public bool IsFirst { get; }

    /// <param name="onActivated">Appelé (hors thread d'interface) quand une autre instance a été lancée.</param>
    public SingleInstance(Action onActivated)
    {
        // Nom propre au dossier de données : SYSDECK_DATA_DIR permet de lancer une instance de test à côté.
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DataDir.ToLowerInvariant())))[..16];
        _mutex = new Mutex(initiallyOwned: true, $@"Local\SysDeck-{id}", out var createdNew);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\SysDeck-{id}-activate");
        IsFirst = createdNew;

        if (IsFirst)
            _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivated(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Demande à la première instance de se montrer.</summary>
    public void ActivateFirstInstance() => _activate.Set();

    public void Dispose()
    {
        _registration?.Unregister(null);
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* déjà relâché */ }
        }
        _mutex.Dispose();
        _activate.Dispose();
    }
}
