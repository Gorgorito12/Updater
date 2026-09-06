using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WarsOfLibertyLauncher.Services;
using Xunit;

namespace WarsOfLibertyLauncher.Tests;

/// <summary>
/// The <c>.bar</c> reader, which is what reaches the card icons that are not loose on disk —
/// three quarters of a real deck's.
///
/// <para>The archives themselves are gigabytes and are not in the repository, so these build the
/// container by hand to the layout measured against all eight of Wars of Liberty's: magic and
/// two words, the file count at 0x118 and the table offset at 0x11C, then a length-prefixed
/// UTF-16 root name, one more word, and the entries.</para>
/// </summary>
public class BarArchiveTests : IDisposable
{
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var dir in _temp)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private string NewDir()
    {
        var dir = Directory.CreateTempSubdirectory("wol-bar-test-").FullName;
        _temp.Add(dir);
        return dir;
    }

    /// <summary>The writer moved to <see cref="TestArchive"/> once the art index and the packed
    /// civilization list needed it too. Same bytes, one copy.</summary>
    private string WriteBar(string magic, params TestArchive.Planned[] files) =>
        TestArchive.Write(Path.Combine(NewDir(), "Art1.bar"), magic, files);

    private static TestArchive.Planned Entry(string name, byte[] data) =>
        TestArchive.Entry(name, data);

    // ------------------------------------------------------------------

    [Fact]
    public void EveryEntryIsIndexedAndReadsBackExactly()
    {
        var first = new byte[] { 1, 2, 3, 4, 5 };
        var second = new byte[] { 9, 8, 7 };

        var bar = WriteBar("ESPN",
            Entry(@"ui\techs\hc_trade_empire.ddt", first),
            Entry(@"effects\particles.xml.xmb", second));

        var index = BarArchive.ReadIndex(bar);

        Assert.Equal(2, index.Count);
        Assert.Equal(@"ui\techs\hc_trade_empire.ddt", index[0].Name);
        Assert.Equal(first, BarArchive.ReadEntry(bar, index[0]));
        Assert.Equal(second, BarArchive.ReadEntry(bar, index[1]));
    }

    /// <summary>
    /// <b>The case this reader exists to refuse.</b> No entry in the shipped archives declares
    /// two different sizes — 44,997 were checked and none did — so nothing here can decompress.
    /// Handing such an entry to the decoder as if it were raw would produce garbage pixels in
    /// silence; skipping it costs one missing icon, which somebody can see.
    /// </summary>
    [Fact]
    public void AnEntryWhoseTwoSizesDisagreeIsSkipped()
    {
        var bar = WriteBar("ESPN",
            new TestArchive.Planned(@"ui\compressed.ddt", new byte[] { 1, 2, 3 }, DeclaredUncompressed: 99),
            Entry(@"ui\plain.ddt", new byte[] { 4, 5 }));

        var index = BarArchive.ReadIndex(bar);

        Assert.Single(index);
        Assert.Equal(@"ui\plain.ddt", index[0].Name);
    }

    [Fact]
    public void SomethingThatIsNotAnArchiveReadsAsEmpty()
    {
        Assert.Empty(BarArchive.ReadIndex(WriteBar("NOPE", Entry("a.ddt", new byte[] { 1 }))));
    }

    [Fact]
    public void AMissingOrBlankPathReadsAsEmptyRatherThanThrowing()
    {
        Assert.Empty(BarArchive.ReadIndex(Path.Combine(NewDir(), "absent.bar")));
        Assert.Empty(BarArchive.ReadIndex(""));
        Assert.Empty(BarArchive.ReadIndex("   "));
    }

    [Fact]
    public void AFileTooShortToHoldAHeaderReadsAsEmpty()
    {
        var path = Path.Combine(NewDir(), "stub.bar");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("ESPN"));
        Assert.Empty(BarArchive.ReadIndex(path));
    }

    /// <summary>An index built against one file must not be trusted against a different one.</summary>
    [Fact]
    public void ReadingPastTheEndOfTheArchiveReturnsNull()
    {
        var bar = WriteBar("ESPN", Entry(@"ui\a.ddt", new byte[] { 1, 2 }));
        var beyond = new BarEntry("ui\\a.ddt", new FileInfo(bar).Length - 1, 4096);

        Assert.Null(BarArchive.ReadEntry(bar, beyond));
    }
}
