using System.Collections.Generic;
using System.Linq;
using WarsOfLibertyLauncher.Models;
using WarsOfLibertyLauncher.Services;
using Xunit;

namespace WarsOfLibertyLauncher.Tests;

/// <summary>
/// Pure-logic regression tests for <see cref="NotificationCenter"/> — the
/// Steam-style notification bell backing store. A no-op persist callback keeps
/// these off the real <c>launcher-config.json</c>. Covers the per-kind dedup
/// rules, the 50-item cap, and the unread accounting that drives the badge.
/// </summary>
public class NotificationCenterTests
{
    private static NotificationCenter NewCenter(out LauncherConfig config)
    {
        config = new LauncherConfig();
        return new NotificationCenter(config, persist: () => { });
    }

    // ---------- announcements ----------
    //
    // News from the project, delivered so people stop having to remember to go and look for it.
    // The rules that matter are the ones that stop it becoming spam.

    /// <summary>
    /// <b>The one that matters.</b> The first read must record everything already published
    /// WITHOUT belling any of it, or the day somebody installs the launcher they are handed the
    /// whole back catalogue at once — the same trap the catalog listing and the translation index
    /// each had to solve.
    /// </summary>
    [Fact]
    public void Announcements_FirstReadSeedsSilently_AndNothingBells()
    {
        var center = NewCenter(out var config);

        Assert.True(center.SeedAnnouncementBaseline(new[] { "a", "b", "c" }));
        Assert.Empty(center.Items);
        Assert.True(config.AnnouncementBaselineSeeded);

        // Already published before we ever looked: still silent afterwards.
        Assert.False(center.RaiseAnnouncement("a", "Old news", "", ""));
        Assert.Empty(center.Items);
    }

    /// <summary>The seed happens once; a later read must not re-baseline and swallow real news.</summary>
    [Fact]
    public void Announcements_SeedingIsOneShot()
    {
        var center = NewCenter(out _);

        Assert.True(center.SeedAnnouncementBaseline(new[] { "a" }));
        Assert.False(center.SeedAnnouncementBaseline(new[] { "b" }));
        // 'b' was NOT swallowed by the second call, so it can still be announced.
        Assert.True(center.RaiseAnnouncement("b", "Real news", "", ""));
    }

    /// <summary>
    /// <b>The regression.</b> The feed ships with an empty announcements list, so the baseline
    /// has to be seedable from NOTHING. It was not: the caller returned early when there was
    /// nothing to seed, the marker was never written, and the first announcement ever published
    /// then arrived, ran the seed for the first time, was swallowed as "the backlog", and reached
    /// nobody — the one announcement that matters most, since it announces the feature.
    /// </summary>
    [Fact]
    public void Announcements_AnEmptyBaselineStillCounts_SoTheFirstOnePublishedBells()
    {
        var center = NewCenter(out var config);

        Assert.True(center.SeedAnnouncementBaseline(System.Array.Empty<string>()));
        Assert.True(config.AnnouncementBaselineSeeded);
        Assert.Empty(center.Items);

        // The first thing ever published, arriving after that empty seed, must ring.
        Assert.True(center.RaiseAnnouncement("first-ever", "Competitive rooms", "…", ""));
        Assert.Equal(1, center.Items.Count(i => i.Kind == NotificationKind.Announcement));
    }

    [Fact]
    public void Announcements_SomethingNewAfterTheBaselineDoesBell()
    {
        var center = NewCenter(out _);
        center.SeedAnnouncementBaseline(new[] { "a" });

        Assert.True(center.RaiseAnnouncement("b", "Competitive rooms", "Ranked play is here.", ""));
        Assert.Equal(1, center.Items.Count(i => i.Kind == NotificationKind.Announcement));
    }

    /// <summary>
    /// The feed is re-read every few minutes and the same items come back every time, so without
    /// the dedup one announcement would ring forever.
    /// </summary>
    [Fact]
    public void Announcements_TheSameIdNeverBellsTwice()
    {
        var center = NewCenter(out _);
        center.SeedAnnouncementBaseline(System.Array.Empty<string>());

        Assert.True(center.RaiseAnnouncement("news-1", "Title", "Body", ""));
        Assert.False(center.RaiseAnnouncement("news-1", "Title", "Body", ""));
        Assert.False(center.RaiseAnnouncement("NEWS-1", "Title", "Body", ""));   // case-insensitive
        Assert.Equal(1, center.Items.Count(i => i.Kind == NotificationKind.Announcement));
    }

    /// <summary>
    /// An entry with no id would bell on every single poll, and one with no title would be a blank
    /// row. Both are refused rather than shown.
    /// </summary>
    [Fact]
    public void Announcements_WithoutAnIdOrATitleAreRefused()
    {
        var center = NewCenter(out _);
        center.SeedAnnouncementBaseline(System.Array.Empty<string>());

        Assert.False(center.RaiseAnnouncement("", "Title", "b", ""));
        Assert.False(center.RaiseAnnouncement("   ", "Title", "b", ""));
        Assert.False(center.RaiseAnnouncement("id", "", "b", ""));
        Assert.Empty(center.Items);
    }

    /// <summary>
    /// The url rides on TargetId because clicking an announcement leaves the app — there is
    /// nowhere in the launcher to navigate to. An empty one is allowed: the click falls back to
    /// the project's Discord, so a published announcement always leads somewhere.
    /// </summary>
    [Fact]
    public void Announcements_CarryTheirUrlAsTheClickTarget()
    {
        var center = NewCenter(out _);
        center.SeedAnnouncementBaseline(System.Array.Empty<string>());

        center.RaiseAnnouncement("n", "T", "B", "https://example.com/post");
        var item = center.Items.Single(i => i.Kind == NotificationKind.Announcement);
        Assert.Equal("https://example.com/post", item.TargetId);
        // Not tied to a mod — like the launcher-update and connectivity items.
        Assert.True(string.IsNullOrEmpty(item.ModId));
    }

    // ---------- catalog patch notices (mods that are NOT installed) ----------
    //
    // The same spam rules as announcements, for the same reason, plus one of its own: this
    // latch must never be shared with the installed-mod one.

    /// <summary>
    /// <b>The one that matters.</b> Without the seed, the first feed read bells a patch notice
    /// for every mod in the catalog at once.
    /// </summary>
    [Fact]
    public void CatalogPatches_FirstReadSeedsSilently_AndNothingBells()
    {
        var center = NewCenter(out var config);

        Assert.True(center.SeedCatalogVersionBaseline(new Dictionary<string, string>
        {
            ["improvement-mod"] = "06.09.2026",
            ["napoleonic-era"] = "2.1.7b",
        }));

        Assert.Empty(center.Items);
        Assert.True(config.CatalogVersionBaselineSeeded);
        Assert.Equal("06.09.2026", config.NotifiedCatalogVersions["improvement-mod"]);
    }

    [Fact]
    public void CatalogPatches_SeedRunsOnce()
    {
        var center = NewCenter(out _);

        Assert.True(center.SeedCatalogVersionBaseline(
            new Dictionary<string, string> { ["improvement-mod"] = "06.09.2026" }));
        Assert.False(center.SeedCatalogVersionBaseline(
            new Dictionary<string, string> { ["napoleonic-era"] = "2.1.8" }));
    }

    [Fact]
    public void CatalogPatches_AfterTheBaseline_ANewVersionBellsExactlyOnce()
    {
        var center = NewCenter(out _);
        center.SeedCatalogVersionBaseline(
            new Dictionary<string, string> { ["improvement-mod"] = "25.07.2026" });

        Assert.True(center.RaiseModPatch("improvement-mod", "06.09.2026", "t", "b"));
        Assert.False(center.RaiseModPatch("improvement-mod", "06.09.2026", "t", "b"));

        Assert.Single(center.Items, i => i.Kind == NotificationKind.ModPatchPublished);
    }

    [Fact]
    public void CatalogPatches_TheSeededVersionItselfNeverBells()
    {
        var center = NewCenter(out _);
        center.SeedCatalogVersionBaseline(
            new Dictionary<string, string> { ["improvement-mod"] = "06.09.2026" });

        Assert.False(center.RaiseModPatch("improvement-mod", "06.09.2026", "t", "b"));
        Assert.Empty(center.Items);
    }

    /// <summary>
    /// An installed mod records its version but stays silent here: it bells through
    /// RaiseUpdateAvailable instead. Leaving the entry stale would fire a patch notice the
    /// moment the player uninstalled the mod.
    /// </summary>
    [Fact]
    public void CatalogPatches_RecordOnly_UpdatesTheLatchWithoutBelling()
    {
        var center = NewCenter(out var config);
        center.SeedCatalogVersionBaseline(
            new Dictionary<string, string> { ["improvement-mod"] = "25.07.2026" });

        Assert.False(center.RaiseModPatch("improvement-mod", "06.09.2026", "t", "b", record: false));

        Assert.Empty(center.Items);
        Assert.Equal("06.09.2026", config.NotifiedCatalogVersions["improvement-mod"]);
    }

    /// <summary>
    /// The two latches are separate on purpose. Sharing one would let a patch notice swallow
    /// the real update bell the day the player installs that mod.
    /// </summary>
    [Fact]
    public void CatalogPatches_DoNotConsumeTheInstalledModsUpdateLatch()
    {
        var center = NewCenter(out _);
        center.SeedCatalogVersionBaseline(
            new Dictionary<string, string> { ["improvement-mod"] = "25.07.2026" });

        Assert.True(center.RaiseModPatch("improvement-mod", "06.09.2026", "t", "b"));
        // The player installs it, and the ordinary update path still has something to say.
        Assert.True(center.RaiseUpdateAvailable("improvement-mod", "06.09.2026", "t", "b"));
    }

    [Fact]
    public void CatalogPatches_AnEmptyVersionIsNoAnswer_AndNeverBells()
    {
        var center = NewCenter(out _);
        center.SeedCatalogVersionBaseline(
            new Dictionary<string, string> { ["improvement-mod"] = "25.07.2026" });

        Assert.False(center.RaiseModPatch("improvement-mod", "", "t", "b"));
        Assert.Empty(center.Items);
    }

    [Fact]
    public void UpdateAvailable_SameVersion_DedupesToOneItem()
    {
        var center = NewCenter(out _);

        Assert.True(center.RaiseUpdateAvailable("wol", "1.0.5", "t", "b"));
        Assert.False(center.RaiseUpdateAvailable("wol", "1.0.5", "t", "b")); // same (mod, version)
        Assert.Equal(1, center.Items.Count(i => i.Kind == NotificationKind.UpdateAvailable));
    }

    [Fact]
    public void UpdateAvailable_NewerVersion_BellsAgain()
    {
        var center = NewCenter(out _);

        Assert.True(center.RaiseUpdateAvailable("wol", "1.0.5", "t", "b"));
        Assert.True(center.RaiseUpdateAvailable("wol", "1.0.6", "t", "b")); // a genuinely newer version
        Assert.Equal(2, center.Items.Count(i => i.Kind == NotificationKind.UpdateAvailable));
    }

    [Fact]
    public void UpdateFinished_SupersedesAvailable_AndResetsLatch()
    {
        var center = NewCenter(out _);

        center.RaiseUpdateAvailable("wol", "1.0.6", "t", "b");
        center.RaiseUpdateFinished("wol", "1.0.6", "t", "b");

        // The pending "available" item is dropped; one "finished" remains.
        Assert.DoesNotContain(center.Items, i => i.Kind == NotificationKind.UpdateAvailable);
        Assert.Single(center.Items, i => i.Kind == NotificationKind.UpdateFinished);

        // Latch reset → a FUTURE version can bell "available" again.
        Assert.True(center.RaiseUpdateAvailable("wol", "1.0.7", "t", "b"));
    }

    [Fact]
    public void NewTranslation_DedupesByKey()
    {
        var center = NewCenter(out _);

        Assert.True(center.RaiseNewTranslation("wol", "es@1.0", "es", "t", "b"));
        Assert.False(center.RaiseNewTranslation("wol", "es@1.0", "es", "t", "b")); // same id@version
        Assert.True(center.RaiseNewTranslation("wol", "es@1.1", "es", "t", "b"));  // new version → bells
        Assert.Equal(2, center.Items.Count(i => i.Kind == NotificationKind.NewTranslation));
    }

    [Fact]
    public void Add_TrimsToFiftyMostRecent()
    {
        var center = NewCenter(out _);

        for (int i = 0; i < 60; i++)
            center.RaiseUpdateAvailable("wol", $"1.0.{i}", "t", $"body {i}");

        Assert.Equal(NotificationCenter.MaxItems, center.Items.Count);
        // Newest first → the most recent version is at index 0.
        Assert.Contains("body 59", center.Items[0].Body);
    }

    [Fact]
    public void MarkAllRead_ZeroesUnreadCount()
    {
        var center = NewCenter(out _);
        center.RaiseUpdateAvailable("wol", "1.0.5", "t", "b");
        center.RaiseUpdateFinished("wol", "1.0.5", "t", "b");
        Assert.True(center.UnreadCount > 0);

        center.MarkAllRead();

        Assert.Equal(0, center.UnreadCount);
        Assert.All(center.Items, i => Assert.True(i.Read));
    }

    [Fact]
    public void Clear_EmptiesHistory()
    {
        var center = NewCenter(out _);
        center.RaiseUpdateAvailable("wol", "1.0.5", "t", "b");

        center.Clear();

        Assert.Empty(center.Items);
        Assert.Equal(0, center.UnreadCount);
    }

    [Fact]
    public void Constructor_SeedsFromConfig_NewestFirst()
    {
        var config = new LauncherConfig();
        config.Notifications.Add(new NotificationItem
        {
            Title = "old", CreatedAtUtc = new System.DateTime(2026, 1, 1),
        });
        config.Notifications.Add(new NotificationItem
        {
            Title = "new", CreatedAtUtc = new System.DateTime(2026, 6, 1),
        });

        var center = new NotificationCenter(config, persist: () => { });

        Assert.Equal(2, center.Items.Count);
        Assert.Equal("new", center.Items[0].Title); // newest first
    }

    [Fact]
    public void LauncherUpdate_DedupesByTag()
    {
        var center = NewCenter(out _);

        Assert.True(center.RaiseLauncherUpdate("v1.0.6", "t", "b"));
        Assert.False(center.RaiseLauncherUpdate("v1.0.6", "t", "b")); // same tag
        Assert.True(center.RaiseLauncherUpdate("v1.0.7", "t", "b"));   // new tag → bells
        Assert.Equal(2, center.Items.Count(i => i.Kind == NotificationKind.LauncherUpdate));
    }

    [Fact]
    public void Connectivity_DedupesConsecutiveSameState()
    {
        var center = NewCenter(out _);

        Assert.True(center.RaiseConnectivity(offline: true, "off", "b"));
        Assert.False(center.RaiseConnectivity(offline: true, "off", "b")); // same state, no spam
        Assert.True(center.RaiseConnectivity(offline: false, "on", "b"));   // flip → bells
        Assert.True(center.RaiseConnectivity(offline: true, "off", "b"));   // flip back → bells
        Assert.Equal(3, center.Items.Count(i => i.Kind == NotificationKind.Connectivity));
    }

    [Fact]
    public void NewMod_DedupesById()
    {
        var center = NewCenter(out _);

        Assert.True(center.RaiseNewMod("napoleonic-era", "t", "b"));
        Assert.False(center.RaiseNewMod("napoleonic-era", "t", "b")); // same id
        Assert.True(center.RaiseNewMod("colonial-wars", "t", "b"));
        Assert.Equal(2, center.Items.Count(i => i.Kind == NotificationKind.NewMod));
    }

    [Fact]
    public void Installed_AddsItem_NotDeduped()
    {
        var center = NewCenter(out _);

        // An install is user-initiated and raised once per install; unlike UpdateFinished
        // it is NOT deduped, so a second copy of the same version still confirms.
        Assert.True(center.RaiseInstalled("wol", "1.2.0d", "t", "b"));
        Assert.True(center.RaiseInstalled("wol", "1.2.0d", "t", "b"));
        Assert.Equal(2, center.Items.Count(i => i.Kind == NotificationKind.Installed));
    }

    [Fact]
    public void SeedCatalogBaseline_SuppressesExisting_ThenBellsOnlyNew()
    {
        var center = NewCenter(out var config);

        // First fetch: baseline the whole existing catalog silently → nothing bells.
        Assert.True(center.SeedCatalogBaseline(new[] { "wol", "aoe3-tad", "improvement-mod" }));
        Assert.Empty(center.Items);
        Assert.True(config.CatalogBaselineSeeded);

        // Baseline is one-shot.
        Assert.False(center.SeedCatalogBaseline(new[] { "another" }));

        // A pre-existing id doesn't bell; a genuinely-new one does.
        Assert.False(center.RaiseNewMod("improvement-mod", "t", "b"));
        Assert.True(center.RaiseNewMod("napoleonic-era", "t", "b"));
        Assert.Single(center.Items, i => i.Kind == NotificationKind.NewMod);
    }
}
