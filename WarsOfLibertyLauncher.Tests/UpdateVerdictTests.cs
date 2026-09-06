using System.Collections.Generic;
using WarsOfLibertyLauncher.Models;
using WarsOfLibertyLauncher.Services;
using Xunit;

namespace WarsOfLibertyLauncher.Tests;

/// <summary>
/// Pins the one question three screens kept answering differently: "does this mod have an
/// update waiting?".
///
/// <para>The case that mattered is <see cref="ModUpdateMechanism.GitHubReleases"/> with an
/// EMPTY <c>PendingDownloads</c>. That list is a WolPatcher concept and
/// <see cref="UpdateService"/> returns it empty by construction for every other mechanism, so
/// the Properties dialog and the Workshop row — both of which read it — could never report an
/// update for a GitHub-released mod. Improvement Mod sat on 25.07.2026 with 06.09.2026
/// published and the dialog showed a green "You're up to date."</para>
///
/// <para>The other half is that nothing here ORDERS two tags. A tag is whatever the modder
/// typed; Improvement Mod names its releases <c>25.07.2026</c>, and parsing that as a version
/// sorts it by day and calls 06.09.2026 older. The source declares which release is latest —
/// this only asks whether the installed one is that.</para>
/// </summary>
public class UpdateVerdictTests
{
    private static ModProfile Profile(ModUpdateMechanism mechanism) =>
        new() { Id = "improvement-mod", UpdateMechanism = mechanism };

    private static UpdateService.CheckResult Result(
        string? current,
        string? latest,
        int pending = 0,
        bool valid = true)
        => new(
            new UpdateInfo(),
            current == null ? null : new VersionInfo { Ver = current },
            latest == null ? null : new VersionInfo { Ver = latest },
            MakePending(pending),
            valid);

    private static List<DownloadInfo> MakePending(int count)
    {
        var list = new List<DownloadInfo>();
        for (int i = 0; i < count; i++) list.Add(new DownloadInfo { Id = i });
        return list;
    }

    private static ModState State(string pinned = "") => new() { PinnedVersion = pinned };

    // ---- the bug in the screenshot ---------------------------------------

    [Fact]
    public void GitHubReleases_NewerPublished_IsAnUpdate_EvenWithNoPendingDownloads()
        => Assert.Equal(
            UpdateVerdict.UpdateOffer.UpdateAvailable,
            UpdateVerdict.Evaluate(
                Result("25.07.2026", "06.09.2026"),
                Profile(ModUpdateMechanism.GitHubReleases),
                State()));

    [Fact]
    public void GitHubReleases_NewerPublished_ShowsTheWorkshopBadge()
        => Assert.True(UpdateVerdict.HasUpdate(
            Result("25.07.2026", "06.09.2026"),
            Profile(ModUpdateMechanism.GitHubReleases),
            State()));

    /// <summary>
    /// The tag that would sort BACKWARDS if anyone parsed it as a version number: day 6 is
    /// less than day 25. Ordering is never consulted, so the direction cannot matter.
    /// </summary>
    [Theory]
    [InlineData("25.07.2026", "06.09.2026")]
    [InlineData("06.09.2026", "25.07.2026")]
    [InlineData("2.1.7b", "2.1.8")]
    [InlineData("Improvement-Mod", "13.08.2026")]
    public void AnyTwoDifferentTagsAreAnOffer_NoOrderingInvolved(string current, string latest)
        => Assert.Equal(
            UpdateVerdict.UpdateOffer.UpdateAvailable,
            UpdateVerdict.Evaluate(
                Result(current, latest),
                Profile(ModUpdateMechanism.GitHubReleases),
                State()));

    // ---- the ordinary states ---------------------------------------------

    [Fact]
    public void SameTag_IsUpToDate()
        => Assert.Equal(
            UpdateVerdict.UpdateOffer.UpToDate,
            UpdateVerdict.Evaluate(
                Result("06.09.2026", "06.09.2026"),
                Profile(ModUpdateMechanism.GitHubReleases),
                State()));

    [Fact]
    public void TagComparisonIgnoresCase()
        => Assert.Equal(
            UpdateVerdict.UpdateOffer.UpToDate,
            UpdateVerdict.Evaluate(
                Result("v1.2B", "V1.2b"),
                Profile(ModUpdateMechanism.GitHubReleases),
                State()));

    [Fact]
    public void NotAValidInstall_IsNotInstalled()
        => Assert.Equal(
            UpdateVerdict.UpdateOffer.NotInstalled,
            UpdateVerdict.Evaluate(
                Result("", "06.09.2026", valid: false),
                Profile(ModUpdateMechanism.GitHubReleases),
                State()));

    /// <summary>A DETECTED GitHubReleases mod: on disk, but the launcher never stamped a
    /// version because it did not do the install. Still offerable — the re-overlay stamps
    /// it — which is why HasUpdate counts this state too.</summary>
    [Fact]
    public void InstalledButUnstamped_IsVersionUnknown_AndStillOffered()
    {
        var result = Result(null, "06.09.2026");
        var profile = Profile(ModUpdateMechanism.GitHubReleases);
        Assert.Equal(
            UpdateVerdict.UpdateOffer.VersionUnknown,
            UpdateVerdict.Evaluate(result, profile, State()));
        Assert.True(UpdateVerdict.HasUpdate(result, profile, State()));
    }

    /// <summary>
    /// Nothing published to compare against. This is a FAILED check, not "you are current" —
    /// claiming the latter off an unreachable fetch is the lie this class removes.
    /// </summary>
    [Fact]
    public void NoLatestPublished_IsFailed_NotUpToDate()
        => Assert.Equal(
            UpdateVerdict.UpdateOffer.Failed,
            UpdateVerdict.Evaluate(
                Result("25.07.2026", null),
                Profile(ModUpdateMechanism.GitHubReleases),
                State()));

    [Fact]
    public void NullResult_IsFailed()
        => Assert.Equal(
            UpdateVerdict.UpdateOffer.Failed,
            UpdateVerdict.Evaluate(null, Profile(ModUpdateMechanism.GitHubReleases), State()));

    // ---- the pin ----------------------------------------------------------

    [Fact]
    public void PinnedToTheInstalledVersion_PausesTheOffer()
        => Assert.Equal(
            UpdateVerdict.UpdateOffer.PausedByPin,
            UpdateVerdict.Evaluate(
                Result("25.07.2026", "06.09.2026"),
                Profile(ModUpdateMechanism.GitHubReleases),
                State(pinned: "25.07.2026")));

    [Fact]
    public void PausedByPin_IsNotABadge()
        => Assert.False(UpdateVerdict.HasUpdate(
            Result("25.07.2026", "06.09.2026"),
            Profile(ModUpdateMechanism.GitHubReleases),
            State(pinned: "25.07.2026")));

    /// <summary>A stale pin naming a version the user no longer has must not pause anything.
    /// The live config carried exactly this: pinned 24.07.2026 while 25.07.2026 was
    /// installed.</summary>
    [Fact]
    public void StalePinNamingAnotherVersion_DoesNotPause()
        => Assert.Equal(
            UpdateVerdict.UpdateOffer.UpdateAvailable,
            UpdateVerdict.Evaluate(
                Result("25.07.2026", "06.09.2026"),
                Profile(ModUpdateMechanism.GitHubReleases),
                State(pinned: "24.07.2026")));

    // ---- WolPatcher keeps its own meaning ---------------------------------

    [Fact]
    public void WolPatcher_PendingChain_IsAnUpdate()
        => Assert.Equal(
            UpdateVerdict.UpdateOffer.UpdateAvailable,
            UpdateVerdict.Evaluate(
                Result("1.0.13", "1.0.14", pending: 2),
                Profile(ModUpdateMechanism.WolPatcher),
                State()));

    /// <summary>
    /// The patcher deduces the installed version from file hashes, so an empty chain really
    /// does mean nothing to apply — even when the two tags differ, which they can while a
    /// manifest is mid-publish. Reading the tags here instead would have changed what the
    /// Workshop badge means for WoL.
    /// </summary>
    [Fact]
    public void WolPatcher_NoPendingChain_IsUpToDate_EvenIfTagsDiffer()
        => Assert.Equal(
            UpdateVerdict.UpdateOffer.UpToDate,
            UpdateVerdict.Evaluate(
                Result("1.0.13", "1.0.14", pending: 0),
                Profile(ModUpdateMechanism.WolPatcher),
                State()));

    [Fact]
    public void WolPatcher_UnknownVersionAndNothingPending_IsVersionUnknown()
        => Assert.Equal(
            UpdateVerdict.UpdateOffer.VersionUnknown,
            UpdateVerdict.Evaluate(
                Result(null, "1.0.14", pending: 0),
                Profile(ModUpdateMechanism.WolPatcher),
                State()));

    // ---- the other mechanisms --------------------------------------------

    [Theory]
    [InlineData(ModUpdateMechanism.Manual)]
    [InlineData(ModUpdateMechanism.DelegatedExternal)]
    public void OtherMechanisms_CompareTagsToo(ModUpdateMechanism mechanism)
        => Assert.Equal(
            UpdateVerdict.UpdateOffer.UpdateAvailable,
            UpdateVerdict.Evaluate(Result("1.0", "1.1"), Profile(mechanism), State()));
}
