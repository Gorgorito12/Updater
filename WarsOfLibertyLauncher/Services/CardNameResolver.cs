using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

namespace WarsOfLibertyLauncher.Services;

/// <summary>What the mod says about one home city card, once its ids have been resolved.</summary>
/// <param name="Name">The card's title. Null when the mod does not name it; the caller then
/// shows the internal name, which at least identifies it.</param>
/// <param name="Description">The rollover text, markup already stripped by
/// <see cref="GameText"/>. Null for a card that carries no <c>RolloverTextID</c> at all — which
/// is not a data gap: measured on a real deck, every one of the 12 such cards is a unit shipment
/// whose title already IS the description ("8 Tigermen"), so showing nothing extra loses
/// nothing.</param>
/// <param name="IconPath">The raw <c>&lt;Icon&gt;</c> value: no extension, backslashes, and
/// sometimes already prefixed with <c>art\</c>. <see cref="CardArtService"/> owns the
/// resolution.</param>
/// <param name="Effects">
/// What the card actually changes, as the tech tree writes it. These are what
/// <see cref="CardEffectText"/> turns into the sentences with percentages the game shows —
/// and they are the ONLY description the 20-odd crate and unit-shipment cards in a real deck
/// have, since those carry no <c>RolloverTextID</c> at all.
/// </param>
/// <param name="NameMarkup">
/// The name as the table WROTE it, colour span and all.
///
/// <para>Opt-in, and last, because <see cref="Name"/> is the safe default and every existing
/// caller keeps getting it. Only a surface that can paint runs
/// (<see cref="GameText.Fill"/>) has any business with this; anything that assigns a string
/// takes <see cref="Name"/>, which has already been through <see cref="GameText.Clean"/>.
/// Null when there is nothing extra in it.</para>
/// </param>
public sealed record CardDetail(
    string? Name,
    string? Description,
    string? IconPath,
    IReadOnlyList<CardEffect>? Effects = null,
    string? NameMarkup = null)
{
    /// <summary>Never null: a card with no effects has none, not an unknown number of them.</summary>
    public IReadOnlyList<CardEffect> EffectsOrEmpty => Effects ?? Array.Empty<CardEffect>();
}

/// <summary>
/// Turns a home city card's internal name — <c>HCShipWoodCrates3</c>,
/// <c>YPHCExpandedTradingPost</c> — into what the mod calls it on screen, what it says it does,
/// and which icon it wears.
///
/// <para>The sibling of <see cref="ProtoNameResolver"/>, one file along: a card is a tech with
/// <c>&lt;Flag&gt;HomeCity&lt;/Flag&gt;</c> in <c>data\techtree*.xml</c>, carrying a
/// <c>&lt;DisplayNameID&gt;</c> that <see cref="ModStringTable"/> turns into text. Measured on Wars
/// of Liberty: <b>4,390 of its 4,517 cards resolve, 97.2%</b>.</para>
///
/// <para><b>An unresolved card falls back to its internal name</b>, the same choice
/// <see cref="ProtoNameResolver"/> makes and for the same reason: the internal name is already a
/// word that identifies the card to anyone who mods, and it claims nothing false.</para>
/// </summary>
public static class CardNameResolver
{
    /// <summary>The raw fields of one tech, before any string table is consulted.</summary>
    internal sealed record CardTech(
        int DisplayNameId,
        int RolloverTextId,
        string? IconPath,
        IReadOnlyList<CardEffect> Effects);

    /// <summary>The layers, base first so later ones win — the engine's own order.</summary>
    private static readonly string[] BaseFiles = { "techtree.xml", "techtreex.xml", "techtreey.xml" };

    /// <summary>A guard against a hostile file — Wars of Liberty ships ~4,500 cards.</summary>
    private const int MaxCards = 65536;

    /// <summary>Card name to its tech fields, one map per install. Built by a 12 MB scan.</summary>
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, CardTech>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static void ResetCache() => Cache.Clear();

    /// <summary>
    /// Display names for the cards asked for. A card the mod does not name is simply absent, and
    /// the caller shows the internal name instead.
    ///
    /// <para>A thin view over <see cref="ResolveDetails"/> so the two can never disagree about
    /// which cards resolve.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Resolve(
        string? installPath, string? gameExecutable, IEnumerable<string> cardNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, detail) in ResolveDetails(installPath, gameExecutable, cardNames))
        {
            if (!string.IsNullOrWhiteSpace(detail.Name)) result[name] = detail.Name!;
        }
        return result;
    }

    /// <summary>
    /// Name, description and icon for the cards asked for, in one pass.
    ///
    /// <para><b>Do not call this on the UI thread the first time for a given install</b> — it
    /// streams every <c>techtree*.xml</c> the mod ships, which is 12 MB for Wars of Liberty.</para>
    ///
    /// <para>A card present in the tech files gets an entry even when its title does not resolve,
    /// because its icon and description may still be there and are worth showing.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, CardDetail> ResolveDetails(
        string? installPath, string? gameExecutable, IEnumerable<string> cardNames)
    {
        var result = new Dictionary<string, CardDetail>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(installPath)) return result;

        var techs = TechsFor(installPath!, gameExecutable);
        if (techs.Count == 0) return result;

        var wanted = new HashSet<int>();
        var byName = new Dictionary<string, CardTech>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in cardNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!techs.TryGetValue(name, out var tech)) continue;

            byName[name] = tech;
            if (tech.DisplayNameId > 0) wanted.Add(tech.DisplayNameId);
            if (tech.RolloverTextId > 0) wanted.Add(tech.RolloverTextId);
        }

        if (byName.Count == 0) return result;

        var strings = wanted.Count > 0
            ? ModStringTable.Resolve(installPath!, wanted)
            : new Dictionary<int, string>();

        foreach (var (name, tech) in byName)
        {
            strings.TryGetValue(tech.DisplayNameId, out var title);
            strings.TryGetValue(tech.RolloverTextId, out var rollover);

            var description = GameText.Clean(rollover);

            // The NAME goes through the cleaner too. It did not, and the community-cards
            // table printed rows reading "10 Dragoons <color=1.0, 1.0, 0.0>+ 3 Hussars</color>"
            // -- the same defect the descriptions had already been fixed for, one field over.
            // Cleaned here rather than at each caller so a surface added tomorrow is safe by
            // default; the raw form is kept alongside for the one table that paints it.
            var cleanName = GameText.Clean(title);
            var rawName = (title ?? "").Trim();

            result[name] = new CardDetail(
                cleanName.Length == 0 ? null : cleanName,
                description.Length == 0 ? null : description,
                tech.IconPath,
                tech.Effects,
                string.Equals(rawName, cleanName, StringComparison.Ordinal) ? null : rawName);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, CardTech> TechsFor(
        string installPath, string? gameExecutable)
    {
        string key;
        try { key = Path.GetFullPath(installPath); }
        catch { return new Dictionary<string, CardTech>(); }

        return Cache.GetOrAdd(key, k => BuildTechs(k, gameExecutable));
    }

    private static IReadOnlyDictionary<string, CardTech> BuildTechs(
        string installPath, string? gameExecutable)
    {
        var techs = new Dictionary<string, CardTech>(StringComparer.OrdinalIgnoreCase);
        var dataDir = Path.Combine(installPath, "data");

        foreach (var file in TechFilesFor(gameExecutable))
        {
            var path = Path.Combine(dataDir, file);
            if (!File.Exists(path)) continue;

            try { ReadTechs(path, techs); }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"CardNameResolver: could not read '{path}' — {ex.Message}");
            }
        }

        DiagnosticLog.Write(
            $"CardNameResolver: '{Path.GetFileName(installPath)}' — {techs.Count} techs indexed.");
        return techs;
    }

    /// <summary>
    /// The tech files to read, base first so a later layer overrides an earlier one. Same
    /// executable-suffix convention as <see cref="ProtoNameResolver.ProtoFilesFor"/> — Napoleonic
    /// Era runs <c>age3n.exe</c> and ships the <c>n</c> layer. Pure and internal so the derivation
    /// is tested rather than trusted.
    /// </summary>
    internal static IReadOnlyList<string> TechFilesFor(string? gameExecutable)
    {
        var files = new List<string>(BaseFiles);

        var match = Regex.Match(gameExecutable ?? "", @"^age3([a-z]?)\.exe$", RegexOptions.IgnoreCase);
        if (!match.Success) return files;

        var suffix = match.Groups[1].Value.ToLowerInvariant();
        if (suffix.Length == 0) return files;

        var own = "techtree" + suffix + ".xml";
        if (!files.Contains(own, StringComparer.OrdinalIgnoreCase)) files.Add(own);
        return files;
    }

    /// <summary>
    /// Streams one tech file, collecting <c>name</c> to its three fields.
    ///
    /// <para><b>Every tech is indexed, not only the HomeCity-flagged ones</b>, and that is
    /// deliberate: the flag arrives AFTER the fields we want inside the element, so filtering on
    /// it would mean either buffering each tech or reading the file twice, to save a dictionary
    /// that costs a few hundred kilobytes. The caller only ever asks about cards.</para>
    /// </summary>
    private static void ReadTechs(string path, Dictionary<string, CardTech> techs)
    {
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, ModStringTable.Settings());

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (!string.Equals(reader.Name, "Tech", StringComparison.OrdinalIgnoreCase)) continue;
            if (techs.Count >= MaxCards) return;

            var name = reader.GetAttribute("name");
            if (string.IsNullOrWhiteSpace(name) || reader.IsEmptyElement) continue;

            var tech = ReadTech(reader);
            if (tech != null) techs[name!.Trim()] = tech;
        }
    }

    /// <summary>
    /// The three fields of the Tech element the reader is on, consuming exactly that element.
    ///
    /// <para><b>Advances by hand, and must.</b> <c>ReadElementContentAsString</c> already moves
    /// past the element it read, so a plain <c>while (reader.Read())</c> loop steps over whatever
    /// follows — the same trap <see cref="ModStringTable"/> documents, and it bites harder here
    /// than it did when only one field was wanted: the skipped node would be the NEXT field.
    /// A card's real order is <c>DisplayNameID … Icon, RolloverTextID</c>, so reading one would
    /// silently cost the others the moment they sit next to each other.</para>
    /// </summary>
    private static CardTech? ReadTech(XmlReader reader)
    {
        var depth = reader.Depth;
        int display = 0, rollover = 0;
        string? icon = null;
        IReadOnlyList<CardEffect> effects = Array.Empty<CardEffect>();

        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;

            if (reader.NodeType == XmlNodeType.Element && !reader.IsEmptyElement)
            {
                if (display == 0 && Is(reader, "DisplayNameID"))
                {
                    if (int.TryParse(reader.ReadElementContentAsString().Trim(), out var v)) display = v;
                    continue;   // already advanced
                }
                if (rollover == 0 && Is(reader, "RolloverTextID"))
                {
                    if (int.TryParse(reader.ReadElementContentAsString().Trim(), out var v)) rollover = v;
                    continue;
                }
                if (icon == null && Is(reader, "Icon"))
                {
                    var text = reader.ReadElementContentAsString().Trim();
                    if (text.Length > 0) icon = text;
                    continue;
                }
                if (effects.Count == 0 && Is(reader, "Effects"))
                {
                    effects = ReadEffects(reader);
                    continue;   // consumed the whole subtree, including its end tag
                }
            }

            reader.Read();
        }

        return display == 0 && rollover == 0 && icon == null && effects.Count == 0
            ? null
            : new CardTech(display, rollover, icon, effects);
    }

    /// <summary>
    /// The <c>&lt;Effects&gt;</c> subtree the reader is on, consumed whole.
    ///
    /// <para><b>Only <c>Data</c> effects are kept.</b> The other kinds say nothing about what a
    /// card does — <c>TextOutput</c> is the chat line printed when the shipment lands ("Trade
    /// Empire Shipment has arrived."), measured on every one of them — and dropping them takes
    /// a fifth off what this cache holds for a 12 MB tech tree.</para>
    /// </summary>
    private static IReadOnlyList<CardEffect> ReadEffects(XmlReader reader)
    {
        var effects = new List<CardEffect>();
        var depth = reader.Depth;

        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                reader.Read();
                break;
            }

            if (reader.NodeType == XmlNodeType.Element && Is(reader, "Effect"))
            {
                var effect = ReadEffect(reader);   // consumes the element
                if (effect != null
                    && string.Equals(effect.Type, CardEffectText.DataEffect,
                        StringComparison.OrdinalIgnoreCase))
                {
                    effects.Add(effect);
                }
                continue;
            }

            reader.Read();
        }

        return effects.Count == 0 ? Array.Empty<CardEffect>() : effects;
    }

    /// <summary>
    /// One <c>&lt;Effect&gt;</c>, consumed whole, with its <c>&lt;Target&gt;</c> if it has one.
    /// A <c>TextOutput</c> effect carries a string id as TEXT rather than a target, which the
    /// walk simply steps over.
    /// </summary>
    private static CardEffect? ReadEffect(XmlReader reader)
    {
        var type = reader.GetAttribute("type") ?? "";
        var subtype = reader.GetAttribute("subtype") ?? "";
        var relativity = reader.GetAttribute("relativity") ?? "";
        var resource = reader.GetAttribute("resource") ?? "";
        var unitType = reader.GetAttribute("unittype") ?? "";
        var action = reader.GetAttribute("action") ?? "";

        double.TryParse(reader.GetAttribute("amount"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var amount);

        var allActions = !string.IsNullOrWhiteSpace(reader.GetAttribute("allactions"));

        var targetType = "";
        var targetName = "";

        if (reader.IsEmptyElement)
        {
            reader.Read();
        }
        else
        {
            var depth = reader.Depth;
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                {
                    reader.Read();
                    break;
                }

                if (reader.NodeType == XmlNodeType.Element && Is(reader, "Target"))
                {
                    targetType = reader.GetAttribute("type") ?? "";
                    if (reader.IsEmptyElement) reader.Read();
                    else targetName = reader.ReadElementContentAsString().Trim();
                    continue;   // already advanced either way
                }

                reader.Read();
            }
        }

        return new CardEffect(
            type, subtype, relativity, amount, resource, unitType, action,
            targetType, targetName, allActions);
    }

    private static bool Is(XmlReader reader, string name) =>
        string.Equals(reader.Name, name, StringComparison.OrdinalIgnoreCase);
}
