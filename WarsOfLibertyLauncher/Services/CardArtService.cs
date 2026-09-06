using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WarsOfLibertyLauncher.Services;

/// <summary>
/// Turns a tech's <c>&lt;Icon&gt;</c> value into a picture, from wherever the mod keeps it.
///
/// <para><b>Both sources are needed, and the split is lopsided.</b> Measured on a real 35-card
/// deck: every card has an icon, <b>9 are loose under <c>art\</c> and 26 live only inside a
/// <c>.bar</c></b> — the bar-only ones being the generic Asian Dynasties art (crates, combat
/// upgrades) that the most cards share. Reading only loose files would show a quarter of a
/// deck.</para>
///
/// <para><b>Everything is frozen, so this runs off the UI thread.</b> That is not optional:
/// building the archive index means reading five tables of contents, and the first call for an
/// install pays for it.</para>
/// </summary>
public static class CardArtService
{
    /// <summary>Icons are 64x64 or 128x128; a whole deck is a few megabytes decoded.</summary>
    private const string DdtExtension = ".ddt";

    private static readonly ConcurrentDictionary<string, InstallArt> Installs =
        new(StringComparer.OrdinalIgnoreCase);

    public static void ResetCache() => Installs.Clear();

    /// <summary>
    /// Pictures for the icon paths asked for, keyed by the raw value that was passed in. A path
    /// that resolves to nothing is simply absent — the card then shows without a picture, which
    /// is visible, rather than taking the grid down.
    ///
    /// <para><b>Call this from a background thread.</b> The result is frozen and safe to hand
    /// straight to the UI.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, ImageSource> Load(
        string? installPath, IEnumerable<string?> iconPaths)
    {
        var result = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(installPath)) return result;

        InstallArt art;
        try { art = Installs.GetOrAdd(Path.GetFullPath(installPath!), p => new InstallArt(p)); }
        catch { return result; }

        foreach (var raw in iconPaths)
        {
            if (string.IsNullOrWhiteSpace(raw) || result.ContainsKey(raw!)) continue;
            var image = art.Get(raw!);
            if (image != null) result[raw!] = image;
        }

        return result;
    }

    /// <summary>One install's art: the archive index, built once, and the pictures decoded so far.</summary>
    private sealed class InstallArt
    {
        private readonly string _installPath;
        private readonly ConcurrentDictionary<string, ImageSource?> _decoded =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly object _indexLock = new();
        private Dictionary<string, (string Bar, BarEntry Entry)>? _index;

        public InstallArt(string installPath) => _installPath = installPath;

        public ImageSource? Get(string rawIconPath)
        {
            var relative = Normalize(rawIconPath);
            if (relative.Length == 0) return null;

            // Nulls are cached too: a card whose art is genuinely missing must not re-walk the
            // archives every time its deck is drawn.
            return _decoded.GetOrAdd(relative, Decode);
        }

        private ImageSource? Decode(string relative)
        {
            var bytes = ReadLoose(relative) ?? ReadFromArchives(relative);
            if (bytes == null) return null;

            var image = DdtDecoder.Decode(bytes);
            if (image == null) return null;

            try
            {
                var source = BitmapSource.Create(
                    image.Width, image.Height, 96, 96,
                    PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
                source.Freeze();
                return source;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"CardArtService: could not build '{relative}' — {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The two shapes a loose icon takes. Some <c>&lt;Icon&gt;</c> values already begin with
        /// <c>art\</c> and some do not, and Windows compares paths case-insensitively, which is
        /// what saves us from the file's inconsistent casing.
        /// </summary>
        private byte[]? ReadLoose(string relative)
        {
            foreach (var candidate in Candidates(relative))
            {
                var full = Path.Combine(_installPath, candidate);
                try
                {
                    if (File.Exists(full)) return File.ReadAllBytes(full);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"CardArtService: could not read '{full}' — {ex.Message}");
                }
            }
            return null;
        }

        private byte[]? ReadFromArchives(string relative)
        {
            var index = Index();
            if (index.Count == 0) return null;

            foreach (var candidate in ArchiveKeys(relative))
            {
                if (index.TryGetValue(candidate, out var hit))
                    return BarArchive.ReadEntry(hit.Bar, hit.Entry);
            }
            return null;
        }

        /// <summary>
        /// Every <c>.ddt</c> in every archive of the install, merged. <b>Only the textures are
        /// kept</b> — the archives hold tens of thousands of entries between them and barely a
        /// third are icons, so indexing the rest would be megabytes of strings nothing ever
        /// looks up.
        ///
        /// <para><b>The archives at the install ROOT are read first, and that order is the whole
        /// point.</b> <c>art\*.bar</c> is the base game's art; a mod that replaces a texture ships
        /// the replacement in an archive at the root. Improvement Mod overrides 2,282 of them in
        /// <c>ImpMod.bar</c>, thirty of which are civilization flags — British, French, Dutch,
        /// China — so with <c>art\</c> winning, the launcher would draw the vanilla flag beside
        /// the mod's civilization and look like it was working. Where no mod is involved the two
        /// layers agree: of Wars of Liberty's 276 shared textures 265 are byte-identical, and the
        /// eleven that differ are ESO chrome and map screenshots nothing here looks up.</para>
        ///
        /// <para><b>The two layers name their entries differently.</b> <c>art\*.bar</c> writes
        /// <c>objects\flags\x.ddt</c>, the root archives write <c>Art\objects\flags\x.ddt</c> —
        /// measured, and consistent within each layer. The prefix comes off when the key is
        /// built, so a single lookup finds either.</para>
        /// </summary>
        private Dictionary<string, (string Bar, BarEntry Entry)> Index()
        {
            if (_index != null) return _index;

            lock (_indexLock)
            {
                if (_index != null) return _index;

                var index = new Dictionary<string, (string, BarEntry)>(StringComparer.OrdinalIgnoreCase);

                foreach (var bar in ArtArchives())
                {
                    try
                    {
                        foreach (var entry in BarArchive.ReadIndex(bar))
                        {
                            if (!entry.Name.EndsWith(DdtExtension, StringComparison.OrdinalIgnoreCase))
                                continue;

                            // First archive wins, and the order above is what decides which.
                            index.TryAdd(IndexKey(entry.Name), (bar, entry));
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Write($"CardArtService: could not index '{bar}' — {ex.Message}");
                    }
                }

                DiagnosticLog.Write($"CardArtService: {index.Count} textures indexed in the archives.");
                return _index = index;
            }
        }

        /// <summary>
        /// Where to look, in order: whatever the mod put at the install root, then the base game's
        /// own <c>art\</c>.
        ///
        /// <para>By pattern, <b>never by file name</b> — <c>ImpMod.bar</c> and <c>DataPN.bar</c>
        /// appear nowhere in this code, so a mod added to the catalogue tomorrow resolves through
        /// exactly this path with nothing written for it. Adding a mod is a data change.</para>
        /// </summary>
        private IEnumerable<string> ArtArchives()
        {
            foreach (var bar in BarsIn(_installPath)) yield return bar;
            foreach (var bar in BarsIn(Path.Combine(_installPath, "art"))) yield return bar;
        }

        private static IReadOnlyList<string> BarsIn(string directory)
        {
            try
            {
                return Directory.Exists(directory)
                    ? Directory.GetFiles(directory, "*.bar", SearchOption.TopDirectoryOnly)
                    : Array.Empty<string>();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"CardArtService: could not list '{directory}' — {ex.Message}");
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// One key for an entry named either way. The <c>art\</c> the root archives put in front
        /// is dropped here rather than tried as an extra candidate at lookup time: this fixes the
        /// index instead of multiplying every search, and <see cref="ArchiveKeys"/> already strips
        /// the same prefix off the value the XML asked for.
        /// </summary>
        private static string IndexKey(string entryName)
        {
            var key = Normalize(entryName);
            return key.StartsWith("art\\", StringComparison.OrdinalIgnoreCase)
                ? key.Substring(4)
                : key;
        }

        private static IEnumerable<string> Candidates(string relative)
        {
            yield return Path.Combine("art", relative) + DdtExtension;
            if (relative.StartsWith("art\\", StringComparison.OrdinalIgnoreCase))
                yield return relative + DdtExtension;
        }

        /// <summary>
        /// <b>No extension here, unlike <see cref="Candidates"/>.</b> The index is keyed by
        /// <see cref="Normalize"/>, which strips <c>.ddt</c>, so appending it to the lookup makes
        /// every key miss — measured, and it cost 26 of one deck's 35 icons while looking exactly
        /// like an archive that had failed to parse.
        ///
        /// <para>Archive names are relative to <c>art\</c> (<c>effects\icon.ddt</c>), so an
        /// <c>&lt;Icon&gt;</c> value that carries the prefix has to be tried without it too.</para>
        /// </summary>
        private static IEnumerable<string> ArchiveKeys(string relative)
        {
            yield return relative;
            if (relative.StartsWith("art\\", StringComparison.OrdinalIgnoreCase))
                yield return relative.Substring(4);
        }
    }

    /// <summary>
    /// One shape for a path that arrives written three ways: forward or backward slashes, a
    /// leading separator or not, and with or without the extension the XML never writes.
    /// </summary>
    private static string Normalize(string path)
    {
        var text = path.Trim().Replace('/', '\\').Trim('\\');
        return text.EndsWith(DdtExtension, StringComparison.OrdinalIgnoreCase)
            ? text.Substring(0, text.Length - DdtExtension.Length)
            : text;
    }
}
