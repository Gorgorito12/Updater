using System;
using System.Collections.Generic;
using System.Linq;
using WarsOfLibertyLauncher.Localization;

namespace WarsOfLibertyLauncher.Services.Multiplayer;

/// <summary>
/// The room title the create dialog offers, and the question of whether the host has since
/// written one of their own.
///
/// <para><b>Why the title says it at all, when the browser row already carries a badge.</b>
/// The title is the first thing read about a room and the only part of it the host writes.
/// The competitive badge is a small gold chip on the row's SECOND line, and it is derived
/// from the server's boolean — that is deliberate and stays that way, because anyone can type
/// "competitive" into a room name and a badge a stranger can forge is worth less than no badge
/// at all. So the two are different claims: the badge is what the room IS, the title is what
/// its host is announcing. They will read the same word twice on one row, and that was
/// weighed and accepted.</para>
///
/// <para><b>Why the title is not localized.</b> A room title is a NETWORK VALUE, not UI copy.
/// It is typed into <c>CreateLobbyRequest.Title</c>, persisted on the lobby server, and
/// rendered verbatim to everyone who opens the browser — the badge beside it is localized per
/// viewer, the title never can be. Composing it from <c>Strings</c> meant a Spanish host
/// published <c>Sala de WoL · COMPETITIVA 2v2</c> and an English player read exactly that.
/// So the two markers below are constants and deliberately do NOT live in
/// <c>Strings.cs</c>: putting them there is the defect, not the fix. Everyone sees
/// <c>WoL · Ranked 2v2</c>.</para>
///
/// <para><b>Why this is a separate class instead of two lines in the dialog.</b>
/// <see cref="IsOurs"/> is the half that is easy to get wrong and impossible to see going
/// wrong: the dialog only replaces a title it recognises as its own, so the instant the
/// proposal gains a variant this method does not enumerate, the box freezes — ticking the
/// competitive box once would make every later change look like a hand-typed title and be
/// left alone. That is a silent failure, so it is pinned by tests rather than by reading.
/// It is also why <see cref="LegacyProposals"/> exists: titles this class wrote BEFORE the
/// wording changed are still sitting in people's boxes, and they have to stay recognisable.</para>
/// </summary>
public static class RoomTitleProposal
{
    /// <summary>
    /// The separator between the room's name and what is being announced about it. The same
    /// one the suggestion pills under this field already use to append themselves.
    /// </summary>
    private const string Join = " · ";

    /// <summary>
    /// The competitive marker. A constant, in English, for everyone — see the class remarks:
    /// this string is persisted server-side and read by players in every language, so the one
    /// thing it must not do is change with the host's launcher language. "Ranked" over
    /// "Competitive" because it is the shorter and more widely recognised word for the same
    /// thing across the games these players come from.
    /// </summary>
    private const string Ranked = "Ranked";

    /// <summary>The casual marker. Same rule as <see cref="Ranked"/>.</summary>
    private const string Casual = "Casual";

    /// <summary>The languages a title could have been written in by an earlier build. Used
    /// only by <see cref="LegacyProposals"/>.</summary>
    private static readonly string[] Languages = { Strings.LangEn, Strings.LangEs };

    /// <summary>
    /// What the dialog would put in an untouched title box.
    /// </summary>
    /// <param name="modName">The picked mod's display name.</param>
    /// <param name="competitive">Whether the room is declared competitive.</param>
    /// <param name="format">The declared format. <see cref="RoomFormat.Unknown"/> is a real
    /// case — a competitive room whose size names no format — and it is answered by saying
    /// only the marker rather than by inventing a 1v1.</param>
    /// <param name="maxLength">The title field's own cap.</param>
    public static string Propose(string modName, bool competitive, RoomFormat format, int maxLength)
    {
        var marker = competitive ? Ranked + FormatSuffix(format) : Casual;
        return Compose(modName ?? "", marker, maxLength);
    }

    /// <summary>
    /// The format, as a space-separated suffix, or empty when the format names none.
    ///
    /// <para>Read from the EN table explicitly rather than through <see cref="Strings.Get"/>.
    /// The three format labels are identical in both languages today, so this changes
    /// nothing now — it is here so that localizing one of them later cannot quietly put a
    /// translated word back into a value that goes on the wire.</para>
    /// </summary>
    private static string FormatSuffix(RoomFormat format)
    {
        var key = RoomFormats.LabelKey(format);
        return key == null ? "" : " " + Strings.GetIn(Strings.LangEn, key);
    }

    /// <summary>
    /// Joins the room's name to its marker within the cap.
    ///
    /// <para>THE NAME GIVES WAY, NEVER THE MARKER. The marker is the thing that was added on
    /// purpose and the reason the host ticked anything; a title cut off mid-"Ranke…"
    /// announces nothing and looks like a bug. So the room's own name is what is trimmed,
    /// which is also the only part with anything to spare. A blank name drops the separator
    /// too rather than opening the title with one.</para>
    /// </summary>
    private static string Compose(string name, string marker, int maxLength)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return Clamp(marker, maxLength);

        var suffix = Join + marker;
        if (name.Length + suffix.Length > maxLength)
        {
            var room = Math.Max(0, maxLength - suffix.Length);
            name = name[..Math.Min(name.Length, room)].TrimEnd();
            if (name.Length == 0) return Clamp(marker, maxLength);
        }

        return name + suffix;
    }

    /// <summary>
    /// Whether <paramref name="current"/> is a title this class wrote, rather than one a
    /// person typed.
    ///
    /// <para>Every variant, for every mod: casual, and competitive at each of the formats
    /// including the one that names none. Miss one and the dialog stops updating the title
    /// the moment it produces that one — see the class remarks.</para>
    ///
    /// <para>Plus every variant EARLIER BUILDS wrote, in both languages. A host who created a
    /// room before the wording changed still has that title in the box; not recognising it
    /// would freeze the field for them permanently, and they would have no way to know why.</para>
    /// </summary>
    public static bool IsOurs(string? current, IEnumerable<string> modNames, int maxLength)
    {
        var text = (current ?? "").Trim();
        if (text.Length == 0) return true;   // an empty box is nobody's title

        var mods = (modNames ?? Enumerable.Empty<string>()).ToList();

        return AllProposals(mods, maxLength)
            .Concat(LegacyProposals(mods, maxLength))
            .Any(p => string.Equals(p, text, StringComparison.Ordinal));
    }

    /// <summary>Every title this class can produce for these mods. Exposed so a test can
    /// assert the round trip rather than restate the list.</summary>
    public static IEnumerable<string> AllProposals(IEnumerable<string> modNames, int maxLength)
    {
        foreach (var mod in modNames ?? Enumerable.Empty<string>())
        {
            yield return Propose(mod, competitive: false, RoomFormat.Casual, maxLength);
            foreach (var f in CompetitiveFormats)
            {
                yield return Propose(mod, competitive: true, f, maxLength);
            }
        }
    }

    /// <summary>
    /// Every title this class produced BEFORE the wording became language-neutral: the
    /// localized room name (<c>Sala de {0}</c> / <c>{0} room</c>), optionally joined to the
    /// localized competitive badge and a format.
    ///
    /// <para>Enumerated in BOTH languages, not just the active one, because the host may have
    /// switched language since — the title on the server does not change when they do.</para>
    ///
    /// <para>Recognition only. Nothing here is ever offered; <see cref="AllProposals"/> stays
    /// the answer to "what would this class write", which is what the round-trip test asserts.</para>
    /// </summary>
    public static IEnumerable<string> LegacyProposals(IEnumerable<string> modNames, int maxLength)
    {
        foreach (var mod in modNames ?? Enumerable.Empty<string>())
        {
            foreach (var lang in Languages)
            {
                var name = Strings.FormatIn(lang, "MpCreateDialogDefaultTitle", mod ?? "");
                var badge = Strings.GetIn(lang, "MpRoomCompetitiveBadge");

                yield return Clamp(name, maxLength);

                foreach (var f in CompetitiveFormats)
                {
                    var key = RoomFormats.LabelKey(f);
                    var marker = key == null ? badge : badge + " " + Strings.GetIn(lang, key);
                    yield return Compose(name, marker, maxLength);
                }
            }
        }
    }

    /// <summary>The competitive states a room can declare, including the one that names no
    /// format. Shared by both enumerations so they cannot cover different sets.</summary>
    private static readonly RoomFormat[] CompetitiveFormats =
    {
        RoomFormat.OneVOne, RoomFormat.TwoVTwo,
        RoomFormat.ThreeVThree, RoomFormat.Unknown,
    };

    private static string Clamp(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength];
}
