using System.Collections.Generic;
using System.Linq;
using WarsOfLibertyLauncher.Models.Multiplayer;
using WarsOfLibertyLauncher.Services.Multiplayer;
using Xunit;

namespace WarsOfLibertyLauncher.Tests;

/// <summary>
/// The community-cards table's decisions, away from the layout.
///
/// <para>What it replaced was a flat <c>Take(60)</c>: sixty rows every one of which said "1",
/// beside a civilization column repeating the same value twelve and twenty times. That is the
/// absence of a sample, printed. Every rule below is one the maps list or the civilization
/// balance already applies — brought here so a player does not meet three tables on one page
/// that disagree about what counts as evidence.</para>
/// </summary>
public class DeckStatsViewTests
{
    private static DeckCardEntry Card(string civ, string card, int players) =>
        new() { ModId = "wol", Civ = civ, Card = card, Players = players };

    /// <summary>Identity resolvers: what a mod calls a card is not this class's business.</summary>
    private static IReadOnlyList<DeckCivGroup> Group(
        IEnumerable<DeckCardEntry> rows, ISet<string>? expanded = null)
        => DeckStatsView.Group(rows.ToList(), c => c, c => c, expanded);

    /// <summary>A civilization with enough decks behind it to publish a share.</summary>
    private static List<DeckCardEntry> Sampled(string civ, params int[] counts)
    {
        var rows = new List<DeckCardEntry>();
        // The generic shipment every deck of every civilization carries. It IS the denominator.
        rows.Add(Card(civ, "HCShipWood300", 10));
        for (int i = 0; i < counts.Length; i++) rows.Add(Card(civ, $"{civ}Card{i}", counts[i]));
        return rows;
    }

    // ---- the denominator -------------------------------------------------

    /// <summary>
    /// THE ONE THAT MATTERS. The share is of THAT CIVILIZATION's decks.
    ///
    /// <para>The payload's only headcount is <c>Contributors</c>: players who shared a deck for
    /// the MOD. Dividing a Mexican card by that counts everyone who never played Mexico. There
    /// is no per-civilization count on the wire, but every deck carries the generic shipments,
    /// so the largest count inside a civilization IS its deck count.</para>
    /// </summary>
    [Fact]
    public void THE_ONE_THAT_MATTERS_TheDenominatorIsThisCivilizationsOwnDeckCount()
    {
        var groups = Group(Sampled("Aztecs", 5).Concat(Sampled("Zulu", 1)));

        Assert.Equal(10, groups.Single(g => g.Civ == "Aztecs").Decks);
        Assert.Equal(10, groups.Single(g => g.Civ == "Zulu").Decks);

        // 5 of 10 is half of the Aztec decks, whatever the rest of the mod did.
        var aztec = groups.Single(g => g.Civ == "Aztecs").Shown.Single(r => r.Players == 5);
        Assert.Equal(50, aztec.Percent);
    }

    /// <summary>
    /// A card that IS in somebody's deck is never reported as being in nobody's. One deck out
    /// of two hundred rounds to 1 %, not to 0 %.
    /// </summary>
    [Fact]
    public void ARareCardRoundsUpToOnePercentRatherThanDownToZero()
        => Assert.Equal(1, DeckStatsView.Percent(1, 200));

    [Fact]
    public void NoDecksIsNoPercentage() => Assert.Equal(0, DeckStatsView.Percent(3, 0));

    // ---- the sample minimum ----------------------------------------------

    /// <summary>
    /// THE STATE THE USER HAS TODAY. Every count is 1, so every civilization has one deck, so
    /// no percentage is publishable and every group is nothing but its summary row. That
    /// degenerate case is the one on the maintainer's screen right now, and it has to look
    /// deliberate rather than broken.
    /// </summary>
    [Fact]
    public void WithOneDeckPerCivilization_NothingIsShownButTheTail_AndNoPercentages()
    {
        var rows = Enumerable.Range(0, 18).Select(i => Card("Austrians", $"Card{i}", 1));

        var group = Assert.Single(Group(rows));

        Assert.Empty(group.Shown);
        Assert.Equal(18, group.Tail.Count);
        Assert.Equal(18, group.DistinctCards);
        Assert.All(group.Tail, r => Assert.Null(r.Percent));
    }

    /// <summary>
    /// One under the minimum publishes no share; one at it publishes every share in the group.
    /// The threshold is the civilization balance's own — two thresholds on one page is a page
    /// that contradicts itself.
    /// </summary>
    [Fact]
    public void FourDecksIsNotEnoughForAShare_FiveIs()
    {
        Assert.Equal(5, DeckStatsView.MinDecksForPercent);
        Assert.Equal(CivStatsView.MinDecidedForPercent, DeckStatsView.MinDecksForPercent);

        var four = Group(new[] { Card("Zulu", "Generic", 4), Card("Zulu", "Real", 2) });
        Assert.All(four[0].Shown, r => Assert.Null(r.Percent));

        var five = Group(new[] { Card("Zulu", "Generic", 5), Card("Zulu", "Real", 2) });
        Assert.All(five[0].Shown, r => Assert.NotNull(r.Percent));
        Assert.Equal(40, five[0].Shown.Single(r => r.Card == "Real").Percent);
    }

    // ---- the tail ---------------------------------------------------------

    [Fact]
    public void ACardSeenOnceGoesToTheTail_AndOneSeenTwiceEarnsItsRow()
    {
        var group = Assert.Single(Group(new[]
        {
            Card("Chinese", "Generic", 6),
            Card("Chinese", "Twice", 2),
            Card("Chinese", "Once", 1),
        }));

        Assert.Contains(group.Shown, r => r.Card == "Twice");
        Assert.Contains(group.Tail, r => r.Card == "Once");
        Assert.DoesNotContain(group.Tail, r => r.Card == "Twice");
    }

    /// <summary>Everything past the per-civilization cap joins the tail rather than vanishing:
    /// the counts under the table have to keep adding up.</summary>
    [Fact]
    public void RowsPastTheCapJoinTheTailRatherThanDisappearing()
    {
        var rows = Enumerable.Range(0, 20).Select(i => Card("Aztecs", $"Card{i}", 3));

        var group = Assert.Single(Group(rows));

        Assert.Equal(DeckStatsView.RowsShownPerCiv, group.Shown.Count);
        Assert.Equal(20, group.Shown.Count + group.Tail.Count);
        Assert.Equal(20, group.DistinctCards);
    }

    /// <summary>Expanding a civilization shows all of it and leaves no tail behind to click
    /// again.</summary>
    [Fact]
    public void AnExpandedCivilizationShowsEverything()
    {
        var rows = Enumerable.Range(0, 20).Select(i => Card("Aztecs", $"Card{i}", 1)).ToList();

        var group = Assert.Single(Group(rows, new HashSet<string> { "Aztecs" }));

        Assert.Equal(20, group.Shown.Count);
        Assert.Empty(group.Tail);
    }

    // ---- ordering ---------------------------------------------------------

    /// <summary>
    /// By times seen, never by rarity. The other direction puts the card somebody brought once
    /// at the top and calls it notable — the lie the civilization rules already name.
    /// </summary>
    [Fact]
    public void RowsAreOrderedByTimesSeen()
    {
        var group = Assert.Single(Group(new[]
        {
            Card("Zulu", "Rare", 1),
            Card("Zulu", "Common", 9),
            Card("Zulu", "Middling", 4),
        }, new HashSet<string> { "Zulu" }));

        Assert.Equal(new[] { "Common", "Middling", "Rare" }, group.Shown.Select(r => r.Card));
    }

    /// <summary>Groups by how much each civilization has to say, and ties by name so the page
    /// does not reshuffle itself between two visits.</summary>
    [Fact]
    public void GroupsAreOrderedByHowMuchTheyHold_TiesByName()
    {
        var groups = Group(new[]
        {
            Card("Zulu", "A", 1),
            Card("Aztecs", "A", 1), Card("Aztecs", "B", 1), Card("Aztecs", "C", 1),
            Card("Berbers", "A", 1),
        });

        Assert.Equal(new[] { "Aztecs", "Berbers", "Zulu" }, groups.Select(g => g.Civ));
    }

    // ---- the ordinary defensive cases -------------------------------------

    [Fact]
    public void NoRowsIsNoGroups() => Assert.Empty(Group(System.Array.Empty<DeckCardEntry>()));

    [Fact]
    public void ARowWithNoCardIsSkipped()
        => Assert.Empty(Group(new[] { Card("Zulu", "", 3) }));

    /// <summary>The label is what the resolver returned, and the fold key stays the INTERNAL
    /// name — a group keyed by a display name would lose its place the moment the mod
    /// resolved.</summary>
    [Fact]
    public void TheFoldKeyIsTheInternalNameAndTheLabelIsTheResolvedOne()
    {
        var groups = DeckStatsView.Group(
            new List<DeckCardEntry> { Card("SPCXulu", "HCXPRefrigeration", 2) },
            c => "Refrigeration",
            c => "Zulu");

        Assert.Equal("SPCXulu", groups[0].Civ);
        Assert.Equal("Zulu", groups[0].CivLabel);
        Assert.Equal("Refrigeration", groups[0].Shown.Concat(groups[0].Tail).Single().Label);
    }
}
