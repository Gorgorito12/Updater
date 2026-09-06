using System;
using System.Collections.Generic;
using System.Linq;
using WarsOfLibertyLauncher.Models.Multiplayer;

namespace WarsOfLibertyLauncher.Services.Multiplayer;

/// <summary>One card, as the community-cards table draws it.</summary>
/// <param name="Card">The internal name, for the icon and the tooltip lookups.</param>
/// <param name="Label">What to show. Already resolved; never an internal name if a name exists.</param>
/// <param name="Players">How many of that civilization's shared decks carry it.</param>
/// <param name="Percent">
/// The share of that civilization's decks, or NULL when there is not enough sample to state one.
/// Null means nothing is drawn in its place — not a dash, and never a 0.
/// </param>
public readonly record struct DeckCardRow(string Card, string Label, int Players, int? Percent);

/// <summary>One civilization's group.</summary>
/// <param name="Civ">The internal name, used as the fold key so it survives a repaint.</param>
/// <param name="CivLabel">What to show as the group header.</param>
/// <param name="DistinctCards">Every distinctive card seen for it, tail included.</param>
/// <param name="Decks">
/// How many decks were shared for this civilization — the denominator. See
/// <see cref="DeckStatsView.Group"/> for why it is the maximum and not a field from the server.
/// </param>
/// <param name="Shown">The rows that earned their own line.</param>
/// <param name="Tail">The ones seen once, folded into a summary row.</param>
public sealed record DeckCivGroup(
    string Civ,
    string CivLabel,
    int DistinctCards,
    int Decks,
    IReadOnlyList<DeckCardRow> Shown,
    IReadOnlyList<DeckCardRow> Tail);

/// <summary>
/// Turns <c>/stats/decks</c> into the per-civilization table the STATS tab draws.
///
/// <para>Pure and WPF-free, like <see cref="CivStatsView"/> and <see cref="CommunityStatsView"/>
/// beside it, for the same reason: what is worth getting right here is a set of decisions, not a
/// layout.</para>
///
/// <para><b>What the table looked like before.</b> A flat <c>Take(60)</c> over the server's rows.
/// With today's data that is sixty lines every one of which says "1", beside a civilization
/// column repeating the same value twelve and twenty times in a row. That is not a data set; it
/// is the absence of one, printed. The rules below are the ones the maps list and the
/// civilization balance already apply, brought to this table so a player does not meet three
/// tables on one page that disagree about what counts as evidence.</para>
/// </summary>
public static class DeckStatsView
{
    /// <summary>
    /// How many shared decks a civilization needs before a percentage is published for it.
    ///
    /// <para>Deliberately the same number as <see cref="CivStatsView.MinDecidedForPercent"/>.
    /// Two tables on the same page with two different thresholds is a page that contradicts
    /// itself; if one of them moves, both should.</para>
    /// </summary>
    public const int MinDecksForPercent = CivStatsView.MinDecidedForPercent;

    /// <summary>
    /// How many decks a card must appear in to earn its own row. Mirrors
    /// <c>MultiplayerTab.MapRowMinMatches</c>: below it, the card goes to the summary row.
    /// </summary>
    public const int RowMinPlayers = 2;

    /// <summary>Rows drawn per civilization before the rest joins the tail. Mirrors
    /// <c>MultiplayerTab.MapRowsShown</c>.</summary>
    public const int RowsShownPerCiv = 7;

    /// <summary>
    /// Civilization groups drawn before a "see all".
    ///
    /// <para>Wars of Liberty ships 188 civilizations and <c>/stats/decks</c> is not bounded by
    /// civilization, so the GROUPS need a cap of their own — otherwise a page that fixed sixty
    /// meaningless rows could grow a hundred and eighty meaningless headers.</para>
    /// </summary>
    public const int CivGroupsShown = 12;

    /// <summary>
    /// Group the server's rows by civilization, newest evidence first.
    ///
    /// <para><b>Where the denominator comes from, and why not from the payload.</b> The response
    /// carries <c>Contributors</c>: how many players shared a deck for this MOD. Dividing a
    /// Mexican card by that counts everyone who never played Mexico, so it is not the
    /// denominator for anything. There is no per-civilization deck count on the wire either. But
    /// every deck of every civilization contains the generic resource shipments — "Cords of 300
    /// wood" and its siblings appear in all of them — so the LARGEST <c>Players</c> value inside
    /// a civilization is the number of decks shared for that civilization. That is the
    /// denominator, and it is computed here rather than guessed at the view.</para>
    ///
    /// <para><b>Ordered by times seen, never by rarity.</b> The other direction puts the card
    /// somebody brought once at the top of the table and calls it notable — the same lie the
    /// civilization rules already name. Ties break on the label so the list does not reshuffle
    /// itself between two visits to the tab.</para>
    /// </summary>
    /// <param name="rows">The payload's rows. Nulls and rows with no card are skipped.</param>
    /// <param name="label">Resolves a card's internal name to what the mod calls it.</param>
    /// <param name="civLabel">The same for a civilization.</param>
    /// <param name="expanded">
    /// Civilizations whose tail the player has opened. Those groups show every row and carry no
    /// tail; everything else folds.
    /// </param>
    public static IReadOnlyList<DeckCivGroup> Group(
        IReadOnlyList<DeckCardEntry>? rows,
        Func<string, string> label,
        Func<string, string> civLabel,
        ISet<string>? expanded = null)
    {
        if (rows == null || rows.Count == 0) return Array.Empty<DeckCivGroup>();

        var byCiv = new Dictionary<string, List<DeckCardEntry>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Card)) continue;
            var civ = row.Civ ?? "";
            if (!byCiv.TryGetValue(civ, out var list)) byCiv[civ] = list = new List<DeckCardEntry>();
            list.Add(row);
        }

        var groups = new List<DeckCivGroup>();
        foreach (var (civ, entries) in byCiv)
        {
            // The denominator: see the remarks. Never zero — every entry has at least one
            // player behind it, or the server would not have sent the row.
            int decks = entries.Max(e => e.Players);
            bool sampled = decks >= MinDecksForPercent;

            var all = entries
                .Select(e => new DeckCardRow(
                    e.Card,
                    label(e.Card),
                    e.Players,
                    sampled ? Percent(e.Players, decks) : null))
                .OrderByDescending(r => r.Players)
                .ThenBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            bool open = expanded != null && expanded.Contains(civ);

            List<DeckCardRow> shown;
            List<DeckCardRow> tail;
            if (open)
            {
                shown = all;
                tail = new List<DeckCardRow>();
            }
            else
            {
                shown = all.Where(r => r.Players >= RowMinPlayers).Take(RowsShownPerCiv).ToList();
                // Everything the cut left behind, in the order it was already in, so the
                // summary's first names are the most-seen of what it folded. Keyed by card
                // rather than by row equality: two rows can carry the same numbers.
                var kept = new HashSet<string>(shown.Select(r => r.Card), StringComparer.Ordinal);
                tail = all.Where(r => !kept.Contains(r.Card)).ToList();
            }

            groups.Add(new DeckCivGroup(civ, civLabel(civ), all.Count, decks, shown, tail));
        }

        return groups
            .OrderByDescending(g => g.DistinctCards)
            .ThenBy(g => g.CivLabel, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The share of a civilization's decks, rounded to whole points.
    ///
    /// <para>Rounded away from zero so a card in one deck out of two hundred reads as 1 %, not
    /// as 0 % — a card that IS in somebody's deck must never be reported as being in nobody's.</para>
    /// </summary>
    public static int Percent(int players, int decks)
    {
        if (decks <= 0 || players <= 0) return 0;
        return (int)Math.Max(1, Math.Round(players * 100.0 / decks, MidpointRounding.AwayFromZero));
    }
}
