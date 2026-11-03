using System.IO;
using System.IO.Compression;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class IconPackStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sysdeck-pack-{Guid.NewGuid():N}");

    public IconPackStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    [Fact]
    public void Read_ElgatoPack_UsesManifestAndIconsJson()
    {
        File.WriteAllText(Path.Combine(_dir, "manifest.json"), """{"Name":"Néon","Author":"Moi","Version":"1.2","Icon":"pack.png"}""");
        File.WriteAllText(Path.Combine(_dir, "icons.json"), """[{"path":"micro.png","name":"Micro","tags":["son","voix"]},{"path":"logo.svg","name":"Logo"},{"path":"absent.png"}]""");
        Touch("pack.png");
        Touch(@"icons\micro.png");
        Touch(@"icons\logo.svg");

        var pack = IconPackStore.Read(_dir, "neon")!;

        Assert.Equal("Néon", pack.Name);
        Assert.Equal("Moi", pack.Author);
        var icon = Assert.Single(pack.Icons);
        Assert.Equal("Micro", icon.Name);
        Assert.Equal(new[] { "son", "voix" }, icon.Tags);
        Assert.Equal(1, pack.SkippedSvg);
        Assert.EndsWith("pack.png", pack.IconPath);
    }

    [Fact]
    public void Read_WithoutIconsJson_ListsImagesOfIconsFolder()
    {
        File.WriteAllText(Path.Combine(_dir, "manifest.json"), """{"name":"Simple"}""");
        Touch(@"icons\play_button.png");
        Touch(@"icons\notes.txt");

        var pack = IconPackStore.Read(_dir, "simple")!;

        Assert.Equal("play button", Assert.Single(pack.Icons).Name);
    }

    [Fact]
    public void Read_WithoutImages_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_dir, "manifest.json"), """{"Name":"Vide"}""");
        Assert.Null(IconPackStore.Read(_dir, "vide"));
    }

    [Fact]
    public void IconsJson_PathLeavingThePack_IsIgnored()
    {
        var inner = Path.Combine(_dir, "pack");
        Directory.CreateDirectory(inner);
        Touch("dehors.png");
        Touch(@"pack\ok.png");
        File.WriteAllText(Path.Combine(inner, "icons.json"), """[{"path":"..\\dehors.png"},{"path":"ok.png"}]""");

        var pack = IconPackStore.Read(inner, "p")!;

        Assert.EndsWith("ok.png", Assert.Single(pack.Icons).Path);
    }

    [Fact]
    public void FindRoot_PicksShallowestManifest()
    {
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "a.sdIconPack")).FullName, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, @"a.sdIconPack\x\y")).FullName, "manifest.json"), "{}");

        Assert.Equal(Path.Combine(_dir, "a.sdIconPack"), IconPackStore.FindRoot(_dir));
    }

    [Fact]
    public void ExtractSafely_RefusesZipSlip()
    {
        var zip = Path.Combine(_dir, "mauvais.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../../evade.txt").Open());
            writer.Write("x");
        }

        Assert.Throws<InvalidDataException>(() => IconPackStore.ExtractSafely(zip, Path.Combine(_dir, "out")));
        Assert.False(File.Exists(Path.Combine(_dir, "..", "evade.txt")));
    }

    [Fact]
    public void ExtractSafely_RefusesNonZip()
    {
        var file = Touch("pas-un-zip.streamDeckIconPack");
        Assert.Throws<InvalidDataException>(() => IconPackStore.ExtractSafely(file, Path.Combine(_dir, "out")));
    }

    [Theory]
    [InlineData("Néon Bleu !", "neon-bleu")]
    [InlineData("   ", "pack")]
    [InlineData("Twitch/OBS pack", "twitch-obs-pack")]
    public void MakeId_IsFolderSafe(string name, string expected) => Assert.Equal(expected, IconPackStore.MakeId(name));

    [Fact]
    public void Search_MatchesTagsWithoutAccents()
    {
        var pack = new IconPack
        {
            Id = "p",
            Name = "Jeux",
            Folder = _dir,
            Icons = new[] { new PackIcon("a.png", "Manette", new[] { "Gaming" }), new PackIcon("b.png", "Café", Array.Empty<string>()) }
        };

        Assert.Equal("Manette", Assert.Single(IconPackStore.Search("gaming", new[] { pack })).Icon.Name);
        Assert.Equal("Café", Assert.Single(IconPackStore.Search("cafe", new[] { pack })).Icon.Name);
        Assert.Equal(2, IconPackStore.Search("jeux", new[] { pack }).Count());
    }

    [Fact]
    public void Import_SvgPack_ConvertsToPng()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var zip = Path.Combine(_dir, "svg.streamDeckIconPack");
                using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    using (var w = new StreamWriter(archive.CreateEntry("x.sdIconPack/manifest.json").Open())) w.Write("""{"Name":"Pack SVG de test","Icon":"icon.svg"}""");
                    using (var w = new StreamWriter(archive.CreateEntry("x.sdIconPack/icons.json").Open())) w.Write("""[{"path":"rond.svg","name":"Rond","tags":["cercle"]}]""");
                    const string svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><circle cx="5" cy="5" r="4" fill="#fff"/></svg>""";
                    using (var w = new StreamWriter(archive.CreateEntry("x.sdIconPack/icons/rond.svg").Open())) w.Write(svg);
                    using (var w = new StreamWriter(archive.CreateEntry("x.sdIconPack/icon.svg").Open())) w.Write(svg);
                }

                var pack = IconPackStore.Import(zip);

                var icon = Assert.Single(pack.Icons);
                Assert.EndsWith("rond.svg.png", icon.Path);
                Assert.True(new FileInfo(icon.Path).Length > 100);
                Assert.EndsWith("icon.svg.png", pack.IconPath);
                Assert.Equal(0, pack.SkippedSvg);
                IconPackStore.Delete(pack.Id);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    [Fact]
    public void Import_Zip_InstallsAndReplaces()
    {
        var zip = Path.Combine(_dir, "test.streamDeckIconPack");
        void Build(int count)
        {
            File.Delete(zip);
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            using (var w = new StreamWriter(archive.CreateEntry("com.test.sdIconPack/manifest.json").Open())) w.Write("""{"Name":"Pack de test unitaire"}""");
            for (var i = 0; i < count; i++)
                using (var s = archive.CreateEntry($"com.test.sdIconPack/icons/i{i}.png").Open()) s.WriteByte(1);
        }

        Build(2);
        Assert.Equal(2, IconPackStore.Import(zip).Icons.Count);
        Build(3);
        var pack = IconPackStore.Import(zip);
        Assert.Equal(3, pack.Icons.Count);
        Assert.Single(IconPackStore.All, p => p.Id == pack.Id);

        IconPackStore.Delete(pack.Id);
        Assert.DoesNotContain(IconPackStore.All, p => p.Id == pack.Id);
    }
}
