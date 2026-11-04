using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

/// <summary>Faux hôte : la « personnalisation courante » est un AppConfig en mémoire (aucune interface WPF).</summary>
internal sealed class FakeProfileHost : IProfileHost
{
    public AppConfig Config { get; } = new() { Theme = new ThemeSettings() };
    public Profile? Applied { get; private set; }
    public bool AppliedLayout { get; private set; }

    public Profile CaptureProfile() => Profile.Capture(Config);

    public void ApplyProfile(Profile profile, bool includeLayout)
    {
        Applied = profile;
        AppliedLayout = includeLayout;
        profile.ApplyTo(Config, includeLayout);
    }
}

public sealed class ProfileTests : IDisposable
{
    // PNG minimal : seule la signature est vérifiée à l'import, pas le décodage.
    private static readonly byte[] PngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8 };
    private static readonly byte[] FontBytes = { 0x00, 0x01, 0x00, 0x00, 9, 9, 9, 9, 9, 9 };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "SysDeckProfileTests-" + Guid.NewGuid().ToString("N"));

    public ProfileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* dossier temporaire */ }
    }

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static (FakeProfileHost Host, ProfileService Service) Create(string dataDir)
    {
        var host = new FakeProfileHost();
        return (host, new ProfileService(host, dataDir));
    }

    // ---------- Stockage ----------

    [Fact]
    public void SaveListRenameDelete()
    {
        var (host, service) = Create(Dir("a"));
        host.Config.Theme!.GaugeStartColor = "#FF112233";
        Assert.Empty(service.List());
        Assert.False(service.Exists("Gamer"));

        service.SaveCurrent("Gamer");
        service.SaveCurrent("Bureau");
        Assert.True(service.Exists("gamer")); // casse ignorée, comme le système de fichiers
        Assert.Equal(new[] { "Bureau", "Gamer" }, service.List().Select(p => p.Name).ToArray());

        host.Config.Theme.GaugeStartColor = "#FF445566";
        service.SaveCurrent("Gamer"); // écrase
        Assert.Equal(2, service.List().Count);
        Assert.Equal("#FF445566", service.Load("Gamer").Theme.GaugeStartColor);

        service.Rename("Gamer", "Jeux");
        Assert.False(service.Exists("Gamer"));
        Assert.Equal("Jeux", service.Load("Jeux").Name);
        Assert.Throws<ProfileException>(() => service.Rename("Jeux", "Bureau")); // nom pris
        Assert.Throws<ProfileException>(() => service.Rename("Absent", "X"));

        service.Delete("Jeux");
        service.Delete("Jeux"); // sans erreur
        Assert.Equal(new[] { "Bureau" }, service.List().Select(p => p.Name).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyNameIsRefused(string name)
    {
        var (_, service) = Create(Dir("a"));
        Assert.Throws<ProfileException>(() => service.SaveCurrent(name));
    }

    [Fact]
    public void UnsafeNamesBecomeSafeFileNames()
    {
        var (_, service) = Create(Dir("a"));
        service.SaveCurrent(@"..\evil:name*?");
        service.SaveCurrent("CON");
        Assert.Equal(2, service.List().Count);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "a", "Profiles"), "*.json").Length);
        Assert.False(File.Exists(Path.Combine(_root, "evil.json")));
    }

    [Fact]
    public void ProfileNeverContainsSecrets()
    {
        var (host, service) = Create(Dir("a"));
        host.Config.GeminiApiKey = "cle-secrete-123";
        host.Config.EmojiHotkey = "Ctrl+Shift+E";
        host.Config.Power.GaugeMaxWatts = 7771;
        service.SaveCurrent("P");
        var json = File.ReadAllText(Path.Combine(_root, "a", "Profiles", "P.json"));
        Assert.DoesNotContain("cle-secrete", json);
        Assert.DoesNotContain("Gemini", json);
        Assert.DoesNotContain("Ctrl+Shift+E", json);
        Assert.DoesNotContain("777", json);
    }

    [Fact]
    public void ProfileKeepsItsImageWhenModuleImageIsDeleted()
    {
        var dir = Dir("a");
        var (host, service) = Create(dir);
        var image = Path.Combine(Dir("a\\Backgrounds"), "fond.png");
        File.WriteAllBytes(image, PngBytes);
        host.Config.Modules["cpu"] = new ModuleAppearance { BackgroundImage = image };
        service.SaveCurrent("P");

        File.Delete(image); // l'utilisateur change le fond du module
        var loaded = service.Load("P");
        var restored = loaded.Modules["cpu"].BackgroundImage!;
        Assert.True(File.Exists(restored));
        Assert.Equal(PngBytes, File.ReadAllBytes(restored));
        Assert.NotEqual(image, restored);

        service.Delete("P"); // l'image n'est plus utilisée par aucun profil
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "Profiles", "Files")));
    }

    [Fact]
    public void ApplyHandsLoadedProfileToHost()
    {
        var (host, service) = Create(Dir("a"));
        host.Config.Theme!.TextColor = "#FF010203";
        service.SaveCurrent("P");
        host.Config.Theme.TextColor = "#FFFFFFFF";

        service.Apply("P", includeLayout: true);
        Assert.True(host.AppliedLayout);
        Assert.Equal("#FF010203", host.Config.Theme.TextColor);
    }

    // ---------- Export / import ----------

    [Fact]
    public void ExportThenImport_RoundTripWithImageAndFont()
    {
        var dirA = Dir("a");
        var (hostA, serviceA) = Create(dirA);
        var image = Path.Combine(Dir("a\\Backgrounds"), "x.png");
        File.WriteAllBytes(image, PngBytes);
        File.WriteAllBytes(Path.Combine(Dir("a\\Fonts"), "Cool.ttf"), FontBytes);
        hostA.Config.Theme!.TextFont = "@Cool.ttf#Cool Font";
        hostA.Config.Theme.GaugeEndColor = "#FFABCDEF";
        hostA.Config.Modules["gpu"] = new ModuleAppearance { BackgroundImage = image };
        hostA.Config.Deck.Modules["gpu"] = new ModuleAppearance { BackgroundImage = image };
        hostA.Config.Modules["gpu"].GetText("title").Font = "@Cool.ttf#Cool Font";
        hostA.Config.Deck.BackgroundImage = image;
        hostA.Config.Deck.BackgroundColor = "#112233";
        hostA.Config.Deck.BackgroundDim = 0.6;
        hostA.Config.Deck.ClockOpacity = 0.4;
        serviceA.SaveCurrent("Mon profil");

        var package = Path.Combine(dirA, "p" + ProfileService.PackageExtension);
        serviceA.Export("Mon profil", package);

        using (var zip = ZipFile.OpenRead(package))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("profile.json", names);
            Assert.Contains("Fonts/Cool.ttf", names);
            Assert.Equal(1, names.Count(n => n.StartsWith("Backgrounds/"))); // image identique (modules et fond Switch) : une seule fois
        }

        var dirB = Dir("b");
        var (_, serviceB) = Create(dirB);
        var imported = serviceB.Import(package);
        Assert.Equal("Mon profil", imported);
        Assert.Equal("Mon profil (2)", serviceB.Import(package)); // pas d'écrasement

        var profile = serviceB.Load(imported);
        Assert.Equal("#FFABCDEF", profile.Theme.GaugeEndColor);
        Assert.Equal("@Cool.ttf#Cool Font", profile.Theme.TextFont);
        Assert.Equal(FontBytes, File.ReadAllBytes(Path.Combine(dirB, "Fonts", "Cool.ttf")));
        var cpuImage = profile.Modules["gpu"].BackgroundImage!;
        Assert.StartsWith(Path.Combine(dirB, "Backgrounds"), cpuImage);
        Assert.Equal(PngBytes, File.ReadAllBytes(cpuImage));
        Assert.Equal(PngBytes, File.ReadAllBytes(profile.DeckModules["gpu"].BackgroundImage!));

        // Fond de l'écran de la Switch : image dans l'archive, couleur et opacités conservées.
        var screen = profile.DeckScreen!;
        Assert.StartsWith(Path.Combine(dirB, "Backgrounds"), screen.BackgroundImage!);
        Assert.Equal(PngBytes, File.ReadAllBytes(screen.BackgroundImage!));
        Assert.Equal("#112233", screen.BackgroundColor);
        Assert.Equal(0.6, screen.BackgroundDim);
        Assert.Equal(0.4, screen.ClockOpacity);
    }

    [Fact]
    public void DeckScreen_IsCapturedAppliedAndKeptByOldProfiles()
    {
        var dir = Dir("a");
        var (host, service) = Create(dir);
        host.Config.Deck.BackgroundColor = "#0A0B0C";
        host.Config.Deck.BackgroundDim = 0.8;
        host.Config.Deck.ClockOpacity = 0.25;
        service.SaveCurrent("P");

        host.Config.Deck.BackgroundColor = "#FFFFFF";
        host.Config.Deck.BackgroundDim = 0;
        host.Config.Deck.ClockOpacity = 1;
        service.Apply("P", includeLayout: false);
        Assert.Equal("#0A0B0C", host.Config.Deck.BackgroundColor);
        Assert.Equal(0.8, host.Config.Deck.BackgroundDim);
        Assert.Equal(0.25, host.Config.Deck.ClockOpacity);

        // Profil ancien (sans DeckScreen) : l'écran de la Switch n'est pas touché.
        var old = new Profile { DeckScreen = null };
        old.ApplyTo(host.Config, includeLayout: false);
        Assert.Equal("#0A0B0C", host.Config.Deck.BackgroundColor);
    }

    [Fact]
    public void DeckScreen_AppliesToEveryPage()
    {
        var config = new AppConfig();
        config.Deck.Pages.Add(new Deck.DeckPage { Name = "A" });
        config.Deck.Pages.Add(new Deck.DeckPage { Name = "B" });
        config.Deck.Migrate();
        var profile = new Profile { DeckScreen = new ProfileDeckScreen { BackgroundColor = "#FF123456", TileStyle = Deck.DeckTileStyle.Flat, TileIconScale = 0.8 } };

        profile.ApplyTo(config, includeLayout: false);

        Assert.All(config.Deck.Pages, p => Assert.True(p.BackgroundColor == "#FF123456" && p.TileStyle == Deck.DeckTileStyle.Flat && p.TileIconScale == 0.8));
    }

    [Fact]
    public void ImportWithDifferentFontOfSameNameKeepsBoth()
    {
        var dirA = Dir("a");
        var (hostA, serviceA) = Create(dirA);
        File.WriteAllBytes(Path.Combine(Dir("a\\Fonts"), "Cool.ttf"), FontBytes);
        hostA.Config.Theme!.NumberFont = "@Cool.ttf#Cool";
        serviceA.SaveCurrent("P");
        var package = Path.Combine(dirA, "p.sysdeckprofile");
        serviceA.Export("P", package);

        var dirB = Dir("b");
        var other = new byte[] { 0x00, 0x01, 0x00, 0x00, 1, 1, 1 };
        File.WriteAllBytes(Path.Combine(Dir("b\\Fonts"), "Cool.ttf"), other);
        var (_, serviceB) = Create(dirB);
        serviceB.Import(package);

        Assert.Equal(other, File.ReadAllBytes(Path.Combine(dirB, "Fonts", "Cool.ttf"))); // l'existante n'est pas écrasée
        var key = serviceB.Load("P").Theme.NumberFont;
        Assert.NotEqual("@Cool.ttf#Cool", key);
        Assert.EndsWith("#Cool", key);
    }

    private string MakeZip(Action<ZipArchive> fill)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sysdeckprofile");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            fill(zip);
        return path;
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] data)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(data);
    }

    private static byte[] ProfileJson(string background = "")
        => System.Text.Encoding.UTF8.GetBytes(
            "{\"Name\":\"Evil\",\"Modules\":{\"cpu\":{\"BackgroundImage\":" + (background.Length == 0 ? "null" : $"\"{background}\"") + "}}}");

    private void AssertRefused(string package, string service_dir = "victim")
    {
        var dir = Dir(service_dir);
        var (_, service) = Create(dir);
        var ex = Assert.Throws<ProfileException>(() => service.Import(package));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        // Rien d'écrit : ni profil, ni image, ni police, et rien hors du dossier.
        Assert.Empty(service.List());
        Assert.False(Directory.Exists(Path.Combine(dir, "Profiles", "Files")) && Directory.GetFiles(Path.Combine(dir, "Profiles", "Files")).Length > 0);
        Assert.False(Directory.Exists(Path.Combine(dir, "Fonts")));
        Assert.False(Directory.Exists(Path.Combine(dir, "Backgrounds")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.png")));
    }

    [Theory]
    [InlineData("../evil.png")]
    [InlineData("Backgrounds/../../evil.png")]
    [InlineData("..\\evil.png")]
    [InlineData("/evil.png")]
    [InlineData("C:/evil.png")]
    [InlineData("Backgrounds/sub/evil.png")]
    public void ZipSlipIsRefused(string entryName)
    {
        var package = MakeZip(zip =>
        {
            AddEntry(zip, "profile.json", ProfileJson());
            AddEntry(zip, entryName, PngBytes);
        });
        AssertRefused(package);
    }

    [Fact]
    public void ForbiddenFileTypesAreRefused()
    {
        AssertRefused(MakeZip(zip =>
        {
            AddEntry(zip, "profile.json", ProfileJson());
            AddEntry(zip, "Backgrounds/virus.exe", PngBytes);
        }), "v1");
        AssertRefused(MakeZip(zip =>
        {
            AddEntry(zip, "profile.json", ProfileJson());
            AddEntry(zip, "Fonts/script.ps1", FontBytes);
        }), "v2");
        AssertRefused(MakeZip(zip =>
        {
            AddEntry(zip, "profile.json", ProfileJson());
            AddEntry(zip, "readme.txt", new byte[] { 1 });
        }), "v3");
    }

    [Fact]
    public void ImageWithWrongContentIsRefused()
    {
        AssertRefused(MakeZip(zip =>
        {
            AddEntry(zip, "profile.json", ProfileJson());
            AddEntry(zip, "Backgrounds/fake.png", System.Text.Encoding.ASCII.GetBytes("MZ pas une image"));
        }));
    }

    [Fact]
    public void AbsoluteOrUnknownImageReferenceIsRefused()
    {
        AssertRefused(MakeZip(zip => AddEntry(zip, "profile.json", ProfileJson(@"C:\\\\Windows\\\\secret.png"))), "v1");
        AssertRefused(MakeZip(zip => AddEntry(zip, "profile.json", ProfileJson("Backgrounds/absent.png"))), "v2");
    }

    [Fact]
    public void InvalidArchivesAreRefused()
    {
        var notZip = Path.Combine(_root, "bad.sysdeckprofile");
        File.WriteAllText(notZip, "ceci n'est pas un zip");
        AssertRefused(notZip, "v1");
        AssertRefused(MakeZip(zip => AddEntry(zip, "autre.json", ProfileJson())), "v2");
        AssertRefused(MakeZip(zip => AddEntry(zip, "profile.json", new byte[] { (byte)'{', (byte)'x' })), "v3");

        var (_, service) = Create(Dir("v4"));
        Assert.Throws<ProfileException>(() => service.Import(Path.Combine(_root, "absent.sysdeckprofile")));
    }

    [Fact]
    public void FontKeyOutsideFolderIsRefused()
    {
        var json = System.Text.Encoding.UTF8.GetBytes("{\"Name\":\"X\",\"Theme\":{\"TextFont\":\"@../../evil.ttf#Evil\"}}");
        AssertRefused(MakeZip(zip => AddEntry(zip, "profile.json", json)));
    }

    [Fact]
    public void TooManyBytesAreRefused()
    {
        // 101 Mo de zéros : très compressible, donc l'archive est petite mais le contenu dépasse la limite.
        var package = MakeZip(zip =>
        {
            AddEntry(zip, "profile.json", ProfileJson("Backgrounds/big.png"));
            var entry = zip.CreateEntry("Backgrounds/big.png", CompressionLevel.SmallestSize);
            using var s = entry.Open();
            s.Write(PngBytes);
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 101; i++) s.Write(zeros);
        });
        AssertRefused(package);
    }

    // ---------- Copieur générique ----------

    [Fact]
    public void Copier_CopiesValuesIntoSameInstanceAndKeepsSubscribers()
    {
        var target = new ThemeSettings();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)target).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var source = new ThemeSettings { GaugeStartColor = "#FF010101", CornerRadius = 3, Glow = false, TextFont = "Consolas" };

        var same = target;
        SettingsCopier.CopyInto(source, target);

        Assert.Same(same, target);
        Assert.Equal("#FF010101", target.GaugeStartColor);
        Assert.Equal(3, target.CornerRadius);
        Assert.False(target.Glow);
        Assert.Equal("Consolas", target.TextFont);
        Assert.Contains("GaugeStartColor", raised); // l'abonné est prévenu
    }

    [Fact]
    public void Copier_ModuleAppearance_SubThemeAndDictionaries()
    {
        var source = new ModuleAppearance { Width = 400, UseCustomTheme = true, Theme = new ThemeSettings { TextColor = "#FF0A0B0C" } };
        source.GetText("title").CustomText = "Salut";
        source.GetElement("gauge").X = 12;

        var target = new ModuleAppearance { Width = 100 };
        target.GetText("old").Hidden = true;
        var raised = new List<string?>();
        target.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        SettingsCopier.CopyInto(source, target);

        Assert.Equal(400, target.Width);
        Assert.True(target.UseCustomTheme);
        Assert.Equal("#FF0A0B0C", target.Theme!.TextColor);
        Assert.NotSame(source.Theme, target.Theme); // pas de partage entre source et cible
        Assert.Equal(new[] { "title" }, target.Texts.Keys.ToArray()); // contenu remplacé
        Assert.Equal("Salut", target.Texts["title"].CustomText);
        Assert.NotSame(source.Texts["title"], target.Texts["title"]);
        Assert.Equal(12, target.Elements["gauge"].X);
        Assert.Contains("Texts", raised);
        Assert.Contains("Elements", raised);
    }

    [Fact]
    public void Copier_ReusesExistingSubThemeInstance()
    {
        var existingTheme = new ThemeSettings();
        var themeEvents = 0;
        existingTheme.PropertyChanged += (_, _) => themeEvents++;
        var target = new ModuleAppearance { Theme = existingTheme };

        SettingsCopier.CopyInto(new ModuleAppearance { Theme = new ThemeSettings { GaugeEndColor = "#FF999999" } }, target);

        Assert.Same(existingTheme, target.Theme);
        Assert.Equal("#FF999999", existingTheme.GaugeEndColor);
        Assert.True(themeEvents > 0);

        SettingsCopier.CopyInto(new ModuleAppearance(), target); // source sans thème propre
        Assert.Null(target.Theme);
    }

    [Fact]
    public void ApplyTo_KeepsConfigInstances()
    {
        var config = new AppConfig { Theme = new ThemeSettings() };
        var theme = config.Theme;
        var cpu = new ModuleAppearance();
        config.Modules["cpu"] = cpu;
        var picker = config.EmojiPicker;
        config.Deck.Modules["cpu"] = new ModuleAppearance();
        config.Deck.Modules["gpu"] = new ModuleAppearance();

        var profile = new Profile();
        profile.Theme.GaugeStartColor = "#FF123456";
        profile.Modules["cpu"] = new ModuleAppearance { Height = 321 };
        profile.DeckModules["cpu"] = new ModuleAppearance { Height = 222 };
        profile.EmojiPicker.CornerRadius = 5;
        profile.Dock.IconSize = 70;
        profile.Dock.Orientation = "Vertical";
        profile.Layout.Add(new WidgetPosition { Id = "cpu", X = 5, Y = 6, Visible = false });

        profile.ApplyTo(config, includeLayout: false);
        Assert.Same(theme, config.Theme);
        Assert.Same(cpu, config.Modules["cpu"]);
        Assert.Same(picker, config.EmojiPicker);
        Assert.Equal("#FF123456", theme.GaugeStartColor);
        Assert.Equal(321, cpu.Height);
        Assert.Equal(222, config.Deck.Modules["cpu"].Height);
        Assert.False(config.Deck.Modules.ContainsKey("gpu")); // absent du profil : recréé à la demande
        Assert.Equal(5, picker.CornerRadius);
        Assert.Equal(70, config.DockIconSize);
        Assert.Equal("Vertical", config.DockOrientation);
        Assert.Empty(config.Layout); // disposition non demandée

        profile.ApplyTo(config, includeLayout: true);
        Assert.Single(config.Layout);
        Assert.False(config.Layout[0].Visible);
    }

    // ---------- Thèmes prêts à l'emploi ----------

    [Fact]
    public void ThemePresets_AreNineDistinctValidThemes()
    {
        Assert.Equal(9, ThemePresets.All.Count);
        Assert.Equal(9, ThemePresets.All.Select(p => p.Name).Distinct().Count());
        foreach (var preset in ThemePresets.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Description));
            var theme = preset.CreateTheme();
            Assert.NotSame(theme, preset.CreateTheme()); // instance neuve à chaque appel
            Assert.True(Contrast(theme.TextColor, theme.PanelColor) >= 7, $"{preset.Name} : texte principal");
            Assert.True(Contrast(theme.SubTextColor, theme.PanelColor) >= 4.5, $"{preset.Name} : texte secondaire");
        }
        Assert.NotNull(ThemePresets.Find("glace"));
    }

    private static double Contrast(string a, string b)
    {
        static double Lum(string hex)
        {
            static double Channel(int v) { var c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            var n = Convert.ToInt32(hex[3..], 16);
            return 0.2126 * Channel(n >> 16 & 255) + 0.7152 * Channel(n >> 8 & 255) + 0.0722 * Channel(n & 255);
        }
        double l1 = Lum(a), l2 = Lum(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
