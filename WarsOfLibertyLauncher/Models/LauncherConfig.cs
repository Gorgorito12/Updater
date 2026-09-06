using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using WarsOfLibertyLauncher.Services;

namespace WarsOfLibertyLauncher.Models;

/// <summary>
/// Multiplayer-specific persistent state. Lives nested under
/// <see cref="LauncherConfig.Multiplayer"/> so the JSON layout stays
/// flat at the top level even as the multiplayer feature grows.
/// </summary>
public class MultiplayerConfig
{
    /// <summary>
    /// Base URL of the lobby backend. The default points at the
    /// maintainer's self-hosted Node.js + Fastify deployment on an
    /// Oracle Cloud VM, fronted by DuckDNS + Let's Encrypt. Every
    /// fresh install hits this URL until the user explicitly
    /// overrides it in Settings. Power users can point at their
    /// own deployment by editing this field. Configs written by
    /// older launchers (which defaulted to the now-retired
    /// Cloudflare Worker URL) are auto-healed by
    /// <see cref="MigrateLobbyBaseUrl"/> on next load.
    /// </summary>
    [JsonPropertyName("lobbyBaseUrl")]
    public string LobbyBaseUrl { get; set; } = "https://wol-lobby.duckdns.org";

    /// <summary>
    /// Session JWT issued by the backend after a successful Discord
    /// sign-in. Empty when the user is not signed in (the Multiplayer
    /// tab will prompt them on first visit). Treat this like a
    /// password — it's a bearer credential.
    /// </summary>
    [JsonPropertyName("sessionToken")]
    public string SessionToken { get; set; } = "";

    /// <summary>
    /// Unix seconds when the <see cref="SessionToken"/> stops being
    /// accepted by the backend. The launcher refreshes silently when the
    /// remaining lifetime drops below 24 h.
    /// </summary>
    [JsonPropertyName("sessionExpiresAt")]
    public long SessionExpiresAt { get; set; }

    /// <summary>
    /// Cached profile of the signed-in user — saves a /me round trip on
    /// every launcher start. Refreshed whenever the user signs in or
    /// when /me is called for any other reason.
    /// </summary>
    [JsonPropertyName("cachedUser")]
    public Multiplayer.LobbyUserSummary? CachedUser { get; set; }

    // (The previous RadminBannerDismissed flag was removed when the
    //  banner became reactive — colour + content change with state and
    //  a dismiss button no longer made sense. Old JSON configs with
    //  "radminBannerDismissed":true deserialise harmlessly: the
    //  unknown key is dropped on the next save.)
}

/// <summary>
/// One INACTIVE installation of a mod (the ACTIVE one lives in the flat fields
/// of <see cref="ModState"/>). A mod may have several copies in different
/// folders; the active copy plus this list make up the full set. Carries the
/// per-INSTALL state — per-MOD state (latest-version cache, notification dedup)
/// stays on <see cref="ModState"/>.
/// </summary>
public class ModInstall
{
    /// <summary>Stable id — the switch key and the per-install productGuid seed.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>User-facing label ("Principal", "Prueba"…). Empty => derive from the folder name.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("installPath")]
    public string InstallPath { get; set; } = "";

    [JsonPropertyName("lastKnownVersion")]
    public string LastKnownVersion { get; set; } = "";

    [JsonPropertyName("pinnedVersion")]
    public string PinnedVersion { get; set; } = "";

    [JsonPropertyName("activeTranslationId")]
    public string ActiveTranslationId { get; set; } = "";

    [JsonPropertyName("activeTranslationVersion")]
    public string ActiveTranslationVersion { get; set; } = "";
}

/// <summary>
/// Per-mod state that has to survive launcher restarts AND has to be kept
/// separate per profile. Stored under <see cref="LauncherConfig.Mods"/>
/// keyed by mod id so switching between mods doesn't cross-contaminate
/// (e.g. so detecting Improvement Mod's install path doesn't overwrite
/// the Wars of Liberty install path the user already had cached).
/// </summary>
public class ModState
{
    /// <summary>
    /// Where this mod is installed on disk. Empty when the launcher hasn't
    /// found it yet — the next call to the install detector will populate it.
    /// </summary>
    [JsonPropertyName("installPath")]
    public string InstallPath { get; set; } = "";

    /// <summary>
    /// The mod's folder under <c>Documents\My Games\</c>, once the launcher has
    /// worked it out for a mod whose catalog manifest declares no
    /// <c>userDataFolder</c> (discovered by name, learned from a game launch, or
    /// picked by the user).
    ///
    /// Remembered rather than re-derived on every read so the answer is stable:
    /// discovery depends on what is on disk at the time, and a folder that
    /// momentarily fails to match must not silently move a mod's settings
    /// somewhere else. A declared manifest value always wins over this — see
    /// <c>UserDataService.ResolveFolderName</c>.
    /// </summary>
    [JsonPropertyName("userDataFolder")]
    public string UserDataFolder { get; set; } = "";

    /// <summary>
    /// ID of the community translation pack currently applied for this mod
    /// (e.g. "es", "fr"). Empty means the canonical English data is active.
    /// </summary>
    [JsonPropertyName("activeTranslationId")]
    public string ActiveTranslationId { get; set; } = "";

    /// <summary>
    /// Which VERSION of the active translation (<see cref="ActiveTranslationId"/>)
    /// is applied, for folder packs that keep a version history. Empty for
    /// single-version packs or English. Lets the Language tab's version picker
    /// pre-select and mark the applied version.
    /// </summary>
    [JsonPropertyName("activeTranslationVersion")]
    public string ActiveTranslationVersion { get; set; } = "";

    /// <summary>
    /// Ids of the community addons currently applied to this install (the
    /// transparent-UI overlay, gun-smoke effects, and so on).
    ///
    /// Lives beside <see cref="ActiveTranslationId"/> because it is the same
    /// kind of state: a user choice that modifies files inside ONE install and
    /// has to be re-applied after an update or a repair re-lays the overlay.
    /// The authoritative record of WHICH files each addon owns is the install
    /// manifest's <c>addonFiles</c>, not this list — this only answers "what
    /// should be on".
    /// </summary>
    [JsonPropertyName("enabledAddons")]
    public List<string> EnabledAddons { get; set; } = new();

    /// <summary>
    /// Last mod version we detected, stored so the UI can show "Installed"
    /// with the right version number immediately after the user switches to
    /// this mod, without waiting for the async CheckAsync MD5-and-XML pass
    /// to complete. CheckAsync overwrites it with the freshly-computed value
    /// when it finishes. Empty means we have never detected a version for
    /// this profile (e.g. brand-new install, or mod whose UpdateMechanism
    /// isn't WolPatcher and so doesn't compute versions at all).
    /// </summary>
    [JsonPropertyName("lastKnownVersion")]
    public string LastKnownVersion { get; set; } = "";

    /// <summary>
    /// When this mod's game was last LAUNCHED (UTC), or null when it has never
    /// been played. Stamped at both launch sites — the dashboard PLAY button and
    /// the multiplayer in-lobby launch (which runs the ROOM's mod, not necessarily
    /// the displayed one). Drives the "most recently played first" ordering and the
    /// "Played 2 h ago" hint in the MODS switcher.
    ///
    /// Nullable ON PURPOSE: "never played" has to be distinguishable from a zero
    /// date so the switcher can say "Not played yet" instead of an absurd age.
    /// Absent in configs written before this field existed → null → same thing.
    ///
    /// It does NOT decide which mod the launcher opens on — that stays
    /// <see cref="LauncherConfig.ActiveModId"/> (the mod you last had on screen),
    /// so a multiplayer match in someone else's mod can't move your dashboard.
    /// </summary>
    [JsonPropertyName("lastPlayedUtc")]
    public DateTime? LastPlayedUtc { get; set; }

    /// <summary>
    /// Last "latest version" we got from the mod's update server, cached so
    /// the "Latest version" row in the status card has a value to show
    /// immediately after a mod switch instead of waiting for the async
    /// CheckAsync HTTP fetch to complete. Empty until the first successful
    /// CheckAsync (or for non-WolPatcher mods that don't fetch a manifest).
    /// </summary>
    [JsonPropertyName("lastKnownLatestVersion")]
    public string LastKnownLatestVersion { get; set; } = "";

    /// <summary>
    /// When true, this mod shares its graphics / sound / hotkey settings with every other mod
    /// that has this on: playing any of them updates the shared copy, and launching any of them
    /// applies it (<see cref="Services.GameSettingsStore"/>).
    ///
    /// <para><b>Per MOD, not per install, and off by default.</b> Being in the group means
    /// launching the mod rewrites part of its profile, which would silently undo a deliberate
    /// per-mod choice — someone who lowered the graphics for a heavy mod would find them raised
    /// again after playing another. That is precisely why the decision lives on the mod's own
    /// settings page instead of a launcher-wide switch: the blast radius is visible from where
    /// you turn it on. A mod without this is never read from or written to by the sync; only the
    /// explicit "import settings from…" button touches it, and only when pressed.</para>
    /// </summary>
    [JsonPropertyName("syncGameSettings")]
    public bool SyncGameSettings { get; set; } = false;

    /// <summary>
    /// The value of <c>optionrecordgame</c> the launcher last wrote into THIS mod's profile, or
    /// <b>null</b> when it has never written one.
    ///
    /// <para><b>Per mod because the profile is</b> — each mod keeps its own
    /// <c>My Games\&lt;mod&gt;\Users3\&lt;profile&gt;.xml</c>, so a single launcher-wide "already
    /// seeded" marker would enable recording for whichever mod happened to be launched first and
    /// leave every other one alone, forever.</para>
    ///
    /// <para><b>It records what we wrote, not merely that we wrote</b>, and that is what makes the
    /// opt-out final. When this equals the user's current preference there is nothing to do —
    /// including when the game itself has changed the setting since, which is the player's
    /// business, not ours. Only a change to the preference makes the launcher touch the profile
    /// again. See <see cref="Services.GameSettingsStore.PlanGameRecording"/>.</para>
    /// </summary>
    [JsonPropertyName("gameRecordingApplied")]
    public bool? GameRecordingApplied { get; set; }

    /// <summary>
    /// A mod the player chose, while installing THIS one, to copy graphics, sound and hotkeys
    /// from — kept until the copy actually happens. Empty means there is nothing owed.
    ///
    /// <para><b>It exists because the copy cannot be made when the choice is made.</b> The
    /// settings live in <c>My Games\&lt;mod&gt;\Users3\&lt;profile&gt;.xml</c>, and Age of Empires III
    /// writes that file on its FIRST run — so at the end of an install there is usually nothing
    /// to write into, and nothing may fabricate one (see
    /// <see cref="Services.GameSettingsSync"/>, which refuses to invent profile structure). The
    /// choice is therefore recorded and applied at the next launch that finds a profile.</para>
    ///
    /// <para><b>Per mod, and cleared on any outcome except "not yet"</b> — see
    /// <see cref="Services.GameSettingsStore.KeepPending"/>. A reinstall of a mod that was played
    /// before never lands here at all: its profile already exists and the copy happens during the
    /// install.</para>
    /// </summary>
    [JsonPropertyName("pendingSettingsImportFrom")]
    public string PendingSettingsImportFrom { get; set; } = "";

    /// <summary>
    /// Whether the last competitive match of this mod produced no recording at all.
    /// <c>null</c> = nothing conclusive yet.
    ///
    /// <para><b>What it is for.</b> The launcher cannot tick AoE3's per-match Record Game box and
    /// cannot see whether the player did, so the confirmation before a competitive start is a
    /// nudge rather than a guarantee — and one that reads identically every time stops being
    /// read. This is the evidence that lets the NEXT one lead with a fact instead: "the last match
    /// wasn't recorded". See <see cref="Services.Multiplayer.RecordingMemory"/>, which owns the
    /// rules, including why a recording that exists but never finished writing its ending counts
    /// as recorded.</para>
    ///
    /// <para><b>Per mod, like its sibling above</b>, because <c>optionrecordgame</c> is per mod
    /// profile: that Wars of Liberty failed to record says nothing about Improvement Mod.</para>
    /// </summary>
    [JsonPropertyName("lastMatchHadNoRecording")]
    public bool? LastMatchHadNoRecording { get; set; }

    /// <summary>
    /// ETag of the last 200 response from <c>/releases/latest</c> for this mod
    /// (follow-latest GitHubReleases mods only). Sent as <c>If-None-Match</c> so
    /// an unchanged latest release is a free 304 (conditional requests don't
    /// count against GitHub's unauthenticated 60/h rate limit). Kept on a
    /// transient failure; an indivisible pair with
    /// <see cref="LastKnownLatestVersion"/> — only sent when the cached tag is
    /// non-empty, because a 304 carries no body and would leave us tagless.
    /// </summary>
    [JsonPropertyName("latestReleaseETag")]
    public string LatestReleaseETag { get; set; } = "";

    /// <summary>
    /// The <c>owner/repo</c> that <see cref="LastKnownLatestVersion"/> and
    /// <see cref="LatestReleaseETag"/> were cached FROM. Load-bearing when the
    /// catalog migrates a mod to a different repository: the old ETag still
    /// matches the OLD repo, so re-sending it there yields a 304 and the launcher
    /// silently keeps serving the old repo's tag — the user simply never sees the
    /// new version, with no error anywhere. On a mismatch the pair is discarded
    /// and the tag is resolved fresh (same rule
    /// <c>ModCatalogService.LoadFromCache</c> applies to the catalog cache).
    /// Empty on configs written before this field existed, which reads as
    /// "doesn't match" — exactly the safe direction.
    /// </summary>
    [JsonPropertyName("latestReleaseRepo")]
    public string LatestReleaseRepo { get; set; } = "";

    /// <summary>
    /// Version the user explicitly chose to STAY ON for this mod. Empty (the
    /// default) means "follow the latest" — the normal behaviour. When it equals
    /// the installed version, the launcher PAUSES update prompts for this mod: the
    /// PLAY button stays "Play" instead of flipping to "Update" and the secondary
    /// Update button is hidden, so the user can keep playing this version without
    /// being pushed to upgrade. It only suppresses the PROMPT — nothing is ever
    /// auto-updated. The pin goes stale (and stops suppressing) once the installed
    /// version no longer matches it, e.g. after a manual update; the user clears it
    /// from Mod Properties to resume updates.
    /// </summary>
    [JsonPropertyName("pinnedVersion")]
    public string PinnedVersion { get; set; } = "";

    /// <summary>
    /// Latest "available version" for which the notification bell has ALREADY
    /// raised an "update available" item. Dedup key for the notification center:
    /// we only bell a given (mod, latest-version) pair once, even after the
    /// visible notification list rolls past its 50-item cap. Empty until the
    /// first "update available" notification for this mod.
    /// </summary>
    [JsonPropertyName("notifiedUpdateVersion")]
    public string NotifiedUpdateVersion { get; set; } = "";

    /// <summary>
    /// Installed version for which the bell has ALREADY raised (or baselined) an
    /// "update finished" item. Startup reconciliation compares the freshly-detected
    /// installed version against this: empty → seed a silent baseline (no bell);
    /// a newer value → the mod was updated (possibly by an elevated/other-profile
    /// process that couldn't write THIS user's bell), so raise "update finished"
    /// here in the user's own session. Idempotent with the direct raise in
    /// <c>ApplyAsync</c> (that dedups on the visible list).
    /// </summary>
    [JsonPropertyName("notifiedInstalledVersion")]
    public string NotifiedInstalledVersion { get; set; } = "";

    /// <summary>
    /// Translation entries (keyed <c>id@version</c>) for which the notification
    /// bell has already raised a "new translation" item. Dedup set so a freshly
    /// published translation only bells once per mod, surviving the 50-item cap
    /// of the visible notification list.
    /// </summary>
    [JsonPropertyName("notifiedTranslationKeys")]
    public List<string> NotifiedTranslationKeys { get; set; } = new();

    // ---- Multi-install support ----
    // The flat fields above ARE the ACTIVE install. INACTIVE copies of the same
    // mod (a second folder, a test copy, a different version) live in
    // <see cref="OtherInstalls"/>. Switching the active install swaps an entry
    // of OtherInstalls with the flat fields (see SnapshotActive/AdoptInstall).
    // An empty OtherInstalls == the legacy single-install shape, so every
    // existing reader and old build keeps working with ZERO migration. The
    // stock game never participates (stripped in NormalizeInstalls).

    /// <summary>
    /// Stable id of the ACTIVE install. Empty on a legacy single-install config
    /// (the flat fields are simply "the install"); assigned a GUID once the mod
    /// gains a second copy, so the active one can be referenced after it later
    /// rotates into <see cref="OtherInstalls"/>.
    /// </summary>
    [JsonPropertyName("activeInstallId")]
    public string ActiveInstallId { get; set; } = "";

    /// <summary>User-facing label of the active install ("Principal", "Prueba"…).
    /// Empty => the UI derives one from the folder name.</summary>
    [JsonPropertyName("activeInstallLabel")]
    public string ActiveInstallLabel { get; set; } = "";

    /// <summary>
    /// The mod's INACTIVE installs (the active one is the flat fields above).
    /// Empty for single-install users — round-trips and readers behave exactly
    /// as before. Populated by "install another copy" / adopt.
    /// </summary>
    [JsonPropertyName("otherInstalls")]
    public List<ModInstall> OtherInstalls { get; set; } = new();

    /// <summary>True when this mod has more than one registered install.</summary>
    [JsonIgnore]
    public bool HasMultipleInstalls => OtherInstalls.Count > 0;

    /// <summary>
    /// Snapshot the ACTIVE install (the flat fields) as a <see cref="ModInstall"/>,
    /// used when rotating it into <see cref="OtherInstalls"/> on a switch. Mints a
    /// stable id if the active install doesn't have one yet.
    /// </summary>
    public ModInstall SnapshotActive() => new()
    {
        Id = string.IsNullOrEmpty(ActiveInstallId) ? Guid.NewGuid().ToString("N") : ActiveInstallId,
        Label = ActiveInstallLabel,
        InstallPath = InstallPath,
        LastKnownVersion = LastKnownVersion,
        PinnedVersion = PinnedVersion,
        ActiveTranslationId = ActiveTranslationId,
        ActiveTranslationVersion = ActiveTranslationVersion,
    };

    /// <summary>
    /// Copy a stored install INTO the flat fields (make it the active one).
    /// Per-mod fields (<see cref="LastKnownLatestVersion"/>, notification dedup)
    /// are left untouched — they are not per-install.
    /// </summary>
    public void AdoptInstall(ModInstall slot)
    {
        ActiveInstallId = slot.Id;
        ActiveInstallLabel = slot.Label;
        InstallPath = slot.InstallPath;
        LastKnownVersion = slot.LastKnownVersion;
        PinnedVersion = slot.PinnedVersion;
        ActiveTranslationId = slot.ActiveTranslationId;
        ActiveTranslationVersion = slot.ActiveTranslationVersion;
    }

    /// <summary>
    /// Forget the ACTIVE install after it has been uninstalled — the exact mirror of
    /// <see cref="AdoptInstall"/>, and deliberately a method rather than a run of
    /// assignments in an event handler, so the cleared/survives split is one reviewable
    /// list with one reason attached.
    ///
    /// <para>⚠ <b>Only for the branch that leaves the mod with NO install.</b> When an
    /// uninstall promotes a remaining copy it calls <see cref="AdoptInstall"/>, which
    /// ASSIGNS the version and the pin from that copy — clearing afterwards would wipe a
    /// live install's state.</para>
    ///
    /// <para><b>Cleared</b>, because each describes a folder that no longer exists.
    /// <see cref="LastKnownVersion"/> is the one that was reported: nothing had ever
    /// cleared it anywhere in the codebase, so a mod that was later mis-detected in
    /// somebody else's folder painted a version chip and an Update button out of a
    /// version it no longer had. <see cref="PinnedVersion"/> is worse if kept — a pin
    /// that outlives its install is a SILENT update block on the next reinstall, with
    /// nothing on screen to explain it. The two notification keys go so a reinstall
    /// re-seeds its baseline instead of suppressing the first bell.</para>
    ///
    /// <para><b>Survives:</b> <see cref="OtherInstalls"/> (other folders, still there),
    /// <see cref="LastLaunchedUtc"/> (play history is not invalidated by an uninstall),
    /// and — the one worth stating — the triple
    /// <see cref="LastKnownLatestVersion"/> / <see cref="LatestReleaseETag"/> /
    /// <see cref="LatestReleaseRepo"/>. Those describe what is available UPSTREAM, which
    /// is still true with nothing installed, and they are an indivisible unit: the ETag
    /// is only ever sent alongside its cached tag and its repo, so clearing one of the
    /// three re-opens the tagless-304 and wrong-repo bugs each was added to close.</para>
    /// </summary>
    public void ClearInstallState()
    {
        ActiveInstallId = "";
        ActiveInstallLabel = "";
        InstallPath = "";
        LastKnownVersion = "";
        PinnedVersion = "";
        ActiveTranslationId = "";
        ActiveTranslationVersion = "";
        NotifiedInstalledVersion = "";
        NotifiedUpdateVersion = "";
    }

    /// <summary>Every registered install path for this mod (active + others),
    /// non-empty only. Used by the sibling-exclusion list so a new clone of one
    /// copy never scoops up another.</summary>
    public IEnumerable<string> AllInstallPaths()
    {
        if (!string.IsNullOrEmpty(InstallPath)) yield return InstallPath;
        foreach (var o in OtherInstalls)
            if (!string.IsNullOrEmpty(o.InstallPath)) yield return o.InstallPath;
    }

    /// <summary>
    /// Forget a single registered copy by id (the switcher's "remove" action). Only
    /// drops the registration — it does NOT touch files on disk. Returns true if an
    /// entry was removed.
    /// </summary>
    public bool RemoveInstall(string id)
        => !string.IsNullOrEmpty(id) && OtherInstalls.RemoveAll(i => i.Id == id) > 0;

    /// <summary>
    /// Register an EXISTING install folder as an inactive copy (the "add existing folder"
    /// action) — adopts a real install already on disk WITHOUT reinstalling. No-op (returns
    /// false) when the path is empty or already registered (active or another copy), so it
    /// can't create a duplicate. Returns true when a new copy was added.
    /// </summary>
    public bool RegisterInstall(string path, string label = "")
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        foreach (var p in AllInstallPaths())
            if (PathEquals(p, path)) return false;
        OtherInstalls.Add(new ModInstall
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = (label ?? "").Trim(),
            InstallPath = path,
        });
        return true;
    }

    /// <summary>
    /// Rename a registered install (the "edit label" action). Targets the active install
    /// when <paramref name="id"/> matches <see cref="ActiveInstallId"/>, else the matching
    /// <see cref="OtherInstalls"/> entry. An empty label reverts to the folder-derived
    /// display name. Returns true if something was renamed.
    /// </summary>
    public bool RenameInstall(string id, string label)
    {
        if (string.IsNullOrEmpty(id)) return false;
        label = (label ?? "").Trim();
        if (id == ActiveInstallId) { ActiveInstallLabel = label; return true; }
        var slot = OtherInstalls.Find(i => i.Id == id);
        if (slot == null) return false;
        slot.Label = label;
        return true;
    }

    /// <summary>
    /// Case-insensitive, full-path-normalized comparison of two install paths, so
    /// <c>bin\..\</c>, trailing slashes, and casing don't defeat dedup. Falls back to
    /// a trimmed ordinal compare when a path can't be fully qualified.
    /// </summary>
    public static bool PathEquals(string? a, string? b)
        => string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        try { p = Path.GetFullPath(p); } catch { /* keep raw on a malformed path */ }
        return p.TrimEnd('\\', '/');
    }

    /// <summary>
    /// Idempotent post-load normalization. A NO-OP for single-install configs
    /// (the common case, <see cref="OtherInstalls"/> empty): it only assigns a
    /// stable <see cref="ActiveInstallId"/> when the mod actually has extra
    /// copies but the active one lost its id (e.g. a hand-edited config). When
    /// <paramref name="isStock"/>, strips any multi-install state entirely — the
    /// detect-only base game must never carry copies.
    /// </summary>
    public void NormalizeInstalls(bool isStock)
    {
        if (isStock)
        {
            OtherInstalls.Clear();
            ActiveInstallId = "";
            ActiveInstallLabel = "";
            return;
        }

        // Drop empty entries and any copy whose path duplicates the active install or
        // an earlier-kept copy — a stale re-point / double registration would otherwise
        // surface a phantom duplicate in the switcher. Pure path compare, no disk I/O
        // (non-existent folders are filtered at render time + removable by hand).
        if (OtherInstalls.Count > 0)
        {
            var kept = new List<ModInstall>();
            foreach (var o in OtherInstalls)
            {
                if (string.IsNullOrWhiteSpace(o.InstallPath)) continue;
                if (PathEquals(o.InstallPath, InstallPath)) continue;
                if (kept.Any(k => PathEquals(k.InstallPath, o.InstallPath))) continue;
                kept.Add(o);
            }
            if (kept.Count != OtherInstalls.Count) OtherInstalls = kept;
        }

        if (OtherInstalls.Count > 0 && string.IsNullOrEmpty(ActiveInstallId))
            ActiveInstallId = Guid.NewGuid().ToString("N");
    }
}

/// <summary>
/// Kind of a <see cref="NotificationItem"/> shown in the bell panel. Serialized
/// as a string so adding a value later doesn't shift existing JSON, and so a
/// config written by a newer launcher degrades gracefully on an older one.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationKind
{
    /// <summary>A newer version of a mod is available to download.</summary>
    UpdateAvailable,
    /// <summary>A mod update finished applying successfully.</summary>
    UpdateFinished,
    /// <summary>A new community translation was published for a mod.</summary>
    NewTranslation,
    /// <summary>A newer version of the LAUNCHER itself is available.</summary>
    LauncherUpdate,
    /// <summary>Connectivity changed — went offline, or came back online.</summary>
    Connectivity,
    /// <summary>A new community mod appeared in the Workshop catalog.</summary>
    NewMod,
    /// <summary>A fresh install (or a new copy) of a mod finished — distinct from an update.</summary>
    Installed,
    /// <summary>Any user created a new multiplayer room (for a mod you have installed).</summary>
    RoomCreated,
    /// <summary>
    /// A match that had gone down without a result was later decided and rated, from a
    /// recording one of the two players read after the report had already gone out.
    ///
    /// <para>It needs its own kind because it arrives with the room long closed: there is no
    /// lobby window left to write into, and without a bell the correction would only ever be
    /// discovered by someone who happened to open their History.</para>
    /// </summary>
    MatchRated,

    /// <summary>
    /// News from the project itself, published through the notification feed.
    ///
    /// <para>Mod-less, like <see cref="LauncherUpdate"/> and <see cref="Connectivity"/>. It
    /// exists so announcements reach people instead of waiting for them to remember to go
    /// looking: the bell carries the notice, and clicking it opens where the conversation is.</para>
    /// </summary>
    Announcement,

    /// <summary>
    /// A mod in the catalog published a new version — and this launcher does NOT have that
    /// mod installed.
    ///
    /// <para><b>Why it is not <see cref="UpdateAvailable"/>.</b> That kind's wording says a
    /// version "is available to download" for something you have, and clicking it takes you
    /// to the update flow. Neither is true here: there is nothing installed to update. This
    /// one says a mod you might care about shipped a patch, and clicking it opens that mod in
    /// the Workshop. Sharing the kind would have made the bell lie about half its items.</para>
    ///
    /// <para>Deduped by <see cref="LauncherConfig.NotifiedCatalogVersions"/>, which is a
    /// separate latch from <see cref="ModState.NotifiedUpdateVersion"/> so a mod that gets
    /// installed later cannot have its first real update swallowed by a patch notice.</para>
    /// </summary>
    ModPatchPublished,
}

/// <summary>
/// One entry in the Steam-style notification bell. Persisted in
/// <see cref="LauncherConfig.Notifications"/> so the history survives launcher
/// restarts until the user clears it. Created by <see cref="Services.NotificationCenter"/>.
/// </summary>
public class NotificationItem : INotifyPropertyChanged
{
    /// <summary>Stable id (GUID string) — used as the list key and for removal.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("kind")]
    public NotificationKind Kind { get; set; }

    /// <summary>Mod profile id this notification is about (drives click navigation).</summary>
    [JsonPropertyName("modId")]
    public string ModId { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    /// <summary>UTC timestamp the notification was raised (for "hace X" labels + ordering).</summary>
    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the user has seen this item. Drives the per-row unread dot via a
    /// WPF DataTrigger, so the setter MUST raise <see cref="PropertyChanged"/> —
    /// without it the dot never hides when <c>MarkAllRead</c> flips the flag.
    /// </summary>
    private bool _read;
    [JsonPropertyName("read")]
    public bool Read
    {
        get => _read;
        set { if (_read != value) { _read = value; OnPropertyChanged(); } }
    }

    /// <summary>Local-time projection of <see cref="CreatedAtUtc"/> for display binding.</summary>
    [JsonIgnore]
    public DateTime CreatedLocal => CreatedAtUtc.ToLocalTime();

    /// <summary>
    /// Optional navigation payload (e.g. a translation id for
    /// <see cref="NotificationKind.NewTranslation"/>). Null/empty for kinds that
    /// only need <see cref="ModId"/>.
    /// </summary>
    [JsonPropertyName("targetId")]
    public string? TargetId { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Local launcher config. Most defaults match the official servers; the install
/// path is normally auto-detected from the Windows registry on first run.
/// </summary>
public class LauncherConfig
{
    /// <summary>
    /// ID of the mod profile the launcher last had selected (e.g. "wol",
    /// "improvement-mod"). Empty on a fresh config — the launcher resolves
    /// it to the registry's default profile at startup. Set whenever the
    /// user picks a different mod in the header dropdown.
    /// </summary>
    [JsonPropertyName("activeModId")]
    public string ActiveModId { get; set; } = "";

    /// <summary>
    /// Resolves <see cref="ActiveModId"/> to its full profile, falling
    /// back to <see cref="ModRegistry.Default"/> when the id is empty or
    /// unknown (e.g. user hand-edited the config with a typo).
    /// </summary>
    public ModProfile GetActiveProfile() =>
        ModRegistry.Find(ActiveModId) ?? ModRegistry.Default;

    /// <summary>
    /// Per-mod state (install path, active translation, etc.) keyed by
    /// <see cref="ModProfile.Id"/>. Replaces the old shared root-level
    /// fields like <c>modInstallPath</c> and <c>activeTranslationId</c> so
    /// switching mods doesn't overwrite data belonging to another mod.
    /// Created lazily by <see cref="GetState(string)"/>.
    /// </summary>
    [JsonPropertyName("mods")]
    public Dictionary<string, ModState> Mods { get; set; } = new();

    /// <summary>
    /// Returns the persistent state record for a given mod id, creating an
    /// empty one if it doesn't exist yet. The returned reference is the
    /// live one stored in <see cref="Mods"/> — modifying its fields and
    /// then calling <see cref="Save"/> persists the change.
    /// </summary>
    public ModState GetState(string modId)
    {
        if (string.IsNullOrEmpty(modId)) modId = ModRegistry.Default.Id;
        if (!Mods.TryGetValue(modId, out var state))
        {
            state = new ModState();
            Mods[modId] = state;
        }
        return state;
    }

    /// <summary>Convenience overload: state of the currently active profile.</summary>
    public ModState GetActiveState() => GetState(GetActiveProfile().Id);

    /// <summary>
    /// IDs of mods the user has explicitly added to their personal
    /// collection from the Workshop. Drives the Dashboard's MODS
    /// popup, which lists only what the user has curated rather than
    /// the full catalog. Built-in profiles (WoL) are always treated
    /// as added regardless of this list (see <see cref="IsUserMod"/>),
    /// so a fresh install never has an empty MODS popup.
    ///
    /// Migration: on first launch with this field present but empty,
    /// MainWindow seeds it from the currently-installed mods so users
    /// upgrading from older configs don't lose their setup. After
    /// that, the list only changes via the Workshop's Add / Remove
    /// buttons.
    /// </summary>
    [JsonPropertyName("userModIds")]
    public List<string> UserModIds { get; set; } = new();

    /// <summary>
    /// Addon archives the user imported from a file, as opposed to ones the
    /// catalog offers. Launcher-wide on purpose: these overlay the stock AoE3
    /// files every mod clones, so one import is usable by every install. WHICH
    /// installs currently have it applied is <see cref="ModState.EnabledAddons"/>.
    ///
    /// Importing exists because the community pages these addons come from
    /// (AoE3 Heaven) hand out session-bound download links — verified: the same
    /// URL returns the generic listing page to any client but the browser that
    /// requested it. So the launcher cannot fetch them, and the only paths to
    /// disk are a re-hosted catalog copy or a file the user already has.
    /// </summary>
    [JsonPropertyName("importedAddons")]
    public List<ImportedAddon> ImportedAddons { get; set; } = new();

    /// <summary>
    /// Unlocks the Settings → DEVELOPER tab, which holds the author tools: trying a local
    /// <c>mod.json</c>, the translation packager and the delta-patch generator.
    ///
    /// <para>Off by default, and the tab is hidden entirely while it is — those tools each
    /// already told the reader "normal users can ignore this section", which is a sign they
    /// were costing every user a tab for nothing.</para>
    ///
    /// <para><b>It gates the TOOLS, never the content.</b> Local manifests stay merged into
    /// the catalog with this off: a test mod can be INSTALLED, and dropping it from the
    /// listing would orphan that install — no active mod to return to and no way to
    /// uninstall it from the UI. Turning developer mode off hides where you manage them,
    /// not what you already have.</para>
    ///
    /// <para>The switch that closes it lives INSIDE the block it governs, and the only way
    /// back in is the seven-tap gesture on the version line in the settings rail
    /// (<c>LauncherSettingsDialog.RailVersionText_MouseLeftButtonUp</c>). It used to be a
    /// visible row at the bottom of GENERAL, which advertised the tools to every player.</para>
    ///
    /// <para><b>This is not access control and cannot be.</b> It is a boolean in the
    /// player's own config file; anybody who knows it exists can set it in a text editor.
    /// What it gates is author tooling no server treats differently.</para>
    /// </summary>
    [JsonPropertyName("developerMode")]
    public bool DeveloperMode { get; set; } = false;

    /// <summary>
    /// Whether <see cref="DeveloperMode"/> has already been switched off once, for the
    /// people who had turned it on back when the switch sat in plain sight in GENERAL.
    ///
    /// <para>Hiding the block only hid it from players who had never opened it: a persisted
    /// <c>developerMode: true</c> kept the whole thing on screen for everybody else, which is
    /// most of the point of hiding it. So it is retired once, on load.</para>
    ///
    /// <para><b>Keyed off THIS marker and never off "DeveloperMode is true".</b> Read from
    /// the flag, the reset would run on every launch, and somebody who re-opened the block
    /// with the seven-tap gesture would find it closed again at the next start with nothing
    /// to explain it. That is the mirror of <see cref="BackgroundDefaultSeeded"/>'s
    /// invariant — there a default that refuses to stay off, here a setting that refuses to
    /// stay on — and both are the same bug: the launcher stops obeying an explicit choice.
    /// Pinned by <c>LauncherConfigMigrationTests</c>.</para>
    ///
    /// <para>Set even when the flag was ALREADY false, so the migration never looks again.
    /// That costs one config save on one launch.</para>
    /// </summary>
    [JsonPropertyName("developerModeRetired")]
    public bool DeveloperModeRetired { get; set; } = false;

    /// <summary>
    /// Absolute paths of local <c>mod.json</c> files the user added to try a manifest
    /// before publishing it (Workshop → "Add local mod"). Merged into the catalog listing
    /// by <see cref="Services.ModRegistry.SetLocalModPaths"/>.
    ///
    /// <para>Launcher-wide, and only PATHS are stored — the manifest is re-read on every
    /// merge, which is what makes editing the file and hitting Refresh show the change.
    /// Caching a copy here would defeat the entire point.</para>
    ///
    /// <para>A path that stops loading (deleted, or edited into invalid JSON) is skipped
    /// with a log line, never dropped automatically: the user is mid-edit exactly when
    /// that happens, and silently forgetting their entry would be worse than showing
    /// nothing for a moment.</para>
    /// </summary>
    [JsonPropertyName("localCatalogModPaths")]
    public List<string> LocalCatalogModPaths { get; set; } = new();

    /// <summary>
    /// True when the given mod id belongs to the user's collection
    /// (either explicitly added via Workshop or a built-in profile
    /// like WoL). Drives the Dashboard's MODS popup filter and the
    /// Workshop's per-row Add/Remove button state.
    /// </summary>
    public bool IsUserMod(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return false;
        if (ModRegistry.IsBuiltIn(modId)) return true;
        return UserModIds.Any(id => string.Equals(id, modId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adds the given mod id to <see cref="UserModIds"/> if not
    /// already present. No-op for built-in ids (those are always
    /// implicitly present via <see cref="IsUserMod"/>). Caller is
    /// responsible for invoking <see cref="Save"/> after batching the
    /// changes that should land on disk.
    /// </summary>
    public void AddUserMod(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return;
        if (ModRegistry.IsBuiltIn(modId)) return;
        if (!UserModIds.Any(id => string.Equals(id, modId, StringComparison.OrdinalIgnoreCase)))
            UserModIds.Add(modId);
    }

    /// <summary>
    /// Removes the given mod id from <see cref="UserModIds"/>. No-op
    /// for built-in ids. Caller is responsible for <see cref="Save"/>.
    /// </summary>
    public void RemoveUserMod(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return;
        if (ModRegistry.IsBuiltIn(modId)) return;
        UserModIds.RemoveAll(id => string.Equals(id, modId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Mod ids the user has starred via the right-click context
    /// menu (Add to Favorites). Favorites pin to the top of the
    /// Dashboard MODS popup so the user can switch between their
    /// most-played mods in one click. Distinct from
    /// <see cref="UserModIds"/> (which controls visibility);
    /// favorites only control ORDERING — a favorite mod must also
    /// be in UserModIds to appear at all.
    /// </summary>
    [JsonPropertyName("favoriteModIds")]
    public List<string> FavoriteModIds { get; set; } = new();

    public bool IsFavoriteMod(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return false;
        return FavoriteModIds.Any(id => string.Equals(id, modId, StringComparison.OrdinalIgnoreCase));
    }

    public void AddFavoriteMod(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return;
        if (!IsFavoriteMod(modId))
            FavoriteModIds.Add(modId);
    }

    public void RemoveFavoriteMod(string? modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return;
        FavoriteModIds.RemoveAll(id => string.Equals(id, modId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns every non-empty install path currently registered for a
    /// mod profile OTHER than <paramref name="excludeModId"/>. Used by
    /// the install pipeline as the canonical "sibling-mod exclusion
    /// list" so a fresh install of mod B never scoops up the on-disk
    /// folder of mod A that happens to live inside the same AoE3 root.
    ///
    /// Centralised here (instead of inlined at each call site) so that
    /// every install / repair / update entry point uses the same rule —
    /// future code paths just call this method and get the same
    /// exclusion behaviour the WoL → Improvement Mod install fix
    /// introduced.
    /// </summary>
    public IReadOnlyList<string> GetSiblingInstallPaths(string excludeModId)
    {
        var paths = new List<string>();
        foreach (var p in ModRegistry.All)
        {
            if (string.Equals(p.Id, excludeModId, StringComparison.OrdinalIgnoreCase))
                continue;
            // NEVER exclude the stock base game (aoe3-tad, IsStockGame=true).
            // Its "install path" is the user's real AoE3 (e.g. ...\Age Of
            // Empires 3\bin) — which is exactly the base the installer CLONES
            // (then flattens bin\ into the mod root). Excluding it makes the
            // clone copy 0 base files, so the mod ships with no engine DLLs
            // (RockallDLL/binkw32/granny2/deformerdlly) or data\*.xml and the
            // game exits on launch. The stock game is detect-only and never a
            // "sibling mod" that could be scooped into another install.
            if (p.IsStockGame)
                continue;
            // Enumerate ALL of the sibling mod's copies (active install + its
            // other copies), not just the active one, so a clone never scoops a
            // non-active copy of a sibling mod into the new folder.
            paths.AddRange(GetState(p.Id).AllInstallPaths());
        }
        return paths;
    }

    /// <summary>
    /// Every registered install path across ALL non-stock mods (each mod's active
    /// install + its other copies). Used when installing an ADDITIONAL copy of a
    /// mod: the new clone must exclude every existing mod install — INCLUDING this
    /// mod's own other copies — so it doesn't scoop one into the fresh folder.
    /// (The plain <see cref="GetSiblingInstallPaths"/> excludes the whole current
    /// mod, which is right for a first/normal install but wrong for an extra copy.)
    /// The stock game is skipped for the same reason as above.
    /// </summary>
    public IReadOnlyList<string> GetAllInstallPaths()
    {
        var paths = new List<string>();
        foreach (var p in ModRegistry.All)
        {
            if (p.IsStockGame) continue;
            paths.AddRange(GetState(p.Id).AllInstallPaths());
        }
        return paths;
    }

    /// <summary>
    /// When to show the Radmin VPN connection assistant overlay.
    ///   "Auto"      — opens automatically when the Multiplayer tab
    ///                 loads and the user isn't confirmed in the
    ///                 AoE3 network. Default for new installs.
    ///   "OnRequest" — never opens automatically; user has to click
    ///                 "Show steps" in the compact banner.
    ///   "Never"     — assistant disabled entirely. The compact
    ///                 banner still shows but the "Show steps"
    ///                 button is hidden.
    /// String instead of enum so legacy configs that don't know the
    /// field default cleanly to "Auto" via the property initializer
    /// (an unknown enum value would have to be migrated explicitly).
    /// </summary>
    [JsonPropertyName("radminAssistantMode")]
    public string RadminAssistantMode { get; set; } = "Auto";

    /// <summary>
    /// One-shot "don't show again" flag set when the user ticks the
    /// checkbox at the bottom of the assistant overlay. Equivalent
    /// to switching <see cref="RadminAssistantMode"/> to "OnRequest"
    /// but cheaper for the user to set — and we keep them separate
    /// so a power-user who flips Mode to Never doesn't have to also
    /// flip this back to false to re-show on demand.
    /// </summary>
    [JsonPropertyName("radminAssistantSkipped")]
    public bool RadminAssistantSkipped { get; set; }

    /// <summary>Primary URL of UpdateInfo.xml. Default: official aoe3wol.com server.</summary>
    [JsonPropertyName("updateInfoUrl")]
    public string UpdateInfoUrl { get; set; } = "http://aoe3wol.com/updates/UpdateInfo.xml";

    /// <summary>Fallback URL used if the primary fails. Default: SourceForge mirror.</summary>
    [JsonPropertyName("updateInfoUrlAlt")]
    public string UpdateInfoUrlAlt { get; set; } =
        "http://master.dl.sourceforge.net/project/wars-of-liberty/Patches/UpdateInfo.xml";

    /// <summary>
    /// LEGACY — kept for backward compatibility with configs written before
    /// the per-mod <see cref="Mods"/> dictionary existed. New code should
    /// read/write via <see cref="GetState(string)"/>. On <see cref="Load"/>,
    /// when a non-empty value here AND no <c>mods["wol"]</c> entry exists,
    /// the value is migrated under the WoL profile.
    /// </summary>
    [JsonPropertyName("modInstallPath")]
    public string ModInstallPath { get; set; } = "";

    /// <summary>
    /// Path to age3y.exe (Age of Empires III: The Asian Dynasties).
    /// If empty, the launcher tries to find it automatically by walking up
    /// from the WoL install folder. Wars of Liberty does NOT have its own
    /// .exe — it patches AoE3's data files and the game is launched via
    /// age3y.exe in the AoE3 folder.
    /// </summary>
    [JsonPropertyName("gameExecutable")]
    public string GameExecutable { get; set; } = "";

    /// <summary>
    /// The AoE3 base-game FOLDER the user confirmed by hand via "Change AoE3
    /// folder" — the root that contains <c>data\</c> (or the folder holding
    /// <c>age3y.exe</c>). Unlike <see cref="GameExecutable"/> (a volatile launch
    /// cache cleared on every mod switch), this is DURABLE: it survives switches
    /// so a manually-pointed, non-standard AoE3 install (e.g.
    /// <c>…\Microsoft Studios\Age of Empires III - Complete Collection</c>, which
    /// <see cref="Services.AoE3Detector.FindAll"/> can't auto-locate) stays
    /// recognized — including the detect-only stock <c>aoe3-tad</c> profile, whose
    /// install detection resolves through it. Empty = never set manually (auto-
    /// detection only). See <c>GameLauncher.FindAoe3InstallRoot</c>.
    /// </summary>
    [JsonPropertyName("aoe3ManualPath")]
    public string Aoe3ManualPath { get; set; } = "";

    /// <summary>Optional command-line arguments for the game.</summary>
    [JsonPropertyName("gameArguments")]
    public string GameArguments { get; set; } = "";

    // ------------------------------------------------------------------------
    // Launcher-wide preferences (not per-mod). Surfaced in the
    // "Launcher Settings" dialog. Default values match the previous
    // hard-coded behaviour, so upgrading from an older launcher config
    // doesn't change what the user sees out of the box.
    //
    // Deliberate exception: the "run in background" trio (StartWithWindows /
    // MinimizeToTray / StartMinimized) defaults ON and IS applied to existing
    // configs, once, via BackgroundDefaultSeeded — a pre-existing `false` there
    // means "never chose", not "declined", because the toggle used to default off.
    // The one-time tray notice is what keeps that from being a silent change.
    // ------------------------------------------------------------------------

    /// <summary>
    /// When true (default), the launcher registers itself in
    /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> so Windows
    /// starts it automatically at login — the "run in background" experience,
    /// which is what keeps a player shown as connected to their friends without
    /// having to open the launcher.
    ///
    /// <see cref="Services.StartupRegistrationService"/> applies / clears the
    /// registry key whenever this flag is saved, AND <c>MainWindow</c> re-applies
    /// it each launch (self-heals the exe path for the portable binary).
    ///
    /// This default is ON but it is NOT self-executing: the Settings checkbox reads
    /// the REGISTRY, not this flag, so the default only becomes real once
    /// <see cref="BackgroundDefaultSeeded"/> drives the one-time Run-key write.
    /// </summary>
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; } = true;

    /// <summary>
    /// When true (default), the launcher registers the <c>wol-launcher://</c> URI
    /// scheme (HKCU) so a Discord room "Join" link opens the launcher and joins
    /// the room. On by default so the deep link "just works" once the portable
    /// exe has run; users who want the portable exe to leave no registry trace can
    /// turn it off, which clears the key. <see cref="Services.DeepLinkService"/>
    /// applies / clears it on save and re-applies (self-heals the exe path) each
    /// launch.
    /// </summary>
    [JsonPropertyName("enableJoinLinks")]
    public bool EnableJoinLinks { get; set; } = true;

    /// <summary>
    /// When true, the launcher's main window closes itself once the game
    /// process has started, freeing resources while the user plays. The
    /// previously default behaviour (window stays open) is preserved by
    /// the false default — turning this on is opt-in.
    /// </summary>
    [JsonPropertyName("closeLauncherOnGameStart")]
    public bool CloseLauncherOnGameStart { get; set; } = false;

    /// <summary>
    /// When true (default), the tray icon stays resident so the launcher has a
    /// home to sit in while it runs in the background. Right-click the tray icon
    /// → Exit to actually terminate. Set together with
    /// <see cref="StartWithWindows"/> + <see cref="StartMinimized"/> by the single
    /// "Run in background" toggle.
    ///
    /// NOT the close-button behaviour — that is the independent
    /// <see cref="CloseToTray"/>, which has its own checkbox.
    /// </summary>
    [JsonPropertyName("minimizeToTray")]
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// When true (default), clicking the window's X (or Alt+F4) hides the
    /// launcher to the system tray instead of quitting — the process keeps
    /// running (so the user stays shown as connected) and the only way to
    /// fully exit is the tray icon → Exit (Discord/Steam pattern). The
    /// minimise button is unaffected (it still goes to the taskbar).
    ///
    /// Independent of the "Run in background" bundle (<see cref="MinimizeToTray"/>
    /// / <see cref="StartMinimized"/> / <see cref="StartWithWindows"/>): this
    /// governs ONLY the close-button behaviour and is toggled by its own
    /// "Minimize to tray on close" checkbox in Launcher Settings. Default true
    /// gives everyone close-to-tray with a one-click opt-out; turning it off
    /// restores the conventional "X = quit". Read by
    /// <c>MainWindow.OnClosing</c>. Adds NO antivirus signal (no registry key,
    /// no persistence) — the AV-weighted auto-start lives in the separate
    /// "Run in background" toggle.
    /// </summary>
    [JsonPropertyName("closeToTray")]
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// Set to true after the launcher has shown the one-time "still running in
    /// the tray" balloon the first time <see cref="CloseToTray"/> hid the
    /// window on close — so the onboarding hint fires exactly once and never
    /// nags again. Written by <c>MainWindow.OnClosing</c>.
    /// </summary>
    [JsonPropertyName("closedToTrayHintShown")]
    public bool ClosedToTrayHintShown { get; set; } = false;

    /// <summary>
    /// Set to true the first time the launcher acted on the ON-by-default
    /// "run in background" preference by writing the Run key (see
    /// <see cref="StartWithWindows"/>). Written once by <c>MainWindow</c>'s ctor.
    ///
    /// LOAD-BEARING — this marker is the only thing that makes the default
    /// distinguishable from a re-arm. The Settings checkbox reads the REGISTRY,
    /// so the flags alone are inert; something has to write the Run key for a new
    /// user. But if that write were driven by "the key is missing" instead of this
    /// marker, then unchecking the toggle (which deletes the key) would silently
    /// re-enable auto-start at the next launch — a default that refuses to stay off
    /// is malware behaviour, not a default. With the marker, the seed happens
    /// exactly once per config and the user's opt-out is final.
    ///
    /// Written BEFORE the registry write is attempted, so a failed write (managed-PC
    /// policy, AV) doesn't leave the seed retrying on every launch.
    /// </summary>
    [JsonPropertyName("backgroundDefaultSeeded")]
    public bool BackgroundDefaultSeeded { get; set; } = false;

    /// <summary>
    /// Whether the launcher may switch Age of Empires III's own game recording on.
    ///
    /// <para><b>Default ON, because without a recording a multiplayer match has no result.</b>
    /// The winner is read out of the <c>.age3Yrec</c> file the game writes, and AoE3 ships with
    /// recording OFF — so every match was being stored as "nobody knows who won" and nobody's
    /// rating ever moved. Verified on this machine: <c>optionrecordgame</c> was <c>false</c> in
    /// all five installed mods' profiles.</para>
    ///
    /// <para><b>Launcher-wide, not per-mod</b>: wanting your games recorded is a property of the
    /// player. What IS per-mod is the bookkeeping of which profiles we have already written —
    /// see <see cref="ModState.GameRecordingApplied"/>.</para>
    ///
    /// <para>Turning this off writes <c>false</c> back once per mod, so unchecking the box has a
    /// visible effect, and then the launcher stops touching the profile entirely.</para>
    /// </summary>
    [JsonPropertyName("enableGameRecording")]
    public bool EnableGameRecording { get; set; } = true;

    /// <summary>
    /// Set once the "recording is on" notice has actually been shown, so it never repeats — one
    /// notice for the launcher, not one per mod, even though five separate profiles get written.
    /// </summary>
    [JsonPropertyName("gameRecordingNoticeShown")]
    public bool GameRecordingNoticeShown { get; set; } = false;

    /// <summary>
    /// Armed when a profile was seeded, cleared when the notice is finally shown.
    ///
    /// <para><b>Persisted, and separate from <see cref="GameRecordingNoticeShown"/>, because the
    /// write happens somewhere with no way to talk to the user.</b> Recording is enabled inside
    /// <c>GameLauncher</c> at the instant the player leaves for the game — there is no window to
    /// put a message on, and a toast raised while the game is running is deliberately dropped so
    /// it cannot knock AoE3 out of full screen. So the notice has to be deferred to the next
    /// moment the launcher is actually on screen, and survive the launcher being closed in
    /// between. Without this the "never silently" rule would hold on paper and not in
    /// reality.</para>
    /// </summary>
    [JsonPropertyName("gameRecordingNoticePending")]
    public bool GameRecordingNoticePending { get; set; } = false;

    /// <summary>
    /// Silences the pre-match reminder telling the host to tick "Record Game" on Age of Empires
    /// III's own setup screen.
    ///
    /// <para><b>An explicit choice is the only thing that stops that reminder, and that is the
    /// point.</b> It was first gated on "we have read a recording, so it must be working" —
    /// which was wrong, and measurably so: AoE3's per-match box does NOT inherit from the
    /// profile setting the launcher writes (tested with <c>optionrecordgame=true</c> in place
    /// before the game started, box still unchecked). If the box resets every match, that rule
    /// would have gone quiet after the first success and let every match afterwards go
    /// unrecorded in silence — the reminder disappearing exactly when it is needed most.</para>
    ///
    /// <para>So the reminder fires every time and the way out is one click. Never infer this
    /// from a match that happened to record.</para>
    /// </summary>
    [JsonPropertyName("gameRecordingReminderMuted")]
    public bool GameRecordingReminderMuted { get; set; } = false;

    /// <summary>
    /// Marks that the first-launch "Install a stable copy on this PC?" offer has
    /// already been shown, so it is offered exactly ONCE (never nagged on every
    /// launch, not even if the install failed or the user declined). It is a
    /// separate marker from <see cref="BackgroundDefaultSeeded"/> because the two
    /// answer different questions: that one is "did we seed auto-start", this one
    /// is "did we offer the durable install". The offer only fires while
    /// auto-start is on (<see cref="StartWithWindows"/>) and there is no runnable
    /// canonical copy yet (a portable exe) — the moment for making auto-start
    /// durable. Set BEFORE the install is attempted, same rationale as the seed
    /// marker: a failed/declined attempt must not retry forever.
    /// </summary>
    [JsonPropertyName("selfInstallPromptShown")]
    public bool SelfInstallPromptShown { get; set; } = false;

    /// <summary>
    /// Marks that we have already explained, once, that the launcher is running under a
    /// different Windows account than the one whose session is open (see
    /// <see cref="Services.RunningAccount"/>) — so the player's recordings, saves, decks
    /// and launcher settings are landing in that other account's folders.
    ///
    /// <para>Set BEFORE the dialog opens, the same rationale as
    /// <see cref="SelfInstallPromptShown"/> and <see cref="BackgroundDefaultSeeded"/>: a
    /// notice that was dismissed, or that died with the process, must not come back every
    /// launch.</para>
    ///
    /// <para><b>Per-config, and that is exactly right here.</b> Each Windows account has its
    /// own <see cref="Services.AppPaths.DataDir"/>, so this marker lives in the config of the
    /// account that has the problem — which is the one the notice is about. Running normally
    /// again reads a different config, where it was never needed and never shown.</para>
    /// </summary>
    [JsonPropertyName("crossUserAccountNoticeShown")]
    public bool CrossUserAccountNoticeShown { get; set; } = false;

    /// <summary>
    /// Set when the user ticked "don't show this again" on the pre-install
    /// antivirus-exclusion notice (see
    /// <see cref="ModProfile.AntivirusFalsePositiveFile"/>), i.e. they have added
    /// the exclusions and don't want to be asked before every install.
    ///
    /// <para>Launcher-wide, NOT per-mod, because an antivirus exclusion is a
    /// property of the MACHINE: the folders it covers
    /// (<see cref="Services.AppPaths.InstallTempRoot"/> and the install folder) are
    /// the same ones every mod installs through, so having acknowledged it once for
    /// WoL means it is genuinely handled.</para>
    ///
    /// <para>Only the PREVENTIVE notice reads and writes this. The notice shown
    /// AFTER an antivirus actually blocked a file deliberately ignores it — someone
    /// hitting that failure needs the exclusion paths regardless of what they
    /// dismissed earlier.</para>
    /// </summary>
    [JsonPropertyName("antivirusNoticeAcknowledged")]
    public bool AntivirusNoticeAcknowledged { get; set; } = false;

    /// <summary>
    /// The user ticked "don't show this again" on the compatibility-layer notice — the
    /// one that appears when Windows pinned a compat mode on the game .exe, forcing a UAC
    /// prompt on every launch (see <see cref="Services.AppCompatLayerService"/>).
    ///
    /// <para>Launcher-wide rather than per-mod, like the antivirus notice: the layer is a
    /// property of the MACHINE's Windows configuration, and a player who chose to live
    /// with the prompt for one game does not want to be asked again for the next.</para>
    /// </summary>
    [JsonPropertyName("compatLayerNoticeAcknowledged")]
    public bool CompatLayerNoticeAcknowledged { get; set; } = false;

    /// <summary>
    /// When true, an AUTO-START launch (Windows login, recognised by the
    /// <c>--minimized</c> argument the Run-key registration appends) opens the
    /// launcher straight to the system tray instead of showing the window — so
    /// the "run in background" experience doesn't pop a window on every login.
    /// A MANUAL double-click still shows the window (it carries no
    /// <c>--minimized</c> arg). Set together with <see cref="StartWithWindows"/>
    /// + <see cref="MinimizeToTray"/> by the single "Run in background" toggle.
    /// On by default, with that toggle.
    /// </summary>
    [JsonPropertyName("startMinimized")]
    public bool StartMinimized { get; set; } = true;

    /// <summary>
    /// When true, the launcher shows a system-tray balloon notification
    /// after long-running operations finish (mod update applied, launcher
    /// self-update available). The toast only fires when the main window
    /// is hidden or minimised — there's no point notifying the user about
    /// something they're already watching on screen.
    ///
    /// Default true: matches the principle of "let the user step away and
    /// come back when something's done". Turning it off is opt-out for
    /// users who want a silent launcher.
    /// </summary>
    [JsonPropertyName("showToastNotifications")]
    public bool ShowToastNotifications { get; set; } = true;

    /// <summary>
    /// When true, the launcher shows a Windows notification when ANY user creates
    /// a new multiplayer room for a mod you have installed (a background poll of
    /// the lobby list detects it). Independent of <see cref="ShowToastNotifications"/>
    /// so a user can keep update toasts but silence room notifications. Default true.
    /// </summary>
    [JsonPropertyName("notifyNewRooms")]
    public bool NotifyNewRooms { get; set; } = true;

    /// <summary>
    /// When true (default), the launcher shows an in-app toast (+ sound) when
    /// another player invites you to their multiplayer room. A durable global
    /// opt-out for the invite feature; the receiver-side anti-spam (per-sender
    /// cooldown + session mute) lives in <see cref="Controls.MultiplayerTab"/>.
    /// Independent of <see cref="NotifyNewRooms"/> and
    /// <see cref="ShowToastNotifications"/>. Default true.
    /// </summary>
    [JsonPropertyName("receiveInvites")]
    public bool ReceiveInvites { get; set; } = true;

    /// <summary>
    /// When true (default), the launcher plays short feedback sounds — a chat
    /// blip on an incoming message, a ding on a bell notification, and a pop
    /// when someone connects (joins your room / a new room appears / a player
    /// comes online). Independent of <see cref="ShowToastNotifications"/> and
    /// <see cref="NotifyNewRooms"/> so a user can keep visual notifications but
    /// silence audio. Wired to <see cref="Services.SoundService.Enabled"/> at
    /// startup and on settings save.
    /// </summary>
    [JsonPropertyName("enableSounds")]
    public bool EnableSounds { get; set; } = true;

    /// <summary>
    /// When true (default), the launcher runs the standard "check for
    /// updates" routine on startup — launcher self-update + mod patches +
    /// translations index + mods catalog. Turning it off lets users with
    /// flaky connections, metered data, or strict privacy preferences
    /// avoid any outbound HTTP at launch (the launcher still works fully
    /// from cached state).
    /// </summary>
    [JsonPropertyName("checkUpdatesOnStartup")]
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>If true, opens the postUpdatePage URLs in the browser after each update.</summary>
    [JsonPropertyName("openPostUpdatePages")]
    public bool OpenPostUpdatePages { get; set; } = true;

    /// <summary>
    /// The four settings the "Mods and updates" redesign puts on screen that nothing
    /// reads yet.
    ///
    /// <para>They are here because the design handoff draws them and the maintainer asked
    /// for the reference verbatim; the engine work behind each is a separate job. They
    /// persist, so a player's choice survives, and the day one is wired the value is
    /// already there. <b>Until then they do nothing</b> — do not assume otherwise from
    /// the fact that they are saved.</para>
    ///
    /// <para><b>Read this before wiring <see cref="VerifyDownloadSignatures"/>.</b> Payload
    /// verification is ALREADY conditional: <c>NativeInstallService</c> checks a
    /// download's SHA-256 only when the catalogue pinned one, and a mod with no pinned
    /// hash installs unverified today. So "off" must never come to mean "skip the check"
    /// — that would turn a settings row into a way to disable the only defence there is.
    /// If it is ever wired, it should read as "warn me when a payload is not
    /// hash-pinned".</para>
    /// </summary>
    [JsonPropertyName("autoUpdateMods")]
    public bool AutoUpdateMods { get; set; } = false;

    /// <summary>Incremental patches instead of the whole mod. See <see cref="AutoUpdateMods"/>
    /// for why this is stored and unread. The engine already decides per mod
    /// (<c>DeltaPatchService.IsEligible</c>); this would be the player's half of it.</summary>
    [JsonPropertyName("deltaDownloadsOnly")]
    public bool DeltaDownloadsOnly { get; set; } = true;

    /// <summary><c>"stable"</c> or <c>"beta"</c>. Stored and unread — see
    /// <see cref="AutoUpdateMods"/>. A real Beta channel costs the ETag conditional-request
    /// optimisation on every update check, so it is not a one-line change.</summary>
    [JsonPropertyName("updateChannel")]
    public string UpdateChannel { get; set; } = "stable";

    /// <summary>Download ceiling in KB/s; 0 = no limit. Stored and unread — see
    /// <see cref="AutoUpdateMods"/>.</summary>
    [JsonPropertyName("downloadLimitKbps")]
    public int DownloadLimitKbps { get; set; } = 0;

    /// <summary>Stored and unread, and the one with a trap — see the warning on
    /// <see cref="AutoUpdateMods"/>.</summary>
    [JsonPropertyName("verifyDownloadSignatures")]
    public bool VerifyDownloadSignatures { get; set; } = true;

    /// <summary>Whether other players see your rating. Stored and unread — hiding it is
    /// a SERVER decision (it joins and pushes the number to everyone else), so the switch
    /// exists because the handoff draws it and the backend half is separate work. See the
    /// block comment on <see cref="AutoUpdateMods"/>.</summary>
    [JsonPropertyName("showMyElo")]
    public bool ShowMyElo { get; set; } = true;

    /// <summary><c>"ask"</c>, <c>"always"</c> or <c>"never"</c>. Stored and unread: the
    /// POLICY is genuinely the client's, and the recording is already located after a
    /// match, but the upload endpoint is unverified — an "always" that failed silently
    /// every match would be worse than no setting.</summary>
    [JsonPropertyName("replayUploadPolicy")]
    public string ReplayUploadPolicy { get; set; } = "ask";

    /// <summary>
    /// Opt-in switch for the local multiplayer telemetry log
    /// (<c>multiplayer-events.log</c>): plain event counters (sign-ins,
    /// lobby joins, error codes) appended next to the .exe, with NO network
    /// and NO third-party SDK. Off by default — a fresh install collects
    /// nothing until the user enables it in Launcher Settings → Privacy.
    /// Wired to <see cref="Services.Multiplayer.MultiplayerTelemetry.Enabled"/>
    /// at startup and on settings save. Disclosed in PRIVACY.md (the SignPath
    /// Foundation OSS terms require data collection to be both disclosed and
    /// disableable).
    /// </summary>
    [JsonPropertyName("multiplayerTelemetryEnabled")]
    public bool MultiplayerTelemetryEnabled { get; set; } = false;

    /// <summary>
    /// Contributes your home-city decks to the community card table (Multiplayer →
    /// STATISTICS). <b>On by default</b>, and switched on once for configs that predate that
    /// — see <see cref="ApplyShareDecksDefaultMigration"/>.
    ///
    /// <para><b>This is the ONE thing in the launcher that sends data off the player's own
    /// disk that is not about a match they played</b>, so it stays disclosed in PRIVACY.md and
    /// it stays switchable: the SignPath Foundation OSS terms require collection to be both
    /// disclosed and disableable, and this is collection. Defaulting it on changes the first
    /// of those and not the second — which is why the migration is keyed off a MARKER and not
    /// off the flag, so that turning it off is final.</para>
    ///
    /// <para>What goes up is the CARD NAMES a deck holds, per civilization, keyed to the
    /// Discord account the player is already signed in with. No deck name (that is whatever
    /// they typed), no match, no timestamp of play. The server replaces the account's
    /// previous rows, so it is a standing statement of what they currently carry rather than
    /// a history.</para>
    ///
    /// <para>It says what a player BRINGS and can never say what they PLAYED: the engine
    /// plays a card by deck slot and never transmits an identifier, so no recording carries
    /// it. Every surface built on this has to say so.</para>
    ///
    /// <para>Self-declared and unverifiable by construction — the deck is a file on that
    /// machine — so nothing derived from it may ever reach the rating path.</para>
    /// </summary>
    [JsonPropertyName("shareDeckStats")]
    public bool ShareDeckStats { get; set; } = true;

    /// <summary>
    /// Whether <see cref="ShareDeckStats"/> has already been switched on once for a config
    /// written before it defaulted on.
    ///
    /// <para><b>Changing the default alone reaches nobody who already has the launcher.</b>
    /// The whole config is serialised on every save, so <c>shareDeckStats: false</c> is
    /// already written in every file that exists, and deserialising it puts that straight back
    /// over the new default. Only a migration reaches them.</para>
    ///
    /// <para><b>Keyed off THIS marker and never off "the flag is false".</b> Read from the
    /// flag, the seed would run on every launch and turning the switch off would be undone at
    /// the next start — which is not a setting, it is a countdown, and it would break the
    /// "disableable" half of the terms this collection is disclosed under. Same invariant as
    /// <see cref="BackgroundDefaultSeeded"/> and <see cref="DeveloperModeRetired"/>.</para>
    ///
    /// <para>Set even when the flag was ALREADY true, so the migration never looks again.
    /// That costs one config save on one launch.</para>
    /// </summary>
    [JsonPropertyName("shareDecksDefaultSeeded")]
    public bool ShareDecksDefaultSeeded { get; set; } = false;

    /// <summary>
    /// Public URL of the project's privacy policy (PRIVACY.md on GitHub).
    /// Opened from Launcher Settings → Privacy and linked from the Discord
    /// sign-in dialog (the point where multiplayer data collection begins).
    /// A const, not a serialised field — it's a fixed project link, not user
    /// state.
    /// </summary>
    public const string PrivacyPolicyUrl =
        "https://github.com/Gorgorito12/AoE3-Mod-Launcher/blob/main/PRIVACY.md";

    /// <summary>
    /// The project's Discord — support and announcements.
    ///
    /// <para>A `const` for the same reason as <see cref="PrivacyPolicyUrl"/>: a fixed project
    /// link, not user state. Every surface that shows it goes through
    /// <see cref="Controls.SupportLink"/>, so the label, the glyph and the opening live in one
    /// place rather than in each dialog that happens to want one.</para>
    ///
    /// <para><b>Why the launcher needed this at all.</b> There was no way to reach the project
    /// from inside the app — no Discord, no repo, no "report a bug". The word "Discord" appeared
    /// in the whole UI exactly once outside the sign-in flow: a tooltip telling the player to
    /// attach their diagnostics zip to a bug report there, with nothing to click.</para>
    /// </summary>
    public const string SupportDiscordUrl = "https://discord.gg/WVarbzzzmc";

    /// <summary>
    /// The page explaining how the multiplayer rating works — <c>docs/ELO.md</c>, which is
    /// already written in both languages and is what gets linked when somebody asks why their
    /// match did not count.
    ///
    /// <para>A `const` for the same reason as the two above. Linked from the Clasificación
    /// footnote and from the amber note on a history card that did not score, which are the two
    /// places a player is looking at the consequence of a rule they have not read.</para>
    /// </summary>
    public const string RatingHelpUrl =
        "https://github.com/Gorgorito12/AoE3-Mod-Launcher/blob/main/docs/ELO.md";

    /// <summary>UI language: "en" or "es". While <see cref="LanguageExplicitlyChosen"/>
    /// is false the launcher FOLLOWS the Windows display language on every launch
    /// (see <see cref="DefaultLanguageForCulture"/>); once the user picks a language
    /// in Settings this holds their choice.</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    /// <summary>
    /// Launcher-wide text size: <c>"auto"</c> (the default), or a percentage as a bare
    /// number — <c>"100"</c>, <c>"110"</c>, <c>"125"</c>.
    ///
    /// <para>It multiplies the font-size tokens and nothing else; see
    /// <see cref="Services.TextScale"/> for why that is not the same thing as
    /// <c>UiScale</c>, and for what "auto" works out from. Read once in
    /// <c>App.OnStartup</c>, before the first window exists.</para>
    ///
    /// <para>A string rather than a number so <c>"auto"</c> is a value of the setting
    /// instead of a second flag beside it — the two can then never disagree.</para>
    ///
    /// <para><b>The default went to 100 for one round and came back, and the reason it came
    /// back is the half nobody had looked at.</b> Automatic raises a large desktop panel to
    /// 115 %, which put the multiplayer type back above the handoff values that had just been
    /// restored — so 100 was chosen to stop it deciding for people. But the curve only ever
    /// raises: with the default at 100 it is the SMALL screens that end up on 11.5 px labels
    /// and 9.5 px captions, which is the size this project's own notes call unreadable at
    /// 125/150 % scaling. Trading a big monitor's comfort for a small one's legibility is the
    /// worse of the two bargains.</para>
    /// </summary>
    [JsonPropertyName("textScale")]
    public string TextScale { get; set; } = DefaultTextScale;

    /// <summary>
    /// True once the user has actually picked a text size in Settings. Until then this config
    /// FOLLOWS <see cref="DefaultTextScale"/>, whatever value happens to be sitting in
    /// <see cref="TextScale"/>.
    ///
    /// <para><b>Without this, a default can only ever reach people who have never run the
    /// launcher — which is nobody.</b> Every property here is serialised on the first
    /// <c>Save()</c>, and Save runs constantly (a mod switch, a game launch), so a default is
    /// written into the file within minutes of a first launch and is indistinguishable from a
    /// choice from then on. That is not hypothetical: the default moved to 100 for one round,
    /// every machine that ran that build had <c>"textScale": "100"</c> stamped into its config,
    /// and moving the default back to Automatic changed nothing for any of them. The setting
    /// looked broken and was working exactly as written.</para>
    ///
    /// <para>Same shape, and the same reasoning, as <see cref="LanguageExplicitlyChosen"/> —
    /// which is also why it is set ONLY when the value in Settings actually changes, never
    /// merely because Settings was saved.</para>
    /// </summary>
    [JsonPropertyName("textScaleExplicitlyChosen")]
    public bool TextScaleExplicitlyChosen { get; set; }

    /// <summary>The text size this config is actually on. Never read <see cref="TextScale"/>
    /// raw — a value nobody chose is not a setting, it is a leftover.</summary>
    [JsonIgnore]
    public string EffectiveTextScale => ResolveTextScale(TextScale, TextScaleExplicitlyChosen);

    /// <summary>
    /// The one rule for turning what is on disk into the size to use, shared by this class and
    /// by <c>App.OnStartup</c> — which reads the raw JSON rather than going through
    /// <see cref="Load"/>. Those two having separate copies of a simpler rule is precisely how
    /// the default came to be honoured in one of them and not the other.
    /// </summary>
    public static string ResolveTextScale(string? stored, bool explicitlyChosen)
        => explicitlyChosen && !string.IsNullOrWhiteSpace(stored)
            ? stored.Trim()
            : DefaultTextScale;

    /// <summary>
    /// The text size a config that has never said otherwise gets.
    ///
    /// <para><b>A const because TWO places need it and they diverged.</b>
    /// <c>App.OnStartup</c> reads this one setting straight out of the JSON rather than through
    /// <see cref="Load"/> — it runs before MainWindow and must not trigger four migrations for
    /// one string — so it has its own fallback for a missing key. That fallback said "auto"
    /// after this property was changed to 100, and the result was a launcher whose config
    /// contained no <c>textScale</c> at all and which still scaled to 115 % on a large monitor:
    /// the default was changed in the only place that could not see it.</para>
    /// </summary>
    public const string DefaultTextScale = Services.TextScale.Auto;

    /// <summary>
    /// True once the user has explicitly picked a UI language in Settings. Until
    /// then (the default, and every existing config that predates this flag) the
    /// launcher follows the OS display language each launch — "follow the system
    /// until you override it". Set ONLY when the Settings language actually changes,
    /// so saving Settings without touching the language doesn't silently lock it.
    /// </summary>
    [JsonPropertyName("languageExplicitlyChosen")]
    public bool LanguageExplicitlyChosen { get; set; }

    /// <summary>
    /// Map the OS display language to a shipped UI language. Only two ship, so a
    /// Spanish Windows ("es-*") → "es", everything else → "en". Pure (no
    /// <c>CultureInfo</c> lookup) so it's unit-testable; the caller passes
    /// <c>CultureInfo.CurrentUICulture.TwoLetterISOLanguageName</c>.
    /// </summary>
    internal static string DefaultLanguageForCulture(string? twoLetterIsoLang)
        => string.Equals(twoLetterIsoLang, "es", StringComparison.OrdinalIgnoreCase) ? "es" : "en";

    // (Theme property removed — the launcher is dorado-imperial
    //  dark-only by design now. Old configs with a "theme" key
    //  deserialise harmlessly: System.Text.Json ignores unknown
    //  properties and the next Save drops it from the JSON.)

    /// <summary>
    /// URL of the catalog news.json feed. Default points at the official
    /// catalog repo. Empty disables the news fetch entirely (the Noticias
    /// tab then shows just the placeholder).
    /// </summary>
    [JsonPropertyName("newsUrl")]
    public string NewsUrl { get; set; } =
        "https://raw.githubusercontent.com/Gorgorito12/aoe3-mods-catalog/main/news.json";

    /// <summary>
    /// Persisted window geometry. Width/Height are the user's preferred
    /// normal-state size; Left/Top default to NaN meaning "let WPF
    /// CenterScreen pick a position on first run". Maximized is restored
    /// as a separate flag so we don't store maximized dimensions.
    /// </summary>
    [JsonPropertyName("windowWidth")]
    public double WindowWidth { get; set; } = 1100;

    [JsonPropertyName("windowHeight")]
    public double WindowHeight { get; set; } = 700;

    // Nullable so "never saved a position" serialises as JSON null rather
    // than NaN (System.Text.Json refuses NaN by default and would throw
    // from Save()).
    [JsonPropertyName("windowLeft")]
    public double? WindowLeft { get; set; }

    [JsonPropertyName("windowTop")]
    public double? WindowTop { get; set; }

    [JsonPropertyName("windowMaximized")]
    public bool WindowMaximized { get; set; } = false;

    /// <summary>
    /// Tab the right content panel was showing when the launcher last closed.
    /// One of "Noticias" (default), "Changelog", "Ayuda".
    /// </summary>
    [JsonPropertyName("lastActiveTab")]
    public string LastActiveTab { get; set; } = "Noticias";

    /// <summary>
    /// Left-to-right order of the three top navigation tabs, as stable
    /// tab ids: "library", "workshop", "multiplayer". User-reorderable
    /// from Launcher Settings → Interface. The FIRST entry is also the
    /// tab that opens on launch — the user's mental model is "put the
    /// tab I want first, and it opens first", so order + startup are one
    /// setting, not two.
    ///
    /// Never read this raw — go through <see cref="GetTopTabOrder"/>,
    /// which sanitises a hand-edited / stale / corrupt value (drops
    /// unknown ids, de-dupes, and appends any canonical tab the saved
    /// list is missing) so a bad config can never permanently hide a
    /// tab.
    /// </summary>
    [JsonPropertyName("topTabOrder")]
    public string[] TopTabOrder { get; set; } = { "library", "workshop", "multiplayer" };

    /// <summary>The canonical set of top-tab ids, in their default order.</summary>
    public static readonly string[] CanonicalTopTabs = { "library", "workshop", "multiplayer" };

    /// <summary>
    /// Returns <see cref="TopTabOrder"/> sanitised against
    /// <see cref="CanonicalTopTabs"/>: keeps the saved order for ids we
    /// recognise (case-insensitive, de-duplicated), then appends any
    /// canonical tab the saved list omitted. Guarantees the result is
    /// exactly the canonical set, permuted — so the nav bar always shows
    /// all three tabs regardless of what's on disk.
    /// </summary>
    public string[] GetTopTabOrder()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(CanonicalTopTabs.Length);
        foreach (var id in TopTabOrder ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var norm = id.Trim().ToLowerInvariant();
            if (Array.IndexOf(CanonicalTopTabs, norm) < 0) continue; // unknown id
            if (seen.Add(norm)) result.Add(norm);
        }
        // Append any canonical tab the saved order forgot (e.g. config
        // written before a new tab existed, or a hand-deleted entry).
        foreach (var id in CanonicalTopTabs)
            if (seen.Add(id)) result.Add(id);
        return result.ToArray();
    }

    /// <summary>
    /// URLs of the Wars of Liberty payload ZIP parts. The ZIP is split into
    /// multiple files (.zip.001, .zip.002, ...) to work around GitHub's file
    /// size limits. The launcher downloads all parts, concatenates them into
    /// a single ZIP, then extracts the raw mod files.
    /// </summary>
    [JsonPropertyName("payloadZipUrls")]
    public string[] PayloadZipUrls { get; set; } = new[]
    {
        "https://github.com/papillo12/Updater/releases/download/updater/WolPayload.zip.001",
        "https://github.com/papillo12/Updater/releases/download/updater/WolPayload.zip.002",
        "https://github.com/papillo12/Updater/releases/download/updater/WolPayload.zip.003",
    };

    /// <summary>
    /// Legacy single-URL field. Kept for backward compat; if PayloadZipUrls is
    /// empty, the launcher falls back to this URL.
    /// </summary>
    [JsonPropertyName("installerZipUrl")]
    public string InstallerZipUrl { get; set; } = "";

    /// <summary>
    /// Default install folder shown in the install dialog. The user can
    /// override it before installing.
    /// </summary>
    [JsonPropertyName("defaultInstallFolder")]
    public string DefaultInstallFolder { get; set; } =
        @"C:\Program Files (x86)\Wars of Liberty";

    /// <summary>
    /// Official Wars of Liberty website. Used as a fallback link if the
    /// installer ZIP URL is empty or fails.
    /// </summary>
    [JsonPropertyName("officialWebsite")]
    public string OfficialWebsite { get; set; } = "http://aoe3wol.com/";

    /// <summary>
    /// GitHub release tag of the launcher binary the user is currently running
    /// (e.g. "v0.6.0"). Set automatically after a successful self-update.
    /// Empty on a fresh install — the launcher will prompt once and save it.
    ///
    /// This is the source of truth for self-update detection: we compare it
    /// against the latest release tag on GitHub, NOT the AssemblyVersion of
    /// the running binary. That way the update mechanism doesn't depend on
    /// remembering to bump csproj before publishing.
    /// </summary>
    [JsonPropertyName("lastInstalledLauncherTag")]
    public string LastInstalledLauncherTag { get; set; } = "";

    /// <summary>
    /// GitHub release tag the user dismissed via "Later". The launcher won't
    /// prompt again for this exact tag — only when a different tag appears.
    /// </summary>
    [JsonPropertyName("skippedLauncherTag")]
    public string SkippedLauncherTag { get; set; } = "";

    /// <summary>
    /// ETag from the last successful self-update check against the GitHub
    /// Releases API. Sent back as If-None-Match so GitHub can answer 304 Not
    /// Modified when the latest release is unchanged, sparing the unauthenticated
    /// rate-limit (60 req/h per IP). Opaque value — never parsed, just echoed.
    /// </summary>
    [JsonPropertyName("launcherUpdateETag")]
    public string LauncherUpdateETag { get; set; } = "";

    /// <summary>
    /// GitHub repository where community translations live (format
    /// "owner/repo"). The launcher discovers translations by listing
    /// the releases of this repo and reading the <c>translation.json</c>
    /// asset inside each one.
    /// </summary>
    [JsonPropertyName("translationsRepo")]
    public string TranslationsRepo { get; set; } = "papillo12/translations";

    /// <summary>
    /// DEPRECATED single-repo override. Superseded by
    /// <see cref="ExtraTranslationsFolderRepos"/> + <see cref="CommunityTranslationsDisabled"/>.
    /// Kept only so old configs deserialize; <see cref="MigrateTranslationsFolderRepo"/>
    /// folds any value into the new fields on load and clears this. Never read at
    /// runtime anymore.
    /// </summary>
    [JsonPropertyName("translationsFolderRepo")]
    public string TranslationsFolderRepo { get; set; } = "";

    /// <summary>
    /// EXTRA community-translation folder repos (each "owner/repo") the user has
    /// added by hand in Settings → TRANSLATIONS. These are fetched IN ADDITION to
    /// the active mod profile's own folder repo (the default), and all packs are
    /// merged — so translations from several people coexist. Different people can
    /// host their own repo; the user opts in explicitly, so this is not a trust
    /// escalation (apply-time MD5 + <c>targetMod</c> remain the compatibility
    /// authority). On an id collision the packs' versions are UNIONED into one
    /// entry's version picker (labelled by source repo); display + one-click-apply
    /// metadata comes from the default repo when it has that id.
    ///
    /// Never read raw — go through <see cref="GetExtraTranslationsFolderRepos"/>,
    /// which trims, de-dupes (case-insensitive) and drops entries that aren't a
    /// valid <c>owner/repo</c>.
    /// </summary>
    [JsonPropertyName("extraTranslationsFolderRepos")]
    public string[] ExtraTranslationsFolderRepos { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Master off-switch for ALL community translations (the default folder repo,
    /// the extra repos, and the legacy releases path). Toggled by the "Disable"
    /// checkbox in Settings → TRANSLATIONS. Default false = translations enabled.
    /// </summary>
    [JsonPropertyName("communityTranslationsDisabled")]
    public bool CommunityTranslationsDisabled { get; set; } = false;

    /// <summary>Matches a valid "owner/repo" GitHub identifier.</summary>
    private static readonly System.Text.RegularExpressions.Regex RepoIdRegex =
        new(@"^[a-zA-Z0-9._-]+/[a-zA-Z0-9._-]+$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Returns <see cref="ExtraTranslationsFolderRepos"/> sanitised: trimmed,
    /// de-duplicated case-insensitively, and filtered to syntactically valid
    /// <c>owner/repo</c> entries — so a hand-edited config can't feed a garbage
    /// value into the fetch URL builder.
    /// </summary>
    public string[] GetExtraTranslationsFolderRepos()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var r in ExtraTranslationsFolderRepos ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            var norm = r.Trim();
            if (!RepoIdRegex.IsMatch(norm)) continue;
            if (seen.Add(norm)) result.Add(norm);
        }
        return result.ToArray();
    }

    /// <summary>
    /// GitHub repository (format "owner/repo") that hosts the mods catalog
    /// — one folder per community-submitted mod, each with a
    /// <c>mod.json</c> manifest.
    ///
    /// Three values are meaningful:
    /// <list type="bullet">
    ///   <item><c>""</c> (empty, default) — use the launcher's built-in
    ///     default catalog at <c>Gorgorito12/aoe3-mods-catalog</c>. This
    ///     is what most users want.</item>
    ///   <item><c>"none"</c> — opt-out: skip the catalog fetch entirely.
    ///     The launcher still works, just shows only its built-in mods
    ///     (WoL + Improvement Mod). For users who don't want their
    ///     launcher reaching out to GitHub, or for kiosk deployments.</item>
    ///   <item><c>"owner/repo"</c> — fetch from a specific repo. Useful
    ///     for forks, mirrors, or private test catalogs.</item>
    /// </list>
    ///
    /// Whichever path is taken, built-in mods always win on id collisions:
    /// a community PR cannot shadow the official "wol" entry to redirect
    /// downloads.
    /// </summary>
    [JsonPropertyName("modsCatalogRepo")]
    public string ModsCatalogRepo { get; set; } = "";

    /// <summary>
    /// URL of the central notification feed — a small JSON manifest published by
    /// the self-hosted notifier service (a second Oracle VM, separate from the
    /// lobby backend) that polls GitHub ONCE for everyone and reports each mod's
    /// latest version + published translation keys. The launcher reads it with a
    /// single cheap REST call (ETag/304) instead of the per-mod GitHub polling in
    /// <c>SweepInstalledModsForNotificationsAsync</c>, sparing the unauthenticated
    /// 60 req/h-per-IP budget. The launcher still does the version/translation
    /// DIFF and the dedup locally, so the feed only changes the data SOURCE.
    ///
    /// Three values are meaningful (same convention as <see cref="ModsCatalogRepo"/>):
    /// <list type="bullet">
    ///   <item><c>""</c> (empty, default) — use the launcher's built-in default
    ///     feed URL.</item>
    ///   <item><c>"none"</c> — opt-out: don't contact the notifier; fall back to
    ///     the per-mod GitHub checks for everyone.</item>
    ///   <item>any URL — use that endpoint (forks, mirrors, local test servers).</item>
    /// </list>
    /// If the feed is unreachable or returns bad JSON the launcher ALWAYS falls
    /// back to the direct-GitHub checks, so the notifier is never a single point
    /// of failure.
    /// </summary>
    [JsonPropertyName("notificationFeedUrl")]
    public string NotificationFeedUrl { get; set; } = "";

    /// <summary>
    /// ETag from the last successful notification-feed fetch. Sent back as
    /// If-None-Match so the notifier can answer 304 Not Modified when nothing
    /// changed — the launcher then serves its on-disk feed cache without
    /// re-downloading. Opaque value — never parsed, just echoed. Mirrors
    /// <see cref="LauncherUpdateETag"/>.
    /// </summary>
    [JsonPropertyName("notificationFeedETag")]
    public string NotificationFeedETag { get; set; } = "";

    /// <summary>
    /// LEGACY — see <see cref="ModInstallPath"/>. Migrated to
    /// <see cref="ModState.ActiveTranslationId"/> for the WoL profile on
    /// first load.
    /// </summary>
    [JsonPropertyName("activeTranslationId")]
    public string ActiveTranslationId { get; set; } = "";

    // ------------------------------------------------------------------------
    // Multiplayer (v1.0). Empty / unset values mean "user hasn't opted in";
    // the Multiplayer tab handles bootstrap (sign-in, ZeroTier install) on
    // first open, so a fresh launcher with no MP config still works fully
    // for single-player updates.
    // ------------------------------------------------------------------------

    /// <summary>
    /// Multiplayer state — backend URL and the session token issued by
    /// the lobby backend after a Discord sign-in. Lives in its own
    /// nested object so the JSON layout stays tidy and so adding new
    /// multiplayer fields later doesn't keep ballooning the root
    /// schema. Initialised lazily; <see cref="Multiplayer"/> is never
    /// null after <see cref="Load"/> returns.
    /// </summary>
    [JsonPropertyName("multiplayer")]
    public MultiplayerConfig Multiplayer { get; set; } = new();

    /// <summary>
    /// Persisted history of the notification-bell items (newest-relevant kept,
    /// trimmed to the most recent ~50 by <see cref="Services.NotificationCenter"/>).
    /// Empty on a fresh config; older configs without this key deserialize to an
    /// empty list, so no migration is needed.
    /// </summary>
    [JsonPropertyName("notifications")]
    public List<NotificationItem> Notifications { get; set; } = new();

    /// <summary>
    /// Launcher release tag for which the bell has ALREADY raised a
    /// "launcher update available" item. Dedup key so a given launcher version
    /// only bells once (the gold self-update pill is separate). Empty until the
    /// first launcher-update notification.
    /// </summary>
    [JsonPropertyName("notifiedLauncherTag")]
    public string NotifiedLauncherTag { get; set; } = "";

    /// <summary>
    /// Catalog mod ids for which the bell has ALREADY raised a "new mod" item.
    /// Seeded silently on the first catalog fetch (so the whole existing catalog
    /// doesn't flood the bell on first launch); afterwards only genuinely-new ids
    /// bell. <see cref="CatalogBaselineSeeded"/> distinguishes "empty because first
    /// run" from "empty because no mods".
    /// </summary>
    [JsonPropertyName("notifiedCatalogModIds")]
    public List<string> NotifiedCatalogModIds { get; set; } = new();

    /// <summary>
    /// The last version this launcher has ALREADY accounted for, per catalog mod id.
    ///
    /// <para>Feeds <see cref="NotificationKind.ModPatchPublished"/> — the patch notice for
    /// mods that are not installed. Deliberately separate from
    /// <see cref="ModState.NotifiedUpdateVersion"/>: that one belongs to the installed-mod
    /// path, and sharing a latch would let a patch notice eat the update bell the day the
    /// player installs that mod.</para>
    ///
    /// <para>Recorded for INSTALLED mods too, silently. They bell through the
    /// update-available path instead, but keeping their entry current means uninstalling a
    /// mod does not immediately produce a patch notice for a version already seen.</para>
    ///
    /// <para><see cref="CatalogVersionBaselineSeeded"/> tells "first ever read" apart from
    /// "nothing published", which is the whole difference between a quiet first launch and
    /// the entire catalog arriving at once.</para>
    /// </summary>
    [JsonPropertyName("notifiedCatalogVersions")]
    public Dictionary<string, string> NotifiedCatalogVersions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True once <see cref="NotifiedCatalogVersions"/> has been baselined. Without it the
    /// first feed read bells a patch notice for every mod in the catalog at once.
    /// </summary>
    [JsonPropertyName("catalogVersionBaselineSeeded")]
    public bool CatalogVersionBaselineSeeded { get; set; }

    /// <summary>
    /// Announcement ids already belled, so one is never announced twice — across restarts, and
    /// across the feed being re-read every few minutes. Capped like its siblings.
    /// </summary>
    public List<string> NotifiedAnnouncementIds { get; set; } = new();

    /// <summary>
    /// Whether the announcement baseline has been taken. Distinguishes "nothing published yet"
    /// from "first ever read", which is what keeps the entire published backlog from arriving at
    /// once the first time a launcher sees the feed — the same trap the catalog listing and the
    /// translation index both had to solve.
    /// </summary>
    public bool AnnouncementBaselineSeeded { get; set; }

    /// <summary>
    /// True once the catalog "new mod" baseline has been seeded (see
    /// <see cref="NotifiedCatalogModIds"/>). Prevents the first-ever catalog fetch
    /// from belling every existing mod.
    /// </summary>
    [JsonPropertyName("catalogBaselineSeeded")]
    public bool CatalogBaselineSeeded { get; set; }

    /// <summary>
    /// Deduplicates the "new room created" Windows notification so the same room
    /// isn't re-announced across a restart. Capped like <see cref="NotifiedCatalogModIds"/>.
    /// </summary>
    [JsonPropertyName("notifiedRoomIds")]
    public List<string> NotifiedRoomIds { get; set; } = new();

    /// <summary>
    /// Deduplicates the "your match was rated after all" bell. Keyed by match id, so a frame
    /// delivered twice — or once before a restart and again after — bells once. Capped like
    /// <see cref="NotifiedRoomIds"/>.
    /// </summary>
    [JsonPropertyName("notifiedRatedMatchIds")]
    public List<string> NotifiedRatedMatchIds { get; set; } = new();

    private const string ConfigFileName = "launcher-config.json";

    public static LauncherConfig Load()
    {
        var path = Services.AppPaths.ConfigFile;
        if (!File.Exists(path))
        {
            // A fresh config leaves Language at its "en" default with
            // LanguageExplicitlyChosen=false; the startup step
            // (MainWindow.ApplyStartupLanguage) then follows the OS display language.
            var defaults = new LauncherConfig();
            defaults.Save();
            return defaults;
        }
        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<LauncherConfig>(json) ?? new LauncherConfig();
        // The JSON may have been written by an older launcher (no
        // "multiplayer" key) or by a user who edited it and set the
        // section to null. Either way, callers rely on Multiplayer
        // being non-null, so normalise here.
        cfg.Multiplayer ??= new MultiplayerConfig();
        cfg.MigrateLegacyState();
        cfg.MigrateLobbyBaseUrl();
        cfg.MigrateTranslationsFolderRepo();
        cfg.MigrateDeveloperModeReset();
        cfg.MigrateShareDecksDefault();
        cfg.NormalizeModInstalls();
        return cfg;
    }

    /// <summary>
    /// Heal stale <c>multiplayer.lobbyBaseUrl</c> values that point
    /// at addresses which no longer (or never) resolved. Known bad
    /// values shipped in earlier builds:
    ///
    ///   * <c>https://wol-launcher-lobby.jeisonso1997.workers.dev</c>
    ///     — the previous production URL, served by a Cloudflare
    ///     Worker that has been retired in favour of the self-hosted
    ///     Node backend at wol-lobby.duckdns.org.
    ///   * <c>https://wol-launcher-lobby.workers.dev</c> — looked
    ///     like a public Cloudflare URL but doesn't include the
    ///     account subdomain, so DNS fails with "Host desconocido".
    ///   * <c>http://127.0.0.1:8787</c> — the local wrangler dev
    ///     server. Useful only on the developer's PC.
    ///   * <c>https://*.trycloudflare.com</c> — quick tunnels
    ///     baked into a release; tunnels die when the dev closes
    ///     the terminal.
    ///
    /// When we spot any of these, rewrite to the current production
    /// backend URL and save. Idempotent — once migrated, subsequent
    /// loads see a healthy URL and do nothing.
    /// </summary>
    private void MigrateLobbyBaseUrl()
    {
        var url = Multiplayer.LobbyBaseUrl ?? "";
        bool isBroken = url == "https://wol-launcher-lobby.jeisonso1997.workers.dev"
            || url == "http://wol-launcher-lobby.jeisonso1997.workers.dev"
            || url == "https://wol-launcher-lobby.workers.dev"
            || url == "http://wol-launcher-lobby.workers.dev"
            || url.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".trycloudflare.com", StringComparison.OrdinalIgnoreCase);
        if (!isBroken) return;

        var oldUrl = url;
        Multiplayer.LobbyBaseUrl = new MultiplayerConfig().LobbyBaseUrl;
        // Old sessionToken was signed by a different backend / JWT
        // key, so clear it too — otherwise the next /me call fails
        // with `invalid_token` and the user can't sign in until they
        // manually edit the config. Forcing a fresh Discord sign-in
        // is the right reset.
        Multiplayer.SessionToken = "";
        Multiplayer.SessionExpiresAt = 0;
        Multiplayer.CachedUser = null;

        try { Save(); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Config lobbyBaseUrl migration save failed: {ex.Message}");
        }
        DiagnosticLog.Write(
            $"Migrated multiplayer.lobbyBaseUrl: '{oldUrl}' -> '{Multiplayer.LobbyBaseUrl}'. " +
            $"Session cleared; user needs to sign in again with Discord.");
    }

    /// <summary>
    /// One-time migration of the DEPRECATED single-repo
    /// <see cref="TranslationsFolderRepo"/> into the multi-repo model
    /// (<see cref="ExtraTranslationsFolderRepos"/> + <see cref="CommunityTranslationsDisabled"/>):
    ///   * <c>"none"</c> → set <see cref="CommunityTranslationsDisabled"/> = true.
    ///   * a custom <c>"owner/repo"</c> → append to the extra-repos list.
    ///   * <c>""</c> → nothing to do.
    /// Then clears the old field so it never re-migrates. Idempotent.
    /// </summary>
    private void MigrateTranslationsFolderRepo()
    {
        if (!ApplyDeprecatedTranslationsFolderRepoMigration()) return;
        try { Save(); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Config translationsFolderRepo migration save failed: {ex.Message}");
        }
        DiagnosticLog.Write("Migrated translationsFolderRepo into the multi-repo model.");
    }

    /// <summary>
    /// Pure in-place migration of the deprecated <see cref="TranslationsFolderRepo"/>
    /// into the multi-repo model (<see cref="ExtraTranslationsFolderRepos"/> +
    /// <see cref="CommunityTranslationsDisabled"/>): <c>"none"</c> → disabled;
    /// a custom <c>owner/repo</c> → appended (de-duped) to the extra list;
    /// <c>""</c> → no-op. Clears the old field afterward. Returns true iff it
    /// changed anything. Split out (no disk write) so it's unit-testable without
    /// touching <c>launcher-config.json</c>; the <see cref="Save"/> lives in the
    /// caller <see cref="MigrateTranslationsFolderRepo"/>. Idempotent.
    /// </summary>
    internal bool ApplyDeprecatedTranslationsFolderRepoMigration()
    {
        var old = (TranslationsFolderRepo ?? "").Trim();
        if (old.Length == 0) return false;

        if (string.Equals(old, "none", StringComparison.OrdinalIgnoreCase))
        {
            CommunityTranslationsDisabled = true;
        }
        else
        {
            var list = ExtraTranslationsFolderRepos?.ToList() ?? new List<string>();
            if (!list.Contains(old, StringComparer.OrdinalIgnoreCase))
                list.Add(old);
            ExtraTranslationsFolderRepos = list.ToArray();
        }

        TranslationsFolderRepo = "";
        return true;
    }

    /// <summary>
    /// Switch developer mode off, once, for a config that predates it being hidden.
    /// The <see cref="Save"/> and the log line live here; the decision is in
    /// <see cref="ApplyDeveloperModeResetMigration"/>.
    /// </summary>
    /// <summary>
    /// Turn deck sharing on, once, for a config that predates it being the default. The
    /// decision is in <see cref="ApplyShareDecksDefaultMigration"/>.
    /// </summary>
    private void MigrateShareDecksDefault()
    {
        // Read BEFORE the migration mutates it: "sharing is on" says nothing in a diagnostic
        // bundle without knowing whether this launch is what turned it on.
        bool wasOn = ShareDeckStats;

        if (!ApplyShareDecksDefaultMigration()) return;
        try { Save(); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Config shareDeckStats seed save failed: {ex.Message}");
        }
        DiagnosticLog.Write(wasOn
            ? "Deck sharing was already on; the seed marker is set so it is never forced again."
            : "Deck sharing seeded on for a config that predates the default; "
              + "turning it off in Settings -> Privacy is final.");
    }

    private void MigrateDeveloperModeReset()
    {
        // Read BEFORE the migration mutates it. The two cases are worth telling apart in a
        // diagnostic bundle: "developer mode is off" means nothing without knowing whether
        // this launch is what turned it off.
        bool wasOn = DeveloperMode;

        if (!ApplyDeveloperModeResetMigration()) return;
        try { Save(); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Config developerMode reset save failed: {ex.Message}");
        }
        DiagnosticLog.Write(wasOn
            ? "Developer mode was on and has been retired once; it stays off unless it is unlocked again."
            : "Developer mode reset marker set; it was already off, so nothing changed.");
    }

    /// <summary>
    /// Pure in-place one-time reset of <see cref="DeveloperMode"/>. Returns true iff it
    /// changed anything, which the caller turns into a <see cref="Save"/>. Split out (no
    /// disk write) so it is unit-testable without touching <c>launcher-config.json</c>,
    /// the same shape as <see cref="ApplyDeprecatedTranslationsFolderRepoMigration"/>.
    /// Idempotent.
    ///
    /// <para><b>The guard is the MARKER, not the flag</b> — see
    /// <see cref="DeveloperModeRetired"/> for why reading the flag here would mean the
    /// unlock gesture did not survive a restart. Marker first, then the flip, for the reason
    /// on <see cref="BackgroundDefaultSeeded"/>: a failed save must not leave this retrying
    /// on every launch.</para>
    ///
    /// <para>It touches <see cref="DeveloperMode"/> and nothing else. In particular
    /// <see cref="LocalCatalogModPaths"/> stays, because developer mode gates the TOOLS and
    /// never the content: dropping a locally-added manifest would orphan a real install.</para>
    /// </summary>
    internal bool ApplyDeveloperModeResetMigration()
    {
        if (DeveloperModeRetired) return false;

        DeveloperModeRetired = true;
        DeveloperMode = false;
        return true;
    }

    /// <summary>
    /// Switch deck sharing on, once, for a config written before it defaulted on. The
    /// <see cref="Save"/> and the log line live in <see cref="MigrateShareDecksDefault"/>;
    /// the decision is here, pure and testable.
    /// </summary>
    ///
    /// <para>Marker first, then the flip, for the reason on
    /// <see cref="BackgroundDefaultSeeded"/>: a failed save must not leave this retrying on
    /// every launch. And the guard is the marker, so somebody who turns it off stays off —
    /// see <see cref="ShareDecksDefaultSeeded"/>.</para>
    internal bool ApplyShareDecksDefaultMigration()
    {
        if (ShareDecksDefaultSeeded) return false;

        ShareDecksDefaultSeeded = true;
        ShareDeckStats = true;
        return true;
    }

    /// <summary>
    /// One-time migration of the pre-multi-mod root-level state fields
    /// (<see cref="ModInstallPath"/>, <see cref="ActiveTranslationId"/>)
    /// into the per-mod <see cref="Mods"/> dictionary. Only runs when the
    /// dictionary doesn't already have an entry for the WoL profile, so
    /// it's idempotent — re-loading a migrated config is a no-op.
    /// </summary>
    private void MigrateLegacyState()
    {
        var wolId = ModRegistry.WolId;
        bool needsMigration =
            (!string.IsNullOrEmpty(ModInstallPath) || !string.IsNullOrEmpty(ActiveTranslationId))
            && !Mods.ContainsKey(wolId);

        if (!needsMigration) return;

        Mods[wolId] = new ModState
        {
            InstallPath = ModInstallPath,
            ActiveTranslationId = ActiveTranslationId,
        };
        try { Save(); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Config migration save failed: {ex.Message}");
        }
        DiagnosticLog.Write(
            $"Migrated legacy mod state into Mods[\"{wolId}\"]: " +
            $"installPath='{ModInstallPath}', activeTranslationId='{ActiveTranslationId}'.");
    }

    /// <summary>
    /// Normalize the multi-install shape of every mod after load. Idempotent and
    /// a NO-OP for single-install configs. The stock game is stripped of any
    /// multi-install state. Runs after <see cref="MigrateLegacyState"/> so the
    /// legacy flat fields are already folded into <see cref="Mods"/>.
    /// </summary>
    private void NormalizeModInstalls()
    {
        foreach (var kv in Mods)
        {
            var profile = ModRegistry.Find(kv.Key);
            kv.Value.NormalizeInstalls(isStock: profile?.IsStockGame ?? false);
        }
    }

    public void Save()
    {
        var path = Services.AppPaths.ConfigFile;
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }
}

/// <summary>
/// One addon archive the user imported from a file. The archive itself lives in
/// <see cref="Services.AddonStore"/>; this is only the bookkeeping needed to
/// list it and to recognise a re-import of the same file.
/// </summary>
public class ImportedAddon
{
    /// <summary>Content-derived (<c>local-&lt;sha12&gt;</c>), so re-importing doesn't duplicate.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Shown in the list. Defaults to the archive's file name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Original file name, so the user can tell which download this was.</summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    /// <summary>
    /// Risk level recorded at import time (the string form of
    /// <c>Services.AddonRiskLevel</c>). Cached so listing the tab doesn't reopen
    /// every archive on each render; the authoritative check still runs inside
    /// <c>AddonService.ApplyAsync</c>, which reads the zip again.
    /// </summary>
    [JsonPropertyName("risk")]
    public string Risk { get; set; } = "";

    /// <summary>Files that made it Blocked or SimulationRisk, so the UI can name them.</summary>
    [JsonPropertyName("riskFiles")]
    public List<string> RiskFiles { get; set; } = new();
}
