using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WarsOfLibertyLauncher.Tests;

/// <summary>
/// Writes XMB — the binary XML the game compiles its data into — so a test can state a document
/// and get bytes.
///
/// <para>Kept deliberately literal: it emits the same fields in the same order the reader
/// expects, which is what makes a disagreement between the two show up as a failing test. The
/// real documents are 300 KB inside a 551 MB archive, and what is worth pinning is the format,
/// not a copy of somebody's mod.</para>
///
/// <para>Shared rather than private to the reader's tests because the civilization list needs it
/// too: two of the catalogued mods ship <c>civs.xml</c> only as <c>Data\civs.xml.xmb</c> inside a
/// <c>.bar</c>, so testing that path means writing both containers.</para>
/// </summary>
internal static class TestXmb
{
    internal sealed record Node(
        string Name,
        string Text = "",
        Dictionary<string, string>? Attributes = null,
        List<Node>? Children = null)
    {
        public Dictionary<string, string> Attributes { get; } = Attributes ?? new();
        public List<Node> Children { get; } = Children ?? new();
    }

    /// <summary>A <c>&lt;civ&gt;</c> block as the real file writes it: a name, the id its display
    /// name is looked up by, and the mod's own flag.</summary>
    internal static Node Civ(string name, string displayNameId, string? flag = null,
                             string? portrait = null)
    {
        var children = new List<Node> { new("name", name), new("displaynameid", displayNameId) };
        if (portrait != null) children.Add(new Node("portrait", portrait));
        if (flag != null) children.Add(new Node("homecityflagtexture", flag));
        return new Node("civ", Children: children);
    }

    internal static byte[] Build(Node root) => new Writer().Build(root);

    private sealed class Writer
    {
        private readonly List<string> _elements = new();
        private readonly List<string> _attributes = new();

        public byte[] Build(Node root)
        {
            Index(root);

            var body = new MemoryStream();
            var w = new BinaryWriter(body, Encoding.Unicode, leaveOpen: true);
            w.Write((ushort)0x5258);                 // "XR"
            w.Write(4);                              // version
            w.Write(8);                              // flags
            WriteTable(w, _elements);
            WriteTable(w, _attributes);
            WriteNode(w, root);
            w.Flush();

            var file = new MemoryStream();
            var head = new BinaryWriter(file, Encoding.Unicode, leaveOpen: true);
            head.Write((ushort)0x3158);              // "X1"
            head.Write((int)body.Length);            // everything after this field
            head.Flush();
            body.Position = 0;
            body.CopyTo(file);
            return file.ToArray();
        }

        private void Index(Node n)
        {
            if (!_elements.Contains(n.Name)) _elements.Add(n.Name);
            foreach (var a in n.Attributes.Keys)
                if (!_attributes.Contains(a)) _attributes.Add(a);
            foreach (var c in n.Children) Index(c);
        }

        private static void WriteTable(BinaryWriter w, List<string> names)
        {
            w.Write(names.Count);
            foreach (var n in names) WriteString(w, n);
        }

        private static void WriteString(BinaryWriter w, string s)
        {
            w.Write(s.Length);
            if (s.Length > 0) w.Write(Encoding.Unicode.GetBytes(s));
        }

        private void WriteNode(BinaryWriter w, Node n)
        {
            var inner = new MemoryStream();
            var b = new BinaryWriter(inner, Encoding.Unicode, leaveOpen: true);
            WriteString(b, n.Text);
            b.Write(_elements.IndexOf(n.Name));
            b.Write(1);                              // the source line
            b.Write(n.Attributes.Count);
            foreach (var (name, value) in n.Attributes)
            {
                b.Write(_attributes.IndexOf(name));
                WriteString(b, value);
            }
            b.Write(n.Children.Count);
            foreach (var c in n.Children) WriteNode(b, c);
            b.Flush();

            w.Write((ushort)0x4E58);                 // "XN"
            w.Write((int)inner.Length);              // covers every descendant
            inner.Position = 0;
            inner.CopyTo(w.BaseStream);
        }
    }
}
