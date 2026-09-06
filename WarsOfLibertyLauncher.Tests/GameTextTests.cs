using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using WarsOfLibertyLauncher.Services;
using Xunit;

namespace WarsOfLibertyLauncher.Tests;

/// <summary>
/// The string-table cleaner. Small, but it is the difference between a card description and a
/// line of visible junk — measured on one real deck, 2 of its 23 descriptions carry markup.
/// </summary>
public class GameTextTests
{
    /// <summary>
    /// Both of the marked-up descriptions reachable from a real deck, verbatim from
    /// <c>stringtabley.xml</c>. The values are floats 0-1, which is why nothing tries to honour
    /// them as a colour.
    /// </summary>
    [Theory]
    [InlineData(
        "Nien Riders do more damage against Villagers and buildings. Manchus do more damage "
        + "against <color=0.74, 0.25, 0.11>assault units</color>.",
        "Nien Riders do more damage against Villagers and buildings. Manchus do more damage "
        + "against assault units.")]
    [InlineData(
        "Chu Ko Nu and Xiangu attack increased and do extra damage to light cavalry "
        + "and <color=0.19, 0.52, 0.76>line units</color>.",
        "Chu Ko Nu and Xiangu attack increased and do extra damage to light cavalry "
        + "and line units.")]
    public void ColourSpansComeOutAndTheSentenceSurvives(string raw, string expected) =>
        Assert.Equal(expected, GameText.Clean(raw));

    /// <summary>
    /// The overwhelmingly common case, and the one a careless stripper would damage: a
    /// description with no markup at all has to come back identical.
    /// </summary>
    [Theory]
    [InlineData("You get a Trading Post Rickshaw, and Trading Posts are cheaper and stronger.")]
    [InlineData("Wood source.")]
    [InlineData("Ships 1 Mariscala.")]
    public void PlainTextIsUntouched(string raw) => Assert.Equal(raw, GameText.Clean(raw));

    /// <summary>
    /// <b>Why the stripper is not <c>&lt;[^&gt;]+&gt;</c>.</b> These strings are written by
    /// modders, and a general rule would eat a lone comparison sign together with everything up
    /// to the next one — turning a sentence into half a sentence, silently.
    /// </summary>
    [Fact]
    public void ALoneAngleBracketDoesNotSwallowTheRestOfTheLine()
    {
        Assert.Equal("Range < 12 and speed > 4", GameText.Clean("Range < 12 and speed > 4"));
        Assert.Equal("Damage <br> up", GameText.Clean("Damage <br> up"));
    }

    /// <summary>The table writes line breaks as the two characters backslash and n.</summary>
    [Fact]
    public void AWrittenOutNewlineBecomesARealOne() =>
        Assert.Equal("Cost:\nWood", GameText.Clean(@"Cost:\nWood"));

    [Fact]
    public void AnInlineIconReferenceIsRemoved() =>
        Assert.Equal("Pop:", GameText.Clean(@"Pop: <icon=""(32)(ui/ingame/resource_population)"">"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingComesBackAsAnEmptyString(string? raw) => Assert.Equal("", GameText.Clean(raw));

    [Fact]
    public void SurroundingSpaceIsTrimmed() => Assert.Equal("Wood source.", GameText.Clean("  Wood source.  "));

    // ---------------- Parse: the same markup, honoured instead of removed ----------------
    //
    // Clean() is for anything that assigns a string. Parse() is for the surfaces that can
    // paint. The rule both serve is the same one: the markup itself NEVER reaches a player.
    // The community-cards table broke it, and rows read
    // "10 Dragoons <color=1.0, 1.0, 0.0>+ 3 Hussars</color>".

    /// <summary>
    /// THE ONE THAT MATTERS. The span becomes a colour, and the words on either side of it
    /// keep their own.
    /// </summary>
    [Fact]
    public void ASpanBecomesAColouredStretchInsideAnUncolouredLine()
    {
        var runs = GameText.Parse("5 Settlers <color=1.0, 1.0, 0.0>Instead of Hussars</color> here");

        Assert.Equal(3, runs.Count);
        Assert.Equal("5 Settlers ", runs[0].Text);
        Assert.Null(runs[0].Colour);
        Assert.Equal("Instead of Hussars", runs[1].Text);
        Assert.NotNull(runs[1].Colour);
        Assert.Equal(" here", runs[2].Text);
        Assert.Null(runs[2].Colour);
    }

    /// <summary>
    /// THE SILENT ONE. Whatever happens, no fragment of a tag survives into the text. A
    /// caller cannot check this for itself: it gets strings, and a bad one just renders.
    /// </summary>
    [Theory]
    [InlineData("10 Dragoons <color=1.0, 1.0, 0.0>+ 3 Hussars</color>")]
    [InlineData("<color=0.74, 0.25, 0.11>assault units</color>")]
    [InlineData("unclosed <color=1.0, 0.0, 0.0>span")]
    [InlineData("<color=nonsense>text</color>")]
    [InlineData("<color=2.0, -1.0, 9.9>out of range</color>")]
    [InlineData("<color>no values</color>")]
    [InlineData("stray </color> close")]
    [InlineData(@"icon <icon=""(32)(ui/x)""> gone")]
    public void NoMarkupEverSurvives(string raw)
    {
        var joined = string.Concat(GameText.Parse(raw).Select(r => r.Text));

        Assert.DoesNotContain("<color", joined);
        Assert.DoesNotContain("</color", joined);
        Assert.DoesNotContain("<icon", joined);
    }

    /// <summary>
    /// A triple that does not resolve loses the TAG and keeps the WORDS. That is the
    /// documented second-best outcome; dropping the sentence with the tag would be worse than
    /// the bug being fixed.
    /// </summary>
    [Theory]
    [InlineData("<color=nonsense>5 Settlers</color>")]
    [InlineData("<color=2.0, 0.5, 0.5>5 Settlers</color>")]
    [InlineData("<color>5 Settlers</color>")]
    public void AnUnresolvableColourKeepsTheWords(string raw)
    {
        var runs = GameText.Parse(raw);

        Assert.Equal("5 Settlers", string.Concat(runs.Select(r => r.Text)));
        Assert.All(runs, r => Assert.Null(r.Colour));
    }

    /// <summary>
    /// ANY triple, not the one from the screenshot. Two real spans from one real deck, plus
    /// pure yellow: substituting the literal string somebody happened to see would leave every
    /// other span on screen as raw markup.
    /// </summary>
    [Theory]
    [InlineData("0.74, 0.25, 0.11")]
    [InlineData("0.19, 0.52, 0.76")]
    [InlineData("1.0, 1.0, 0.0")]
    [InlineData("0,0,1")]
    [InlineData(".5,.5,.5")]
    public void EveryTripleIsParsedRatherThanMatched(string triple)
    {
        var runs = GameText.Parse($"<color={triple}>x</color>");

        Assert.Single(runs);
        Assert.NotNull(runs[0].Colour);
    }

    /// <summary>
    /// The hue the modder wrote survives; only saturation and lightness are pulled into the
    /// band that reads on the launcher's dark panels.
    ///
    /// <para>This is the one deviation from the design handoff, and it is deliberate. That
    /// document maps 1.0, 1.0, 0.0 to the launcher's gold #E6C86A. That gold is hue 45°; pure
    /// yellow is 60°. No clamp moves one to the other without rotating every other colour by
    /// the same amount, which would misreport the reds and blues the real tables use. Same
    /// lightness, same saturation, same weight on the page — one shade greener.</para>
    /// </summary>
    [Fact]
    public void PureYellowLandsOnALegibleGold()
    {
        var colour = GameText.Parse("<color=1.0, 1.0, 0.0>x</color>")[0].Colour;
        Assert.Equal(Color.FromRgb(0xE6, 0xE6, 0x6A), colour);
    }

    /// <summary>
    /// Pure blue is near-black against #12213a. Honouring it verbatim would render the
    /// modder's emphasis as a disappearance.
    /// </summary>
    [Fact]
    public void ANearBlackColourIsLiftedIntoReadableRange()
    {
        var colour = GameText.Parse("<color=0.0, 0.0, 1.0>x</color>")[0].Colour;

        Assert.NotNull(colour);
        // Well clear of the panel it sits on.
        Assert.True(colour!.Value.R + colour.Value.G + colour.Value.B > 300,
            $"{colour} would vanish against the panel.");
        // Still blue: B leads, and by a distance.
        Assert.True(colour.Value.B > colour.Value.R + 40 && colour.Value.B > colour.Value.G + 40,
            $"{colour} is no longer the colour the modder asked for.");
    }

    /// <summary>
    /// THE ONE A SPANISH WINDOWS WOULD HAVE BROKEN. The table writes "1.0"; under es-ES the
    /// thread's own culture reads that as ten, which is out of range — so every span would
    /// silently lose its colour, for most of this player base.
    /// </summary>
    [Fact]
    public void TheTripleIsReadInInvariantCultureNotTheThreadsOwn()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("es-ES");
            var colour = GameText.Parse("<color=1.0, 1.0, 0.0>x</color>")[0].Colour;
            Assert.Equal(Color.FromRgb(0xE6, 0xE6, 0x6A), colour);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    /// <summary>Parse and Clean lay the same string out the same way, or one surface would
    /// indent differently from another for no reason a reader could see.</summary>
    [Theory]
    [InlineData("  Wood source.  ")]
    [InlineData("5 Settlers <color=1.0, 1.0, 0.0>and more</color>")]
    [InlineData(@"Cost:\nWood")]
    [InlineData("Range < 12 and speed > 4")]
    public void ParseAndCleanAgreeOnTheWords(string raw)
        => Assert.Equal(GameText.Clean(raw), string.Concat(GameText.Parse(raw).Select(r => r.Text)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingParsesToNoRuns(string? raw) => Assert.Empty(GameText.Parse(raw));

    /// <summary>Adjacent stretches of the same colour are one stretch, not two that only look
    /// like one.</summary>
    [Fact]
    public void AdjacentStretchesOfTheSameColourAreMerged()
    {
        var runs = GameText.Parse("a</color>b");

        Assert.Single(runs);
        Assert.Equal("ab", runs[0].Text);
    }
}
