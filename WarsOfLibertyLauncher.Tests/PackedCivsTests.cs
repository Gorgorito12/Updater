using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WarsOfLibertyLauncher.Services.Multiplayer;
using Xunit;
using Node = WarsOfLibertyLauncher.Tests.TestXmb.Node;

namespace WarsOfLibertyLauncher.Tests;

/// <summary>
/// The civilization list of a mod that ships NO loose <c>civs.xml</c>.
///
/// <para>Two of the four catalogued mods are like that: Improvement Mod keeps its 91
/// civilizations as <c>Data\civs.xml.xmb</c> inside <c>ImpMod.bar</c>, Napoleonic Era its 88
/// inside <c>DataPN.bar</c>. Until this path existed both were drawn with no civilization name
/// and no flag at all, and that was written down as their ordinary state rather than as a
/// fault.</para>
/// </summary>
public class PackedCivsTests : IDisposable
{
    private readonly string _root;

    public PackedCivsTests()
    {
        _root = Directory.CreateTempSubdirectory("wol-packedcivs-").FullName;
        CivNameResolver.ResetCache();
    }

    public void Dispose()
    {
        CivNameResolver.ResetCache();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// An install with no loose <c>civs.xml</c>: the list lives in an archive, exactly as the two
    /// mods ship it. The archive is named after nothing in particular on purpose — the resolver
    /// finds it by pattern, and a test that used the real name would pass even if the code had
    /// been written to look for that name.
    /// </summary>
    private string MakeMod(string name, string barFile, string? stringTable, params Node[] civs)
    {
        var install = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(install, "data"));

        var document = TestXmb.Build(new Node("civs", Children: new List<Node>(civs)));
        TestArchive.Write(
            Path.Combine(install, barFile),
            TestArchive.Entry(@"Data\civs.xml.xmb", document));

        if (stringTable != null)
        {
            // UTF-16 with a BOM, which is what AoE3 actually ships.
            File.WriteAllText(Path.Combine(install, "data", "stringtablem.xml"), stringTable,
                new UnicodeEncoding(false, true));
        }

        return install;
    }

    /// <summary>The <c>m</c> layer, which is the mod's own — Improvement Mod puts all 57 of its
    /// civilization names here and nowhere else. Declared UTF-16 because that is what it is
    /// written as, and a declaration that disagrees with the bytes costs the whole file.</summary>
    private const string Table = """
    <?xml version="1.0" encoding="UTF-16"?>
    <StringTable version='8'>
      <Language name='English'>
        <String _locID ='22861'>Britain</String>
        <String _locID ='22866'>Prussia</String>
      </Language>
    </StringTable>
    """;

    // ------------------------------------------------------------------ the shape

    /// <summary>
    /// THE ONE THAT MATTERS. A name and a flag come out of the archive, which is the whole
    /// point: both are blank today for these two mods.
    /// </summary>
    [Fact]
    public void THE_ONE_THAT_MATTERS_ANameAndAFlagComeOutOfTheArchive()
    {
        var mod = MakeMod("packed", "ImpSomething.bar", Table,
            TestXmb.Civ("British", "22861", flag: @"objects\flags\british"),
            TestXmb.Civ("Germans", "22866", flag: @"objects\flags\germans"));

        Assert.Equal("Britain", CivNameResolver.ResolveByInternalName(mod, "British"));
        Assert.Equal("Prussia", CivNameResolver.ResolveByInternalName(mod, "Germans"));

        var art = CivNameResolver.ResolvePortraits(mod);
        Assert.Equal(@"objects\flags\british", art["British"]);
        Assert.Equal(@"objects\flags\germans", art["Germans"]);
    }

    /// <summary>Document order is index order and the index is 1-BASED, the same rule the loose
    /// file follows — a recording reports the civilization as a number into this list, and
    /// reading it 0-based lands one civ short every time.</summary>
    [Fact]
    public void TheIndexIsDocumentOrderAndOneBased()
    {
        var mod = MakeMod("byindex", "Mod.bar", Table,
            TestXmb.Civ("British", "22861"),
            TestXmb.Civ("Germans", "22866"));

        Assert.Equal("Britain", CivNameResolver.Resolve(mod, 1));
        Assert.Equal("Prussia", CivNameResolver.Resolve(mod, 2));
        Assert.Null(CivNameResolver.Resolve(mod, 3));
    }

    /// <summary>The mod's own flag beats a stale portrait here too — see
    /// <see cref="CivPortraitTests"/> for why that order is not the obvious one.</summary>
    [Fact]
    public void TheModsOwnFlagStillBeatsTheStalePortrait()
    {
        var mod = MakeMod("packedflag", "Mod.bar", null,
            TestXmb.Civ("Germans", "22866",
                flag: @"War of the Triple Alliance\Flags\prussia",
                portrait: @"objects\flags\germans"));

        Assert.Equal(@"War of the Triple Alliance\Flags\prussia",
            CivNameResolver.ResolvePortraits(mod)["Germans"]);
    }

    // ------------------------------------------------------------------ which list

    /// <summary>
    /// <b>THE ONE THAT LOOKED LIKE IT WORKED.</b> A real install carries SEVEN civilization
    /// lists — the engine's override layers, 26 civilizations then 45 then 60 and finally the
    /// mod's 91 — and only the fullest is the one the game plays with.
    ///
    /// <para>Taking the first archive that answered picked the 26-civ layer, by alphabet, and
    /// drew twenty-six base-game civilizations under the mod's name. Worse than the missing
    /// count: the layers RENUMBER, so the civilizations it did show were labelled wrong. Neither
    /// symptom throws and neither leaves the screen blank.</para>
    ///
    /// <para>The short layer here is named to sort first for exactly that reason.</para>
    /// </summary>
    [Fact]
    public void THE_ONE_THAT_MATTERS_TheFullestListWinsNotTheFirstOneFound()
    {
        var install = Path.Combine(_root, "sevenlists");
        Directory.CreateDirectory(Path.Combine(install, "data"));
        File.WriteAllText(Path.Combine(install, "data", "stringtablem.xml"), Table,
            new UnicodeEncoding(false, true));

        // The base game's layer: fewer civilizations, and in a different order.
        TestArchive.Write(Path.Combine(install, "DataP.bar"),
            TestArchive.Entry(@"Data\civs.xml.xmb", TestXmb.Build(
                new Node("civs", Children: new()
                {
                    TestXmb.Civ("Aztecs", "3", flag: @"objects\flags\aztecs"),
                    TestXmb.Civ("British", "22861", flag: @"objects\flags\british"),
                }))));

        // And the same again under data\, which is where the oldest copy lives.
        TestArchive.Write(Path.Combine(install, "data", "Data.bar"),
            TestArchive.Entry(@"civs.xml.xmb", TestXmb.Build(
                new Node("civs", Children: new()
                {
                    TestXmb.Civ("Aztecs", "3", flag: @"objects\flags\aztecs"),
                    TestXmb.Civ("British", "22861", flag: @"objects\flags\british"),
                }))));

        // The mod's: more civilizations, and Aztecs has moved.
        TestArchive.Write(Path.Combine(install, "TheMod.bar"),
            TestArchive.Entry(@"Data\civs.xml.xmb", TestXmb.Build(
                new Node("civs", Children: new()
                {
                    TestXmb.Civ("British", "22861", flag: @"themod\flags\british"),
                    TestXmb.Civ("Aztecs", "3", flag: @"themod\flags\aztecs"),
                    TestXmb.Civ("Argentinians", "4", flag: @"themod\flags\argentinians"),
                }))));

        var art = CivNameResolver.ResolvePortraits(install);

        Assert.Equal(@"themod\flags\british", art["British"]);
        Assert.True(art.ContainsKey("Argentinians"), "the shorter layer won: a civilization is missing.");

        // And the numbering is the fullest list's, not the short one's.
        Assert.Equal("Britain", CivNameResolver.Resolve(install, 1));
    }

    /// <summary>And <c>data\Data.bar</c> is still read when it is the only one — a mod that ships
    /// no archive of its own resolves through exactly the same path.</summary>
    [Fact]
    public void DataBarIsReadWhenNothingElseAnswers()
    {
        var install = Path.Combine(_root, "onlydata");
        Directory.CreateDirectory(Path.Combine(install, "data"));

        TestArchive.Write(Path.Combine(install, "data", "Data.bar"),
            TestArchive.Entry(@"Data\civs.xml.xmb", TestXmb.Build(
                new Node("civs", Children: new() { TestXmb.Civ("Dutch", "1", flag: @"objects\flags\dutch") }))));

        Assert.Equal(@"objects\flags\dutch", CivNameResolver.ResolvePortraits(install)["Dutch"]);
    }

    // ------------------------------------------------------------------ the loose file wins

    /// <summary>
    /// A mod that ships BOTH reads the loose file. Wars of Liberty is that mod, and the archives
    /// beside it hold the base game's list — reading those instead would replace ninety-one
    /// civilizations with fourteen on the one mod that was working.
    /// </summary>
    [Fact]
    public void ALooseCivsFileWinsOverEveryArchive()
    {
        var install = Path.Combine(_root, "both");
        var data = Path.Combine(install, "data");
        Directory.CreateDirectory(data);

        File.WriteAllText(Path.Combine(data, "civs.xml"), """
        <?xml version="1.0" encoding="utf-8"?>
        <civs>
          <civ>
            <name>Ethiopians</name>
            <displaynameid>601967</displaynameid>
            <homecityflagtexture>loose\flags\abyss</homecityflagtexture>
          </civ>
        </civs>
        """, Encoding.UTF8);

        TestArchive.Write(Path.Combine(install, "Mod.bar"),
            TestArchive.Entry(@"Data\civs.xml.xmb", TestXmb.Build(
                new Node("civs", Children: new() { TestXmb.Civ("Packed", "1", flag: @"packed\flag") }))));

        var art = CivNameResolver.ResolvePortraits(install);

        Assert.Equal(@"loose\flags\abyss", art["Ethiopians"]);
        Assert.False(art.ContainsKey("Packed"), "the archive was read over the loose file.");
    }

    // ------------------------------------------------------------------ refusals

    /// <summary>An install with neither is empty rather than a throw. Struggle of Indonesia and
    /// Wars of Liberty never reach this path at all; a broken install must not take the tab
    /// down.</summary>
    [Fact]
    public void NothingToReadIsEmptyRatherThanAThrow()
    {
        var install = Path.Combine(_root, "nothing");
        Directory.CreateDirectory(Path.Combine(install, "data"));

        Assert.Empty(CivNameResolver.ResolvePortraits(install));
        Assert.Null(CivNameResolver.Resolve(install, 1));
    }

    /// <summary>An archive holding something that is not a civ list is skipped, not fatal — the
    /// root of these installs holds five or six archives and only one carries the list.</summary>
    [Fact]
    public void AnArchiveWithoutACivListIsSkipped()
    {
        var install = Path.Combine(_root, "sound");
        Directory.CreateDirectory(Path.Combine(install, "data"));

        TestArchive.Write(Path.Combine(install, "Sound.bar"),
            TestArchive.Entry(@"Sound\music.wav", new byte[] { 1, 2, 3 }));
        TestArchive.Write(Path.Combine(install, "Zmod.bar"),
            TestArchive.Entry(@"Data\civs.xml.xmb", TestXmb.Build(
                new Node("civs", Children: new() { TestXmb.Civ("Swedish", "1", flag: @"mod\flags\swedish") }))));

        Assert.Equal(@"mod\flags\swedish", CivNameResolver.ResolvePortraits(install)["Swedish"]);
    }
}
