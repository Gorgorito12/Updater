using System;
using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WarsOfLibertyLauncher.Services;
using Xunit;

namespace WarsOfLibertyLauncher.Tests;

/// <summary>
/// Finding a texture across the layers an install keeps it in.
///
/// <para><b>Two things here are measured facts about the files, not conventions.</b> The
/// base game's <c>art\*.bar</c> names its entries <c>objects\flags\x.ddt</c>; a mod's archive at
/// the install ROOT names the same texture <c>Art\objects\flags\x.ddt</c>. And when the same
/// name appears in both, the ROOT one is the mod's replacement — Improvement Mod overrides 2,282
/// textures that way, thirty of them civilization flags. Get either wrong and the launcher draws
/// the base game's picture beside the mod's civilization while looking like it works, which is
/// exactly the bug that was reported.</para>
/// </summary>
public class CardArtServiceTests : IDisposable
{
    private readonly string _root;

    public CardArtServiceTests()
    {
        _root = Directory.CreateTempSubdirectory("wol-cardart-").FullName;
        CardArtService.ResetCache();
    }

    public void Dispose()
    {
        CardArtService.ResetCache();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// A <c>.ddt</c> of the given size, so a test can tell which copy of a texture came back by
    /// looking at the picture rather than at a path.
    /// </summary>
    private static byte[] Ddt(int size)
    {
        var header = new byte[DdtDecoder.HeaderBytes];
        header[0] = 0x52; header[1] = 0x54; header[2] = 0x53; header[3] = 0x33;   // RTS3
        header[4] = 1;                          // usage
        header[5] = 8;                          // alpha bits
        header[6] = DdtDecoder.FormatRaw;
        header[7] = 1;                          // mip levels
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), size);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), size);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), DdtDecoder.HeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), size * size * 4);

        var file = new byte[header.Length + size * size * 4];
        header.CopyTo(file, 0);
        return file;
    }

    private string Install(string name)
    {
        var install = Path.Combine(_root, name);
        Directory.CreateDirectory(install);
        return install;
    }

    private static int? WidthOf(string install, string icon)
    {
        var art = CardArtService.Load(install, new[] { icon });
        return art.TryGetValue(icon, out var image) ? (int)((BitmapSource)image).PixelWidth : null;
    }

    // ------------------------------------------------------------------ the prefix

    /// <summary>
    /// THE ONE THAT MATTERS for the reported bug. The XML asks for <c>objects\flags\germans</c>;
    /// the mod's archive calls it <c>Art\objects\flags\germans.ddt</c>. Before the prefix was
    /// normalized away at index time, that never matched and the flag simply did not appear.
    /// </summary>
    [Fact]
    public void THE_ONE_THAT_MATTERS_AnEntryNamedWithTheArtPrefixIsFound()
    {
        var install = Install("prefixed");
        TestArchive.Write(Path.Combine(install, "TheMod.bar"),
            TestArchive.Entry(@"Art\objects\flags\germans.ddt", Ddt(4)));

        Assert.Equal(4, WidthOf(install, @"objects\flags\germans"));
    }

    /// <summary>And the base game's own naming, without the prefix, still resolves — that is
    /// where three quarters of a real deck's card icons come from.</summary>
    [Fact]
    public void AnEntryNamedWithoutThePrefixIsStillFound()
    {
        var install = Install("plain");
        TestArchive.Write(Path.Combine(install, "art", "Art1.bar"),
            TestArchive.Entry(@"ui\techs\hc_trade_empire.ddt", Ddt(2)));

        Assert.Equal(2, WidthOf(install, @"ui\techs\hc_trade_empire"));
    }

    /// <summary>An <c>&lt;Icon&gt;</c> value that carries the prefix itself resolves too. Both
    /// spellings occur in the shipped XML.</summary>
    [Fact]
    public void AnIconValueThatCarriesThePrefixResolves()
    {
        var install = Install("askedwith");
        TestArchive.Write(Path.Combine(install, "art", "Art1.bar"),
            TestArchive.Entry(@"ui\techs\hc.ddt", Ddt(2)));

        Assert.Equal(2, WidthOf(install, @"art\ui\techs\hc"));
    }

    // ------------------------------------------------------------------ the order

    /// <summary>
    /// <b>THE OTHER ONE THAT MATTERS.</b> The same texture in both layers: the mod's replacement
    /// at the install root wins over the base game's under <c>art\</c>. With the order the other
    /// way round the launcher drew the vanilla British and French flags beside Improvement Mod's
    /// civilizations — a wrong picture, not a missing one, which nothing downstream can detect.
    /// </summary>
    [Fact]
    public void THE_ONE_THAT_MATTERS_TheModsReplacementBeatsTheBaseGamesTexture()
    {
        var install = Install("override");
        TestArchive.Write(Path.Combine(install, "art", "Art2.bar"),
            TestArchive.Entry(@"objects\flags\french.ddt", Ddt(2)));
        TestArchive.Write(Path.Combine(install, "TheMod.bar"),
            TestArchive.Entry(@"Art\objects\flags\french.ddt", Ddt(4)));

        Assert.Equal(4, WidthOf(install, @"objects\flags\french"));
    }

    /// <summary>A loose file on disk still beats every archive: that is where Wars of Liberty
    /// puts the flags it ships.</summary>
    [Fact]
    public void ALooseFileBeatsTheArchives()
    {
        var install = Install("loose");
        var folder = Path.Combine(install, "art", "objects", "flags");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "prussia.ddt"), Ddt(8));

        TestArchive.Write(Path.Combine(install, "TheMod.bar"),
            TestArchive.Entry(@"Art\objects\flags\prussia.ddt", Ddt(4)));

        Assert.Equal(8, WidthOf(install, @"objects\flags\prussia"));
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public void AnInstallWithNoArtAtAllIsEmptyRatherThanAThrow()
        => Assert.Empty(CardArtService.Load(Install("bare"), new[] { @"objects\flags\x" }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoInstallPathIsEmpty(string? path)
        => Assert.Empty(CardArtService.Load(path, new[] { @"objects\flags\x" }));

    /// <summary>A path the archives do not carry is simply absent, so the card draws without a
    /// picture instead of taking the grid down.</summary>
    [Fact]
    public void AMissingTextureIsAbsent()
    {
        var install = Install("missing");
        TestArchive.Write(Path.Combine(install, "TheMod.bar"),
            TestArchive.Entry(@"Art\objects\flags\german.ddt", Ddt(4)));

        Assert.Null(WidthOf(install, @"objects\flags\nobody"));
    }
}
