using System.Runtime.InteropServices;

namespace SysDeck.Services.ConsoleMode;

/// <summary>Rectangle d'un écran sur le bureau virtuel (pixels).</summary>
public sealed record DisplayBounds(int X, int Y, int Width, int Height);

/// <summary>Écran connecté (actif ou non) tel que listé par Windows.</summary>
/// <param name="Id">Identifiant stable « luid|targetId|devicePath » (voir <see cref="DisplayTargetId"/>).</param>
public sealed record DisplayTarget(
    string Id, string Name, string DevicePath, uint TargetId, long AdapterLuid,
    uint OutputTechnology, bool IsActive, string? GdiName, DisplayBounds? Bounds);

/// <summary>Résultat d'une opération sur la topologie d'écrans.</summary>
public sealed record DisplayResult(bool Success, int ErrorCode, string Message)
{
    public static DisplayResult Ok(string message = "OK") => new(true, 0, message);
    public static DisplayResult Fail(int code, string message) => new(false, code, message);
}

/// <summary>Construction et reconnaissance des identifiants d'écran (pur, testé).</summary>
public static class DisplayTargetId
{
    public static string Make(long luid, uint targetId, string devicePath)
        => $"{luid:X16}|{targetId}|{devicePath}";

    public static bool TryParse(string? id, out long luid, out uint targetId, out string devicePath)
    {
        luid = 0; targetId = 0; devicePath = "";
        if (string.IsNullOrWhiteSpace(id)) return false;
        var parts = id.Split('|', 3);
        if (parts.Length != 3) return false;
        if (!long.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out luid)) return false;
        if (!uint.TryParse(parts[1], out targetId)) return false;
        devicePath = parts[2];
        return true;
    }

    /// <summary>Vrai si l'identifiant désigne cet écran : le chemin du moniteur prime (le LUID change au redémarrage).</summary>
    public static bool Matches(string? id, long luid, uint targetId, string devicePath)
    {
        if (!TryParse(id, out var l, out var t, out var path)) return false;
        if (path.Length > 0 && devicePath.Length > 0)
            return string.Equals(path, devicePath, StringComparison.OrdinalIgnoreCase);
        return l == luid && t == targetId;
    }
}

/// <summary>Un chemin (source vers écran) de la configuration, sous forme brute + clé de reconnaissance.</summary>
public sealed class DisplayPathRecord
{
    /// <summary>DISPLAYCONFIG_PATH_INFO brut (72 octets).</summary>
    public byte[] Raw { get; set; } = Array.Empty<byte>();
    /// <summary>monitorDevicePath de l'écran cible : sert à retrouver l'écran si le LUID a changé.</summary>
    public string DevicePath { get; set; } = "";
    public string FriendlyName { get; set; } = "";
}

/// <summary>
/// Configuration d'affichage active complète (chemins + modes), sérialisable en JSON.
/// Les structures natives sont conservées en octets bruts (base64 dans le JSON).
/// </summary>
public sealed class DisplaySnapshot
{
    public int Version { get; set; } = 1;
    public DateTime Created { get; set; } = DateTime.Now;
    public List<DisplayPathRecord> Paths { get; set; } = new();
    /// <summary>DISPLAYCONFIG_MODE_INFO bruts (64 octets chacun).</summary>
    public List<byte[]> Modes { get; set; } = new();

    public const uint InvalidIndex = 0xFFFFFFFF;

    public static T ReadStruct<T>(byte[] raw) where T : struct => MemoryMarshal.Read<T>(raw);

    public static byte[] ToBytes<T>(T value) where T : struct
    {
        var bytes = new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(bytes, in value);
        return bytes;
    }

    public static DisplaySnapshot FromNative(IReadOnlyList<DisplayNative.PathInfo> paths, IReadOnlyList<DisplayNative.ModeInfo> modes,
        Func<DisplayNative.PathInfo, (string Path, string Name)> describe)
    {
        var snap = new DisplaySnapshot();
        foreach (var p in paths)
        {
            var (path, name) = describe(p);
            snap.Paths.Add(new DisplayPathRecord { Raw = ToBytes(p), DevicePath = path, FriendlyName = name });
        }
        foreach (var m in modes) snap.Modes.Add(ToBytes(m));
        return snap;
    }

    /// <summary>Reconstitue les tableaux natifs à passer à SetDisplayConfig.</summary>
    public (DisplayNative.PathInfo[] Paths, DisplayNative.ModeInfo[] Modes) ToNative()
        => (Paths.Select(p => ReadStruct<DisplayNative.PathInfo>(p.Raw)).ToArray(),
            Modes.Select(m => ReadStruct<DisplayNative.ModeInfo>(m)).ToArray());

    /// <summary>
    /// Signature indépendante du LUID : un texte par écran actif (chemin du moniteur, position, taille, rotation).
    /// Sert à vérifier qu'une configuration appliquée est bien celle voulue.
    /// </summary>
    public HashSet<string> Signature()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in Paths)
        {
            if (rec.Raw.Length < Marshal.SizeOf<DisplayNative.PathInfo>()) continue;
            var p = ReadStruct<DisplayNative.PathInfo>(rec.Raw);
            if ((p.Flags & DisplayNative.PathActive) == 0) continue;
            var idx = p.SourceModeIdx;
            string geo = "?";
            if (idx != InvalidIndex && idx < Modes.Count && Modes[(int)idx].Length >= 64)
            {
                var m = ReadStruct<DisplayNative.ModeInfo>(Modes[(int)idx]);
                geo = $"{(int)m.D3},{(int)m.D4}|{m.D0}x{m.D1}";
            }
            set.Add($"{rec.DevicePath}|{geo}|r{p.Rotation}");
        }
        return set;
    }

    /// <summary>
    /// Adapte le snapshot aux écrans actuellement connus : les LUID d'adaptateur ayant pu changer (redémarrage,
    /// pilote), chaque chemin est reconnu par son monitorDevicePath. Les chemins dont l'écran a disparu sont
    /// retirés, et les modes inutilisés élagués. Renvoie null s'il ne reste aucun chemin.
    /// </summary>
    public DisplaySnapshot? RemapTo(IReadOnlyList<DisplayTarget> current, out List<string> missing)
    {
        missing = new List<string>();
        var luidMap = new Dictionary<long, long>();
        var keptPaths = new List<(DisplayNative.PathInfo Path, DisplayPathRecord Rec)>();

        foreach (var rec in Paths)
        {
            if (rec.Raw.Length < Marshal.SizeOf<DisplayNative.PathInfo>()) continue;
            var p = ReadStruct<DisplayNative.PathInfo>(rec.Raw);
            var oldLuid = p.TargetAdapter.ToLong();

            DisplayTarget? match = null;
            if (rec.DevicePath.Length > 0)
                match = current.FirstOrDefault(t => string.Equals(t.DevicePath, rec.DevicePath, StringComparison.OrdinalIgnoreCase)
                                                    && t.TargetId == p.TargetId)
                        ?? current.FirstOrDefault(t => string.Equals(t.DevicePath, rec.DevicePath, StringComparison.OrdinalIgnoreCase));
            match ??= current.FirstOrDefault(t => t.AdapterLuid == oldLuid && t.TargetId == p.TargetId);
            if (match is null)
            {
                missing.Add(string.IsNullOrEmpty(rec.FriendlyName) ? rec.DevicePath : rec.FriendlyName);
                continue;
            }
            luidMap[oldLuid] = match.AdapterLuid;
            p.TargetAdapter = DisplayNative.Luid.From(match.AdapterLuid);
            p.TargetId = match.TargetId;
            keptPaths.Add((p, rec));
        }
        if (keptPaths.Count == 0) return null;

        long Map(long l) => luidMap.TryGetValue(l, out var n) ? n : l;

        var result = new DisplaySnapshot { Version = Version, Created = Created };
        foreach (var (p0, rec) in keptPaths)
        {
            var p = p0;
            p.SourceAdapter = DisplayNative.Luid.From(Map(p.SourceAdapter.ToLong()));
            // Un nouveau LUID est appliqué aux modes : on copie les modes référencés avec l'adaptateur corrigé.
            p.SourceModeIdx = CopyMode(p.SourceModeIdx, result, Map);
            p.TargetModeIdx = CopyMode(p.TargetModeIdx, result, Map);
            result.Paths.Add(new DisplayPathRecord { Raw = ToBytes(p), DevicePath = rec.DevicePath, FriendlyName = rec.FriendlyName });
        }
        return result;
    }

    // Copie le mode d'indice idx (de this) dans result avec le LUID corrigé ; renvoie son nouvel indice.
    private uint CopyMode(uint idx, DisplaySnapshot result, Func<long, long> map)
    {
        if (idx == InvalidIndex || idx >= Modes.Count || Modes[(int)idx].Length < 64) return InvalidIndex;
        var m = ReadStruct<DisplayNative.ModeInfo>(Modes[(int)idx]);
        m.AdapterId = DisplayNative.Luid.From(map(m.AdapterId.ToLong()));
        result.Modes.Add(ToBytes(m));
        return (uint)(result.Modes.Count - 1);
    }
}
