using System.Runtime.InteropServices;
using System.Text;
using SysDeck.Infrastructure;
using static SysDeck.Services.ConsoleMode.DisplayNative;

namespace SysDeck.Services.ConsoleMode;

/// <summary>
/// Topologie des écrans (API CCD de user32) : liste des écrans, capture / restauration de la configuration,
/// activation exclusive d'un écran (la TV) et désactivation. Aucune méthode ne lève d'exception :
/// les échecs sont journalisés et renvoyés sous forme de <see cref="DisplayResult"/>.
/// </summary>
public sealed class DisplayTopology
{
    private const uint Invalid = DisplaySnapshot.InvalidIndex;

    // ---------- Lecture ----------

    /// <summary>Écrans connectés (actifs et inactifs), sans doublon.</summary>
    public List<DisplayTarget> ListTargets()
    {
        var result = new List<DisplayTarget>();
        try
        {
            if (!Query(QdcAllPaths, out var allPaths, out _, out var err))
            {
                Log.Warn($"Écrans : QueryDisplayConfig(ALL_PATHS) a échoué (code {err}).");
                return result;
            }
            Query(QdcOnlyActivePaths, out var actPaths, out var actModes, out _);
            actPaths ??= Array.Empty<PathInfo>();
            actModes ??= Array.Empty<ModeInfo>();

            var active = new Dictionary<(long, uint), (string? Gdi, DisplayBounds? Bounds)>();
            foreach (var p in actPaths)
            {
                string? gdi = GetSourceGdiName(p.SourceAdapter, p.SourceId);
                DisplayBounds? bounds = null;
                if (p.SourceModeIdx != Invalid && p.SourceModeIdx < actModes.Length && actModes[p.SourceModeIdx].InfoType == ModeSource)
                {
                    var m = actModes[p.SourceModeIdx];
                    bounds = new DisplayBounds((int)m.D3, (int)m.D4, (int)m.D0, (int)m.D1);
                }
                active[(p.TargetAdapter.ToLong(), p.TargetId)] = (gdi, bounds);
            }

            var seen = new HashSet<(long, uint)>();
            foreach (var p in allPaths)
            {
                var key = (p.TargetAdapter.ToLong(), p.TargetId);
                bool isActive = active.ContainsKey(key);
                if (p.TargetAvailable == 0 && !isActive) continue;
                if (!seen.Add(key)) continue;

                var (name, devPath) = GetTargetName(p.TargetAdapter, p.TargetId);
                if (string.IsNullOrWhiteSpace(name)) name = $"Écran {p.TargetId}";
                active.TryGetValue(key, out var info);
                result.Add(new DisplayTarget(DisplayTargetId.Make(key.Item1, key.Item2, devPath), name, devPath,
                    p.TargetId, key.Item1, p.OutputTechnology, isActive, info.Gdi, info.Bounds));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Écrans : liste impossible", ex);
        }
        return result;
    }

    /// <summary>Capture la configuration active complète ; null en cas d'échec.</summary>
    public DisplaySnapshot? Snapshot()
    {
        try
        {
            if (!Query(QdcOnlyActivePaths, out var paths, out var modes, out var err))
            {
                Log.Warn($"Écrans : capture impossible (code {err}).");
                return null;
            }
            return DisplaySnapshot.FromNative(paths, modes, p =>
            {
                var (name, path) = GetTargetName(p.TargetAdapter, p.TargetId);
                return (path, name);
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Écrans : capture impossible", ex);
            return null;
        }
    }

    // ---------- Écriture ----------

    /// <summary>N'active QUE l'écran demandé (les autres sont désactivés), à sa résolution préférée, en principal.</summary>
    public DisplayResult ActivateOnly(string targetId)
    {
        try
        {
            if (!Query(QdcAllPaths, out var all, out var allModes, out var err))
                return DisplayResult.Fail(err, $"Lecture des écrans impossible (code {err}).");

            var path = FindPath(all, targetId);
            if (path is null) return DisplayResult.Fail(1168, "Écran introuvable (ERROR_NOT_FOUND).");
            var p = path.Value;

            // Candidat A : chemin seul, avec résolution préférée imposée (source en 0,0 = principal).
            var attempts = new List<(PathInfo[] Paths, ModeInfo[]? Modes, uint Flags, string Label)>();
            var preferred = GetPreferredMode(p.TargetAdapter, p.TargetId);
            var pa = p; pa.Flags |= PathActive;
            if (preferred is { } pref)
            {
                var src = new ModeInfo { InfoType = ModeSource, Id = p.SourceId, AdapterId = p.SourceAdapter, D0 = pref.W, D1 = pref.H, D2 = 4 /* 32 bpp */ };
                var tgt = pref.Target; tgt.InfoType = ModeTarget; tgt.Id = p.TargetId; tgt.AdapterId = p.TargetAdapter;
                var pm = pa; pm.SourceModeIdx = 0; pm.TargetModeIdx = 1;
                attempts.Add((new[] { pm }, new[] { src, tgt }, SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase, "résolution préférée"));
            }
            // Candidat B : modes laissés à Windows.
            var pb = pa; pb.SourceModeIdx = Invalid; pb.TargetModeIdx = Invalid;
            attempts.Add((new[] { pb }, null, SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase, "modes automatiques"));
            attempts.Add((new[] { pb }, null, SdcUseSuppliedDisplayConfig | SdcAllowChanges, "modes automatiques sans base"));
            // Candidat C : topologie seule.
            attempts.Add((new[] { pb }, null, SdcTopologySupplied | SdcAllowChanges, "topologie imposée"));

            int last = 0;
            foreach (var a in attempts)
            {
                last = ValidateAndApply(a.Paths, a.Modes, a.Flags);
                if (last == 0)
                {
                    Log.Info($"Écrans : écran seul activé ({a.Label}).");
                    return DisplayResult.Ok(a.Label);
                }
                Log.Warn($"Écrans : activation seule refusée ({a.Label}, code {last}).");
            }
            return DisplayResult.Fail(last, $"Windows a refusé d'activer l'écran seul (code {last}).");
        }
        catch (Exception ex)
        {
            Log.Warn("Écrans : activation seule impossible", ex);
            return DisplayResult.Fail(Marshal.GetLastWin32Error(), ex.Message);
        }
    }

    /// <summary>Temps laissé à un écran en veille (DisplayPort coupé faute de signal) pour réapparaître.</summary>
    private static readonly TimeSpan MissingWait = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Remet exactement la configuration capturée ; vérifie après coup et réessaie.
    /// Un écran absent (souvent un écran DisplayPort en veille après la perte du signal) est attendu :
    /// les écrans présents sont rallumés tout de suite, puis la configuration complète est réappliquée
    /// dès que l'écran manquant réapparaît.
    /// </summary>
    public DisplayResult Restore(DisplaySnapshot snapshot)
    {
        try
        {
            int last = 0;
            var waited = false;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var remapped = snapshot.RemapTo(ListTargets(), out var missing);
                if (missing.Count > 0 && attempt > 1 && !waited)
                {
                    waited = true;
                    Log.Info("Écrans : attente de " + string.Join(", ", missing) + " (veille ?)");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.Elapsed < MissingWait)
                    {
                        Thread.Sleep(500);
                        remapped = snapshot.RemapTo(ListTargets(), out missing);
                        if (missing.Count == 0) { Log.Info($"Écrans : écran revenu après {sw.Elapsed.TotalSeconds:0.0} s."); break; }
                    }
                }
                if (missing.Count > 0) Log.Warn("Écrans : écran(s) absent(s) à la restauration : " + string.Join(", ", missing));
                if (remapped is null) return DisplayResult.Fail(1168, "Aucun écran de la configuration sauvegardée n'est connecté.");

                var (paths, modes) = remapped.ToNative();
                last = ValidateAndApply(paths, modes, SdcUseSuppliedDisplayConfig | SdcSaveToDatabase | SdcNoOptimization);
                if (last != 0)
                    last = ValidateAndApply(paths, modes, SdcUseSuppliedDisplayConfig | SdcSaveToDatabase | SdcAllowChanges);
                if (last != 0)
                    Log.Warn($"Écrans : restauration refusée (essai {attempt}, code {last}).");

                Thread.Sleep(1200);
                var now = Snapshot();
                if (last == 0 && now is not null && now.Signature().SetEquals(remapped.Signature()))
                {
                    if (missing.Count == 0)
                    {
                        Log.Info($"Écrans : configuration restaurée (essai {attempt}).");
                        return DisplayResult.Ok();
                    }
                    // Écrans présents rallumés ; on repasse pour attendre l'écran manquant.
                    if (waited) break;
                    continue;
                }
                if (last == 0) Log.Warn($"Écrans : la configuration restaurée diffère de celle attendue (essai {attempt}).");
            }
            if (last == 0 && waited)
            {
                Log.Warn("Écrans : un écran n'est pas revenu à temps ; les autres sont rétablis.");
                return DisplayResult.Fail(1168, "Un écran n'est pas revenu (en veille ou éteint ?).");
            }
            return DisplayResult.Fail(last, last == 0 ? "La configuration restaurée ne correspond pas à la sauvegarde." : $"Restauration refusée (code {last}).");
        }
        catch (Exception ex)
        {
            Log.Warn("Écrans : restauration impossible", ex);
            return DisplayResult.Fail(Marshal.GetLastWin32Error(), ex.Message);
        }
    }

    /// <summary>Indique si la configuration actuelle est celle du snapshot (comparaison indépendante du LUID).</summary>
    public bool Matches(DisplaySnapshot snapshot)
    {
        var now = Snapshot();
        var remapped = snapshot.RemapTo(ListTargets(), out _);
        return now is not null && remapped is not null && now.Signature().SetEquals(remapped.Signature());
    }

    /// <summary>Désactive l'écran demandé en gardant le reste de la configuration active.</summary>
    public DisplayResult Deactivate(string targetId)
    {
        try
        {
            if (!Query(QdcOnlyActivePaths, out var paths, out var modes, out var err))
                return DisplayResult.Fail(err, $"Lecture des écrans impossible (code {err}).");

            var keep = paths.Where(p => !MatchesPath(targetId, p)).ToList();
            if (keep.Count == paths.Length) return DisplayResult.Ok("déjà inactif");
            if (keep.Count == 0) return DisplayResult.Fail(87, "Refus : ce serait le dernier écran actif.");

            var (newPaths, newModes) = Compact(keep, modes);
            NormalizeOrigin(newPaths, newModes);
            int rc = ValidateAndApply(newPaths, newModes, SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase);
            if (rc != 0)
            {
                Log.Warn($"Écrans : désactivation refusée (code {rc}).");
                return DisplayResult.Fail(rc, $"Windows a refusé de désactiver l'écran (code {rc}).");
            }
            Log.Info("Écrans : écran désactivé.");
            return DisplayResult.Ok();
        }
        catch (Exception ex)
        {
            Log.Warn("Écrans : désactivation impossible", ex);
            return DisplayResult.Fail(Marshal.GetLastWin32Error(), ex.Message);
        }
    }

    // ---------- Pur (testé) ----------

    /// <summary>Ne garde que les modes référencés par les chemins et corrige les indices.</summary>
    public static (PathInfo[] Paths, ModeInfo[] Modes) Compact(IReadOnlyList<PathInfo> paths, IReadOnlyList<ModeInfo> modes)
    {
        var outModes = new List<ModeInfo>();
        var outPaths = new PathInfo[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            var p = paths[i];
            p.SourceModeIdx = Take(p.SourceModeIdx);
            p.TargetModeIdx = Take(p.TargetModeIdx);
            outPaths[i] = p;
        }
        return (outPaths, outModes.ToArray());

        uint Take(uint idx)
        {
            if (idx == Invalid || idx >= modes.Count) return Invalid;
            outModes.Add(modes[(int)idx]);
            return (uint)(outModes.Count - 1);
        }
    }

    /// <summary>Si aucun écran n'est en (0,0) après retrait d'un écran, décale tout pour qu'un écran le soit.</summary>
    public static void NormalizeOrigin(PathInfo[] paths, ModeInfo[] modes)
    {
        var sources = paths.Select(p => p.SourceModeIdx).Where(i => i != Invalid && i < modes.Length && modes[i].InfoType == ModeSource).ToList();
        if (sources.Count == 0 || sources.Any(i => (int)modes[i].D3 == 0 && (int)modes[i].D4 == 0)) return;
        int minX = sources.Min(i => (int)modes[i].D3), minY = sources.Min(i => (int)modes[i].D4);
        foreach (var i in sources)
        {
            modes[i].D3 = (uint)((int)modes[i].D3 - minX);
            modes[i].D4 = (uint)((int)modes[i].D4 - minY);
        }
    }

    // ---------- Interne ----------

    private static PathInfo? FindPath(PathInfo[] all, string targetId)
    {
        PathInfo? best = null;
        foreach (var p in all)
        {
            if (!MatchesPath(targetId, p)) continue;
            if ((p.Flags & PathActive) != 0) return p;
            if (best is null && p.TargetAvailable != 0) best = p;
        }
        return best;
    }

    private static bool MatchesPath(string targetId, PathInfo p)
    {
        var (_, devPath) = GetTargetName(p.TargetAdapter, p.TargetId);
        return DisplayTargetId.Matches(targetId, p.TargetAdapter.ToLong(), p.TargetId, devPath);
    }

    /// <summary>Teste la configuration (SDC_VALIDATE) puis l'applique ; renvoie 0 ou le code d'erreur Win32.</summary>
    private static int ValidateAndApply(PathInfo[] paths, ModeInfo[]? modes, uint flags)
    {
        uint nm = modes is null ? 0 : (uint)modes.Length;
        int rc = SetDisplayConfig((uint)paths.Length, paths, nm, modes, SdcValidate | flags);
        if (rc != 0) return rc;
        return SetDisplayConfig((uint)paths.Length, paths, nm, modes, SdcApply | flags);
    }

    private static bool Query(uint flags, out PathInfo[] paths, out ModeInfo[] modes, out int error)
    {
        paths = Array.Empty<PathInfo>();
        modes = Array.Empty<ModeInfo>();
        error = 0;
        for (int i = 0; i < 6; i++)
        {
            error = GetDisplayConfigBufferSizes(flags, out var np, out var nm);
            if (error != 0) return false;
            var p = new PathInfo[np];
            var m = new ModeInfo[nm];
            error = QueryDisplayConfig(flags, ref np, p, ref nm, m, IntPtr.Zero);
            if (error == ErrorInsufficientBuffer) continue;
            if (error != 0) return false;
            Array.Resize(ref p, (int)np);
            Array.Resize(ref m, (int)nm);
            paths = p; modes = m;
            return true;
        }
        return false;
    }

    // DisplayConfigGetDeviceInfo avec un tampon brut : en-tête (type, taille, LUID, id) puis données.
    private static byte[]? GetInfo(int type, Luid adapter, uint id, int size)
    {
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            var zero = new byte[size];
            Marshal.Copy(zero, 0, buf, size);
            Marshal.WriteInt32(buf, 0, type);
            Marshal.WriteInt32(buf, 4, size);
            Marshal.WriteInt32(buf, 8, (int)adapter.LowPart);
            Marshal.WriteInt32(buf, 12, adapter.HighPart);
            Marshal.WriteInt32(buf, 16, (int)id);
            if (DisplayConfigGetDeviceInfo(buf) != 0) return null;
            var res = new byte[size];
            Marshal.Copy(buf, res, 0, size);
            return res;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string ReadWide(byte[] data, int offset, int chars)
    {
        var s = Encoding.Unicode.GetString(data, offset, chars * 2);
        int z = s.IndexOf('\0');
        return z >= 0 ? s[..z] : s;
    }

    private static (string Name, string DevicePath) GetTargetName(Luid adapter, uint id)
    {
        var d = GetInfo(2, adapter, id, 420);
        return d is null ? ("", "") : (ReadWide(d, 36, 64), ReadWide(d, 164, 128));
    }

    private static string? GetSourceGdiName(Luid adapter, uint id)
    {
        var d = GetInfo(1, adapter, id, 84);
        return d is null ? null : ReadWide(d, 20, 32);
    }

    private static (uint W, uint H, ModeInfo Target)? GetPreferredMode(Luid adapter, uint id)
    {
        var d = GetInfo(3, adapter, id, 76);
        if (d is null) return null;
        uint w = BitConverter.ToUInt32(d, 20), h = BitConverter.ToUInt32(d, 24);
        if (w == 0 || h == 0) return null;
        var t = new ModeInfo
        {
            D0 = BitConverter.ToUInt32(d, 28), D1 = BitConverter.ToUInt32(d, 32), D2 = BitConverter.ToUInt32(d, 36), D3 = BitConverter.ToUInt32(d, 40),
            D4 = BitConverter.ToUInt32(d, 44), D5 = BitConverter.ToUInt32(d, 48), D6 = BitConverter.ToUInt32(d, 52), D7 = BitConverter.ToUInt32(d, 56),
            D8 = BitConverter.ToUInt32(d, 60), D9 = BitConverter.ToUInt32(d, 64), D10 = BitConverter.ToUInt32(d, 68), D11 = BitConverter.ToUInt32(d, 72),
        };
        return (w, h, t);
    }
}
