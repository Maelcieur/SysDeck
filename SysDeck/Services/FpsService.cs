using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

public enum FpsState
{
    /// <summary>Mesure arrêtée (module masqué).</summary>
    Stopped,
    /// <summary>Le compte n'est pas (encore) dans le groupe « Utilisateurs du journal de performances ».</summary>
    NoPermission,
    /// <summary>PresentMon introuvable ou en échec répété.</summary>
    Unavailable,
    Running,
}

/// <summary>
/// Images par seconde du jeu au premier plan, mesurées par PresentMon (Intel, open source) : il écoute
/// les événements Windows émis à chaque image affichée, quelle que soit l'API (DirectX, Vulkan, OpenGL).
/// Fonctionne sans droits administrateur si le compte est membre du groupe « Utilisateurs du journal
/// de performances » (SID S-1-5-32-559). PresentMon ne tourne que pendant que la mesure est demandée.
/// </summary>
public sealed class FpsService : IDisposable
{
    private const string PerformanceLogUsersSid = "S-1-5-32-559";
    private const string SessionName = "SysDeckFPS";
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1.5);

    private readonly string _exePath = Path.Combine(AppContext.BaseDirectory, "Tools", "PresentMon", "PresentMon.exe");
    private readonly object _gate = new();
    // Par processus : arrivée et durée (ms) de chaque image récente.
    private readonly Dictionary<int, Queue<(DateTime At, double Ms)>> _frames = new();
    private Process? _process;
    private int _failures;
    private DateTime _retryAfter = DateTime.MinValue;

    public FpsState State { get; private set; } = FpsState.Stopped;

    /// <summary>Vrai si le jeton de session contient le groupe (effectif seulement après reconnexion).</summary>
    public static bool HasPermission => CachedPermission.Value;

    // Le jeton ne change pas pendant la session (l'ajout au groupe n'est effectif qu'après reconnexion) :
    // inutile de relire l'identité Windows à chaque rafraîchissement de 500 ms.
    private static readonly Lazy<bool> CachedPermission = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem
               || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
               || identity.Groups?.Any(g => g.Value == PerformanceLogUsersSid) == true;
    });

    /// <summary>Démarre la mesure si elle ne tourne pas (appelé à chaque rafraîchissement : relance après un plantage).</summary>
    public void EnsureRunning()
    {
        if (_process is { HasExited: false }) return;
        if (DateTime.UtcNow < _retryAfter) return;

        // Ancien processus terminé (plantage) : on libère son handle avant d'en lancer un autre.
        if (_process is not null)
        {
            var old = _process;
            _process = null;
            try { old.Dispose(); } catch { /* best effort */ }
        }

        if (!File.Exists(_exePath))
        {
            State = FpsState.Unavailable;
            Log.WarnOnce("fps:missing", $"PresentMon introuvable : {_exePath}");
            return;
        }
        if (!HasPermission)
        {
            State = FpsState.NoPermission;
            return;
        }

        try
        {
            var psi = new ProcessStartInfo(_exePath)
            {
                // Sortie CSV en continu, sans fichier ni console ; mesures 1.x (colonnes stables).
                Arguments = $"--output_stdout --no_console_stats --v1_metrics --session_name {SessionName} --stop_existing_session " +
                            "--no_track_input --no_track_gpu --exclude dwm.exe --exclude SysDeck.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            // Première ligne : en-têtes CSV. Les positions des colonnes utiles sont repérées une fois.
            int pidIndex = -1, msIndex = -1;
            var headerRead = false;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                if (!headerRead)
                {
                    var header = e.Data.Split(',');
                    pidIndex = Array.FindIndex(header, h => h.Equals("ProcessID", StringComparison.OrdinalIgnoreCase));
                    msIndex = Array.FindIndex(header, h => h.Equals("msBetweenPresents", StringComparison.OrdinalIgnoreCase));
                    headerRead = true;
                    if (pidIndex < 0 || msIndex < 0) Log.Warn("PresentMon : colonnes attendues absentes (" + e.Data + ")");
                    return;
                }
                if (pidIndex >= 0 && msIndex >= 0) OnFrame(e.Data, pidIndex, msIndex);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data) && e.Data.StartsWith("error", StringComparison.OrdinalIgnoreCase))
                    Log.WarnOnce("fps:error:" + e.Data, "PresentMon : " + e.Data);
            };
            process.Exited += (_, _) => OnExited(process);
            // Affecté AVANT Start : un arrêt immédiat déclenche Exited sur un autre thread, qui ignorerait
            // le processus s'il n'était pas encore reconnu (et le backoff ne s'appliquerait pas).
            _process = process;
            State = FpsState.Running;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _process = null;
            Log.WarnOnce("fps:start", "Démarrage de PresentMon impossible", ex);
            State = FpsState.Unavailable;
            _retryAfter = DateTime.UtcNow.AddSeconds(30);
        }
    }

    private void OnExited(Process process)
    {
        var code = 0;
        try { code = process.ExitCode; } catch { /* processus déjà libéré */ }
        if (process != _process) return;

        // Code 6 = accès refusé (droits manquants) ; sinon plantage : nouvelle tentative, de plus en plus espacée.
        if (code == 6)
        {
            State = FpsState.NoPermission;
            _retryAfter = DateTime.UtcNow.AddMinutes(1);
            return;
        }
        _failures++;
        Log.Warn($"PresentMon s'est arrêté (code {code}), tentative {_failures}");
        State = _failures >= 5 ? FpsState.Unavailable : FpsState.Running;
        _retryAfter = DateTime.UtcNow.AddSeconds(Math.Min(60, 5 * _failures));
    }

    private void OnFrame(string line, int pidIndex, int msIndex)
    {
        var columns = line.Split(',');
        if (columns.Length <= Math.Max(pidIndex, msIndex)) return;
        if (!int.TryParse(columns[pidIndex], out var pid)) return;
        if (!double.TryParse(columns[msIndex], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms) || ms <= 0) return;

        _failures = 0; // PresentMon produit des mesures : les plantages précédents sont oubliés
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (!_frames.TryGetValue(pid, out var queue))
                _frames[pid] = queue = new Queue<(DateTime, double)>();
            queue.Enqueue((now, ms));
            while (queue.Count > 0 && now - queue.Peek().At > Window) queue.Dequeue();
        }
    }

    /// <summary>
    /// FPS du processus au premier plan (le jeu, quand on joue) : 1000 / durée moyenne d'une image sur
    /// la dernière seconde et demie. Null si ce processus n'affiche rien en ce moment.
    /// </summary>
    public double? ReadForegroundFps()
    {
        if (State != FpsState.Running) return null;
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            // Ménage : processus qui n'affichent plus rien.
            foreach (var stale in _frames.Where(f => f.Value.Count == 0 || now - f.Value.Last().At > Window).Select(f => f.Key).ToList())
                _frames.Remove(stale);

            if (!_frames.TryGetValue((int)pid, out var queue) || queue.Count < 2) return null;
            var average = queue.Average(f => f.Ms);
            return average > 0 ? 1000 / average : null;
        }
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        State = FpsState.Stopped;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill();
            process.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Arrêt de PresentMon", ex);
        }
        lock (_gate) _frames.Clear();
    }

    /// <summary>
    /// Ajoute le compte au groupe « Utilisateurs du journal de performances » (une confirmation
    /// administrateur). Le nom du compte est passé depuis la session courante : l'invite peut ouvrir
    /// la commande sous un autre compte administrateur. Vrai si la commande a réussi.
    /// </summary>
    public static async Task<bool> RequestPermissionAsync()
    {
        var user = WindowsIdentity.GetCurrent().Name; // DOMAINE\nom de la session, pas celui de l'administrateur
        var command = $"Add-LocalGroupMember -SID '{PerformanceLogUsersSid}' -Member '{user.Replace("'", "''")}' -ErrorAction SilentlyContinue; " +
                      $"if (Get-LocalGroupMember -SID '{PerformanceLogUsersSid}' | Where-Object Name -eq '{user.Replace("'", "''")}') {{ exit 0 }} else {{ exit 1 }}";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{command}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)); // le temps de taper le mot de passe
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // confirmation administrateur refusée
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
