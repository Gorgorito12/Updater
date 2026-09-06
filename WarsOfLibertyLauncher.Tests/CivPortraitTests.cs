using System;
using System.IO;
using System.Text;
using WarsOfLibertyLauncher.Services.Multiplayer;
using Xunit;

namespace WarsOfLibertyLauncher.Tests;

/// <summary>
/// Reading each civilization's flag art path out of the mod's own <c>civs.xml</c>.
///
/// <para>The flag is taken from the MOD's files, not from anything the launcher ships, which is
/// what makes a reskin come out right: Struggle of Indonesia's block is still named
/// <c>Ottomans</c> internally but carries Surakarta's flag, and Surakarta is what the player
/// saw.</para>
///
/// <para><b>Why the element choice is pinned.</b> A <c>&lt;civ&gt;</c> block names up to five
/// pictures and only two of them are a standalone flag. <c>&lt;bannertexture&gt;</c> is a shared
/// atlas that means nothing without its <c>&lt;bannertexturecoords&gt;</c> crop, and
/// <c>&lt;portraittexture&gt;</c> sits one level down inside <c>&lt;matchmakingtextures&gt;</c>
/// and is a different picture entirely. Picking either by accident would put a wrong or a
/// smeared image beside a civilization's name, which nothing downstream could detect.</para>
/// </summary>
public class CivPortraitTests : IDisposable
{
    private readonly string _root;

    public CivPortraitTests()
    {
        _root = Directory.CreateTempSubdirectory("wol-civflag-").FullName;
        CivNameResolver.ResetCache();
    }

    public void Dispose()
    {
        CivNameResolver.ResetCache();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeMod(string name, string civsXml)
    {
        var install = Path.Combine(_root, name);
        var data = Path.Combine(install, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "civs.xml"), civsXml, Encoding.UTF8);
        return install;
    }

    /// <summary>
    /// Shaped after the real file: the elements in the order Wars of Liberty writes them,
    /// including the nested block and a civ that names no art at all.
    /// </summary>
    private const string Civs = """
    <?xml version="1.0" encoding="utf-8"?>
    <civs>
      <civ>
        <name>Ethiopians</name>
        <portrait>War of the Triple Alliance\Flags\abyss</portrait>
        <displaynameid>601967</displaynameid>
        <homecityflagtexture>War of the Triple Alliance\Flags\abyss</homecityflagtexture>
        <postgameflagtexture>War of the Triple Alliance\Flags\ingame_ui_postgame_flag_ethiopian</postgameflagtexture>
        <matchmakingtextures>
          <bannertexture>ui\eso\civ_flags_quick_launch_02</bannertexture>
          <bannertexturecoords>0 0.125 0.78125 0.25</bannertexturecoords>
          <portraittexture>WoL\ui\singleplayer\cpai_avatar_ethiopians-sm</portraittexture>
        </matchmakingtextures>
      </civ>
      <civ>
        <name>OnlyFlag</name>
        <displaynameid>1</displaynameid>
        <homecityflagtexture>objects\flags\fallback</homecityflagtexture>
      </civ>
      <civ>
        <name>Fale</name>
        <displaynameid>2</displaynameid>
      </civ>
      <civ>
        <name>OnlyAtlas</name>
        <displaynameid>3</displaynameid>
        <matchmakingtextures>
          <bannertexture>ui\eso\civ_flags_quick_launch_02</bannertexture>
          <bannertexturecoords>0 0.125 0.78125 0.25</bannertexturecoords>
          <portraittexture>WoL\ui\singleplayer\cpai_avatar_x-sm</portraittexture>
        </matchmakingtextures>
      </civ>
    </civs>
    """;

    /// <summary>
    /// THE ONE THAT MATTERS. <c>&lt;portrait&gt;</c> is the flag, and the nested pictures are
    /// not it — even though one of them is literally called a portrait.
    /// </summary>
    [Fact]
    public void THE_ONE_THAT_MATTERS_ThePortraitIsTakenAndTheNestedArtIsNot()
    {
        var map = CivNameResolver.ResolvePortraits(MakeMod("wol", Civs));

        Assert.Equal(@"War of the Triple Alliance\Flags\abyss", map["Ethiopians"]);
        Assert.DoesNotContain(map.Values, v => v.Contains("cpai_avatar", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(map.Values, v => v.Contains("civ_flags_quick_launch", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A civ with no <c>&lt;portrait&gt;</c> falls back to its home-city flag. Between
    /// the two, 185 of Wars of Liberty's 187 civilizations are covered.</summary>
    [Fact]
    public void TheHomeCityFlagIsTheFallback()
        => Assert.Equal(@"objects\flags\fallback",
            CivNameResolver.ResolvePortraits(MakeMod("wol", Civs))["OnlyFlag"]);

    /// <summary>
    /// A civ that names no standalone art is ABSENT, not present with an empty string. The
    /// caller draws no flag, which is the honest outcome — one real Wars of Liberty portrait
    /// path even names a file that does not exist.
    /// </summary>
    [Theory]
    [InlineData("Fale")]
    [InlineData("OnlyAtlas")]
    public void ACivWithNoStandaloneArtIsAbsent(string civ)
        => Assert.False(CivNameResolver.ResolvePortraits(MakeMod("wol", Civs)).ContainsKey(civ));

    /// <summary>
    /// Two mods that keep <c>civs.xml</c> inside <c>Data.bar</c> resolve nothing here — and they
    /// resolve no NAME either, so this is the same ordinary state rather than a new fault.
    /// </summary>
    [Fact]
    public void NoLooseCivsFileIsEmptyRatherThanAThrow()
    {
        var install = Path.Combine(_root, "packed");
        Directory.CreateDirectory(Path.Combine(install, "data"));
        Assert.Empty(CivNameResolver.ResolvePortraits(install));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoInstallPathIsEmpty(string? path)
        => Assert.Empty(CivNameResolver.ResolvePortraits(path));

    /// <summary>
    /// The reskin case, end to end on the art side: the block is named <c>Ottomans</c> and ships
    /// its own flag, so the picture belongs to the civilization the player actually saw.
    /// </summary>
    [Fact]
    public void AReskinnedCivKeepsItsOwnArt()
    {
        const string soi = """
        <?xml version="1.0" encoding="utf-8"?>
        <civs>
          <civ>
            <name>Ottomans</name>
            <portrait>objects\flags\ottomans</portrait>
            <displaynameid>22868</displaynameid>
          </civ>
        </civs>
        """;

        Assert.Equal(@"objects\flags\ottomans",
            CivNameResolver.ResolvePortraits(MakeMod("soi", soi))["Ottomans"]);
    }
}
