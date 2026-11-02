using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SysDeck.Services;

public static class IconExtractor
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new();

    /// <summary>Icône choisie manuellement par l'utilisateur : chargée telle quelle, sans passer par
    /// la détection automatique (l'utilisateur a déjà fait le tri).</summary>
    public static ImageSource? GetCustomIcon(string imagePath)
    {
        var cacheKey = "custom:" + imagePath;
        if (Cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var result = TryLoadImageFile(imagePath);
        Cache[cacheKey] = result;
        return result;
    }

    public static ImageSource? GetIcon(string path)
    {
        // Raccourci clavier ou entrée sans programme : pas d'icône à détecter.
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Cache.TryGetValue(path, out var cached))
            return cached;

        ImageSource? raw = null;

        // Certains raccourcis (jeux Xbox/Game Pass notamment) référencent volontairement un petit
        // logo ("SmallLogo.ico", ~44px) comme icône de raccourci. Le dossier de ressources du jeu
        // contient presque toujours une version bien plus grande à côté (ex : Square480x480Logo.png)
        // — on la préfère quand elle existe plutôt que de recadrer/agrandir la petite version.
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var iconLocation = ResolveShortcutIconLocation(path);
            var largerSibling = iconLocation is not null ? TryFindLargerSiblingLogo(iconLocation) : null;
            if (largerSibling is not null)
                raw = TryLoadImageFile(largerSibling);
        }

        raw ??= TryGetHighResIcon(path) ?? TryGetLowResIcon(path);
        var result = raw is BitmapSource bitmap ? TrimTransparentPadding(bitmap) : raw;

        Cache[path] = result;
        return result;
    }

    /// <summary>
    /// Icône d'un processus en cours d'exécution (mixeur de volume), appelable hors thread UI : l'image
    /// renvoyée est figée. Ordre : logo du paquet (applications Store/Xbox), icône de l'exécutable,
    /// icône de la fenêtre principale ; null si rien ne marche (l'appelant affiche alors un glyphe).
    /// Mis en cache par chemin d'exécutable ; sans chemin, rien n'est mémorisé ici (le PID peut être réutilisé).
    /// </summary>
    public static ImageSource? GetProcessIcon(int pid, string? exePath, string? packagePath)
    {
        var cacheKey = string.IsNullOrWhiteSpace(exePath) ? null : "proc:" + exePath.ToLowerInvariant();
        if (cacheKey is not null && Cache.TryGetValue(cacheKey, out var cached) && cached is not null)
            return cached;

        ImageSource? result = null;
        var packaged = packagePath is not null
            || (exePath?.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ?? false);

        if (packaged)
            result = TryGetPackageLogo(packagePath, exePath);

        if (result is null && !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            var raw = TryGetHighResIcon(exePath) ?? TryGetLowResIcon(exePath);
            result = raw is BitmapSource bitmap ? TrimTransparentPadding(bitmap) : raw;
        }

        result ??= TryGetWindowIcon(pid);

        // Un échec n'est pas mémorisé : l'exécutable peut devenir lisible plus tard (fenêtre créée après coup).
        if (cacheKey is not null && result is not null)
            Cache[cacheKey] = result;
        return result;
    }

    /// <summary>
    /// Logo d'une application empaquetée, lu dans AppxManifest.xml (VisualElements : Square44x44Logo, sinon
    /// Square150x150Logo). Le manifeste est cherché dans le dossier du paquet, sinon dans le dossier de
    /// l'exécutable et ses parents (jeux Xbox : l'exe est dans "Content").
    /// </summary>
    private static ImageSource? TryGetPackageLogo(string? packagePath, string? exePath)
    {
        try
        {
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(packagePath)) roots.Add(packagePath);
            var dir = string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath);
            for (var i = 0; i < 3 && !string.IsNullOrEmpty(dir); i++, dir = Path.GetDirectoryName(dir))
                roots.Add(dir);

            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var manifest = Path.Combine(root, "AppxManifest.xml");
                if (!File.Exists(manifest)) continue;

                var visual = System.Xml.Linq.XDocument.Load(manifest).Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "VisualElements");
                if (visual is null) continue;

                foreach (var attribute in new[] { "Square44x44Logo", "Square150x150Logo", "Logo" })
                {
                    var relative = visual.Attribute(attribute)?.Value;
                    if (string.IsNullOrWhiteSpace(relative)) continue;
                    var file = FindBestLogoVariant(Path.Combine(root, relative.Replace('/', '\\')));
                    if (file is null) continue;
                    var image = TryLoadImageFile(file);
                    if (image is null) continue;
                    return image is BitmapSource bitmap ? TrimTransparentPadding(bitmap) : image;
                }
            }
        }
        catch
        {
            // manifeste illisible ou dossier inaccessible : on passe à l'icône de l'exécutable
        }
        return null;
    }

    /// <summary>
    /// Le manifeste désigne "Assets\Square44x44Logo.png" mais les fichiers réels portent un suffixe
    /// (".scale-200", ".targetsize-48"…) : retient la plus grande variante existante, hors thème contraste élevé.
    /// </summary>
    private static string? FindBestLogoVariant(string declaredPath)
    {
        var dir = Path.GetDirectoryName(declaredPath);
        if (dir is null || !Directory.Exists(dir)) return null;

        var stem = Path.GetFileNameWithoutExtension(declaredPath);
        var extension = Path.GetExtension(declaredPath);
        var best = Directory.EnumerateFiles(dir, stem + "*" + extension)
            .Where(f => !Path.GetFileName(f).Contains("contrast", StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var match = Regex.Match(Path.GetFileNameWithoutExtension(f), @"(?:scale|targetsize)-(\d+)", RegexOptions.IgnoreCase);
                var size = match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
                // scale-N : N est un pourcentage (200 = 2x) ; targetsize-N : N est en pixels.
                var pixels = match.Success && match.Value.StartsWith("scale", StringComparison.OrdinalIgnoreCase)
                    ? size * 44 / 100 : size;
                return (Path: f, Pixels: pixels);
            })
            .OrderByDescending(t => t.Pixels)
            .Select(t => t.Path)
            .FirstOrDefault();

        return best ?? (File.Exists(declaredPath) ? declaredPath : null);
    }

    /// <summary>Dernier recours : icône de la première fenêtre visible du processus (WM_GETICON, sinon icône de classe).</summary>
    private static ImageSource? TryGetWindowIcon(int pid)
    {
        try
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var windowPid);
                if (windowPid != (uint)pid || !IsWindowVisible(hwnd)) return true;
                found = hwnd;
                return false;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) return null;

            // Délai court : une fenêtre figée ne doit pas bloquer le rafraîchissement du mixeur.
            const uint flags = 0x0002 | 0x0001; // SMTO_ABORTIFHUNG | SMTO_BLOCK
            IntPtr icon = IntPtr.Zero;
            foreach (var kind in new IntPtr[] { (IntPtr)1 /* ICON_BIG */, (IntPtr)2 /* ICON_SMALL2 */, IntPtr.Zero /* ICON_SMALL */ })
            {
                if (SendMessageTimeout(found, 0x007F /* WM_GETICON */, kind, IntPtr.Zero, flags, 100, out icon) != IntPtr.Zero
                    && icon != IntPtr.Zero)
                    break;
                icon = IntPtr.Zero;
            }
            if (icon == IntPtr.Zero) icon = GetClassLongPtrW(found, -14 /* GCLP_HICON */);
            if (icon == IntPtr.Zero) icon = GetClassLongPtrW(found, -34 /* GCLP_HICONSM */);
            if (icon == IntPtr.Zero) return null;

            // Le handle appartient à la fenêtre : on le copie en image sans le détruire.
            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtrW(IntPtr hwnd, int index);

    private static ImageSource? TryLoadImageFile(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveShortcutIconLocation(string lnkPath)
    {
        var iconLocation = ReadShortcut(lnkPath, shortcut => shortcut.IconLocation); // format "chemin,index"
        if (string.IsNullOrWhiteSpace(iconLocation)) return null;

        var commaIndex = iconLocation.LastIndexOf(',');
        return commaIndex > 0 ? iconLocation[..commaIndex] : iconLocation;
    }

    /// <summary>
    /// Lit une propriété d'un raccourci .lnk via WScript.Shell, en libérant les objets COM tout de suite :
    /// sans cela ils ne partent qu'au prochain passage du ramasse-miettes, et chaque appel en laisse deux.
    /// </summary>
    private static string? ReadShortcut(string lnkPath, Func<dynamic, string?> read)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            shell = Activator.CreateInstance(shellType)!;
            shortcut = ((dynamic)shell).CreateShortcut(lnkPath);
            return read(shortcut);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Cherche, dans le même dossier que l'icône référencée, un autre fichier "*Logo*" de plus
    /// grande résolution — seulement si l'icône référencée a l'air d'être délibérément une petite
    /// version (nom contenant "Small"), pour ne pas s'appliquer aux icônes déjà correctement choisies.
    /// </summary>
    private static string? TryFindLargerSiblingLogo(string smallIconPath)
    {
        try
        {
            var fileName = Path.GetFileName(smallIconPath);
            if (!fileName.Contains("Small", StringComparison.OrdinalIgnoreCase))
                return null;

            var dir = Path.GetDirectoryName(smallIconPath);
            if (dir is null || !Directory.Exists(dir)) return null;

            return Directory.EnumerateFiles(dir, "*Logo*.*")
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                .Select(f => (Path: f, Score: LogoSizeHint(Path.GetFileNameWithoutExtension(f))))
                .Where(t => t.Score > 0)
                .OrderByDescending(t => t.Score)
                .Select(t => t.Path)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static int LogoSizeHint(string nameWithoutExtension)
    {
        if (nameWithoutExtension.Contains("Small", StringComparison.OrdinalIgnoreCase))
            return 0;

        var match = Regex.Match(nameWithoutExtension, @"(\d{2,4})");
        if (match.Success && int.TryParse(match.Value, out var size))
            return size;

        if (nameWithoutExtension.Equals("Logo", StringComparison.OrdinalIgnoreCase)) return 150;
        if (nameWithoutExtension.Contains("Store", StringComparison.OrdinalIgnoreCase)) return 100;
        return 50;
    }

    /// <summary>
    /// Certaines icônes (tuiles Xbox/Store, vignettes générées par le shell pour des .lnk personnalisés)
    /// contiennent une large marge autour du logo réel — parfois transparente (norme UWP), parfois une
    /// couleur unie opaque (souvent blanche, quand le shell renvoie une vignette plutôt qu'une icône).
    /// Dans les deux cas le logo n'occupe qu'une petite portion centrée, minuscule une fois affiché
    /// dans une case d'icône de taille fixe. On détecte la couleur de fond (coin haut-gauche) et on
    /// recadre sur la zone qui en diffère réellement, qu'elle soit délimitée par la transparence ou
    /// par un contraste de couleur.
    /// </summary>
    private static ImageSource TrimTransparentPadding(BitmapSource source)
    {
        try
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            if (width < 4 || height < 4) return source;

            int stride = width * 4;
            var pixels = new byte[height * stride];
            converted.CopyPixels(pixels, stride, 0);

            byte bgB = pixels[0], bgG = pixels[1], bgR = pixels[2], bgA = pixels[3];
            // Seuil volontairement généreux : certaines icônes composées par le shell portent un fin
            // liseré semi-transparent résiduel (ex : alpha ~38/255) autour du cadre, bien plus faible
            // que le contenu réel (typiquement proche de l'opacité totale) — un seuil trop bas le
            // ferait passer pour du contenu et empêcherait tout recadrage.
            const byte alphaThreshold = 80;
            const int colorThreshold = 24;
            bool bgIsTransparent = bgA <= alphaThreshold;

            bool IsBackground(int offset)
            {
                byte a = pixels[offset + 3];
                if (a <= alphaThreshold) return bgIsTransparent;
                if (bgIsTransparent) return false;

                var dist = Math.Abs(pixels[offset] - bgB) + Math.Abs(pixels[offset + 1] - bgG) + Math.Abs(pixels[offset + 2] - bgR);
                return dist <= colorThreshold;
            }

            // Compte les pixels de contenu par ligne et par colonne, plutôt que de se fier à un seul
            // pixel isolé : certaines icônes ont un artefact d'anti-aliasing d'un pixel de large en
            // bordure de l'image (résidu de la composition de l'icône par le shell), qui ferait sinon
            // croire — à tort — que le contenu réel s'étend jusqu'au bord et empêcherait tout recadrage.
            var rowCounts = new int[height];
            var colCounts = new int[width];

            for (var y = 0; y < height; y++)
            {
                var rowOffset = y * stride;
                for (var x = 0; x < width; x++)
                {
                    if (IsBackground(rowOffset + x * 4)) continue;
                    rowCounts[y]++;
                    colCounts[x]++;
                }
            }

            var minContentRun = Math.Max(2, (int)(Math.Min(width, height) * 0.01));
            var minY = Array.FindIndex(rowCounts, c => c >= minContentRun);
            var maxY = Array.FindLastIndex(rowCounts, c => c >= minContentRun);
            var minX = Array.FindIndex(colCounts, c => c >= minContentRun);
            var maxX = Array.FindLastIndex(colCounts, c => c >= minContentRun);

            if (minX < 0 || minY < 0 || maxX < minX || maxY < minY)
                return source; // rien de significatif détecté (bruit uniquement, ou image vide)

            var contentWidth = maxX - minX + 1;
            var contentHeight = maxY - minY + 1;

            // Contenu déjà proche du plein cadre : pas la peine de recadrer (évite un recadrage
            // inutile — et potentiellement imprécis — sur des icônes déjà correctement cadrées).
            if (contentWidth >= width * 0.92 && contentHeight >= height * 0.92)
                return source;

            // Petite marge de respiration autour du contenu détecté, pour ne pas couper trop juste.
            var padding = (int)(Math.Max(contentWidth, contentHeight) * 0.04);
            minX = Math.Max(0, minX - padding);
            minY = Math.Max(0, minY - padding);
            maxX = Math.Min(width - 1, maxX + padding);
            maxY = Math.Min(height - 1, maxY + padding);

            var cropped = new CroppedBitmap(converted, new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
            cropped.Freeze();
            return cropped;
        }
        catch
        {
            return source;
        }
    }

    /// <summary>
    /// Icône haute résolution (jusqu'à 256x256) via l'API shell moderne (celle qu'utilise
    /// l'Explorateur pour ses grandes icônes). Icon.ExtractAssociatedIcon (repli ci-dessous) ne
    /// renvoie qu'une petite icône système (souvent 32x32), floue/pixelisée une fois agrandie par
    /// l'effet de survol du dock (jusqu'à ~1,7x en plus de sa taille déjà affichée).
    /// </summary>
    private static ImageSource? TryGetHighResIcon(string path)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            var riid = typeof(IShellItemImageFactory).GUID;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref riid, out var factory);
            if (hr != 0 || factory is null)
                return null;

            factory.GetImage(new SIZE(256, 256), SIIGBF.RESIZETOFIT | SIIGBF.ICONONLY, out hbitmap);
            if (hbitmap == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
                DeleteObject(hbitmap);
        }
    }

    private static ImageSource? TryGetLowResIcon(string path)
    {
        try
        {
            var target = path;
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                target = ResolveShortcutTarget(path) ?? path;

            using var icon = Icon.ExtractAssociatedIcon(target);
            if (icon is null) return null;

            var result = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveShortcutTarget(string lnkPath)
    {
        var targetPath = ReadShortcut(lnkPath, shortcut => shortcut.TargetPath);
        return string.IsNullOrWhiteSpace(targetPath) ? null : targetPath;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [Flags]
    private enum SIIGBF
    {
        RESIZETOFIT = 0x00,
        ICONONLY = 0x04,
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }
}
