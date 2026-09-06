using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WarsOfLibertyLauncher.Tests;

/// <summary>
/// Writes a <c>.bar</c> archive, so a test can state what an install contains and get a real
/// file.
///
/// <para>The archives themselves are gigabytes and are not in the repository, so the container is
/// built by hand to the layout measured against all eight of Wars of Liberty's: magic and two
/// words, the file count at 0x118 and the table offset at 0x11C, then a length-prefixed UTF-16
/// root name, one more word, and the entries.</para>
///
/// <para>It lives here rather than inside one test class because three of them now need it —
/// the reader's own tests, the art index, and the civilization list that two mods ship only in
/// here.</para>
/// </summary>
internal static class TestArchive
{
    /// <summary>
    /// One file to put in. <paramref name="DeclaredUncompressed"/> is separate from the data's
    /// real length only so a test can make the two disagree, which is the shape the reader
    /// refuses.
    /// </summary>
    internal sealed record Planned(string Name, byte[] Data, uint DeclaredUncompressed);

    internal static Planned Entry(string name, byte[] data) => new(name, data, (uint)data.Length);

    /// <summary>Writes an archive at <paramref name="path"/>, creating its folder.</summary>
    internal static string Write(string path, params Planned[] files) =>
        Write(path, "ESPN", files);

    internal static string Write(string path, string magic, params Planned[] files)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream, Encoding.Unicode);

        w.Write(Encoding.ASCII.GetBytes(magic));
        w.Write(2u);
        w.Write(0x44332211u);
        w.Write(new byte[0x118 - 12]);

        var countAt = stream.Position;
        w.Write(0u);            // file count, back-filled
        w.Write(0u);            // toc offset, back-filled

        var offsets = new List<long>();
        foreach (var file in files)
        {
            offsets.Add(stream.Position);
            w.Write(file.Data);
        }

        var tocAt = stream.Position;
        WriteName(w, "Art\\");
        w.Write((uint)files.Length);

        for (var i = 0; i < files.Length; i++)
        {
            w.Write((uint)offsets[i]);
            w.Write((uint)files[i].Data.Length);
            w.Write(files[i].DeclaredUncompressed);
            w.Write(new byte[16]);                  // the timestamp nothing reads
            WriteName(w, files[i].Name);
        }

        stream.Position = countAt;
        w.Write((uint)files.Length);
        w.Write((uint)tocAt);

        return path;
    }

    /// <summary>Length in CHARACTERS, then the UTF-16 bytes — the archive's own convention.</summary>
    private static void WriteName(BinaryWriter w, string name)
    {
        w.Write((uint)name.Length);
        w.Write(Encoding.Unicode.GetBytes(name));
    }
}
