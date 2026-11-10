using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Menu languages: (language 0..5 = english, german, french, spanish, italian [ref:2809]
/// swedish, from install.win in the original; port.json <c>language</c> in the port) and (TEXT.ENG/GER/FRE/SPA/ITA/SWE). [ref:2810]
/// PNG for review: <c>NFS_LANGUAGE_DIR</c> (usually out/gfx_check/language).</summary>
public class MenuLanguageTests
{
    static readonly string[] Codes = ["ENG", "GER", "FRE", "SPA", "ITA", "SWE"];

    [Fact]
    public void LanguageIndexMapsToTheTextFileAndParsesNamesCodesAndDigits()
    {
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(Codes[i], TextFile.CodeFor(i));
            Assert.Equal(i, TextFile.ParseLanguage(TextFile.LanguageNames[i]));
            Assert.Equal(i, TextFile.ParseLanguage(Codes[i].ToLowerInvariant()));
            Assert.Equal(i, TextFile.ParseLanguage(i.ToString()));
        }
        Assert.Equal(2, TextFile.ParseLanguage(" French "));
        Assert.Equal("ENG", TextFile.CodeFor(9));
        Assert.Null(TextFile.ParseLanguage("klingon"));
        Assert.Null(TextFile.ParseLanguage("6"));
        Assert.Null(TextFile.ParseLanguage("-1"));
        Assert.Null(TextFile.ParseLanguage(null));
    }

    [Fact]
    public void PortSettingsLanguageRoundTripsAndUnknownValuesMeanEnglish()
    {
        string path = Path.Combine(Path.GetTempPath(), "nfs2se-lang-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new PortSettings();
            Assert.Equal(0, s.LanguageIndex);                                   // never saved = English
            s.SetLanguage(5);
            Assert.Null(s.Save(path));
            Assert.Contains("\"language\": \"swedish\"", File.ReadAllText(path));
            Assert.Equal(5, PortSettings.Load(path).LanguageIndex);

            File.WriteAllText(path, "{ \"language\": \"3\", \"customKey\": 7 }");   // digits and unknown keys
            var loaded = PortSettings.Load(path);
            Assert.Equal(3, loaded.LanguageIndex);
            loaded.SetLanguage(1);
            loaded.Save(path);
            Assert.Contains("customKey", File.ReadAllText(path));
            Assert.Equal(1, PortSettings.Load(path).LanguageIndex);

            File.WriteAllText(path, "{ \"language\": \"klingon\" }");
            Assert.Equal(0, PortSettings.Load(path).LanguageIndex);
            var bad = new PortSettings(); bad.SetLanguage(42);                   // out of range -> English
            Assert.Equal(0, bad.LanguageIndex);
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [GameDataFact]
    public void EveryLanguageLoadsItsOwnTextFileAndSetsConfigLanguage()
    {
        string? dir = GameFiles.TryFindGameDir();
        var english = TextFile.Load("ENG", dir);
        for (int lang = 0; lang < 6; lang++)
        {
            var menu = MenuController.Load(dir, language: lang);
            var file = TextFile.Load(Codes[lang], dir);
            Assert.Equal(lang, menu.Config.Language);
            Assert.Equal(file.Entries.Count, menu.Text.Entries.Count);
            foreach (var item in menu.CurrentScreen.Items.Where(i => i.TextId > 0 && i.TextId < file.Entries.Count && i.Text != null))
                Assert.Equal(file[item.TextId], item.Text);                       // every item text comes from that file
            if (lang > 0)
            {
                int differing = Enumerable.Range(0, Math.Min(english.Entries.Count, file.Entries.Count)).Count(i => english[i] != file[i]);
                Assert.True(differing > 100, $"{Codes[lang]} differs from ENG in {differing} texts");
            }
        }
    }

    [GameDataFact]
    public void DefaultAndOutOfRangeLanguageStayInRangeAndFreshNonEnglishConfigUsesKmh()
    {
        string? dir = GameFiles.TryFindGameDir();
        Assert.Equal(0, MenuController.Load(dir).Config.Language);
        Assert.Equal(5, MenuController.Load(dir, language: 99).Config.Language);
        Assert.Equal(0, MenuController.Load(dir, language: -3).Config.Language);
        Assert.Equal(0, MenuController.Load(dir, language: 0).Config.Units);        // English keeps miles [ref:2811]
        Assert.Equal(1, MenuController.Load(dir, language: 1).Config.Units);        // every other language = km/h
    }

    /// <summary>A settings.json profile that stores <paramref name="language"/> in CONFIG +0x5B.</summary>
    static UserSettings ProfileWithLanguage(int language)
    {
        var c = new FeConfig(new byte[FeConfig.Size]) { Language = (byte)language };
        return UserSettings.From(c);
    }

    [GameDataFact]
    public void PinnedLanguageSurvivesAProfileThatStoresAnotherOne()
    {
        string? dir = GameFiles.TryFindGameDir();
        var menu = MenuController.Load(dir, language: 4);
        ProfileWithLanguage(2).ApplyTo(menu.Config);
        Assert.Equal(2, menu.Config.Language);
        menu.PinLanguage();
        Assert.Equal(4, menu.Config.Language);                                      // install language wins (runs on every start) [ref:2812]
        var plain = MenuController.Load(dir);                                       // nothing chosen: no pin, the profile value stays
        ProfileWithLanguage(2).ApplyTo(plain.Config);
        plain.PinLanguage();
        Assert.Equal(2, plain.Config.Language);
    }

    static MenuController OpenTrackInfo(string? dir, int lang)
    {
        var menu = MenuController.Load(dir, language: lang);
        foreach (var k in new[] { MenuKey.Down, MenuKey.Enter, MenuKey.Down, MenuKey.Down, MenuKey.Enter }) menu.HandleKey(k);
        Assert.True(menu.InTrackInfo);
        return menu;
    }

    [GameDataFact]
    public void SwedishTrackLengthUsesADecimalCommaOnlyForLanguage5()
    {
        string? dir = GameFiles.TryFindGameDir();
        Assert.DoesNotContain(',', OpenTrackInfo(dir, 0).TrackInfoLength);
        Assert.DoesNotContain(',', OpenTrackInfo(dir, 2).TrackInfoLength);
        Assert.Equal(',', OpenTrackInfo(dir, 5).TrackInfoLength[1]);                // 2nd char of the buffer becomes 0x2C [ref:2813]
    }

    [GameDataFact]
    public void ArtOverlaysFollowTheLanguage()
    {
        string? dir = GameFiles.TryFindGameDir();
        var overlays = Enumerable.Range(0, 6).Select(l => MenuController.Load(dir, language: l).CreditsOverlay).ToArray();
        Assert.Equal(6, overlays.Distinct().Count());
    }

    [GameDataFact]
    public void AllSixLanguagesRenderADifferentGameSetupScreen()
    {
        string? dir = GameFiles.TryFindGameDir();
        // The game setup screen draws icon shapes (French name substitutions for language 2), the lap header [ref:2814]
        // (swedish word order for language 5) and its own texts.
        var frames = new byte[6][];
        for (int lang = 0; lang < 6; lang++)
        {
            var menu = MenuController.Load(dir, language: lang);
            menu.HandleKey(MenuKey.Enter);
            var fb = new FrameBuffer();
            new MenuScene(menu, dir).Render(fb);
            frames[lang] = fb.Rgba.ToArray();
        }
        for (int a = 0; a < 6; a++)
            for (int b = a + 1; b < 6; b++)
                Assert.False(frames[a].AsSpan().SequenceEqual(frames[b]), $"language {a} and {b} render the same game setup screen");
    }

    [GameDataFact]
    public void NetworkTrackNamesComeFromTheLanguageTextFile()
    {
        string? dir = GameFiles.TryFindGameDir();
        for (int lang = 0; lang < 6; lang++)
        {
            var tree = MenuTables.Load(TextFile.Load(Codes[lang], dir));
            var expected = tree.Screens.SelectMany(s => s.Items).First(i => i.ConfigOffset == 0x10).ValueTexts!
                .Select(t => t.Trim().ToUpperInvariant()).ToArray();
            Assert.Equal(expected, NetworkTrackNames.Load(dir, lang));
        }
        Assert.Equal(NetworkTrackNames.Load(dir, 0), NetworkTrackNames.Load(dir, PortLanguage.Current));   // default = English
    }

    [GameDataFact]
    public void ReviewPictures()
    {
        if (Environment.GetEnvironmentVariable("NFS_LANGUAGE_DIR") is not { } outDir) return;
        string? dir = GameFiles.TryFindGameDir();
        Directory.CreateDirectory(outDir);
        for (int lang = 0; lang < 6; lang++)
        {
            string tag = Codes[lang].ToLowerInvariant();
            Save(outDir, $"{tag}_main", MenuController.Load(dir, language: lang), dir);
            var setup = MenuController.Load(dir, language: lang);
            setup.HandleKey(MenuKey.Enter);
            Save(outDir, $"{tag}_setup", setup, dir);
            Save(outDir, $"{tag}_trackinfo", OpenTrackInfo(dir, lang), dir);
        }
    }

    static void Save(string outDir, string name, MenuController menu, string? dir)
    {
        var fb = new FrameBuffer();
        new MenuScene(menu, dir).Render(fb);
        File.WriteAllBytes(Path.Combine(outDir, name + ".png"), fb.EncodePng());
    }
}
