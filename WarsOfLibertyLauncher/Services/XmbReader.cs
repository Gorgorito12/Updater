using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace WarsOfLibertyLauncher.Services;

/// <summary>One element of a decoded XMB document.</summary>
/// <param name="Name">The element name, ALREADY LOWER-CASE — see <see cref="XmbReader"/>.</param>
/// <param name="Text">Its own text content. Empty, never null, when it carries none.</param>
/// <param name="Attributes">Attribute name (lower-case) to value.</param>
/// <param name="Children">Direct children, in document order.</param>
public sealed record XmbNode(
    string Name,
    string Text,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<XmbNode> Children)
{
    /// <summary>The direct children with this name, compared case-insensitively.</summary>
    public IEnumerable<XmbNode> Elements(string name)
    {
        foreach (var child in Children)
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                yield return child;
    }

    /// <summary>The text of the FIRST direct child with this name, or null when there is
    /// none. Direct children only — the same rule the plain-XML reader follows, which is what
    /// keeps a nested <c>portraittexture</c> out of a flag lookup.</summary>
    public string? Value(string name)
    {
        foreach (var child in Elements(name))
            return string.IsNullOrWhiteSpace(child.Text) ? null : child.Text;
        return null;
    }
}

/// <summary>
/// Reads XMB — the binary XML the game compiles its data files into.
///
/// <para><b>Why this exists.</b> Wars of Liberty ships a loose <c>data\civs.xml</c> and is
/// readable with an ordinary XML reader. Improvement Mod and Napoleonic Era do not: theirs
/// lives only as <c>Data\civs.xml.xmb</c> inside a <c>.bar</c> archive, so until this existed
/// those two mods had no civilization names and no flags at all, and that was recorded as
/// their ordinary state rather than a fault.</para>
///
/// <para><b>The names come out LOWER-CASE.</b> The compiler folds them: <c>String</c> becomes
/// <c>string</c>, <c>_locID</c> becomes <c>_locid</c>. Nothing here restores the original
/// casing because the original is not in the file, so every lookup compares
/// case-insensitively. This is the detail that does not fail to build and does not throw — it
/// just finds nothing.</para>
///
/// <para><b>No decompressor, for the files this reads.</b> Inside the mods' archives the
/// payload is a raw <c>X1</c> document: measured across both archives, 12,317 entries, and the
/// compressed size differed from the real size in none of them. Loose <c>.XMB</c> files on
/// disk ARE wrapped — <c>l33t</c> plus a zlib stream — and this refuses those by name rather
/// than returning something that looks like a document and is not.</para>
///
/// <para>Verified against a file whose answer was already known: Improvement Mod ships both
/// <c>randomnames.xml</c> and its compiled twin, and decoding the twin with these rules
/// reproduces the plain file's element names, nesting, mixed text and non-ASCII exactly.</para>
/// </summary>
public static class XmbReader
{
    /// <summary>Document magic, "X1".</summary>
    private const ushort FileMagic = 0x3158;

    /// <summary>The header that follows the length, "XR".</summary>
    private const ushort HeaderMagic = 0x5258;

    /// <summary>Node magic, "XN".</summary>
    private const ushort NodeMagic = 0x4E58;

    /// <summary>The wrapper a LOOSE <c>.XMB</c> carries: 'l33t', a length, then zlib.</summary>
    private const uint CompressedMagic = 0x7433336C;

    /// <summary>
    /// A ceiling on the tables and on child counts, so a corrupt length cannot make this
    /// allocate its way through memory before failing.
    /// </summary>
    private const int MaxCount = 1 << 20;

    /// <summary>
    /// The document's root element, or null when these bytes are not an XMB this can read.
    ///
    /// <para>Null rather than an exception for every rejection: a mod shipping something
    /// unexpected is an ordinary state on this path, and the caller already draws "no
    /// civilizations" without complaint.</para>
    /// </summary>
    public static XmbNode? Parse(ReadOnlySpan<byte> file)
    {
        try
        {
            return ParseCore(file);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"XmbReader: refused a document — {ex.Message}");
            return null;
        }
    }

    private static XmbNode? ParseCore(ReadOnlySpan<byte> file)
    {
        if (file.Length < 8) return null;

        if (BinaryPrimitives.ReadUInt32LittleEndian(file) == CompressedMagic)
        {
            // Said out loud rather than guessed at. Nothing this reads is wrapped, and a
            // silent empty answer here would look exactly like a mod with no civilizations.
            DiagnosticLog.Write("XmbReader: this document is l33t/zlib-wrapped, which is not read.");
            return null;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(file) != FileMagic) return null;

        // The declared length covers everything after itself. Trust the smaller of the two so a
        // truncated read cannot walk off the end.
        var declared = BinaryPrimitives.ReadInt32LittleEndian(file[2..]);
        if (declared < 0) return null;
        var end = Math.Min(file.Length, 6 + declared);

        var at = 6;
        if (end - at < 12) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[at..]) != HeaderMagic) return null;
        at += 2;
        at += 4;   // version
        at += 4;   // flags

        var elements = ReadTable(file, end, ref at);
        var attributes = ReadTable(file, end, ref at);
        if (elements == null || attributes == null) return null;

        return ReadNode(file, end, ref at, elements, attributes, depth: 0);
    }

    /// <summary>A count, then that many length-prefixed UTF-16 strings.</summary>
    private static List<string>? ReadTable(ReadOnlySpan<byte> file, int end, ref int at)
    {
        if (end - at < 4) return null;
        var count = BinaryPrimitives.ReadInt32LittleEndian(file[at..]);
        at += 4;
        if (count < 0 || count > MaxCount) return null;

        var names = new List<string>(Math.Min(count, 1024));
        for (int i = 0; i < count; i++)
        {
            var text = ReadString(file, end, ref at);
            if (text == null) return null;
            names.Add(text);
        }
        return names;
    }

    /// <summary>A character count, then that many UTF-16LE code units.</summary>
    private static string? ReadString(ReadOnlySpan<byte> file, int end, ref int at)
    {
        if (end - at < 4) return null;
        var chars = BinaryPrimitives.ReadInt32LittleEndian(file[at..]);
        at += 4;
        if (chars < 0 || chars > MaxCount) return null;
        if (chars == 0) return "";

        var bytes = chars * 2;
        if (end - at < bytes) return null;
        var text = Encoding.Unicode.GetString(file.Slice(at, bytes));
        at += bytes;
        return text;
    }

    /// <summary>
    /// One node and, recursively, its children.
    ///
    /// <para>The node's own length covers all of its descendants, so this uses it as the
    /// authority on where the node ends rather than trusting the walk — a child count that
    /// disagrees with the bytes stops here instead of eating the following node.</para>
    /// </summary>
    private static XmbNode? ReadNode(
        ReadOnlySpan<byte> file, int end, ref int at,
        List<string> elements, List<string> attributes, int depth)
    {
        // civs.xml nests three deep; anything approaching this is a malformed file looping.
        if (depth > 64) return null;
        if (end - at < 6) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[at..]) != NodeMagic) return null;

        var start = at;
        at += 2;
        var length = BinaryPrimitives.ReadInt32LittleEndian(file[at..]);
        at += 4;
        if (length < 0) return null;

        var nodeEnd = Math.Min(end, start + 6 + length);

        var text = ReadString(file, nodeEnd, ref at);
        if (text == null) return null;

        if (nodeEnd - at < 12) return null;
        var nameIndex = BinaryPrimitives.ReadInt32LittleEndian(file[at..]);
        at += 4;
        at += 4;   // the source line, which nothing needs
        var attributeCount = BinaryPrimitives.ReadInt32LittleEndian(file[at..]);
        at += 4;
        if (nameIndex < 0 || nameIndex >= elements.Count) return null;
        if (attributeCount < 0 || attributeCount > MaxCount) return null;

        var values = attributeCount == 0
            ? EmptyAttributes
            : new Dictionary<string, string>(attributeCount, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < attributeCount; i++)
        {
            if (nodeEnd - at < 4) return null;
            var attributeIndex = BinaryPrimitives.ReadInt32LittleEndian(file[at..]);
            at += 4;
            var value = ReadString(file, nodeEnd, ref at);
            if (value == null) return null;
            if (attributeIndex < 0 || attributeIndex >= attributes.Count) return null;
            ((Dictionary<string, string>)values)[attributes[attributeIndex]] = value;
        }

        if (nodeEnd - at < 4) return null;
        var childCount = BinaryPrimitives.ReadInt32LittleEndian(file[at..]);
        at += 4;
        if (childCount < 0 || childCount > MaxCount) return null;

        var children = childCount == 0 ? EmptyChildren : new List<XmbNode>(childCount);
        for (int i = 0; i < childCount; i++)
        {
            var child = ReadNode(file, nodeEnd, ref at, elements, attributes, depth + 1);
            if (child == null) return null;
            ((List<XmbNode>)children).Add(child);
        }

        // The node's declared end wins. It is what lets a caller trust the tree even when a
        // count and the bytes disagree.
        at = nodeEnd;
        return new XmbNode(elements[nameIndex], text, values, children);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<XmbNode> EmptyChildren = Array.Empty<XmbNode>();
}
