using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using WarsOfLibertyLauncher.Models;

namespace WarsOfLibertyLauncher.Services.Multiplayer;

/// <summary>
/// Turning the community deck table's internal names into names a player recognises.
///
/// <para>The table arrives from the server as <c>HCXPRefrigeration</c> and <c>Mexicans</c> —
/// identifiers, because that is what the game stores and what a launcher uploads. The house rule
/// is that an internal name never reaches a player, and it was already honoured for
/// civilizations and for maps; this closes the last place it was not.</para>
///
/// <para><b>Nothing here is per-mod knowledge.</b> It takes a mod id, asks the registry for that
/// mod's profile, and reads the mod's OWN files through the resolvers that already exist. A mod
/// added to the catalogue tomorrow resolves through exactly this path with no code written for
/// it — which is the point, and is pinned by <c>DeckCardNamesTests</c>.</para>
///
/// <para><b>It must not run on the UI thread the first time.</b> Underneath,
/// <see cref="CardNameResolver"/> streams the mod's tech trees — twelve megabytes for Wars of
/// Liberty — and <see cref="CardArtService"/> indexes its art archives. Both cache per install
/// for the life of the process, so only the first call for a given mod is expensive; the wrapper
/// below caches the resolved answer per mod as well, because the set of cards asked for is the
/// same on every repaint.</para>
///
/// <para><b>A mod that is not installed resolves to nothing, and that is a state and not a
/// failure.</b> Every resolver already answers an empty dictionary for an empty install path, so
/// the caller gets a result whose <see cref="Resolved"/> is false and shows the identifier with a
/// line saying why. Refusing to draw the table would hide a whole feature to avoid admitting a
/// limit.</para>
/// </summary>
internal static class DeckCardNames
{
    /// <summary>What one mod's card vocabulary came to.</summary>
    internal sealed record Vocabulary(
        IReadOnlyDictionary<string, CardDetail> Cards,
        IReadOnlyDictionary<string, ImageSource> Icons,
        IReadOnlyDictionary<string, string> Civs)
    {
        /// <summary>An empty answer: the mod is not installed, or its files gave nothing.</summary>
        internal static readonly Vocabulary None = new(
            new Dictionary<string, CardDetail>(StringComparer.Ordinal),
            new Dictionary<string, ImageSource>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

        /// <summary>Whether anything was resolved at all. The UI says the identifier and the
        /// reason when this is false, rather than pretending or hiding.</summary>
        internal bool Resolved => Cards.Count > 0;

        /// <summary>The card's name, or the identifier it came as.</summary>
        internal string NameOf(string? internalName)
        {
            if (string.IsNullOrWhiteSpace(internalName)) return "";
            return Cards.TryGetValue(internalName!, out var d) && !string.IsNullOrWhiteSpace(d.Name)
                ? d.Name!
                : internalName!;
        }

        /// <summary>The civilization's name, or the identifier it came as.</summary>
        internal string CivOf(string? internalName)
        {
            if (string.IsNullOrWhiteSpace(internalName)) return "";
            return Civs.TryGetValue(internalName!, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : internalName!;
        }

        /// <summary>
        /// The name as the mod's table wrote it, colour span included, for the one caller that
        /// can paint it through <see cref="GameText.Fill"/>. Falls back to
        /// <see cref="NameOf"/> - and therefore to the identifier - so a caller using this
        /// never gets less than one using the plain name.
        /// </summary>
        internal string NameMarkupOf(string? internalName)
        {
            if (string.IsNullOrWhiteSpace(internalName)) return "";
            return Cards.TryGetValue(internalName!, out var d)
                && !string.IsNullOrWhiteSpace(d.NameMarkup)
                    ? d.NameMarkup!
                    : NameOf(internalName);
        }

        /// <summary>
        /// What the card says it does, already cleaned. Null when the mod ships none - and a
        /// card with no description is drawn as ABSENCE, never as a placeholder.
        /// </summary>
        internal string? DescriptionOf(string? internalName)
        {
            if (string.IsNullOrWhiteSpace(internalName)) return null;
            return Cards.TryGetValue(internalName!, out var d)
                && !string.IsNullOrWhiteSpace(d.Description)
                    ? d.Description
                    : null;
        }

        internal ImageSource? IconOf(string? internalName)
        {
            if (string.IsNullOrWhiteSpace(internalName)) return null;
            if (!Cards.TryGetValue(internalName!, out var d) || d.IconPath == null) return null;
            return Icons.TryGetValue(d.IconPath, out var icon) ? icon : null;
        }
    }

    /// <summary>One resolved vocabulary per mod. Process-lifetime, like the resolvers it wraps:
    /// the files it reads only change when the mod is reinstalled, which restarts nothing but
    /// also happens far less often than this table is drawn.</summary>
    private static readonly ConcurrentDictionary<string, Vocabulary> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mods currently being resolved, so a repaint mid-flight does not start a second
    /// twelve-megabyte scan of the same files.</summary>
    private static readonly ConcurrentDictionary<string, byte> InFlight =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What is already known for a mod, without doing any work. Null when it has not
    /// been resolved yet — the caller draws identifiers and asks for the real answer.</summary>
    internal static Vocabulary? Peek(string? modId)
        => string.IsNullOrWhiteSpace(modId) ? null
         : Cache.TryGetValue(modId!, out var v) ? v
         : null;

    /// <summary>
    /// Resolve one mod's cards and civilizations, off the UI thread.
    /// </summary>
    /// <param name="modId">Which mod. Anything the registry knows; nothing is special-cased.</param>
    /// <param name="installPathOf">How to find that mod on disk. Injected rather than reached
    /// for, because the launcher's install lookup lives on the tab and this stays testable.</param>
    /// <param name="cards">The internal card names the table is about to draw.</param>
    /// <param name="civs">The internal civilization names beside them.</param>
    /// <returns>The vocabulary, which may be empty when the mod is not installed.</returns>
    internal static async Task<Vocabulary> ResolveAsync(
        string? modId,
        Func<ModProfile, string?> installPathOf,
        IEnumerable<string> cards,
        IEnumerable<string> civs)
    {
        if (string.IsNullOrWhiteSpace(modId)) return Vocabulary.None;
        if (Cache.TryGetValue(modId!, out var cached)) return cached;

        // A second caller while the first is still reading gets identifiers for now and the
        // real names on the repaint the first one triggers. Better than two scans.
        if (!InFlight.TryAdd(modId!, 0)) return Vocabulary.None;

        try
        {
            var profile = ModRegistry.Find(modId);
            if (profile == null) return Vocabulary.None;

            string? installPath = installPathOf(profile);
            if (string.IsNullOrWhiteSpace(installPath)) return Vocabulary.None;

            var wantedCards = cards
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var wantedCivs = civs
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var resolved = await Task.Run(() =>
            {
                var details = CardNameResolver.ResolveDetails(
                    installPath, profile.GameExecutable, wantedCards);

                var icons = CardArtService.Load(
                    installPath, details.Values.Select(d => d.IconPath));

                var civNames = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var civ in wantedCivs)
                {
                    var name = CivNameResolver.ResolveByInternalName(installPath, civ);
                    if (!string.IsNullOrWhiteSpace(name)) civNames[civ] = name!;
                }

                return new Vocabulary(details, icons, civNames);
            }).ConfigureAwait(true);

            // An empty answer is NOT cached: the mod may simply not be installed yet, and
            // caching that would keep the table showing identifiers for the rest of the
            // session after the player installs it.
            if (resolved.Resolved) Cache[modId!] = resolved;
            return resolved;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"DeckCardNames: could not resolve '{modId}' — {ex.Message}");
            return Vocabulary.None;
        }
        finally
        {
            InFlight.TryRemove(modId!, out _);
        }
    }
}
