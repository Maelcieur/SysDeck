using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using SysDeck.Models;
using SysDeck.Plugins;
using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class PluginManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sysdeck-plugin-{Guid.NewGuid():N}");

    public PluginManifestTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "plugin.exe"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Valid = """
        { "id": "com.moi.test", "name": "Test", "version": "2.1", "CodePath": "plugin.exe",
          "Actions": [ { "Id": "com.moi.test.go", "Name": "Go",
            "Settings": [ { "Key": "n", "Type": "number", "Label": "N", "Default": 3 },
                          { "Key": "mode", "Type": "select", "Options": [ { "Value": "a", "Label": "A" } ] } ] } ] }
        """;

    [Fact]
    public void Parse_ValidManifest_CaseInsensitive()
    {
        var manifest = PluginManifest.Parse(Valid, _dir);

        Assert.Equal("com.moi.test", manifest.Id);
        Assert.Equal("2.1", manifest.Version);
        Assert.Equal(Path.Combine(_dir, "plugin.exe"), manifest.CodePath);
        var action = Assert.Single(manifest.Actions);
        Assert.Equal(PluginSettingType.Select, action.Settings[1].Type);
        Assert.Equal(3, PluginManifest.DefaultSettings(action)["n"]!.GetValue<int>());
        Assert.Same(action, manifest.FindAction("COM.MOI.TEST.GO"));
    }

    [Fact]
    public void Load_EncryptedElgatoPlugin_GivesClearMessage()
    {
        File.WriteAllBytes(Path.Combine(_dir, "manifest.json"), new byte[] { (byte)'E', (byte)'L', (byte)'G', (byte)'A', (byte)'T', (byte)'O', 1, 0, 0x82 });
        Assert.Equal(PluginManifest.ElgatoEncrypted, Assert.Throws<InvalidDataException>(() => PluginManifest.Load(_dir)).Message);
    }

    private const string ElgatoManifest = """
        { "Name": "Compteur SD", "Version": "1.2.0.0", "Author": "Elgato", "CodePath": "bin/plugin.js", "SDKVersion": 2,
          "Icon": "imgs/plugin", "Nodejs": { "Version": "20" }, "PropertyInspectorPath": "ui/pi.html",
          "OS": [ { "Platform": "mac" }, { "Platform": "windows" } ],
          "Actions": [
            { "UUID": "com.elgato.counter.increment", "Name": "Compter", "Icon": "imgs/action",
              "States": [ { "Image": "imgs/off" }, { "Image": "imgs/on", "Title": "ON" } ] },
            { "UUID": "com.elgato.counter.dial", "Name": "Molette", "Controllers": [ "Encoder" ] } ] }
        """;

    [Fact]
    public void Parse_ElgatoManifest_ReadsStatesImagesAndNode()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "com.elgato.counter.sdPlugin")).FullName;
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        Directory.CreateDirectory(Path.Combine(folder, "imgs"));
        File.WriteAllText(Path.Combine(folder, "bin", "plugin.js"), "");
        File.WriteAllBytes(Path.Combine(folder, "imgs", "off@2x.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(folder, "imgs", "on.png"), new byte[] { 1 });

        var manifest = PluginManifest.Parse(ElgatoManifest, folder);

        Assert.True(manifest.IsElgato);
        Assert.Equal("com.elgato.counter", manifest.Id); // dossier sans « .sdPlugin »
        Assert.Equal(20, manifest.NodeMajor);
        Assert.Equal(1, manifest.SkippedActions); // molette seule
        var action = Assert.Single(manifest.Actions);
        Assert.True(action.AutoStates);
        Assert.EndsWith("off@2x.png", action.ImageFor(0));
        Assert.EndsWith("on.png", action.ImageFor(1));
        Assert.Equal("ui/pi.html", action.PropertyInspectorPath);
    }

    [Fact]
    public void Parse_ElgatoMacOnlyOrHtml_Refused()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "com.x.y.sdPlugin")).FullName;
        File.WriteAllText(Path.Combine(folder, "index.html"), "");
        File.WriteAllText(Path.Combine(folder, "plugin.exe"), "");
        Assert.Throws<InvalidDataException>(() => PluginManifest.Parse(
            """{"Name":"x","CodePath":"plugin.exe","OS":[{"Platform":"mac"}],"Actions":[{"UUID":"com.x.y.a","Name":"a"}]}""", folder));
        Assert.Equal(PluginManifest.ElgatoHtml, Assert.Throws<InvalidDataException>(() => PluginManifest.Parse(
            """{"Name":"x","CodePath":"index.html","Actions":[{"UUID":"com.x.y.a","Name":"a"}]}""", folder)).Message);
    }

    [Theory]
    [InlineData("""{"Id":"pasdepoint","Name":"x","CodePath":"plugin.exe","Actions":[{"Id":"a.b","Name":"x"}]}""")]
    [InlineData("""{"Id":"com.a.b","Name":"x","CodePath":"..\\evil.exe","Actions":[{"Id":"a.b","Name":"x"}]}""")]
    [InlineData("""{"Id":"com.a.b","Name":"x","CodePath":"C:\\Windows\\notepad.exe","Actions":[{"Id":"a.b","Name":"x"}]}""")]
    [InlineData("""{"Id":"com.a.b","Name":"x","CodePath":"plugin.txt","Actions":[{"Id":"a.b","Name":"x"}]}""")]
    [InlineData("""{"Id":"com.a.b","Name":"x","CodePath":"plugin.exe","Actions":[]}""")]
    [InlineData("""{"Id":"com.a.b","Name":"x","CodePath":"plugin.exe","Actions":[{"Id":"a.b","Name":"x"},{"Id":"A.B","Name":"y"}]}""")]
    [InlineData("""{"Id":"com.a.b","Name":"x","CodePath":"plugin.exe","SdkVersion":99,"Actions":[{"Id":"a.b","Name":"x"}]}""")]
    [InlineData("""{"Id":"com.a.b","Name":"x","CodePath":"plugin.exe","Actions":[{"Id":"a.b","Name":"x","Settings":[{"Key":"k","Type":"select"}]}]}""")]
    [InlineData("pas du json")]
    public void Parse_InvalidManifest_Throws(string json)
        => Assert.Throws<InvalidDataException>(() => PluginManifest.Parse(json, _dir));
}

public class PluginProtocolTests
{
    [Fact]
    public void TileEvent_HasElgatoShape()
    {
        var json = PluginProtocol.BuildTileEvent(PluginProtocol.KeyDown, "com.a.b", "ctx1", new JsonObject { ["n"] = 2 });
        var obj = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("keyDown", obj["event"]!.GetValue<string>());
        Assert.Equal("com.a.b", obj["action"]!.GetValue<string>());
        Assert.Equal("ctx1", obj["context"]!.GetValue<string>());
        Assert.Equal(2, obj["payload"]!["settings"]!["n"]!.GetValue<int>());
    }

    [Fact]
    public void Parse_ReadsPluginMessage()
    {
        Assert.Equal("setState", PluginProtocol.SetState);
        var message = PluginProtocol.Parse("""{"event":"setTitle","context":"c","payload":{"title":"Salut"}}""")!;
        Assert.Equal("setTitle", message.Event);
        Assert.Equal("c", message.Context);
        Assert.Equal("Salut", message.Payload!["title"]!.GetValue<string>());
        Assert.Null(PluginProtocol.Parse("[1,2]"));
        Assert.Null(PluginProtocol.Parse("{\"pas\":\"d'event\"}"));
        Assert.Null(PluginProtocol.Parse("{oups"));
    }

    [Fact]
    public void Arguments_FollowStreamDeckOrder()
        => Assert.Equal(new[] { "-port", "1234", "-pluginUUID", "T", "-registerEvent", "registerPlugin", "-info", "{}" },
            PluginProtocol.BuildArguments(1234, "T", "{}"));

    [Fact]
    public void ElgatoPayloads_AndSvgImages()
    {
        var payload = PluginProtocol.KeyPayload((2, 1), 1);
        Assert.Equal(2, payload["coordinates"]!["column"]!.GetValue<int>());
        Assert.Equal(1, payload["state"]!.GetValue<int>());
        Assert.Equal("Keypad", payload["controller"]!.GetValue<string>());
        var device = JsonNode.Parse(PluginProtocol.BuildDeviceDidConnect(8, 4))!;
        Assert.Equal(8, device["deviceInfo"]!["size"]!["columns"]!.GetValue<int>());
        Assert.Equal("<svg width='1'/>", PluginProtocol.DecodeSvg("data:image/svg+xml;charset=utf8,%3Csvg%20width%3D%271%27%2F%3E"));
        Assert.Equal("<svg/>", PluginProtocol.DecodeSvg("data:image/svg+xml;base64,PHN2Zy8+"));
        Assert.Null(PluginProtocol.DecodeSvg("data:image/png;base64,AQID"));
    }

    [Fact]
    public void CatalogRelease_PicksWindowsPluginNotBrowserExtension()
    {
        const string json = """
            {"tag_name":"v5.1","assets":[
              {"name":"chrome-extension-v1.zip","browser_download_url":"https://x/c.zip","size":1},
              {"name":"com.a.b.mac.streamDeckPlugin","browser_download_url":"https://x/mac","size":2},
              {"name":"com.a.b.streamDeckPlugin","browser_download_url":"https://x/win","size":3}]}
            """;
        var release = PluginCatalog.ParseRelease(json)!;
        Assert.Equal("v5.1", release.Tag);
        Assert.Equal("https://x/win", release.DownloadUrl);
        Assert.Null(PluginCatalog.ParseRelease("""{"tag_name":"v1","assets":[{"name":"firefox.xpi","browser_download_url":"https://x/f"}]}"""));
        Assert.Equal("https://x/s", PluginCatalog.ParseRelease(
            """{"tag_name":"v1","assets":[{"name":"a.streamDeckPlugin","browser_download_url":"https://x/a"},{"name":"a.sysdeckplugin","browser_download_url":"https://x/s"}]}""")!.DownloadUrl);
    }

    [Fact]
    public void CatalogSearchAndVerified_AreRead()
    {
        var search = PluginCatalog.ParseSearch("""
            {"items":[{"full_name":"moeilijk/hwinfo-streamdeck","name":"hwinfo-streamdeck","description":"Capteurs","html_url":"https://github.com/moeilijk/hwinfo-streamdeck",
              "stargazers_count":261,"pushed_at":"2026-10-01T10:00:00Z","owner":{"login":"moeilijk"},"license":{"spdx_id":"MIT"}}]}
            """, CatalogKind.StreamDeck);
        var entry = Assert.Single(search);
        Assert.Equal("Hwinfo streamdeck", entry.Name);
        Assert.Equal(261, entry.Stars);
        Assert.Equal("MIT", entry.License);
        Assert.False(entry.Verified);

        var verified = PluginCatalog.ParseVerified("""[{"repo":"a/b","name":"Super","kind":"streamdeck"},{"repo":"pas-un-depot"}]""");
        var v = Assert.Single(verified);
        Assert.True(v.Verified);
        Assert.Equal(CatalogKind.StreamDeck, v.Kind);
        Assert.Equal("https://github.com/a/b", v.Url);
    }

    [Fact]
    public void NodeShasums_FindWindowsZip()
    {
        var text = "aaa  node-v20.18.0-darwin-arm64.tar.gz\n" + new string('b', 64) + "  node-v20.18.0-win-x64.zip\n";
        Assert.Equal((new string('b', 64), "node-v20.18.0-win-x64.zip"), NodeRuntime.ParseShasums(text));
        Assert.Equal(24, NodeRuntime.ParseMajor("v24.18.0"));
    }

    [Fact]
    public void Token_IsRandomAndComparedExactly()
    {
        var token = PluginProtocol.NewToken();
        Assert.Equal(32, token.Length);
        Assert.NotEqual(token, PluginProtocol.NewToken());
        Assert.True(PluginProtocol.TokenMatches(token, token));
        Assert.False(PluginProtocol.TokenMatches(token, token.ToLowerInvariant() + "x"));
        Assert.False(PluginProtocol.TokenMatches(token, null));
    }

    [Fact]
    public void Handshake_RfcExampleAndOriginRefused()
    {
        // Exemple de la RFC 6455.
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", PluginProtocol.HandshakeAccept("dGhlIHNhbXBsZSBub25jZQ=="));
        const string request = "GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: abc==\r\n\r\n";
        Assert.Equal("abc==", PluginProtocol.ParseHandshake(request));
        Assert.Null(PluginProtocol.ParseHandshake(request.Replace("\r\n\r\n", "\r\nOrigin: https://site.example\r\n\r\n")));
        // Page de réglages locale d'un plugin Stream Deck (WebView2) : acceptée, elle doit ensuite donner un contexte ouvert.
        Assert.Equal("abc==", PluginProtocol.ParseHandshake(request.Replace("\r\n\r\n", "\r\nOrigin: file://\r\n\r\n")));
        var context = JsonNode.Parse(PluginProtocol.BuildContextEvent("sendToPropertyInspector", "a.b", "ctx", new JsonObject { ["x"] = 1 }))!;
        Assert.Equal("ctx", context["context"]!.GetValue<string>());
        Assert.Equal(1, context["payload"]!["x"]!.GetValue<int>());
        Assert.Null(PluginProtocol.ParseHandshake("POST / HTTP/1.1\r\n\r\n"));
    }

    [Fact]
    public void DecodeImage_AcceptsBitmapsOnly()
    {
        Assert.Equal(new byte[] { 1, 2, 3 }, PluginProtocol.DecodeImage("data:image/png;base64,AQID"));
        Assert.Null(PluginProtocol.DecodeImage("data:image/svg+xml;base64,AQID"));
        Assert.Null(PluginProtocol.DecodeImage("data:image/png,brut"));
        Assert.Null(PluginProtocol.DecodeImage("C:\\image.png"));
        Assert.Null(PluginProtocol.DecodeImage("data:image/png;base64," + new string('A', PluginProtocol.MaxImageBytes * 2)));
    }

    [Theory]
    [InlineData("https://sysdeck.example/page", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("C:\\Windows\\notepad.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    public void SafeUrl_OnlyWeb(string url, bool allowed) => Assert.Equal(allowed, PluginProtocol.SafeUrl(url) is not null);
}

/// <summary>Bout en bout : le vrai plugin d'exemple (PowerShell) installé, lancé, relié, puis un appui sur sa tuile.</summary>
public class PluginHostTests
{
    private static string ExampleFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SysDeck.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "plugins", "exemple-compteur");
    }

    [Fact]
    public void ExamplePlugin_ShowsAndCountsOnKeyDown()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Run(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "délai dépassé");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var tile = new ShortcutItem
        {
            Kind = TileKind.Plugin,
            Name = "Compteur",
            PluginId = "fr.sysdeck.compteur",
            PluginAction = "fr.sysdeck.compteur.compter",
            PluginSettings = new JsonObject { ["label"] = "Morts", ["step"] = 2, ["value"] = 5 }
        };
        var saves = 0;
        using var host = new PluginHost(() => new[] { tile }, () => saves++, new PluginsConfig(), () => { });

        host.Install(ExampleFolder());
        WaitFor(() => host.TitleOf(tile) == "Morts 5", () => $"titre initial : {host.TitleOf(tile)} ; état : {host.Find(tile.PluginId)?.Status} {host.Find(tile.PluginId)?.Detail}");

        Assert.True(host.Press(tile));
        WaitFor(() => host.TitleOf(tile) == "Morts 7" && saves > 0, () => $"après appui : {host.TitleOf(tile)}, enregistrements : {saves}");
        Assert.Equal(7, tile.PluginSettings!["value"]!.GetValue<int>());
    }

    /// <summary>Fait tourner la file du dispatcher (suites des attentes réseau) jusqu'à la condition.</summary>
    private static void WaitFor(Func<bool> condition, Func<string> describe)
    {
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(describe());
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Background, (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                frame.Continue = false;
            }, Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame);
        }
    }
}
