using System;
using WarsOfLibertyLauncher.Models;

namespace WarsOfLibertyLauncher.Services;

/// <summary>
/// The one place that answers "does this mod have an update waiting?".
///
/// <para><b>Why this exists.</b> Three screens asked that question and two of them got it
/// wrong, in the same way. The dashboard compared the installed tag against the latest one;
/// the Properties dialog and the Workshop row both read
/// <see cref="UpdateService.CheckResult.PendingDownloads"/> instead — and that list is a
/// WolPatcher concept. <see cref="UpdateService"/> returns it EMPTY by construction for
/// <see cref="ModUpdateMechanism.GitHubReleases"/>, <see cref="ModUpdateMechanism.Manual"/>
/// and <see cref="ModUpdateMechanism.DelegatedExternal"/>, so for those mods the verdict could
/// never be anything but "up to date" — a green panel over a mod with three newer releases
/// published, and a Workshop badge and an "Updates" filter that were blind to the same mods.
/// One judge, so the three cannot drift apart again.</para>
///
/// <para><b>Equality, never ordering.</b> A tag is whatever the modder typed. Improvement Mod
/// names its releases <c>25.07.2026</c>; parsing that as a version number sorts it by DAY and
/// decides that <c>06.09.2026</c> is older. So nothing here tries to rank two tags: the source
/// declares which release is the latest one, and this only asks whether the installed tag is
/// that one. Different means there is something to offer.</para>
/// </summary>
internal static class UpdateVerdict
{
    /// <summary>What the launcher should say about a mod's update state.</summary>
    internal enum UpdateOffer
    {
        /// <summary>No valid install to compare against.</summary>
        NotInstalled,

        /// <summary>Installed, but the launcher never stamped which version — the ordinary
        /// state of a DETECTED GitHubReleases mod. Still offerable: the re-overlay stamps it.</summary>
        VersionUnknown,

        /// <summary>The installed tag is the one the source calls latest.</summary>
        UpToDate,

        /// <summary>There is something newer to install.</summary>
        UpdateAvailable,

        /// <summary>There is, but the user pinned this version and asked not to be prompted.</summary>
        PausedByPin,

        /// <summary>The check itself did not produce a usable answer.</summary>
        Failed,
    }

    /// <summary>
    /// The verdict for one mod.
    /// </summary>
    /// <param name="result">The check result, or null when the check failed outright.</param>
    /// <param name="profile">The mod, for its <see cref="ModProfile.UpdateMechanism"/>.</param>
    /// <param name="state">That mod's persisted state, for <see cref="ModState.PinnedVersion"/>.</param>
    internal static UpdateOffer Evaluate(
        UpdateService.CheckResult? result, ModProfile? profile, ModState? state)
    {
        if (result == null || profile == null) return UpdateOffer.Failed;
        if (!result.IsValidInstall) return UpdateOffer.NotInstalled;

        var current = result.CurrentVersion?.Ver;
        var latest = result.LatestVersion?.Ver;

        bool offer;
        if (profile.UpdateMechanism == ModUpdateMechanism.WolPatcher)
        {
            // The patcher's own answer: a computed chain of .tar.xz patches. Here the list is
            // the verdict, and an empty one really does mean there is nothing to apply.
            //
            // Asked BEFORE the unknown-version case on purpose: the patcher deduces the
            // installed version from file hashes, so a pending chain is a real answer even
            // when the stamped version is missing, and the Workshop badge must not change
            // meaning for these mods.
            offer = result.PendingDownloads.Count > 0;
            if (!offer && string.IsNullOrEmpty(current)) return UpdateOffer.VersionUnknown;
        }
        else
        {
            // Nothing published to compare against: the check produced no answer rather than
            // an answer of "up to date". Saying "you're current" off a failed fetch is the
            // lie this class was written to remove.
            if (string.IsNullOrEmpty(latest)) return UpdateOffer.Failed;

            // A valid install whose version was never stamped. Offer the re-overlay: it is an
            // update in place, and its tail stamps LastKnownVersion, so one click self-heals.
            if (string.IsNullOrEmpty(current)) return UpdateOffer.VersionUnknown;

            offer = !string.Equals(current, latest, StringComparison.OrdinalIgnoreCase);
        }

        if (!offer) return UpdateOffer.UpToDate;

        // The user opted to keep playing this version. Nothing is auto-updated either way —
        // the pin only decides whether they are prompted.
        var pinned = state?.PinnedVersion;
        if (!string.IsNullOrEmpty(pinned)
            && !string.IsNullOrEmpty(current)
            && string.Equals(pinned, current, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateOffer.PausedByPin;
        }

        return UpdateOffer.UpdateAvailable;
    }

    /// <summary>
    /// Whether there is an update worth showing a badge for. <see cref="UpdateOffer.VersionUnknown"/>
    /// counts: a detected mod with no stamped version is exactly the case the Workshop badge
    /// used to miss forever.
    /// </summary>
    internal static bool HasUpdate(
        UpdateService.CheckResult? result, ModProfile? profile, ModState? state)
    {
        var offer = Evaluate(result, profile, state);
        return offer == UpdateOffer.UpdateAvailable || offer == UpdateOffer.VersionUnknown;
    }
}
