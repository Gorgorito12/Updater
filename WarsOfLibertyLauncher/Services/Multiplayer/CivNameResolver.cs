using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace WarsOfLibertyLauncher.Services.Multiplayer;

/// <summary>
/// Turns the raw civilization INDEX a recording carries into the name the players actually saw,
/// using the mod's own data files. Returns <b>null</b> whenever it cannot be sure, which is a
/// normal outcome and means "report no civ" — exactly what every match did before this existed.
///
/// <para><b>The index is 1-BASED into the top-level <c>civ</c> list of the mod's
/// <c>data\civs.xml</c>.</b> This was recorded as "plausible but unconfirmed" for a long time
/// because the only evidence was that the numbers landed on real civs. It is confirmed now, and
/// not by reasoning about the index: every case below was cross-checked against an INDEPENDENT
/// field of the same recording — the home-city file name, the explorer's name, or the AI
/// personality — each of which names the civ on its own.</para>
///
/// <code>
///   mod  civ   0-based would give   1-based gives   independent proof in the same file
///   WoL    7   Indians              Chinese         sp_Beijing_homecity.xml, explorer Bai Yu Feng
///   WoL   34   Peruvians            Paraguayans     sp_Asuncion_homecity.xml, explorer Jose Bareiro
///   WoL   13   Danish               British         sp_Londres_homecity.xml
///   WoL    6   Chinese              Canadians       sp_Quebec_homecity.xml
///   WoL   31   Haitians             Colombians      sp_Bogota_homecity.xml, explorer Maluma Beiby
///   WoL    1   Egyptians            Ethiopians      AI wolMenelik (Menelik II ruled Ethiopia)
///   WoL    4   Australians          UnitedStates    AI named Abraham Lincoln
///   SoI    8   SPCAct1              Surakarta       sp_Solo_homecity.xml -- Solo IS Surakarta
///   SoI    1   British              Erucakran       AI Abdulhamid Erucakra
/// </code>
///
/// <para>Nine of nine across two mods, and the wrong reading fails recognisably: 0-based lands on
/// <c>SPCAct1</c>, a campaign placeholder, for the Struggle of Indonesia case. Index 0 is the
/// nature slot and never a civilization.</para>
///
/// <para><b>The string-table step is NOT optional, and removing it would print civs that do not
/// exist.</b> A mod that reskins a base civ keeps the original internal name: in Struggle of
/// Indonesia the block whose name is <c>Ottomans</c> resolves, through its
/// <c>displaynameid</c> 22868, to <b>"Surakarta"</b>, and the one called <c>Spanish</c> to
/// <b>"Erucakran"</b>. A player of that mod never saw the word "Ottomans". So an unresolvable
/// display id yields null rather than falling back to the internal name — a missing civ is
/// honest, a wrong one is not.</para>
///
/// <para><b>It reads the canonical-English snapshot when there is one</b>
/// (the <c>_originals</c> folder, via the same rule <see cref="ModHashService"/> and version
/// detection already use). The resolved name is stored on the server and shown to everybody, so
/// it must not depend on which translation the reporting player happens to have applied.</para>
/// </summary>
public static class CivNameResolver
{
    /// <summary>
    /// A guard against a corrupt or hostile file, not a real limit — Wars of Liberty ships 188
    /// civilizations, the base game 14.
    /// </summary>
    private const int MaxCivs = 4096;

    /// <summary>Resolved tables, one per install. Building one reads several MB of XML.</summary>
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string?>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The display name for <paramref name="civIndex"/> as that mod calls it, or null when the
    /// mod ships no loose civ list, the index names nothing, or the display name cannot be
    /// resolved. Null is a normal answer.
    /// </summary>
    public static string? Resolve(string? installPath, int civIndex)
    {
        // Index 0 is the nature slot and negatives are AoE3's "unset". Neither is a civilization,
        // and treating either as one would put slot 0's name on somebody's match.
        if (civIndex <= 0) return null;
        if (string.IsNullOrWhiteSpace(installPath)) return null;

        var table = TableFor(installPath!);
        if (table == null || civIndex >= table.Count) return null;
        return table[civIndex];
    }

    /// <summary>
    /// Drops the cached tables. For tests, and after an install is repaired or updated.
    /// <b>Both</b> tables — the by-index one and the by-name one — or a repair would leave one of
    /// them describing the files as they were before it.
    /// </summary>
    public static void ResetCache()
    {
        Cache.Clear();
        ByNameCache.Clear();
    }

    private static IReadOnlyList<string?>? TableFor(string installPath)
    {
        string key;
        try { key = Path.GetFullPath(installPath); }
        catch { return null; }

        if (Cache.TryGetValue(key, out var cached)) return cached.Count == 0 ? null : cached;

        var built = BuildTable(key);
        // An install with no loose civs.xml caches an empty list so the miss costs one lookup
        // per session rather than a directory probe per participant per match.
        Cache[key] = built ?? Array.Empty<string?>();
        return built;
    }

    /// <summary>
    /// Reads the two file kinds and joins them. Internal so a test can drive it against a small
    /// synthetic install instead of a mod's real 481 KB civ list.
    /// </summary>
    internal static IReadOnlyList<string?>? BuildTable(string installPath)
    {
        var dataDir = Path.Combine(installPath, "data");
        var civsPath = Path.Combine(dataDir, "civs.xml");

        // Improvement Mod and Napoleonic Era keep theirs packed inside Data.bar, so this is the
        // ordinary state for them, not a fault. They stay civ-less until something can read that.
        if (!File.Exists(civsPath))
        {
            DiagnosticLog.Write($"CivNameResolver: no loose civs.xml under '{dataDir}' — civ stays unresolved.");
            return null;
        }

        List<int?> displayIds;
        try { displayIds = ReadCivDisplayIds(civsPath); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"CivNameResolver: could not read '{civsPath}' — {ex.Message}");
            return null;
        }

        if (displayIds.Count == 0)
        {
            DiagnosticLog.Write($"CivNameResolver: '{civsPath}' declared no civilizations.");
            return null;
        }

        var wanted = new HashSet<int>();
        foreach (var id in displayIds) if (id.HasValue) wanted.Add(id.Value);

        var strings = ModStringTable.Resolve(installPath, wanted);

        // One slot longer than the civ list and 1-based throughout, so callers index with the
        // recording's own number and never do the arithmetic themselves.
        var table = new string?[displayIds.Count + 1];
        var resolved = 0;
        for (var i = 0; i < displayIds.Count; i++)
        {
            var id = displayIds[i];
            if (id.HasValue && strings.TryGetValue(id.Value, out var name) && !string.IsNullOrWhiteSpace(name))
            {
                table[i + 1] = name.Trim();
                resolved++;
            }
        }

        DiagnosticLog.Write(
            $"CivNameResolver: '{Path.GetFileName(installPath)}' — {displayIds.Count} civilizations, "
            + $"{resolved} named.");
        return table;
    }

    /// <summary>
    /// Every top-level civ element's display-name id, in document order — which IS the index
    /// order. Depth matters: only the children of the root count, so a civ element nested inside
    /// something else can never shift the numbering.
    /// </summary>
    internal static List<int?> ReadCivDisplayIds(string civsPath)
    {
        var ids = new List<int?>();

        using var stream = File.OpenRead(civsPath);
        using var reader = XmlReader.Create(stream, ModStringTable.Settings());

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Depth != 1) continue;
            if (!string.Equals(reader.Name, "civ", StringComparison.OrdinalIgnoreCase)) continue;
            if (ids.Count >= MaxCivs) break;

            ids.Add(ReadDisplayNameId(reader));
        }

        return ids;
    }

    /// <summary>
    /// The display name for a civ named by its INTERNAL name — <c>Chinese</c>, <c>Ottomans</c> —
    /// rather than by the index a recording carries.
    ///
    /// <para><b>Needed because the internal name is frequently not the name anyone saw.</b> A mod
    /// that reskins a base civilization keeps the original: Struggle of Indonesia's Solo home city
    /// files itself under <c>Ottomans</c> and displays as <b>Surakarta</b>, and its
    /// <c>Spanish</c> as <b>Erucakran</b>. Printing the internal name beside a player's own deck
    /// would tell them they play a civilization they have never heard of.</para>
    ///
    /// <para>Null when the mod keeps its civ list packed inside <c>Data.bar</c> (Improvement Mod,
    /// Napoleonic Era) or does not describe that civ — the caller then shows the internal name,
    /// which at least identifies it. <b>Not for the UI thread on a first call</b>: it reads the
    /// mod's civ list and its string table.</para>
    /// </summary>
    public static string? ResolveByInternalName(string? installPath, string? internalName)
    {
        if (string.IsNullOrWhiteSpace(installPath) || string.IsNullOrWhiteSpace(internalName))
            return null;

        string key;
        try { key = Path.GetFullPath(installPath!); }
        catch { return null; }

        var map = ByNameCache.GetOrAdd(key, BuildByNameTable);
        return map.TryGetValue(internalName!.Trim(), out var display) ? display : null;
    }

    /// <summary>Internal civ name to display name, one map per install.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, IReadOnlyDictionary<string, string>> ByNameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads <c>civs.xml</c> a second way — by name rather than by position — and joins it to the
    /// string table. A separate pass on purpose: the index path above is what decides a stored
    /// match's civilization, and it is not worth reshaping for a panel.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildByNameTable(string installPath)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var civsPath = Path.Combine(installPath, "data", "civs.xml");
        if (!File.Exists(civsPath)) return byName;

        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = File.OpenRead(civsPath);
            using var reader = XmlReader.Create(stream, ModStringTable.Settings());

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.Depth != 1) continue;
                if (!string.Equals(reader.Name, "civ", StringComparison.OrdinalIgnoreCase)) continue;
                if (ids.Count >= MaxCivs) break;

                var pair = ReadNameAndDisplayId(reader);
                if (pair.Name != null && pair.Id.HasValue) ids[pair.Name] = pair.Id.Value;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"CivNameResolver: could not read '{civsPath}' by name — {ex.Message}");
            return byName;
        }

        if (ids.Count == 0) return byName;

        var strings = ModStringTable.Resolve(installPath, new HashSet<int>(ids.Values));
        foreach (var (name, id) in ids)
        {
            if (strings.TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text))
                byName[name] = text.Trim();
        }

        return byName;
    }

    /// <summary>
    /// The internal name and display-name id of the civ element the reader is on, consuming
    /// exactly that element.
    ///
    /// <para><b>This reads TWO fields, and that is why it cannot be written like
    /// <see cref="ReadDisplayNameId"/>.</b> <c>ReadElementContentAsString</c> already leaves the
    /// reader on the node AFTER the element it read, so a plain <c>while (reader.Read())</c> steps
    /// over whatever follows — here, the second field. Reading one field hides the bug; reading
    /// two makes it certain, and it cost this method a failing test before the shape was fixed.
    /// The <c>advanced</c> flag is what suppresses the loop's own <c>Read</c> for exactly one
    /// turn.</para>
    /// </summary>
    private static (string? Name, int? Id) ReadNameAndDisplayId(XmlReader reader)
    {
        if (reader.IsEmptyElement) return (null, null);

        var depth = reader.Depth;
        string? name = null;
        int? id = null;
        var advanced = false;

        while (advanced || reader.Read())
        {
            advanced = false;

            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType != XmlNodeType.Element || reader.IsEmptyElement) continue;

            var isName = name == null
                && string.Equals(reader.Name, "name", StringComparison.OrdinalIgnoreCase);
            var isId = id == null
                && string.Equals(reader.Name, "displaynameid", StringComparison.OrdinalIgnoreCase);
            if (!isName && !isId) continue;

            var text = reader.ReadElementContentAsString().Trim();
            if (isName) name = text.Length == 0 ? null : text;
            else if (int.TryParse(text, out var parsed)) id = parsed;

            advanced = true;
        }

        return (name, id);
    }

    /// <summary>
    /// The display-name id of the civ element the reader is sitting on, consuming exactly that
    /// element. Null when it declares none — some placeholder civs do not.
    /// </summary>
    private static int? ReadDisplayNameId(XmlReader reader)
    {
        if (reader.IsEmptyElement) return null;

        var depth = reader.Depth;
        int? id = null;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;

            if (id == null
                && reader.NodeType == XmlNodeType.Element
                && string.Equals(reader.Name, "displaynameid", StringComparison.OrdinalIgnoreCase)
                && !reader.IsEmptyElement
                && int.TryParse(reader.ReadElementContentAsString().Trim(), out var parsed))
            {
                id = parsed;
                // ReadElementContentAsString has already moved past the end tag, so the loop's
                // own Read would step over a node. Re-check the boundary here instead.
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            }
        }

        return id;
    }

    /// <summary>
    /// Each civilization's flag art path, by internal name. Empty when the mod keeps
    /// <c>civs.xml</c> packed inside <c>Data.bar</c> — Improvement Mod and Napoleonic Era both
    /// do, and they get no name from this file either, so no flag is the same ordinary state
    /// and not a new fault.
    ///
    /// <para><b>A third pass rather than a third field in
    /// <see cref="ReadNameAndDisplayId"/>.</b> That method carries an <c>advanced</c> flag
    /// because <c>ReadElementContentAsString</c> leaves the reader past the element it read, and
    /// its own remarks record that reading a second field is what made the bug certain. Adding a
    /// third is exactly where it would break again, silently, and it would put the flag on the
    /// path that decides a stored match's civilization. This one reads art and nothing else.</para>
    ///
    /// <para><b>Which element, and why not the others.</b> <c>&lt;portrait&gt;</c> first, then
    /// <c>&lt;homecityflagtexture&gt;</c>: between them they cover 185 of Wars of Liberty's 187
    /// civilizations and all 79 of Struggle of Indonesia's. NOT <c>&lt;bannertexture&gt;</c>,
    /// which names a shared atlas and is meaningless without the
    /// <c>&lt;bannertexturecoords&gt;</c> crop beside it, and not the
    /// <c>&lt;portraittexture&gt;</c> nested inside <c>&lt;matchmakingtextures&gt;</c> — a
    /// different picture, and at a different depth, which is why only direct children of the
    /// <c>&lt;civ&gt;</c> block are considered.</para>
    ///
    /// <para>Reading the MOD's own art is what makes a reskin come out right: Struggle of
    /// Indonesia's block named <c>Ottomans</c> ships its own flag, so Surakarta gets Surakarta's.</para>
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ResolvePortraits(string? installPath)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(installPath)) return byName;

        var civsPath = Path.Combine(installPath!, "data", "civs.xml");
        if (!File.Exists(civsPath)) return byName;

        try
        {
            using var stream = File.OpenRead(civsPath);
            using var reader = XmlReader.Create(stream, ModStringTable.Settings());

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.Depth != 1) continue;
                if (!string.Equals(reader.Name, "civ", StringComparison.OrdinalIgnoreCase)) continue;
                if (byName.Count >= MaxCivs) break;

                var pair = ReadNameAndPortrait(reader);
                if (pair.Name != null && pair.Art != null) byName[pair.Name] = pair.Art;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(
                $"CivNameResolver: could not read portraits from '{civsPath}' — {ex.Message}");
            return byName;
        }

        return byName;
    }

    /// <summary>
    /// The internal name and flag art path of the civ element the reader is on, consuming
    /// exactly that element.
    ///
    /// <para>Same <c>advanced</c> shape as <see cref="ReadNameAndDisplayId"/>, and for the same
    /// reason — see its remarks. The depth check is what keeps
    /// <c>&lt;matchmakingtextures&gt;/&lt;portraittexture&gt;</c> out: only direct children of
    /// the civ block count.</para>
    /// </summary>
    private static (string? Name, string? Art) ReadNameAndPortrait(XmlReader reader)
    {
        if (reader.IsEmptyElement) return (null, null);

        var depth = reader.Depth;
        string? name = null;
        string? portrait = null;
        string? flag = null;
        var advanced = false;

        while (advanced || reader.Read())
        {
            advanced = false;

            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType != XmlNodeType.Element || reader.IsEmptyElement) continue;
            if (reader.Depth != depth + 1) continue;

            var isName = name == null
                && string.Equals(reader.Name, "name", StringComparison.OrdinalIgnoreCase);
            var isPortrait = portrait == null
                && string.Equals(reader.Name, "portrait", StringComparison.OrdinalIgnoreCase);
            var isFlag = flag == null
                && string.Equals(reader.Name, "homecityflagtexture", StringComparison.OrdinalIgnoreCase);
            if (!isName && !isPortrait && !isFlag) continue;

            var text = reader.ReadElementContentAsString().Trim();
            if (text.Length > 0)
            {
                if (isName) name = text;
                else if (isPortrait) portrait = text;
                else flag = text;
            }

            advanced = true;
        }

        // THE FLAG FIRST, and the portrait only as a fallback. That looks backwards and is
        // not: in Wars of Liberty <portrait> was left pointing at the BASE GAME's art while
        // the mod put its own flag in <homecityflagtexture>. Germans reads
        // objects\flags\germans - the vanilla white flag with the eagle - against
        // "War of the Triple Alliance\Flags\prussia", the black-white-red one the mod
        // actually ships; French reads the Bourbon navy-and-gold against the tricolour.
        // Eleven of its civilizations diverge that way, and in every one of them the
        // home-city flag is the mod's own art and the portrait is a stale base path.
        // Where the two agree - all of Struggle of Indonesia, and most of WoL - the order
        // changes nothing.
        return (name, flag ?? portrait);
    }
}
