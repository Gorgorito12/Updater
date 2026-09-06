using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WarsOfLibertyLauncher.Services;
using Xunit;
using Node = WarsOfLibertyLauncher.Tests.TestXmb.Node;

namespace WarsOfLibertyLauncher.Tests;

/// <summary>
/// Reading XMB, the binary XML the game compiles its data into.
///
/// <para>It exists because two of the five catalogued mods ship no loose <c>civs.xml</c> at
/// all — Improvement Mod and Napoleonic Era keep theirs as <c>Data\civs.xml.xmb</c> inside a
/// <c>.bar</c> — so until this could be read, those mods had no civilization names and no
/// flags.</para>
///
/// <para>The documents are BUILT here rather than checked in. The real ones are 300 KB inside
/// a 551 MB archive, and what is worth pinning is the format, not a copy of somebody's mod.</para>
/// </summary>
public class XmbReaderTests
{
    // ---------------------------------------------------------------- a writer

    /// <summary>The writer lives in <see cref="TestXmb"/>: the packed civilization list needs it
    /// too, and two copies of a format would be two chances to fix only one of them.</summary>
    private static XmbNode Parse(Node root) =>
        XmbReader.Parse(TestXmb.Build(root))
        ?? throw new Xunit.Sdk.XunitException("the reader refused a document this test wrote.");

    // ---------------------------------------------------------------- the shape

    /// <summary>
    /// THE ONE THAT MATTERS. A civ block comes back with its elements, its text and its
    /// nesting — the shape the whole feature depends on.
    /// </summary>
    [Fact]
    public void THE_ONE_THAT_MATTERS_ACivBlockRoundTrips()
    {
        var root = Parse(new Node("civs", Children: new()
        {
            new Node("civ", Children: new()
            {
                new Node("name", "Germans"),
                new Node("portrait", @"objects\flags\germans"),
                new Node("displaynameid", "22866"),
                new Node("homecityflagtexture", @"War of the Triple Alliance\Flags\prussia"),
                new Node("matchmakingtextures", Children: new()
                {
                    new Node("portraittexture", @"WoL\ui\singleplayer\cpai_avatar_germans-sm"),
                }),
            }),
        }));

        Assert.Equal("civs", root.Name);
        var civ = Assert.Single(root.Children);

        Assert.Equal("Germans", civ.Value("name"));
        Assert.Equal("22866", civ.Value("displaynameid"));
        Assert.Equal(@"War of the Triple Alliance\Flags\prussia", civ.Value("homecityflagtexture"));

        // DIRECT children only. The nested portrait is a leader's face, not a flag, and this
        // is the guard that keeps it out of a flag lookup.
        Assert.Null(civ.Value("portraittexture"));
        Assert.Single(civ.Elements("matchmakingtextures"));
    }

    /// <summary>
    /// Names are compared without case, because the compiler LOWER-CASES them: the real
    /// string table's <c>&lt;String _locID=…&gt;</c> comes back as <c>string</c> / <c>_locid</c>.
    /// A case-sensitive lookup here does not throw and does not fail to build — it silently
    /// finds nothing.
    /// </summary>
    [Fact]
    public void LookupsIgnoreCase()
    {
        var root = Parse(new Node("civs", Children: new()
        {
            new Node("civ", Children: new() { new Node("name", "Zulu") }),
        }));

        Assert.Single(root.Elements("CIV"));
        Assert.Equal("Zulu", root.Elements("civ").First().Value("NAME"));
    }

    [Fact]
    public void AttributesComeBackWithTheirValues()
    {
        var root = Parse(new Node("stringtable", Children: new()
        {
            new Node("string", "Infinito", new() { ["_locid"] = "11727", ["symbol"] = "cStringInfinite" }),
        }));

        var s = Assert.Single(root.Children);
        Assert.Equal("Infinito", s.Text);
        Assert.Equal("11727", s.Attributes["_LOCID"]);
        Assert.Equal("cStringInfinite", s.Attributes["symbol"]);
    }

    [Fact]
    public void AnElementWithNoTextIsEmptyRatherThanNull()
    {
        var civ = Assert.Single(Parse(new Node("civs", Children: new()
        {
            new Node("civ", Children: new() { new Node("name") }),
        })).Children);

        Assert.Equal("", civ.Elements("name").First().Text);
        // Value() reports a blank as absent, so a caller cannot mistake one for a real value.
        Assert.Null(civ.Value("name"));
    }

    [Fact]
    public void DeepNestingSurvives()
    {
        var root = Parse(new Node("a", Children: new()
        {
            new Node("b", Children: new()
            {
                new Node("c", Children: new() { new Node("d", "deep") }),
            }),
        }));

        Assert.Equal("deep",
            root.Elements("b").First().Elements("c").First().Value("d"));
    }

    // ---------------------------------------------------------------- refusals

    /// <summary>
    /// A LOOSE <c>.XMB</c> is wrapped in <c>l33t</c> plus zlib. Refused by name rather than
    /// parsed into nonsense — an empty answer here would look exactly like a mod with no
    /// civilizations, which is a state the launcher draws without complaint.
    /// </summary>
    [Fact]
    public void ACompressedDocumentIsRefusedRatherThanMisread()
    {
        var wrapped = new byte[] { 0x6C, 0x33, 0x33, 0x74, 0xDC, 0xAD, 0x1C, 0x00, 0x78, 0x9C, 0x00 };
        Assert.Null(XmbReader.Parse(wrapped));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0x58, 0x31 })]                                  // "X1" and nothing else
    [InlineData(new byte[] { 0x58, 0x31, 4, 0, 0, 0, 0x5A, 0x5A, 0, 0, 0, 0 })] // wrong inner magic
    public void RubbishIsRefusedRatherThanThrowing(byte[] bytes) =>
        Assert.Null(XmbReader.Parse(bytes));

    /// <summary>Truncation is the realistic corruption — a partial read of an archive — and it
    /// must not walk off the end of the buffer.</summary>
    [Fact]
    public void ATruncatedDocumentIsRefusedAtEveryLength()
    {
        var whole = TestXmb.Build(new Node("civs", Children: new()
        {
            new Node("civ", Children: new() { new Node("name", "Germans") }),
        }));

        for (int cut = 1; cut < whole.Length; cut++)
        {
            var partial = whole[..cut];
            // No assertion on the RESULT: some prefixes are legitimately a shorter valid
            // document. What is pinned is that none of them throws.
            var ex = Record.Exception(() => XmbReader.Parse(partial));
            Assert.Null(ex);
        }
    }
}
