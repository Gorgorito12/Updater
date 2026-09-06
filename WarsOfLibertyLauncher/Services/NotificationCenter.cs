using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WarsOfLibertyLauncher.Models;

namespace WarsOfLibertyLauncher.Services;

/// <summary>
/// Steam-style notification bell backing store. Owns the in-memory
/// <see cref="ObservableCollection{T}"/> the bell panel binds to, mirrors it
/// into <see cref="LauncherConfig.Notifications"/> for persistence, and applies
/// the per-kind dedup rules so the same event never bells twice.
///
/// Deliberately UI-free (no WPF types): the caller passes already-localized
/// title/body strings and wires <see cref="ToastRequested"/> to the system-tray
/// toast and <see cref="ItemAdded"/> to the bell's one-shot pulse animation.
/// That keeps the dedup / cap / unread logic unit-testable without a UI thread.
/// </summary>
public sealed class NotificationCenter
{
    /// <summary>Most recent items kept; older ones are trimmed on add.</summary>
    public const int MaxItems = 50;

    private readonly LauncherConfig _config;
    private readonly Action _persist;

    /// <summary>Bound by the bell panel. Newest item first (index 0).</summary>
    public ObservableCollection<NotificationItem> Items { get; }

    /// <summary>Fires on any state change (add / read / remove / clear) so the badge can refresh.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Fires only when a brand-new item is actually added — drives the one-shot bell
    /// pulse and the feedback sound.
    ///
    /// <para>It CARRIES the item: the sound is chosen from
    /// <see cref="NotificationItem.Kind"/>, so a bare <c>EventArgs.Empty</c> would force
    /// every kind to share one tone (which is what it used to do).</para>
    /// </summary>
    public event EventHandler<NotificationItem>? ItemAdded;

    /// <summary>Raised (title, body) when a new item is added, so the host can show a tray toast.</summary>
    public event Action<string, string>? ToastRequested;

    /// <param name="config">Source of persisted history + per-mod dedup state.</param>
    /// <param name="persist">
    /// How to flush the config to disk after a mutation. Defaults to
    /// <see cref="LauncherConfig.Save"/>; tests pass a no-op so they never
    /// touch the real <c>launcher-config.json</c>.
    /// </param>
    public NotificationCenter(LauncherConfig config, Action? persist = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _persist = persist ?? _config.Save;
        // Seed newest-first; tolerate a hand-edited config with nulls.
        var seed = (_config.Notifications ?? new List<NotificationItem>())
            .Where(n => n != null)
            .OrderByDescending(n => n.CreatedAtUtc);
        Items = new ObservableCollection<NotificationItem>(seed);
    }

    /// <summary>Number of unread items — drives the red badge count.</summary>
    public int UnreadCount => Items.Count(i => !i.Read);

    // ---------------------------------------------------------------- raises

    /// <summary>
    /// "Update available" for a mod. Deduped on (mod, version) via
    /// <see cref="ModState.NotifiedUpdateVersion"/> so re-checks don't re-bell
    /// the same version. Returns true if a new item was added.
    /// </summary>
    public bool RaiseUpdateAvailable(string modId, string version, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(modId)) return false;
        var state = _config.GetState(modId);
        if (string.Equals(state.NotifiedUpdateVersion, version, StringComparison.OrdinalIgnoreCase))
            return false;
        state.NotifiedUpdateVersion = version ?? "";
        return Add(new NotificationItem
        {
            Kind = NotificationKind.UpdateAvailable,
            ModId = modId,
            Title = title,
            Body = body,
        });
    }

    /// <summary>
    /// "Update finished" for a mod. Deduped against an existing finished item
    /// for the same (mod, version) still in the visible list.
    /// </summary>
    /// <param name="bypassDedup">
    /// Set ONLY by a raise that follows an operation the user just performed. The dedup
    /// exists for the reconciliation backstop, which must stay idempotent because it runs
    /// on every check; but it silently swallowed a real, user-initiated update when the
    /// same version had been installed before — a maintainer downgraded via the version
    /// picker and updated back, and got nothing. An action you asked for has to answer,
    /// even if the resulting version already belled once. Same reasoning that keeps
    /// <see cref="RaiseInstalled"/> undeduped.
    /// </param>
    public bool RaiseUpdateFinished(
        string modId, string version, string title, string body, bool bypassDedup = false)
    {
        if (string.IsNullOrWhiteSpace(modId)) return false;
        bool dup = !bypassDedup && Items.Any(i => i.Kind == NotificationKind.UpdateFinished
            && string.Equals(i.ModId, modId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(i.TargetId, version, StringComparison.OrdinalIgnoreCase));
        if (dup) return false;
        // A completed update supersedes any pending "update available" for the
        // same mod — clear its dedup latch so a FUTURE version can bell again,
        // and drop the now-stale "available" item from the list.
        var state = _config.GetState(modId);
        state.NotifiedUpdateVersion = "";
        RemoveWhere(i => i.Kind == NotificationKind.UpdateAvailable
            && string.Equals(i.ModId, modId, StringComparison.OrdinalIgnoreCase));
        return Add(new NotificationItem
        {
            Kind = NotificationKind.UpdateFinished,
            ModId = modId,
            Title = title,
            Body = body,
            TargetId = version,
        });
    }

    /// <summary>
    /// A mod's files were laid down and the operation finished — a fresh install, a new
    /// copy, a version picked from the list, or a repair. Distinct from an update, which
    /// has its own kind so "Actualización completada" is never shown for a DOWNGRADE.
    ///
    /// <para>The caller supplies the wording; this kind only drives the icon badge, the
    /// success sound and the click target, which are identical for all four cases — which
    /// is why they share it instead of each getting an enum value, a badge trigger and a
    /// navigation branch that would all behave the same.</para>
    ///
    /// <para>Not deduped, on purpose: these are user-initiated and raised exactly once per
    /// operation (no reconciliation double-fires them), so each one — including a repair,
    /// or a second copy of the same version — gets its own confirmation.</para>
    /// </summary>
    public bool RaiseInstalled(string modId, string version, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(modId)) return false;
        return Add(new NotificationItem
        {
            Kind = NotificationKind.Installed,
            ModId = modId,
            Title = title,
            Body = body,
            TargetId = version,
        });
    }

    /// <summary>
    /// A match that had gone down undecided was rated afterwards, from a recording read late.
    ///
    /// <para>Deduped on the MATCH id and persisted, because the frame that carries it rides the
    /// always-on global socket: it can arrive twice, and it can arrive again after a restart.
    /// Belling the same correction twice would read as two separate rating changes.</para>
    /// </summary>
    public bool RaiseMatchRated(string modId, string matchId, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(matchId)) return false;
        if (_config.NotifiedRatedMatchIds.Contains(matchId, StringComparer.OrdinalIgnoreCase))
            return false;
        _config.NotifiedRatedMatchIds.Add(matchId);
        if (_config.NotifiedRatedMatchIds.Count > 500)
            _config.NotifiedRatedMatchIds.RemoveRange(0, _config.NotifiedRatedMatchIds.Count - 500);

        return Add(new NotificationItem
        {
            Kind = NotificationKind.MatchRated,
            ModId = modId ?? "",
            Title = title,
            Body = body,
            TargetId = matchId,
        });
    }

    /// <summary>
    /// "New translation" for a mod. Deduped on a stable <paramref name="translationKey"/>
    /// (e.g. <c>id@version</c>) via <see cref="ModState.NotifiedTranslationKeys"/>.
    /// </summary>
    public bool RaiseNewTranslation(string modId, string translationKey, string translationId, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(translationKey)) return false;
        var state = _config.GetState(modId);
        if (state.NotifiedTranslationKeys.Contains(translationKey, StringComparer.OrdinalIgnoreCase))
            return false;
        state.NotifiedTranslationKeys.Add(translationKey);
        // Keep the dedup set from growing without bound on a busy translations repo.
        if (state.NotifiedTranslationKeys.Count > 200)
            state.NotifiedTranslationKeys.RemoveRange(0, state.NotifiedTranslationKeys.Count - 200);
        return Add(new NotificationItem
        {
            Kind = NotificationKind.NewTranslation,
            ModId = modId,
            Title = title,
            Body = body,
            TargetId = translationId,
        });
    }

    /// <summary>
    /// "Launcher update available" — a newer version of the launcher itself.
    /// Deduped on the release tag via <see cref="LauncherConfig.NotifiedLauncherTag"/>
    /// so a given version only bells once (the gold self-update pill is a separate
    /// surface). Not tied to a mod, so <see cref="NotificationItem.ModId"/> is empty.
    /// </summary>
    public bool RaiseLauncherUpdate(string tag, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        if (string.Equals(_config.NotifiedLauncherTag, tag, StringComparison.OrdinalIgnoreCase))
            return false;
        _config.NotifiedLauncherTag = tag;
        return Add(new NotificationItem
        {
            Kind = NotificationKind.LauncherUpdate,
            Title = title,
            Body = body,
            TargetId = tag,
        });
    }

    private bool? _lastConnectivityOffline;

    /// <summary>
    /// "Offline" / "Back online" — a connectivity transition. Deduped against the
    /// last connectivity state RAISED so a flaky network doesn't spam the bell (only
    /// an actual flip bells). The state is per-session (not persisted); the caller
    /// only invokes this on an actual <c>OfflineChanged</c> transition, never on the
    /// initial online state at startup.
    /// </summary>
    public bool RaiseConnectivity(bool offline, string title, string body)
    {
        if (_lastConnectivityOffline == offline) return false;
        _lastConnectivityOffline = offline;
        return Add(new NotificationItem
        {
            Kind = NotificationKind.Connectivity,
            Title = title,
            Body = body,
            TargetId = offline ? "offline" : "online",
        });
    }

    /// <summary>
    /// "New mod in the catalog" — a community mod id not seen before. Deduped via
    /// <see cref="LauncherConfig.NotifiedCatalogModIds"/>. Call
    /// <see cref="SeedCatalogBaseline"/> once before the first diff so the whole
    /// existing catalog doesn't bell on first launch.
    /// </summary>
    public bool RaiseNewMod(string modId, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(modId)) return false;
        if (_config.NotifiedCatalogModIds.Contains(modId, StringComparer.OrdinalIgnoreCase))
            return false;
        _config.NotifiedCatalogModIds.Add(modId);
        if (_config.NotifiedCatalogModIds.Count > 500)
            _config.NotifiedCatalogModIds.RemoveRange(0, _config.NotifiedCatalogModIds.Count - 500);
        return Add(new NotificationItem
        {
            Kind = NotificationKind.NewMod,
            ModId = modId,
            Title = title,
            Body = body,
            TargetId = modId,
        });
    }

    /// <summary>
    /// "New multiplayer room created" — any user opened a room. Deduped via
    /// <see cref="LauncherConfig.NotifiedRoomIds"/> so the same room id isn't
    /// re-announced across a restart. The caller filters to joinable rooms and
    /// seeds a silent baseline of existing rooms itself (in-memory), so this only
    /// fires for rooms created after the poll started.
    /// </summary>
    /// <summary>
    /// Records a room as already-notified (persistent, cross-restart dedup on
    /// <see cref="LauncherConfig.NotifiedRoomIds"/>) WITHOUT adding a bell item.
    /// Room notifications no longer live in the bell — the caller surfaces them as a
    /// Windows toast plus a dot on the MULTIPLAYER tab / Rooms subtab — so this only
    /// owns the dedup. Returns true the first time a room id is seen (caller should
    /// surface it), false if it was already notified.
    /// (The <see cref="NotificationKind.RoomCreated"/> enum member + its KindBrush /
    /// NavigateToNotification handling are kept only so room items persisted by older
    /// builds still render and click; no new room items are ever added.)
    /// </summary>
    public bool TryMarkRoomNotified(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId)) return false;
        if (_config.NotifiedRoomIds.Contains(roomId, StringComparer.OrdinalIgnoreCase))
            return false;
        _config.NotifiedRoomIds.Add(roomId);
        if (_config.NotifiedRoomIds.Count > 500)
            _config.NotifiedRoomIds.RemoveRange(0, _config.NotifiedRoomIds.Count - 500);
        _config.Save();   // previously saved via Add→Persist; now saved here directly
        return true;
    }

    /// <summary>
    /// One-time SILENT baseline of the catalog "new mod" dedup set — records every
    /// currently-known mod id as already-notified WITHOUT belling, so the first-ever
    /// catalog fetch doesn't flood the bell with every existing mod. No-op after the
    /// first call (guarded by <see cref="LauncherConfig.CatalogBaselineSeeded"/>).
    /// Returns true if it seeded this call.
    /// </summary>
    /// <summary>
    /// News from the project itself. Not tied to a mod, like the launcher-update item.
    ///
    /// <para><c>TargetId</c> carries the URL rather than an id, because that is what clicking it
    /// has to do — an announcement has nowhere in the launcher to navigate to. An empty one falls
    /// back to the Discord at the click site, so a published announcement always leads somewhere.</para>
    ///
    /// <para>Deduped on the author's own id. Call <see cref="SeedAnnouncementBaseline"/> once
    /// before the first diff, or every announcement ever published arrives at once.</para>
    /// </summary>
    public bool RaiseAnnouncement(string id, string title, string body, string url)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return false;
        if (_config.NotifiedAnnouncementIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            return false;
        _config.NotifiedAnnouncementIds.Add(id);
        if (_config.NotifiedAnnouncementIds.Count > 500)
            _config.NotifiedAnnouncementIds.RemoveRange(0, _config.NotifiedAnnouncementIds.Count - 500);
        return Add(new NotificationItem
        {
            Kind = NotificationKind.Announcement,
            Title = title,
            Body = body,
            TargetId = url ?? "",
        });
    }

    /// <summary>
    /// Silently record everything published so far, once. Returns true when it did the seeding,
    /// so the caller skips its diff on that pass.
    ///
    /// <para>Without it, the first launcher to read the feed bells the entire back catalogue of
    /// announcements at once — which is exactly what a notification system must never do on the
    /// day somebody installs it.</para>
    /// </summary>
    public bool SeedAnnouncementBaseline(IEnumerable<string> ids)
    {
        if (_config.AnnouncementBaselineSeeded) return false;
        foreach (var id in ids)
            if (!string.IsNullOrWhiteSpace(id)
                && !_config.NotifiedAnnouncementIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                _config.NotifiedAnnouncementIds.Add(id);
        _config.AnnouncementBaselineSeeded = true;
        Persist();
        return true;
    }

    /// <summary>
    /// "A mod you do not have published a patch." Deduped on (mod, version) via
    /// <see cref="LauncherConfig.NotifiedCatalogVersions"/>. Returns true if an item was added.
    /// </summary>
    /// <param name="record">
    /// When false the version is recorded WITHOUT belling. That is how installed mods keep
    /// their entry current: they bell through <see cref="RaiseUpdateAvailable"/> instead, and
    /// leaving their entry stale would fire a patch notice the moment they were uninstalled.
    /// </param>
    public bool RaiseModPatch(
        string modId, string version, string title, string body, bool record = true)
    {
        if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(version)) return false;

        _config.NotifiedCatalogVersions.TryGetValue(modId, out var seen);
        if (string.Equals(seen, version, StringComparison.OrdinalIgnoreCase)) return false;

        _config.NotifiedCatalogVersions[modId] = version;
        if (!record)
        {
            Persist();
            return false;
        }

        return Add(new NotificationItem
        {
            Kind = NotificationKind.ModPatchPublished,
            ModId = modId,
            Title = title,
            Body = body,
        });
    }

    /// <summary>
    /// Silently record every catalog mod's current version, once.
    ///
    /// <para>Same non-negotiable rule as <see cref="SeedAnnouncementBaseline"/> and
    /// <see cref="SeedCatalogBaseline"/>: without it, the first launcher to read the feed
    /// bells a patch notice for the whole catalog at once.</para>
    /// </summary>
    /// <returns>True when it did the seeding, so the caller skips its diff on that pass.</returns>
    public bool SeedCatalogVersionBaseline(IEnumerable<KeyValuePair<string, string>> versions)
    {
        if (_config.CatalogVersionBaselineSeeded) return false;
        foreach (var pair in versions)
            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                _config.NotifiedCatalogVersions[pair.Key] = pair.Value;
        _config.CatalogVersionBaselineSeeded = true;
        Persist();
        return true;
    }

    public bool SeedCatalogBaseline(IEnumerable<string> modIds)
    {
        if (_config.CatalogBaselineSeeded) return false;
        foreach (var id in modIds)
            if (!string.IsNullOrWhiteSpace(id)
                && !_config.NotifiedCatalogModIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                _config.NotifiedCatalogModIds.Add(id);
        _config.CatalogBaselineSeeded = true;
        Persist();
        return true;
    }

    // --------------------------------------------------------------- mutators

    /// <summary>Marks every item read (badge → 0).</summary>
    public void MarkAllRead()
    {
        bool any = false;
        foreach (var i in Items)
            if (!i.Read) { i.Read = true; any = true; }
        if (any) { Persist(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>Marks a single item read.</summary>
    public void MarkRead(NotificationItem item)
    {
        if (item == null || item.Read) return;
        item.Read = true;
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears the whole history.</summary>
    public void Clear()
    {
        if (Items.Count == 0) return;
        Items.Clear();
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // --------------------------------------------------------------- internals

    private bool Add(NotificationItem item)
    {
        Items.Insert(0, item);
        // Trim oldest beyond the cap.
        while (Items.Count > MaxItems)
            Items.RemoveAt(Items.Count - 1);
        Persist();
        ItemAdded?.Invoke(this, item);
        Changed?.Invoke(this, EventArgs.Empty);
        ToastRequested?.Invoke(item.Title, item.Body);
        return true;
    }

    private void RemoveWhere(Func<NotificationItem, bool> pred)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
            if (pred(Items[i])) Items.RemoveAt(i);
    }

    private void Persist()
    {
        _config.Notifications = Items.ToList();
        try { _persist(); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"NotificationCenter save failed: {ex.Message}");
        }
    }
}
