using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WarsOfLibertyLauncher.Models;
using WarsOfLibertyLauncher.Services;
using WarsOfLibertyLauncher.Services.Multiplayer;
using Xunit;

namespace WarsOfLibertyLauncher.Tests;

/// <summary>
/// Adding a mod is a DATA change and never a code change. Run: <c>dotnet test</c>.
///
/// <para>That is the contract this file exists to hold, and the deck-card resolver is where it
/// was most at risk: it is the newest thing on the statistics page and the one that reaches into
/// a mod's own files. If it ever grows a branch for a particular mod, adding the next mod stops
/// being a catalogue entry and starts being an implementation — which is exactly what the
/// request that produced it asked to prevent.</para>
///
/// <para>These are not integration tests. Nothing here installs a mod or reads a tech tree; the
/// resolvers underneath already have their own tests (<see cref="CardNameResolverTests"/>,
/// <see cref="CivNameResolverTests"/>). What is pinned is the SHAPE: every profile in the
/// catalogue goes down the same path, and a mod that is not installed degrades to identifiers
/// instead of to a crash or an empty table.</para>
/// </summary>
public class DeckCardNamesTests
{
    private static readonly string[] Cards = { "HCXPRefrigeration", "HCCigarRollers" };
    private static readonly string[] Civs = { "Mexicans", "Americans" };

    // ---------------------------------------------------------------- the contract

    [Fact]
    public async Task THE_ONE_THAT_MATTERS_EveryCataloguedModTakesTheSamePath()
    {
        // Walked over the WHOLE registry rather than a couple of known ids: a special case
        // written for one mod would show up here as one profile behaving differently, and a
        // hand-picked list would be updated alongside the special case and hide it.
        Assert.NotEmpty(ModRegistry.All);

        foreach (var profile in ModRegistry.All)
        {
            // Not installed, which is the state every mod is in on somebody's machine.
            var vocabulary = await DeckCardNames.ResolveAsync(
                profile.Id, _ => null, Cards, Civs);

            Assert.False(vocabulary.Resolved);

            // And the rows still draw: the identifier, never a blank and never an invention.
            Assert.Equal("HCXPRefrigeration", vocabulary.NameOf("HCXPRefrigeration"));
            Assert.Equal("Mexicans", vocabulary.CivOf("Mexicans"));
            Assert.Null(vocabulary.IconOf("HCXPRefrigeration"));
        }
    }

    [Fact]
    public async Task AnUnknownModIdIsNotAnException()
    {
        // The server names the mods it has matches for, and it can name one this build's
        // catalogue has never heard of. That has to be a quiet empty answer: the picker skips
        // the chip and the table shows identifiers.
        var vocabulary = await DeckCardNames.ResolveAsync(
            "a-mod-that-does-not-exist", _ => @"C:\nowhere", Cards, Civs);

        Assert.False(vocabulary.Resolved);
        Assert.Equal("HCCigarRollers", vocabulary.NameOf("HCCigarRollers"));
    }

    [Fact]
    public async Task NoModIdMeansNoWork()
    {
        bool asked = false;
        var vocabulary = await DeckCardNames.ResolveAsync(
            null, _ => { asked = true; return @"C:\nowhere"; }, Cards, Civs);

        Assert.False(vocabulary.Resolved);
        Assert.False(asked, "an absent mod id must not send anybody looking for an install");
    }

    // ---------------------------------------------------------------- the empty vocabulary

    [Fact]
    public void AnEmptyNameStaysEmptyRatherThanBecomingAnIdentifier()
    {
        // A blank card or civilization is a row the server should not have sent, and the right
        // answer to it is nothing at all - not the string "null" and not a stray placeholder.
        Assert.Equal("", DeckCardNames.Vocabulary.None.NameOf(null));
        Assert.Equal("", DeckCardNames.Vocabulary.None.NameOf("   "));
        Assert.Equal("", DeckCardNames.Vocabulary.None.CivOf(null));
    }

    [Fact]
    public void PeekAnswersNothingBeforeAnythingIsResolved()
    {
        // Null is "not read yet", which is what makes the render draw identifiers now and ask
        // for the names in the background. It must never be confused with an empty answer.
        Assert.Null(DeckCardNames.Peek(null));
        Assert.Null(DeckCardNames.Peek("   "));
        Assert.Null(DeckCardNames.Peek("a-mod-that-does-not-exist"));
    }

    [Fact]
    public async Task AnEmptyAnswerIsNotRemembered()
    {
        // The whole reason an empty result is not cached: the player may install the mod during
        // the session, and a cached "nothing" would keep the table on identifiers until the
        // launcher was restarted.
        var profile = ModRegistry.All.First();
        await DeckCardNames.ResolveAsync(profile.Id, _ => null, Cards, Civs);

        Assert.Null(DeckCardNames.Peek(profile.Id));
    }

    // ---------------------------------------------------------------- resolution itself

    [Fact]
    public void AResolvedNameWinsAndAMissingOneFallsBack()
    {
        // Built by hand rather than from a mod on disk: what is being pinned is the lookup, and
        // a test that needed an installed mod would simply not run on most machines.
        var vocabulary = new DeckCardNames.Vocabulary(
            new Dictionary<string, CardDetail>(StringComparer.Ordinal)
            {
                ["HCXPRefrigeration"] = new CardDetail("Refrigeración", null, null),
            },
            new Dictionary<string, System.Windows.Media.ImageSource>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Mexicans"] = "México",
            });

        Assert.True(vocabulary.Resolved);
        Assert.Equal("Refrigeración", vocabulary.NameOf("HCXPRefrigeration"));
        Assert.Equal("México", vocabulary.CivOf("Mexicans"));

        // One card resolving does not make the next one resolve. The table mixes both states in
        // the same column and the row decides its own styling from exactly this comparison.
        Assert.Equal("HCCigarRollers", vocabulary.NameOf("HCCigarRollers"));
        Assert.Equal("Americans", vocabulary.CivOf("Americans"));
    }

    // ---------- what a card SAYS ----------
    //
    // The table showed no description at all until this existed, and the reason was in the
    // data rather than the layout: every unit shipment and crate carries no RolloverTextID, so
    // the modder's sentence is null for most of a real table and the description with the
    // numbers has to be BUILT from the card's effects instead.

    private static DeckCardNames.Vocabulary WithLines(
        string? sentence, params string[] effects)
        => new(
            new Dictionary<string, CardDetail>(StringComparer.Ordinal)
            {
                ["HCXPRefrigeration"] = new CardDetail(
                    "Refrigeration", string.IsNullOrEmpty(sentence) ? null : sentence, null),
            },
            new Dictionary<string, System.Windows.Media.ImageSource>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            effects.Length == 0
                ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["HCXPRefrigeration"] = effects,
                });

    /// <summary>
    /// THE ONE THAT MATTERS. Both halves, in order — the modder's own sentence first, then the
    /// effects with their figures.
    ///
    /// <para>They are two independent blocks and NOT a fallback: a card can carry either, both
    /// or neither, and the deck detail panel has always drawn them that way. Treating the
    /// effects as a fallback would hide them on exactly the cards that carry both.</para>
    /// </summary>
    [Fact]
    public void THE_ONE_THAT_MATTERS_TheSentenceAndTheEffectsBothAppearInOrder()
        => Assert.Equal(
            new[]
            {
                "Trading Posts are cheaper and stronger.",
                "Trading Post: Changes Wood cost by -20.00%",
            },
            WithLines(
                "Trading Posts are cheaper and stronger.",
                "Trading Post: Changes Wood cost by -20.00%")
                .DescriptionLinesOf("HCXPRefrigeration"));

    /// <summary>
    /// The case that made the table look empty: no sentence, but effects that describe it
    /// perfectly well. Reading only <c>Description</c> returned nothing here.
    /// </summary>
    [Fact]
    public void ACardWithNoSentenceStillDescribesItselfThroughItsEffects()
        => Assert.Equal(
            new[] { "Delivers 10 Cheriks" },
            WithLines(null, "Delivers 10 Cheriks").DescriptionLinesOf("HCXPRefrigeration"));

    [Fact]
    public void ACardWithNoEffectsStillShowsTheSentence()
        => Assert.Equal(
            new[] { "Trading Posts are cheaper and stronger." },
            WithLines("Trading Posts are cheaper and stronger.")
                .DescriptionLinesOf("HCXPRefrigeration"));

    /// <summary>
    /// Neither is an ORDINARY answer, not a failure. A crate's effect targets the player and
    /// the engine has no wording for that, so the card genuinely says nothing beyond its own
    /// name — and the row that gets this must not offer to expand.
    /// </summary>
    [Fact]
    public void ACardWithNeitherSaysNothingRatherThanSomethingInvented()
        => Assert.Empty(WithLines(null).DescriptionLinesOf("HCXPRefrigeration"));

    [Fact]
    public void AVocabularyWithNoEffectsAtAllIsNotAnError()
    {
        // Three positional arguments: what the tests below build, and what the older callers
        // build. The two new members are optional precisely so this keeps working.
        var vocabulary = new DeckCardNames.Vocabulary(
            new Dictionary<string, CardDetail>(StringComparer.Ordinal)
            {
                ["HCXPRefrigeration"] = new CardDetail("Refrigeration", "A sentence.", null),
            },
            new Dictionary<string, System.Windows.Media.ImageSource>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal(new[] { "A sentence." }, vocabulary.DescriptionLinesOf("HCXPRefrigeration"));
        Assert.Null(vocabulary.CivIconOf("Mexicans"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SomethingElse")]
    public void AnUnknownCardSaysNothing(string? card)
        => Assert.Empty(WithLines("A sentence.", "An effect.").DescriptionLinesOf(card));

    [Fact]
    public void ACardWithNoNameFallsBackInsteadOfDrawingBlank()
    {
        // A detail that resolved but carries an empty name is worse than one that did not
        // resolve at all: it would blank the cell. The identifier is the floor.
        var vocabulary = new DeckCardNames.Vocabulary(
            new Dictionary<string, CardDetail>(StringComparer.Ordinal)
            {
                ["HCXPRefrigeration"] = new CardDetail("", null, null),
            },
            new Dictionary<string, System.Windows.Media.ImageSource>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal("HCXPRefrigeration", vocabulary.NameOf("HCXPRefrigeration"));
    }
}
