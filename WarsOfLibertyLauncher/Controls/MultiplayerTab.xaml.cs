using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WarsOfLibertyLauncher.Localization;
using WarsOfLibertyLauncher.Models;
using WarsOfLibertyLauncher.Models.Multiplayer;
using WarsOfLibertyLauncher.Services;
using WarsOfLibertyLauncher.Services.Multiplayer;

namespace WarsOfLibertyLauncher.Controls;

/// <summary>
/// Top-level multiplayer UI. Owns no state itself — everything sits in
/// <see cref="MultiplayerSession"/> and we re-render whenever it raises
/// <c>StateChanged</c>. The host (MainWindow) injects the session via
/// <see cref="Attach"/> after construction, mirroring how the rest of
/// the controls get their services.
///
/// Subtab navigation is purely visual; the same session backs every
/// view, so switching subtab is just toggling which Grid is visible.
/// </summary>
public partial class MultiplayerTab : UserControl
{
    // Three. PROFILE (history and decks included) moved to ProfileWindow, opened from the
    // account block in the nav bar; FRIENDS was deleted outright — see the note beside the
    // subtab buttons in the XAML.
    // PUBLIC only so a test method can name these four in its signature - xUnit needs a
    // public method, and a public method cannot take an internal parameter. The rule the
    // test states is about all four at once, so it has to be able to say them.
    public enum Subtab { Rooms, Tournaments, Ranking, Stats }

    private MultiplayerSession? _session;
    private Func<ModProfile?>? _getActiveProfile;
    private Func<ModProfile, Task<string>>? _computeModFingerprint;
    /// <summary>
    /// MainWindow-provided launch hook. Returns the spawned process so
    /// the multiplayer flow can subscribe to its Exited event (replay
    /// upload, match reporting). Null when the host has no active mod
    /// install — in that case the multiplayer tab declines to launch
    /// rather than guessing.
    ///
    /// 3rd param is the room-aware extra args string built by
    /// <see cref="BuildMultiplayerLaunchArgs"/> — keeps the
    /// multiplayer-specific flag knowledge local to this control so
    /// MainWindow stays a dumb plumber.
    /// </summary>
    /// <summary>
    /// Launch the room's game. Returns the FACTS of the launch rather than a bare process
    /// handle: a launch can succeed with no handle to show for it (elevated, or a lost race
    /// attaching the watcher), and reading a null there as "it didn't start" is what used to
    /// put "couldn't open the game" in the chat of a player whose game was opening.
    /// </summary>
    private Func<ModProfile, EventHandler, string?, Services.WatchedLaunch>? _launchGame;

    /// <summary>
    /// MainWindow-provided callback to switch the launcher's active
    /// mod profile in place (same path the Play-tab tiles use). The
    /// multiplayer-join flow asks for the switch when the user
    /// clicks Join on a room hosted by a different mod than the
    /// one currently active — instead of forcing them to navigate
    /// to Play, click the tile, then come back. Returns true when
    /// the switch succeeded (or the target was already active).
    /// </summary>
    private Func<ModProfile, bool>? _switchActiveMod;

    /// <summary>
    /// MainWindow-provided callback to rotate the ACTIVE install copy of the
    /// active mod (wraps <c>SwitchActiveInstallAsync</c>). Used by the create-room
    /// copy picker: multiplayer always launches / fingerprints the active copy, so
    /// choosing a copy there switches the active copy (single source of truth).
    /// </summary>
    private Func<string, Task>? _switchActiveCopy;

    /// <summary>Show an in-app toast (new-room / invite popups) over any tab, with an
    /// OS tray-balloon fallback when the window isn't visible. Set in <see cref="Attach"/>.</summary>
    private Action<AppToast.ToastOptions>? _showAppToast;

    /// <summary>
    /// Below this, a session was almost certainly "opened AoE3, closed it" rather than a game.
    /// Shared by the report gate and the missing-recording notice, so the launcher never complains
    /// about a recording it would not have reported anyway.
    /// </summary>
    private const int MinReportableSeconds = 180;

    /// <summary>
    /// When to look for the match's recording, in milliseconds after the previous attempt. The
    /// first is immediate; the rest only happen when something was unreadable, so a match with no
    /// recording at all still reports at once. Worst case ~8.5 s before the report — which is the
    /// case that gives a wrong answer instantly today.
    /// </summary>
    private static readonly int[] ReplayRetryDelaysMs = { 0, 1000, 2500, 5000 };

    /// <summary>
    /// The same ladder for a COMPETITIVE match, where waiting is the right call rather than a
    /// cost imposed on the majority.
    ///
    /// <para>The short ladder exists because almost no match is recorded, so patience buys
    /// nothing for most players. A competitive room inverts that: the host confirmed Record Game
    /// before the countdown, so there should be a recording, and the seconds spent finding it are
    /// spent protecting somebody's rating.</para>
    ///
    /// <para>~16.5 s of delay plus the inflates, which keeps the whole wait inside
    /// <see cref="Services.Multiplayer.RoomMatchState.ResultGraceSeconds"/> — the ceiling on how
    /// long the host is held in the room. Lengthen one and look at the other.</para>
    /// </summary>
    private static readonly int[] ReplayRetryDelaysCompetitiveMs = { 0, 1000, 2500, 5000, 8000 };

    /// <summary>
    /// How far past the game closing a recording may be written and still be treated as this
    /// match's own, for ORDERING candidates — never for rejecting them.
    ///
    /// <para>Generous on purpose. The recording is finished as the game closes and its
    /// timestamp keeps moving while the retries run, so a tight edge would start pushing the
    /// real recording behind somebody else's file. Two minutes comfortably covers the retry
    /// ladder and any clock granularity, while still ranking a replay copied into the folder
    /// half an hour later below the one we actually want.</para>
    /// </summary>
    private static readonly TimeSpan ReplayWindowMargin = TimeSpan.FromMinutes(2);

    /// <summary>A new room arrived over /global/ws (id, title, modId, hostUserId, hostLogin).
    /// Handed to MainWindow so the room dedup + dots are shared with the 90 s fallback poll.</summary>
    private Action<string, string, string, string, string>? _onNewRoomFromWs;
    /// <summary>Raised when the backend says a previously-undecided match ended up rated.</summary>
    private Action<MatchRatedNotice>? _onMatchRated;

    /// <summary>
    /// The server refused this build from multiplayer. Raised so MainWindow can offer the
    /// update, which is the only thing the player can actually do about it.
    /// </summary>
    private Action<string>? _onLauncherTooOld;

    private Subtab _activeSubtab = Subtab.Rooms;
    private bool _isRefreshingList;
    private bool _isRefreshingHistory;

    private System.Windows.Threading.DispatcherTimer? _quotaTimer;

    /// <summary>
    /// Polls the overall connection ping and refreshes the rooms-browser
    /// PING cells in place while the Multiplayer tab is visible. Tied to the
    /// same visibility gate as <see cref="_quotaTimer"/>.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _roomsPingTimer;

    /// <summary>
    /// Auto-refreshes the rooms-browser LIST (a quiet, diff-based render
    /// that only repaints when the set of rooms actually changed) while the
    /// Multiplayer tab is visible AND the Rooms subtab is active, so newly
    /// created rooms appear without the user pressing Actualizar. Separate
    /// from <see cref="_roomsPingTimer"/> (which owns the PING column) and
    /// tied to the same visibility gate as <see cref="_quotaTimer"/>.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _roomsListTimer;

    /// <summary>
    /// Signature of the rooms list as last rendered, used by the quiet
    /// auto-refresh to skip a full re-render (and the Join-button rebuild it
    /// would cause) when nothing visible changed. Built by
    /// <see cref="BuildRoomsSignature"/> from the payload fields only — ping
    /// is excluded because <see cref="_roomsPingTimer"/> refreshes it in place.
    /// </summary>
    private string _lastRenderedRoomsSignature = "\0uninitialized";

    /// <summary>
    /// The PING cells of the currently-rendered rooms rows, so the ping can
    /// be refreshed in place (no row rebuild — that would disrupt the Join
    /// buttons). Rebuilt each time the rooms list re-renders.
    /// </summary>
    private readonly System.Collections.Generic.List<StackPanel> _roomPingCells = new();

    /// <summary>Per-room "open for X" sub-line cells + each room's UTC creation
    /// time, so <see cref="RefreshRoomAgeCells"/> ticks them up in place (no
    /// rebuild). Rebuilt with the rooms list, same as <see cref="_roomPingCells"/>.</summary>
    // The subtitle line is ONE TextBlock (the reference gives the row a single second
    // line), and its tail is a live "hace N" that ticks on the rooms timer — so the
    // static part travels with it and the timer rewrites the whole string. Splitting the
    // line in two so only the tail updated would need a horizontal StackPanel, which
    // measures with infinite width and would leave the ellipsis inert.
    private readonly System.Collections.Generic.List<(TextBlock Text, DateTime CreatedUtc, string Prefix)> _roomAgeCells = new();

    /// <summary>
    /// The same trick for the community strip's "31 min ago" cells: the TextBlock plus the
    /// moment the match was reported, so <see cref="RefreshActivityAgeCells"/> ticks them up in
    /// place. Rebuilt with the strip, exactly like <see cref="_roomAgeCells"/>.
    ///
    /// <para>Without it those labels were computed once, when the row was built, and the row is
    /// only rebuilt by a fetch that got past a 60-second gate — so a tab left open said "31 min
    /// ago" for as long as you looked at it. This costs no network at all, which is why it runs
    /// on the plain 3-second tick and not behind the foreground check the FETCH needs.</para>
    /// </summary>
    private readonly System.Collections.Generic.List<(TextBlock Text, DateTime ReportedUtc)> _activityAgeCells = new();

    /// <summary>
    /// The rooms-table columns currently on screen, from <see cref="Services.RoomsTableLayout"/>.
    /// Both the header strip and every row read this, so they cannot drift apart and misalign.
    /// Starts as the full set and is narrowed by <see cref="ApplyRoomColumns"/> as the card
    /// shrinks. It must ALWAYS hold a usable set: rows can be built from a cached response
    /// before the header has ever been measured, and a row built against an empty list gets
    /// no columns and drops every cell.
    /// </summary>
    private System.Collections.Generic.IReadOnlyList<Services.RoomColumnSpec> _roomColumns =
        Services.RoomsTableLayout.All;

    /// <summary>
    /// Whether <see cref="ApplyRoomColumns"/> has actually built the header strip yet.
    ///
    /// <para><b>Load-bearing.</b> The guard in that method used to be "did the resolved set
    /// differ from <see cref="_roomColumns"/>?" alone — but the field is SEEDED with the full
    /// set, which is exactly what <c>Resolve()</c> returns at any comfortable width. So on a
    /// wide window the very first call decided nothing had changed, took its early return, and
    /// never ran the code that builds the header's ColumnDefinitions and re-indexes the header
    /// labels. The header kept the seven-column design-time placeholder from the XAML — two of
    /// whose columns (MOD, STATUS) no longer exist — while the rows used the real five, so
    /// every row sat ~500px to the right of its own heading. The set genuinely matching on the
    /// first call is the NORMAL case, not an edge case, which is why this needs its own flag
    /// rather than a cleverer comparison.</para>
    /// </summary>
    private bool _roomColumnsApplied;

    // Radmin banner state. The timer polls the install/connection
    // status every 3 s while the tab is visible so the user gets
    // immediate feedback when they finish installing or starting
    // Radmin from its own window. _lastRadminStatus is kept so the
    // primary button's click handler knows which branch (install vs
    // launch) to take without re-querying.
    private System.Windows.Threading.DispatcherTimer? _radminTimer;
    private RadminStatus? _lastRadminStatus;
    // Last Radmin state signature written to the diagnostic log; the banner
    // poll only logs when this changes, so the log records transitions, not
    // one line every 3 seconds. See RefreshRadminBanner.
    private string? _lastRadminLogSig;

    // (Pre-Radmin: there used to be n2n bootstrap status here for the
    //  header badge. With the n2n stack removed and Radmin as the
    //  user-managed VPN, the header badge just shows a static label.)

    /// <summary>Currently-subscribed WS, so we can unsubscribe cleanly on room change.</summary>
    private LobbyWebSocket? _attachedSocket;

    /// <summary>
    /// The persistent GLOBAL chat socket (the /global/ws room), owned here
    /// because its lifetime is gated on this tab's visibility + sign-in, not
    /// on being in a lobby. Reuses the generic <see cref="LobbyWebSocket"/>
    /// (SessionToken hello). Opened by <see cref="SyncGlobalChat"/> and torn
    /// down when the tab hides / the user signs out. Separate from
    /// <see cref="MultiplayerSession.RoomSocket"/> — a user can be in the
    /// global chat and a lobby at the same time.
    /// </summary>
    private LobbyWebSocket? _globalChatSocket;

    /// <summary>True once a <c>global_state</c> frame has populated the panel,
    /// so the empty-hint can say "connecting…" before then and "no messages"
    /// after.</summary>
    private bool _globalChatRendered;

    // (Pre-n2n: a per-room PeerMesh lived here so the tab could repaint
    //  when peer RTT/state changed. With n2n the local edge is owned by
    //  the session, peer-by-peer ping is no longer something we can
    //  observe at this layer, and connection state is just N2n.State on
    //  the session. The whole subscription dance is gone.)

    /// <summary>Live state of the current room, rebuilt as WS frames arrive.</summary>
    private readonly System.Collections.Generic.Dictionary<string, RoomMemberEntry> _roomMembers = new();
    private string? _roomHostUserId;
    private bool _isHostInCurrentRoom;

    private sealed class RoomMemberEntry
    {
        public required string UserId { get; init; }
        public string Login { get; set; } = "";
        /// <summary>Discord avatar URL from the room roster (room_state/member_joined),
        /// null for legacy rooms that don't send it yet → falls back to a monogram.</summary>
        public string? AvatarUrl { get; set; }
        public bool Ready { get; set; }
        /// <summary>
    /// The member's AoE3 profile name, from room_state / member_ingame_name.
    ///
    /// <para>The only thing that joins a slot in the recording to a Discord account, and so the
    /// only way a team game can be reported with real teams. Null until they report it, and null
    /// from a backend that predates the frame — <see cref="Services.Multiplayer.MatchTeamMap"/>
    /// then refuses and the match goes down with no teams, exactly as before.</para>
    /// </summary>
    public string? InGameName { get; set; }

    /// <summary>Peer's Radmin VPN IP (26.x), from room_state / member_net.
        /// Null until they report it (at match launch). Used to ICMP-ping them
        /// for the in-game per-player ping column.</summary>
        public string? RadminIp { get; set; }
        /// <summary>Last measured ICMP RTT to <see cref="RadminIp"/> in ms;
        /// -1 = unknown / no answer / no IP yet.</summary>
        public int PingMs { get; set; } = -1;
        /// <summary>Consecutive non-answering probes — feeds
        /// <see cref="PeerNetHealth.Classify"/> so a single dropped packet doesn't
        /// flip the peer to "Lost". Reset to 0 on any answer.</summary>
        public int ConsecutiveFails { get; set; }
        /// <summary>Consecutive answering probes — used to debounce the
        /// "reconnected" chat edge. Reset to 0 on any failure.</summary>
        public int ConsecutiveOks { get; set; }
        /// <summary>Last link state we ANNOUNCED in chat, so RefreshInGamePanel only
        /// posts on the Online↔Lost edge (not every tick). Init WaitingVpn.</summary>
        public PeerLinkState LastLinkState { get; set; } = PeerLinkState.WaitingVpn;
        /// <summary>The member's Glicko rating and deviation from room_state /
        /// member_joined. Null for a room whose backend doesn't send them, and null
        /// for a player who has no rating row yet — both mean "don't paint a number".
        /// See <see cref="RatingDisplay.ShouldShow"/>.</summary>
        public double? Rating { get; set; }
        public double? Rd { get; set; }
    }

    /// <summary>
    /// Whether the player's own AoE3 profile name could be read when this match started.
    ///
    /// <para>Cached because <see cref="UserDataService.GetInGameName"/> reads a file and
    /// the in-game panel that shows it repaints on a two-second timer. False means the
    /// launcher cannot find this player inside their own recording, so the match cannot
    /// produce a result no matter what the recording does — which is why the RECORDING
    /// cell says so instead of advising them to tick a box that would not help.</para>
    /// </summary>
    private bool _canIdentifyPlayerInReplay = true;

    /// <summary>
    /// Why the last match's recording could not be read, if it could not.
    ///
    /// <para>A field because the result card is not always built by the same call that
    /// did the reading: the host builds it from its own report, the guest from the
    /// <c>match_reported</c> frame, and the close-the-room path from neither. All three
    /// want the same explanation, and only the reading knew it.</para>
    ///
    /// <para>It NEVER overrides the server. See
    /// <see cref="Services.Multiplayer.MatchOutcomeView.UnratedNoteKey"/>: a specific
    /// server reason wins, and this only speaks when the server said "nobody won"
    /// without saying why.</para>
    /// </summary>
    private Services.Multiplayer.LocalReadFailure _lastLocalReadFailure = Services.Multiplayer.LocalReadFailure.None;

    /// <summary>
    /// The particulars behind <see cref="_lastLocalReadFailure"/>, appended to its message —
    /// today the AoE3 profile name we read against the names the recordings actually carried.
    /// Data, never translated. Null whenever there is nothing specific to add.
    /// </summary>
    private string? _lastLocalReadDetail;

    /// <summary>
    /// The recording this match was read from, so the result card can point at it.
    ///
    /// <para><b>A field for the same reason as the two above, and it took the same bug to see
    /// why.</b> The card's rebuilder CAPTURES its <c>MatchReplayInfo</c>, so a reading that
    /// lands after the card was first painted — the early read, or the late correction — would
    /// repaint it still holding the null it started with. Everything that can change has to be
    /// read at call time, and this can change.</para>
    ///
    /// <para><b>Only ever assigned to a real file, and cleared only when a new match starts.</b>
    /// A later pass may fail to find what an earlier one found, and letting it null this would
    /// take the answer away again — the same "a later pass may only improve the diagnosis" rule
    /// that governs <see cref="_lastLocalReadFailure"/>.</para>
    /// </summary>
    private string? _lastRecordingPath;

    /// <summary>
    /// Records the recording this match was read from, and repaints the card so the REPLAY cell
    /// actually shows it.
    ///
    /// <para><b>Reading the field at paint time is worth nothing if nobody paints again</b>, and
    /// that is exactly what happened: <c>HandleMatchReported</c> can paint the card while our own
    /// AoE3 is still open, when this is still the null <c>EnterInGamePhase</c> left — and for a
    /// RATED match neither repaint call site can run (see <see cref="RepaintMatchResult"/>). The
    /// exit handler then found the file, assigned it here, and the chat line named a recording
    /// the card had just said did not exist.</para>
    ///
    /// <para>Assignment goes through one method so the repaint cannot be forgotten at a fourth
    /// site. It refuses a blank rather than clearing: a later pass may only improve what an
    /// earlier one found.</para>
    /// </summary>
    private void SetLastRecordingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (string.Equals(_lastRecordingPath, path, StringComparison.Ordinal)) return;
        _lastRecordingPath = path;
        RepaintMatchResult();
    }

    /// <summary>
    /// How to rebuild the end-of-match card, captured when it is first painted.
    ///
    /// <para><b>Why a rebuilder and not a repaint of the same model.</b> The card used to be
    /// built exactly once, at the moment the report arrived — so a player whose own AoE3 was
    /// still open saw "the match was not recorded" frozen on screen while their recording sat
    /// on disk naming the winner. The reading lands seconds or minutes later and has to be able
    /// to replace that text.</para>
    ///
    /// <para>It must NOT be done by re-entering <see cref="EnterResultPhase"/>: that method
    /// clears <c>_roomMatchLive</c>, drops the process handle, kills the tick timer, stops the
    /// socket's reconnect and suppresses the leave confirm. Running all of that again over an
    /// already-terminal state is a different bug. This closure rebuilds the MODEL only, and both
    /// the host's path and the guest's history path install one, so a late reading corrects
    /// either card.</para>
    /// </summary>
    private Func<Services.Multiplayer.MatchOutcomeView>? _outcomeRebuilder;

    /// <summary>
    /// One recording analysis at a time. The search now runs from two places — the game exiting
    /// and the match being reported while the game is still open — and both write
    /// <see cref="_lastLocalReadFailure"/>. Without this they can interleave and the later,
    /// worse answer wins.
    /// </summary>
    private int _replayAnalysisInFlight;

    // -------- Lobby window (replaces the old in-tab popup) ----------
    //
    // The lobby UI used to be a Canvas overlay inside this tab
    // (RoomPanel Grid + floating-card Border). We extracted it to a
    // real top-level Window so the user can drag/resize/move it freely
    // via OS chrome. Single-instance: opening a room with the window
    // already open just .Activate()s it. Closing it (✕/Esc/Alt+F4)
    // fires Closed which clears this field AND triggers leave-room on
    // the session if we're still in one (see HandleLobbyWindowClosed).
    //
    // Render* and Apply* methods below guard on _lobbyWindow == null
    // and return early — they're invoked from session events that may
    // fire after the window was already closed (e.g. host disconnect
    // race) and we shouldn't crash on a null reference. When non-null,
    // they read/write the window's UI elements directly through the
    // field-modifier-internal x:Name fields (same assembly).
    private LobbyWindow? _lobbyWindow;

    // The player's own profile — same single-instance contract as _lobbyWindow above. This
    // class keeps building the page (RenderProfileTab and its builders are bound to the
    // session, the standing cache, the history rows and the deck caches); the window is the
    // frame it draws into. Non-null means "open", and every repaint path guards on it.
    private ProfileWindow? _profileWindow;

    // -------- Match lifecycle state ---------------------------------
    //
    // Three logical phases:
    //   Lobby     — popup is fully interactive, X visible
    //   Starting  — countdown overlay shown, no X
    //   InGame    — InGame overlay shown, only Cancel/Leave
    //
    // We track the phase locally so the UI gates immediately without
    // waiting for a round-trip to the server. The Worker's
    // game_countdown / game_started / game_cancelled frames flip
    // this; the popup's UI responds to changes.

    /// <summary>
    /// Lobby → Starting → InGame → <b>AwaitingResult</b> → <b>Result</b>. The last one is
    /// terminal for that room: a reported match closes it server-side, so there is nothing to go
    /// back to.
    ///
    /// <para><b>AwaitingResult</b> is the gap between our own game closing and the match being
    /// settled, and it exists because a guest used to spend it looking at nothing. It is NOT
    /// <c>Result</c> with a different label: <see cref="EnterResultPhase"/> stops the socket's
    /// reconnect, and the socket is precisely what the <c>match_reported</c> frame still has to
    /// arrive on. Entering Result early would hang up on the answer.</para>
    /// </summary>
    private enum MatchPhase { Lobby, Starting, InGame, AwaitingResult, Result }
    private MatchPhase _matchPhase = MatchPhase.Lobby;

    /// <summary>
    /// Set true when the host has auto-triggered the start because everyone
    /// marked ready, and cleared on return to the lobby (<see cref="ExitInGamePhase"/>).
    /// Guards <see cref="MaybeAutoStartOnAllReady"/> against firing twice in the
    /// brief window between <c>SendStartAsync</c> and the <c>game_countdown</c>
    /// echo that actually flips <see cref="_matchPhase"/> to Starting.
    /// </summary>
    private bool _autoStartInFlight;

    /// <summary>
    /// Abort grace window (ms) AFTER AoE3 launches. Within it, ANY member can
    /// abort the match for everyone (covers a bad/desynced start while the map
    /// loads). After it, "leave" only removes yourself. Must stay ≤ the backend's
    /// own window (COUNTDOWN_MS + 60s) — the server is authoritative; this just
    /// flips the button UX so the user isn't offered an abort the server rejects.
    /// </summary>
    private const long AbortWindowMs = 60_000;

    /// <summary>
    /// True while aborting the match for everyone is still allowed: the whole
    /// countdown (Starting), plus the first <see cref="AbortWindowMs"/> after
    /// launch (InGame). Measured off the local launch tick — the same moment the
    /// server measures from once the synchronised countdown fires.
    /// </summary>
    private bool WithinAbortWindow =>
        _matchPhase == MatchPhase.Starting
        || (_matchPhase == MatchPhase.InGame
            && (Environment.TickCount64 - _matchTimerStartTicks) < AbortWindowMs);

    /// <summary>
    /// AoE3 process spawned when the countdown completed. Cached so
    /// <see cref="InGameCancelButton_Click"/> can <c>Kill()</c> it on
    /// cancel without re-walking the process table. Cleared when the
    /// process exits or we leave the room.
    /// </summary>
    private System.Diagnostics.Process? _aoe3Process;
    private long _matchTimerStartTicks;


    /// <summary>
    /// The match currently being played, captured when AoE3 was launched and consumed when it
    /// exits — roster, lobby, mod, our role and the start time. Null outside a match.
    ///
    /// <para>It replaced a roster list plus a start timestamp that were read LIVE at exit, which
    /// is how a real match went unreported: leaving the room cleared both a moment before the
    /// exit handler ran. See <see cref="Services.Multiplayer.MatchContext"/> for the full
    /// story — nothing here may go back to asking the room what just happened.</para>
    ///
    /// <para>Not to be confused with <see cref="_matchPhase"/> ("am I in a game right now") or
    /// <see cref="_roomMatchLive"/> ("is the ROOM in a match") — three different lifetimes.</para>
    /// </summary>
    private Services.Multiplayer.MatchContext? _matchContext;

    /// <summary>
    /// True while the ROOM is in a match, which outlives our own game: it stays set when our
    /// AoE3 closes and we drop back to the lobby, and that is exactly the state where a guest
    /// needs to be offered a way back in (and warned that leaving the room is one-way while the
    /// backend answers <c>Conflict('Lobby already in game.')</c> to any re-join).
    ///
    /// <para>Set in <see cref="StartCountdown"/> — the one point every member passes through,
    /// including one whose launch fails. Cleared by <c>game_cancelled</c> (abort, cancel, "the
    /// host ended it"), by the socket teardown, and by the host's own <c>game_ended</c>, which
    /// the server broadcasts to everyone EXCEPT the sender.</para>
    /// </summary>
    private bool _roomMatchLive;

    /// <summary>
    /// Radmin-adapter total-byte counter captured when the match started,
    /// so the InGame TRAFFIC stat can show bytes moved during THIS match
    /// (the OS counter is cumulative since the adapter came up). -1 = the
    /// adapter wasn't found at match start, so we show "—".
    /// </summary>
    private long _matchBaselineBytes = -1;

    /// <summary>
    /// Last measured internet latency (ICMP RTT to a public host — see
    /// <see cref="PingInternetRttMsAsync"/>), in ms; -1 = unknown/no answer.
    /// Refreshed by a fire-and-forget probe, guarded by
    /// <see cref="_connectionPingInFlight"/>.
    /// </summary>
    private int _connectionPingMs = -1;
    private bool _connectionPingInFlight;

    /// <summary>Drives the breathing animation of the InGame "live" dot + match timer.</summary>
    private System.Windows.Threading.DispatcherTimer? _inGameTickTimer;

    /// <summary>Drives the per-frame countdown number 3 → 2 → 1.</summary>
    private System.Windows.Threading.DispatcherTimer? _countdownTickTimer;

    /// <summary>
    /// Polls the overall connection ping (seed-peer RTT) while the lobby
    /// window is open, so the room header's CONNECTION stat stays live
    /// even before a match starts. Stopped when the window closes.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _lobbyPingTimer;

    /// <summary>
    /// Local tick count when the countdown started + the total
    /// duration in ms. We use a purely-local timer (not the
    /// server's `starts_at_ms` absolute timestamp) because client
    /// and server clocks can drift by seconds, which made the
    /// countdown either skip entirely (client ahead → remaining
    /// is negative the moment the frame arrives) or run too long
    /// (client behind). Per-peer drift is bounded by WS latency
    /// variance (~50 ms), which is fine for AoE3 LAN host setup.
    /// </summary>
    private long _countdownStartedAtTicks;
    private int _countdownDurationMs = 3000;

    /// <summary>
    /// Launcher config reference, injected via <see cref="Attach"/>.
    /// Read by the Radmin assistant auto-open logic to honour the
    /// user's mode preference (Auto / OnRequest / Never) and to
    /// flip <c>RadminAssistantSkipped</c> when the user ticks the
    /// "Don't show again" checkbox inside the overlay.
    /// </summary>
    private LauncherConfig? _config;

    /// <summary>
    /// Tracks whether we've already attempted to auto-open the
    /// Radmin assistant during the current launcher session. Without
    /// this guard the assistant would re-open every time the user
    /// switched tabs back to Multiplayer (because StartRadminPolling
    /// re-runs from the IsVisibleChanged hook). One auto-open per
    /// session is enough — if they closed it they can reopen via the
    /// banner's "Show steps" button.
    /// </summary>
    private bool _radminAssistantAutoOpenedThisSession;

    /// <summary>
    /// The currently-open assistant window, if any. Kept so a
    /// second "Show steps" click brings the existing window to
    /// front instead of opening a duplicate.
    /// </summary>
    private RadminAssistantWindow? _radminAssistantWindow;

    public MultiplayerTab()
    {
        InitializeComponent();
        ApplyStrings();

        // Window-size scaling for the whole Multiplayer surface (Controls/UiScale.cs).
        // sizeSource = this UserControl (host-sized, transform-independent → no
        // feedback). A LayoutTransform makes the scaled root still fill its slot,
        // so the MpAlertOverlay scrim injected into the root keeps covering the
        // full tab.
        if (Content is FrameworkElement mpRoot)
        {
            UiScale.Attach(mpRoot, this, 1100, 560);

            // A tab taller than its slot is CLIPPED in silence: ContentHost is
            // ClipToBounds, so the bottom rows just are not there and nothing errors.
            // Logged once per size, and only when it actually overflows, so a healthy
            // layout stays quiet.
            //
            // It never caught the rooms column, and could not have: that column divided a
            // FIXED height between a star row and two Auto ones, so being too short made the
            // star row SHRINK rather than the tab overflow — the list collapsed to one row
            // and this stayed silent through it (zero hits across the reporter's logs). The
            // column scrolls now, so what this can still catch is the Auto rows ABOVE it:
            // the header, the Radmin banner and the toolbar.
            mpRoot.SizeChanged += (_, _) =>
            {
                var want = mpRoot.DesiredSize.Height;
                var got = ActualHeight;
                if (got > 0 && want > got + 0.5)
                    DiagnosticLog.Write(
                        $"Multiplayer tab OVERFLOWS its slot: wants {want:0} DIP, has {got:0} " +
                        $"(short by {want - got:0}). The bottom of the left column is being clipped.");
            };
        }
        // Re-decide which columns fit whenever the table's width changes. Hooked to the header
        // strip rather than the window because that IS the width the columns divide up, already
        // in the logical units UiScale lays this tab out in.
        RoomsHeaderStrip.SizeChanged += (_, _) => ApplyRoomColumns();
        // Initial Radmin banner render (state poll + paint). The timer
        // starts ticking only once IsVisible flips to true via the
        // OnVisibleChangedTabGate hook installed by Attach().
        RefreshRadminBanner();
        // Initial state is the signed-out gate; once Attach() runs we
        // re-render against the real session.
        RefreshFromSession();
    }

    // ------------------------------------------------------------------
    // Radmin VPN banner — reactive 3-state UI driven by RadminVpnService.
    //
    // The banner sits at the top of the rooms browser. We re-render it
    // every 3 s while the tab is visible so manual state changes the
    // user makes in Radmin's own window (connect, disconnect, install
    // mid-session) are reflected without them having to navigate away
    // and back.
    //
    // The user dismiss flag (previously RadminBannerDismissed) was
    // removed in this iteration: the new banner is informative (small,
    // colour-coded) rather than nagging, and a dismissed user who
    // later forgets why their game isn't connecting has no recourse
    // otherwise. The config field has also been deleted.
    // ------------------------------------------------------------------

    /// <summary>
    /// Query Radmin's current state and update the banner's icon,
    /// title, body and primary-action button to match. Cheap (sub-ms
    /// registry + NIC enumeration), safe to call on the UI thread.
    /// </summary>
    /// <summary>
    /// Paints the launcher's title-bar connection chip. Set in <see cref="Attach"/>;
    /// null when the tab is hosted without a chip (tests, or an older MainWindow).
    /// </summary>
    private Action<string?, string?>? _setConnectionChip;

    /// <summary>Paints the title-bar account cluster. Set in <see cref="Attach"/>.</summary>
    private Action<string?, string?, string?>? _setAccountChip;

    /// <summary>
    /// Pushes the signed-in identity (and the cached rating, when there is one) to the
    /// title bar. A null user hides the cluster. Called from the top of
    /// <see cref="RefreshFromSession"/> — above every early return — and again when a
    /// standing arrives.
    ///
    /// <para><b>That position is the whole fix, not a tidy-up.</b> This used to be called from
    /// a one-line <c>RenderBrowser()</c>, with a comment claiming it therefore tracked sign-in
    /// AND sign-out. It tracked sign-in only: that method sat on the signed-in branch of
    /// <c>RenderRoomsTab</c>, so signing out hit the signed-out gate (then a method of its own,
    /// <c>ShowSignInPanel</c>; folded into <see cref="ShowSubtabView"/> since) and returned one line
    /// earlier, leaving the username and the rating on the title bar of a launcher that had
    /// just cleared its token, its user and both sockets. The tab self-corrects because it
    /// re-reads <c>Status</c> every pass; the chip cannot, because it reads nothing at all.
    /// <c>RenderBrowser</c> is gone rather than left empty — it held this line and nothing
    /// else.</para>
    ///
    /// <para>It also KICKS the standing fetch when there is no cached one. That line is
    /// what makes the ELO under the name appear at all: the cache was only ever filled by
    /// <see cref="LoadStandingAsync"/>, which was only reachable by opening the Profile
    /// subtab — so for everyone who never opened it the chip's rating was permanently
    /// null, silently, while the doc comment claimed otherwise. The fetch is bounded by
    /// <c>_standingFetchInFlight</c> and by only firing on a null cache, and the callers
    /// are session-state changes (sign-in, entering or leaving a room), never a poll.</para>
    /// </summary>
    private void PushAccountChip(Models.Multiplayer.LobbyUserSummary? user)
    {
        if (_setAccountChip == null) return;
        if (user == null)
        {
            _setAccountChip(null, null, null);
            return;
        }

        // Plain, with no qualifier: 1500 is where everyone starts, so showing it says
        // nothing about anybody. Still hidden when there is no standing at all — that is
        // not a 1500, it is not knowing, which is what the backend outage looked like.
        var elo = !RatingDisplay.ShouldShow(_cachedStanding?.Rating)
            ? null
            : RatingDisplay.IsUnrated(_cachedStanding!.Rd, _cachedStanding.GamesPlayed)
                ? Strings.Get("MpEloUnrated")
                : Strings.Format("MpChipElo", (int)Math.Round(_cachedStanding.Rating));
        _setAccountChip(user.DiscordUsername, user.AvatarUrl, elo);

        // Null cache: either we have never fetched, or a match just invalidated it. Both
        // want the same thing. LoadStandingAsync re-pushes when it lands.
        if (_cachedStanding == null) _ = LoadStandingAsync();
    }

    private void RefreshRadminBanner()
    {
        if (RadminBanner == null) return;

        var status = RadminVpnService.GetStatus();
        _lastRadminStatus = status;

        // Default to shown; only the READY branch below collapses it. This poll runs
        // every ~3 s, so the banner has to be able to come BACK when Radmin drops —
        // without this it would collapse once and never return.
        RadminBanner.Visibility = Visibility.Visible;

        // Record every Radmin state TRANSITION to the diagnostic log so a
        // bundle can show WHY IsServiceRunning was false (open-but-Desconectado,
        // closed, wrong GUI process name, no 26.x adapter). The banner poll is
        // every 3 s but we only write on change, so the log stays quiet. Before
        // this, GetStatus was never logged and "Radmin wasn't recognized" was
        // undiagnosable from a bundle.
        var radminSig = RadminVpnService.DescribeStateForLog();
        if (!string.Equals(radminSig, _lastRadminLogSig, StringComparison.Ordinal))
        {
            _lastRadminLogSig = radminSig;
            DiagnosticLog.Write($"RadminState: {radminSig}");
        }

        // Three-way switch driven by (InstallState, IsServiceRunning):
        //   * NotInstalled              → red    "Install"
        //   * Installed, service off    → blue   "Open Radmin"
        //   * Service on (any state)    → green  "Radmin running — copy/paste the AoE3 network name"
        //
        // We DON'T try to distinguish "in the AoE3 network" from "in
        // some other Radmin network" or "in no network at all". Radmin
        // keeps per-network membership inside its own process — the OS
        // only learns about specific peers when there's actual IP
        // traffic with them (typically 1-2 entries even for a 20+
        // member active network), so any peer-count heuristic produces
        // misleading false negatives. We report the honest signal
        // ("Radmin is on"), put the network name + a Copy button
        // directly in the banner, and number the manual steps. That's
        // as low-friction as the GUI-only manual flow can be made.
        if (status.InstallState == RadminInstallState.NotInstalled)
        {
            RadminBanner.Background = (Brush)new BrushConverter().ConvertFromString("#3d1f1f")!;
            RadminBanner.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#8c3a3a")!;
            RadminStatusIcon.Background = (Brush)new BrushConverter().ConvertFromString("#8c3a3a")!;
            RadminStatusGlyph.Text = "!";
            RadminBannerTitle.Text = Strings.Get("MpRadminNotInstalledTitle");
            RadminBannerBody.Text = Strings.Get("MpRadminNotInstalledBody");
            RadminBannerBody.Visibility = Visibility.Visible;
            RadminPrimaryButton.Content = Strings.Get("MpRadminInstallButton");
            RadminPrimaryButton.Visibility = Visibility.Visible;
            RadminPrimaryButton.IsEnabled = true;
            // No actionable network info to show while Radmin isn't on yet.
        }
        else if (!status.IsServiceRunning)
        {
            // RED warning (same palette as NotInstalled): "not ready" —
            // Radmin is installed but closed OR powered off ("Desconectado").
            // A blue "info" tone under-sold it; this is an action-required
            // state, so it reads as a warning like the traffic-light green.
            RadminBanner.Background = (Brush)new BrushConverter().ConvertFromString("#3d1f1f")!;
            RadminBanner.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#8c3a3a")!;
            RadminStatusIcon.Background = (Brush)new BrushConverter().ConvertFromString("#8c3a3a")!;
            RadminStatusGlyph.Text = "!";
            RadminBannerTitle.Text = Strings.Get("MpRadminNotConnectedTitle");
            // The 26.x adapter keeps its IP even while Radmin is closed /
            // "Desconectado" (RvControlSvc), so show it here too — it tells the
            // user the launcher already sees their Radmin IP (and that AoE3 will
            // bind to it) even though the banner is red / action-required.
            var offIp = RadminVpnService.TryGetAdapterIp();
            RadminBannerBody.Text = string.IsNullOrEmpty(offIp)
                ? Strings.Get("MpRadminNotConnectedBody")
                : Strings.Format("MpRadminNotConnectedBodyIp", offIp);
            RadminBannerBody.Visibility = Visibility.Visible;
            RadminPrimaryButton.Content = Strings.Get("MpRadminOpenButton");
            RadminPrimaryButton.Visibility = Visibility.Visible;
            RadminPrimaryButton.IsEnabled = true;
        }
        else
        {
            // READY. The banner used to stay here as a permanent full-width green
            // strip restating that everything was fine — one of the five stacked
            // bars the redesign exists to remove (handoff 1a). It now collapses
            // entirely and the state moves to the title-bar chip, so the banner
            // appears ONLY when something needs the user's attention. The two
            // branches above are unchanged and still show it.
            RadminBanner.Visibility = Visibility.Collapsed;
        }

        // Radmin owns only the chip's ADDRESS half; the word comes from the lobby
        // session. Refreshing through the shared method keeps this poll from
        // overwriting the other half every ~3 s.
        PushConnectionChip();
    }

    /// <summary>
    /// Briefly swap a button's label to "Copied!" so the click feels
    /// acknowledged, then restore the original text after a short
    /// delay. Pure UI candy — no behavioural consequence beyond the
    /// visual feedback.
    /// </summary>
    private void FlashCopiedToast(System.Windows.Controls.Button button)
    {
        var original = button.Content;
        button.Content = Strings.Get("MpRadminCopiedToast");
        button.IsEnabled = false;
        var revert = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            button.Content = original;
            button.IsEnabled = true;
        };
        revert.Start();
    }

    /// <summary>
    /// State-aware click handler for the banner's primary button. Routes
    /// to install / launch based on the last polled status — that's
    /// what was on screen when the user clicked, so it's always the
    /// right action to perform even if the world changed mid-frame.
    /// </summary>
    /// <summary>
    /// "Show steps" button on the Radmin banner. Opens (or focuses)
    /// the assistant overlay window — same window the auto-open path
    /// uses. Independent of the assistant mode so a power user who
    /// turned auto-open off can still summon the overlay when they
    /// genuinely need the tutorial.
    /// </summary>
    /// <summary>
    /// "Help connecting" in the Rooms toolbar — the assistant's entry point when the
    /// red banner is not on screen, which is exactly when Radmin is working and the
    /// banner has collapsed. Same destination as the banner's "Show steps"; two doors
    /// for two situations, not a duplicate.
    /// </summary>
    private void RadminHelpButton_Click(object sender, RoutedEventArgs e)
        => ShowRadminAssistant();

    private void RadminShowStepsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowRadminAssistant();
    }

    private async void RadminPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        var status = _lastRadminStatus ?? RadminVpnService.GetStatus();

        if (status.InstallState == RadminInstallState.NotInstalled)
        {
            await RunRadminAutoInstallAsync();
        }
        else
        {
            // Pre-load the AoE3 TAD network name into the clipboard so
            // the user only has to Ctrl+V into Radmin's "Join network"
            // dialog instead of typing 38 characters of mixed case.
            // Clipboard access can fail under restricted RDP / locked
            // workstation sessions; swallow + log so the launcher still
            // lifts the GUI window.
            try { Clipboard.SetText(RadminVpnService.AoE3TadNetworkName); }
            catch (Exception ex) { DiagnosticLog.Write($"RadminPrimaryButton_Click: clipboard: {ex.Message}"); }

            if (!string.IsNullOrEmpty(status.ExePath))
            {
                var launched = RadminVpnService.LaunchGui(status.ExePath);
                if (!launched)
                {
                    await MpAlertOverlay.NoticeAsync(
                        TabRootGrid,
                        Strings.Get("MpNoticeRadminLaunchTitle"),
                        Strings.Get("MpRadminLaunchFailed"),
                        Strings.Get("MpAlertOk"));
                }
            }
            // Immediate refresh — the new connection state will only
            // show once the user actually clicks Join inside Radmin,
            // but if Radmin was already connected and the launcher
            // just opened the window, the banner should still tick
            // visibly so the click feels responsive.
            RefreshRadminBanner();
        }
    }

    /// <summary>
    /// Download Famatech's MSI and run a silent install, with a
    /// progress label in the banner body. UAC fires once because the
    /// MSI installs a system service + TAP driver. On any failure
    /// we degrade gracefully to opening the download page in the
    /// browser so the user still has a path forward.
    /// </summary>
    /// <summary>
    /// Warn before the Radmin MSI when the disk is too tight. The requirement is the download
    /// PLUS a fixed allowance for what msiexec expands into Program Files — normally the same
    /// volume as <c>%TEMP%</c>, and <see cref="DiskSpaceService.Check"/> adds the two
    /// requirements rather than comparing them apart when that is the case.
    /// </summary>
    private bool ConfirmRadminSpaceOk(long msiBytes)
    {
        var required = Math.Max(0, msiBytes) + DiskSpaceService.RadminInstallAllowanceBytes;
        var shortfall = DiskSpaceService.Check(
            System.IO.Path.GetTempPath(), required, tempPath: null, tempRequired: 0);
        return DiskSpacePrompt.ConfirmOrCancel(
            Window.GetWindow(this), shortfall, "DiskSpaceConfirmDownloadBody");
    }

    private async Task RunRadminAutoInstallAsync()
    {
        if (RadminBanner == null) return;
        RadminPrimaryButton.IsEnabled = false;

        var progress = new Progress<int>(p =>
        {
            RadminBannerBody.Text = string.Format(Strings.Get("MpRadminInstalling"), p);
        });

        bool ok;
        try
        {
            ok = await RadminVpnService.InstallSilentAsync(
                progress, CancellationToken.None, confirmSpace: ConfirmRadminSpaceOk);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"MultiplayerTab.RunRadminAutoInstallAsync: {ex.Message}");
            ok = false;
        }

        if (!ok)
        {
            RadminBannerBody.Text = Strings.Get("MpRadminInstallFailed");
            RadminVpnService.OpenDownloadPageInBrowser();
        }

        // One immediate refresh to flip the banner to "installed but
        // not connected" if msiexec succeeded. The 3 s timer will keep
        // it honest if the user proceeds to join a network manually.
        RefreshRadminBanner();
    }

    /// <summary>
    /// Start the 3-second poll. Called from the IsVisible hook so we
    /// don't burn CPU enumerating NICs while the user is on another
    /// tab. Idempotent — calling twice is safe.
    /// </summary>
    private void StartRadminPolling()
    {
        if (_radminTimer == null)
        {
            _radminTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3),
            };
            _radminTimer.Tick += (_, _) => RefreshRadminBanner();
        }
        RefreshRadminBanner();   // one-shot so the user sees fresh data immediately
        _radminTimer.Start();

        // First-time visit to the Multiplayer tab in this session →
        // maybe pop the Radmin assistant overlay. Gated by config so
        // the user can opt out (Mode=Never), one-shot dismiss
        // (RadminAssistantSkipped), or already-connected detection
        // (we don't pop the overlay when they're past LoggedIn —
        // that means everything is working).
        MaybeAutoOpenAssistant();
    }

    /// <summary>
    /// Decide whether to auto-open the Radmin assistant overlay this
    /// session. Three gates:
    ///   1. Mode != "Never" — user explicitly disabled the assistant
    ///   2. !RadminAssistantSkipped — user previously ticked "don't
    ///      show again"
    ///   3. Stage &lt; LoggedIn — already signed in to Radmin? skip
    ///      auto-open since the user clearly knows what they're
    ///      doing (the "Show steps" button stays available if they
    ///      want it).
    /// Also guarded by _radminAssistantAutoOpenedThisSession so
    /// repeated tab switches don't keep re-opening the overlay —
    /// once auto-opened (or once we decided to skip), we stay quiet
    /// for the rest of the session.
    /// </summary>
    private async void MaybeAutoOpenAssistant()
    {
        if (_radminAssistantAutoOpenedThisSession) return;
        _radminAssistantAutoOpenedThisSession = true;

        if (_config == null) return;
        if (string.Equals(_config.RadminAssistantMode, "Never", StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(_config.RadminAssistantMode, "OnRequest", StringComparison.OrdinalIgnoreCase)) return;
        if (_config.RadminAssistantSkipped) return;

        try
        {
            var snap = await RadminAssistantService.ProbeAsync();
            // Skip auto-open if the user is already past LoggedIn —
            // they don't need a tutorial for something that's working.
            // Future: when seed-peer ping ships, this also catches
            // InAoE3Network → nothing to teach.
            if (snap.Stage >= RadminStage.LoggedIn) return;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"MultiplayerTab.MaybeAutoOpenAssistant: probe failed: {ex.Message}");
            // Probe failure → don't auto-open. Better to stay quiet
            // than to flash a confused overlay.
            return;
        }

        // The ONLY caller that passes autoOpened. Reaching here means Radmin was
        // below LoggedIn (the guard above), so this window is a tutorial we pushed
        // — it earns the right to close itself once the user gets to the network.
        ShowRadminAssistant(autoOpened: true);
    }

    /// <summary>
    /// Open (or focus) the Radmin assistant overlay. Single-instance:
    /// a second click brings the existing window to front instead of
    /// opening a duplicate that would race the first one on the 3s
    /// poll timer.
    ///
    /// <paramref name="autoOpened"/> is true ONLY for the launcher's own
    /// auto-open path; it is what lets the window close itself once the
    /// checklist goes green. Defaults to false so every user-initiated
    /// entry point stays open until the user closes it.
    /// </summary>
    private void ShowRadminAssistant(bool autoOpened = false)
    {
        if (_config == null) return;
        if (_radminAssistantWindow != null)
        {
            try
            {
                _radminAssistantWindow.Activate();
                if (_radminAssistantWindow.WindowState == WindowState.Minimized)
                    _radminAssistantWindow.WindowState = WindowState.Normal;
                return;
            }
            catch
            {
                // Previous window was disposed without our Closed
                // hook firing (rare — usually means the dispatcher
                // queue ate the event). Fall through to recreate.
                _radminAssistantWindow = null;
            }
        }

        var win = new RadminAssistantWindow(_config, autoOpened);
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_radminAssistantWindow, win))
                _radminAssistantWindow = null;
        };
        _radminAssistantWindow = win;
        // Owner = the main window so the overlay sits above it but
        // stops appearing in the taskbar (ShowInTaskbar=false in
        // XAML), and so closing the main launcher also closes the
        // overlay. Wrapped in try because Window.GetWindow returns
        // null in some unit-test paths.
        try
        {
            var owner = Window.GetWindow(this);
            if (owner != null) win.Owner = owner;
        }
        catch { /* fall through to ownerless */ }
        win.Show();
    }

    /// <summary>
    /// Wires the control to its dependencies. Called once from
    /// MainWindow after the session is constructed. The
    /// <paramref name="computeModFingerprint"/> callback hashes the
    /// currently-installed mod files using <see cref="ModHashService"/>
    /// and returns the combined hash — kept as a callback so the heavy
    /// I/O lives on the host's thread pool instead of behind this
    /// UserControl.
    /// </summary>
    public void Attach(
        MultiplayerSession session,
        Func<ModProfile?> getActiveProfile,
        Func<ModProfile, Task<string>> computeModFingerprint,
        Func<ModProfile, EventHandler, string?, Services.WatchedLaunch>? launchGame = null,
        Func<ModProfile, bool>? switchActiveMod = null,
        LauncherConfig? config = null,
        Func<string, Task>? switchActiveCopy = null,
        Action<AppToast.ToastOptions>? showAppToast = null,
        Action<string, string, string, string, string>? onNewRoomFromWs = null,
        Action<MatchRatedNotice>? onMatchRated = null,
        Action<string>? onLauncherTooOld = null,
        Action<string?, string?>? setConnectionChip = null,
        Action<string?, string?, string?>? setAccountChip = null)
    {
        _setConnectionChip = setConnectionChip;
        _setAccountChip = setAccountChip;
        if (_session != null)
        {
            _session.StateChanged -= OnSessionStateChanged;
            // Drop the old session's global chat socket before rebinding.
            CloseGlobalChat();
        }

        _session = session;
        _getActiveProfile = getActiveProfile;
        _computeModFingerprint = computeModFingerprint;
        _launchGame = launchGame;
        _switchActiveMod = switchActiveMod;
        _switchActiveCopy = switchActiveCopy;
        _showAppToast = showAppToast;
        _onNewRoomFromWs = onNewRoomFromWs;
        _onMatchRated = onMatchRated;
        _onLauncherTooOld = onLauncherTooOld;
        // Optional so old callers (and the parameterless ctor path
        // used by XAML preview) still work — null _config just means
        // the Radmin assistant features stay dormant.
        _config = config;
        session.StateChanged += OnSessionStateChanged;

        RefreshFromSession();

        // Fire-and-forget probes that don't hit the Worker (cheap, no
        // budget cost). The expensive ones — RefreshQuotaAsync /
        // RefreshRoomsListAsync — are gated below so they only run
        // when the user is actually looking at the Multiplayer tab.
        // No async bootstrap to fire anymore — the game-network layer
        // (Radmin VPN) is user-managed, and its state now surfaces in the
        // title-bar chip rather than a badge in this header.

        // Auto-refresh the quota bar every 60 s. Only ticks while
        // the control is *visible* — when the user switches to
        // Play / Mods / News / Settings, the IsVisibleChanged hook
        // stops the timer so we don't burn ~60 Worker requests/hour
        // on the launcher just being open in another tab. Resumes
        // when they come back to Multiplayer.
        _quotaTimer?.Stop();
        _quotaTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60),
        };
        _quotaTimer.Tick += async (_, _) => await RefreshQuotaAsync();

        // Subscribe once. Multiple calls to Attach are guarded by
        // the unsubscribe step on the previous session above, but
        // IsVisibleChanged on this control is process-lifetime, so
        // we unsubscribe-then-resubscribe to keep the count at 1.
        IsVisibleChanged -= OnVisibleChangedTabGate;
        IsVisibleChanged += OnVisibleChangedTabGate;

        // Initial state: kick off the cheap fetches + the timer only
        // when we're already the visible tab (e.g. user launched the
        // app with Multiplayer as last-active-tab). Otherwise the
        // IsVisibleChanged handler will pick it up when the user
        // navigates here.
        if (IsVisible)
        {
            StartQuotaPolling();
            StartRadminPolling();
        }

        // Connect the global chat / presence socket NOW if we're already signed
        // in (e.g. the cached JWT was still valid at startup), regardless of
        // which tab is visible — this is what makes the user appear connected in
        // the background. If sign-in completes later, OnSessionStateChanged
        // calls SyncGlobalChat again. Idempotent (self-gates on SignedIn).
        SyncGlobalChat();

        // Chat is the panel's default — it is the half people watch, and the one the
        // reference shows selected.
        ShowPanelTab(players: false);
    }

    /// <summary>
    /// Toggles the quota timer + initial fetches when this control's
    /// Visibility flips. Switching to the Multiplayer tab → fetch a
    /// fresh quota + lobbies snapshot and start the 60 s poll.
    /// Switching away → stop the poll so the launcher stops burning
    /// Worker requests while the user is reading the news or
    /// fiddling with settings.
    /// </summary>
    private void OnVisibleChangedTabGate(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            StartQuotaPolling();
            StartRadminPolling();
        }
        else
        {
            _quotaTimer?.Stop();
            _radminTimer?.Stop();
            _roomsPingTimer?.Stop();
            _roomsListTimer?.Stop();
            // NOTE: the global chat / presence socket is intentionally NOT
            // closed here. It stays open while signed in (see SyncGlobalChat)
            // so the user keeps appearing "connected" in the background — that
            // is the whole point of the presence feature. Only the polling
            // timers above are visibility-gated.
        }
    }

    private void StartQuotaPolling()
    {
        // One-shot fetch on activation so the user sees fresh data
        // immediately (otherwise they'd wait up to 60 s for the
        // first timer tick after switching to this tab).
        _ = RefreshQuotaAsync();
        if (_session?.Status == MultiplayerSession.SessionStatus.SignedIn)
        {
            _ = RefreshRoomsListAsync();
            // THE ACTIVATION EDGE THE STRIP NEVER HAD. Its only two callers were the subtab
            // CLICK handlers, and _activeSubtab starts on Rooms — so whoever opened the tab
            // was already on the subtab they would have had to press, and the strip sat
            // Collapsed until an accidental click. Reported as "it takes minutes to appear",
            // and that is exactly what it was: the wait for that click. Free on re-entry,
            // because RefreshActivityStripAsync self-limits on its 60-second window.
            _ = RefreshActivityStripAsync();
        }
        _quotaTimer?.Start();

        // Keep the rooms-browser PING (your connection latency) fresh every
        // ~3 s while the tab is visible, updating the cells in place.
        _roomsPingTimer?.Stop();
        _roomsPingTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _roomsPingTimer.Tick += (_, _) => { KickConnectionPing(); RefreshRoomPingCells(); RefreshRoomAgeCells(); RefreshActivityAgeCells(); UpdateRoomsUpdatedLabel(); };
        _roomsPingTimer.Start();
        KickConnectionPing();

        // Auto-refresh the rooms LIST every ~5 s so newly-created rooms
        // appear quickly without the user pressing Actualizar. The fetch is a
        // quiet, diff-based render (no "loading" skeleton, no row/Join-button
        // rebuild when nothing changed — see RefreshRoomsListAsync(quiet:true))
        // and only fires while signed in AND on the Rooms subtab, so it costs
        // at most one cheap GET /lobbies every 5 s while the user is actually
        // browsing: 12 req/min (under the 60/min per-IP cap) and ~720/h — the
        // daily 2000/IP cap is only approached after hours of continuous
        // browsing, which is acceptable for a fresher list.
        _roomsListTimer?.Stop();
        _roomsListTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _roomsListTimer.Tick += (_, _) =>
        {
            if (_session?.Status != MultiplayerSession.SessionStatus.SignedIn
                || _activeSubtab != Subtab.Rooms) return;

            _ = RefreshRoomsListAsync(quiet: true);

            // The community strip rides this SAME tick instead of getting a timer of its own,
            // and the cadence comes from RefreshActivityStripAsync's own 60-second window: it
            // is asked every 5 s and actually fetches once a minute. A second timer would be a
            // second cadence, free to drift from the one the method already enforces. The gates
            // above are also exactly the ones it needs — the strip lives inside RoomsPageScroll,
            // on this very subtab — and a copy of them is a copy that can go out of step.
            //
            // THE FOREGROUND CHECK IS WHAT PAYS FOR IT, and it is not politeness. /stats/community
            // allows 30/min and 2000/DAY per IP, and a 60-second poll with the tab left open is
            // 1440 a day from one launcher — two of them behind one address (a house with two
            // PCs, or a CGNAT, which is common for this player base) exceed the daily cap, and
            // the server's own 60 s memo does not help because the quota is counted before the
            // cache is consulted. Minimising to the taskbar does NOT stop these timers (only
            // closing to the tray does), so "left open all day" is the ordinary case, not the
            // odd one. Nobody needs live data for a window they are not looking at; with this,
            // an hour of actually watching costs 60 requests and the daily cap is unreachable.
            if (Application.Current?.MainWindow?.IsActive == true)
                _ = RefreshActivityStripAsync();
        };
        _roomsListTimer.Start();

        // Ensure the presence socket is up (idempotent). It's now always-on
        // while signed in — NOT tied to this tab being visible — so this call is
        // just a belt-and-suspenders refresh on tab activation.
        SyncGlobalChat();
    }

    public void RefreshStrings() => ApplyStrings();

    private void ApplyStrings()
    {
        SubtabRooms.Content = Strings.Get("MpSubtabRooms");
        SubtabTournaments.Content = Strings.Get("MpSubtabTournaments");
        SubtabRanking.Content = Strings.Get("MpSubtabRanking");
        // It had none: declared in XAML with no Content and never assigned here, so the pill
        // rendered blank — clickable and anonymous — for as long as the subtab has existed.
        SubtabStats.Content = Strings.Get("MpSubtabStats");
        RankingModeSolo.Content = Strings.Get("MpRankingModeSolo");
        RankingModeTeam.Content = Strings.Get("MpRankingModeTeam");

        // Radmin assistant "Show steps" button. Hidden when the
        // user disabled the assistant entirely via Settings
        // (Mode=Never) — the legacy Open Radmin / Install button
        // (RadminPrimaryButton) stays for them.
        RadminShowStepsButton.Content = Strings.Get("RadAsstBannerShowSteps");
        var mode = _config?.RadminAssistantMode;
        var assistantOff = string.Equals(mode, "Never", StringComparison.OrdinalIgnoreCase);
        RadminShowStepsButton.Visibility = assistantOff ? Visibility.Collapsed : Visibility.Visible;

        // The toolbar door follows the SAME gate. That setting is called "Never" and
        // its own hint says the assistant is disabled — leaving a visible way in would
        // make the option a lie. (The header "?" this replaced ignored the mode, which
        // was one more reason it was the wrong place for it.)
        // Prefixed like its neighbours ("↻  Actualizar", "+  Crear sala") so the three
        // read as one row. A plain Unicode mark, not an emoji and not an icon font —
        // the house rule bans emoji in labels and this row deliberately avoids pulling
        // a glyph font. Note the "?" is a PREFIX to a word here, never the whole label:
        // on its own it was tried in the header and said help existed without ever
        // saying about what.
        RadminHelpButton.Content = "?  " + Strings.Get("MpRoomsRadminHelp");
        RadminHelpButton.ToolTip = TooltipHelper.Wrap(Strings.Get("MpRoomsRadminHelpTooltip"));
        RadminHelpButton.Visibility = assistantOff ? Visibility.Collapsed : Visibility.Visible;

        SignInTitleText.Text = Strings.Get("MpSignInTitle");
        SignInBodyText.Text = Strings.Get("MpSignInBody");
        SignInButton.Content = Strings.Get("MpSignInButton");

        // Compose icon + label using inline runs so the look stays
        // close to the reference (small glyph + word). Plain content
        // strings would be fine too — we keep them simple to avoid
        // pulling icon fonts.
        RefreshButton.Content = "↻  " + Strings.Get("MpRoomsRefresh");
        CreateRoomButton.Content = "+  " + Strings.Get("MpRoomsCreate");
        RoomSearchPlaceholder.Text = Strings.Get("MpRoomsSearchPlaceholder");
        ActivityStripTitle.Text = Strings.Get("MpActivityStripTitle");
        // Both of these depend on data, so they are re-derived rather than assigned: the
        // totals carry the windows the SERVER looked back over, and the recent-matches
        // heading says whose matches these are — not the same on an older backend.
        FillCommunityTotals(_communityStats);
        ActivityRecentTitle.Text = Strings.Get(_activityRecentIsCommunity
            ? "MpActivityRecentCommunityTitle"
            : "MpActivityRecentTitle");
        ActivityRankingTitle.Text = Strings.Get("MpActivityRankingTitle");
        ActivityPeakTitle.Text = Strings.Get("MpActivityPeakTitle");
        // The strip's ranking lost its five-column header to the handoff's row shape; the
        // MpActivityRankCol* strings live on, because BuildRankingHeader still labels the
        // FULL table in the RANKING subtab with exactly those keys.
        ActivityRankingSeeAll.Content = Strings.Get("MpActivityRankingSeeAll");
        JoinByCodePlaceholder.Text = Strings.Get("MpJoinByCodePlaceholder");
        // The field moved into the 48-px toolbar, where the two sentences it used to show above
        // it do not fit. They are not dropped — they are its tooltip, from the very same keys,
        // so a 6-character box still says what it is for and why the room is not in the list.
        JoinByCodeBox.ToolTip = TooltipHelper.Wrap(
            Strings.Get("MpJoinByCodeTitle") + " " + Strings.Get("MpJoinByCodeHint"));
        // Icon-only now, so its caption is the tooltip.
        JoinByCodeButton.ToolTip = TooltipHelper.Wrap(Strings.Get("MpJoinByCodeButton"));

        // Active-rooms section title + global chat panel labels.
        RoomsSectionTitle.Text = Strings.Get("MpRoomsSectionTitle");
        GlobalChatHeaderText.Text = Strings.Get("MpGlobalChatTitle");
        // The pill's Content IS the text that lands in the box, so a language switch
        // has to reach them or the pills would keep filling in the old language.
        QuickReplyAnyone.Content = Strings.Get("MpQuickReplyAnyone");
        QuickReplyGg.Content = Strings.Get("MpQuickReplyGg");
        QuickReplyMinute.Content = Strings.Get("MpQuickReplyMinute");
        // The players tab's caption is normally rewritten with a live count by
        // RenderPlayersPanel; seed it here so it isn't blank before the first
        // presence frame, and so a language switch reaches it in the meantime.
        if (_globalOnlineUsers.Count == 0)
            PlayersPanelTitle.Text = Strings.Format("MpPlayersPanelTitle", 0);
        // The chat HISTORY is deliberately left alone. A system line is a record of something
        // that happened, stamped with the time it happened at; rewriting it in another language
        // would be rewriting the past. Only the furniture around it follows the switch.
        GlobalChatPlaceholder.Text = Strings.Get("MpGlobalChatPlaceholder");
        // Send is an icon button now — the localized caption lives on its ToolTip.
        GlobalChatSendButton.ToolTip = Strings.Get("MpGlobalChatSend");
        UpdateGlobalChatEmptyHint();

        // Lobby window labels — only updated if it's currently open.
        // Static labels go through ApplyLobbyStaticLabels(); the dynamic,
        // state-driven ones (status line, player count, ready toggle, …)
        // are refreshed by re-running RenderRoomPanel, so a mid-room
        // language switch re-localises the whole window at once.
        if (_lobbyWindow != null)
        {
            ApplyLobbyStaticLabels();
            RenderRoomPanel();
            // Re-localise the match-phase overlays too, so switching
            // language mid-countdown / mid-match refreshes the
            // cancel/leave button and in-game mode badge — not just
            // the lobby body.
            ApplyMatchPhaseUi();
            if (_matchPhase == MatchPhase.InGame)
                RefreshInGamePanel();
        }

        // The profile window, same arrangement: its own chrome through ApplyStrings, and the
        // page rebuilt because every string in it is read fresh by the builders.
        if (_profileWindow != null)
        {
            _profileWindow.ApplyStrings();
            RenderProfileTab();
        }

        // Room-list column headers (localized) + empty-state copy.
        ColHeaderRoom.Text = Strings.Get("MpColRoom");
        ColHeaderHost.Text = Strings.Get("MpColHost");
        ColHeaderPlayers.Text = Strings.Get("MpColPlayers");
        ColHeaderPing.Text = Strings.Get("MpColPing");
        UpdateSortArrows();
        EmptyTitleText.Text = Strings.Get("MpRoomsEmptyTitle");
        EmptyBodyText.Text = Strings.Get("MpRoomsEmptyBody");
        EmptyCreateButton.Content = "+  " + Strings.Get("MpRoomsCreate");
        UpdateRoomsUpdatedLabel();

        UpdateSubtabHighlights();
        UpdateConnectionStatus();

        // Ranking, History and Profile are built in code, so none of their text is reached by
        // the assignments above — a mid-session language change used to leave whichever of the
        // three was on screen in the old language until the user left the subtab and came back.
        // The lobby has always solved this by re-running its own render (below); these three
        // had no equivalent.
        RefreshActiveSubtabStrings();
    }

    /// <summary>
    /// Re-draws whichever of the code-built subtabs is showing, after a language change.
    ///
    /// <para>Only the visible one, and only from its cached data: none of these fetches
    /// anything, so switching language cannot cost a request.</para>
    ///
    /// <para><b>ALL FOUR. Rooms was missing, and it was the one you look at.</b> The community
    /// strip kept saying "Hay más gente entre las 18:00 y 21:00" under an English heading, and
    /// then fixed itself minutes later, which is the shape of a bug nobody can describe: the
    /// strip only repainted inside its own fetch, capped at one a minute and only while the
    /// window is in the foreground, so the language followed the POLL rather than the setting.
    /// The room rows were worse - the quiet refresh skips repainting when the list has not
    /// changed, so their chips would have stayed in the old language until somebody opened or
    /// closed a room.</para>
    ///
    /// <para>There is no <c>default</c> here on purpose - each page needs its own call - which
    /// also means a missing case says nothing at all. <c>EverySubtabIsCoveredWhenTheLanguage
    /// Changes</c> walks the enum so the next page cannot be forgotten the same way.</para>
    /// </summary>
    private void RefreshActiveSubtabStrings()
    {
        switch (_activeSubtab)
        {
            case Subtab.Rooms:
                // Both from memory. RerenderRoomsFromCache leaves the render signature alone
                // on purpose, so the next quiet poll can still skip its work.
                RenderActivityStrip();
                RerenderRoomsFromCache();
                break;
            case Subtab.Tournaments:
                RenderTournamentsTab();
                break;
            case Subtab.Ranking:
                RenderRanking();
                break;
            case Subtab.Stats:
                RenderStatsTab();
                break;
        }
    }

    private void OnSessionStateChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            SyncRoomSocketSubscription();
            RefreshFromSession();
            // Sign-in / sign-out flips whether the global chat should be
            // connected; entering/leaving a room is harmless (idempotent).
            SyncGlobalChat();
            // And the strip, because everything that asks for it requires SignedIn: a request
            // that lands during the sign-in window is dropped, and nothing used to try again.
            if (_session?.Status == MultiplayerSession.SessionStatus.SignedIn && IsVisible)
                _ = RefreshActivityStripAsync();
        });

    // ------------------------------------------------------------------------
    // Offline mode (driven by MainWindow from the app-wide ConnectivityState)
    // ------------------------------------------------------------------------

    private bool _offlineMode;
    private string _offlineNeedsInternet = "";
    private string _offlineNotice = "";

    /// <summary>
    /// Greys the multiplayer actions that need the network (sign-in, create room,
    /// refresh rooms) while the app is offline, and restores them on reconnect.
    /// Strings are passed in by MainWindow (from the localized keys). Multiplayer is
    /// inherently online, so this is the whole tab's "you can't do this offline" gate;
    /// the title-bar chip carries the global signal.
    /// </summary>
    public void SetOfflineMode(bool offline, string needsInternetTooltip, string offlineNotice)
    {
        _offlineMode = offline;
        _offlineNeedsInternet = needsInternetTooltip;
        _offlineNotice = offlineNotice;

        if (offline)
        {
            ApplyOfflineDisable();
        }
        else
        {
            // Back online: drop our tooltips and let the session logic recompute the
            // correct enabled states (don't force IsEnabled=true on a button that
            // should stay disabled for another reason, e.g. CreateRoom while signed
            // out). RefreshFromSession is the single source of those states.
            if (SignInButton != null) SignInButton.ToolTip = null;
            if (RefreshButton != null) { RefreshButton.IsEnabled = true; RefreshButton.ToolTip = null; }
            if (CreateRoomButton != null) CreateRoomButton.ToolTip = null;
            // The code field is the exception to "let RefreshFromSession recompute it": nothing
            // there touches these two, so leaving them to it would strand them disabled for the
            // rest of the session. Its own tooltip comes back (not null — it explains what the
            // field is for), and the button's enabled state is whatever the box's contents say,
            // which is the same rule JoinByCodeBox_TextChanged applies.
            if (JoinByCodeBox != null)
            {
                JoinByCodeBox.IsEnabled = true;
                JoinByCodeBox.ToolTip = TooltipHelper.Wrap(
                    Strings.Get("MpJoinByCodeTitle") + " " + Strings.Get("MpJoinByCodeHint"));
            }
            if (JoinByCodeButton != null)
            {
                JoinByCodeButton.IsEnabled = (JoinByCodeBox?.Text ?? "").Trim().Length > 0;
                JoinByCodeButton.ToolTip = TooltipHelper.Wrap(Strings.Get("MpJoinByCodeButton"));
            }
            RefreshFromSession();
        }
    }

    /// <summary>
    /// Force-disables the online action buttons and explains why. Re-applied at the
    /// end of <see cref="RefreshFromSession"/> so a session refresh can't silently
    /// re-enable them while we're still offline.
    /// </summary>
    private void ApplyOfflineDisable()
    {
        if (SignInButton != null) { SignInButton.IsEnabled = false; SignInButton.ToolTip = _offlineNotice; }
        if (RefreshButton != null) { RefreshButton.IsEnabled = false; RefreshButton.ToolTip = _offlineNeedsInternet; }
        if (CreateRoomButton != null) { CreateRoomButton.IsEnabled = false; CreateRoomButton.ToolTip = _offlineNeedsInternet; }
        // Joining by code needs the backend exactly as much as its two neighbours do. It was
        // left out while it lived in a panel of its own further down the page; sitting in the
        // same cluster, one live control among greyed ones would read as the offline state
        // being wrong rather than as a field that will fail when pressed.
        if (JoinByCodeBox != null) { JoinByCodeBox.IsEnabled = false; JoinByCodeBox.ToolTip = _offlineNeedsInternet; }
        if (JoinByCodeButton != null) { JoinByCodeButton.IsEnabled = false; JoinByCodeButton.ToolTip = _offlineNeedsInternet; }
    }

    /// <summary>
    /// Compares <c>_attachedSocket</c> with the session's current
    /// <see cref="MultiplayerSession.RoomSocket"/> and (un)subscribes
    /// to match. Called every time the session state changes — joining
    /// a lobby sets a new socket; leaving sets it to null. Idempotent.
    /// </summary>
    private void SyncRoomSocketSubscription()
    {
        var s = _session;
        var nextSocket = s?.RoomSocket;

        var socketChanged = !ReferenceEquals(_attachedSocket, nextSocket);
        if (!socketChanged) return;

        if (socketChanged)
        {
            if (_attachedSocket != null)
            {
                _attachedSocket.FrameReceived -= OnRoomFrame;
                _attachedSocket.Disconnected -= OnRoomDisconnected;
                _attachedSocket.Reconnecting -= OnRoomReconnecting;
            }
            _attachedSocket = nextSocket;
            // Detaching a socket always means "we're no longer in
            // an active room" — reset the reconnect flag so the
            // status pill goes back to plain Connected, and clear
            // the room-mod cache so a stale value doesn't drive a
            // future LaunchActiveModGame.
            _isReconnecting = false;
            if (nextSocket == null)
            {
                _currentLobbyModId = null;
                _currentLobbyMaxPlayers = 0;
                _currentLobbyIsPrivate = null;
                _currentLobbyIsCompetitive = false;
                _currentLobbySpectatorSlots = 0;
                _currentLobbyTournamentMatchId = null;
                _currentLobbyCreatedUtc = null;
                // We are out of the room, so it can no longer be "in a match" as far as we're
                // concerned — this is what takes the reopen button away.
                _roomMatchLive = false;
                // The match roster is deliberately NOT cleared here any more. Clearing it was
                // what silently unreported a real match: this branch runs when the host closes
                // the room, and it KILLS the game two lines below — so the OnGameExited it
                // causes arrived to find the participants gone and skipped itself as "not
                // host". _matchContext is owned by the exit handler now, which is the only
                // place that knows the match is actually over.
            }
            UpdateConnectionStatus();
            // Also reset the match-phase machinery. If we somehow
            // exit a room with an active game (forced disconnect,
            // host left), tear down the local AoE3 process and
            // unlock the popup chrome.
            // Off the UI thread — this runs on a UI callback and the kill confirms with a
            // WaitForExit, so it must not block the dispatcher.
            var leavingGame = _aoe3Process;
            if (leavingGame != null)
                _ = Task.Run(() => Services.GameProcessCloser.Stop(leavingGame, killEntireTree: true));
            ExitInGamePhase();
            // Lobby window position used to need re-centering here for
            // the in-tab popup. The real Window we use now remembers
            // its own position between opens via OS chrome, so there's
            // nothing to reset.
        }

        // Reset per-room UI state whenever we change rooms.
        if (socketChanged)
        {
            _roomMembers.Clear();
            _roomHostUserId = null;
            _isHostInCurrentRoom = false;
            if (_lobbyWindow != null)
            {
                _lobbyWindow.ChatLogPanel.Children.Clear();
                _lobbyWindow.RoomMembersPanel.Children.Clear();
                UpdateChatEmptyState();
            }
            // Fresh room → fresh chat replay cursor. Otherwise the
            // first room_state of the new room would skip lines whose
            // atMs happens to be smaller than the last one we saw in
            // the previous room.
            _highestSeenChatAtMs = 0;

            // Seed the members map with the local user before any
            // server frame arrives. Without this, a brief delay or
            // a tunnel-side WS hiccup leaves the Players panel
            // completely empty — confusing because the user clearly
            // IS in a room. The real room_state frame from the DO
            // will overwrite this with the authoritative list as
            // soon as it lands.
            var me = _session?.CurrentUser;
            if (me != null && nextSocket != null)
            {
                _roomMembers[me.Id] = new RoomMemberEntry
                {
                    UserId = me.Id,
                    Login = string.IsNullOrEmpty(me.DiscordUsername) ? me.DisplayName : me.DiscordUsername,
                    Ready = false,
                };
                RenderRoomMembers();
            }
        }

        if (socketChanged && nextSocket != null)
        {
            nextSocket.FrameReceived += OnRoomFrame;
            nextSocket.Disconnected += OnRoomDisconnected;
            nextSocket.Reconnecting += OnRoomReconnecting;
        }
    }

    /// <summary>
    /// The socket close the backend sends when a reported match closes its room
    /// (<c>rooms.close(lobby_id, 4007, 'match_reported')</c>).
    /// </summary>
    private const string RoomClosedByReport = "server_close:4007";

    /// <summary>
    /// The socket close the backend sends when this build is below its minimum version.
    ///
    /// <para>It must be handled BEFORE the generic disconnect path, or the launcher would
    /// retry forever against a server that is never going to accept it — showing "reconnecting"
    /// instead of the one thing the player needs to know.</para>
    /// </summary>
    private const string RoomClosedTooOld = "server_close:4010";

    /// <summary>
    /// The backend closes with these when the room is simply GONE — deleted after a reported
    /// match (<c>4404 lobby_not_found</c>) or closed outright (<c>4006 lobby_closed</c>).
    ///
    /// <para>They have to stop the reconnect, and not merely because retrying is pointless.
    /// <see cref="LobbyWebSocket"/> resets its backoff on a connection that ESTABLISHES, and
    /// these close immediately after the upgrade succeeds — so the exponential backoff never
    /// grows past its first step. A real client retried a deleted room about two hundred times
    /// over five minutes, roughly once a second, and only stopped because the player gave up and
    /// closed the window.</para>
    /// </summary>
    private const string RoomClosedGone = "server_close:4404";
    private const string RoomClosedByServer = "server_close:4006";

    private void OnRoomDisconnected(object? sender, string reason) =>
        Dispatcher.InvokeAsync(() =>
        {
            // A room closed BECAUSE the match was reported is not a dropped connection,
            // and treating it as one is what produced the zombie lobby window: the socket
            // retried forever while the room no longer existed. This is the only signal a
            // NON-host gets — no frame carries the result — so both sides of the match
            // reach the result phase through the same line.
            //
            // Gated on having been in a match: 4007 is also the kick code, and a kick has
            // already closed the window through its own frame by the time this arrives.
            if (reason == RoomClosedByReport
                && (_matchPhase == MatchPhase.InGame || ResultContext() != null))
            {
                // Not if the match_reported frame already got here. It arrives just
                // before this close — the server publishes it and then shuts the sockets
                // — and without this guard we would enter the result phase a second time
                // and fire the history polls that the frame exists to make unnecessary.
                if (_matchPhase != MatchPhase.Result) EnterResultPhase();
                return;
            }

            // The room no longer exists. Retrying cannot bring it back, and the retry does not
            // slow down on its own — see RoomClosedGone. If we were waiting on a result, it is
            // not coming down this socket, so say so rather than leaving the card promising.
            if (reason == RoomClosedGone || reason == RoomClosedByServer)
            {
                DiagnosticLog.Write(
                    $"Room socket closed for good ({reason}) — not reconnecting.");
                try { _session?.RoomSocket?.StopReconnect(); }
                catch (Exception ex) { DiagnosticLog.Write($"StopReconnect — {ex.Message}"); }
                if (_matchPhase == MatchPhase.AwaitingResult) FinishWaitingUnresolved();
                return;
            }

            // Refused for being out of date: retrying cannot help, so stop reconnecting and
            // say why. The server sends no min_version over the socket, so the message is the
            // version-less one — the REST path names it when it can.
            if (reason == RoomClosedTooOld)
            {
                _session?.RoomSocket?.StopReconnect();
                _ = ShowLauncherTooOldAsync(null);
                return;
            }

            // Connection-state events used to spam the room chat
            // log, which the redesign brief explicitly calls out as
            // wrong — they're now routed to the global chat bar at
            // the bottom AND drive the status pill at the top.
            _isReconnecting = true;
            UpdateConnectionStatus();
            AppendGlobalSystemEvent($"Disconnected: {reason}. Reconnecting…");
        });

    private void OnRoomReconnecting(object? sender, string nextAttempt) =>
        Dispatcher.InvokeAsync(() =>
        {
            _isReconnecting = true;
            UpdateConnectionStatus();
            AppendGlobalSystemEvent($"Reconnecting… ({nextAttempt})");
        });

    /// <summary>
    /// Frame router. Every type from the Worker's LobbyRoom DO arrives
    /// here; we deserialise the slice we care about and update local
    /// state + UI. Marshals back to the UI thread because
    /// <see cref="LobbyWebSocket.FrameReceived"/> fires from the
    /// background receive loop.
    /// </summary>
    private void OnRoomFrame(object? sender, LobbyWebSocket.FrameReceivedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                switch (e.Type)
                {
                    case "room_state":
                        HandleRoomState(e.Json);
                        break;
                    case "chat":
                        HandleChat(e.Json);
                        break;
                    case "member_joined":
                        HandleMemberJoined(e.Json);
                        break;
                    case "match_reported":
                        HandleMatchReported(e.Json);
                        break;
                    case "member_left":
                        HandleMemberLeft(e.Json);
                        break;
                    case "member_ready":
                        HandleMemberReady(e.Json);
                        break;
                    case "host_changed":
                        HandleHostChanged(e.Json);
                        break;
                    case "member_net":
                        HandleMemberNet(e.Json);
                        break;
                    case "member_ingame_name":
                        HandleMemberInGameName(e.Json);
                        break;
                    case "kicked":
                        HandleKicked();
                        break;
                    case "room_renamed":
                        HandleRoomRenamed(e.Json);
                        break;
                    case "game_countdown":
                    {
                        // Host pressed Start — server broadcasts the
                        // canonical countdown duration. Switch popup
                        // into Starting phase and run a purely-local
                        // countdown timer (no dependence on absolute
                        // server timestamps, which would let clock
                        // skew skip the wait entirely on a host with
                        // a fast-running clock).
                        var durationMs = e.Json.TryGetProperty("duration_ms", out var dm)
                            && dm.ValueKind == System.Text.Json.JsonValueKind.Number
                                ? dm.GetInt32()
                                : 10000;
                        // OBEY the server's duration (backend LobbyRoom.COUNTDOWN_MS) —
                        // no launcher-side floor, so redeploying the backend to 5000
                        // makes the countdown 5 s automatically. StartCountdown applies
                        // its own small sanity floor. The 10000 default only covers a
                        // malformed frame with no duration_ms (the backend always sends it).
                        StartCountdown(durationMs);
                        AppendChatSystem(Strings.Format("MpChatGameStartingIn", durationMs / 1000));
                        break;
                    }
                    case "game_started":
                        // Legacy-compat path: this frame is broadcast
                        // alongside `game_countdown` for clients that
                        // don't know about the countdown protocol. The
                        // CURRENT launcher routes the actual launch
                        // through the countdown timer's expiry (see
                        // UpdateCountdownTick) so we only honour
                        // game_started when we're still in the bare
                        // Lobby phase — meaning the host pressed Start
                        // on a server old enough not to emit the
                        // countdown frame. In Starting / InGame phase
                        // we ignore game_started (the countdown handles
                        // the launch, or we're already running).
                        if (_matchPhase == MatchPhase.Lobby)
                        {
                            AppendChatSystem(Strings.Get("MpChatGameStarted"));
                            var process = LaunchActiveModGame();
                            EnterInGamePhase(process);
                        }
                        RefreshFromSession();
                        break;
                    case "game_cancelled":
                    {
                        var reason = e.Json.TryGetProperty("reason", out var r)
                            ? (r.GetString() ?? "host_cancelled")
                            : "host_cancelled";
                        AppendChatSystem(reason switch
                        {
                            "host_cancelled" or "aborted" => Strings.Get("MpChatGameAborted"),
                            "ended" => Strings.Get("MpChatHostEndedMatch"),
                            _ => Strings.Format("MpChatGameCancelledReason", reason),
                        });
                        // Whatever the reason — aborted, cancelled, or the host reporting that
                        // the game ended — the server has put the room back to 'open', so it is
                        // no longer in a match and the reopen button must go away. This frame is
                        // broadcast to everyone including whoever aborted, so it is the one place
                        // that covers every ending except the host's own game_ended (the sender
                        // is excluded from that one, and clears the flag itself).
                        _roomMatchLive = false;

                        // Kill local AoE3 if running and exit the
                        // InGame phase. We don't send a follow-up
                        // frame back — the server already cleared
                        // the lobby state in its UPDATE.
                        var cancelledGame = _aoe3Process;
                        if (cancelledGame != null)
                            _ = Task.Run(() => Services.GameProcessCloser.Stop(
                                cancelledGame, killEntireTree: true));
                        ExitInGamePhase();
                        RefreshFromSession();
                        break;
                    }
                    case "error":
                        var code = e.Json.TryGetProperty("code", out var c) ? c.GetString() : "";
                        var msg = e.Json.TryGetProperty("message", out var m) ? m.GetString() : "";
                        // The abort grace window closed — surface a friendly,
                        // localized note instead of the raw English server text.
                        if (code == "grace_window_closed")
                            AppendChatSystem(Strings.Get("MpChatAbortWindowClosed"));
                        // Rename rejections: the dialog already mirrors the
                        // length rule, so these are the race/spam cases.
                        else if (code == "bad_title" || code == "rename_too_fast")
                            AppendChatSystem(Strings.Get("MpRenameFailed"));
                        else
                            AppendChatSystem($"[{code}] {msg}");
                        break;
                    // "pong" intentionally swallowed.
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"MultiplayerTab.OnRoomFrame ({e.Type}): {ex.Message}");
            }
        });
    }

    private void HandleRoomState(JsonElement json)
    {
        // Receiving a room_state means the socket is alive — clear
        // the "Reconnecting…" pill if it was set. Cheap to do here
        // and keeps the status indicator in sync with reality
        // without polling the WS object directly.
        if (_isReconnecting)
        {
            _isReconnecting = false;
            UpdateConnectionStatus();
            AppendGlobalSystemEvent("Reconnected to multiplayer server.");
        }

        var state = JsonSerializer.Deserialize<WsRoomState>(json.GetRawText());
        if (state == null) return;

        _roomMembers.Clear();
        foreach (var kv in state.Members)
        {
            var memberLogin = string.IsNullOrEmpty(kv.Value.Login) ? kv.Key : kv.Value.Login;
            _roomMembers[kv.Key] = new RoomMemberEntry
            {
                UserId = kv.Key,
                // Prefer the server-provided login; fall back to the
                // user id for legacy rooms that don't carry it yet.
                Login = memberLogin,
                Ready = kv.Value.Ready,
                RadminIp = kv.Value.RadminIp,
                InGameName = kv.Value.InGameName,
                AvatarUrl = kv.Value.AvatarUrl,
                Rating = kv.Value.Rating,
                Rd = kv.Value.Rd,
            };
        }

        // The n2n edge bring-up is owned by MultiplayerSession.OnFrame —
        // it sees the same room_state snapshot we do and uses the
        // sorted member list to derive each peer's slot index + virtual
        // IP deterministically. No extra signaling from this tab.
        _roomHostUserId = state.HostUserId;
        _isHostInCurrentRoom = !string.IsNullOrEmpty(_session?.CurrentUser?.Id)
            && string.Equals(_roomHostUserId, _session!.CurrentUser!.Id, StringComparison.Ordinal);

        // Replay the server-buffered chat WITHOUT wiping local lines.
        // Why: room_state fires on every WS reconnect (auto-reconnect
        // backoff). If the user typed a message right before a brief
        // tunnel hiccup, the server's chatRing might not contain that
        // message yet (the send arrived after the reconnect snapshot),
        // and the old "clear + replay" path would erase the message
        // the user JUST saw appear as a local echo. The bug we shipped
        // looked exactly like "I type, the message flashes, then it
        // disappears".
        //
        // New behaviour: only append lines whose atMs is newer than the
        // newest one we already have rendered. The local echo uses
        // DateTime.Now so its effective atMs is "now" — server lines
        // for that same message will carry a slightly different atMs
        // and may produce one duplicate, which is an acceptable cost
        // (the alternative was making messages vanish). For the
        // initial connect (chat panel empty) this still replays the
        // entire ring exactly once.
        ReplayChatRing(state.Chat);

        RenderRoomMembers();
        RenderRoomPanel();
        MaybeAutoStartOnAllReady();
    }

    /// <summary>
    /// Append any chat lines from the server's ring buffer that we
    /// haven't shown yet, in chronological order. Idempotent across
    /// repeated calls — re-running with the same ring is a no-op.
    /// </summary>
    private void ReplayChatRing(System.Collections.Generic.IEnumerable<WsChatLine> ring)
    {
        if (_lobbyWindow == null) return;
        foreach (var line in ring)
        {
            if (line == null) continue;
            if (line.AtMs <= _highestSeenChatAtMs) continue;
            AppendChatLine(line);
        }
    }

    /// <summary>
    /// Cursor for the chat-replay dedup. AppendChatLine bumps this
    /// every time it processes a server-sourced line. Local echoes
    /// don't touch it (they're rendered out-of-band with DateTime.Now).
    /// </summary>
    private long _highestSeenChatAtMs;

    /// <summary>
    /// Bodies of messages we just sent locally that haven't been
    /// "echoed" back by the server yet. Used to skip the duplicate
    /// render when the broadcast `chat` frame for our own message
    /// lands a few hundred ms after the optimistic local echo.
    /// Bounded by a 5 s TTL so a stale entry can't shadow a genuine
    /// later duplicate (e.g. user repeats themselves after a delay).
    /// </summary>
    private readonly System.Collections.Generic.List<(string Body, long SentTicks)> _recentLocalEchoes = new();
    private const int LocalEchoMatchWindowMs = 5000;

    private void HandleChat(JsonElement json)
    {
        if (!json.TryGetProperty("line", out var lineJson)) return;
        var line = JsonSerializer.Deserialize<WsChatLine>(lineJson.GetRawText());
        if (line == null) return;
        AppendChatLine(line);

        // A bare number (1..33) is an AoE3 taunt, not chat. The "11" already
        // reached every member through the normal chat broadcast, so each client
        // plays it from its OWN embedded set — that is what lets every player hear
        // it in THEIR launcher's language, and it needs nothing from the backend.
        // Unlike the blip below, this fires for our own line too: in AoE3 you hear
        // your own taunt (the server echoes it back to us, which is exactly why the
        // blip has to filter on UserId). Returning early keeps the taunt from being
        // stacked on top of a chat blip — the taunt IS the sound.
        if (Services.TauntService.TryParseTaunt(line.Body, out int taunt))
        {
            Services.TauntService.Play(taunt, line.UserId);
            return;
        }

        // Live incoming lobby message → chat blip, unless it's our own. Only the
        // live frame reaches here; ReplayChatRing (history) calls AppendChatLine
        // directly, so replayed history stays silent.
        if (!string.Equals(line.UserId, _session?.CurrentUser?.Id, StringComparison.Ordinal))
            Services.SoundService.PlayChat();
    }

    private void HandleMemberJoined(JsonElement json)
    {
        if (!json.TryGetProperty("user_id", out var u)) return;
        var userId = u.GetString();
        if (string.IsNullOrEmpty(userId)) return;
        var login = json.TryGetProperty("discord_username", out var l) ? (l.GetString() ?? userId) : userId;
        var avatar = json.TryGetProperty("avatar_url", out var av) ? av.GetString() : null;
        // Read like the avatar, and never written back as null for the same reason: a
        // backend that doesn't send them must not erase what room_state already gave us.
        double? rating = json.TryGetProperty("rating", out var rt) && rt.ValueKind == JsonValueKind.Number
            ? rt.GetDouble() : null;
        double? rd = json.TryGetProperty("rd", out var rdv) && rdv.ValueKind == JsonValueKind.Number
            ? rdv.GetDouble() : null;

        if (_roomMembers.TryGetValue(userId, out var existing))
        {
            existing.Login = login;
            if (!string.IsNullOrEmpty(avatar)) existing.AvatarUrl = avatar;
            if (rating.HasValue) existing.Rating = rating;
            if (rd.HasValue) existing.Rd = rd;
        }
        else
        {
            _roomMembers[userId] = new RoomMemberEntry
            {
                UserId = userId, Login = login, AvatarUrl = avatar, Rating = rating, Rd = rd,
            };
        }
        AppendChatSystem(Strings.Format("MpChatMemberJoined", login));
        RenderRoomMembers();
        // Someone joined your room → connect pop (never for our own id).
        if (!string.Equals(userId, _session?.CurrentUser?.Id, StringComparison.Ordinal))
            Services.SoundService.PlayConnect();

        // n2n discovery is supernode-mediated: edges find each other
        // by community, not by per-room signaling. The session's
        // OnFrame handler watches member_joined frames too and may
        // re-derive our slot index when the roster changes; nothing
        // else for this tab to do.
    }

    private void HandleMemberLeft(JsonElement json)
    {
        if (!json.TryGetProperty("user_id", out var u)) return;
        var userId = u.GetString();
        if (string.IsNullOrEmpty(userId)) return;
        if (_roomMembers.Remove(userId, out var entry))
            AppendChatSystem(Strings.Format("MpChatMemberLeft", entry.Login));
        RenderRoomMembers();
    }

    private void HandleMemberReady(JsonElement json)
    {
        if (!json.TryGetProperty("user_id", out var u)) return;
        var userId = u.GetString();
        if (string.IsNullOrEmpty(userId)) return;
        var ready = json.TryGetProperty("ready", out var r) && r.GetBoolean();
        if (_roomMembers.TryGetValue(userId, out var entry))
            entry.Ready = ready;
        RenderRoomMembers();
        MaybeAutoStartOnAllReady();
    }

    /// <summary>
    /// The host left and the server handed the lobby to the next member
    /// (GameRanger-style migration). Update who we think the host is, re-evaluate
    /// our own host-only controls, and re-render the roster's HOST badge.
    /// </summary>
    private void HandleHostChanged(JsonElement json)
    {
        var newHost = json.TryGetProperty("new_host_user_id", out var h) ? h.GetString() : null;
        if (string.IsNullOrEmpty(newHost)) return;
        var newLogin = json.TryGetProperty("new_host_login", out var l) ? (l.GetString() ?? "") : "";

        _roomHostUserId = newHost;
        _isHostInCurrentRoom = !string.IsNullOrEmpty(_session?.CurrentUser?.Id)
            && string.Equals(_roomHostUserId, _session!.CurrentUser!.Id, StringComparison.Ordinal);

        // A guard that only became necessary once the match context started surviving a teardown.
        // Before, a host who lost the socket lost the roster with it and fell silent by accident;
        // now they would report the match — and so would whoever the room just promoted, putting
        // the same game into two people's ratings twice.
        //
        // ONE WAY ONLY. Losing the role silences us; gaining it must NOT arm us, because the
        // previous host may be disconnected and may never receive the frame that would have
        // silenced them. A false negative costs one row in the history. A false positive corrupts
        // two players' rating, and nothing in ReportMatchRequest would let the backend spot it.
        if (_matchContext is { IsHost: true } mc && !_isHostInCurrentRoom)
        {
            DiagnosticLog.Write(
                "MultiplayerTab.HandleHostChanged: host role moved away mid-match — this client will not report it");
            _matchContext = mc.WithHostLost();
        }
        // The other direction, and ONLY for a competitive room. Without it the rule is
        // one-sided: a guest who walks out is caught by the abandonment check, while a HOST who
        // closes his launcher produces no report at all, because his client was the only one
        // that would have sent one. See MatchContext.WithHostGained for why the double-report
        // risk that justified the one-way rule is smaller than it was.
        else if (_matchContext is { IsHost: false, IsCompetitive: true } promoted
                 && _isHostInCurrentRoom
                 && _matchPhase == MatchPhase.InGame)
        {
            DiagnosticLog.Write(
                "MultiplayerTab.HandleHostChanged: promoted to host mid-match in a competitive " +
                "room — this client will report it");
            _matchContext = promoted.WithHostGained();
        }

        if (string.IsNullOrEmpty(newLogin) && _roomMembers.TryGetValue(newHost, out var e))
            newLogin = e.Login;
        AppendChatSystem(Strings.Format("MpChatHostChanged",
            string.IsNullOrEmpty(newLogin) ? newHost : newLogin));

        RenderRoomMembers();   // host-first order + HOST badge
        RenderRoomPanel();     // re-evaluates host-only controls (Start, etc.)
    }

    /// <summary>
    /// The host kicked us. Close the lobby window (which fires the normal
    /// leave-room cleanup + disposes the socket, so there's no reconnect loop
    /// after the server closes ours) and show a notice on the tab.
    /// </summary>
    private void HandleKicked()
    {
        CloseLobbyWindow();
        _ = MpAlertOverlay.NoticeAsync(
            TabRootGrid,
            Strings.Get("MpKickedTitle"),
            Strings.Get("MpKickedBody"),
            Strings.Get("MpAlertOk"));
    }

    /// <summary>
    /// The host renamed the room. The server broadcasts this to EVERYONE
    /// (including the host, who deliberately doesn't paint the new name
    /// locally), so this is the single point where the name changes mid-room
    /// and every client is guaranteed to show the same thing.
    /// </summary>
    private void HandleRoomRenamed(JsonElement json)
    {
        var title = json.TryGetProperty("title", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(title)) return;

        _session?.SetCurrentLobbyTitle(title);
        RenderRoomPanel();   // repaints RoomTitleText from CurrentLobbyTitle
        AppendChatSystem(Strings.Format("MpChatRoomRenamed", title));
    }

    /// <summary>
    /// Host action: ask for a new room name and send it. The name is NOT
    /// applied locally — we wait for the server's <c>room_renamed</c> echo, so
    /// a rejected rename (not host / too short / too fast) can't leave this
    /// client showing a name nobody else has.
    /// </summary>
    private async Task RenameRoomAsync()
    {
        if (_lobbyWindow == null || _session?.RoomSocket == null) return;

        var dlg = new RenameRoomDialog(_session.CurrentLobbyTitle ?? "")
        {
            Owner = _lobbyWindow,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            await _session.RoomSocket.SendRenameRoomAsync(dlg.EnteredName);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"MultiplayerTab.RenameRoom: {ex.Message}");
            await MpAlertOverlay.NoticeAsync(
                _lobbyWindow.LobbyRootGrid,
                Strings.Get("MpRenameDialogTitle"),
                Strings.Get("MpRenameFailed"),
                Strings.Get("MpAlertOk"));
        }
    }

    /// <summary>A peer reported (or changed) its Radmin IP — store it so the
    /// in-game ping prober can ICMP them.</summary>
    private void HandleMemberNet(JsonElement json)
    {
        var userId = json.TryGetProperty("user_id", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(userId)) return;
        var ip = json.TryGetProperty("radmin_ip", out var r) ? r.GetString() : null;
        if (_roomMembers.TryGetValue(userId, out var entry))
            entry.RadminIp = ip;
    }

    /// <summary>
    /// A member told the room which AoE3 profile they play under. Stored and nothing more: it is
    /// read once, at report time, to work out who was on which team.
    ///
    /// <para>Deliberately does NOT re-render the roster — nothing on screen shows this name, and
    /// the twin above sets the precedent. If a future roster ever displays it, this is where the
    /// <c>RenderRoomMembers()</c> call belongs.</para>
    /// </summary>
    private void HandleMemberInGameName(JsonElement json)
    {
        var userId = json.TryGetProperty("user_id", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(userId)) return;
        var name = json.TryGetProperty("ingame_name", out var n) ? n.GetString() : null;
        if (_roomMembers.TryGetValue(userId, out var entry))
            entry.InGameName = name;
    }

    /// <summary>
    /// Rebuild the players sidebar from <see cref="_roomMembers"/>. Host
    /// rendered first with a small "host" tag; everyone else follows in
    /// join order (dictionary insertion order in .NET is preserved).
    /// </summary>
    private void RenderRoomMembers()
    {
        if (_lobbyWindow == null) return;
        _lobbyWindow!.RoomMembersPanel.Children.Clear();

        // Host first. The doc-comment always promised this, but raw
        // dictionary order only happens to put the host first in the
        // host's OWN room — a joiner sees room_state replay order.
        // OrderByDescending is stable, so non-host members keep their
        // join order.
        var ordered = _roomMembers.Values
            .OrderByDescending(m => string.Equals(m.UserId, _roomHostUserId, StringComparison.Ordinal));
        foreach (var m in ordered)
        {
            _lobbyWindow!.RoomMembersPanel.Children.Add(BuildMemberRow(m));
        }

        // Open-slot placeholders up to the room capacity, so the list
        // shows at a glance how many players can still join. Only when
        // the max is known (see TryGetCurrentLobbyMaxPlayers).
        if (TryGetCurrentLobbyMaxPlayers(out var max) && max > _roomMembers.Count)
        {
            for (var i = _roomMembers.Count; i < max; i++)
                _lobbyWindow!.RoomMembersPanel.Children.Add(BuildOpenSlotRow());
        }

        // Keep the PLAYERS stat in lockstep with the roster. RenderRoomMembers
        // is called by EVERY room frame (room_state / member_joined / member_left
        // / member_ready / host_changed), so deriving the count here means the
        // stat and the roster can never diverge — fixing the "roster 2, stat 1/8"
        // bug where an incremental member_joined rebuilt the roster but left the
        // count text stale (it was only refreshed by RenderRoomPanel).
        RefreshRoomPlayerCount();
        // Same reasoning: the big Ready button is derived from the roster, so
        // refresh it wherever the roster is refreshed.
        RefreshReadyButton();
    }

    /// <summary>
    /// Repaint the big Ready toggle from the roster: glyph + label, plus the
    /// <c>Tag="ready"</c> that the MpReadyButton style's trigger keys off to go
    /// green.
    ///
    /// Called from <see cref="RenderRoomMembers"/> — which EVERY room frame runs
    /// — for exactly the reason <see cref="RefreshRoomPlayerCount"/> is: this used
    /// to live inline in <see cref="RenderRoomPanel"/> ALONE, and neither the
    /// local click (<see cref="ReadyButton_Click"/>) nor the server's
    /// `member_ready` echo (<c>HandleMemberReady</c>) calls RenderRoomPanel — both
    /// only rebuild the roster. So readying up tinted your roster row and left the
    /// button frozen on "○ Marcar listo" until some unrelated full `room_state`
    /// frame happened by (a join, a host change). That's the reported bug: "la
    /// opción de marcar como listo funciona, pero el botón grande no se pone
    /// verde". Deriving it from the roster here means button and roster can never
    /// diverge.
    /// </summary>
    private void RefreshReadyButton()
    {
        if (_lobbyWindow == null) return;
        var me = _session?.CurrentUser;
        var iAmReady = me != null
            && _roomMembers.TryGetValue(me.Id, out var meEntry)
            && meEntry.Ready;
        // No leading glyph: the button is half a column wide now, and the ready STATE is
        // already carried by the style's Tag trigger going green.
        _lobbyWindow.ReadyButton.Content = iAmReady
            ? Strings.Get("MpRoomReady")
            : Strings.Get("MpRoomReadyShort");
        _lobbyWindow.ReadyButton.Tag = iAmReady ? "ready" : "";
    }

    /// <summary>
    /// Set the PLAYERS stat ("N / max", or "N" when the max is unknown) from the
    /// live roster (<c>_roomMembers.Count</c>). Single source of truth for the
    /// count, called from both <see cref="RenderRoomPanel"/> and
    /// <see cref="RenderRoomMembers"/> so they stay consistent. (Max only arrives
    /// on the browser's lobby summary — see <see cref="TryGetCurrentLobbyMaxPlayers"/>.)
    /// </summary>
    private void RefreshRoomPlayerCount()
    {
        if (_lobbyWindow == null) return;
        var playerCount = _roomMembers.Count;
        _lobbyWindow.RoomPlayersText.Text = TryGetCurrentLobbyMaxPlayers(out var maxP)
            ? $"{playerCount} / {maxP}"
            : playerCount.ToString();
    }

    /// <summary>
    /// A dimmed "open slot" row, one per unfilled player slot up to the
    /// room capacity. Mirrors <see cref="BuildMemberRow"/>'s left metrics
    /// (an avatar-sized disc + a label) so the rows line up, but muted and
    /// with an empty outlined circle instead of an avatar.
    /// </summary>
    private FrameworkElement BuildOpenSlotRow()
    {
        var row = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = (Brush)Application.Current.FindResource("MpRimFaint"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 7),
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        const double avatarSize = 26.0;
        panel.Children.Add(new Border
        {
            Width = avatarSize, Height = avatarSize,
            CornerRadius = new CornerRadius(avatarSize / 2),
            Background = Brushes.Transparent,
            BorderBrush = (Brush)Application.Current.FindResource("MpRimStrong"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        // It says what to DO about the empty slot. "Waiting for player…" describes the
        // situation; sharing the code is what changes it.
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Get("MpRoomSlotOpenShare"),
            Foreground = (Brush)Application.Current.FindResource("MpTextFaint"),
            FontSize = (double)Application.Current.FindResource("MpMetaSize"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Child = panel;
        return row;
    }

    /// <summary>
    /// One row in the players list. Layout:
    ///   [online dot] [avatar 32] [name + ping (small)] [Host badge] [Ready badge]
    /// Avatar uses the Discord avatar URL when we have one for the
    /// current user; for other members we don't have a URL yet,
    /// so we draw a coloured circle with their initial (cheap,
    /// stable, matches the redesign's "warm gold" placeholder).
    /// </summary>
    /// <summary>
    /// Refresh each roster row's second line in place — the rating and the live ping —
    /// without rebuilding the rows, which would re-fetch every avatar and flicker.
    ///
    /// <para>It replaced a version that recoloured a leading health DOT. The dot is gone
    /// (the reference states the link in words on the row's own second line), and the old
    /// method looked the dot up by walking for an Ellipse with a string Tag — so with the
    /// dot removed it would have found nothing and failed SILENTLY, leaving the ping
    /// frozen at whatever it read when the row was built. The Tag moved to the second
    /// line's TextBlock; the lookup is the same idea, on an element that exists.</para>
    /// </summary>
    private void RefreshRosterLiveCells()
    {
        var panel = _lobbyWindow?.RoomMembersPanel;
        if (panel == null) return;
        foreach (var child in panel.Children)
        {
            if (child is not Border b || b.Child is not Grid g) continue;
            foreach (var el in g.Children)
            {
                if (el is not StackPanel stack) continue;
                foreach (var inner in stack.Children)
                {
                    if (inner is not TextBlock tb || tb.Tag is not string uid) continue;
                    if (_roomMembers.TryGetValue(uid, out var m)) tb.Text = MemberDetailLine(m);
                }
            }
        }
    }

    /// <summary>
    /// The roster row's second line: "{rating} ELO · {ping}".
    ///
    /// <para><b>The rating segment is omitted entirely when unknown</b>, rather than shown
    /// as a placeholder — the same refusal <c>PlayerStanding</c> makes, and for the same
    /// reason: the 1500 the server hands new players must never be displayed as if it were
    /// earned. Ratings are not on the wire per member (the room-state frame carries login,
    /// avatar, ready and radminIp, and nothing else), so for most players this line is the
    /// ping alone until a batch endpoint exists — see the backend gaps in CLAUDE.md.</para>
    ///
    /// <para>The ping half reuses <see cref="PeerNetHealth.Classify"/>, which already
    /// distinguishes "no VPN address reported yet" from "no answer" — a distinction a bare
    /// number cannot make and which players read as the launcher being broken.</para>
    /// </summary>
    private string MemberDetailLine(RoomMemberEntry m)
    {
        var me = _session?.CurrentUser;
        var isMe = me != null && string.Equals(m.UserId, me.Id, StringComparison.Ordinal);

        // Everyone's ELO, not just your own: the rating now rides in the room-state
        // member object, so the roster no longer has to fall back to "only I know mine".
        //
        // No provisional gate any more: 1500 is the shared starting point, and hiding it
        // left this line blank for everybody.
        double? memberRating = m.Rating;
        double? memberRd = m.Rd;
        if (isMe && memberRating == null && _cachedStanding != null)
        {
            // Fallback for a backend that doesn't put ratings in the frame yet: we know
            // our OWN standing from GET /matches/elo. The deviation comes WITH it — without
            // that, our own row would be the one line that cannot tell unrated from 1500.
            memberRating = _cachedStanding.Rating;
            memberRd = _cachedStanding.Rd;
        }

        string? rating = null;
        if (RatingDisplay.ShouldShow(memberRating))
        {
            rating = RatingDisplay.IsUnrated(memberRd, gamesPlayed: null)
                ? Strings.Get("MpEloUnrated")
                : Strings.Format("MpRoomMemberElo", (int)Math.Round(memberRating!.Value));
        }

        string link;
        if (isMe)
        {
            link = Strings.Get("MpPeerYou");
        }
        else
        {
            var state = PeerNetHealth.Classify(
                !string.IsNullOrEmpty(m.RadminIp), m.PingMs, m.ConsecutiveFails);
            link = state switch
            {
                PeerLinkState.Online when m.PingMs >= 0 => $"{m.PingMs} ms",
                PeerLinkState.WaitingVpn => Strings.Get("MpPeerWaitingVpn"),
                PeerLinkState.Lost => Strings.Get("MpPeerLost"),
                _ => "\u2026",
            };
        }

        return rating == null ? link : rating + " \u00B7 " + link;
    }

    /// <summary>
    /// One row in the players list: avatar, name with its host pill, the live detail line,
    /// and the ready state as a word.
    ///
    /// <para>The reference states everything in text on two lines instead of encoding it in
    /// a dot and two pills. What that buys is the numbers: the ping was previously a colour
    /// only, in the lobby, and the actual figure lived in the in-game panel where it is too
    /// late to act on it.</para>
    /// </summary>
    private FrameworkElement BuildMemberRow(RoomMemberEntry m)
    {
        var me = _session?.CurrentUser;
        var isMe = me != null && string.Equals(m.UserId, me.Id, StringComparison.Ordinal);
        var isHost = string.Equals(m.UserId, _roomHostUserId, StringComparison.Ordinal);

        var row = new Border
        {
            // A filled row marks the host; everyone else gets a rim. It used to be a green
            // wash on whoever had readied up, which fought with the ready WORD on the same
            // row for the same meaning.
            Background = isHost
                ? (Brush)Application.Current.FindResource("MpRowHighlight")
                : Brushes.Transparent,
            BorderBrush = isHost
                ? Brushes.Transparent
                : (Brush)Application.Current.FindResource("MpRimFaint"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 7),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // avatar
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // state / kick

        // Avatar: the member's real Discord photo from the room roster
        // (room_state/member_joined), with a coloured-initial fallback. "Me" also has its
        // avatar locally as a backstop for legacy rooms.
        var memberAvatar = !string.IsNullOrEmpty(m.AvatarUrl)
            ? m.AvatarUrl
            : (isMe ? me?.AvatarUrl : null);
        var avatarHost = BuildAvatarDisc(m.Login, memberAvatar, 26);
        avatarHost.Margin = new Thickness(0, 0, 10, 0);
        grid.Children.Add(WithColumn(avatarHost, 0));

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var nameRow = new Grid();
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameRow.Children.Add(WithColumn(new TextBlock
        {
            Text = m.Login,
            Foreground = (Brush)Application.Current.FindResource("MpTextPrimary"),
            FontSize = (double)Application.Current.FindResource("MpBodySize"),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        }, 0));
        if (isHost)
        {
            nameRow.Children.Add(WithColumn(new Border
            {
                Background = (Brush)Application.Current.FindResource("MpEventBg"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = Strings.Get("MpRoomBadgeHost"),
                    Foreground = (Brush)Application.Current.FindResource("MpActionText"),
                    FontSize = (double)Application.Current.FindResource("MpSectionLabelSize"),
                    FontWeight = FontWeights.SemiBold,
                },
            }, 1));
        }
        stack.Children.Add(nameRow);

        // Tagged with the userId so RefreshRosterLiveCells can rewrite it each tick
        // without rebuilding the row.
        stack.Children.Add(new TextBlock
        {
            Text = MemberDetailLine(m),
            Tag = m.UserId,
            Foreground = (Brush)Application.Current.FindResource("MpTextFaint"),
            FontSize = (double)Application.Current.FindResource("MpPillSize"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0),
        });
        grid.Children.Add(WithColumn(stack, 1));

        var tail = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tail.Children.Add(new TextBlock
        {
            Text = Strings.Get(m.Ready ? "MpRoomMemberReady" : "MpRoomMemberWaiting"),
            Foreground = (Brush)Application.Current.FindResource(m.Ready ? "MpOkText" : "MpCaution"),
            FontSize = (double)Application.Current.FindResource("MpLabelSize"),
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Kick — host-only, never on the host's OWN row. It tracks _isHostInCurrentRoom,
        // which host migration keeps current, so it follows whoever holds the room.
        if (_isHostInCurrentRoom && !isMe && !isHost)
        {
            var kickBtn = new Button
            {
                Content = "\u2715",
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.FindResource("MpDestructiveRim"),
                Foreground = (Brush)Application.Current.FindResource("MpDestructiveText"),
                FontSize = (double)Application.Current.FindResource("MpPillSize"),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Strings.Format("MpConfirmKickBody", m.Login),
            };
            var targetId = m.UserId;
            var targetLogin = m.Login;
            kickBtn.Click += async (_, _) => await KickMemberAsync(targetId, targetLogin);
            tail.Children.Add(kickBtn);
        }
        grid.Children.Add(WithColumn(tail, 2));

        row.Child = grid;
        return row;
    }

    /// <summary>
    /// Host action: confirm, then ask the server to kick a member. The roster
    /// re-renders on its own when the resulting member_left frame arrives.
    /// </summary>
    private async Task KickMemberAsync(string userId, string login)
    {
        if (_lobbyWindow == null || _session?.RoomSocket == null) return;
        bool confirmed = await MpAlertOverlay.ConfirmAsync(
            _lobbyWindow.LobbyRootGrid,
            Strings.Get("MpConfirmKickTitle"),
            Strings.Format("MpConfirmKickBody", login),
            Strings.Get("MpConfirmKickYes"),
            Strings.Get("MpAlertCancel"),
            danger: true);
        if (!confirmed) return;
        try { await _session.RoomSocket.SendKickAsync(userId); }
        catch (Exception ex) { DiagnosticLog.Write($"MultiplayerTab.KickMember: {ex.Message}"); }
    }

    /// <summary>Helper: assigns a Grid.Column without verbosity at call sites.</summary>
    private static T WithColumn<T>(T element, int col) where T : FrameworkElement
    {
        Grid.SetColumn(element, col);
        return element;
    }

    /// <summary>
    /// Compact rounded pill ("Host", "Ready"). Background +
    /// foreground passed in so the caller controls the colour.
    /// </summary>
    /// <summary>
    /// A player's rating rendered as the number followed by a small, dimmer "ELO".
    ///
    /// <para>The word is not decoration. A bare "1500" beside a name does not say what it
    /// is, and the tooltip that used to carry that job is useless here — nobody hovers a
    /// thing they do not know exists. The rest of the launcher already spells it out (the
    /// title-bar chip, the room roster, the ladder column), so these two surfaces were the
    /// only ones showing it naked.</para>
    ///
    /// <para>One TextBlock with two Runs, so the two share a BASELINE: the word sits on the
    /// number rather than being centred against it. Don't "fix" that with
    /// <c>BaselineAlignment</c>.</para>
    ///
    /// <para>It is a shared helper because this exact value has already been corrected twice
    /// in two copies — the too-dim brush and the stray "·" each had to be fixed in both
    /// places separately. One function, and that stops.</para>
    /// </summary>
    private static TextBlock BuildRatingText(
        double rating, double? rd, double numberSize, double unitSize)
    {
        var value = (int)Math.Round(rating);
        var tb = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = TooltipHelper.Wrap(Strings.Format("MpChipElo", value)),
        };

        // Never played a rated match: the words, not the 1500 everybody starts from. Handled
        // HERE so the rooms table and the players panel cannot answer it differently — they
        // are the two callers, and they used to share only the styling.
        if (RatingDisplay.IsUnrated(rd, gamesPlayed: null))
        {
            tb.ToolTip = TooltipHelper.Wrap(Strings.Get("MpEloUnrated"));
            tb.Inlines.Add(new System.Windows.Documents.Run(Strings.Get("MpEloUnrated"))
            {
                FontSize = unitSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
            });
            return tb;
        }

        tb.Inlines.Add(new System.Windows.Documents.Run(value.ToString())
        {
            FontSize = numberSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("MpTextSecondary"),
        });
        tb.Inlines.Add(new System.Windows.Documents.Run(" " + Strings.Get("MpEloUnit"))
        {
            FontSize = unitSize,
            FontWeight = FontWeights.SemiBold,
            // Muted, not Faint or Dim: those are the two tones that already drew a
            // "you can barely see it" report, and this one still has to be readable.
            Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
        });
        return tb;
    }

    private static Border BuildBadge(string text, Brush background, Brush foreground)
    {
        return new Border
        {
            Background = background,
            BorderBrush = foreground,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontSize = (double)Application.Current.FindResource("FontSizeCaption"),
                FontWeight = FontWeights.SemiBold,
            },
        };
    }

    private void AppendChatLine(WsChatLine line)
    {
        // Dedup the local optimistic echo. When the server broadcasts
        // OUR message back to us, it carries our user_id and the same
        // body we typed; we already drew that line as a local echo on
        // send, so re-rendering would produce a visible duplicate.
        // We match by (userId, body, within the 5 s send window) and
        // consume one entry per matched echo so a second identical
        // message from us later still renders.
        var me = _session?.CurrentUser;
        if (me != null
            && string.Equals(line.UserId, me.Id, StringComparison.Ordinal))
        {
            var nowTicks = Environment.TickCount64;
            // GC stale local-echo records first so a hours-old entry
            // can't accidentally swallow a brand-new server line.
            _recentLocalEchoes.RemoveAll(x =>
                nowTicks - x.SentTicks > LocalEchoMatchWindowMs);

            for (int i = 0; i < _recentLocalEchoes.Count; i++)
            {
                if (string.Equals(_recentLocalEchoes[i].Body, line.Body, StringComparison.Ordinal))
                {
                    _recentLocalEchoes.RemoveAt(i);
                    if (line.AtMs > _highestSeenChatAtMs)
                        _highestSeenChatAtMs = line.AtMs;
                    return;
                }
            }
        }

        var when = DateTimeOffset.FromUnixTimeMilliseconds(line.AtMs).LocalDateTime;
        AppendChatRow(
            timestamp: when,
            isSystem: false,
            authorLogin: line.Login,
            authorUserId: line.UserId,
            body: line.Body,
            severity: ChatSeverity.Info);
        // Track the newest server-sourced timestamp so a later
        // room_state replay can skip lines we already rendered.
        if (line.AtMs > _highestSeenChatAtMs)
            _highestSeenChatAtMs = line.AtMs;
    }

    private void AppendChatSystem(string body) => AppendChatSystem(body, ChatSeverity.Info);

    /// <summary>
    /// A system line that stands out from the ordinary blue-tagged ones. The severity colours
    /// were already wired in <see cref="AppendChatRow"/> and had no callers at all — every line
    /// was Info — so an amber line costs nothing but choosing it.
    /// </summary>
    private void AppendChatSystem(string body, ChatSeverity severity) =>
        AppendChatRow(
            timestamp: DateTime.Now,
            isSystem: true,
            authorLogin: null,
            authorUserId: null,
            body: body,
            severity: severity);

    /// <summary>Severity bucket for a chat row's body colour.</summary>
    private enum ChatSeverity { Info, Warning, Error }

    /// <summary>
    /// Say something about the finished match where the player will actually see it.
    ///
    /// <para><see cref="AppendChatRow"/> returns in silence when there is no lobby window — and
    /// the end of a match is exactly when there tends not to be one, because a successful report
    /// makes the backend close the room, which tears the window down. So the two lines that tell
    /// the host whether their game was recorded were being written to a window that had just
    /// disappeared. Same lesson <see cref="MaybeReportMissingRecording"/> already learned one
    /// method further down: for anything post-match, the chat is the bonus and the toast is the
    /// delivery.</para>
    /// </summary>
    private void AnnounceMatchOutcome(string body, string title, string icon)
    {
        if (_lobbyWindow != null)
        {
            AppendChatSystem(body);
            return;
        }

        try
        {
            _showAppToast?.Invoke(new AppToast.ToastOptions(
                icon, title, body,
                System.Array.Empty<AppToast.ToastAction>(), AutoDismissMs: 12000));
        }
        catch (Exception ex) { DiagnosticLog.Write($"Match-outcome toast failed: {ex.Message}"); }
    }

    /// <summary>
    /// One chat row (design handoff 1e).
    ///
    /// <para>A MESSAGE is an avatar, then the name and the text on one wrapped line — the
    /// name is a lead-in to the sentence, not a column to align on. An EVENT is a small
    /// square icon and a sentence: a blue arrow for people arriving and leaving, an amber
    /// bang for anything the player has to know. That replaces a monospaced
    /// <c>[System]</c> tag, which read like a log file dropped into a conversation.</para>
    ///
    /// <para>The timestamp moved to the END of an event line and off messages entirely.
    /// It led every row before, in a fixed 68 px column, so the eye met the time of every
    /// line before its content.</para>
    /// </summary>
    private void AppendChatRow(
        DateTime timestamp,
        bool isSystem,
        string? authorLogin,
        string? authorUserId,
        string body,
        ChatSeverity severity)
    {
        if (_lobbyWindow == null) return;

        var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stamp = (Brush)Application.Current.FindResource("MpTextDim");

        if (isSystem)
        {
            // Warning and error share the amber bang: both mean "read this". Only the
            // ordinary arrival/departure line gets the blue arrow.
            var warn = severity != ChatSeverity.Info;
            var iconBg = (Brush)Application.Current.FindResource(warn ? "MpCautionBg" : "MpEventBg");
            var iconFg = (Brush)Application.Current.FindResource(warn ? "MpCaution" : "MpActionText");

            rowGrid.Children.Add(WithColumn(new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = iconBg,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 9, 0),
                Child = new TextBlock
                {
                    Text = warn ? "!" : "\u2192",
                    Foreground = iconFg,
                    FontSize = (double)Application.Current.FindResource("MpMicroSize"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            }, 0));

            rowGrid.Children.Add(WithColumn(new TextBlock
            {
                Text = body,
                Foreground = (Brush)Application.Current.FindResource(
                    warn ? "MpCautionText" : "MpTextBody"),
                FontSize = (double)Application.Current.FindResource("MpLabelSize"),
                LineHeight = 17,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            }, 1));

            rowGrid.Children.Add(WithColumn(new TextBlock
            {
                Text = timestamp.ToString("HH:mm"),
                Foreground = stamp,
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont"),
                FontSize = (double)Application.Current.FindResource("MpMicroSize"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0),
            }, 2));
        }
        else
        {
            var me = _session?.CurrentUser;
            var isMe = !string.IsNullOrEmpty(authorUserId)
                && me != null
                && string.Equals(authorUserId, me.Id, StringComparison.Ordinal);
            // The roster's own avatar helper, so a room member looks the same in both
            // places and the fallback monogram is decided once.
            var avatar = BuildAvatarDisc(authorLogin ?? "?", isMe ? me?.AvatarUrl : null, 22);
            avatar.VerticalAlignment = VerticalAlignment.Top;
            avatar.Margin = new Thickness(0, 0, 9, 0);
            rowGrid.Children.Add(WithColumn(avatar, 0));

            // Name and body in ONE wrapped block: two Runs, not two controls, so a long
            // message flows under the name instead of being pushed into a narrow column.
            var line = new TextBlock
            {
                FontSize = (double)Application.Current.FindResource("MpLabelSize"),
                LineHeight = 17,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.Wrap,
            };
            line.Inlines.Add(new System.Windows.Documents.Run((authorLogin ?? "?") + " ")
            {
                Foreground = (Brush)Application.Current.FindResource("MpTextSecondary"),
                FontWeight = FontWeights.SemiBold,
            });
            line.Inlines.Add(new System.Windows.Documents.Run(body)
            {
                Foreground = (Brush)Application.Current.FindResource(severity switch
                {
                    ChatSeverity.Warning => "MpCautionText",
                    ChatSeverity.Error => "MpDestructiveText",
                    _ => "MpTextBody",
                }),
            });
            line.ToolTip = timestamp.ToString("g");
            rowGrid.Children.Add(WithColumn(line, 1));
        }

        _lobbyWindow!.ChatLogPanel.Children.Add(rowGrid);

        // Cap the in-memory log so a marathon session doesn't bloat
        // the visual tree. 500 rows ≈ 7 hours of moderate chat.
        while (_lobbyWindow!.ChatLogPanel.Children.Count > 500)
            _lobbyWindow!.ChatLogPanel.Children.RemoveAt(0);
        UpdateChatEmptyState();
        _lobbyWindow?.ChatScroll.ScrollToBottom();
    }

    /// <summary>
    /// Legacy raw-append path. Kept so any caller still in
    /// transition to AppendChatRow doesn't break. Renders as a
    /// system info row with a "—" prefix to match the old look.
    /// </summary>
    private void AppendChatRaw(string text, Brush color)
    {
        if (_lobbyWindow == null) return;
        _lobbyWindow!.ChatLogPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = color,
            FontSize = (double)Application.Current.FindResource("FontSizeBody"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 1),
        });
        while (_lobbyWindow!.ChatLogPanel.Children.Count > 500)
            _lobbyWindow!.ChatLogPanel.Children.RemoveAt(0);
        UpdateChatEmptyState();
        _lobbyWindow?.ChatScroll.ScrollToBottom();
    }

    /// <summary>
    /// Show or hide the "no messages yet" hint based on whether the
    /// chat log has any rows. Called after every append and every
    /// clear so the hint tracks the live state.
    /// </summary>
    private void UpdateChatEmptyState()
    {
        if (_lobbyWindow == null) return;
        _lobbyWindow.ChatEmptyHint.Visibility =
            _lobbyWindow.ChatLogPanel.Children.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>
    /// Forward a connection-state event to the diagnostic log.
    /// The old design routed these into a dedicated "global lobby
    /// chat" strip at the bottom of the tab; the redesign removed
    /// that strip entirely, so the user-visible signal is now
    /// just the connection-status pill at the top-right (driven
    /// by UpdateConnectionStatus). We keep the log line so
    /// developers can still debug WS hiccups from the trace file.
    /// </summary>
    private void AppendGlobalSystemEvent(string body)
    {
        DiagnosticLog.Write($"Multiplayer event: {body}");
    }

    /// <summary>
    /// (Removed) The old layout had a Lobby Chat strip at the
    /// bottom with a collapse toggle and a "Join with IP" button.
    /// We deleted both per the redesign; this comment is the
    /// only thing left so future readers don't wonder where
    /// they went. Handler stubs are intentionally absent.
    /// </summary>

    /// <summary>
    /// "Clear chat" header button: wipes the visible log without
    /// touching the server side. Useful when the chat got noisy
    /// during reconnects and the user wants a clean view. We do
    /// NOT re-emit the room_state replay — only the user's local
    /// view is cleared.
    /// </summary>
    private void ClearChatButton_Click(object sender, RoutedEventArgs e)
    {
        _lobbyWindow?.ChatLogPanel.Children.Clear();
        UpdateChatEmptyState();
    }

    /// <summary>
    /// Emoji button placeholder. A proper picker pulls in a UI
    /// library we don't need yet — for now this drops a smiley
    /// at the caret so the button is functional and visibly
    /// alive instead of a dead icon.
    /// </summary>
    private void ChatEmojiButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lobbyWindow == null) return;
        var caret = _lobbyWindow!.ChatInputBox.CaretIndex;
        _lobbyWindow!.ChatInputBox.Text = _lobbyWindow!.ChatInputBox.Text.Insert(caret, "🙂");
        _lobbyWindow!.ChatInputBox.CaretIndex = caret + 2; // emoji is a surrogate pair (length 2)
        _lobbyWindow!.ChatInputBox.Focus();
    }

    /// <summary>
    /// Toggle the faux placeholder TextBlock over the chat input.
    /// WPF TextBox has no native placeholder support so we draw
    /// our own and hide it as soon as the user types. Cheap to
    /// run on every TextChanged because it's just a Visibility
    /// flip.
    /// </summary>
    private void ChatInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_lobbyWindow == null) return;
        _lobbyWindow!.ChatPlaceholderText.Visibility = string.IsNullOrEmpty(_lobbyWindow!.ChatInputBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RefreshFromSession()
    {
        // Refresh the top-right connection pill on every state
        // pass so signing in / out / reconnecting always flow
        // through to the UI without extra plumbing.
        UpdateConnectionStatus();

        // The account cluster, for exactly the same reason and therefore in exactly the
        // same place. It was pushed from a RenderBrowser() that only ran on the SIGNED-IN
        // branch of RenderRoomsTab — so signing out took the signed-out early return
        // and the name and the ELO stayed painted on the title bar of a launcher that had
        // just dropped its token, its user and both sockets. The chip holds a pushed
        // snapshot and reads nothing of its own, so it is right only for as long as somebody
        // pushes to it, and above every return is the only place that is always true.
        // ONE writer, and it fires on every state pass whatever subtab is open — which
        // also closed the same hole in Tournaments, Ranking and Stats.
        PushAccountChip(_session?.CurrentUser);

        if (_session == null)
        {
            // Before Attach. The switch below cannot run without a session, so the table is
            // called here by hand - and it is the table, not a second path, so the gate looks
            // the same on whichever subtab happens to be selected.
            ShowSubtabView();
            return;
        }

        switch (_activeSubtab)
        {
            case Subtab.Rooms:      ShowSubtabView(); RenderRoomsTab();   break;
            case Subtab.Tournaments: ShowSubtabView(); RenderTournamentsTab(); break;
            case Subtab.Ranking:    ShowSubtabView(); RenderRanking();    break;
            case Subtab.Stats:      ShowSubtabView(); RenderStatsTab();   break;
        }

        // The profile is a WINDOW now, so it is not one of the cases above — but it is still
        // reached without a click: a session-state change lands here, and the history fetch is
        // kicked from RenderProfileTab rather than from any click handler. Drop this and an open
        // window goes stale after a sign-in or a reconnect, silently.
        if (_profileWindow != null) RenderProfileTab();

        UpdateSubtabHighlights();

        // Keep the online actions greyed while offline — RenderRoomsTab and the renders
        // above may have re-enabled them based on session state.
        if (_offlineMode) ApplyOfflineDisable();
    }

    /// <summary>
    /// Whether this subtab is replaced by the sign-in panel.
    ///
    /// <para><b>All four, and that is the fix.</b> Not one of these pages says anything true
    /// to somebody with no session. Rooms and Tournaments have nothing to list. Ranking and
    /// Stats look like they work and are worse for it: <c>RefreshStatsForMod</c> returns on
    /// its first line without a session, so the launcher never asked - and then the ladder
    /// printed "there is no ranking to show yet", which is a claim about the server made out
    /// of a request that was never sent.</para>
    ///
    /// <para><b>One preview flag for all four, not one per subtab.</b>
    /// <c>--demo-tournaments</c> and <c>--demo-stats</c> exist to draw these pages with no
    /// session, which is the exact state this reacts to. And the stats preview fills
    /// <c>_communityStats</c>, which is what the RANKING page reads: excusing that flag only
    /// on the Stats subtab would cover real, present data as soon as somebody clicked
    /// Clasificación.</para>
    ///
    /// <para>The <paramref name="subtab"/> is unused, deliberately. It is here because the
    /// answer being the same for all four is the thing worth being able to see, and a rule
    /// that cannot be asked per subtab cannot be pinned per subtab either.</para>
    /// </summary>
    internal static bool SubtabShowsSignInGate(Subtab subtab, bool signedIn, bool preview)
        => !signedIn && !preview;

    /// <summary>
    /// Shows the one view the active subtab owns, hides the rest, and raises the signed-out
    /// gate over all four when there is nobody to show them to.
    ///
    /// <para>This replaced one repeated visibility assignment per case. It is three views now
    /// rather than five, and the table stays because the hazard does: the failure of a missed
    /// line is a page drawn UNDER another one, which looks like a rendering bug rather than a
    /// missing assignment. One table, and a new view is one entry.</para>
    ///
    /// <para><b>The gate joined the table for that same reason, having been the proof of it.</b>
    /// It was <c>ShowSignInPanel</c>, called from the ROOMS render path, so Salas was the only
    /// subtab that could show it and the other three each invented a signed-out state of their
    /// own - a corner note with no button, and a ladder reporting an emptiness nobody had
    /// asked the server about. It is one line here instead, and there is no fourth place for
    /// the next page to forget.</para>
    /// </summary>
    private void ShowSubtabView()
    {
        // THE GATE IS PART OF THIS TABLE, and that is the whole fix. It was three assignments
        // on the Rooms render path, so Salas was the only subtab that ever showed it. Here it
        // is decided once, on the one method that runs whichever subtab is open.
        bool gate = SubtabShowsSignInGate(
            _activeSubtab,
            _session?.Status == MultiplayerSession.SessionStatus.SignedIn,
            _demoTournaments || _demoStats);

        SignInPanel.Visibility = gate ? Visibility.Visible : Visibility.Collapsed;

        if (gate)
        {
            // THE REASON TRAVELS WITH THE PANEL. Written from RenderRoomsTab, it had the panel's
            // own bug one layer down: a refused sign-in explained itself on Salas and nowhere
            // else, and signing out from any other subtab left the previous message stranded
            // under the button, because nothing on those paths cleared it.
            //
            // Read straight off the session rather than passed in. Both old callers passed
            // exactly this - one of them as a literal null, from a branch where the session
            // was null anyway - so the argument was never carrying a decision.
            var reason = _session?.LastError;
            SignInErrorText.Text = reason ?? "";
            SignInErrorText.Visibility = string.IsNullOrEmpty(reason)
                ? Visibility.Collapsed
                : Visibility.Visible;

            // Not a room to go back to. The gate replaces the whole tab, so leaving a lobby
            // window floating over it would be a room with no launcher behind it.
            BrowserPanel.Visibility = Visibility.Collapsed;
            CloseLobbyWindow();
        }

        // COLLAPSED, not just covered. The gate is drawn over these by declaration order, but
        // painted over is not hidden: a page taller than the panel shows around it, which is
        // how the ladder's "there is no ranking to show yet" would still be legible under a
        // button asking you to sign in.
        RoomsView.Visibility   = !gate && _activeSubtab == Subtab.Rooms   ? Visibility.Visible : Visibility.Collapsed;
        TournamentsView.Visibility = !gate && _activeSubtab == Subtab.Tournaments ? Visibility.Visible : Visibility.Collapsed;
        RankingView.Visibility = !gate && _activeSubtab == Subtab.Ranking ? Visibility.Visible : Visibility.Collapsed;
        StatsView.Visibility   = !gate && _activeSubtab == Subtab.Stats   ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderRoomsTab()
    {
        // The legacy WinDivert bootstrap gate is gone — the hook
        // injector ships next to the .exe and needs no per-user
        // setup. Fall straight through to sign-in / browser rendering.

        if (_session == null
            || _session.Status != MultiplayerSession.SessionStatus.SignedIn)
        {
            // Nothing to draw and nothing to say: ShowSubtabView has already raised the gate
            // over this whole tab and closed the room window behind it.
            return;
        }

        // In a room? Show the room as a centered popup over the
        // browser. BrowserPanel stays Visible underneath (the
        // RoomPanel's own backdrop rectangle dims it) so the user
        // doesn't lose context. Leaving / X closes the popup and
        // the browser becomes interactive again without any
        // extra state plumbing.
        if (_session.Lobby == MultiplayerSession.LobbyStatus.InLobby
            || _session.Lobby == MultiplayerSession.LobbyStatus.InGame
            || _session.Lobby == MultiplayerSession.LobbyStatus.Joining
            || _session.Lobby == MultiplayerSession.LobbyStatus.Leaving)
        {
            BrowserPanel.Visibility = Visibility.Visible;
            OpenLobbyWindow();
            RenderRoomPanel();
        }
        else
        {
            BrowserPanel.Visibility = Visibility.Visible;
            CloseLobbyWindow();
        }
    }

    /// <summary>
    /// Push the lobby window's STATIC labels (section headers, field
    /// labels, button captions, placeholder, copy tooltip) through the
    /// localisation table. Called when the window opens and again on a
    /// mid-room language switch. The dynamic, state-driven text (status
    /// line, player count, ready toggle, …) is owned by
    /// <see cref="RenderRoomPanel"/> instead.
    /// </summary>
    private void ApplyLobbyStaticLabels()
    {
        if (_lobbyWindow == null) return;
        _lobbyWindow.PlayersStatHeader.Text = Strings.Get("MpRoomPlayersHeader");
        // "CÓDIGO", not "ROOM ID": it is the thing you read out to someone so they can
        // get in, and the reference names it after what it is for.
        _lobbyWindow.RoomIdStatHeader.Text = Strings.Get("MpRoomCodeHeader");
        _lobbyWindow.RoomConnHeader.Text = Strings.Get("MpInGameConnectionHeader");
        _lobbyWindow.CopyRoomIdButton.ToolTip = Strings.Get("MpRoomCopyCode");
        _lobbyWindow.RoomCompetitiveBadgeText.Text = Strings.Get("MpRoomCompetitiveBadge");
        _lobbyWindow.RoomCompetitiveBadge.ToolTip =
            TooltipHelper.Wrap(Strings.Get("MpRoomCompetitiveTooltip"));
        // Caption AND tooltip: the word says what the button does, the tooltip carries the
        // detail (who sees the new name). It lives here so a mid-room language switch catches it.
        _lobbyWindow.RenameRoomButton.Content = Strings.Get("MpRoomRenameButton");
        _lobbyWindow.RenameRoomButton.ToolTip = TooltipHelper.Wrap(Strings.Get("MpRoomRenameTooltip"));
        _lobbyWindow.PlayersListHeader.Text = Strings.Get("MpRoomPlayersHeader");
        _lobbyWindow.RoomInfoHeaderText.Text = Strings.Get("MpRoomInfoHeader");
        _lobbyWindow.RoomModLabel.Text = Strings.Get("MpRoomFieldMod");
        _lobbyWindow.RoomPasswordLabel.Text = Strings.Get("MpRoomFieldPassword");
        _lobbyWindow.RoomCopyLabel.Text = Strings.Get("MpRoomFieldCopy");
        _lobbyWindow.ChatHeaderText.Text = Strings.Get("MpRoomChatHeader");
        // A quiet text link now, so no glyph: the bin icon read as a destructive button
        // sitting in a chat header.
        _lobbyWindow.ClearChatButton.Content = Strings.Get("MpRoomChatClear");
        _lobbyWindow.ChatSendButton.Content = Strings.Get("MpRoomChatSend");
        _lobbyWindow.ChatPlaceholderText.Text = Strings.Get("MpRoomChatPlaceholder");
        _lobbyWindow.ChatEmptyHint.Text = Strings.Get("MpRoomChatEmpty");

        // Match-phase static labels (countdown chat-line / InGameOverlay).
        // The dynamic captions — countdown "Go", the in-game mode badge, the
        // in-game cancel/leave button, AND the Start-button-as-Cancel during
        // the countdown — are owned by UpdateCountdownTick /
        // RefreshInGamePanel / ApplyMatchPhaseUi. The countdown now lives as
        // a single live line INSIDE the chat (⏱ label + number, no hint and
        // no button of its own — the left-column Start button doubles as
        // Cancel), so there's no CountdownHint / CountdownCancelButton
        // caption to set here.
        _lobbyWindow.CountdownLabel.Text = Strings.Get("MpCountdownLabel");
        _lobbyWindow.InGameTitleText.Text = Strings.Get("MpInGameTitle");

        _lobbyWindow.InGameTrafficHeader.Text = Strings.Get("MpInGameTrafficHeader");
        _lobbyWindow.InGameConnectionHeader.Text = Strings.Get("MpInGameConnectionHeader");
        _lobbyWindow.InGameRecordingHeader.Text = Strings.Get("MpInGameRecordingHeader");
        _lobbyWindow.InGameSoloTitle.Text = Strings.Get("MpInGameSoloTitle");
        _lobbyWindow.InGameSoloBody.Text = Strings.Get("MpInGameSoloBody");
        _lobbyWindow.InGameSoloCopyButton.Content = Strings.Get("MpInGameSoloCopy");
        _lobbyWindow.InGameSoloAnnounceButton.Content = Strings.Get("MpInGameSoloAnnounce");

        // Record Game band. Only the fixed parts here — the BODY says something
        // different to the host than to everyone else, so RenderRoomPanel owns it
        // (and re-picks it when the host migrates).
        _lobbyWindow.PreflightHeader.Text = Strings.Get("MpPreflightHeader");
        _lobbyWindow.PreflightRecordHelp.Content = Strings.Get("MpPreflightSeeHow");
        _lobbyWindow.InvitePlayersButton.Content = Strings.Get("MpRoomInvite");
        // The two secondary actions sit side by side now, so their captions have to fit
        // half a column each.
        _lobbyWindow.LeaveRoomButton.Content = Strings.Get("MpRoomLeaveShort");
    }

    private void RenderRoomPanel()
    {
        // Lobby window closed → nothing to render. Fires from session
        // events that may arrive after we've left the room and the
        // window has already been disposed.
        if (_lobbyWindow == null) return;

        var s = _session!;

        var status = s.Lobby switch
        {
            MultiplayerSession.LobbyStatus.Joining => Strings.Get("MpRoomStatusJoining"),
            MultiplayerSession.LobbyStatus.Leaving => Strings.Get("MpRoomStatusLeaving"),
            MultiplayerSession.LobbyStatus.InGame => Strings.Get("MpRoomStatusInGame"),
            _ => Strings.Get("MpRoomStatusInLobby"),
        };

        // P2P readiness: with the hook-injector bridge, "ready to
        // play" just means the mesh is up AND the injector artefacts
        // are shipped — there's no per-machine driver install gate
        // anymore. For solo rooms (host alone) we still show
        // "P2P ready"; peers will join later.
        var p2pReady = s.IsInLobby;
        var p2pStatus = p2pReady ? Strings.Get("MpRoomP2pReady") : Strings.Get("MpRoomP2pStarting");

        // Build the meta line as inline runs so the P2P state can wear
        // its own (green) colour without two TextBlocks: status in muted
        // text, P2P readiness highlighted. This line is now the single
        // home for the P2P status — the old "Connection" info-card cell
        // repeated it and was removed.
        var muted = (Brush)Application.Current.FindResource("MpTextFaint");
        _lobbyWindow!.RoomMetaText.Inlines.Clear();
        // The STATE leads and wears the colour — a dot plus two words — and everything
        // after it is context in the muted tone. The old line put the state in the same
        // grey as the rest and coloured the P2P readiness instead, which is the less
        // interesting of the two.
        _lobbyWindow!.RoomMetaText.Inlines.Add(new System.Windows.Documents.Run("\u25CF " + status)
        {
            Foreground = (Brush)Application.Current.FindResource(
                p2pReady ? "MpOkText" : "MpCautionText"),
            FontWeight = FontWeights.Medium,
        });
        _lobbyWindow!.RoomMetaText.Inlines.Add(new System.Windows.Documents.Run("  ")
        {
            Foreground = muted,
        });
        _lobbyWindow!.RoomMetaText.Inlines.Add(new System.Windows.Documents.Run(p2pStatus)
        {
            Foreground = muted,
        });
        // "· open for X" — a live count-up of how long the room has been open. The
        // Run is stashed so RefreshLobbyOpenAge (lobby ping timer, ~2.5 s) ticks it
        // up without rebuilding the line. Only when we know the open time.
        _lobbyAgeRun = null;
        if (_currentLobbyCreatedUtc.HasValue)
        {
            _lobbyWindow!.RoomMetaText.Inlines.Add(new System.Windows.Documents.Run("  \u00B7  ")
            {
                Foreground = muted,
            });
            _lobbyAgeRun = new System.Windows.Documents.Run(LobbyOpenAgeText()) { Foreground = muted };
            _lobbyWindow!.RoomMetaText.Inlines.Add(_lobbyAgeRun);
        }

        // ---------- Host (drives the title fallback only) ----------
        // The roster below marks the host with a badge, so there's no
        // separate HOST stat to fill anymore; we still resolve the name
        // to build a friendly title when the room is unnamed.
        string hostLabel = "";
        if (!string.IsNullOrEmpty(_roomHostUserId)
            && _roomMembers.TryGetValue(_roomHostUserId, out var hostEntry))
        {
            hostLabel = hostEntry.Login;
        }
        else if (!string.IsNullOrEmpty(_roomHostUserId))
        {
            hostLabel = _roomHostUserId;
        }

        // ---------- Title ----------
        // Prefer the room's own name. When unnamed, the title used to
        // fall back to the raw lobby id — exactly what the ROOM ID stat
        // already shows, so it read as a duplicate. Use "<host>'s room"
        // instead (or a generic label until the host is known).
        var title = s.CurrentLobbyTitle;
        if (string.IsNullOrWhiteSpace(title)
            || string.Equals(title, s.CurrentLobbyId, StringComparison.Ordinal))
        {
            title = !string.IsNullOrEmpty(hostLabel)
                ? Strings.Format("MpRoomTitleFallback", hostLabel)
                : Strings.Get("MpRoomTitleGeneric");
        }
        _lobbyWindow!.RoomTitleText.Text = title;
        // Painted from the room's own flag, alongside the title it qualifies. Repainted on
        // every render so it follows a room switch rather than sticking from the last one.
        _lobbyWindow.RoomCompetitiveBadge.Visibility =
            _currentLobbyIsCompetitive ? Visibility.Visible : Visibility.Collapsed;
        // The format belongs on the badge: it is what decides which ladder the match counts
        // for and which of the competitive promises apply, and there is nowhere else in the
        // room that says it.
        var roomFormatKey = Services.Multiplayer.RoomFormats.LabelKey(CurrentRoomFormat());
        _lobbyWindow.RoomCompetitiveBadgeText.Text = roomFormatKey == null
            ? Strings.Get("MpRoomCompetitiveBadge")
            : Strings.Get("MpRoomCompetitiveBadge") + " · " + Strings.Get(roomFormatKey);

        // The window's own title carries the room name too, so the taskbar button says
        // which room it is instead of the word "Lobby" repeated per window.
        var windowTitle = Strings.Format("MpLobbyWindowTitle", title);
        _lobbyWindow.Title = windowTitle;
        _lobbyWindow.TitleBarControl.Title = windowTitle;

        // The header leads with the room's MOD icon — the same one the rooms list shows,
        // so a room looks like itself from both sides. The crossed-swords glyph stays as
        // the fallback for a mod with no resolvable icon.
        var modIcon = ResolveRoomModIcon(
            string.IsNullOrEmpty(_currentLobbyModId) ? null : ModRegistry.Find(_currentLobbyModId));
        _lobbyWindow.RoomModIconHost.Background = modIcon
            ?? (Brush)Application.Current.FindResource("MpEventBg");
        _lobbyWindow.RoomModIconGlyph.Visibility = modIcon == null
            ? Visibility.Visible : Visibility.Collapsed;

        // Renaming is host-only. Evaluated here (not once at open) so a host
        // migration — which calls RenderRoomPanel — hands the button to the
        // new host and takes it away from the old one.
        _lobbyWindow!.RenameRoomButton.Visibility = _isHostInCurrentRoom
            ? Visibility.Visible
            : Visibility.Collapsed;

        // ---------- "Tick Record Game" band ----------
        // Age of Empires III will not record the match unless its own per-match
        // checkbox is ticked, and a match with no recording has no result — so it
        // never counts towards anyone's rating. The launcher cannot tick it: the
        // profile setting it writes does not drive that box, and a +RecordGame
        // launch argument does nothing (both measured).
        //
        // Shown to EVERYONE, worded differently. Only the host can tick it, but if
        // he forgets, his opponent loses the result too — so the opponent has a
        // reason to say something, and hosts rotate anyway.
        //
        // Evaluated here rather than once at open, like the rename button above,
        // so a host migration swaps the wording to the new host for free.
        RefreshPreflightChecklist();

        // ---------- Players ----------
        RefreshRoomPlayerCount();

        // ---------- ROOM ID ----------
        // Short uppercase code if the worker assigns one, otherwise the
        // raw lobby id (truncated for sanity).
        var rid = s.CurrentLobbyId ?? "";
        if (rid.Length > 12) rid = rid.Substring(0, 12);
        _lobbyWindow!.RoomIdText.Text = rid.ToUpperInvariant();

        // ---------- Room info card (Mod + Password) ----------
        // Slimmed from four cells to two: "Connection" duplicated the
        // P2P meta line and "Max players" duplicated the PLAYERS stat.
        // The whole card collapses when neither remaining field has data.
        var modKnown = TryGetCurrentLobbyModName(out var modName);
        _lobbyWindow!.RoomModText.Text = modKnown ? modName : "—";
        var hasPwd = TryGetCurrentLobbyHasPassword(out var hp) && hp;
        _lobbyWindow!.RoomPasswordText.Text = hasPwd
            ? Strings.Get("MpRoomPasswordYes")
            : Strings.Get("MpRoomPasswordNo");

        // Which install copy the room uses — only when the room's mod has 2+
        // copies (multiplayer always uses the ACTIVE copy of that mod). Tells
        // host + joiners which folder they entered with.
        string? copyLeaf = null;
        if (!string.IsNullOrEmpty(_currentLobbyModId))
        {
            var st = WarsOfLibertyLauncher.Models.LauncherConfig.Load().GetState(_currentLobbyModId);
            if (st.HasMultipleInstalls && !string.IsNullOrWhiteSpace(st.InstallPath))
                copyLeaf = CopyLeaf(st.InstallPath);
        }
        var hasCopy = !string.IsNullOrEmpty(copyLeaf);
        _lobbyWindow!.RoomCopyRow.Visibility = hasCopy ? Visibility.Visible : Visibility.Collapsed;
        if (hasCopy) _lobbyWindow!.RoomCopyText.Text = copyLeaf;

        _lobbyWindow!.RoomInfoCard.Visibility = (modKnown || hasPwd || hasCopy)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // ---------- Action buttons ----------
        RefreshReadyButton();

        // The Start button only appears for the host; enabled once the
        // P2P bridge is ready so AoE3 launches into a working network.
        // GUARD: only own the Start button while we're in the Lobby phase.
        // During the countdown (Starting) ApplyMatchPhaseUi repurposes this
        // same button as the red "Cancel" for everyone, so a room_state
        // refresh mid-countdown must NOT stomp it back to "Start game".
        if (_matchPhase == MatchPhase.Lobby)
        {
            _lobbyWindow!.StartButton.Visibility = _isHostInCurrentRoom
                ? Visibility.Visible
                : Visibility.Collapsed;
            _lobbyWindow!.StartButton.IsEnabled = _isHostInCurrentRoom && s.IsInLobby;
            _lobbyWindow!.StartButton.Content = StartButtonCaption();
        }
        // Host migration is one of the things that changes the answer here, and it arrives as a
        // room refresh rather than a phase change — hence a call from this method too.
        RefreshRejoinButton();
        _lobbyWindow!.LeaveRoomButton.Content = "↩  " + Strings.Get("MpRoomLeave");
    }

    /// <summary>
    /// Try to look up MaxPlayers for the current lobby. The session
    /// keeps the ID/title but not the full LobbySummary, so we walk
    /// the cached browser list (last /lobbies fetch) for a match.
    /// Cheap because the list is bounded at ~8 active rooms.
    /// </summary>
    private bool TryGetCurrentLobbyMaxPlayers(out int maxPlayers)
    {
        maxPlayers = 0;
        var lobbyId = _session?.CurrentLobbyId;
        if (!string.IsNullOrEmpty(lobbyId) && _lastBrowserList != null)
        {
            foreach (var l in _lastBrowserList)
            {
                if (string.Equals(l.Id, lobbyId, StringComparison.Ordinal))
                {
                    maxPlayers = l.MaxPlayers;
                    return true;
                }
            }
        }
        // Fallback for the host (absent from the browser snapshot of
        // joinable rooms): capacity is stashed on create/join.
        if (_currentLobbyMaxPlayers > 0)
        {
            maxPlayers = _currentLobbyMaxPlayers;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Try to resolve the human-readable mod name for the current
    /// lobby. Same approach as MaxPlayers — walks the cached
    /// browser list, then falls back to <see cref="ModRegistry"/>
    /// to translate the mod id into a display name.
    /// </summary>
    private bool TryGetCurrentLobbyModName(out string modName)
    {
        modName = "";
        var lobbyId = _session?.CurrentLobbyId;
        if (!string.IsNullOrEmpty(lobbyId) && _lastBrowserList != null)
        {
            foreach (var l in _lastBrowserList)
            {
                if (string.Equals(l.Id, lobbyId, StringComparison.Ordinal))
                {
                    // Look the id up in the registry for the friendly
                    // name; fall back to the raw id if not registered.
                    foreach (var p in ModRegistry.All)
                    {
                        if (string.Equals(p.Id, l.ModId, StringComparison.OrdinalIgnoreCase))
                        {
                            modName = p.DisplayName;
                            return true;
                        }
                    }
                    modName = l.ModId;
                    return true;
                }
            }
        }
        // Fallback: the host (and anyone whose browser snapshot is stale
        // or was never fetched) isn't in _lastBrowserList, but the
        // current room's mod id is cached on create/join. Resolve that
        // so the info card shows the mod name instead of an em-dash.
        if (!string.IsNullOrEmpty(_currentLobbyModId))
        {
            foreach (var p in ModRegistry.All)
            {
                if (string.Equals(p.Id, _currentLobbyModId, StringComparison.OrdinalIgnoreCase))
                {
                    modName = p.DisplayName;
                    return true;
                }
            }
            modName = _currentLobbyModId;
            return true;
        }
        return false;
    }

    private bool TryGetCurrentLobbyHasPassword(out bool hasPwd)
    {
        hasPwd = false;
        var lobbyId = _session?.CurrentLobbyId;
        if (string.IsNullOrEmpty(lobbyId)) return false;
        if (_lastBrowserList != null)
        {
            foreach (var l in _lastBrowserList)
            {
                if (string.Equals(l.Id, lobbyId, StringComparison.Ordinal))
                {
                    hasPwd = l.IsPrivate;
                    return true;
                }
            }
        }

        // The stash, and it is not a nicety: GET /lobbies excludes your OWN room, so for a host
        // the loop above NEVER matches and this is the only answer there is. Without it the host
        // of a private room was told their room had no password. Same fallback the mod name and
        // the capacity already had.
        if (_currentLobbyIsPrivate is bool priv)
        {
            hasPwd = priv;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Most recent /lobbies snapshot. Cached so the room view can
    /// read MaxPlayers / IsPrivate / ModId without re-fetching. We
    /// don't expire it aggressively — the data is mostly static
    /// for the duration of a single match and worst case the user
    /// sees "?" until the next refresh tick.
    /// </summary>
    private List<LobbySummary>? _lastBrowserList;

    /// <summary>
    /// The mod id of the CURRENT room — set when the user creates
    /// or joins a lobby, cleared on leave. <see cref="LaunchActiveModGame"/>
    /// uses this to pick the right profile (NOT the Play tab's
    /// active profile, which can disagree with the room's mod
    /// when the user was browsing other mods between sessions).
    /// </summary>
    private string? _currentLobbyModId;

    /// <summary>
    /// Max players for the CURRENT room — set on create/join, cleared on
    /// leave. The host isn't in the browser snapshot (`_lastBrowserList`)
    /// so this is the only reliable source of room capacity for the
    /// PLAYERS stat and the players-list open-slot rows. 0 = unknown.
    /// </summary>
    private int _currentLobbyMaxPlayers;

    /// <summary>
    /// Whether the CURRENT room is private, mirroring <see cref="_currentLobbyMaxPlayers"/>
    /// (set on create/join, cleared on leave). Same reason as its sibling: the host is absent
    /// from the browser snapshot, so without this the HOST of a private room read
    /// "Password: none" in their own room info — the exact opposite of what they had just
    /// configured, and the one person guaranteed to know it was wrong. Null = unknown.
    /// </summary>
    private bool? _currentLobbyIsPrivate;

    /// <summary>
    /// Whether the CURRENT room puts rating on the line, mirroring <see cref="_currentLobbyIsPrivate"/>
    /// (set on create/join, cleared on leave). Same reason as its siblings: the browser snapshot
    /// excludes your own room, so the host has no other way to learn what the server decided.
    ///
    /// <para>This drives the BADGE and the Start confirmation, which are questions about the room
    /// as it stands. The match's own copy lives in <see cref="Services.Multiplayer.MatchContext"/>
    /// and is frozen at launch — do not read this field once the game has closed, since by then
    /// the room may be gone.</para>
    /// </summary>
    private bool _currentLobbyIsCompetitive;

    /// <summary>
    /// How many of the current room's seats are for watching rather than playing.
    ///
    /// <para>Travels beside <see cref="_currentLobbyIsCompetitive"/> and is set and cleared at
    /// the same three points, because the format is read from the two together: a room's
    /// competitive flag with the wrong seat count names the wrong format, and the badge would
    /// promise a 2v2 the abandonment rule and ladder of something else.</para>
    ///
    /// <para>0 for every room from a server that does not send the field, which is the truth
    /// about those rooms rather than a fallback — nothing could have reserved a seat in
    /// them.</para>
    /// </summary>
    private int _currentLobbySpectatorSlots;

    /// <summary>
    /// The bracket slot the current room belongs to, or null for an ordinary room.
    ///
    /// <para>Sixth of the <c>_currentLobby*</c> family and set and cleared at exactly the same
    /// three points, for the reason the family exists: the room window is a dumb shell and
    /// these are what the tab knows about the room it is drawing.</para>
    ///
    /// <para><b>It is the whole basis of the tournament room.</b> A room carrying one admits
    /// only the entrants of that tie \u2014 the server refuses everybody else before it looks at
    /// seats, password or anything else \u2014 so invite, the shareable code, the password row
    /// and the empty-seat rows are five controls for things that cannot happen. Until this
    /// field existed the launcher had no way to know, because the DTO dropped the value the
    /// server had been sending all along.</para>
    /// </summary>
    private string? _currentLobbyTournamentMatchId;

    /// <summary>Whether the room on screen is one leg of a tournament bracket.</summary>
    private bool InTournamentRoom => !string.IsNullOrEmpty(_currentLobbyTournamentMatchId);

    /// <summary>
    /// What the launcher still owes the match that just ended, and when the game closed.
    ///
    /// <para>Together these hold the Leave button shut for a competitive host until the result is
    /// actually settled — see <see cref="Services.Multiplayer.RoomMatchState.HoldLeave"/> for why
    /// walking out in that window destroys the result rather than merely risking it.</para>
    /// </summary>
    private Services.Multiplayer.RoomMatchState.ResultPhase _resultPhase =
        Services.Multiplayer.RoomMatchState.ResultPhase.None;
    private DateTime _resultPhaseSinceUtc = DateTime.UtcNow;

    /// <summary>
    /// The match we are still owed a RESULT for, after our own game has already closed.
    ///
    /// <para><b>Deliberately separate from <see cref="_matchContext"/>, and the separation is the
    /// whole point.</b> That field means "the match I will report", and its life is deliberately
    /// short: <see cref="OnGameExitedAsync"/> clears it in a <c>finally</c>, guarded so an older
    /// exit handler cannot drop a newer match's context. Correct for reporting — and wrong for
    /// RECEIVING, because a match does not end when our game closes. It ends when the HOST
    /// reports it, which on a guest's machine is always later.</para>
    ///
    /// <para>What that cost, measured on a real match: the guest's game closed sixteen seconds
    /// before the host's, so when <c>match_reported</c> arrived — carrying both players' results
    /// and rating changes — the context was already null and <see cref="HandleMatchReported"/>
    /// returned without a word. The <c>4007</c> close behind it was gated on the same field, so
    /// it fell through to the generic reconnect and retried a deleted room some two hundred
    /// times.</para>
    ///
    /// <para>Cleared when the result lands, when the room is left, when a new match captures its
    /// own context, and by
    /// <see cref="Services.Multiplayer.RoomMatchState.ResultWaitCeilingSeconds"/> — never left to
    /// sit around on its own.</para>
    /// </summary>
    private Services.Multiplayer.MatchContext? _pendingResultContext;
    private DateTime _pendingResultSinceUtc = DateTime.UtcNow;
    private System.Windows.Threading.DispatcherTimer? _resultWaitTimer;

    /// <summary>
    /// The id of the match the result card is showing, kept only so the no-window fallback can
    /// raise a notification the bell will DEDUPE. Without an id the bell would happily show the
    /// same match twice — once from here and again from a later <c>match_rated</c> frame.
    /// </summary>
    private string? _lastResultMatchId;

    /// <summary>
    /// The match that post-game code should be reasoning about: the live one while our game is
    /// still running, otherwise the one we are waiting on a result for.
    /// </summary>
    private Services.Multiplayer.MatchContext? ResultContext()
        => _matchContext ?? _pendingResultContext;

    /// <summary>UTC time the CURRENT room opened, mirroring <see cref="_currentLobbyMaxPlayers"/>
    /// (set on create/join, cleared on leave). Drives the lobby header's live "open for X".
    /// On create it's ~now (the POST returns no created_at); on join it's parsed from the
    /// joined <c>LobbySummary.CreatedAt</c>. Null = unknown → no age shown.</summary>
    private DateTime? _currentLobbyCreatedUtc;

    /// <summary>The inline Run inside the lobby meta line that shows "open for X", so
    /// <see cref="RefreshLobbyOpenAge"/> ticks it up without rebuilding the whole line.
    /// Recreated by each <see cref="RenderRoomPanel"/>; null when no age is shown.</summary>
    private System.Windows.Documents.Run? _lobbyAgeRun;

    /// <summary>
    /// Whether the last rooms fetch failed, so the next one that succeeds can tell it is
    /// a RECOVERY rather than just another poll.
    ///
    /// <para>It exists for one thing: the player's standing is fetched once per session,
    /// and a session that starts while the backend is down never gets it. Four attempts
    /// hit a 502 once and the ELO stayed blank under the player's name for the rest of the
    /// session — with the server back up the whole time — because nothing retried.</para>
    ///
    /// <para>The transition is what is being detected, NOT the poll. Retrying on every
    /// tick would fire every few seconds for as long as the server stayed down, which is
    /// precisely when it is failing, and <c>/matches/elo</c> allows 20 a minute and 500 a
    /// day PER IP — shared by everyone behind the same Radmin network.</para>
    /// </summary>
    private bool _roomsFetchFailed;

    /// <summary>The standing, fetched once per session — see <see cref="LoadStandingAsync"/>.</summary>
    private EloSnapshot? _cachedStanding;

    /// <summary>Guards the standing fetch. Two entry points reach it now — the Profile
    /// subtab and the title-bar chip — and both can fire on the same state change.</summary>
    private bool _standingFetchInFlight;

    /// <summary>
    /// Draws the Profile tab. Everything comes from what is already in hand — the cached
    /// standing, the community stats and the history page — so this costs no request, and it
    /// is safe to call again on a language change or after either of those lands.
    /// </summary>
    private void RenderProfileTab()
    {
        // The page lives in ProfileWindow now; this class still builds it. Every caller is
        // guarded, but guard here too — the render is reached from a session change, a fetch
        // landing and a language switch, and the window can be closed at any of them.
        var ProfileBody = _profileWindow?.ProfileBody;
        if (ProfileBody == null) return;
        ProfileBody.Children.Clear();

        var user = _session?.CurrentUser;
        if (user == null)
        {
            ProfileBody.Children.Add(new TextBlock
            {
                Text = Strings.Get("MpSignInPrompt"),
                Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
                FontSize = (double)Application.Current.FindResource("MpMetaSize"),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        // Both are fetched elsewhere and cached; asking for them here only kicks a fetch that
        // has not happened yet, and the page redraws when it lands.
        if (_cachedStanding == null) _ = LoadStandingAsync();
        if (_communityStats == null && _session?.Status == MultiplayerSession.SessionStatus.SignedIn)
            _ = RefreshActivityStripAsync();
        // The history the section below shows. Kicked HERE, with the other two, rather than
        // from the subtab's click handler: this page is also reached without a click — a
        // session-state change re-enters it through RefreshFromSession — and from the handler
        // alone that path would have shown an empty history for ever.
        //
        // It cannot loop: RefreshHistoryAsync sets _isRefreshingHistory before its first
        // await, so the repaint it triggers when the fetch lands hits the guard below. And
        // because that flag is already set by the time this method builds the section, the
        // section paints its "Loading…" state on this very pass.
        if (_historyRows == null && !_isRefreshingHistory) _ = RefreshHistoryAsync();

        ProfileBody.Children.Add(BuildProfileSectionPills());

        // The decks take the page rather than sitting in it: with the game's art and what each
        // card does they are a screen, and stacked under the profile they would push the history
        // a screenful down.
        if (_profileSection == ProfileSection.Decks)
        {
            ProfileBody.Children.Add(BuildProfileDecks());
            if (!_mpDecksLoaded) _ = LoadMpDecksAsync();
            return;
        }

        ProfileBody.Children.Add(BuildProfileHeader(user));
        ProfileBody.Children.Add(BuildProfileMiddleRow());
        ProfileBody.Children.Add(BuildProfileStatsRow());
        ProfileBody.Children.Add(BuildProfileCivs());
        ProfileBody.Children.Add(BuildProfileHistory());
    }

    /// <summary>
    /// Which civilizations the player uses, and how they go with each.
    ///
    /// <para><b>Computed from the history page this tab already fetched, and the label says so.</b>
    /// A dedicated endpoint would answer over every match ever played; this answers over the last
    /// fifty, which is what <c>GetHistoryAsync</c> returns. That is the right trade today and the
    /// caption is what makes it honest — the whole community played 40 rated matches in the thirty
    /// days this shipped, and civilizations were only reported from that build onwards, so nobody
    /// will have fifty matches carrying one for many months. When somebody does, the source moves
    /// behind this method and the card does not change.</para>
    ///
    /// <para>Drawn even when empty, with a line saying why — for weeks that is what everybody will
    /// see, and a blank card would read as broken rather than as new.</para>
    /// </summary>
    private UIElement BuildProfileCivs()
    {
        var card = BuildProfileCard(Strings.Get("MpProfileCivsTitle"));
        var stack = (StackPanel)card.Child;
        card.Margin = new Thickness(0, 11, 0, 0);

        var rows = Services.Multiplayer.CivStatsView.Rows(_historyRows);

        if (rows.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = Strings.Get("MpProfileCivsEmpty"),
                Foreground = (Brush)Application.Current.FindResource("MpTextFaint"),
                FontSize = (double)Application.Current.FindResource("MpMetaSize"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
            return card;
        }

        foreach (var row in rows.Take(MaxProfileCivRows))
            stack.Children.Add(BuildProfileCivRow(row));

        stack.Children.Add(new TextBlock
        {
            Text = Strings.Format("MpProfileCivsWindow", _historyRows?.Count ?? 0),
            Foreground = (Brush)Application.Current.FindResource("MpTextFaint"),
            FontSize = (double)Application.Current.FindResource("MpMicroSize"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        });
        return card;
    }

    /// <summary>How many civilizations the card lists before it stops being a card.</summary>
    private const int MaxProfileCivRows = 8;

    /// <summary>
    /// One civilization: name, matches, record, and a percentage only when there is enough behind
    /// it to state one.
    /// </summary>
    /// <remarks>
    /// <c>internal static</c> so <c>DialogXamlTests</c> can build the real row — nothing else
    /// constructs it and no compile step checks a resource looked up by name.
    /// </remarks>
    internal static FrameworkElement BuildProfileCivRow(Services.Multiplayer.CivStatRow row)
    {
        var meta = (double)Application.Current.FindResource("MpMetaSize");

        // A Grid and not a horizontal StackPanel, for the reason the rooms table documents: a
        // horizontal StackPanel measures its children with INFINITE width, so a long civilization
        // name would push the record off the card instead of trimming.
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        grid.Children.Add(WithColumn(new TextBlock
        {
            Text = row.Civ,
            Foreground = (Brush)Application.Current.FindResource("MpTextPrimary"),
            FontSize = meta,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        }, 0));

        // Matches played and the record behind them — both always, because they are facts however
        // few they are. The record is what the percentage would have said, without the arithmetic
        // that needs a sample.
        grid.Children.Add(WithColumn(new TextBlock
        {
            Text = Strings.Format("MpProfileCivsRecord", row.Played, row.Wins, row.Losses),
            Foreground = (Brush)Application.Current.FindResource("MpTextSecondary"),
            FontSize = meta,
            FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        }, 1));

        // Null below the bar, and then NOTHING is drawn — not an em dash where a number would go.
        // Wars of Liberty ships 188 civilizations; for months almost every row is null.
        var pct = Services.Multiplayer.CivStatsView.WinPercent(row);
        grid.Children.Add(WithColumn(new TextBlock
        {
            Text = pct == null ? "" : pct.Value.ToString() + " %",
            Foreground = (Brush)Application.Current.FindResource(
                pct == null ? "MpTextFaint"
                : Services.Multiplayer.RankingTableLayout.PercentBrushKey(pct.Value)),
            FontSize = meta,
            FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        }, 2));

        return grid;
    }

    /// <summary>
    /// The header: who you are on the left, what you are rated on the right.
    ///
    /// <para>The rating is the one number the page exists for, so it is 30 px of serif and
    /// nothing competes with it. It is blank — not 1500 — when the standing was never
    /// fetched: the server hands every new player 1500, and showing it as though it were
    /// earned is the lie this refuses to tell.</para>
    /// </summary>
    /// <remarks><c>internal</c> for the same reason as <see cref="BuildLeaderboardRow"/>.</remarks>
    internal UIElement BuildProfileHeader(Models.Multiplayer.LobbyUserSummary user)
    {
        var grid = new Grid { Margin = new Thickness(18, 16, 18, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = BuildAvatarDisc(user.DisplayName, user.AvatarUrl, 56, cornerRadius: 14);
        avatar.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(avatar, 0);
        grid.Children.Add(avatar);

        var who = new StackPanel
        {
            Margin = new Thickness(16, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new TextBlock
        {
            Text = user.DisplayName,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("DisplayFont"),
            FontSize = (double)Application.Current.FindResource("MpProfileNameSize"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("MpTextHeading"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // PROVISIONAL means "not on the ladder yet", not "the deviation has not settled" —
        // see ProfileSummaryView.IsProvisional for why the second version marks everybody.
        if (Services.Multiplayer.ProfileSummaryView.IsProvisional(
                LadderEntryBar(), _cachedStanding?.GamesPlayed ?? 0))
        {
            nameRow.Children.Add(BuildProfileTag(Strings.Get("MpProfileProvisionalTag")));
        }
        who.Children.Add(nameRow);

        // "@handle · joined in {month} · {mod}". Each segment is dropped when its value is
        // missing, so an older backend that sends no created_at simply loses that clause.
        var line = new System.Collections.Generic.List<string> { "@" + user.DiscordUsername };
        var joined = Services.Multiplayer.MatchHistoryView.ParseLocal(user.CreatedAt);
        if (joined.HasValue)
        {
            line.Add(Strings.Format(
                "MpProfileJoined",
                joined.Value.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture)));
        }
        // The mod being PLAYED, not the launcher's default. It named Wars of Liberty for
        // everybody, on every mod, which for a launcher that manages several is simply false -
        // and the segment is dropped when there is no active mod rather than filled with a
        // guess, exactly like the "joined" segment above it.
        var playing = _getActiveProfile?.Invoke();
        if (!string.IsNullOrWhiteSpace(playing?.Id)) line.Add(ResolveModDisplayName(playing!.Id));

        who.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", line),
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
            FontSize = (double)Application.Current.FindResource("MpMetaSize"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(who, 1);
        grid.Children.Add(who);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(new TextBlock
        {
            Text = Strings.Get("MpProfileRatingLabel"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = (Brush)Application.Current.FindResource("MpTextLabel"),
            FontSize = (double)Application.Current.FindResource("MpSectionLabelSize"),
            FontWeight = FontWeights.SemiBold,
        });

        var ratingRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        ratingRow.Children.Add(new TextBlock
        {
            Text = _cachedStanding == null
                ? Strings.Get("MpDash")
                : ((int)Math.Round(_cachedStanding.Rating)).ToString(),
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("DisplayFont"),
            FontSize = (double)Application.Current.FindResource("MpProfileRatingSize"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("MpTextHeading"),
        });

        var summary = Services.Multiplayer.MatchHistoryView.Summarise(_historyRows, _cachedStanding);
        var deltaText = Services.Multiplayer.RatingDisplay.FormatDelta(summary.Delta);
        if (deltaText != null)
        {
            ratingRow.Children.Add(new TextBlock
            {
                Text = deltaText,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                FontSize = (double)Application.Current.FindResource("MpBodySize"),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.FindResource(
                    summary.Delta >= 0 ? "MpOkTextAlt" : "MpDestructiveText"),
            });
        }
        right.Children.Add(ratingRow);

        // "rank N of M" — and only when the server said BOTH. The rank comes from finding the
        // player on the ladder, the total from a count the server does separately; inventing
        // either would put a false fact inside a sentence that reads like one.
        var rank = MyLadderRank();
        var total = Services.Multiplayer.CommunityStatsView.RankedPlayers(_communityStats, team: false);
        if (rank > 0 && total > 0)
        {
            right.Children.Add(new TextBlock
            {
                Text = Strings.Format("MpProfileRank", rank, total),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
                FontSize = (double)Application.Current.FindResource("MpMicroSize"),
            });
        }
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return new Border
        {
            Child = grid,
            CornerRadius = (CornerRadius)Application.Current.FindResource("RadiusLg"),
            Background = (Brush)Application.Current.FindResource("MpProfileHeaderBg"),
            BorderBrush = (Brush)Application.Current.FindResource("MpRimStrong"),
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>The ladder's entry bar, as the server states it. 0 when it did not say.</summary>
    private int LadderEntryBar()
        => Services.Multiplayer.CommunityStatsView.RequiredDecided(_communityStats) ?? 0;

    /// <summary>
    /// The viewer's place on the 1v1 ladder, or 0 when they are not on it (or the table has
    /// not arrived). The rank is the SERVER's — this only looks the player up, it never
    /// counts rows.
    /// </summary>
    private int MyLadderRank()
    {
        var meId = _session?.CurrentUser?.Id;
        if (string.IsNullOrEmpty(meId)) return 0;

        foreach (var row in Services.Multiplayer.CommunityStatsView.Rows(_communityStats))
            if (string.Equals(row.UserId, meId, StringComparison.Ordinal)) return row.Rank;
        return 0;
    }

    /// <summary>The rating curve on the left, the record on the right.</summary>
    private UIElement BuildProfileMiddleRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(295) });

        var curve = BuildProfileCard(Strings.Get("MpProfileCurveTitle"));
        curve.Margin = new Thickness(0, 0, 11, 0);
        Grid.SetColumn(curve, 0);
        grid.Children.Add(curve);
        FillProfileCurve((StackPanel)curve.Child);

        var record = BuildProfileCard(Strings.Get("MpProfileRecordTitle"));
        Grid.SetColumn(record, 1);
        grid.Children.Add(record);
        FillProfileRecord((StackPanel)record.Child);

        return grid;
    }

    /// <summary>
    /// The rating curve.
    ///
    /// <para>With fewer than two points it says so instead of drawing a flat line: a straight
    /// horizontal stroke is a claim about a rating that has been steady, and for a player with
    /// one match that claim is false.</para>
    /// </summary>
    private void FillProfileCurve(StackPanel host)
    {
        var points = Services.Multiplayer.ProfileSummaryView.RatingCurve(_historyRows);
        if (points.Count < 2)
        {
            host.Children.Add(new TextBlock
            {
                Text = Strings.Get("MpProfileCurveTooFew"),
                Margin = new Thickness(0, 14, 0, 0),
                Foreground = (Brush)Application.Current.FindResource("MpTextDim"),
                FontSize = (double)Application.Current.FindResource("MpMetaSize"),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var lowest = points.Min();
        var highest = points.Max();
        var span = Math.Max(1, highest - lowest);

        const double height = 56;
        var line = new System.Windows.Shapes.Polyline
        {
            Stroke = (Brush)Application.Current.FindResource("MpAction"),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Fill,
            Height = height,
            Margin = new Thickness(0, 14, 0, 0),
        };
        for (var i = 0; i < points.Count; i++)
        {
            // Y is inverted because a canvas grows downward and a rating does not.
            line.Points.Add(new Point(i, height - (points[i] - lowest) / span * height));
        }
        host.Children.Add(line);

        var ends = new Grid { Margin = new Thickness(0, 9, 0, 0) };
        ends.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ends.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var from = ProfileCurveLabel(
            Strings.Format("MpProfileCurveFrom", (int)Math.Round(points[0])), "MpTextDim");
        Grid.SetColumn(from, 0);
        ends.Children.Add(from);
        var to = ProfileCurveLabel(
            Strings.Format("MpProfileCurveTo", (int)Math.Round(points[^1])), "MpTextMuted");
        to.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(to, 1);
        ends.Children.Add(to);
        host.Children.Add(ends);
    }

    private static TextBlock ProfileCurveLabel(string text, string brushKey)
        => new()
        {
            Text = text,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont"),
            FontSize = (double)Application.Current.FindResource("MpMicroSize"),
            Foreground = (Brush)Application.Current.FindResource(brushKey),
        };

    /// <summary>
    /// The record card: W-L, one segment per match needed to reach the ladder, and the
    /// remaining distance said in words.
    ///
    /// <para><b>This is where "0 % wins" used to be</b>, printed as a headline for a player
    /// with a single decided match. Below the entry bar the percentage is not shown at all —
    /// the record is — because a rate over one match is not a rate, and the one it produced
    /// was the most discouraging number the launcher could have chosen to lead with.</para>
    /// </summary>
    private void FillProfileRecord(StackPanel host)
    {
        var wins = _cachedStanding?.Wins ?? 0;
        var losses = _cachedStanding?.Losses ?? 0;
        var decided = PlayerStanding.DecidedGames(wins, losses);

        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
        };
        line.Children.Add(new TextBlock
        {
            Text = _cachedStanding == null
                ? Strings.Get("MpDash")
                : Strings.Format("MpRankRecordValue", wins, losses),
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont"),
            FontSize = (double)Application.Current.FindResource("MpProfileRecordSize"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("MpTextHeading"),
        });

        var bar = LadderEntryBar();
        var played = _cachedStanding?.GamesPlayed ?? 0;
        var provisional = Services.Multiplayer.ProfileSummaryView.IsProvisional(bar, played);

        // The percentage appears only once it rests on enough matches to mean something. Above
        // the bar it is real information; below it, it is one match expressed as 0 % or 100 %.
        var percent = PlayerStanding.WinPercent(wins, losses);
        var beside = !provisional && percent.HasValue
            ? Strings.Format("MpProfileRecordPercent", percent.Value, decided)
            : Strings.Format("MpProfileRecordDecided", decided);

        line.Children.Add(new TextBlock
        {
            Text = beside,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
            FontSize = (double)Application.Current.FindResource("MpMetaSize"),
            TextWrapping = TextWrapping.Wrap,
        });
        host.Children.Add(line);

        if (bar <= 0) return;

        // One segment per rated match the ladder asks for, filled by the results so far. It
        // turns "provisional" from a label into a distance you can see the end of.
        var segments = new Grid { Margin = new Thickness(0, 11, 0, 0), Height = 5 };
        for (var i = 0; i < bar; i++)
            segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < bar; i++)
        {
            var seg = new Border
            {
                Height = 5,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0),
                Background = (Brush)Application.Current.FindResource(
                    i < wins ? "MpOk"
                    : i < played ? "MpDestructive"
                    : "MpBarTrack"),
            };
            Grid.SetColumn(seg, i);
            segments.Children.Add(seg);
        }
        host.Children.Add(segments);

        var remaining = Services.Multiplayer.ProfileSummaryView.MatchesToLadder(bar, played);
        host.Children.Add(new TextBlock
        {
            Text = remaining > 0
                ? Strings.Format("MpProfileToLadder", remaining)
                : Strings.Get("MpProfileOnLadder"),
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
            FontSize = (double)Application.Current.FindResource("MpMetaSize"),
            TextWrapping = TextWrapping.Wrap,
        });
    }

    /// <summary>Total matches, most-played map, usual opponent.</summary>
    private UIElement BuildProfileStatsRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 11, 0, 0) };
        for (var i = 0; i < 3; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var totals = Services.Multiplayer.ProfileSummaryView.Totals(_historyRows);
        var played = Cell(0, "MpProfileTotalMatches");
        played.Children.Add(ProfileStatValue(totals.Played.ToString()));
        played.Children.Add(ProfileStatSub(
            Strings.Format("MpProfileTotalBreakdown", totals.Decided, totals.Unrated)));

        var (map, mapCount) = Services.Multiplayer.MatchHistoryView.TopMap(_historyRows);
        var topMap = Cell(1, "MpProfileTopMap");
        topMap.Children.Add(ProfileStatText(
            string.IsNullOrWhiteSpace(map) ? Strings.Get("MpDash") : map.Replace('_', ' ')));
        if (mapCount > 0)
            topMap.Children.Add(ProfileStatSub(
                Strings.Format("MpProfileTopMapCount", mapCount, totals.Played)));

        var rival = Services.Multiplayer.ProfileSummaryView.FrequentOpponent(
            _historyRows, _session?.CurrentUser?.Id);
        var rivalCell = Cell(2, "MpProfileRival");
        if (rival == null)
        {
            rivalCell.Children.Add(ProfileStatText(Strings.Get("MpDash")));
        }
        else
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0),
            };
            var face = BuildAvatarDisc(rival.Name, rival.AvatarUrl, 20);
            face.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(face);
            row.Children.Add(new TextBlock
            {
                Text = rival.Name,
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = (Brush)Application.Current.FindResource("MpTextHeading"),
                FontSize = (double)Application.Current.FindResource("MpBodySize"),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            rivalCell.Children.Add(row);
            rivalCell.Children.Add(ProfileStatSub(
                Strings.Format("MpProfileRivalRecord", rival.Wins, rival.Losses)));
        }

        return grid;

        StackPanel Cell(int column, string labelKey)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = Strings.Get(labelKey),
                Foreground = (Brush)Application.Current.FindResource("MpTextLabel"),
                FontSize = (double)Application.Current.FindResource("MpSectionLabelSize"),
                FontWeight = FontWeights.SemiBold,
            });
            var box = new Border
            {
                Child = stack,
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(column == 0 ? 0 : 6, 0, column == 2 ? 0 : 5, 0),
                CornerRadius = (CornerRadius)Application.Current.FindResource("RadiusPanel"),
                Background = (Brush)Application.Current.FindResource("MpPanel"),
                BorderBrush = (Brush)Application.Current.FindResource("MpRimFaint"),
                BorderThickness = new Thickness(1),
            };
            Grid.SetColumn(box, column);
            grid.Children.Add(box);
            return stack;
        }
    }

    private static TextBlock ProfileStatValue(string text)
        => new()
        {
            Text = text,
            Margin = new Thickness(0, 8, 0, 0),
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont"),
            FontSize = (double)Application.Current.FindResource("MpStatValueSize"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("MpTextHeading"),
        };

    private static TextBlock ProfileStatText(string text)
        => new()
        {
            Text = text,
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = (double)Application.Current.FindResource("MpBodySize"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("MpTextHeading"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

    private static TextBlock ProfileStatSub(string text)
        => new()
        {
            Text = text,
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = (double)Application.Current.FindResource("MpMicroSize"),
            Foreground = (Brush)Application.Current.FindResource("MpTextDim"),
            TextWrapping = TextWrapping.Wrap,
        };

    /// <summary>
    /// The match history, as the last section of the Profile.
    ///
    /// <para><b>It used to be a subtab of its own, and the two pages said the same things.</b>
    /// History led with four summary cells — rating, decided record, "didn't count" count,
    /// most-played map — and every one of those is already on this page, in the header, the
    /// RECORD card and the two stat cells. This page in turn carried a "Latest matches" block
    /// that was a three-row excerpt of History's list, under a link back to it. One page and
    /// one set of numbers is the whole change; nothing about a match card moved.</para>
    ///
    /// <para><b>The filter scrolls away with the page, and that is the cost.</b> The Profile is
    /// one ScrollViewer, so the chips do not stay pinned the way they did on a screen of their
    /// own. That is what being a section rather than a page means, and it is worth knowing
    /// before somebody "fixes" it by nesting a second scroller in here.</para>
    /// </summary>
    private UIElement BuildProfileHistory()
    {
        var host = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };

        var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = Strings.Get("MpSubtabHistory"),
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("DisplayFont"),
            FontSize = (double)Application.Current.FindResource("MpPageTitleSize"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("MpTextHeading"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        head.Children.Add(title);

        // Built in code because this section is; they were XAML while History had a view of
        // its own. Same SubTab style as everywhere else that offers this kind of choice, so
        // they carry the active pill without any colour being set here — see
        // UpdateSubtabHighlights for why a local Foreground would kill it.
        var filters = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var (filter, key) in new[]
                 {
                     (Services.Multiplayer.HistoryFilter.All, "MpHistoryFilterAll"),
                     (Services.Multiplayer.HistoryFilter.Rated, "MpHistoryFilterRated"),
                     (Services.Multiplayer.HistoryFilter.Unrated, "MpHistoryFilterUnrated"),
                 })
        {
            var chip = new Button
            {
                Content = Strings.Get(key),
                Style = (Style)FindResource("SubTab"),
                Tag = _historyFilter == filter ? "active" : null,
            };
            var chosen = filter;
            chip.Click += (_, _) => SetHistoryFilter(chosen);
            filters.Children.Add(chip);
        }
        Grid.SetColumn(filters, 1);
        head.Children.Add(filters);
        host.Children.Add(head);

        // The list, built straight into this section rather than into a panel kept in a
        // field. A field panel would have to be un-parented on every repaint (the profile is
        // rebuilt whole), and re-parenting a live element is a WPF exception waiting for the
        // first person who forgets. Rebuilding costs a header, a curve and three cells.
        var section = Services.Multiplayer.MatchHistoryView.SectionFor(
            _historyRows, _historyError, _isRefreshingHistory);

        if (section == Services.Multiplayer.HistorySection.Loading)
        {
            host.Children.Add(new TextBlock
            {
                Text = Strings.Get("MpHistoryLoading"),
                Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(2, 10, 0, 0),
            });
            return host;
        }

        if (section == Services.Multiplayer.HistorySection.Error)
        {
            // A themed line, not the raw Brushes.Salmon this used to paint — that was the one
            // place in the multiplayer surface still using a hardcoded system colour.
            host.Children.Add(new TextBlock
            {
                Text = _historyError,
                Foreground = (Brush)Application.Current.FindResource("MpDestructiveText"),
                FontSize = (double)Application.Current.FindResource("MpMetaSize"),
                Margin = new Thickness(2, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            return host;
        }

        var shown = Services.Multiplayer.MatchHistoryView.Filter(_historyRows, _historyFilter);
        if (shown.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = Strings.Get(
                    _historyRows == null || _historyRows.Count == 0
                        ? "MpHistoryEmpty"
                        : "MpHistoryFilterEmpty"),
                Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
                FontSize = (double)Application.Current.FindResource("MpMetaSize"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 12, 0, 0),
            });
            return host;
        }

        var meId = _session?.CurrentUser?.Id;
        foreach (var day in Services.Multiplayer.MatchHistoryView.GroupByDay(shown))
        {
            host.Children.Add(BuildHistoryDayHeader(day.LocalDate));
            foreach (var row in day.Matches) host.Children.Add(BuildHistoryRow(row, meId));
        }

        return host;
    }

    /// <summary>A titled card on the Profile tab. The caller fills the returned panel.</summary>
    private static Border BuildProfileCard(string title)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = (Brush)Application.Current.FindResource("MpTextLabel"),
            FontSize = (double)Application.Current.FindResource("MpSectionLabelSize"),
            FontWeight = FontWeights.SemiBold,
        });

        return new Border
        {
            Child = stack,
            Padding = new Thickness(15, 14, 15, 14),
            CornerRadius = (CornerRadius)Application.Current.FindResource("RadiusPanel"),
            Background = (Brush)Application.Current.FindResource("MpPanel"),
            BorderBrush = (Brush)Application.Current.FindResource("MpRimFaint"),
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>The amber PROVISIONAL tag beside a name.</summary>
    private static UIElement BuildProfileTag(string text)
        => new Border
        {
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(7, 3, 7, 3),
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = (CornerRadius)Application.Current.FindResource("RadiusSm"),
            Background = (Brush)Application.Current.FindResource("MpProvisionalBg"),
            Child = new TextBlock
            {
                Text = text,
                Foreground = (Brush)Application.Current.FindResource("MpProvisionalText"),
                FontSize = (double)Application.Current.FindResource("MpSectionLabelSize"),
                FontWeight = FontWeights.SemiBold,
            },
        };

    /// <summary>
    /// Fetches the player's standing. Everything shown here lives on the SERVER — the
    /// launcher keeps only this per-session copy so re-opening the tab costs nothing.
    ///
    /// <para>Once per session, and never on a timer: the endpoint allows 20/min and 500/day
    /// <b>per IP</b>, and that IP is shared behind NAT or an active Radmin network, so a poll
    /// would spend everyone's budget on that network.</para>
    ///
    /// <para><b>One exception, and it is an EVENT rather than a timer:</b> entering the result
    /// phase re-fetches, because the end-of-match card puts this tally on screen and the cached
    /// one predates the match it is announcing. That is one request per match PLAYED — bounded
    /// by how often people play, which is what the rule above is actually protecting against.
    /// (The other exception is the backend-recovery retry; see the rooms-poll transition.)</para>
    ///
    /// <para>A failure leaves the lines blank. There is nothing useful to say when the
    /// standing can't be read, and a default would be a lie.</para>
    /// </summary>
    private async Task LoadStandingAsync()
    {
        var session = _session;
        var userId = session?.CurrentUser?.Id;
        if (session == null || string.IsNullOrEmpty(userId)) return;
        if (ConnectivityState.IsOffline) return;
        if (_standingFetchInFlight) return;
        _standingFetchInFlight = true;

        try
        {
            var standing = await session.Api.GetEloAsync(userId);
            _cachedStanding = standing;

            // The title-bar chip is the reason this can be reached without the Profile
            // subtab, so it is repainted regardless of which subtab is on screen.
            PushAccountChip(session.CurrentUser);

            // The user may have moved to another subtab while this was in flight.
            // The whole tab, not one line of it: the rating, the record, the segments and the
            // header's rank all read this standing, so a partial repaint would leave some of
            // them describing the state before the fetch.
            if (_profileWindow != null) RenderProfileTab();

            // The end-of-match card's DECIDED cell reads this tally, so a refresh that does not
            // repaint leaves that cell on whatever was cached BEFORE the match. Harmless when
            // there is no card up — the repaint checks that itself.
            RepaintMatchResult();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"MultiplayerTab.LoadStandingAsync: {ex.Message}");
        }
        finally
        {
            _standingFetchInFlight = false;
        }
    }

    // (ShowStanding is gone. It wrote four TextBlocks that no longer exist — the whole Profile
    //  tab is built by RenderProfileTab now, from the same cached standing, so the refusals it
    //  encoded moved WITH the numbers rather than being dropped: a null standing still paints
    //  an em dash instead of the 1500 the server hands everybody, and the win percentage still
    //  appears only once it rests on enough matches to mean anything — see FillProfileRecord.)

    private void UpdateSubtabHighlights()
    {
        // ALL the colour lives in the SubTab style's Tag="active" trigger; this only
        // sets the flag. It used to assign Foreground and BorderBrush directly, which
        // has to go with the underline-to-pill change and not merely because the
        // underline is gone: a LOCAL value (precedence 3) beats a ControlTemplate
        // trigger (4-6), so leaving these assignments in would silently kill the pill's
        // own foreground. Same trap the brand button and the lobby Ready button were
        // each bitten by.
        static void Paint(Button b, bool active) => b.Tag = active ? "active" : null;

        Paint(SubtabRooms, _activeSubtab == Subtab.Rooms);
        Paint(SubtabTournaments, _activeSubtab == Subtab.Tournaments);
        Paint(SubtabRanking, _activeSubtab == Subtab.Ranking);
        Paint(SubtabStats, _activeSubtab == Subtab.Stats);

        // Viewing the Rooms subtab clears the "new room created" dot.
        if (_activeSubtab == Subtab.Rooms) SetNewRoomIndicator(false);
    }

    /// <summary>True while an unseen "new room created" signal is pending.</summary>
    private bool _hasNewRoomSignal;

    /// <summary>
    /// Shows/hides the small red "new room created" dot on the Rooms subtab.
    /// Room notifications no longer add a bell item — MainWindow's lobby poll
    /// calls this (plus a Windows toast + the MULTIPLAYER nav-tab dot). The dot
    /// only shows while the user is NOT already on the Rooms subtab, and is
    /// cleared when they open it (see <see cref="UpdateSubtabHighlights"/>).
    /// </summary>
    public void SetNewRoomIndicator(bool on)
    {
        _hasNewRoomSignal = on;
        if (RoomsSubtabDot != null)
            RoomsSubtabDot.Visibility =
                on && _activeSubtab != Subtab.Rooms ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Reconnection state tracked from WS events. The LobbyWebSocket
    /// raises Disconnected / Reconnecting; we flip this flag and the
    /// status pill picks it up on the next UpdateConnectionStatus
    /// pass. <c>true</c> means "we lost the room socket and the
    /// retry loop is running"; cleared back to <c>false</c> on the
    /// next successful room_state frame or when the socket is
    /// detached entirely (left room).
    /// </summary>
    private bool _isReconnecting;

    /// <summary>
    /// Repaint the connection-status pill at the top-right of the
    /// header. Three states: Connected (green dot), Reconnecting
    /// (amber), Offline (red). Idempotent — safe to call on every
    /// state change or on a poll.
    /// </summary>
    private void UpdateConnectionStatus()
    {
        // The pill this used to paint is gone — the reference makes the title-bar chip
        // the SINGLE connection indicator, so this now only decides the word and hands
        // it over. Not signed in leaves it null, which hides the chip: claiming
        // "Connected" before sign-in was the old pill's behaviour and it was wrong.
        _connectionLabel =
            _session == null || _session.Status != MultiplayerSession.SessionStatus.SignedIn
                ? null
                : _isReconnecting
                    ? Strings.Get("MpChipReconnecting")
                    : Strings.Get("MpChipConnected");

        PushConnectionChip();
    }

    /// <summary>The lobby-connection word for the title-bar chip; null hides it.</summary>
    private string? _connectionLabel;

    /// <summary>
    /// Renders the header chip from the TWO facts it merges: the lobby connection
    /// (the word) and Radmin (the address). Kept in one method because both feed one
    /// control and they change on different schedules — the session on state changes,
    /// Radmin on its ~3 s poll — so either updating alone would drop the other's half.
    /// </summary>
    private void PushConnectionChip()
    {
        if (_setConnectionChip == null) return;

        string? detail = null;
        if (_connectionLabel != null)
        {
            // BARE, per the header reference — no "VPN ·" prefix and no separator
            // glyph. Saying what the address is happens in the capsule's tooltip,
            // which only became possible once the capsule left the caption region.
            var ip = RadminVpnService.TryGetAdapterIp();
            if (!string.IsNullOrEmpty(ip)) detail = ip;
        }

        _setConnectionChip(_connectionLabel, detail);
    }

    // ---------- Subtab clicks ----------

    /// <summary>
    /// Public entry point (used by MainWindow when a "new room" notification is
    /// clicked) to force the Rooms subtab and freshen the list. Mirrors
    /// <see cref="SubtabRooms_Click"/>.
    /// </summary>
    /// <summary>
    /// Show the player their match history.
    ///
    /// <para>Which is the PROFILE now — History stopped being a subtab of its own and became a
    /// section of it. The method keeps its name because the caller (MainWindow, on the "your
    /// match was scored after all" notification) still means exactly this; where the list
    /// lives is not its business.</para>
    /// </summary>
    public void ShowHistory() => OpenProfileWindow();

    public void ShowRooms()
    {
        _activeSubtab = Subtab.Rooms;
        UpdateSubtabHighlights();
        RefreshFromSession();
        if (_session?.Status == MultiplayerSession.SessionStatus.SignedIn)
            _ = RefreshRoomsListAsync(quiet: true);
    }

    private void SubtabRooms_Click(object sender, RoutedEventArgs e)
    {
        _activeSubtab = Subtab.Rooms;
        SetNewRoomIndicator(false); // opening Rooms clears the "new room" dot
        RefreshFromSession();
        // Coming (back) to the Rooms subtab: quietly freshen the list so
        // rooms created while the user was on another subtab show up at
        // once, without the skeleton flash a full refresh would cause. The
        // 5 s _roomsListTimer keeps it current from here on.
        if (_session?.Status == MultiplayerSession.SessionStatus.SignedIn)
        {
            _ = RefreshRoomsListAsync(quiet: true);
            // Self-gating and one-shot, so re-entering the subtab costs nothing.
            _ = RefreshActivityStripAsync();
        }
    }
    private void SubtabStats_Click(object sender, RoutedEventArgs e)
    {
        _activeSubtab = Subtab.Stats;
        RefreshFromSession();
        // Every fetch here is self-limiting: inside the server's own cache window each returns
        // without asking anything, so opening this subtab repeatedly costs nothing.
        if (_session?.Status == MultiplayerSession.SessionStatus.SignedIn)
        {
            // THE MOD CATALOGUE COMES FIRST, and it was missing entirely. Its only caller was
            // RefreshStatsForMod, reached from the three click handlers — so the row was drawn
            // from installed mods alone, and the moment the user touched a chip the payload
            // landed, added the chips the server knows about and unfolded the 1v1/Teams capsule
            // RIGHT UNDER the finger that had just clicked. Asking on entry lets the row settle
            // before anybody aims at it.
            // THROUGH RefreshStatsForMod, not a second copy of its list. This handler used to
            // repeat the fetches by hand, and the copies drifted the moment one of them gained
            // a sixth: the statistics page's own community payload was kicked only on a mod
            // CHANGE, so opening the subtab left it on "Loading..." until you touched a chip.
            RefreshStatsForMod();
            _ = MaybeUploadDecksAsync();
        }
    }

    // ===================================================================
    // Tournaments
    // ===================================================================
    //
    // The launcher renders and asks; it decides nothing. Whether a match is playable, who
    // won, who may seed — all of that arrives from the server and is only read here. The
    // buttons hidden by TournamentPermissions are a courtesy: every one of those actions
    // is re-checked server-side and answers 403 if it was wrong to offer.

    private TournamentListResponse? _tournaments;
    private TournamentDetail? _tournamentDetail;
    private string? _selectedTournamentId;
    private DateTime _tournamentsFetchedUtc = DateTime.MinValue;

    /// <summary>True when this backend has no tournaments at all — a 404 rather than a
    /// failure. Rendered as a sentence, never as an error.</summary>
    private bool _tournamentsUnavailable;

    /// <summary>True while the subtab is showing fabricated tournaments.
    ///
    /// <para>It does three things and no more: it lets the list render without a session, it
    /// keeps the create button visible, and it makes every action inert. Nothing else in the
    /// tab behaves differently, which is the point — what you are looking at is the real
    /// rendering path with different data in it.</para></summary>
    private bool _demoTournaments;

    /// <summary>Same self-limiting window the civ table uses. Stamped AFTER the await, so
    /// a slow request cannot make the next one look fresh.</summary>
    private static readonly TimeSpan TournamentsRefreshWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Fill the Tournaments subtab with fabricated brackets and show it.
    ///
    /// <para>Reached two ways, one method — the <c>--demo-tournaments</c> argument and a button
    /// in Settings → Developer. That is the same contract <c>PreviewNotificationToasts</c>
    /// established and for the same reasons: the argument is the only way to reach this without
    /// navigating menus, which is what makes a screenshot scriptable, and the button is the only
    /// way to reach it while a launcher is already running, because the single-instance mutex
    /// kills a second process before its window exists.</para>
    ///
    /// <para>It paints through the ordinary render path, so what appears is what will appear.
    /// What it does NOT do is go through <c>RefreshTournamentsAsync</c>, which would need a
    /// session — the fixture is assigned straight into the fields the renderer reads.</para>
    /// </summary>
    /// <param name="scenario">Which sample to open on, or null for the first. Passed by
    /// <c>--demo-tournaments=&lt;name&gt;</c> so a screenshot of any one of them can be taken
    /// without a click - see <see cref="TournamentDemoData.ScenarioByName"/>.</param>
    public void ShowDemoTournaments(string? scenario = null)
    {
        _demoTournaments = true;
        _activeSubtab = Subtab.Tournaments;
        _tournamentShowEntrants = false;

        _tournaments = TournamentDemoData.List();
        var picked = TournamentDemoData.ScenarioByName(scenario) ?? TournamentDemoData.Running();
        _selectedTournamentId = picked.Id;
        _tournamentDetail = picked;

        // Open with the live tie selected, when the sample has one. The actions moved out of
        // the cells into a bar that only exists for a selected cell, so a sample built to
        // show a match being played would otherwise open showing everything except that.
        _selectedMatchId = picked.Matches?
            .FirstOrDefault(m => m.Lobby != null
                                 && string.Equals(m.Status, "pending", StringComparison.Ordinal))?
            .Id;
        _tournamentsUnavailable = false;

        // Not cosmetic. Without it the next SubtabTournaments_Click runs a real fetch, which
        // fails signed out and replaces the fixture with an empty list.
        _tournamentsFetchedUtc = DateTime.UtcNow;

        DiagnosticLog.Write(
            $"Tournaments: showing DEMO data ({picked.Id}) — nothing here came from a server.");

        UpdateSubtabHighlights();
        ShowSubtabView();
        RenderTournamentsTab();

        if (!string.IsNullOrWhiteSpace(scenario)
            && "dialog".StartsWith(scenario!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            // After the tab has painted, so the dialog opens over a populated window rather
            // than over an empty one.
            Dispatcher.BeginInvoke(new Action(ShowDemoCreateDialog),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>True while the Statistics subtab is showing fabricated community figures.
    /// Like its tournament twin it does two things and no more: it stops a real fetch from
    /// replacing the fixture, and it puts a banner on the page.</summary>
    private bool _demoStats;

    /// <summary>
    /// Fill the Statistics subtab with fabricated figures and show it.
    ///
    /// <para>Reached the same two ways as the tournament preview and for the same reasons -
    /// the argument makes a screenshot scriptable, the Settings button works while a launcher
    /// is already open. Everything is assigned straight into the fields the renderer reads, so
    /// what appears is the real rendering path with different data in it.</para>
    /// </summary>
    /// <param name="scenario"><c>empty</c> for the state with no civilization data, anything
    /// else for the filled table.</param>
    public void ShowDemoStats(string? scenario = null)
    {
        _demoStats = true;
        _activeSubtab = Subtab.Stats;

        string want = (scenario ?? "").Trim();
        _demoStatsEmpty = want.Length > 0
                          && "empty".StartsWith(want, StringComparison.OrdinalIgnoreCase);
        bool empty = _demoStatsEmpty;

        // The TEAM ladder, without a click. It is the half of the page that did not exist
        // until now, and the half a screenshot is most likely to be wanted of.
        if (want.Length > 0 && "team".StartsWith(want, StringComparison.OrdinalIgnoreCase))
        {
            _statsMode = "team";
        }

        // The SECOND mod, reachable without a click. It is the only way to see that the mod
        // scope does anything at all: with one mod on screen a broken filter and a working
        // one draw the same page, which is why the fixture holds two.
        bool other = want.Length > 0
                     && "other".StartsWith(want, StringComparison.OrdinalIgnoreCase);
        if (other) _statsModId = Services.Multiplayer.StatsDemoData.SecondModId;

        // Everything the page draws, all at the same mod. A preview whose blocks disagreed
        // about which mod they were showing would be demonstrating the exact bug the mod
        // scope was added to fix.
        ApplyDemoStats();

        // Stamped so the subtab's own click does not immediately fetch over the fixture.
        _civStatsFetchedUtc = DateTime.UtcNow;
        _activityFetchedUtc = DateTime.UtcNow;
        _matchupsFetchedUtc = DateTime.UtcNow;
        _deckStatsFetchedUtc = DateTime.UtcNow;
        _statsCommunityFetchedUtc = DateTime.UtcNow;

        DiagnosticLog.Write(
            $"Stats: showing DEMO data ({(empty ? "no civs" : "full")}, {StatsMode()}) — "
            + "nothing came from a server.");

        UpdateSubtabHighlights();
        ShowSubtabView();
        RenderStatsTab();
    }

    /// <summary>Which preview scenario is on screen, so the two switches can rebuild it.</summary>
    private bool _demoStatsEmpty;

    /// <summary>
    /// Every fixture the page draws, all at the mod and mode currently selected.
    ///
    /// <para>Called again whenever either switch moves. Without it the preview blanked on the
    /// first click: the switches drop the payloads they were showing and ask for new ones, and
    /// in a preview nothing answers — leaving an empty page that looks exactly like the bug the
    /// switch was added to fix.</para>
    /// </summary>
    private void ApplyDemoStats()
    {
        // All at the SAME mod and mode. A preview whose blocks disagreed about which mod or
        // which ladder they were showing would be demonstrating the exact bug the two scopes
        // were added to fix.
        string mod = StatsModId();
        string mode = StatsMode();
        // BOTH, or the preview would leave the Rooms strip blank: they are different fields
        // now and only one of them is what the statistics page reads.
        _communityStats = Services.Multiplayer.StatsDemoData.Community(mod, mode);
        _statsCommunity = _communityStats;
        _civStats = _demoStatsEmpty
            ? Services.Multiplayer.StatsDemoData.NoCivStats(mod)
            : Services.Multiplayer.StatsDemoData.CivStats(mod, mode);
        _matchups = Services.Multiplayer.StatsDemoData.Matchups(mod, mode);
        _deckStats = Services.Multiplayer.StatsDemoData.Decks(mod);

        // Stamped so the subtab's own click does not immediately fetch over the fixture.
        _civStatsFetchedUtc = DateTime.UtcNow;
        _activityFetchedUtc = DateTime.UtcNow;
        _matchupsFetchedUtc = DateTime.UtcNow;
        _deckStatsFetchedUtc = DateTime.UtcNow;
        _statsCommunityFetchedUtc = DateTime.UtcNow;
    }

    private void SubtabTournaments_Click(object sender, RoutedEventArgs e)
    {
        _activeSubtab = Subtab.Tournaments;
        RefreshFromSession();
        _ = RefreshTournamentsAsync();
    }

    /// <summary>
    /// Fetch the list, and the selected tournament with it.
    ///
    /// <para>Windowed rather than timed. The per-IP budget is shared behind a Radmin NAT,
    /// so nothing here may poll: this runs when the subtab is opened and when a push says
    /// something moved, and returns immediately in between.</para>
    /// </summary>
    private async Task RefreshTournamentsAsync(bool force = false)
    {
        if (_session?.Api == null) return;
        if (!force && DateTime.UtcNow - _tournamentsFetchedUtc < TournamentsRefreshWindow) return;

        try
        {
            _tournaments = await _session.Api.ListTournamentsAsync();
            _tournamentsUnavailable = false;

            if (!string.IsNullOrEmpty(_selectedTournamentId))
            {
                try
                {
                    _tournamentDetail = await _session.Api.GetTournamentAsync(_selectedTournamentId!);
                }
                catch (LobbyApiException ex) when (ex.Code == "not_found")
                {
                    // Cancelled or archived while we were looking at it.
                    _tournamentDetail = null;
                    _selectedTournamentId = null;
                }
            }
        }
        catch (LobbyApiException ex) when (ex.Code == "not_found")
        {
            // A backend that predates tournaments. Not an error — a state.
            _tournamentsUnavailable = true;
            _tournaments = null;
            _tournamentDetail = null;
        }
        catch
        {
            // Anything else: keep whatever we had and say nothing. The subtab is not
            // load-bearing enough to interrupt somebody over.
        }
        finally
        {
            _tournamentsFetchedUtc = DateTime.UtcNow;
        }

        if (_activeSubtab == Subtab.Tournaments) RenderTournamentsTab();
    }

    private void RenderTournamentsTab()
    {
        if (TournamentsTitleText != null)
            TournamentsTitleText.Text = Strings.Get("MpSubtabTournaments");
        if (TournamentCreateButton != null)
        {
            // The plus the string table already assumed: its own comment calls this element
            // "the list's '+ New tournament'". Two spaces after it, exactly as the rooms
            // button spells the same idiom.
            TournamentCreateButton.Content = "+  " + Strings.Get("MpTournamentCreate");
            TournamentCreateButton.Visibility =
                (_demoTournaments
                 || _session?.Status == MultiplayerSession.SessionStatus.SignedIn)
                && !_tournamentsUnavailable
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        RenderTournamentsList();
        RenderTournamentDetail();
    }

    private void RenderTournamentsList()
    {
        var panel = TournamentsListPanel;
        if (panel == null) return;
        panel.Children.Clear();

        if (_tournamentsUnavailable)
        {
            panel.Children.Add(Hint(Strings.Get("MpTournamentsUnavailable")));
            return;
        }
        // The caller's own drafts first: they are invisible to everybody else, and the
        // person who just created one is looking for exactly this.
        var drafts = _tournaments?.Drafts ?? new List<TournamentSummary>();
        var open = _tournaments?.Tournaments ?? new List<TournamentSummary>();

        if (drafts.Count == 0 && open.Count == 0)
        {
            panel.Children.Add(Hint(Strings.Get("MpTournamentsEmpty")));
            return;
        }

        foreach (var t in drafts) panel.Children.Add(BuildTournamentCard(t, isDraft: true));
        foreach (var t in open) panel.Children.Add(BuildTournamentCard(t, isDraft: false));
    }

    /// <summary>
    /// The line a tournaments panel draws when it has nothing to list: not signed in, nothing
    /// created yet, the server unreachable, or no bracket picked.
    ///
    /// <para><b>It set neither Foreground nor FontSize, and both were bugs that only a
    /// screenshot could find.</b> There is no implicit TextBlock style with setters
    /// (<c>Styles/Text.xaml</c> says so in its own header) and nothing on this path sets
    /// <c>TextElement.Foreground</c> on an ancestor, so the text inherited WPF's default
    /// BLACK and was drawn italic on navy - effectively invisible. The missing size was the
    /// quieter half: a TextBlock with no FontSize sits at WPF's 12, which no token multiplies,
    /// so this was the one piece of the tab that ignored the text-size setting entirely.</para>
    ///
    /// <para>Internal so <c>MultiplayerTabHintTests</c> can assert both are set. A green build
    /// proved nothing about either.</para>
    /// </summary>
    internal static TextBlock Hint(string text)
    {
        var hint = new TextBlock
        {
            Text = text,
            Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 10, 2, 0),
            FontStyle = FontStyles.Italic,
        };
        // A reference rather than a read, so the size follows a change made while this is on
        // screen - the settings window sits over this very tab.
        hint.SetResourceReference(TextBlock.FontSizeProperty, "MpBodySize");
        return hint;
    }

    /// <summary>
    /// One row of the list: which tournament, what state, and what it wants from me.
    ///
    /// <para>Built in code, so <c>DialogXamlTests</c> reaches it by calling this rather than
    /// by parsing XAML.</para>
    ///
    /// <para>The bottom line is the change worth having. Every row used to repeat
    /// <c>state - format - places</c>, which is three facts none of which distinguish a
    /// tournament I own with two people waiting on me from one I am not even in.</para>
    ///
    /// <para><b>"It is your turn" only appears on the open one</b>, and that is a data limit
    /// rather than a choice: knowing whose turn it is needs the bracket, and the list is one
    /// anonymous payload shared and cached for every caller. Applications DO appear on every
    /// row, because <c>pending_count</c> is a property of the tournament and not of who is
    /// looking.</para>
    /// </summary>
    internal Border BuildTournamentCard(TournamentSummary t, bool isDraft)
    {
        bool selected = string.Equals(t.Id, _selectedTournamentId, StringComparison.Ordinal);
        var me = _demoTournaments ? TournamentDemoData.MeUserId : _session?.CurrentUser?.Id;
        bool owned = TournamentPermissions.IsOwner(t, me);

        var stack = new StackPanel();

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Border
        {
            Width = 7, Height = 7,
            CornerRadius = new CornerRadius(3.5),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        dot.SetResourceReference(Border.BackgroundProperty, StatusDotBrush(t.Status));
        Grid.SetColumn(dot, 0);
        top.Children.Add(dot);

        var title = new TextBlock
        {
            Text = t.Name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetResourceReference(TextBlock.FontSizeProperty, "MpBodySize");
        title.SetResourceReference(TextBlock.ForegroundProperty, "MpTextHeading");
        Grid.SetColumn(title, 1);
        top.Children.Add(title);

        if (owned)
        {
            var tag = BuildTag(Strings.Get("MpTournamentMineTag"), "MpActionText", "MpActionSoftBg");
            tag.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(tag, 2);
            top.Children.Add(tag);
        }

        stack.Children.Add(top);

        var bottom = new Grid { Margin = new Thickness(15, 4, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Confirmed out of capacity, not ENTRANTS out of capacity: the entrant count
        // includes the waiting list, so "10/8" reads as a number that has overflowed
        // rather than as eight places taken and two people queueing.
        var figures = new TextBlock
        {
            Text = $"{t.Format ?? ""}"
                   + (t.Capacity is int cap ? $"  \u00b7  {t.ConfirmedCount ?? 0}/{cap}" : ""),
            VerticalAlignment = VerticalAlignment.Center,
        };
        figures.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        figures.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
        figures.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
        Grid.SetColumn(figures, 0);
        bottom.Children.Add(figures);

        string? note = null;
        string noteInk = "MpTextGhost";

        // The open tournament is the only one whose bracket we hold, so it is the only one
        // that can be asked whose turn it is.
        if (selected && _tournamentDetail != null
            && MyPlayableRound(_tournamentDetail, me) != null)
        {
            note = Strings.Get("MpTournamentYourTurn");
            noteInk = "MpActionText";
        }
        else if (owned && t.PendingCount is int p && p > 0)
        {
            note = p == 1
                ? Strings.Get("MpTournamentRequestsOne")
                : Strings.Format("MpTournamentRequests", p);
            noteInk = "MpCautionText";
        }
        else if (isDraft)
        {
            note = StatusLabel(t.Status, true);
        }

        if (note != null)
        {
            var line = new TextBlock
            {
                Text = note,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            line.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
            line.SetResourceReference(TextBlock.ForegroundProperty, noteInk);
            Grid.SetColumn(line, 1);
            bottom.Children.Add(line);
        }

        stack.Children.Add(bottom);

        var card = new Border
        {
            Padding = new Thickness(11, 9, 11, 10),
            Margin = new Thickness(0, 0, 0, 7),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = stack,
            Tag = t.Id,
        };
        card.SetResourceReference(Border.BackgroundProperty, selected ? "MpRowHighlight" : "MpPanel");
        card.SetResourceReference(Border.BorderBrushProperty,
            selected ? "MpOwnRowRim" : "MpRimFaint");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusRow");
        card.MouseLeftButtonUp += (_, _) => { _ = SelectTournamentAsync(t.Id); };
        return card;
    }

    /// <summary>The colour of a status, as a dot. Same mapping the header's pill uses.</summary>
    private static string StatusDotBrush(string? status) => status switch
    {
        "registration" => "MpCaution",
        "ready" => "MpCaution",
        "running" => "MpOk",
        "cancelled" => "MpDestructive",
        "abandoned" => "MpDestructive",
        _ => "MpNoResult",
    };

    private static string StatusLabel(string? status, bool isDraft) => status switch
    {
        "draft" => Strings.Get(isDraft ? "MpTournamentStatusDraft" : "MpTournamentStatusDraft"),
        "registration" => Strings.Get("MpTournamentStatusRegistration"),
        "ready" => Strings.Get("MpTournamentStatusReady"),
        "running" => Strings.Get("MpTournamentStatusRunning"),
        "finished" => Strings.Get("MpTournamentStatusFinished"),
        "cancelled" => Strings.Get("MpTournamentStatusCancelled"),
        "abandoned" => Strings.Get("MpTournamentStatusAbandoned"),
        _ => status ?? "",
    };

    private async Task SelectTournamentAsync(string id)
    {
        _selectedTournamentId = id;

        if (_demoTournaments)
        {
            _tournamentDetail = TournamentDemoData.ById(id);
            RenderTournamentsTab();
            return;
        }

        if (_session?.Api == null) return;
        try
        {
            _tournamentDetail = await _session.Api.GetTournamentAsync(id);
        }
        catch
        {
            _tournamentDetail = null;
        }
        RenderTournamentsTab();
    }

    /// <summary>True while the running tournament's entrant table is showing instead of its
    /// bracket. Once a bracket exists the entrants disappear behind it, and the seeds, the
    /// waiting list and who withdrew are still worth being able to look at.</summary>
    private bool _tournamentShowEntrants;

    private void RenderTournamentDetail()
    {
        var panel = TournamentDetailPanel;
        if (panel == null) return;
        panel.Children.Clear();

        var t = _tournamentDetail;
        if (t == null)
        {
            if (!_tournamentsUnavailable)
                panel.Children.Add(Hint(Strings.Get("MpTournamentsPickOne")));
            return;
        }

        // In the demo there is nobody signed in, so the cards would all render as somebody
        // else's - which hides half the states worth looking at. The fixture is written from
        // this fake viewer's point of view.
        var me = _demoTournaments ? TournamentDemoData.MeUserId : _session?.CurrentUser?.Id;

        if (_demoTournaments)
        {
            // A populated bracket looks exactly like a real one, and a screenshot without this
            // line ends up somewhere looking like tournaments already work.
            var banner = new Border
            {
                Padding = new Thickness(11, 9, 11, 9),
                Margin = new Thickness(0, 0, 0, 13),
                BorderThickness = new Thickness(1),
            };
            banner.SetResourceReference(Border.CornerRadiusProperty, "RadiusControl");
            banner.SetResourceReference(Border.BackgroundProperty, "MpCautionBg");
            banner.SetResourceReference(Border.BorderBrushProperty, "MpCautionRim");
            var bannerText = new TextBlock
            {
                Text = Strings.Get("MpTournamentDemoBanner"),
                TextWrapping = TextWrapping.Wrap,
            };
            bannerText.SetResourceReference(TextBlock.FontSizeProperty, "MpLabelSize");
            bannerText.SetResourceReference(TextBlock.ForegroundProperty, "MpCautionText");
            banner.Child = bannerText;
            panel.Children.Add(banner);
        }

        panel.Children.Add(BuildTournamentHeader(t, me));

        // The four-step bar, while there are still steps left to take. Once the bracket is
        // drawn the bracket itself is the progress, and a bar repeating that would be noise.
        if (t.Status is "draft" or "registration" or "ready")
        {
            panel.Children.Add(BuildTournamentProgress(t));
        }

        panel.Children.Add(BuildTournamentActions(t, me));

        // Between the actions and the bracket, so it stays on screen in BOTH the bracket and
        // the entrants view - who is running this is not a fact about either one.
        var managers = BuildManagersStrip(t, me);
        if (managers != null) panel.Children.Add(managers);

        if (!string.IsNullOrEmpty(t.WinnerEntrantId))
        {
            var champ = new TextBlock
            {
                Text = Strings.Format("MpTournamentChampion", EntrantName(t, t.WinnerEntrantId)),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
            };
            champ.SetResourceReference(TextBlock.FontSizeProperty, "MpBodySize");
            champ.SetResourceReference(TextBlock.ForegroundProperty, "MpTextHeading");
            panel.Children.Add(champ);
        }

        bool hasBracket = t.Matches is { Count: > 0 };
        panel.Children.Add(hasBracket && !_tournamentShowEntrants
            ? BuildBracketPanel(t, me)
            : BuildEntrantsList(t, me));

        // Cancelling lives at the FOOT, behind a red rim, and away from everything else. It
        // used to sit beside "Enter" in the same blue at the same weight, two centimetres
        // from the button somebody presses to take part.
        if (TournamentPermissions.CanCancel(t, me))
        {
            panel.Children.Add(BuildTournamentDangerZone(t, me));
        }
    }

    /// <summary>
    /// The head of the detail pane: which tournament, what state, and what it wants from me.
    ///
    /// <para>The capsule on the right is the point of the whole row. "It is your turn" was
    /// findable only by reading the bracket until you spotted your own name, which in a
    /// sixteen-entrant draw means reading fifteen cards.</para>
    /// </summary>
    /// <remarks><c>internal</c> for the same reason the bracket builders are: it is
    /// assembled in code, so nothing checks at compile time that the capsule and the
    /// entrant toggle are actually in it. <c>DialogXamlTests</c> calls this.</remarks>
    internal UIElement BuildTournamentHeader(TournamentDetail t, string? me)
    {
        // ONE COLUMN, top to bottom: the name, the figures, then whatever this tournament
        // wants from me.
        //
        // The reference puts that last row to the RIGHT of the name and it was built that
        // way first, as a Grid and then as a DockPanel. Neither survives here, for a reason
        // worth writing down: this pane's width is whatever the widest thing inside it
        // happens to be - the bracket - and it sits in a viewer that does not scroll
        // sideways. Anything right-aligned in it is therefore positioned against a width the
        // window may not have, and is clipped away silently. Both versions produced an
        // element that was present, visible, correctly sized, and nowhere on the screen.
        //
        // Stacked, nothing can be pushed off an edge. And at the width this pane actually
        // gets - about 800px on the launcher's own default window - a serif title, a capsule
        // and a button were never going to share a line anyway.
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

        var left = new StackPanel();

        var name = new TextBlock
        {
            Text = t.Name,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        name.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        name.SetResourceReference(TextBlock.FontSizeProperty, "MpTournamentNameSize");
        name.SetResourceReference(TextBlock.ForegroundProperty, "MpTextHeading");
        left.Children.Add(name);

        var meta = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 7, 0, 0),
        };
        meta.Children.Add(BuildStatusPill(t.Status));

        // Format, places and round, in monospace and in one string: three figures that are
        // read together and never compared down a column.
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(t.Format)) parts.Add(t.Format!);
        if (t.Capacity is int cap)
            parts.Add(Strings.Format("MpTournamentPlacesShort", t.ConfirmedCount ?? 0, cap));
        // Only when the server actually sent a total: it is null until the bracket is drawn,
        // and "round 2 of 0" is worse than not saying it.
        int? round = CurrentRound(t);
        if (round is int r && t.RoundsTotal is int total && total > 0)
            parts.Add(Strings.Format("MpTournamentRoundOfTotal", r, total));

        if (parts.Count > 0)
        {
            var figures = new TextBlock
            {
                Text = string.Join("  \u00b7  ", parts),
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            figures.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            figures.SetResourceReference(TextBlock.FontSizeProperty, "MpFigureSize");
            figures.SetResourceReference(TextBlock.ForegroundProperty, "MpTextMuted");
            meta.Children.Add(figures);
        }

        if (TournamentPermissions.IsOwner(t, me))
        {
            var mine = new TextBlock
            {
                Text = "\u00b7  " + Strings.Get("MpTournamentCreatedByYou"),
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            mine.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
            mine.SetResourceReference(TextBlock.ForegroundProperty, "MpTextMuted");
            meta.Children.Add(mine);
        }

        left.Children.Add(meta);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 11, 0, 0),
        };

        var turn = MyPlayableRound(t, me);
        if (turn != null)
        {
            actions.Children.Add(BuildTurnCapsule(turn));
        }

        // Once a bracket exists it replaces the entrant table completely, and the seeds, the
        // waiting list and who pulled out stop being reachable. This is the way back.
        if (t.Matches is { Count: > 0 })
        {
            var toggle = new Button
            {
                Content = Strings.Get(_tournamentShowEntrants
                    ? "MpSubtabTournaments" : "MpTournamentSeeEntrants"),
                Margin = new Thickness(turn != null ? 9 : 0, 0, 0, 0),
            };
            toggle.SetResourceReference(FrameworkElement.StyleProperty, "MpGhostButton");
            toggle.Click += (_, _) =>
            {
                _tournamentShowEntrants = !_tournamentShowEntrants;
                RenderTournamentDetail();
            };
            actions.Children.Add(toggle);
        }

        row.Children.Add(left);
        // Only when there is something in it: an empty row still costs its margin, and on a
        // tournament that wants nothing from the viewer the header should just stop.
        if (actions.Children.Count > 0) row.Children.Add(actions);
        return row;
    }

    /// <summary>The highest round with a decided match, which is the one being played.</summary>
    private static int? CurrentRound(TournamentDetail t)
    {
        var matches = t.Matches;
        if (matches == null || matches.Count == 0) return null;
        var live = matches.Where(m => m.Status == "pending").Select(m => m.Round).ToList();
        return live.Count > 0 ? live.Min() : matches.Max(m => m.Round);
    }

    /// <summary>The localised name of the round my next match is in, or null if I have none.</summary>
    private static string? MyPlayableRound(TournamentDetail t, string? me)
    {
        var matches = t.Matches;
        if (matches == null) return null;
        foreach (var m in matches)
        {
            var state = MatchCards.For(m, me, t.Entrants);
            if (!Actionable(state)) continue;
            return Strings.Format(
                BracketLayout.RoundLabelKey(m.Round, t.RoundsTotal), m.Round).ToLowerInvariant();
        }
        return null;
    }

    private static Border BuildTurnCapsule(string roundName)
    {
        var capsule = new Border
        {
            Padding = new Thickness(11, 7, 12, 7),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        capsule.SetResourceReference(Border.CornerRadiusProperty, "RadiusControl");
        capsule.SetResourceReference(Border.BackgroundProperty, "MpActionSoftBg");
        capsule.SetResourceReference(Border.BorderBrushProperty, "MpActionSoftRim");

        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new Border
        {
            Width = 6, Height = 6,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        dot.SetResourceReference(Border.BackgroundProperty, "MpOk");
        inner.Children.Add(dot);

        var label = new TextBlock
        {
            Text = Strings.Format("MpTournamentYourTurnIn", roundName),
            FontWeight = FontWeights.SemiBold,
        };
        label.SetResourceReference(TextBlock.FontSizeProperty, "MpLabelSize");
        label.SetResourceReference(TextBlock.ForegroundProperty, "MpActionText");
        inner.Children.Add(label);

        capsule.Child = inner;
        return capsule;
    }

    /// <summary>A status as a coloured pill with a dot, the way rooms and ratings already do it.</summary>
    private static Border BuildStatusPill(string? status)
    {
        var (dot, ink, bg) = status switch
        {
            "registration" => ("MpCaution", "MpCautionText", "MpCautionBg"),
            "ready" => ("MpCaution", "MpCautionText", "MpCautionBg"),
            "running" => ("MpOk", "MpOkText", "MpChipOkBg"),
            "finished" => ("MpNoResult", "MpTextMuted", "MpNeutralBadgeBg"),
            "cancelled" => ("MpDestructive", "MpDestructiveText", "MpNeutralBadgeBg"),
            "abandoned" => ("MpDestructive", "MpDestructiveText", "MpNeutralBadgeBg"),
            _ => ("MpNoResult", "MpTextMuted", "MpNeutralBadgeBg"),
        };

        var pill = new Border
        {
            Padding = new Thickness(9, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        pill.SetResourceReference(Border.CornerRadiusProperty, "RadiusPill");
        pill.SetResourceReference(Border.BackgroundProperty, bg);

        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        var marker = new Border
        {
            Width = 5, Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        marker.SetResourceReference(Border.BackgroundProperty, dot);
        inner.Children.Add(marker);

        var label = new TextBlock
        {
            Text = StatusLabel(status, false),
            FontWeight = FontWeights.SemiBold,
        };
        label.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
        label.SetResourceReference(TextBlock.ForegroundProperty, ink);
        inner.Children.Add(label);

        pill.Child = inner;
        return pill;
    }

    /// <summary>
    /// Created, Registration, Seeds, Under way.
    ///
    /// <para>The third step is why this exists. <c>CanStart</c> refuses until every confirmed
    /// entrant carries a seed, and before this the screen said nothing at all about it: the
    /// button was simply absent and the tournament simply did not begin.</para>
    /// </summary>
    private static UIElement BuildTournamentProgress(TournamentDetail t)
    {
        bool seeded = (t.Entrants ?? new List<TournamentEntrant>())
            .Where(e => e.Status == "confirmed")
            .All(e => e.Seed.HasValue);

        int reached = t.Status switch
        {
            "draft" => 0,
            "registration" => 1,
            "ready" => seeded ? 2 : 1,
            _ => 3,
        };

        var steps = new[]
        {
            "MpTournamentStepCreated", "MpTournamentStepRegistration",
            "MpTournamentStepSeeds", "MpTournamentStepRunning",
        };

        var box = new Border
        {
            Padding = new Thickness(14, 11, 16, 11),
            Margin = new Thickness(0, 0, 0, 14),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        box.SetResourceReference(Border.CornerRadiusProperty, "RadiusPanel");
        box.SetResourceReference(Border.BackgroundProperty, "MpPanel");
        box.SetResourceReference(Border.BorderBrushProperty, "MpRimFaint");

        // A left-aligned row with fixed gaps, not a stretched grid. The pane is about 500
        // units wide on the launcher's own window; rails that grew to fill it pushed the
        // fourth step past the right edge, where it was clipped - and the fourth step is
        // the one that says the tournament has started.
        var line = new StackPanel { Orientation = Orientation.Horizontal };

        for (int i = 0; i < steps.Length; i++)
        {
            bool done = i < reached;
            bool here = i == reached;

            var group = new StackPanel { Orientation = Orientation.Horizontal };

            var bullet = new Border
            {
                Width = 18, Height = 18,
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
            };
            bullet.SetResourceReference(Border.BackgroundProperty,
                done ? "MpChipOkBg" : here ? "MpCautionBg" : "MpNeutralBadgeBg");
            bullet.SetResourceReference(Border.BorderBrushProperty,
                done ? "MpChipOkRim" : here ? "MpCautionRim" : "MpRimFaint");

            var mark = new TextBlock
            {
                // A tick for what is behind us and the step's own number for the rest: the
                // number is what makes "you are on the third of four" readable at a glance.
                Text = done ? "\u2713" : (i + 1).ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
            };
            mark.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            mark.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
            mark.SetResourceReference(TextBlock.ForegroundProperty,
                done ? "MpOkText" : here ? "MpCautionText" : "MpTextGhost");
            bullet.Child = mark;
            group.Children.Add(bullet);

            var label = new TextBlock
            {
                Text = Strings.Get(steps[i]),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = here ? FontWeights.SemiBold : FontWeights.Normal,
            };
            label.SetResourceReference(TextBlock.FontSizeProperty, "MpLabelSize");
            label.SetResourceReference(TextBlock.ForegroundProperty,
                done ? "MpTextSecondary" : here ? "MpTextHeading" : "MpTextGhost");
            group.Children.Add(label);

            line.Children.Add(group);

            if (i < steps.Length - 1)
            {
                var rail = new Border
                {
                    Width = 22,
                    Height = 1,
                    Margin = new Thickness(9, 0, 9, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                rail.SetResourceReference(Border.BackgroundProperty,
                    i < reached ? "MpChipOkRim" : "MpRimHair");
                line.Children.Add(rail);
            }
        }

        box.Child = line;
        return box;
    }

    /// <summary>
    /// The actions, separated by CONSEQUENCE rather than laid out as one row of links.
    ///
    /// <para>One filled button - the single thing that moves the tournament forward from
    /// where it is. One ghost button for taking part or pulling out. Everything else behind
    /// a menu. And cancelling is not here at all: it is in the danger zone at the foot.</para>
    ///
    /// <para>What this replaces put up to seven identical blue links in a row, so "Cancel
    /// tournament" and "Enter" were the same size, the same colour and two centimetres
    /// apart. Every one of them is still gated by <see cref="TournamentPermissions"/> and
    /// still re-checked by the server.</para>
    /// </summary>
    private UIElement BuildTournamentActions(TournamentDetail t, string? me)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        Button Make(string key, string style, Func<Task> action)
        {
            var b = new Button { Content = Strings.Get(key), Margin = new Thickness(0, 0, 9, 0) };
            b.SetResourceReference(FrameworkElement.StyleProperty, style);
            b.Click += (_, _) => { _ = RunTournamentActionAsync(action); };
            return b;
        }

        // ---- the primary: exactly one, and only if this state has a forward move.
        string? nextLine = null;
        if (TournamentPermissions.CanOpenRegistration(t, me))
        {
            row.Children.Add(Make("MpTournamentOpenRegistration", "MpPrimaryButton",
                () => _session!.Api!.OpenTournamentRegistrationAsync(t.Id)));
            nextLine = Strings.Get("MpTournamentNextOpen");
        }
        else if (TournamentPermissions.CanCloseRegistration(t, me))
        {
            row.Children.Add(Make("MpTournamentCloseRegistration", "MpPrimaryButton",
                () => _session!.Api!.CloseTournamentRegistrationAsync(t.Id)));
            nextLine = Strings.Get("MpTournamentNextClose");
        }
        else if (TournamentPermissions.CanStart(t, me))
        {
            row.Children.Add(Make("MpTournamentStart", "MpPrimaryButton",
                () => _session!.Api!.StartTournamentAsync(t.Id)));
            nextLine = Strings.Get("MpTournamentNextStart");
        }
        else if (TournamentPermissions.CanSeed(t, me))
        {
            // Seeding is the forward move here, and the sentence under it says what is
            // still missing - which is the hole this whole redesign was built to close.
            row.Children.Add(Make("MpTournamentSeed", "MpPrimaryButton",
                () => _session!.Api!.SeedTournamentAsync(t.Id)));
            nextLine = SeedBlocker(t) ?? Strings.Get("MpTournamentNextSeed");
        }

        // ---- the secondary: taking part, or stepping out.
        if (TournamentPermissions.CanEnter(t, me))
        {
            row.Children.Add(Make("MpTournamentEnter", "MpGhostButton",
                () => EnterTournamentAsync(t)));
        }
        else if (TournamentPermissions.CanWithdraw(t, me))
        {
            var mine = TournamentPermissions.MyEntrant(t, me);
            if (mine != null)
            {
                row.Children.Add(Make("MpTournamentWithdraw", "MpGhostButton",
                    () => _session!.Api!.WithdrawFromTournamentAsync(t.Id, mine.Id)));
            }
        }

        // ---- the rest, behind a menu. Re-seeding a drawn bracket and disqualifying
        // somebody are both the owner's, both rare, and neither belongs beside a primary.
        var extras = new List<(string Key, Func<Task> Action)>();
        // Seeding again once the bracket CAN be drawn. It is the only case where a second
        // owner action is still available and is not the forward move, and it stays reachable
        // because a draw somebody dislikes is a thing that happens.
        if (TournamentPermissions.CanStart(t, me))
        {
            extras.Add(("MpTournamentSeed", () => _session!.Api!.SeedTournamentAsync(t.Id)));
        }

        if (extras.Count > 0)
        {
            var menu = new Button
            {
                Content = "\u22ef",
                MinWidth = 38,
                ToolTip = TooltipHelper.Wrap(Strings.Get("MpTournamentMoreActions")),
            };
            menu.SetResourceReference(FrameworkElement.StyleProperty, "MpGhostButton");
            var flyout = new ContextMenu();
            foreach (var (key, action) in extras)
            {
                var item = new MenuItem { Header = Strings.Get(key) };
                var captured = action;
                item.Click += (_, _) => { _ = RunTournamentActionAsync(captured); };
                flyout.Items.Add(item);
            }
            menu.Click += (_, _) =>
            {
                flyout.PlacementTarget = menu;
                flyout.IsOpen = true;
            };
            row.Children.Add(menu);
        }

        if (row.Children.Count > 0) stack.Children.Add(row);

        if (nextLine != null)
        {
            var hint = new TextBlock
            {
                Text = nextLine,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(1, 8, 0, 0),
                MaxWidth = 520,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            hint.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MpTextMuted");
            stack.Children.Add(hint);
        }

        return stack;
    }

    /// <summary>Why the bracket cannot be drawn yet, in words, or null when it can.</summary>
    private static string? SeedBlocker(TournamentDetail t)
    {
        var playing = (t.Entrants ?? new List<TournamentEntrant>())
            .Where(e => e.Status == "confirmed")
            .ToList();
        if (playing.Count < 2) return Strings.Get("MpTournamentBlockedTooFew");

        int unseeded = playing.Count(e => !e.Seed.HasValue);
        return unseeded > 0
            ? Strings.Format("MpTournamentBlockedSeeds", unseeded)
            : null;
    }

    /// <summary>
    /// Cancelling, at the foot, behind a red rim, with its consequences written out.
    ///
    /// <para>What it does NOT offer is undoing a played result. That is the maintainer's
    /// CLI and not the owner's, and cancelling a tournament does not un-rate the matches
    /// already played in it - which is exactly the sort of thing somebody assumes the
    /// opposite of unless the button says so.</para>
    /// </summary>
    private UIElement BuildTournamentDangerZone(TournamentDetail t, string? me)
    {
        var box = new Border
        {
            Padding = new Thickness(14, 12, 14, 13),
            Margin = new Thickness(0, 18, 0, 8),
            BorderThickness = new Thickness(1),
        };
        box.SetResourceReference(Border.CornerRadiusProperty, "RadiusPanel");
        box.SetResourceReference(Border.BorderBrushProperty, "MpDestructiveRim");

        var stack = new StackPanel();

        var title = new TextBlock
        {
            Text = Strings.Get("MpTournamentDangerZone"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        };
        title.SetResourceReference(TextBlock.FontSizeProperty, "MpSectionLabelSize");
        title.SetResourceReference(TextBlock.ForegroundProperty, "MpDestructiveText");
        stack.Children.Add(title);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var words = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        var head = new TextBlock
        {
            Text = Strings.Get("MpTournamentCancelTitle"),
            FontWeight = FontWeights.SemiBold,
        };
        head.SetResourceReference(TextBlock.FontSizeProperty, "MpBodySize");
        head.SetResourceReference(TextBlock.ForegroundProperty, "MpTextHeading");
        words.Children.Add(head);

        var body = new TextBlock
        {
            Text = Strings.Format("MpTournamentCancelBody",
                (t.Entrants ?? new List<TournamentEntrant>())
                    .Count(e => e.Status is "confirmed" or "waitlist" or "pending")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        body.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
        body.SetResourceReference(TextBlock.ForegroundProperty, "MpTextMuted");
        words.Children.Add(body);

        Grid.SetColumn(words, 0);
        row.Children.Add(words);

        var cancel = new Button
        {
            Content = Strings.Get("MpTournamentCancel"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "MpGhostDangerButton");
        cancel.Click += (_, _) =>
        {
            _ = RunTournamentActionAsync(() => _session!.Api!.CancelTournamentAsync(t.Id));
        };
        Grid.SetColumn(cancel, 1);
        row.Children.Add(cancel);

        stack.Children.Add(row);
        box.Child = stack;
        return box;
    }

    private async Task EnterTournamentAsync(TournamentDetail t)
    {
        // A 1v1 needs no body at all — the server registers the caller. Team formats are
        // entered from the team picker, which passes its own body.
        await _session!.Api!.EnterTournamentAsync(t.Id);
    }

    /// <summary>Run one tournament action, refresh, and turn a refusal into a sentence.</summary>
    private async Task RunTournamentActionAsync(Func<Task> action)
    {
        // The preview's buttons are real but inert, exactly as the toast preview's are: one
        // that genuinely tried to seed a tournament nobody created would be worse than no
        // preview. Saying so beats a button that looks broken.
        if (_demoTournaments) { await ShowDemoInertNoticeAsync(); return; }

        try
        {
            await action();
        }
        catch (LobbyApiException ex)
        {
            // The server localises nothing; the launcher maps the CODE. Anything unknown
            // falls back to the server's own sentence rather than to silence.
            await MpAlertOverlay.NoticeAsync(
                TabRootGrid,
                Strings.Get("MpTournamentActionFailed"),
                TournamentErrorText(ex),
                Strings.Get("MpAlertOk"));
            return;
        }
        catch
        {
            return;
        }
        // Force: something definitely changed, so the 60-second window must not hide it.
        await RefreshTournamentsAsync(force: true);
    }

    /// <summary>Open the new-tournament dialog with nothing behind it, for the preview.</summary>
    internal void ShowDemoCreateDialog()
    {
        var dlg = new CreateTournamentDialog();
        try { dlg.Owner = Window.GetWindow(this); } catch { /* off-tree */ }
        dlg.ShowDialog();
    }

    /// <summary>Say that a demo button did nothing, rather than letting it look broken.</summary>
    /// <summary>
    /// Open the watch window on a match somebody else is playing.
    ///
    /// <para><b>Demo only, and it refuses rather than pretending.</b> Outside the fabricated
    /// tournaments there is nothing to show: the server would refuse this three times over
    /// — a lobby in <c>in_game</c> rejects every join before it looks at seats or roles, a
    /// lobby bound to a bracket slot admits only that slot's entrants with no owner exemption,
    /// and tournament rooms are created with zero spectator slots. So on live data this says
    /// so, in the same inert notice every other demo-shaped action uses, instead of opening a
    /// window filled with nothing.</para>
    /// </summary>
    private void OpenMatchWatch(TournamentDetail t, TournamentMatch m)
    {
        // Unreachable by construction - BuildBracketCard only asks for SuperviseRoom under the
        // fabricated tournaments - and kept anyway, because the alternative to a guard here is
        // a window full of nothing if that ever stops being true. No notice: there is no
        // button to have pressed.
        if (!_demoTournaments)
        {
            DiagnosticLog.Write("Match watch: asked for outside the demo; refused.");
            return;
        }

        var w = new MatchWatchWindow(t, m, TournamentDemoData.WatchSample());
        try { w.Owner = Window.GetWindow(this); } catch { /* off-tree */ }
        w.ShowDialog();
    }

    private async Task ShowDemoInertNoticeAsync()
    {
        DiagnosticLog.Write("Tournaments: demo button pressed; nothing was sent.");
        await MpAlertOverlay.NoticeAsync(
            TabRootGrid,
            Strings.Get("MpTournamentDemoInertTitle"),
            Strings.Get("MpTournamentDemoInert"),
            Strings.Get("MpAlertOk"));
    }

    private static string TournamentErrorText(LobbyApiException ex) => ex.Code switch
    {
        "tournament_closed" => Strings.Get("MpTournamentErrClosed"),
        "tournament_full" => Strings.Get("MpTournamentErrFull"),
        "tournament_limit_reached" => Strings.Get("MpTournamentErrLimit"),
        "tournament_match_not_ready" => Strings.Get("MpTournamentErrNotReady"),
        "tournament_not_participant" => Strings.Get("MpTournamentErrNotParticipant"),
        "already_entered" => Strings.Get("MpTournamentErrAlreadyEntered"),
        "roster_invalid" => Strings.Get("MpTournamentErrRoster"),
        "team_full" => Strings.Get("MpTeamErrFull"),
        "not_team_captain" => Strings.Get("MpTeamErrNotCaptain"),
        "forbidden" => Strings.Get("MpTournamentErrForbidden"),
        _ => ex.Message,
    };

    private static string EntrantName(TournamentDetail t, string? entrantId)
    {
        if (string.IsNullOrEmpty(entrantId)) return Strings.Get("MpTournamentTbd");
        var e = t.Entrants?.FirstOrDefault(x => string.Equals(x.Id, entrantId, StringComparison.Ordinal));
        return e?.DisplayName ?? Strings.Get("MpTournamentTbd");
    }

    /// <summary>Widest the entrant table is allowed to get.
    ///
    /// <para>Bounded, and not for taste: a vertical <c>StackPanel</c> hands its children the
    /// whole width, and without this the status column sits at the far right edge of the
    /// window - a thousand pixels from the name it describes, at which point it stops reading
    /// as a property of that name at all.</para></summary>
    private const double EntrantTableWidth = 760;

    /// <summary>
    /// Who is in, who is asking, and who is not playing - as three tables, not one list.
    ///
    /// <para>The statuses of <c>TournamentEntrant</c> are not points on one axis, so a single
    /// flat list forces the reader to sort them by eye. Applications wait on the owner and go
    /// FIRST; confirmed entrants are the bracket; withdrawn, rejected, disqualified and
    /// waitlisted are simply not in it.</para>
    ///
    /// <para>The status has its OWN column. It used to be a second <c>TextBlock</c> laid
    /// beside the name in a stretched grid, which is how the screen came to read
    /// "GorgoIn".</para>
    ///
    /// <para>And the seed is a column too, which is the hole this closes:
    /// <c>TournamentPermissions.CanStart</c> refuses while any confirmed entrant has no
    /// seed, so a tournament would sit there not starting with nothing on screen saying
    /// why.</para>
    /// </summary>
    internal UIElement BuildEntrantsList(TournamentDetail t, string? me)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
        var all = t.Entrants ?? new List<TournamentEntrant>();

        var pending = all.Where(e => e.Status == "pending").ToList();
        var confirmed = all.Where(e => e.Status == "confirmed").ToList();
        var outside = all.Where(e =>
            e.Status is "waitlist" or "withdrawn" or "rejected" or "disqualified").ToList();

        bool canDecide = pending.Count > 0
                         && TournamentPermissions.CanDecideEntrant(t, me, pending[0]);

        // Applications first, with an amber count: they are the only thing here waiting on
        // a decision, and burying them under the entrant table is how one sits unanswered.
        if (pending.Count > 0)
        {
            stack.Children.Add(BuildGroupLabel(
                Strings.Get("MpTournamentGroupRequests"),
                count: pending.Count,
                trailing: null));

            var card = BuildTableCard();
            for (int i = 0; i < pending.Count; i++)
            {
                card.Children.Add(BuildEntrantRow(
                    t, pending[i], me, canDecide, isLast: i == pending.Count - 1));
            }
            stack.Children.Add(WrapTable(card));
        }

        if (confirmed.Count > 0 || pending.Count == 0)
        {
            stack.Children.Add(BuildGroupLabel(
                Strings.Get("MpTournamentGroupIn"),
                count: null,
                trailing: t.Capacity is int cap
                    ? Strings.Format("MpTournamentPlacesShort", confirmed.Count, cap)
                    : null));

            var card = BuildTableCard();
            card.Children.Add(BuildEntrantHeader());
            if (confirmed.Count == 0)
            {
                card.Children.Add(BuildTableEmpty(Strings.Get("MpTournamentsEmpty")));
            }
            for (int i = 0; i < confirmed.Count; i++)
            {
                card.Children.Add(BuildEntrantRow(
                    t, confirmed[i], me, canDecide: false, isLast: i == confirmed.Count - 1));
            }
            stack.Children.Add(WrapTable(card));
        }

        if (outside.Count > 0)
        {
            stack.Children.Add(BuildGroupLabel(
                Strings.Get("MpTournamentGroupOut"), count: null, trailing: null));

            var card = BuildTableCard();
            for (int i = 0; i < outside.Count; i++)
            {
                card.Children.Add(BuildEntrantRow(
                    t, outside[i], me, canDecide: false, isLast: i == outside.Count - 1));
            }
            stack.Children.Add(WrapTable(card));
        }

        return stack;
    }

    /// <summary>
    /// The shared-size group that pins the actions column to one width across a card's rows
    /// and its header. Precedent in this file: the profile's history table does the same, and
    /// its comment records the constraint that applies here too - a SharedSizeGroup only works
    /// on an Auto or absolute width, never on a star.
    /// </summary>
    private const string EntrantActionsSizeGroup = "TournamentEntrantActions";

    /// <summary>
    /// One card, and one shared-size scope.
    ///
    /// <para>PER CARD, not one for the whole list. The applications card carries an
    /// "Accept"/"Reject" pair that is far wider than a lone "Withdraw"; a single scope would
    /// hand that width to the IN table as well and squeeze the entrant names for a button
    /// those rows do not have.</para>
    /// </summary>
    private static StackPanel BuildTableCard()
    {
        var card = new StackPanel();
        Grid.SetIsSharedSizeScope(card, true);
        return card;
    }

    private static Border WrapTable(StackPanel rows)
    {
        var card = new Border
        {
            Margin = new Thickness(0, 0, 0, 16),
            MaxWidth = EntrantTableWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
            Child = rows,
        };
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusPanel");
        card.SetResourceReference(Border.BackgroundProperty, "MpPanel");
        card.SetResourceReference(Border.BorderBrushProperty, "MpRimFaint");
        return card;
    }

    private static UIElement BuildGroupLabel(string text, int? count, string? trailing)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(1, 0, 0, 7),
        };

        var label = new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.FontSizeProperty, "MpSectionLabelSize");
        label.SetResourceReference(TextBlock.ForegroundProperty, "MpTextLabel");
        row.Children.Add(label);

        if (count is int n)
        {
            var badge = new Border
            {
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge.SetResourceReference(Border.CornerRadiusProperty, "RadiusPill");
            badge.SetResourceReference(Border.BackgroundProperty, "MpCautionBg");

            var num = new TextBlock { Text = n.ToString(), FontWeight = FontWeights.SemiBold };
            num.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            num.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
            num.SetResourceReference(TextBlock.ForegroundProperty, "MpCautionText");
            badge.Child = num;
            row.Children.Add(badge);
        }

        if (!string.IsNullOrEmpty(trailing))
        {
            var extra = new TextBlock
            {
                Text = trailing,
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            extra.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            extra.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
            extra.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
            row.Children.Add(extra);
        }

        return row;
    }

    /// <summary>The four columns of the entrant table, in one place so header and rows agree.
    ///
    /// <para>Same reason <c>CivTableLayout</c> and <c>RankingTableLayout</c> exist: a header
    /// and its rows built from two separate lists of widths drift the first time either is
    /// edited, and the symptom is a column heading over the wrong column.</para></summary>
    private static Grid BuildEntrantGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        // AUTO, not a fixed width, and it is the difference between a row that fits and a row
        // that paints over itself. At 190 fixed the actions had nowhere to put their surplus:
        // the strip measured 396 px in Spanish, was clamped to 190 so the Grid believed it
        // fitted, and then - being right-aligned - was arranged at its real width BACKWARDS
        // from the column's right edge, across the status and over the name. Auto gives the
        // strip what it asks for and takes it from the star column, which is the only one that
        // can give: the name is the one thing in this row with TextTrimming.
        //
        // SHARED, because Auto alone is measured per Grid and there is one Grid PER ROW. So
        // the row with a "Withdraw" button surrendered star width that the row beside it kept,
        // and the left edge of the status column - which is 46 + whatever the star came to -
        // landed at a different x on every row. That is the asymmetry: "In" under a button and
        // "No seed" without one were never going to line up, and the STATUS heading, whose row
        // has no actions at all, sat furthest right of the lot. One shared group per card
        // resolves every row and the header to the same actions width, so the three columns
        // line up down the whole table.
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
            SharedSizeGroup = EntrantActionsSizeGroup,
        });
        return grid;
    }

    private static UIElement BuildEntrantHeader()
    {
        var grid = BuildEntrantGrid();

        void Cell(int column, string text, TextAlignment align)
        {
            var cell = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = align,
                Margin = new Thickness(column == 0 ? 14 : 0, 10, 0, 10),
            };
            cell.SetResourceReference(TextBlock.FontSizeProperty, "MpSectionLabelSize");
            cell.SetResourceReference(TextBlock.ForegroundProperty, "MpTableHeader");
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }

        Cell(0, "#", TextAlignment.Left);
        Cell(1, Strings.Get("MpTournamentColEntrant"), TextAlignment.Left);
        Cell(2, Strings.Get("MpTournamentColStatus"), TextAlignment.Left);

        var host = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        host.SetResourceReference(Border.BorderBrushProperty, "MpRimHair");
        return host;
    }

    private static UIElement BuildTableEmpty(string text)
    {
        var cell = new TextBlock
        {
            Text = text,
            Margin = new Thickness(14, 12, 14, 13),
            FontStyle = FontStyles.Italic,
        };
        cell.SetResourceReference(TextBlock.FontSizeProperty, "MpLabelSize");
        cell.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
        return cell;
    }

    private UIElement BuildEntrantRow(
        TournamentDetail t, TournamentEntrant e, string? me, bool canDecide, bool isLast)
    {
        bool isMine = e.MemberIds != null && !string.IsNullOrEmpty(me)
                      && e.MemberIds.Any(u => string.Equals(u, me, StringComparison.Ordinal));
        bool out_ = MatchCards.EntrantIsOut(e);
        bool noSeed = e.Status == "confirmed" && !e.Seed.HasValue;

        var grid = BuildEntrantGrid();

        // The seed, and a dash where there is none - never a blank cell, which reads as a
        // rendering fault rather than as an absence.
        var seed = new TextBlock
        {
            Text = e.Seed?.ToString() ?? "\u2014",
            Margin = new Thickness(14, 11, 0, 11),
            FontWeight = FontWeights.SemiBold,
        };
        seed.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        seed.SetResourceReference(TextBlock.FontSizeProperty, "MpFigureSize");
        seed.SetResourceReference(TextBlock.ForegroundProperty,
            e.Seed.HasValue ? "MpActionText" : "MpTextGhost");
        Grid.SetColumn(seed, 0);
        grid.Children.Add(seed);

        var nameCell = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 12, 10),
        };
        var name = new TextBlock
        {
            Text = e.DisplayName ?? "",
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        };
        name.SetResourceReference(TextBlock.FontSizeProperty, "MpBodySize");
        // Somebody who is out is dimmed by COLOUR, never by Opacity - the repo's rule.
        name.SetResourceReference(TextBlock.ForegroundProperty,
            out_ ? "MpTextFade" : "MpTextPrimary");
        nameCell.Children.Add(name);

        if (isMine)
        {
            nameCell.Children.Add(BuildTag(
                Strings.Get("MpTournamentYouTag"), "MpActionText", "MpActionSoftBg"));
        }
        Grid.SetColumn(nameCell, 1);
        grid.Children.Add(nameCell);

        // The status, in its own column, with a dot of its own colour. A confirmed entrant
        // with no seed says SO here, in amber: that row is the reason the bracket will not
        // be drawn, and it is the only place anybody could find that out.
        var statusCell = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 10),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var (dotBrush, inkBrush) = noSeed
            ? ("MpCaution", "MpCautionText")
            : e.Status switch
            {
                "confirmed" => ("MpOk", "MpOkText"),
                "pending" => ("MpCaution", "MpCautionText"),
                "waitlist" => ("MpNoResult", "MpTextMuted"),
                "disqualified" => ("MpDestructive", "MpDestructiveText"),
                _ => ("MpNoResult", "MpTextGhost"),
            };

        var dot = new Border
        {
            Width = 5, Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        dot.SetResourceReference(Border.BackgroundProperty, dotBrush);
        statusCell.Children.Add(dot);

        var statusText = new TextBlock
        {
            Text = e.Status == "pending" && canDecide
                ? Strings.Get("MpTournamentAskedToEnter")
                : noSeed ? Strings.Get("MpTournamentNoSeed")
                : EntrantStatusLabel(e.Status),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // ONE WEIGHT for every state. "No seed" used to be SemiBold while "In" was Normal, so
        // even once the columns line up the two read as different columns - the eye takes a
        // change of weight down a column as a change of kind. The emphasis is already carried
        // by the colour, which is where the rest of this page puts it.
        statusText.SetResourceReference(TextBlock.FontSizeProperty, "MpLabelSize");
        statusText.SetResourceReference(TextBlock.ForegroundProperty, inkBrush);
        statusCell.Children.Add(statusText);

        Grid.SetColumn(statusCell, 2);
        grid.Children.Add(statusCell);

        // The actions, on the SAME row as the name. They used to hang under it as a pair of
        // loose links, which put two centimetres between "who" and "what about them".
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 7, 12, 7),
            VerticalAlignment = VerticalAlignment.Center,
        };

        void Act(string key, string style, Func<Task> action)
        {
            var b = new Button { Content = Strings.Get(key), Margin = new Thickness(6, 0, 0, 0) };
            b.SetResourceReference(FrameworkElement.StyleProperty, style);
            b.Click += (_, _) => { _ = RunTournamentActionAsync(action); };
            actions.Children.Add(b);
        }

        if (e.Status == "pending" && TournamentPermissions.CanDecideEntrant(t, me, e))
        {
            Act("MpTournamentAccept", "MpPrimaryButton",
                () => _session!.Api!.AcceptEntrantAsync(t.Id, e.Id));
            Act("MpTournamentReject", "MpGhostButton",
                () => _session!.Api!.RejectEntrantAsync(t.Id, e.Id));
        }
        else if (e.Status == "waitlist" && TournamentPermissions.IsOwner(t, me))
        {
            // Accept takes a seat if there is one and leaves them waiting if there is not,
            // so the same route promotes a waitlisted entrant. The server decides which.
            Act("MpTournamentGivePlace", "MpGhostButton",
                () => _session!.Api!.AcceptEntrantAsync(t.Id, e.Id));
        }
        else if (isMine && TournamentPermissions.CanWithdraw(t, me)
                 && string.Equals(e.CaptainUserId, me, StringComparison.Ordinal))
        {
            Act("MpTournamentWithdraw", "MpGhostButton",
                () => _session!.Api!.WithdrawFromTournamentAsync(t.Id, e.Id));
        }

        // THE ORGANISER'S TWO POWERS LIVE IN A MENU, not as two more buttons.
        //
        // They are what somebody does TO another person - throwing them out of a running
        // bracket, or handing them the run of the tournament - so they deserve a deliberate
        // extra click rather than sitting one slip away from Accept. Same criterion that put
        // "Decidir esta partida" and "Que la repitan" behind the bracket bar's menu.
        //
        // And they are the two longest captions in the row. Added as buttons, they took a row
        // that carried at most two up to four, in the language where each of them is widest:
        // the strip measured 396 px and drew backwards over the name and the status. Two
        // independent `if`s stacking on top of the Accept/Reject chain is exactly how a row
        // grows without anybody deciding that it should.
        var menu = new ContextMenu();

        // Not for somebody already out - the server refuses a second disqualification, and
        // offering it would be offering a no-op.
        if (TournamentPermissions.CanAwardOrDisqualify(t, me)
            && e.Status is "confirmed" or "pending" or "waitlist")
        {
            // NOT through Act: that helper wraps its action in RunTournamentActionAsync, and
            // the confirm below runs that itself. Wrapped twice, the preview's inert notice
            // would fire before the question and again after it.
            var dq = new MenuItem { Header = Strings.Get("MpTournamentDisqualify") };
            dq.Click += (_, _) => { _ = ConfirmDisqualifyAsync(t, e); };
            menu.Items.Add(dq);
        }

        // CaptainUserId and not MemberIds[0]: it is the only scalar user id an entrant has,
        // it is who registered, and for a solo entrant it IS the person. A team entrant has
        // no single person, so the offer is to its captain and nobody else - a whole team
        // cannot co-organise anything.
        if (TournamentPermissions.CanAppointManagers(t, me)
            && !string.IsNullOrEmpty(e.CaptainUserId)
            && !AlreadyManages(t, e.CaptainUserId))
        {
            var who = e.CaptainUserId!;
            var promote = new MenuItem { Header = Strings.Get("MpTournamentMakeManager") };
            promote.Click += (_, _) =>
            {
                _ = RunTournamentActionAsync(
                    () => _session!.Api!.AddTournamentManagerAsync(t.Id, who));
            };
            menu.Items.Add(promote);
        }

        if (menu.Items.Count > 0)
        {
            var more = new Button
            {
                Content = "\u22ef",
                MinWidth = 32,
                Margin = new Thickness(6, 0, 0, 0),
                ContextMenu = menu,
            };
            more.SetResourceReference(FrameworkElement.StyleProperty, "MpGhostButton");
            more.Click += (_, _) =>
            {
                menu.PlacementTarget = more;
                menu.IsOpen = true;
            };
            actions.Children.Add(more);
        }

        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);

        var row = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, isLast ? 0 : 1),
            Child = grid,
        };
        row.SetResourceReference(Border.BorderBrushProperty, "MpRimHair");
        if (isMine) row.SetResourceReference(Border.BackgroundProperty, "MpRowHighlight");
        return row;
    }

    private static string EntrantStatusLabel(string? status) => status switch
    {
        "confirmed" => Strings.Get("MpTournamentEntrantConfirmed"),
        "waitlist" => Strings.Get("MpTournamentEntrantWaitlist"),
        "pending" => Strings.Get("MpTournamentEntrantPending"),
        "withdrawn" => Strings.Get("MpTournamentEntrantWithdrawn"),
        "rejected" => Strings.Get("MpTournamentEntrantRejected"),
        "disqualified" => Strings.Get("MpTournamentEntrantDisqualified"),
        _ => "",
    };

    /// <summary>Width of one bracket card, and therefore of a round's column.</summary>
    private const double BracketCardWidth = 220;

    /// <summary>The channel between two columns, where the connectors are drawn. A card's
    /// stub reaches half way across it and meets the next card's stub coming back.</summary>
    private const double BracketGutter = 40;

    /// <summary>Breathing room between two cards in the same column, added to the measured
    /// card height to make one row.</summary>
    private const double BracketRowGap = 8;

    /// <summary>
    /// A floor under the measured row height, for the case where measuring cannot work.
    ///
    /// <para>Nothing in <see cref="MeasureBracketRow"/> can fail loudly: an element measured
    /// outside a visual tree whose resources did not resolve simply reports a height of zero,
    /// and a grid of zero-height rows draws a bracket that is not there. This is the number
    /// that turns that into something visibly wrong instead of invisibly absent.</para>
    /// </summary>
    private const double BracketRowFloor = 44;

    /// <summary>
    /// How tall one first-round slot is, MEASURED from the cards this bracket actually built.
    ///
    /// <para>Uniform across every column, and it has to be: rows sized to their own contents
    /// would be a different height in each round, and a round-two card would stop lining up
    /// with the pair it came from. So the row is the tallest card in the whole bracket.</para>
    ///
    /// <para><b>Measured and not chosen.</b> This was a hand-written constant twice, and both
    /// values were wrong in the same way. At 108 the team card was arranged into a cell
    /// shorter than it wanted, its bottom was clipped, and the action button silently
    /// disappeared from the one card that most needs it. At 136 nothing was clipped and a
    /// sixteen-entrant bracket stood over two thousand pixels tall, so the first round ran
    /// off three screens and the later rounds floated in the gap. No single number can be
    /// right for both a 1v1 card of two names and a team card carrying two line-ups, a
    /// warning box and a footer - and a translated name can change either one. Asking WPF is
    /// the only answer that stays correct.</para>
    ///
    /// <para><b>Divided by the SPAN, which is the whole trick.</b> A card is not bound by one
    /// row: a round-two card already covers two of them, a round-three card four. So what has
    /// to fit is the height PER ROW the card needs, and the row is the largest of those. Take
    /// the tallest card raw instead and one playable card in a late round - the tall kind,
    /// with a footer - inflates every row in the bracket by its own height, which is precisely
    /// how 136 happened.</para>
    /// </summary>
    private static double MeasureBracketRow(IEnumerable<(FrameworkElement Card, int Span)> cards)
    {
        double tallest = 0;
        foreach (var (card, span) in cards)
        {
            card.Measure(new Size(BracketCardWidth, double.PositiveInfinity));
            double perRow = (card.DesiredSize.Height + BracketRowGap) / Math.Max(1, span);
            if (perRow > tallest) tallest = perRow;
        }
        return Math.Max(BracketRowFloor, tallest);
    }

    /// <summary>
    /// The bracket, one column per round, with the lines that say which slots feed which.
    ///
    /// <para><c>internal</c> so <c>DialogXamlTests</c> can construct it: it is assembled in
    /// code and therefore checked by nothing at compile time, which is exactly the case
    /// <c>MatchResultCard</c> established the rule for.</para>
    ///
    /// <para>The geometry comes from <see cref="BracketLayout.Build"/> and is not recomputed
    /// here. What this adds is the row height (measured, see above) and the connectors, which
    /// fall straight out of the same arithmetic: a card spanning <c>n</c> first-round slots is
    /// fed by two cards whose centres are <c>(n/2) * rowH</c> apart, so that is exactly how
    /// tall its vertical line is. Writing that number by hand would be writing the layout
    /// down twice.</para>
    /// </summary>
    /// <summary>
    /// The bracket cell the viewer has clicked, or null. Cleared when the tournament changes.
    ///
    /// <para>Selection exists because the actions had to leave the cells. Before this, every
    /// card carried its own buttons and a card is 220 px wide inside a grid of uniform rows \u2014
    /// so the tallest card in the whole bracket set the height of every row in it.</para>
    /// </summary>
    private string? _selectedMatchId;

    /// <summary>
    /// Select a bracket cell from a test, the way a click would.
    ///
    /// <para>The cell is a <c>Border</c> with a <c>MouseLeftButtonUp</c> handler and not a
    /// Button, because sixty Buttons side by side would each bring a focus rectangle and a
    /// hover fill into a grid where the CARD is the thing being drawn. The cost is that no
    /// synthetic click reaches it reliably from outside the process, so the selection has one
    /// door a test can use.</para>
    /// </summary>
    internal void SelectBracketMatchForPreview(string tournamentId, string? matchId)
    {
        // The tab goes into demo mode with it, because the only tournaments rendered without
        // a session are the fabricated ones - and the supervising half of the bracket is
        // gated on exactly that. Selecting a fixture's cell while claiming to be live would
        // render a state the launcher never shows anybody.
        _demoTournaments = true;
        _selectedTournamentId = tournamentId;
        _selectedMatchId = matchId;
    }

    internal UIElement BuildBracketPanel(TournamentDetail t, string? me)
    {
        var grid = BracketLayout.Build(t.Matches);
        var columns = new StackPanel { Orientation = Orientation.Horizontal };

        // Every card first, so the row height can be measured from all of them before any of
        // them is placed. Two passes over the same objects, not two builds.
        var built = new List<(BracketLayout.BracketColumn Column, List<Border> Cards)>();
        foreach (var col in grid.Columns)
        {
            built.Add((col, col.Cells.Select(c => BuildBracketCard(t, c.Match, me)).ToList()));
        }

        // Uniform, and now it can be: with no card carrying a button, the tallest card in
        // the bracket is a card with two names on it. What used to happen is written up in
        // MeasureBracketRow's own doc - a playable team card at 136 px made a sixteen-entrant
        // bracket over two thousand pixels tall.
        double rowH = MeasureBracketRow(built.SelectMany(
            b => b.Column.Cells.Select((c, i) => ((FrameworkElement)b.Cards[i], c.RowSpan))));
        int firstRound = grid.Columns.Count > 0 ? grid.Columns[0].Round : 0;
        int lastRound = grid.Columns.Count > 0 ? grid.Columns[^1].Round : 0;

        foreach (var (col, cards) in built)
        {
            var column = new StackPanel
            {
                Width = BracketCardWidth,
                Margin = new Thickness(0, 0, BracketGutter, 0),
            };

            var head = new TextBlock
            {
                Text = Strings.Format(BracketLayout.RoundLabelKey(col.Round, t.RoundsTotal), col.Round),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 9),
            };
            head.SetResourceReference(TextBlock.FontSizeProperty, "MpSectionLabelSize");
            head.SetResourceReference(TextBlock.ForegroundProperty, "MpTextMuted");
            column.Children.Add(head);

            // A Grid of uniform rows rather than a stack, because THAT is what makes it read
            // as a bracket: BracketLayout gives each card a RowStart and a RowSpan measured in
            // first-round slots, so a round-two card spans the two below it and sits centred
            // between them. Stacking the cards instead lines every round up at the top and the
            // tree stops being legible - which is exactly what the demo mode showed the first
            // time it was looked at, with the geometry computed and then thrown away.
            var body = new Grid();
            for (int r = 0; r < grid.RowCount; r++)
                body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowH) });

            for (int i = 0; i < col.Cells.Count; i++)
            {
                var cell = col.Cells[i];
                var wrapper = BuildBracketCell(
                    cards[i], cell.RowSpan, rowH,
                    drawLeft: col.Round > firstRound,
                    drawRight: col.Round < lastRound);

                Grid.SetRow(wrapper, cell.RowStart);
                Grid.SetRowSpan(wrapper, cell.RowSpan);
                body.Children.Add(wrapper);
            }

            column.Children.Add(body);
            columns.Children.Add(column);
        }

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = columns,
        };

        var bar = BuildBracketActionBar(t, me);
        if (bar == null) return scroller;

        var stacked = new StackPanel();
        stacked.Children.Add(bar);
        stacked.Children.Add(scroller);
        return stacked;
    }

    /// <summary>
    /// What the selected cross-tie offers, in ONE fixed bar above the bracket.
    ///
    /// <para><b>Above the bracket and not inside a cell, and that is a layout fact rather
    /// than a preference.</b> <see cref="MeasureBracketRow"/> makes every row of every round
    /// as tall as the tallest card in the bracket divided by its span, so a button on one
    /// card is vertical space on all sixty of them. A cell also had to serve every viewer at
    /// once \u2014 the entrant's "play mine", the organiser's "decide" and "watch" \u2014 and
    /// stacked them. One selection means one set of actions and nothing to stack.</para>
    ///
    /// <para>Null when nothing is selected: an empty bar reserving its own height above the
    /// bracket is the same mistake one storey up.</para>
    /// </summary>
    private UIElement? BuildBracketActionBar(TournamentDetail t, string? me)
    {
        var m = t.Matches?.FirstOrDefault(
            x => string.Equals(x.Id, _selectedMatchId, StringComparison.Ordinal));
        if (m == null) return null;

        var state = MatchCards.For(
            m, me, t.Entrants,
            _demoTournaments && TournamentPermissions.IsOwnerOrManager(t, me));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // WHICH tie, said the way the bracket says it, so the bar reads as belonging to the
        // card that was clicked rather than as a second opinion about it.
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = Strings.Format("MpTournamentVersus",
                                  EntrantName(t, m.Entrant1Id), EntrantName(t, m.Entrant2Id)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = FontWeights.SemiBold,
        };
        title.SetResourceReference(TextBlock.FontSizeProperty, "MpBodySize");
        title.SetResourceReference(TextBlock.ForegroundProperty, "MpTextPrimary");
        text.Children.Add(title);

        var round = Strings.Format(
            BracketLayout.RoundLabelKey(m.Round, t.RoundsTotal), m.Round).ToLowerInvariant();
        var sub = new TextBlock
        {
            Text = m.Lobby != null
                ? Strings.Format("MpTournamentBarPlaying", round)
                : round,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        sub.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
        sub.SetResourceReference(TextBlock.ForegroundProperty,
                                 m.Lobby != null ? "MpOkText" : "MpTextMuted");
        text.Children.Add(sub);
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var action = BuildBarAction(t, m, state);
        if (action != null)
        {
            Grid.SetColumn(action, 1);
            grid.Children.Add(action);
        }

        var menu = BuildBarOverflow(t, m);
        if (menu != null)
        {
            Grid.SetColumn(menu, 2);
            grid.Children.Add(menu);
        }

        var bar = new Border
        {
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(13, 10, 11, 10),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        bar.SetResourceReference(Border.CornerRadiusProperty, "RadiusControl");
        bar.SetResourceReference(Border.BackgroundProperty, "MpRowHighlight");
        bar.SetResourceReference(Border.BorderBrushProperty, "MpActionRim");
        return bar;
    }

    /// <summary>The one thing this viewer can do about the selected tie, or nothing.</summary>
    private UIElement? BuildBarAction(TournamentDetail t, TournamentMatch m, MatchCardState state)
    {
        string? key = state switch
        {
            MatchCardState.Playable => "MpTournamentPlayMyMatch",
            MatchCardState.JoinRoom => "MpTournamentJoinRoom",
            MatchCardState.ReturnToRoom => "MpTournamentReturnToRoom",
            MatchCardState.SuperviseRoom => "MpTournamentWatchRoom",
            _ => null,
        };
        if (key == null) return null;

        var b = new Button
        {
            Content = Strings.Get(key),
            MinWidth = 118,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Returning to a room you already opened, or looking into somebody else's, are both
        // weaker offers than opening one - drawn that way, as the cards used to draw them.
        b.SetResourceReference(FrameworkElement.StyleProperty,
            state is MatchCardState.ReturnToRoom or MatchCardState.SuperviseRoom
                ? "MpGhostButton"
                : "MpPrimaryButton");

        if (state == MatchCardState.SuperviseRoom) b.Click += (_, _) => OpenMatchWatch(t, m);
        else b.Click += (_, _) => { _ = OpenTournamentMatchAsync(t, m); };
        return b;
    }

    /// <summary>
    /// The organiser's powers, behind a \u22ef.
    ///
    /// <para>Not a second button of the same weight as the action beside it: deciding a match
    /// by hand and ordering it replayed are things one person can do to other people's game,
    /// and they should take a deliberate extra click. It is also why the menu is absent
    /// entirely rather than disabled for everyone else.</para>
    /// </summary>
    private UIElement? BuildBarOverflow(TournamentDetail t, TournamentMatch m)
    {
        var me = _demoTournaments ? TournamentDemoData.MeUserId : _session?.CurrentUser?.Id;
        bool canAward = TournamentPermissions.CanAwardMatch(t, me, m);
        bool canReplay = TournamentPermissions.CanReplayMatch(t, me, m);
        if (!canAward && !canReplay) return null;

        var menu = new ContextMenu();

        if (canAward)
        {
            // The same winners flyout the card carried, one level deeper. The owner still has
            // to say WHICH side won; that has not changed and should not.
            foreach (var id in new[] { m.Entrant1Id!, m.Entrant2Id! })
            {
                var name = EntrantName(t, id);
                var winner = id;
                var item = new MenuItem { Header = Strings.Format("MpTournamentAwardTo", name) };
                item.Click += (_, _) => { _ = ConfirmAwardAsync(t, m, winner, name); };
                menu.Items.Add(item);
            }
        }

        if (canReplay)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            var again = new MenuItem { Header = Strings.Get("MpTournamentReplay") };
            again.Click += (_, _) => { _ = ConfirmReplayAsync(t, m); };
            menu.Items.Add(again);
        }

        var button = new Button
        {
            Content = "\u22ef",
            MinWidth = 34,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ContextMenu = menu,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "MpGhostButton");
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        };
        return button;
    }

    /// <summary>
    /// One cell of the bracket: the card, plus the lines that join it to its neighbours.
    ///
    /// <para>The lines are ordinary <c>Border</c>s pushed into the channel with a NEGATIVE
    /// margin. A <c>Grid</c> does not clip its children, so they draw in the gutter the
    /// column's own margin opened up; there is nothing to lay out and nothing to keep in
    /// sync, which is why this is neither a <c>Canvas</c> nor a drawing.</para>
    ///
    /// <para>The vertical is the only piece carrying arithmetic, and it is the piece that
    /// makes a bracket readable: half the card's span, times the row height, is the distance
    /// between the centres of the two cards feeding it. Derived, never written down - a fixed
    /// height here would be right for one round and wrong for every other.</para>
    /// </summary>
    private static Grid BuildBracketCell(
        Border card, int rowSpan, double rowH, bool drawLeft, bool drawRight)
    {
        var wrapper = new Grid();
        double reach = BracketGutter / 2;

        Border Line(double width, double height, HorizontalAlignment side, Thickness margin)
        {
            var line = new Border
            {
                Width = width,
                Height = height,
                HorizontalAlignment = side,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = margin,
            };
            line.SetResourceReference(Border.BackgroundProperty, "MpBracketConnector");
            return line;
        }

        if (drawRight)
        {
            wrapper.Children.Add(Line(
                reach, 1, HorizontalAlignment.Right, new Thickness(0, 0, -reach, 0)));
        }

        if (drawLeft)
        {
            wrapper.Children.Add(Line(
                reach, 1, HorizontalAlignment.Left, new Thickness(-reach, 0, 0, 0)));
            wrapper.Children.Add(Line(
                1, rowSpan / 2.0 * rowH, HorizontalAlignment.Left, new Thickness(-reach, 0, 0, 0)));
        }

        card.VerticalAlignment = VerticalAlignment.Center;
        wrapper.Children.Add(card);
        return wrapper;
    }

    /// <summary>
    /// One bracket card: its two sides, and whatever it lets the viewer do.
    ///
    /// <para>Every one of <see cref="MatchCardState"/>'s eight values is drawn differently.
    /// It used to distinguish three, which meant a match being played right now, a match
    /// nobody can act on and a match waiting for an opponent all looked identical - and the
    /// difference between entering a room somebody else opened and walking back into your
    /// own was not drawn at all.</para>
    ///
    /// <para><b>There is no score on the wire, and none is invented where nothing was
    /// played.</b> A match carries a winner and an outcome and nothing else. A match somebody
    /// PLAYED shows 1 and 0, one on each side, which is the handoff's notation for who won and
    /// what this launcher can honestly say. A walkover or a disqualification keeps its tag and
    /// leaves the losing side blank: those are two of the ways a slot is settled WITHOUT a
    /// game, and a figure there would be describing a match that never happened. The decision
    /// is <c>BracketLayout.MarkerFor</c> / <c>LoserMarkerFor</c>, pure and pinned.</para>
    ///
    /// <para><b>And no way to undo one.</b> Reversing a played result is the maintainer's
    /// CLI, not the owner's card: see the tournaments block of
    /// <c>.claude/rules/multiplayer.md</c>.</para>
    /// </summary>
    private Border BuildBracketCard(TournamentDetail t, TournamentMatch m, string? me)
    {
        // Who may look at a match being played: the same test that already decides who may
        // settle one by hand. MyPlayableRound asks WITHOUT it on purpose - that one means
        // "which round is my next match in", and running the tournament is not playing in it.
        //
        // AND ONLY UNDER THE FABRICATED TOURNAMENTS. The permission is true of a real owner
        // too, so without this a live organiser whose entrants had opened a room would have
        // been offered a button that cannot work: the server refuses it three times over, and
        // the only thing behind the button today is a preview. Offering it for real would mean
        // telling somebody their own tournament was sample data. The day the server grows the
        // door, this clause is what comes off.
        var state = MatchCards.For(
            m, me, t.Entrants,
            _demoTournaments && TournamentPermissions.IsOwnerOrManager(t, me));
        bool team = !string.Equals(t.Format, "1v1", StringComparison.Ordinal);
        bool decided = string.Equals(m.Status, "done", StringComparison.Ordinal)
                       || string.Equals(m.Status, "bye", StringComparison.Ordinal);
        bool bye = state == MatchCardState.Bye;

        var stack = new StackPanel();

        // A bye is ONE row. Drawing the empty half would say a match happened between
        // somebody and nobody, when what happened is that nobody played.
        stack.Children.Add(BuildBracketSide(t, m, m.Entrant1Id, 1, me, team, decided, bye));
        if (!bye)
        {
            stack.Children.Add(BracketHairline());
            stack.Children.Add(BuildBracketSide(t, m, m.Entrant2Id, 2, me, team, decided, false));
        }

        // In a TEAM tournament the sides are picked inside AoE3, not here, and getting them
        // wrong means the game does not rate AND the bracket does not move. That makes this
        // the rule the card exists to carry, so it is a box and not a grey sentence.
        // NOTHING ELSE GOES IN HERE. Not a button, not a status line, not the sides
        // warning - they moved to the action bar above the bracket. A cell is a POSITION in
        // a structure, and it has one more constraint than it looks: MeasureBracketRow takes
        // the tallest card in the whole bracket and makes that the height of every row of
        // every round, so anything added to one card inflates all of them. The team warning
        // and the two action buttons that used to live here did precisely that.
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            Child = stack,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        // Selecting is how a cell offers anything now. MouseLeftButtonUp rather than a Button
        // wrapper: a Button would bring a focus rectangle and a hover fill into a grid where
        // sixty of them sit side by side.
        card.MouseLeftButtonUp += (_, _) =>
        {
            _selectedMatchId = string.Equals(_selectedMatchId, m.Id, StringComparison.Ordinal)
                ? null
                : m.Id;
            RenderTournamentDetail();
        };
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusControl");

        // Mine, being played, settled, somebody else's: four different surfaces. The dimmed
        // ones are dimmed by COLOUR - a house rule, and the reason is that Opacity fades the
        // card's own background with its text and it stops reading as a card at all.
        bool mine = MatchCards.IsMine(m, me, t.Entrants);
        if (mine && !decided)
        {
            card.SetResourceReference(Border.BackgroundProperty, "MpRowHighlight");
            card.SetResourceReference(Border.BorderBrushProperty, "MpOwnRowRim");
        }
        else if (state is MatchCardState.InProgress or MatchCardState.SuperviseRoom)
        {
            // One surface for both: the card is green-rimmed because the match is being
            // played, which is true whoever is looking at it. What differs is the footer.
            card.SetResourceReference(Border.BackgroundProperty, "MpPanel");
            card.SetResourceReference(Border.BorderBrushProperty, "MpChipOkRim");
        }
        else if (bye || string.IsNullOrEmpty(m.Entrant1Id) || string.IsNullOrEmpty(m.Entrant2Id))
        {
            // Nothing has happened in this slot yet, or nothing ever will. It recedes.
            card.SetResourceReference(Border.BackgroundProperty, "MpPanelDim");
            card.SetResourceReference(Border.BorderBrushProperty, "MpRimSoft");
        }
        else
        {
            card.SetResourceReference(Border.BackgroundProperty, "MpPanel");
            card.SetResourceReference(Border.BorderBrushProperty, "MpRimMedium");
        }

        return card;
    }

    /// <summary>Whether this card offers the viewer a room to open, join or return to.</summary>
    private static bool Actionable(MatchCardState state)
        => state is MatchCardState.Playable or MatchCardState.JoinRoom or MatchCardState.ReturnToRoom;

    private static Border BracketHairline()
    {
        var line = new Border { Height = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "MpRimHair");
        return line;
    }

    /// <summary>
    /// One side of a card: seed, name, and what became of it.
    ///
    /// <para>Three columns and a marker, with the seed in monospace at a fixed width - that
    /// fixed width is what keeps the seeds of every card in a column lined up, which is the
    /// only reason a seed is worth showing on the card at all.</para>
    ///
    /// <para>An undecided side does NOT read "to be decided". It reads which match it is
    /// waiting on, because that is the one useful thing a bracket can say about an empty
    /// slot, and a column of "to be decided" is what makes the top half of a bracket
    /// unreadable.</para>
    /// </summary>
    private Grid BuildBracketSide(
        TournamentDetail t, TournamentMatch m, string? entrantId, int slot,
        string? me, bool team, bool decided, bool bye)
    {
        var e = t.Entrants?.FirstOrDefault(
            x => string.Equals(x.Id, entrantId, StringComparison.Ordinal));
        bool known = e != null;
        bool won = decided && !string.IsNullOrEmpty(entrantId)
                   && string.Equals(entrantId, m.WinnerEntrantId, StringComparison.Ordinal);
        bool lost = decided && known && !won;
        bool isMine = known && e!.MemberIds != null && !string.IsNullOrEmpty(me)
                      && e.MemberIds.Any(u => string.Equals(u, me, StringComparison.Ordinal));

        var row = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        // Wide enough for TWO digits plus its own padding. It was 20 for one build and a
        // sixteen-entrant bracket - the ordinary case - drew seed 12 as "1:", because the
        // label's margins were taken out of the column rather than added to it.
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The 3px bar marking WHICH of the two sides is me. On the row, not the card: in a
        // bracket both halves look alike, and "this match is yours" is a weaker statement
        // than "you are the one on top".
        if (isMine)
        {
            var bar = new Border();
            bar.SetResourceReference(Border.BackgroundProperty, "MpAction");
            Grid.SetColumn(bar, 0);
            row.Children.Add(bar);
        }

        // LEFT-aligned in its lane, and the lane has air on both sides. Measured off handoff
        // 8a: the seed's first digit sits 11px from the card edge and the NAME starts at 39,
        // whatever the seed is - one digit or two. It was right-aligned ending at 28 with the
        // name at 33, so a digit and a letter were 5px apart and read as one word. That is the
        // reported "the numbers are stuck to the names"; left-aligning is also what makes a
        // two-digit seed close the gap to 16 exactly as the reference does, instead of eating
        // into the name.
        var seed = new TextBlock
        {
            Text = e?.Seed?.ToString() ?? "\u2014",
            Margin = new Thickness(8, 6, 3, 6),
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            FontWeight = FontWeights.SemiBold,
        };
        seed.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        seed.SetResourceReference(TextBlock.FontSizeProperty, "MpFigureSize");
        // ONE colour for every seed, winner included. It used to paint the winner's seed
        // MpActionText blue - the reference paints it #5F7592 like all the others, and the
        // winner is already named three times over (heading colour, SemiBold, and the figure
        // on the right). The old ternary had two identical branches, which is what a
        // three-way version looks like after somebody removed the middle one.
        seed.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
        Grid.SetColumn(seed, 1);
        row.Children.Add(seed);

        // The 8 is the other half of the seed lane: seed at 11, name at 39, per the reference.
        var body = new StackPanel { Margin = new Thickness(8, 5, 0, 5) };

        var nameLine = new Grid();
        nameLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // An unknown side reads "por definir" and carries the long answer - which tie it is
        // waiting on - in its tooltip. Drawn in full it was ellipsised to something that said
        // neither: at 220 px a card gives this lane about 180, and "Ganador de X \u00b7 Y" is
        // longer than that whenever either name is more than a nickname.
        var feeder = known ? null : FeederLabel(t, m, slot);
        var name = new TextBlock
        {
            Text = known
                ? (e!.DisplayName ?? "")
                : Strings.Get("MpTournamentSlotUndecided"),
            ToolTip = feeder is { Length: > 0 } ? feeder : null,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = won ? FontWeights.SemiBold : FontWeights.Normal,
        };
        name.SetResourceReference(TextBlock.FontSizeProperty, "MpMetaSize");
        name.SetResourceReference(TextBlock.ForegroundProperty,
            !known ? "MpTextGhost"
                : won ? "MpTextHeading"
                : lost ? "MpTextFade"
                : "MpTextPrimary");
        Grid.SetColumn(name, 0);
        nameLine.Children.Add(name);

        if (isMine)
        {
            var you = BuildTag(Strings.Get(team ? "MpTournamentYourTeamTag" : "MpTournamentYouTag"),
                               "MpActionText", "MpActionSoftBg");
            Grid.SetColumn(you, 1);
            nameLine.Children.Add(you);
        }

        body.Children.Add(nameLine);

        // A slot in a team tournament holds a whole team, and its FROZEN line-up is the only
        // thing that answers "am I playing in this". Frozen and not live: a saved team can
        // change the day after it entered, and the people who registered are the people who
        // play - the same reason MatchCards.IsMine reads the same list.
        if (team && known)
        {
            var roster = BuildRosterPills(e!, me);
            if (roster != null) body.Children.Add(roster);
        }

        Grid.SetColumn(body, 2);
        row.Children.Add(body);

        // What became of this side. The decision is BracketLayout.MarkerFor / LoserMarkerFor;
        // this only draws it.
        var mark = won || bye
            ? BracketLayout.MarkerFor(bye, decided, won, m.Outcome)
            : BracketLayout.LoserMarkerFor(decided, known, m.Outcome);

        UIElement? marker = mark switch
        {
            SideMarker.ByeTag =>
                BuildTag(Strings.Get("MpTournamentOutcomeBye"), "MpTextMuted", "MpNeutralBadgeBg"),
            SideMarker.WalkoverTag =>
                BuildTag(Strings.Get("MpTournamentOutcomeWalkover"), "MpCautionText", "MpCautionBg"),
            SideMarker.DqTag =>
                BuildTag(Strings.Get("MpTournamentOutcomeDq"), "MpCautionText", "MpCautionBg"),
            SideMarker.One => BuildResultFigure("1", "MpOkText"),
            SideMarker.Zero => BuildResultFigure("0", "MpTextGhost"),
            _ => null,
        };

        if (marker != null)
        {
            Grid.SetColumn((FrameworkElement)marker, 3);
            row.Children.Add(marker);
        }
        else if (slot == 1 && !decided && !bye && m.Lobby != null)
        {
            // BEING PLAYED, as a dot in the lane the result will use later - not as a line of
            // its own. A third row here would add height, and height here is height in every
            // row of the bracket. The lane is free precisely because nothing has been decided.
            var live = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Strings.Get("MpTournamentInProgress"),
            };
            live.SetResourceReference(Border.BackgroundProperty, "MpOk");
            Grid.SetColumn(live, 3);
            row.Children.Add(live);
        }

        return row;
    }

    /// <summary>
    /// Which match an undecided slot is waiting on, named.
    ///
    /// <para>Derived from the geometry rather than from <c>next_match_id</c>, and on purpose:
    /// a slot at <c>(round r, position p)</c> is fed by <c>(r-1, 2p)</c> and <c>(r-1, 2p+1)</c>,
    /// which is the SAME arithmetic <see cref="BracketLayout.Build"/> uses to place every card
    /// on the screen. If the server's links disagreed with it the whole bracket would already
    /// be drawn wrong, so reading them here would not catch anything - it would just be a
    /// second thing to keep in step.</para>
    /// </summary>
    private static string FeederLabel(TournamentDetail t, TournamentMatch m, int slot)
    {
        var matches = t.Matches;
        if (matches == null) return Strings.Get("MpTournamentTbd");

        int wantPosition = m.Position * 2 + (slot == 1 ? 0 : 1);
        var feeder = matches.FirstOrDefault(
            x => x.Round == m.Round - 1 && x.Position == wantPosition);
        if (feeder == null) return Strings.Get("MpTournamentTbd");

        // Both sides, or neither. "Winner of Gorgo and to be decided" reads as a sentence
        // with a hole in it, and a slot two rounds out genuinely has nothing to say yet.
        if (string.IsNullOrEmpty(feeder.Entrant1Id) || string.IsNullOrEmpty(feeder.Entrant2Id))
        {
            return Strings.Get("MpTournamentTbd");
        }

        return Strings.Format("MpTournamentWinnerOf",
            EntrantName(t, feeder.Entrant1Id), EntrantName(t, feeder.Entrant2Id));
    }

    /// <summary>
    /// The people the owner let help run this, as pills, each with a way to take it back.
    ///
    /// <para>Drawn only when there ARE any: an empty "co-organisers" heading would advertise
    /// a feature as a gap. Null from a server that predates them is the same as none.</para>
    ///
    /// <para>The remove cross is the OWNER's only, and it is the owner's even when a manager
    /// is looking - a co-organiser seeing the list without the crosses is being told who
    /// else is here, which is worth knowing, and not being offered a power they lack.</para>
    /// </summary>
    private UIElement? BuildManagersStrip(TournamentDetail t, string? me)
    {
        var named = t.Managers;
        var ids = t.ManagerUserIds;
        if ((named == null || named.Count == 0) && (ids == null || ids.Count == 0)) return null;

        bool canRemove = TournamentPermissions.CanAppointManagers(t, me);

        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        var label = new TextBlock { Text = Strings.Get("MpTournamentManagers") };
        label.SetResourceReference(TextBlock.FontSizeProperty, "MpSectionLabelSize");
        label.SetResourceReference(TextBlock.ForegroundProperty, "MpTextMuted");
        stack.Children.Add(label);

        // A server that sends ids only cannot be drawn as people. A count is honest; a row
        // of identifiers would not be - the same call BuildRosterPills makes.
        if (named == null || named.Count == 0)
        {
            var count = new TextBlock
            {
                Text = Strings.Format("MpTournamentManagerCount", ids!.Count),
                Margin = new Thickness(0, 4, 0, 0),
            };
            count.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
            count.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
            stack.Children.Add(count);
            return stack;
        }

        var wrap = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        foreach (var m in named)
        {
            bool self = !string.IsNullOrEmpty(me)
                        && string.Equals(m.UserId, me, StringComparison.Ordinal);

            var pill = new Border { Padding = new Thickness(8, 3, canRemove ? 4 : 8, 3), Margin = new Thickness(0, 0, 5, 4) };
            pill.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            pill.SetResourceReference(Border.BackgroundProperty,
                self ? "MpActionSoftBg" : "MpNeutralBadgeBg");

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            var name = new TextBlock
            {
                Text = m.DisplayName ?? "",
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
            name.SetResourceReference(TextBlock.ForegroundProperty,
                self ? "MpActionText" : "MpTextMuted");
            row.Children.Add(name);

            if (canRemove)
            {
                var x = new Button
                {
                    Content = "\u00d7",
                    Margin = new Thickness(6, 0, 0, 0),
                    Padding = new Thickness(4, 0, 4, 0),
                    MinWidth = 0,
                    ToolTip = TooltipHelper.Wrap(Strings.Get("MpTournamentRemoveManager")),
                };
                x.SetResourceReference(FrameworkElement.StyleProperty, "MpLinkButton");
                var who = m.UserId;
                x.Click += (_, _) =>
                {
                    _ = RunTournamentActionAsync(
                        () => _session!.Api!.RemoveTournamentManagerAsync(t.Id, who));
                };
                row.Children.Add(x);
            }

            pill.Child = row;
            wrap.Children.Add(pill);
        }

        stack.Children.Add(wrap);
        return stack;
    }

    /// <summary>
    /// Whether this person already helps run this tournament.
    ///
    /// <para>Reads the ID list rather than the named one: a server that sends only ids still
    /// answers this correctly, and offering "make co-organiser" to somebody who already is
    /// one would produce a button whose only outcome is a no-op.</para>
    /// </summary>
    private static bool AlreadyManages(TournamentDetail t, string? userId)
        => t.ManagerUserIds != null
           && !string.IsNullOrEmpty(userId)
           && t.ManagerUserIds.Any(u => string.Equals(u, userId, StringComparison.Ordinal));

    /// <summary>The frozen line-up as pills, the captain marked.</summary>
    private static UIElement? BuildRosterPills(TournamentEntrant e, string? me)
    {
        var members = e.Members;
        if (members == null || members.Count == 0)
        {
            // A backend older than the named rosters sends ids only. A count is honest;
            // a row of pills holding identifiers would not be.
            int n = e.MemberIds?.Count ?? 0;
            if (n == 0) return null;
            var fallback = new TextBlock
            {
                Text = Strings.Format("MpTournamentPlayerCount", n),
                Margin = new Thickness(0, 3, 0, 0),
            };
            fallback.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
            fallback.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
            return fallback;
        }

        var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var member in members)
        {
            bool captain = string.Equals(member.UserId, e.CaptainUserId, StringComparison.Ordinal);
            bool self = !string.IsNullOrEmpty(me)
                        && string.Equals(member.UserId, me, StringComparison.Ordinal);

            var pill = new Border
            {
                Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(0, 0, 4, 3),
            };
            pill.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            pill.SetResourceReference(Border.BackgroundProperty,
                self ? "MpActionSoftBg" : "MpNeutralBadgeBg");

            var label = new TextBlock
            {
                // The captain's mark, not a word: it sits inside a pill beside a name and
                // the pill has room for one glyph, not for "captain".
                Text = captain ? member.DisplayName + " \u00a9" : (member.DisplayName ?? ""),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 92,
            };
            label.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
            label.SetResourceReference(TextBlock.ForegroundProperty,
                self ? "MpActionText" : "MpTextMuted");
            pill.Child = label;
            wrap.Children.Add(pill);
        }
        return wrap;
    }

    /// <summary>A small uppercase chip: PASA, W.O., TU.</summary>
    private static Border BuildTag(string text, string foreground, string background)
    {
        var tag = new Border
        {
            Padding = new Thickness(5, 2, 5, 2),
            Margin = new Thickness(6, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        tag.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        tag.SetResourceReference(Border.BackgroundProperty, background);

        var label = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
        label.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        tag.Child = label;
        return tag;
    }

    /// <summary>
    /// A side's result: 1 for the winner, 0 for the loser, in the same monospace as the seed
    /// so the two figures on a card line up with the two on every other card.
    ///
    /// <para>It replaced a green tick that only the winner got, which meant the losing row had
    /// nothing at all on its right and the card read as half filled in. The colours are the
    /// reference's, measured off it: the 1 in <c>MpOkText</c> (the tick's own green, kept) and
    /// the 0 in <c>MpTextGhost</c>.</para>
    /// </summary>
    private static TextBlock BuildResultFigure(string text, string foreground)
    {
        var figure = new TextBlock
        {
            Text = text,
            Margin = new Thickness(6, 5, 9, 0),
            VerticalAlignment = VerticalAlignment.Top,
            FontWeight = FontWeights.Bold,
        };
        figure.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        figure.SetResourceReference(TextBlock.FontSizeProperty, "MpFigureSize");
        figure.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        return figure;
    }

    /// <summary>The amber sides box, naming the viewer's own team.</summary>
    private UIElement BuildSidesWarning(TournamentDetail t, TournamentMatch m, string? me)
    {
        var mine = TournamentPermissions.MyEntrant(t, me);
        string who = mine?.DisplayName
                     ?? EntrantName(t, m.Entrant1Id);

        var box = new Border
        {
            Padding = new Thickness(8, 7, 8, 7),
            Margin = new Thickness(9, 3, 9, 0),
            BorderThickness = new Thickness(1),
        };
        box.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        box.SetResourceReference(Border.BackgroundProperty, "MpCautionBg");
        box.SetResourceReference(Border.BorderBrushProperty, "MpCautionRim");

        var text = new TextBlock
        {
            Text = Strings.Format("MpTournamentSidesWarningTeam", who),
            TextWrapping = TextWrapping.Wrap,
        };
        text.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
        text.SetResourceReference(TextBlock.ForegroundProperty, "MpCautionText");
        box.Child = text;
        return box;
    }

    /// <summary>
    /// The card's foot: a button when there is something to do, a sentence when there is
    /// something to know, and nothing at all otherwise.
    ///
    /// <para><c>InProgress</c> deliberately carries no button. A room on somebody else's
    /// match is worth showing as being played, but the server refuses an outsider walking
    /// into it, so offering the walk would only produce a 403.</para>
    /// </summary>
    /// <summary>
    /// The card's own footer, plus the owner's override when there is one.
    ///
    /// <para><b>Composed and not chosen.</b> The first version returned the award action only
    /// where the card had no footer of its own, and that removed it from every case worth
    /// having: a match somebody opened a room for and never played, or one the owner is in.
    /// Those are exactly the matches that need deciding by hand. So both are drawn, stacked,
    /// and the card gets taller for the owner alone.</para>
    /// </summary>
    private UIElement? BuildBracketFooter(TournamentDetail t, TournamentMatch m, MatchCardState state)
    {
        var own = BuildBracketFooterAction(t, m, state);
        var award = BuildAwardStrip(t, m, stacked: own != null);

        if (award == null) return own;
        if (own == null) return award;

        var both = new StackPanel();
        both.Children.Add(own);
        both.Children.Add(award);
        return both;
    }

    private UIElement? BuildBracketFooterAction(
        TournamentDetail t, TournamentMatch m, MatchCardState state)
    {
        string? actionKey = state switch
        {
            MatchCardState.Playable => "MpTournamentPlayMyMatch",
            MatchCardState.JoinRoom => "MpTournamentJoinRoom",
            MatchCardState.ReturnToRoom => "MpTournamentReturnToRoom",
            _ => null,
        };

        if (actionKey != null)
        {
            var b = new Button
            {
                Content = Strings.Get(actionKey),
                Margin = new Thickness(9, 6, 9, 9),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            // Returning to a room I already opened is a weaker offer than opening one or
            // walking into the one my opponent is waiting in, and it is drawn that way.
            b.SetResourceReference(FrameworkElement.StyleProperty,
                state == MatchCardState.ReturnToRoom ? "MpGhostButton" : "MpPrimaryButton");
            b.Click += (_, _) => { _ = OpenTournamentMatchAsync(t, m); };
            return b;
        }

        if (state is MatchCardState.InProgress or MatchCardState.SuperviseRoom)
        {
            bool supervising = state == MatchCardState.SuperviseRoom;
            var strip = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                // The button below takes the bottom margin when there is one, or the strip
                // would sit twice as far from the card's edge as it does on every other card.
                Margin = new Thickness(11, 2, 9, supervising ? 2 : 8),
            };
            var dot = new Border
            {
                Width = 5, Height = 5,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(2.5),
            };
            dot.SetResourceReference(Border.BackgroundProperty, "MpOk");
            strip.Children.Add(dot);

            var label = new TextBlock { Text = Strings.Get("MpTournamentInProgress") };
            label.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
            label.SetResourceReference(TextBlock.ForegroundProperty, "MpOkText");
            strip.Children.Add(label);
            if (!supervising) return strip;

            // The organiser keeps the line that says it is being played and gains a way to
            // look. A GHOST button, not a primary: this card is not asking to be pressed the
            // way "Play my match" is - it is a door for the one person who might have to
            // settle this match afterwards.
            var watch = new Button
            {
                Content = Strings.Get("MpTournamentWatchRoom"),
                Margin = new Thickness(9, 0, 9, 9),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            watch.SetResourceReference(FrameworkElement.StyleProperty, "MpGhostButton");
            watch.Click += (_, _) => OpenMatchWatch(t, m);

            var wrap = new StackPanel();
            wrap.Children.Add(strip);
            wrap.Children.Add(watch);
            return wrap;
        }

        if (state == MatchCardState.WaitingOpponent)
        {
            var w = new TextBlock
            {
                Text = Strings.Get("MpTournamentWaitingOpponent"),
                Margin = new Thickness(11, 2, 9, 8),
            };
            w.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
            w.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
            return w;
        }

        return null;
    }

    /// <summary>
    /// The owner's last resort on a match that is not going to be played: hand it to one side.
    ///
    /// <para>Everything behind this existed already and nothing called it —
    /// <see cref="TournamentPermissions.CanAwardOrDisqualify"/>, <c>AwardWalkoverAsync</c> and the
    /// server's own <c>/walkover</c> route, which records <c>decided_by = 'owner'</c> so the
    /// database can tell a person's decision from a recording's. The comment on the detail
    /// pane's overflow menu says it was meant to be wired and it was not.</para>
    ///
    /// <para><b>A menu and not two buttons.</b> The owner has to say WHICH side won, and the card
    /// is 220px wide with the two names already on it; a second row of two name-bearing buttons
    /// would double the card and, through <c>MeasureBracketRow</c>, every row of the bracket with
    /// it. One quiet button that opens the two names is the same idiom the detail pane already
    /// uses for its rare owner actions.</para>
    ///
    /// <para>Only on a match with both sides known and nothing decided: awarding an empty slot
    /// would advance a winner into a round nobody has reached, and the server would refuse it.</para>
    /// </summary>
    private UIElement? BuildAwardStrip(TournamentDetail t, TournamentMatch m, bool stacked)
    {
        var me = _demoTournaments ? TournamentDemoData.MeUserId : _session?.CurrentUser?.Id;
        if (!TournamentPermissions.CanAwardMatch(t, me, m)) return null;

        var button = new Button
        {
            Content = Strings.Get("MpTournamentAward"),
            // No top margin under another button: that one already carries its own 9 below.
            Margin = stacked ? new Thickness(9, 0, 9, 9) : new Thickness(9, 4, 9, 9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "MpGhostButton");

        var flyout = new ContextMenu();
        foreach (var id in new[] { m.Entrant1Id!, m.Entrant2Id! })
        {
            var side = t.Entrants?.FirstOrDefault(
                x => string.Equals(x.Id, id, StringComparison.Ordinal));
            var item = new MenuItem
            {
                Header = Strings.Format("MpTournamentAwardTo", side?.DisplayName ?? ""),
            };
            var winner = id;
            var name = side?.DisplayName ?? "";
            item.Click += (_, _) => { _ = ConfirmAwardAsync(t, m, winner, name); };
            flyout.Items.Add(item);
        }

        button.Click += (_, _) =>
        {
            flyout.PlacementTarget = button;
            flyout.IsOpen = true;
        };
        return button;
    }

    /// <summary>
    /// Ask before throwing somebody out, and name what it does to the bracket.
    ///
    /// <para>A disqualification is not only about the person: the server turns every pending
    /// match of theirs whose opponent is already known into a walkover, so one click can settle
    /// several matches. Saying that beforehand is the difference between a decision and a
    /// surprise — and none of it can be undone from the launcher.</para>
    /// </summary>
    private async Task ConfirmDisqualifyAsync(TournamentDetail t, TournamentEntrant e)
    {
        // The preview asks nothing and sends nothing: a question about throwing somebody out
        // of a tournament nobody created would be worse than no preview at all.
        if (_demoTournaments) { await ShowDemoInertNoticeAsync(); return; }

        bool ok = await MpAlertOverlay.ConfirmAsync(
            TabRootGrid,
            Strings.Get("MpTournamentDisqualifyConfirmTitle"),
            Strings.Format("MpTournamentDisqualifyConfirmBody", e.DisplayName ?? ""),
            Strings.Get("MpTournamentDisqualifyConfirmYes"),
            Strings.Get("MpAlertCancel"));
        if (!ok) return;

        await RunTournamentActionAsync(
            () => _session!.Api!.DisqualifyEntrantAsync(t.Id, e.Id));
    }

    /// <summary>
    /// Ask before handing a match to somebody, and say the part nobody else will say: from here
    /// it cannot be undone.
    ///
    /// <para>Undoing a bracket result is <c>tournament:void</c>, which is the maintainer's CLI on
    /// purpose — reversing a match a recording decided touches the anti-cheat story, so it lives
    /// where a dry run and a snapshot exist. The owner gets the power to settle a match and not the
    /// power to unsettle one, and the only honest place to say so is before the click lands.</para>
    /// </summary>
    private async Task ConfirmAwardAsync(
        TournamentDetail t, TournamentMatch m, string winnerEntrantId, string winnerName)
    {
        // Same as the disqualify path: the preview asks nothing and sends nothing.
        if (_demoTournaments) { await ShowDemoInertNoticeAsync(); return; }

        bool ok = await MpAlertOverlay.ConfirmAsync(
            TabRootGrid,
            Strings.Get("MpTournamentAwardConfirmTitle"),
            Strings.Format("MpTournamentAwardConfirmBody", winnerName),
            Strings.Get("MpTournamentAwardConfirmYes"),
            Strings.Get("MpAlertCancel"));
        if (!ok) return;

        await RunTournamentActionAsync(
            () => _session!.Api!.AwardWalkoverAsync(t.Id, m.Id, winnerEntrantId));
    }

    /// <summary>
    /// Open — or walk into — the room for one bracket match.
    ///
    /// <para>The server answers with the same shape <c>POST /lobbies</c> does, so this
    /// reuses the ordinary create-room and join paths rather than growing a second one. The
    /// TITLE comes from the server; the launcher never invents one.</para>
    /// </summary>
    /// <summary>
    /// Tell both sides of an undecided tie to play it again.
    ///
    /// <para><b>Nothing is undone, because there is nothing to undo.</b> Only offered on a
    /// match still <c>pending</c> - typically one whose game ended without a readable
    /// recording, which leaves the bracket slot open and closes the room. Both entrants can
    /// already open a fresh room on it; what they cannot do is find out that they should.
    /// This is the announcement, and the server closes whatever stale room is still standing
    /// so the next "play my match" is not refused as "you already have one open".</para>
    ///
    /// <para>Confirmed, because it acts on other people's screens, and because closing a room
    /// somebody may be sitting in should never be one click away.</para>
    /// </summary>
    private async Task ConfirmReplayAsync(TournamentDetail t, TournamentMatch m)
    {
        if (_demoTournaments) { await ShowDemoInertNoticeAsync(); return; }

        bool ok = await MpAlertOverlay.ConfirmAsync(
            TabRootGrid,
            Strings.Get("MpTournamentReplayConfirmTitle"),
            Strings.Format("MpTournamentReplayConfirmBody",
                           EntrantName(t, m.Entrant1Id), EntrantName(t, m.Entrant2Id)),
            Strings.Get("MpTournamentReplayConfirmYes"),
            Strings.Get("MpAlertCancel"));
        if (!ok) return;

        await RunTournamentActionAsync(() => _session!.Api!.ReplayMatchAsync(t.Id, m.Id));
    }

    private async Task OpenTournamentMatchAsync(TournamentDetail t, TournamentMatch m)
    {
        if (_demoTournaments) { await ShowDemoInertNoticeAsync(); return; }
        if (_session?.Api == null || string.IsNullOrEmpty(t.ModId)) return;

        try
        {
            // The active mod has to BE the tournament's mod. Switching it silently from
            // here would be a bigger decision than a bracket card should make, so this
            // says so and stops — the ordinary join flow is where auto-switching lives.
            var profile = _getActiveProfile?.Invoke();
            if (profile == null || _computeModFingerprint == null) return;
            if (!string.Equals(profile.Id, t.ModId, StringComparison.OrdinalIgnoreCase))
            {
                await MpAlertOverlay.NoticeAsync(
                    TabRootGrid,
                    Strings.Get("MpTournamentWrongModTitle"),
                    Strings.Format("MpTournamentWrongModBody", t.ModId ?? ""),
                    Strings.Get("MpAlertOk"));
                return;
            }

            var hash = await _computeModFingerprint(profile);
            if (string.IsNullOrEmpty(hash)) return;

            var resp = await _session.Api.OpenTournamentMatchLobbyAsync(
                t.Id, m.Id, new TournamentLobbyRequest { ModCombinedHash = hash! });

            if (resp.Existing
                && !string.Equals(resp.HostUserId, _session.CurrentUser?.Id, StringComparison.Ordinal))
            {
                await JoinByLobbyIdAsync(resp.Id);
                return;
            }

            // Whether we created it or walked back into it, entering is the same path:
            // the creator is already a member of the room the server just made.
            await JoinByLobbyIdAsync(resp.Id);
        }
        catch (LobbyApiException ex)
        {
            await MpAlertOverlay.NoticeAsync(
                TabRootGrid,
                Strings.Get("MpTournamentActionFailed"),
                TournamentErrorText(ex),
                Strings.Get("MpAlertOk"));
        }
        catch
        {
            // Nothing actionable to say.
        }
    }

    // ---------------------------------------------------------------
    // The global-socket push
    // ---------------------------------------------------------------

    /// <summary>
    /// Something in a tournament moved.
    ///
    /// <para>It arrives on the GLOBAL socket rather than the room's, because by the time a
    /// bracket advances the room has been closed for minutes. Handled the same way the
    /// rating correction is: a toast, and a refresh if the subtab happens to be open.</para>
    /// </summary>
    /// <summary>Matches the REST client's options: SQLite has no booleans, so a raw
    /// 0/1 must not throw and take a whole frame down with it.</summary>
    private static readonly JsonSerializerOptions TournamentJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new TolerantBoolConverter(), new TolerantNullableBoolConverter() },
    };

    private void HandleTournamentUpdateFrame(string json)
    {
        TournamentUpdateNotice? n;
        try
        {
            n = JsonSerializer.Deserialize<TournamentUpdateNotice>(json, TournamentJson);
        }
        catch
        {
            return;
        }
        if (n == null) return;

        string title = n.Kind switch
        {
            "match_ready" => Strings.Get("MpTournamentToastReady"),
            "room_opened" => Strings.Get("MpTournamentToastRoomOpened"),
            "match_done" => Strings.Get(n.YouWon == true
                ? "MpTournamentToastWon" : "MpTournamentToastLost"),
            "entry_accepted" => Strings.Get("MpTournamentToastAccepted"),
            "entry_promoted" => Strings.Get("MpTournamentToastPromoted"),
            _ => Strings.Get("MpSubtabTournaments"),
        };

        _showAppToast?.Invoke(new AppToast.ToastOptions(
            "🏆",
            title,
            n.TournamentName ?? "",
            System.Array.Empty<AppToast.ToastAction>(),
            PreferDesktop: true));

        // Whatever it was, our copy is now stale.
        _tournamentsFetchedUtc = DateTime.MinValue;
        if (_activeSubtab == Subtab.Tournaments) _ = RefreshTournamentsAsync(force: true);
    }

    /// <summary>
    /// Create a tournament, and select it so the owner lands on their own draft.
    ///
    /// <para>The dialog collects a REQUEST; the server decides. It clamps the capacity,
    /// works out whether the mod has a ladder, and echoes what it actually made — the
    /// launcher holds no copy of those rules.</para>
    /// </summary>
    private async void TournamentCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_demoTournaments)
        {
            // The dialog itself is safe to show: it collects a request and sends nothing.
            // Showing it and then saying nothing was sent beats a visible button that does
            // nothing at all, and it is how the preview reaches the one screen of this
            // feature that is not part of the tab.
            ShowDemoCreateDialog();
            await ShowDemoInertNoticeAsync();
            return;
        }

        if (_session?.Api == null) return;
        var profile = _getActiveProfile?.Invoke();
        if (profile == null) return;

        // The mod, for the proposed name. The dialog does not otherwise know it - mod_id is
        // stamped on the request below, after this returns - so this is the one thing it has
        // to be handed, and the caller has been holding it all along.
        var dlg = new CreateTournamentDialog(profile.DisplayName);
        try { dlg.Owner = Window.GetWindow(this); } catch { /* off-tree */ }
        if (dlg.ShowDialog() != true) return;

        try
        {
            var created = await _session.Api.CreateTournamentAsync(new
            {
                name = dlg.EnteredName,
                mod_id = profile.Id,
                format = dlg.Format,
                team_source = dlg.TeamSource,
                entry_mode = dlg.EntryMode,
                capacity = dlg.Capacity,
            });

            // It is born a draft, which the public list hides. Selecting it is what stops
            // the creator having to hunt for the thing they just made.
            _selectedTournamentId = created.Id;
        }
        catch (LobbyApiException ex)
        {
            await MpAlertOverlay.NoticeAsync(
                TabRootGrid,
                Strings.Get("MpTournamentActionFailed"),
                TournamentErrorText(ex),
                Strings.Get("MpAlertOk"));
            return;
        }
        catch
        {
            return;
        }

        await RefreshTournamentsAsync(force: true);
    }

    private void SubtabRanking_Click(object sender, RoutedEventArgs e)
    {
        _activeSubtab = Subtab.Ranking;
        RefreshFromSession();
        // Same self-limiting fetch the Rooms subtab uses: inside the server's own cache
        // window this returns without asking anything.
        if (_session?.Status == MultiplayerSession.SessionStatus.SignedIn)
            _ = RefreshActivityStripAsync();
    }

    /// <summary>
    /// "See all" on the strip's ranking card — the handoff's link to the whole table.
    ///
    /// <para>Before this the RANKING subtab was reachable only from the top-level button, which
    /// is a long way from the three rows that make somebody want to see the rest.</para>
    /// </summary>
    private void ActivityRankingSeeAll_Click(object sender, RoutedEventArgs e)
        => SubtabRanking_Click(sender, e);


    private void RankingModeSolo_Click(object sender, RoutedEventArgs e)
    {
        _rankingMode = RankingMode.Solo;
        RenderRanking();
    }

    private void RankingModeTeam_Click(object sender, RoutedEventArgs e)
    {
        _rankingMode = RankingMode.Team;
        RenderRanking();
    }

    /// <summary>
    /// Draws the whole ladder — the table the community strip only shows the top of.
    ///
    /// <para>Both ladders come out of one payload and one request. The 1v1 table is always
    /// offered; the TEAM selector appears only when the backend actually sent that list,
    /// because a null there means "this server has no team ladder" and offering a tab that
    /// can only ever be empty is worse than not offering it.</para>
    ///
    /// <para>Ranks are the server's. Nothing here renumbers — a client that did would
    /// report the fourth player as the third the moment it filtered its own copy, and two
    /// people looking at the same table would read different positions.</para>
    /// </summary>
    private void RenderRanking()
    {
        if (RankingBody == null) return;
        RankingBody.Children.Clear();
        RankingHeaderHost.Children.Clear();
        RankingPinnedRow.Children.Clear();
        RankingPinnedRow.Visibility = Visibility.Collapsed;
        _rankingOwnRow = null;

        // The right-hand column, every time the page draws. It is cheap (two lists of five)
        // and hanging it off a fetch instead would leave it blank on the common path, where
        // the data is already cached and no fetch runs.
        RenderRankingSummaryCards();

        var team = Services.Multiplayer.CommunityStatsView.TeamRows(_communityStats);
        var hasTeamLadder = team != null;

        RankingModeTeam.Visibility = hasTeamLadder ? Visibility.Visible : Visibility.Collapsed;
        if (!hasTeamLadder && _rankingMode == RankingMode.Team) _rankingMode = RankingMode.Solo;

        RankingModeSolo.Tag = _rankingMode == RankingMode.Solo ? "active" : null;
        RankingModeTeam.Tag = _rankingMode == RankingMode.Team ? "active" : null;


        var rows = _rankingShowsTeam
            ? team ?? new List<Models.Multiplayer.LeaderboardRow>()
            : Services.Multiplayer.CommunityStatsView.Rows(_communityStats);

        RenderRankingChrome(rows.Count);

        if (rows.Count == 0)
        {
            // The same sentence the strip uses, and for the same reason: an empty ladder
            // that explains its own entry requirement is a fact, while a blank panel reads
            // as something broken. The number is the server's; with none we say nothing.
            var required = Services.Multiplayer.CommunityStatsView.RequiredDecided(_communityStats);
            RankingBody.Children.Add(new TextBlock
            {
                Text = required.HasValue
                    ? Strings.Format("MpActivityRankingEmpty", required.Value)
                    : Strings.Get("MpRankingUnavailable"),
                Foreground = (Brush)Application.Current.FindResource("MpTextDim"),
                FontSize = (double)Application.Current.FindResource("MpMetaSize"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(14, 12, 14, 14),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            });
            return;
        }

        RankingHeaderHost.Children.Add(BuildRankingHeader());

        // The bar beside each rating is measured against the top and bottom of THIS table —
        // see RankingTableLayout.BarFraction for why not against zero.
        var highest = double.MinValue;
        var lowest = double.MaxValue;
        foreach (var r in rows)
        {
            if (r.Rating > highest) highest = r.Rating;
            if (r.Rating < lowest) lowest = r.Rating;
        }

        var meId = _session?.CurrentUser?.Id;
        foreach (var row in rows)
        {
            var isMe = !string.IsNullOrEmpty(meId)
                && string.Equals(row.UserId, meId, StringComparison.Ordinal);
            var element = (FrameworkElement)BuildLeaderboardRow(row, lowest, highest, isMe);
            RankingBody.Children.Add(element);
            if (isMe) _rankingOwnRow = element;
        }

        if (_rankingOwnRow != null)
        {
            // A SECOND copy of the row, built once and shown only while the real one is out
            // of sight. Built here rather than on demand because building it inside the
            // scroll handler would mean re-laying it out on every wheel tick.
            var me = rows.First(r => string.Equals(r.UserId, meId, StringComparison.Ordinal));
            var pinned = BuildLeaderboardRow(me, lowest, highest, isMe: true);
            RankingPinnedRow.Children.Add(new Border
            {
                Child = pinned,
                BorderBrush = (Brush)Application.Current.FindResource("MpOwnRowRim"),
                BorderThickness = new Thickness(0, 1, 0, 0),
            });
        }

        // Deferred: the ScrollViewer has not measured yet, so asking now would compare
        // against a zero-height viewport and pin the row on a table that fits.
        Dispatcher.BeginInvoke(new Action(UpdateRankingPinnedRow),
                               System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// The civilization-balance table, in the same card the ladder uses.
    ///
    /// <para>Only RATED 1v1s reach it, and it is grouped by mod AND version server-side — a
    /// figure that averages two builds of a mod stops meaning anything at exactly the moment
    /// somebody changes one, which is the moment it exists for.</para>
    ///
    /// <para><b>It will be empty for weeks and says so.</b> Civilizations are only reported from
    /// the build that introduced them onwards and nothing can fill them in backwards, so the
    /// empty state has to explain itself or it reads as broken rather than as new.</para>
    /// </summary>
    /// <summary>
    /// Which mod's figures the page is showing, or null before it has been resolved.
    ///
    /// <para><b>This replaced a viewer scope.</b> The page briefly offered "whole community"
    /// against "only mine", and that was the wrong axis: this is a community page, the
    /// player's own numbers live on Profile, and the two halves were computed from different
    /// sources over different windows. What actually needed separating was the MOD.</para>
    ///
    /// <para>And that separation is not a nicety. <c>/stats/civs</c>, <c>/stats/matchups</c>
    /// and <c>/stats/decks</c> have always grouped by <c>mod_id</c>, and the launcher drew
    /// every row: with two mods installed, or two builds of one mod, the same civilization
    /// appeared twice with different numbers and nothing said why.</para>
    /// </summary>
    private string? _statsModId;

    /// <summary>How many matches a map needs before it earns a row of its own.
    ///
    /// <para>Below this the tail is grouped. A map played once says nothing about anybody's
    /// preferences, and eight of them took eight rows to say it - which is the same absence
    /// of a sample the civilization rules already refuse to publish a percentage from.</para>
    /// </summary>
    private const int MapRowMinMatches = 2;

    /// <summary>Rows before a table gets a "see all" of its own.</summary>
    private const int MapRowsShown = 7;

    /// <summary>True once the player asked to see the whole map list.</summary>
    private bool _statsMapsExpanded;

    /// <summary>
    /// Which ladder the page is about: null or <c>default</c> for 1v1, <c>team</c> for 2v2 and 3v3.
    ///
    /// <para>Half this page used to exclude team games without saying so: civilizations and
    /// matchups filtered them out at the server, while the maps, the totals and the activity
    /// counted them in. One page, two criteria, nothing on screen admitting it.</para>
    /// </summary>
    private string? _statsMode;

    /// <summary>
    /// Which mods the SERVER has matches for, and how many of each were team games.
    ///
    /// <para>This is what makes a newly catalogued mod appear here on its own. The picker used
    /// to offer installed mods only, so a mod added to the catalogue stayed invisible until
    /// somebody installed it — even with a hundred matches behind it. Null until the request
    /// lands, and null forever against a backend without the route, which just means the
    /// picker keeps offering what it always did.</para>
    /// </summary>
    private List<Models.Multiplayer.StatsModEntry>? _statsMods;

    private DateTime _statsModsFetchedUtc = DateTime.MinValue;
    private bool _statsModsInFlight;

    /// <summary>Which ladder to ask the server about. Never null: <c>default</c> is 1v1.</summary>
    private string StatsMode()
        => string.Equals(_statsMode, "team", StringComparison.Ordinal) ? "team" : "default";

    /// <summary>Whether this page is showing team games.</summary>
    private bool StatsTeamMode() => StatsMode() == "team";

    /// <summary>
    /// Whether the current mod has any team matches to switch to.
    ///
    /// <para>Answered from the mod catalogue, which counts them per mod, so the switch appears
    /// for a mod that has 2v2s and stays hidden for one that does not. Unknown counts as
    /// "no": before the catalogue lands there is nothing saying the other side has anything,
    /// and offering a switch that leads to an empty page is worse than offering none.</para>
    ///
    /// <para>Except while a preview is on screen, where the fixtures decide.</para>
    /// </summary>
    private bool StatsTeamAvailable()
    {
        if (_demoStats) return Services.Multiplayer.StatsDemoData.HasTeamData(StatsModId());
        if (_statsMods == null) return false;
        string mod = StatsModId();
        foreach (var entry in _statsMods)
        {
            if (string.Equals(entry.ModId, mod, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Team > 0;
            }
        }
        return false;
    }

    /// <summary>
    /// The mod the statistics page is about, falling back to the one being played.
    ///
    /// <para>Never null once there is any mod at all, because "every mod at once" is exactly
    /// the state this page was in when it was wrong.</para>
    /// </summary>
    private string StatsModId()
    {
        if (!string.IsNullOrWhiteSpace(_statsModId)) return _statsModId!;
        var active = _getActiveProfile?.Invoke();
        return !string.IsNullOrWhiteSpace(active?.Id)
            ? active!.Id
            : Services.ModRegistry.Default.Id;
    }

    /// <summary>
    /// The order the chips are drawn in, given the set that belongs in the row.
    ///
    /// <para><b>Nothing here may depend on which chip is selected.</b> That is the whole
    /// reason this is a separate, pure function. The row used to be built by offering the
    /// selected mod THIRD, ahead of the server list and the installed walk, so clicking a mod
    /// hoisted it and shoved every chip after it sideways — pick the last one and the row
    /// rearranged itself under the cursor. The old shape could not express the difference
    /// between "who is in the row" and "in what order", because one loop answered both.</para>
    ///
    /// <para>The order is the CATALOGUE's, Wars of Liberty first. Deliberately not the
    /// server's "most played first": that ranking is data that moves on its own, so a
    /// <c>/stats/mods</c> refresh 60 seconds later reordered the row a second time, with
    /// nobody having touched anything. A picker is not a leaderboard.</para>
    ///
    /// <para>A wanted id the catalogue does not carry is appended in id order rather than
    /// dropped — dropping the SELECTED one would leave the row showing every option except
    /// the one whose figures are on screen. Sorted, not caller-ordered, so this stays true:
    /// the same set always draws the same row.</para>
    /// </summary>
    internal static List<string> StatsModOrder(
        string? defaultId, IEnumerable<string?> catalogue, IEnumerable<string?> wanted)
    {
        var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in wanted)
        {
            if (!string.IsNullOrWhiteSpace(id)) want.Add(id!);
        }

        var order = new List<string>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Place(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            if (!want.Contains(id!)) return;
            if (placed.Add(id!)) order.Add(id!);
        }

        // Wars of Liberty first, always, as asked: it is the mod this launcher is for and the
        // only one with a ladder behind it.
        Place(defaultId);
        foreach (var id in catalogue) Place(id);

        foreach (var id in want.Where(id => !placed.Contains(id))
                               .OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            order.Add(id);
        }
        return order;
    }

    /// <summary>
    /// The mods the picker offers: Wars of Liberty, whatever the server has matches for, and
    /// whatever is installed here — drawn in <see cref="StatsModOrder"/>'s order.
    ///
    /// <para>Deliberately NOT "the mods that appear in the data". Once the payloads are
    /// filtered by mod they only ever contain the one that was asked for, so discovering the
    /// list from them would leave whichever mod was selected as the only option — a picker
    /// that cannot be moved off its current value.</para>
    ///
    /// <para>Same installed-mod walk the create-room dialog does, for the same reason: a mod
    /// with no install has no matches to show and no way to play one. The server half is what
    /// makes adding a mod cost nothing but a catalogue entry: it turns up here the moment
    /// somebody plays one game of it, installed on this machine or not. An id the local
    /// catalogue does not know is skipped rather than drawn raw — an internal name never
    /// reaches a player, and there would be no icon or name to draw.</para>
    /// </summary>
    private List<ModProfile> StatsModOptions()
    {
        var wanted = new List<string?>
        {
            Services.ModRegistry.Default?.Id,
            _getActiveProfile?.Invoke()?.Id,
            // Whatever is currently selected, installed or not: a chosen mod with no chip
            // leaves the row showing every option EXCEPT the one on screen. It contributes
            // MEMBERSHIP only — see StatsModOrder for why that distinction is the fix.
            _statsModId,
        };
        foreach (var entry in _statsMods ?? new List<Models.Multiplayer.StatsModEntry>())
        {
            wanted.Add(entry.ModId);
        }
        foreach (var p in Services.ModRegistry.All)
        {
            if (!string.IsNullOrWhiteSpace(GetInstallPath(p))) wanted.Add(p.Id);
        }

        var options = new List<ModProfile>();
        foreach (var id in StatsModOrder(
                     Services.ModRegistry.Default?.Id,
                     Services.ModRegistry.All.Select(p => (string?)p.Id),
                     wanted))
        {
            var profile = Services.ModRegistry.Find(id);
            if (profile != null) options.Add(profile);
        }
        return options;
    }

    /// <summary>
    /// The row of mods, each as its own icon and name.
    ///
    /// <para>Built from the pieces the room card already uses - <see cref="ResolveRoomModIcon"/>
    /// and its cache, and <c>ResolveModDisplayName</c> - rather than a new control, so a mod
    /// looks the same everywhere in this tab.</para>
    ///
    /// <para>With one mod installed there is nothing to choose between, and the row draws as a
    /// plain label instead of a control that cannot do anything.</para>
    /// </summary>
    /// <summary>
    /// What the drawn row is made of: which mods, in which order, and whether it is the
    /// single-mod label or a row of buttons.
    ///
    /// <para><b>Deliberately not which one is SELECTED.</b> A selection change moves a fill and
    /// needs no new buttons — folding it in here would rebuild the row on the one interaction
    /// most likely to have the pointer resting on it.</para>
    /// </summary>
    internal static string StatsChipSignature(IReadOnlyList<ModProfile> options)
        => string.Join("\u241f", options.Select(p => p.Id))
           + (options.Count == 1 ? "|one" : "|many");

    /// <summary>The signature of the row currently on screen; null when there is none.</summary>
    private string? _statsChipSignature;

    private void RenderStatsModPicker()
    {
        var options = StatsModOptions();
        string current = StatsModId();
        string signature = StatsChipSignature(options);

        // REBUILD ONLY WHEN THE ROW ACTUALLY CHANGED. This is what stops the flicker.
        //
        // One chip click repaints immediately and then invalidates all five timestamps, so
        // five requests go out and EACH landing calls RenderStatsTab() again. This method used
        // to Clear() and rebuild all five buttons on every one of those passes: the button
        // under the pointer was destroyed and recreated up to six times, losing IsMouseOver
        // each time and taking its hover down with it. Counted, that is exactly the "it
        // flickers several times" that was reported.
        //
        // On this path not one child is touched; only the fill moves. The Button check is what
        // makes the indexed cast below safe — a row that is not what the signature says it is
        // falls through and is rebuilt, so the guard can never leave the row lying.
        if (_statsChipSignature == signature
            && options.Count > 1
            && StatsModPicker.Children.Count == options.Count
            && StatsModPicker.Children.OfType<Button>().Count() == options.Count)
        {
            for (int i = 0; i < options.Count; i++)
            {
                ((Button)StatsModPicker.Children[i]).Tag =
                    string.Equals(options[i].Id, current, StringComparison.OrdinalIgnoreCase)
                        ? "active"
                        : null;
            }
            return;
        }

        StatsModPicker.Children.Clear();
        _statsChipSignature = options.Count == 0 ? null : signature;
        if (options.Count == 0) return;

        foreach (var profile in options)
        {
            bool active = string.Equals(profile.Id, current, StringComparison.OrdinalIgnoreCase);
            var content = BuildModChipContent(profile);

            if (options.Count == 1)
            {
                // One mod: a label, not a button. A control whose only option is already
                // chosen invites a click that does nothing.
                var only = new Border { Padding = new Thickness(9, 5, 11, 5), Child = content };
                only.SetResourceReference(Border.CornerRadiusProperty, "RadiusRow");
                only.SetResourceReference(Border.BackgroundProperty, "MpModBadgeBg");
                // The colour has to be set HERE and not on the chip's own TextBlock. In the
                // many-mod row the name inherits it from the MpSegment button, whose
                // Tag="active" trigger swaps it to white on the blue fill; painting the
                // TextBlock directly would win over that trigger and kill the selected state.
                // A Border is not a Control, so this branch inherits nothing and drew black.
                only.SetResourceReference(
                    System.Windows.Documents.TextElement.ForegroundProperty, "MpTextBody");
                StatsModPicker.Children.Add(only);
                return;
            }

            var button = new Button
            {
                Content = content,
                Tag = active ? "active" : null,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(9, 5, 11, 5),
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "MpSegment");
            var chosen = profile.Id;
            button.Click += (_, _) =>
            {
                if (string.Equals(_statsModId, chosen, StringComparison.OrdinalIgnoreCase)) return;
                _statsModId = chosen;
                _statsMapsExpanded = false;
                // Every figure on the page belongs to the mod that was just deselected, so
                // they all go together rather than one table at a time.
                InvalidateStatsForModChange();
                RenderStatsTab();
                RefreshStatsForMod();
            };
            StatsModPicker.Children.Add(button);
        }
    }

    /// <summary>
    /// The 1v1 / Teams switch, when there is anything on the other side of it.
    ///
    /// <para>Same capsule of segments as the ranking's own mode switch, and the same courtesy:
    /// there, the mode falls back to 1v1 when the server has no team ladder. Here it collapses
    /// entirely, because unlike the ranking this page has a per-mod answer to the question.</para>
    /// </summary>
    private void RenderStatsModePicker()
    {
        StatsModePicker.Children.Clear();

        if (!StatsTeamAvailable())
        {
            // A mod change can leave the page in a mode the new mod has no data for. Falling
            // back is not a preference being ignored: the alternative is an empty page whose
            // only explanation is a control that is no longer on screen.
            if (StatsTeamMode())
            {
                _statsMode = null;
                InvalidateStatsForModChange();
                RefreshStatsForMod();
            }
            StatsModeScope.Visibility = Visibility.Collapsed;
            return;
        }

        StatsModeScope.Visibility = Visibility.Visible;

        void AddSegment(string key, string? mode)
        {
            bool active = string.Equals(StatsMode(), mode ?? "default", StringComparison.Ordinal);
            var button = new Button
            {
                Content = Strings.Get(key),
                Tag = active ? "active" : null,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(11, 5, 11, 5),
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "MpSegment");
            button.Click += (_, _) =>
            {
                if (string.Equals(StatsMode(), mode ?? "default", StringComparison.Ordinal)) return;
                _statsMode = mode;
                _statsMapsExpanded = false;
                // Every figure on the page belongs to the ladder being left - the totals and
                // the maps as much as the civilizations - so they all go at once rather than
                // one table at a time.
                InvalidateStatsForModChange();
                RenderStatsTab();
                RefreshStatsForMod();
            };
            StatsModePicker.Children.Add(button);
        }

        AddSegment("MpStatsModeSolo", null);
        AddSegment("MpStatsModeTeam", "team");
    }

    /// <summary>
    /// An icon and a name, the pairing the room card and the mod switcher both use.
    ///
    /// <para>Internal so a test can assert what this does NOT do — see
    /// <c>TheModChipShowsTheWholeNameAndThereforeArmsNoReveal</c>. Same door the civ row uses.</para>
    /// </summary>
    internal UIElement BuildModChipContent(ModProfile profile)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        var icon = new Border
        {
            Width = 18,
            Height = 18,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(4),
            Background = ResolveRoomModIcon(profile),
        };
        row.Children.Add(icon);

        // NO TRIMMING, AND THAT IS THE FIX — not a preference.
        //
        // It used to trim at MaxWidth 160, which armed RevealText without anybody asking: the
        // implicit TextBlock style in Styles/Text.xaml turns the hover reveal on for ANY block
        // with an ellipsis. That reveal is built for flat table cells and came apart on this
        // chip three ways at once — it clones the font when it is built, so on the ACTIVE chip
        // it drew Medium/MpTextBody over text that MpSegment's Tag="active" trigger had since
        // made SemiBold/white; it painted its own bordered box on the blue fill; and it wraps
        // at 560px, so the full name landed on top of the NEXT chip.
        //
        // The trim was never needed. The catalogue schema caps displayName at 50 characters
        // and the worst real one — "Age of Empires III: The Asian Dynasties" — is 38, so a
        // chip can only ever get so wide; and the row is a WrapPanel inside a WrapPanel, put
        // there for exactly the case where five mods do not fit on one line.
        //
        // Don't put a MaxWidth back without knowing that it re-arms the reveal here.
        var name = new TextBlock
        {
            Text = ResolveModDisplayName(profile.Id),
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.SetResourceReference(TextBlock.FontSizeProperty, "MpLabelSize");
        row.Children.Add(name);
        return row;
    }

    /// <summary>
    /// Drop every figure that belonged to the mod being left.
    ///
    /// <para>Without this the page keeps the previous mod's tables on screen until each
    /// request lands, one at a time, so for a second or two it shows two mods at once - the
    /// exact confusion the mod scope exists to end.</para>
    /// </summary>
    private void InvalidateStatsForModChange()
    {
        // In a preview there is nothing to fetch, so dropping the payloads would leave the page
        // blank for good. The fixtures are simply rebuilt at the new scope instead.
        if (_demoStats)
        {
            ApplyDemoStats();
            return;
        }

        _civStats = null;
        _matchups = null;
        _deckStats = null;
        // The SCOPED one. _communityStats is unscoped now, so a mod change tells it nothing -
        // dropping it here is what used to blank the Rooms strip on the way past.
        _statsCommunity = null;
        _civStatsFetchedUtc = DateTime.MinValue;
        _matchupsFetchedUtc = DateTime.MinValue;
        _deckStatsFetchedUtc = DateTime.MinValue;
        // The folds belong to the mod whose table they were opened on. Another mod's
        // civilizations are a different set, and a group left open by internal name would
        // either mean nothing or - worse - silently match.
        _deckCivsOpen.Clear();
        _deckTailsOpen.Clear();
        _deckCardsOpen.Clear();
        _deckCivsSeeded = false;
        _deckCivsExpanded = false;
        _statsCommunityFetchedUtc = DateTime.MinValue;
    }

    /// <summary>
    /// The community payload AT THE STATISTICS PAGE'S MOD.
    ///
    /// <para>Its own request, because the strip's is deliberately unscoped now. Same 60-second
    /// window as the strip's, and only the Statistics page ever triggers it - so on the Rooms
    /// subtab, where the timer lives, nothing extra is asked for at all.</para>
    /// </summary>
    private async Task RefreshStatsCommunityAsync()
    {
        if (_demoStats) return;
        if (_session?.CurrentUser == null) return;
        if (DateTime.UtcNow - _statsCommunityFetchedUtc < ActivityMaxAge) return;

        try
        {
            _statsCommunity = await _session.Api.GetCommunityStatsAsync(
                modId: StatsModId(), mode: StatsMode());
            _statsCommunityFetchedUtc = DateTime.UtcNow;
            if (_activeSubtab == Subtab.Stats) RenderStatsTab();
        }
        catch (Exception ex)
        {
            // Best-effort, like the strip's: the page keeps whatever it had and says nothing.
            DiagnosticLog.Write($"Stats community: fetch failed: {ex.Message}");
        }
    }

    /// <summary>Ask for all five payloads at the current mod scope.</summary>
    private void RefreshStatsForMod()
    {
        if (_session?.Status != MultiplayerSession.SessionStatus.SignedIn) return;
        _ = RefreshStatsCommunityAsync();
        _ = RefreshStatsModsAsync();
        _ = RefreshCivStatsAsync();
        _ = RefreshMatchupsAsync();
        _ = RefreshDeckStatsAsync();
        _ = RefreshActivityStripAsync();
    }

    /// <summary>
    /// The STATS subtab: what the whole community has done, in one mod.
    ///
    /// <para>Two columns. The left one carries whichever table has something to say - the
    /// civilizations when they have a sample, the maps until then - and the right one carries
    /// what is read once rather than scanned: how the figures are measured, how many matches
    /// actually counted, and when people play.</para>
    /// </summary>
    private void RenderStatsTab()
    {
        // WHERE THE READER WAS. Every child of this page is rebuilt below, and a rebuilt panel
        // loses the scroll position: whichever payload lands last - the civilizations, the mod
        // catalogue, the card names - yanks the page. On the first paint it was worse than a
        // yank; the deck names arriving repainted the page onto the mod picker and the title
        // was simply gone, on a launcher nobody had touched.
        double offset = StatsScroll.VerticalOffset;

        StatsTitleText.Text = Strings.Get("MpSubtabStats");
        RenderStatsModPicker();
        RenderStatsModePicker();

        // A page of plausible figures looks exactly like a real one. Same banner the bracket
        // preview carries, and for the same reason: a screenshot without it ends up somewhere
        // as evidence of a community that has not played these matches.
        StatsDemoBanner.Visibility = _demoStats ? Visibility.Visible : Visibility.Collapsed;
        if (_demoStats) StatsDemoBanner.Text = Strings.Get("MpTournamentDemoBanner");

        RenderStatsCounts();
        RenderStatsColumns();

        // Restored UNCONDITIONALLY, zero included, and that is the case that matters. Clearing
        // the panel destroys whichever button had focus, WPF moves focus to the next one it
        // finds, and focus raises RequestBringIntoView - which scrolled a page nobody had
        // touched down to the mod picker and pushed the title off the top. Putting zero back is
        // what undoes it.
        //
        // After layout rather than now: the new content has no height yet, so an offset set
        // here would be clamped away. Background priority runs after the Loaded-priority work
        // that BringIntoView itself is queued at, which is the whole point of the ordering.
        Dispatcher.BeginInvoke(
            new Action(() => StatsScroll.ScrollToVerticalOffset(offset)),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// The honest counts.
    ///
    /// <para>Rated matches, out of all matches, then maps and the window. FOUR facts kept
    /// apart, because the version before this printed the total under the words "rated
    /// matches" - and the server's total carries no <c>rated</c> predicate at all. The two
    /// numbers are usually different and the difference is the interesting part.</para>
    /// </summary>
    private void RenderStatsCounts()
    {
        // The SCOPED payload, here and in the six builders below: this page is about one mod
        // and says so in its picker. _communityStats is everybody now.
        var totals = _statsCommunity?.Totals;
        int maps = totals?.TopMaps?.Count ?? 0;
        var parts = new List<string>();

        if (totals?.Rated is int rated)
        {
            parts.Add(Strings.Format("MpStatsHeadRated", rated, totals.Matches));
        }
        else
        {
            // An older backend sends no rated count. Saying how many matches there were is
            // still true; calling them rated would not be.
            parts.Add(Strings.Format("MpStatsHeadMatches", totals?.Matches ?? 0));
        }

        parts.Add(Strings.Format("MpStatsHeadMaps", maps));
        if (totals?.WindowDays is int days && days > 0)
        {
            parts.Add(Strings.Format("MpStatsWindowDays", days));
        }

        StatsCountsText.Text = string.Join("  \u00b7  ", parts);
    }

    /// <summary>Fill the two columns.</summary>
    private void RenderStatsColumns()
    {
        StatsLeftColumn.Children.Clear();
        StatsRightColumn.Children.Clear();

        var civs = CivRows();
        bool haveCivs = civs.Count > 0;

        // FIRST in the right column, above everything: it is one line tall, it only exists in
        // team mode, and under two full-height cards it fell below the fold - where the one
        // thing it answers, "what kind of team games are these", cannot be read at all.
        StatsRightColumn.Children.Add(BuildTeamFormatsCard());

        if (haveCivs)
        {
            // With a sample, the civilizations are what the page is for and the maps become
            // the sidebar. Without one, the other way round: an empty table does not deserve
            // the widest column on the page.
            StatsLeftColumn.Children.Add(BuildCivTableCard(civs));
            StatsRightColumn.Children.Add(BuildHowMeasuredCard());
            StatsRightColumn.Children.Add(BuildMapCard(compact: true));
        }
        else
        {
            StatsLeftColumn.Children.Add(BuildMapCard(compact: false));
            StatsRightColumn.Children.Add(BuildCivStatusCard());
        }

        // Under the civilization table, in the same column and at the same width: the
        // matchups answer the next question that table raises, and the decks are the other
        // thing people bring to a game. Both used to sit below BOTH columns, which left the
        // widest column on the page ending halfway down with nothing under it.
        StatsLeftColumn.Children.Add(BuildMatchupCard());
        // Directly under the rivals, and only in team mode: the two answer the two halves of
        // the same question and are read against each other.
        StatsLeftColumn.Children.Add(BuildAlliesCard());
        StatsLeftColumn.Children.Add(BuildDeckCard());

        StatsRightColumn.Children.Add(BuildMatchHealthCard());
        StatsRightColumn.Children.Add(BuildActivityCard());
    }

    /// <summary>
    /// How many matches actually counted, and why the rest did not.
    ///
    /// <para>Built from two fields that existed in the database since the rating rules were
    /// written and that no endpoint read. It is probably the most useful figure on the page
    /// for whoever maintains the mod: a community reporting forty games and rating half of
    /// them has a problem worth a name, and until now the page could not say so.</para>
    /// </summary>
    private UIElement BuildMatchHealthCard()
    {
        var totals = _statsCommunity?.Totals;
        if (totals?.Rated is not int rated || totals.Matches <= 0) return new StackPanel();

        var stack = new StackPanel();
        stack.Children.Add(StatsSectionLabel(Strings.Get("MpStatsHealthTitle")));

        var body = new StackPanel { Margin = new Thickness(13, 12, 13, 13) };

        var figures = new StackPanel { Orientation = Orientation.Horizontal };
        StatsFigure(figures, rated.ToString("N0"), Strings.Get("MpStatsHealthCounted"), "MpOkText");
        StatsFigure(figures, (totals.Matches - rated).ToString("N0"),
                    Strings.Get("MpStatsHealthNotCounted"),
                    totals.Matches - rated > 0 ? "MpCautionText" : "MpTextGhost");
        body.Children.Add(figures);

        // The commonest reason, in the server's own words. Not translated: it is an
        // identifier the maintainer greps for, and a translated one would not be findable.
        if (!string.IsNullOrWhiteSpace(totals.UnratedTopReason)
            && totals.UnratedTopReasonMatches is int n && n > 0)
        {
            var why = new TextBlock
            {
                Text = Strings.Format("MpStatsHealthReason", n, totals.UnratedTopReason!),
                Margin = new Thickness(0, 11, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            why.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
            why.SetResourceReference(TextBlock.ForegroundProperty, "MpTextMuted");
            body.Children.Add(why);
        }

        stack.Children.Add(StatsCard(body));
        return stack;
    }

    /// <summary>
    /// When people play, and how many are around.
    ///
    /// <para>The hours come from rooms OPENED, not matches played, and the wording says so -
    /// rooms are stamped by the server and never deleted, while a match only exists if
    /// somebody's game got reported at all. Drawing one and calling it the other would be the
    /// same class of mislabel this page just finished removing from its header.</para>
    /// </summary>
    private UIElement BuildActivityCard()
    {
        var activity = _statsCommunity?.Activity;
        var hours = activity?.Hours;
        if (hours == null || hours.Count == 0) return new StackPanel();

        var stack = new StackPanel();
        stack.Children.Add(StatsSectionLabel(Strings.Get("MpStatsActivityTitle")));

        var body = new StackPanel { Margin = new Thickness(13, 12, 13, 13) };

        int players = _statsCommunity?.Totals?.Players ?? 0;
        int playerDays = _statsCommunity?.Totals?.PlayersWindowDays ?? 0;
        if (players > 0 && playerDays > 0)
        {
            var figures = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 12),
            };
            StatsFigure(figures, players.ToString("N0"),
                        Strings.Format("MpStatsActivePlayers", playerDays), "MpTextHeading");
            body.Children.Add(figures);
        }

        // The busiest hour is the one fact worth stating in words; the bars carry the shape.
        int peak = 0;
        int peakHour = 0;
        foreach (var h in hours)
        {
            if (h.Count > peak) { peak = h.Count; peakHour = h.Hour; }
        }

        if (peak > 0)
        {
            var chart = new Grid { Height = 46 };
            for (int i = 0; i < hours.Count; i++)
            {
                chart.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star),
                });
            }

            for (int i = 0; i < hours.Count; i++)
            {
                var bar = new Border
                {
                    Margin = new Thickness(1, 0, 1, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(2),
                    // At least a sliver for an hour nobody played, so the axis reads as an
                    // axis rather than as a gap in the data.
                    Height = Math.Max(2, 46.0 * hours[i].Count / peak),
                };
                bar.SetResourceReference(Border.BackgroundProperty,
                    hours[i].Count == peak ? "MpAction" : "MpBarFillDim");
                Grid.SetColumn(bar, i);
                chart.Children.Add(bar);
            }
            body.Children.Add(chart);

            var caption = new TextBlock
            {
                // The server sends UTC and says so; the launcher knows its own offset.
                Text = Strings.Format("MpStatsActivityPeak", LocalHourLabel(peakHour)),
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            caption.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
            caption.SetResourceReference(TextBlock.ForegroundProperty, "MpTextMuted");
            body.Children.Add(caption);
        }

        stack.Children.Add(StatsCard(body));
        return stack;
    }

    /// <summary>A UTC hour bucket as this machine's own clock reads it.</summary>
    private static string LocalHourLabel(int utcHour)
    {
        var utc = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day,
                               Math.Clamp(utcHour, 0, 23), 0, 0, DateTimeKind.Utc);
        return utc.ToLocalTime().ToString("HH:mm");
    }

    /// <summary>One big monospaced number with its caption, the shape used across this page.</summary>
    private static void StatsFigure(Panel host, string value, string caption, string ink)
    {
        var cell = new StackPanel { Margin = new Thickness(0, 0, 22, 0) };

        var big = new TextBlock { Text = value, FontWeight = FontWeights.Bold };
        big.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        big.SetResourceReference(TextBlock.FontSizeProperty, "MpFigureHeadlineSize");
        big.SetResourceReference(TextBlock.ForegroundProperty, ink);
        cell.Children.Add(big);

        var label = new TextBlock
        {
            Text = caption,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 120,
        };
        label.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
        label.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
        cell.Children.Add(label);

        host.Children.Add(cell);
    }

    /// <summary>The civilization rows the server sent for this mod.</summary>
    /// <summary>
    /// The civilization rows the server sent, carrying BOTH names: the internal one, which is
    /// what the flag and the fold are keyed by, and the resolved one, which is what a player is
    /// allowed to read.
    /// </summary>
    private List<(string Civ, string Label, int Played, int Wins, int Losses)> CivRows()
        => (_civStats?.Civs ?? new List<Models.Multiplayer.CivStatEntry>())
            .Select(c => (c.Civ ?? "", StatsCivLabel(c.Civ), c.Played, c.Wins, c.Losses))
            .ToList();

    /// <summary>The map rows the server sent for this mod.</summary>
    private List<(string Map, int Matches)> MapRows()
        => (_statsCommunity?.Totals?.TopMaps ?? new List<Models.Multiplayer.MapCount>())
            .Select(m => (m.Map ?? "", m.Matches))
            .ToList();

    // ---------------------------------------------------------------- the cards

    private static Border StatsCard(UIElement content, double topMargin = 0)
    {
        var card = new Border
        {
            Margin = new Thickness(0, topMargin, 0, 14),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusPanel");
        card.SetResourceReference(Border.BackgroundProperty, "MpPanel");
        card.SetResourceReference(Border.BorderBrushProperty, "MpRimFaint");
        return card;
    }

    private static UIElement StatsSectionLabel(string text, string? trailing = null)
    {
        var row = new Grid { Margin = new Thickness(1, 0, 1, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            // Uppercased HERE and not in the string: MpCivsTitle is also the ranking summary
            // card's heading, where sentence case is right. This page's section labels are all
            // uppercase, and one of them in sentence case reads as a mistake.
            Text = text.ToUpperInvariant(),
            FontWeight = FontWeights.SemiBold,
        };
        label.SetResourceReference(TextBlock.FontSizeProperty, "MpSectionLabelSize");
        label.SetResourceReference(TextBlock.ForegroundProperty, "MpTextLabel");
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        if (!string.IsNullOrEmpty(trailing))
        {
            var extra = new TextBlock { Text = trailing, VerticalAlignment = VerticalAlignment.Bottom };
            extra.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
            extra.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
            Grid.SetColumn(extra, 1);
            row.Children.Add(extra);
        }

        return row;
    }

    private static TextBlock StatsFootnote(string text)
    {
        var note = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(1, 0, 1, 16),
        };
        note.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
        note.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
        return note;
    }

    /// <summary>
    /// The map table: a rank, a clean name, a proportional bar and the count.
    ///
    /// <para>The bar is the change worth having. The rows used to stretch to the window with
    /// the number at the far end, so comparing two of them meant crossing the screen twice
    /// and no shape carried the comparison at all.</para>
    /// </summary>
    private UIElement BuildMapCard(bool compact)
    {
        var all = MapRows();
        var stack = new StackPanel();
        stack.Children.Add(StatsSectionLabel(
            Strings.Get("MpStatsMapsTitle"),
            all.Count > 0 ? Strings.Format("MpStatsMapsOver", all.Sum(m => m.Matches)) : null));

        if (all.Count == 0)
        {
            stack.Children.Add(StatsCard(BuildTableEmpty(
                Strings.Get(_statsCommunity == null ? "MpCivsLoading" : "MpCivsEmpty"))));
            return stack;
        }

        // The tail: everything with too little behind it to be worth a row. Grouped, not
        // dropped - the count still belongs to the total above.
        var shown = all.Where(m => m.Matches >= MapRowMinMatches).ToList();
        var tail = all.Where(m => m.Matches < MapRowMinMatches).ToList();
        if (!_statsMapsExpanded && shown.Count > MapRowsShown)
        {
            tail.InsertRange(0, shown.Skip(MapRowsShown));
            shown = shown.Take(MapRowsShown).ToList();
        }

        int max = all.Max(m => m.Matches);
        var rows = new StackPanel();
        for (int i = 0; i < shown.Count; i++)
        {
            rows.Children.Add(BuildMapRow(
                i + 1, shown[i].Map, shown[i].Matches, max, compact,
                isLast: i == shown.Count - 1 && tail.Count == 0));
        }

        if (tail.Count > 0) rows.Children.Add(BuildTailRow(tail));

        stack.Children.Add(StatsCard(rows));
        if (tail.Count > 0) stack.Children.Add(StatsFootnote(Strings.Get("MpStatsTailMapsWhy")));
        return stack;
    }

    private UIElement BuildMapRow(
        int rank, string rawName, int count, int max, bool compact, bool isLast)
    {
        var (name, pack) = Services.Multiplayer.LocalMatchView.MapLabel(rawName);

        var grid = new Grid { Margin = new Thickness(13, 8, 13, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            // Fixed in both, and NARROW in the sidebar. Star-sizing it there made the bar and
            // the name split the column, which cost the name its last few letters - and a
            // proportion reads the same at 80px as at 168 while a trimmed name does not.
            Width = new GridLength(compact ? 84 : 168),
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });

        var position = new TextBlock
        {
            Text = rank.ToString(),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        };
        position.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        position.SetResourceReference(TextBlock.FontSizeProperty, "MpFigureSize");
        position.SetResourceReference(TextBlock.ForegroundProperty,
            rank == 1 ? "MpActionText" : "MpTextGhost");
        Grid.SetColumn(position, 0);
        grid.Children.Add(position);

        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 11, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var mapName = new TextBlock
        {
            Text = name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        };
        mapName.SetResourceReference(TextBlock.FontSizeProperty, "MpBodySize");
        mapName.SetResourceReference(TextBlock.ForegroundProperty,
            rank == 1 ? "MpTextHeading" : "MpTextPrimary");
        label.Children.Add(mapName);

        // The pack as a LABEL and not as part of the name. It is real information - which
        // pack a map came from - but "ESOC_Fertile Crescent" is a file, not a name, and the
        // launcher does not print internal names where a player can see them.
        if (pack != null && !compact)
        {
            var tag = BuildTag(pack, "MpTextMuted", "MpNeutralBadgeBg");
            tag.VerticalAlignment = VerticalAlignment.Center;
            label.Children.Add(tag);
        }
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var bar = BuildProportionBar(count, max, rank == 1);
        Grid.SetColumn(bar, 2);
        grid.Children.Add(bar);

        var value = new TextBlock
        {
            Text = count.ToString("N0"),
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(11, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        };
        value.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        value.SetResourceReference(TextBlock.FontSizeProperty, "MpFigureSize");
        value.SetResourceReference(TextBlock.ForegroundProperty, "MpTextPrimary");
        Grid.SetColumn(value, 3);
        grid.Children.Add(value);

        var row = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, isLast ? 0 : 1),
            Child = grid,
        };
        row.SetResourceReference(Border.BorderBrushProperty, "MpRimHair");
        return row;
    }

    /// <summary>A share of the biggest value, as a bar. The leader is brighter, so it reads
    /// as the leader without a colour nobody else on the page uses.</summary>
    private static UIElement BuildProportionBar(int value, int max, bool leader)
    {
        var track = new Border { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        track.SetResourceReference(Border.BackgroundProperty, "MpBarTrack");
        track.CornerRadius = new CornerRadius(3);

        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(1, value), GridUnitType.Star),
        });
        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0, max - value), GridUnitType.Star),
        });

        var fill = new Border { Height = 6, CornerRadius = new CornerRadius(3) };
        fill.SetResourceReference(Border.BackgroundProperty, leader ? "MpAction" : "MpBarFillDim");
        Grid.SetColumn(fill, 0);
        host.Children.Add(fill);

        track.Child = host;
        return track;
    }

    /// <summary>The grouped tail: how many, which ones, and what they add up to.</summary>
    private UIElement BuildTailRow(List<(string Map, int Matches)> tail)
    {
        var grid = new Grid { Margin = new Thickness(13, 9, 13, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var words = new StackPanel();
        var head = new TextBlock
        {
            Text = Strings.Format("MpStatsTailMaps", tail.Count),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        head.SetResourceReference(TextBlock.FontSizeProperty, "MpLabelSize");
        head.SetResourceReference(TextBlock.ForegroundProperty, "MpTextSecondary");
        words.Children.Add(head);

        var names = new TextBlock
        {
            Text = string.Join(", ", tail
                .Take(4)
                .Select(m => Services.Multiplayer.LocalMatchView.MapLabel(m.Map).Name))
                + (tail.Count > 4 ? "\u2026" : ""),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 8, 0),
        };
        names.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
        names.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
        words.Children.Add(names);

        Grid.SetColumn(words, 0);
        grid.Children.Add(words);

        var total = new TextBlock
        {
            Text = tail.Sum(m => m.Matches).ToString("N0"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 10, 0),
            FontWeight = FontWeights.SemiBold,
        };
        total.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        total.SetResourceReference(TextBlock.FontSizeProperty, "MpFigureSize");
        total.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
        Grid.SetColumn(total, 1);
        grid.Children.Add(total);

        if (!_statsMapsExpanded)
        {
            var seeAll = new Button
            {
                Content = Strings.Get("MpStatsSeeAll"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            seeAll.SetResourceReference(FrameworkElement.StyleProperty, "MpLinkButton");
            seeAll.Click += (_, _) => { _statsMapsExpanded = true; RenderStatsTab(); };
            Grid.SetColumn(seeAll, 2);
            grid.Children.Add(seeAll);
        }

        var row = new Border { Child = grid };
        row.SetResourceReference(Border.BackgroundProperty, "MpPanelDim");
        return row;
    }

    /// <summary>A name on the left and a count hard right. Shared by the map table and both
    /// summary cards, so the three cannot drift apart on spacing or on trimming.</summary>
    internal static Grid BuildCountRow(string label, int count)
    {
        var grid = new Grid { Margin = new Thickness(14, 7, 14, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = label,
            Foreground = (Brush)Application.Current.FindResource("MpTextBody"),
            FontSize = (double)Application.Current.FindResource("MpLabelSize"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var n = new TextBlock
        {
            Text = count.ToString("N0"),
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("MpTextHeading"),
            FontSize = (double)Application.Current.FindResource("MpLabelSize"),
            FontWeight = FontWeights.SemiBold,
        };
        Grid.SetColumn(n, 1);
        grid.Children.Add(n);
        return grid;
    }

    /// <summary>Which half of the profile page is showing.</summary>
    private enum ProfileSection { Overview, Decks }

    private ProfileSection _profileSection = ProfileSection.Overview;

    private bool _mpDecksLoaded;
    private readonly List<Models.HomeCityProfile> _mpDeckProfiles = new();
    private IReadOnlyDictionary<string, Services.CardDetail> _mpCardDetails =
        new Dictionary<string, Services.CardDetail>();
    private IReadOnlyDictionary<string, ImageSource> _mpCardIcons =
        new Dictionary<string, ImageSource>();
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _mpCardEffects =
        new Dictionary<string, IReadOnlyList<string>>();
    private readonly Dictionary<string, string> _mpDeckCivNames = new(StringComparer.OrdinalIgnoreCase);

    private Models.HomeCityDeckEntry? _mpSelectedDeck;
    private Border? _mpSelectedTile;

    // The detail panel's parts, held so a card click repaints them without rebuilding the page.
    private Border? _mpDetailCard;
    private Image? _mpDetailIcon;
    private TextBlock? _mpDetailName;
    private TextBlock? _mpDetailText;
    private StackPanel? _mpDetailEffects;

    /// <summary>
    /// The two halves of this page, as pills.
    ///
    /// <para><b>Decks are a section rather than a card in the page.</b> With the game's art and
    /// what each card does they are a screen in their own right, and stacked under the profile
    /// they would push the history — the thing most people come here for — a screenful down.
    /// Same pattern as the history's own filter chips, which sit a few hundred lines below.</para>
    /// </summary>
    private UIElement BuildProfileSectionPills()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 11),
        };

        foreach (var (section, key) in new[]
                 {
                     (ProfileSection.Overview, "MpProfileSectionOverview"),
                     (ProfileSection.Decks, "MpProfileSectionDecks"),
                 })
        {
            var pill = new Button
            {
                Content = Strings.Get(key),
                Style = (Style)FindResource("SubTab"),
                Tag = _profileSection == section ? "active" : null,
            };

            var chosen = section;
            pill.Click += (_, _) =>
            {
                if (_profileSection == chosen) return;
                _profileSection = chosen;
                RenderProfileTab();
            };

            row.Children.Add(pill);
        }

        return row;
    }

    /// <summary>
    /// The viewer's own decks: a picker, the deck as card art, and one card's detail.
    ///
    /// <para>The full-size twin of the mod window's DECKS section, and it can be — this page is
    /// the whole tab wide. The chat column that the comment here used to blame for the cramped
    /// version only exists on the ROOMS subtab.</para>
    ///
    /// <para>It says outright that these are the cards the player BRINGS. A deck holds 25 and a
    /// match may use five, so letting it read as "cards played" would overstate it by a factor
    /// nothing on screen could reveal — and nobody else's deck can be read at all, since the
    /// file sits on their machine and a recording carries only its name.</para>
    /// </summary>
    private UIElement BuildProfileDecks()
    {
        var card = BuildProfileCard(Strings.Get("MpStatsDecksTitle"));
        var stack = (StackPanel)card.Child;

        stack.Children.Add(new TextBlock
        {
            Text = Strings.Get("MpStatsDecksHint"),
            Foreground = (Brush)Application.Current.FindResource("MpTextDim"),
            FontSize = (double)Application.Current.FindResource("MpMetaSize"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 9),
        });

        if (!_mpDecksLoaded)
        {
            stack.Children.Add(Note("MpStatsDecksLoading"));
            return card;
        }

        var decks = _mpDeckProfiles
            .SelectMany(p => p.Decks.Select(d => (Profile: p, Deck: d)))
            .ToList();

        if (decks.Count == 0)
        {
            stack.Children.Add(Note("MpStatsDecksEmpty"));
            return card;
        }

        if (_mpSelectedDeck == null || decks.All(d => !ReferenceEquals(d.Deck, _mpSelectedDeck)))
            _mpSelectedDeck = decks[0].Deck;

        var chosen = decks.First(d => ReferenceEquals(d.Deck, _mpSelectedDeck));

        // Hidden with a single deck: a chooser with one choice is furniture.
        if (decks.Count > 1) stack.Children.Add(BuildDeckPicker(decks));

        stack.Children.Add(BuildDeckHeadline(chosen.Profile, chosen.Deck));

        // The grid and the detail PAIR UP when there is room and stack when there is not, which
        // is the whole of the responsiveness here and costs no code: each has a cap, so a wide
        // window puts them side by side and a narrow one drops the detail underneath. The cap on
        // the grid is also what stops it drawing all 25 tiles on one very long line, which reads
        // as a band rather than as a deck.
        var pair = new WrapPanel();
        pair.Children.Add(BuildDeckGrid(chosen.Deck));
        pair.Children.Add(BuildDeckDetailPanel());
        stack.Children.Add(pair);

        var first = chosen.Deck.Cards.FirstOrDefault();
        if (first != null) ShowMpCardDetail(first);

        return card;
    }

    private TextBlock Note(string key) => new()
    {
        Text = Strings.Get(key),
        Foreground = (Brush)Application.Current.FindResource("MpTextDim"),
        FontSize = (double)Application.Current.FindResource("MpMetaSize"),
        TextWrapping = TextWrapping.Wrap,
    };

    private UIElement BuildDeckPicker(
        IReadOnlyList<(Models.HomeCityProfile Profile, Models.HomeCityDeckEntry Deck)> decks)
    {
        // A WrapPanel, so it works the same with two decks and with twenty: the pills fall onto
        // a second line instead of the row growing past the page.
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };

        foreach (var (profile, deck) in decks)
        {
            var pill = new Button
            {
                Content = DeckLabel(profile, deck),
                Style = (Style)FindResource("SubTab"),
                Tag = ReferenceEquals(deck, _mpSelectedDeck) ? "active" : null,
                Margin = new Thickness(0, 0, 4, 4),
            };

            var chosen = deck;
            pill.Click += (_, _) =>
            {
                if (ReferenceEquals(chosen, _mpSelectedDeck)) return;
                _mpSelectedDeck = chosen;
                RenderProfileTab();
            };

            row.Children.Add(pill);
        }

        return row;
    }

    private string DeckLabel(Models.HomeCityProfile profile, Models.HomeCityDeckEntry deck)
    {
        // The internal civ name is frequently not the one the player saw — Struggle of Indonesia
        // files its Solo home city under "Ottomans" and shows "Surakarta".
        var civ = !string.IsNullOrWhiteSpace(profile.Civ)
                  && _mpDeckCivNames.TryGetValue(profile.Civ, out var display)
            ? display
            : string.IsNullOrWhiteSpace(profile.Civ) ? profile.CityName : profile.Civ;

        return string.IsNullOrWhiteSpace(deck.Name) ? civ : civ + "  ·  " + deck.Name;
    }

    private UIElement BuildDeckHeadline(
        Models.HomeCityProfile profile, Models.HomeCityDeckEntry deck)
    {
        var facts = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.CityName) && !string.IsNullOrWhiteSpace(profile.Civ))
            facts.Add(profile.CityName);
        facts.Add(Strings.Format("ModPropDecksCardCount", deck.Cards.Count));
        if (profile.Level > 0) facts.Add(Strings.Format("ModPropDecksLevel", profile.Level));

        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(new TextBlock
        {
            Text = DeckLabel(profile, deck),
            Foreground = (Brush)Application.Current.FindResource("MpTextHeading"),
            FontSize = (double)Application.Current.FindResource("MpLabelSize"),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        stack.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", facts),
            Foreground = (Brush)Application.Current.FindResource("MpTextDim"),
            FontSize = (double)Application.Current.FindResource("MpMetaSize"),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        return stack;
    }

    private UIElement BuildDeckGrid(Models.HomeCityDeckEntry deck)
    {
        var grid = new WrapPanel { MaxWidth = 620, Margin = new Thickness(0, 0, 16, 0) };
        _mpSelectedTile = null;

        var tiles = Controls.DeckTiles.Build(
            deck, _mpCardDetails, _mpCardIcons, MpDeckTileSize, "MpRimFaint");

        for (var i = 0; i < tiles.Count; i++)
        {
            var card = deck.Cards[i];
            var tile = tiles[i];

            // Selection only, like the mod window: hovering used to swap the panel as the
            // pointer crossed the grid, which flicked the description past on the way.
            tile.Click += (_, _) =>
            {
                _mpSelectedTile = Controls.DeckTiles.Select(tile, _mpSelectedTile, "MpRimFaint");
                ShowMpCardDetail(card);
            };

            if (i == 0)
                _mpSelectedTile = Controls.DeckTiles.Select(tile, null, "MpRimFaint");

            grid.Children.Add(tile);
        }

        return grid;
    }

    /// <summary>Smaller than the mod window's: this page also carries the ladder and the history.</summary>
    private const int MpDeckTileSize = 40;

    private UIElement BuildDeckDetailPanel()
    {
        _mpDetailIcon = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_mpDetailIcon, BitmapScalingMode.HighQuality);

        _mpDetailName = new TextBlock
        {
            Foreground = (Brush)Application.Current.FindResource("MpTextHeading"),
            FontSize = (double)Application.Current.FindResource("MpBodySize"),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        _mpDetailText = new TextBlock
        {
            Foreground = (Brush)Application.Current.FindResource("MpTextBody"),
            FontSize = (double)Application.Current.FindResource("MpMetaSize"),
            Margin = new Thickness(0, 4, 0, 0),
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
        };

        _mpDetailEffects = new StackPanel
        {
            Margin = new Thickness(0, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var body = new StackPanel { Margin = new Thickness(11, 0, 0, 0) };
        body.Children.Add(_mpDetailName);
        body.Children.Add(_mpDetailText);
        body.Children.Add(_mpDetailEffects);

        var dock = new DockPanel();
        var iconHost = new Border
        {
            Width = 56,
            Height = 56,
            VerticalAlignment = VerticalAlignment.Top,
            Background = (Brush)Application.Current.FindResource("MpField"),
            CornerRadius = (CornerRadius)Application.Current.FindResource("RadiusSm"),
            Child = _mpDetailIcon,
        };
        DockPanel.SetDock(iconHost, Dock.Left);
        dock.Children.Add(iconHost);
        dock.Children.Add(body);

        // A floor, so moving between cards does not make the page jump.
        _mpDetailCard = new Border
        {
            Child = dock,
            MinHeight = 84,
            MinWidth = 360,
            MaxWidth = 560,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(12, 10, 12, 11),
            CornerRadius = (CornerRadius)Application.Current.FindResource("RadiusSm"),
            Background = (Brush)Application.Current.FindResource("MpField"),
            BorderBrush = (Brush)Application.Current.FindResource("MpRimFaint"),
            BorderThickness = new Thickness(1),
        };

        return _mpDetailCard;
    }

    private void ShowMpCardDetail(Models.HomeCityCard card)
    {
        if (_mpDetailName == null || _mpDetailText == null || _mpDetailEffects == null) return;

        _mpCardDetails.TryGetValue(card.InternalName, out var detail);
        _mpDetailName.Text = detail?.Name ?? card.InternalName;

        if (_mpDetailIcon != null)
        {
            _mpDetailIcon.Source =
                detail?.IconPath != null && _mpCardIcons.TryGetValue(detail.IconPath, out var icon)
                    ? icon
                    : null;
        }

        // The modder's own sentence when there is one. 20 of a real deck's 35 cards carry none,
        // so the line is dropped rather than filled with a placeholder — and those are exactly
        // the cards the effects below describe instead.
        var description = detail?.Description ?? "";
        _mpDetailText.Text = description;
        _mpDetailText.Visibility =
            description.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        _mpDetailEffects.Children.Clear();
        if (!_mpCardEffects.TryGetValue(card.InternalName, out var lines) || lines.Count == 0)
        {
            _mpDetailEffects.Visibility = Visibility.Collapsed;
            return;
        }

        _mpDetailEffects.Visibility = Visibility.Visible;
        var size = (double)Application.Current.FindResource("MpMetaSize");
        var brush = (Brush)Application.Current.FindResource("MpTextDim");

        foreach (var line in lines)
        {
            _mpDetailEffects.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = brush,
                FontSize = size,
                MaxWidth = 560,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 2),
            });
        }
    }

    /// <summary>
    /// Reads the decks and everything needed to describe them, in ONE background pass: the home
    /// city files, 12 MB of tech files for the names and what each card does, and the art
    /// archives for the pictures.
    ///
    /// <para>Nothing is drawn until it returns — a grid that appeared as empty squares and
    /// filled in later would look broken rather than busy — so the section shows a line saying
    /// it is reading, and the page repaints when this lands.</para>
    /// </summary>
    private async Task LoadMpDecksAsync()
    {
        var profile = _getActiveProfile?.Invoke();
        if (profile == null || _config == null) return;

        try
        {
            var folderName = Services.UserDataService.ResolveFolderName(profile, _config);
            var folder = string.IsNullOrWhiteSpace(folderName)
                ? "" : Services.UserDataService.GetUserDataFolder(folderName);
            if (string.IsNullOrWhiteSpace(folder)) return;

            var installPath = _config.GetState(profile.Id).InstallPath ?? "";
            var exe = profile.GameExecutable;

            var (read, details, icons, effects, civs) = await Task.Run(() =>
            {
                var decks = Services.HomeCityDeckService.Read(folder).ToList();

                var names = decks.SelectMany(p => p.Decks).SelectMany(d => d.Cards)
                    .Select(c => c.InternalName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var resolved = Services.CardNameResolver.ResolveDetails(installPath, exe, names);
                var art = Services.CardArtService.Load(
                    installPath, resolved.Values.Select(d => d.IconPath));
                var lines = Services.CardEffectRenderer.RenderAll(installPath, exe, resolved);

                var civNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var civ in decks.Select(p => p.Civ)
                             .Where(c => !string.IsNullOrWhiteSpace(c))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var display = Services.Multiplayer.CivNameResolver
                        .ResolveByInternalName(installPath, civ);
                    if (!string.IsNullOrWhiteSpace(display)) civNames[civ!] = display!;
                }

                return (decks, resolved, art, lines, civNames);
            });

            _mpDeckProfiles.Clear();
            _mpDeckProfiles.AddRange(read);
            _mpCardDetails = details;
            _mpCardIcons = icons;
            _mpCardEffects = effects;
            _mpDeckCivNames.Clear();
            foreach (var pair in civs) _mpDeckCivNames[pair.Key] = pair.Value;
        }
        catch (Exception ex)
        {
            // A mod whose files cannot be read still gets its decks, under the internal names
            // and without pictures — which identify the card to anyone who mods.
            DiagnosticLog.Write("Stats: own decks unavailable - " + ex.Message);
        }
        finally
        {
            _mpDecksLoaded = true;
            if (_profileWindow != null && _profileSection == ProfileSection.Decks)
                RenderProfileTab();
        }
    }

    /// <summary>
    /// The card under the ladder: which civilizations the community picks.
    ///
    /// <para>It had a twin listing the most-played maps. That went to Estadisticas, where the
    /// maps are a full table with proportional bars and a grouped tail rather than five names
    /// and five numbers — and where they can say which MOD they belong to, which this strip
    /// never could: the ladder above it mixes every mod a player plays, because a rating is
    /// per player and not per mod.</para>
    /// </summary>
    /// <summary>The same call, reachable from <c>DialogXamlTests</c>: this card is built in
    /// code and nothing else checks that it hides itself when it has nothing to say.</summary>
    internal void RenderRankingSummaryCardsForTest() => RenderRankingSummaryCards();

    private void RenderRankingSummaryCards()
    {
        RankingCivsCardTitle.Text = Strings.Get("MpCivsTitle");

        RankingCivsCardList.Children.Clear();
        var civs = _civStats?.Civs ?? new List<Models.Multiplayer.CivStatEntry>();
        foreach (var c in civs.Take(SummaryRows))
        {
            RankingCivsCardList.Children.Add(BuildCountRow(c.Civ, c.Played));
        }

        // One card, so there is no column to give back and no gap to collapse — the pair of
        // star columns and the 11px spacer between them went with the maps. What is left is
        // the plain rule: a card with nothing in it is not drawn.
        RankingCivsCard.Visibility = civs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>How many rows a summary card shows before it stops being a summary.</summary>
    private const int SummaryRows = 5;

    /// <summary>
    /// The civilization table with a sample behind it: matches, the record, a two-colour bar
    /// and a percentage.
    ///
    /// <para>Built from <c>CivTableLayout</c> like the matchup table under it, so the header
    /// and the rows cannot drift apart and the two tables stay aligned with each other. The
    /// bar is the column this redesign added.</para>
    ///
    /// <para>Three rules the repo already fixed once are honoured and stated under the table,
    /// because a reader who cannot see them thinks it is broken: the percentage is over
    /// DECIDED matches, it is only published from <c>MinDecidedForPercent</c> of them, and the
    /// order is by matches PLAYED. Ordering by percentage would put whoever won their only
    /// game at the top and call it the best civilization in the mod.</para>
    /// </summary>
    private UIElement BuildCivTableCard(
        List<(string Civ, string Label, int Played, int Wins, int Losses)> rows)
    {
        var stack = new StackPanel();
        stack.Children.Add(StatsSectionLabel(
            Strings.Get("MpCivsTitle"), Strings.Get("MpStatsOrderNote")));

        var body = new StackPanel();
        body.Children.Add(BuildCivHeader());

        // Ordered by matches played, and the tail — the ones without enough decided matches to
        // say anything about — grouped rather than listed. With hundreds of civilizations in
        // the mod that tail is the ordinary case for months, not an edge case.
        var ordered = rows
            .OrderByDescending(r => r.Played)
            .ThenBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        int minimum = Services.Multiplayer.CivStatsView.MinDecidedForPercent;
        var shown = ordered.Where(r => r.Wins + r.Losses >= minimum).ToList();

        // If nothing clears the bar, show the most-played handful anyway: an empty table under
        // a heading that says there IS data reads as a fault rather than as a shortage.
        if (shown.Count == 0) shown = ordered.Take(6).ToList();
        var tail = ordered.Where(r => !shown.Contains(r)).ToList();

        foreach (var row in shown)
        {
            body.Children.Add(BuildCivRow(
                row.Label, row.Played, row.Wins, row.Losses, AvgSecondsFor(row.Civ),
                BuildCivFlag(StatsVocabulary(), row.Civ, 16)));
        }

        if (tail.Count > 0) body.Children.Add(BuildCivTailRow(tail, minimum));

        stack.Children.Add(StatsCard(body));
        stack.Children.Add(StatsFootnote(Strings.Format("MpStatsCivsRules", minimum)));
        return stack;
    }

    /// <summary>The server's mean match length for a civilization, when it sent one. The
    /// viewer's own aggregate has no equivalent, so that column is simply blank there.</summary>
    private int? AvgSecondsFor(string civ)
        => _civStats?.Civs?.FirstOrDefault(
            c => string.Equals(c.Civ, civ, StringComparison.Ordinal))?.AvgSeconds;

    /// <summary>Wins over decided, as green on red. Empty and grey when there is no sample.</summary>
    private static Border BuildWinBar(int wins, int decided, bool enough)
    {
        var track = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 11, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        track.SetResourceReference(Border.BackgroundProperty,
            enough ? "MpBarLoss" : "MpBarTrackEmpty");

        if (!enough || decided <= 0) return track;

        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0, wins), GridUnitType.Star),
        });
        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0, decided - wins), GridUnitType.Star),
        });

        var win = new Border { Height = 6, CornerRadius = new CornerRadius(3) };
        win.SetResourceReference(Border.BackgroundProperty, "MpOk");
        Grid.SetColumn(win, 0);
        host.Children.Add(win);

        track.Child = host;
        return track;
    }

    private UIElement BuildCivTailRow(
        List<(string Civ, string Label, int Played, int Wins, int Losses)> tail, int minimum)
    {
        var grid = new Grid { Margin = new Thickness(13, 9, 13, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var words = new StackPanel();
        var head = new TextBlock
        {
            Text = Strings.Format("MpStatsTailCivs", tail.Count, minimum),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        head.SetResourceReference(TextBlock.FontSizeProperty, "MpLabelSize");
        head.SetResourceReference(TextBlock.ForegroundProperty, "MpTextSecondary");
        words.Children.Add(head);

        var why = new TextBlock
        {
            Text = Strings.Get("MpStatsTailCivsWhy"),
            Margin = new Thickness(0, 3, 8, 0),
        };
        why.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
        why.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
        words.Children.Add(why);

        Grid.SetColumn(words, 0);
        grid.Children.Add(words);

        var total = new TextBlock
        {
            Text = tail.Sum(r => r.Played).ToString("N0"),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        };
        total.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        total.SetResourceReference(TextBlock.FontSizeProperty, "MpFigureSize");
        total.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
        Grid.SetColumn(total, 1);
        grid.Children.Add(total);

        var row = new Border { Child = grid };
        row.SetResourceReference(Border.BackgroundProperty, "MpPanelDim");
        return row;
    }

    /// <summary>
    /// The civilization table's empty state, as a card of its own.
    ///
    /// <para>It replaces a full-width box holding one two-hundred-character sentence. The
    /// vacancy is half a feature when it explains itself - civilizations are only reported
    /// from the build that started recording them and nothing can fill them in backwards -
    /// but it does not need the whole page to say so.</para>
    /// </summary>
    private UIElement BuildCivStatusCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(StatsSectionLabel(Strings.Get("MpCivsTitle")));

        var body = new StackPanel { Margin = new Thickness(13, 12, 13, 13) };

        var title = new TextBlock
        {
            Text = Strings.Get("MpStatsCivsNoData"),
            FontWeight = FontWeights.SemiBold,
        };
        title.SetResourceReference(TextBlock.FontSizeProperty, "MpBodySize");
        title.SetResourceReference(TextBlock.ForegroundProperty, "MpTextHeading");
        body.Children.Add(title);

        // The real count, which is the one fact somebody wants and the old sentence buried.
        var count = new TextBlock
        {
            // Both figures from /stats/civs, on the same terms: rated, 1v1, this mod, all
            // time. It used to pair this numerator with a THIRTY-DAY total from another
            // endpoint - "0 of 42" was two different questions in one sentence.
            Text = Strings.Format("MpStatsCivsCount",
                _civStats?.RatedMatchesWithCiv ?? 0,
                _civStats?.RatedMatches ?? _civStats?.RatedMatchesWithCiv ?? 0),
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        count.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        count.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
        count.SetResourceReference(TextBlock.ForegroundProperty, "MpTextMuted");
        body.Children.Add(count);

        var why = new TextBlock
        {
            Text = Strings.Get("MpStatsCivsWhy"),
            Margin = new Thickness(0, 9, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        why.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
        why.SetResourceReference(TextBlock.ForegroundProperty, "MpTextMuted");
        body.Children.Add(why);

        var inner = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 10, 0, 0),
        };
        inner.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        inner.SetResourceReference(Border.BackgroundProperty, "MpCautionBg");
        var many = new TextBlock
        {
            Text = Strings.Get("MpStatsCivsMany"),
            TextWrapping = TextWrapping.Wrap,
        };
        many.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
        many.SetResourceReference(TextBlock.ForegroundProperty, "MpCautionText");
        inner.Child = many;
        body.Children.Add(inner);

        stack.Children.Add(StatsCard(body));
        return stack;
    }

    /// <summary>
    /// Ask for this mod's card and civilization names, once.
    ///
    /// <para>Fire-and-forget on purpose: the table is already on screen with identifiers, and
    /// the names replace them on the repaint. The first call for a mod streams its tech trees,
    /// which is why it is never awaited from a render.</para>
    /// </summary>
    private async Task EnsureDeckNamesAsync(
        IReadOnlyList<Models.Multiplayer.DeckCardEntry> rows)
    {
        string mod = StatsModId();
        if (Services.Multiplayer.DeckCardNames.Peek(mod) != null) return;

        var resolved = await Services.Multiplayer.DeckCardNames.ResolveAsync(
            mod,
            GetInstallPath,
            rows.Select(r => r.Card),
            StatsPageCivs(rows));

        // Only repaint when something was actually learned, and only if the page is still the
        // one that asked: a repaint that changes nothing is a flicker for no reason.
        if (resolved.Resolved && _activeSubtab == Subtab.Stats) RenderStatsTab();
    }

    /// <summary>
    /// Every civilization named ANYWHERE on the statistics page.
    ///
    /// <para>The three tables carry different sets — the balance and the matchups list what was
    /// PLAYED, the card table lists who has shared a deck — so resolving only the deck rows'
    /// left the other two with unresolved names and no flag. One pass over <c>civs.xml</c>
    /// covering all of them costs the same as one covering a third of them.</para>
    /// </summary>
    private IEnumerable<string> StatsPageCivs(
        IReadOnlyList<Models.Multiplayer.DeckCardEntry>? rows)
    {
        var all = new List<string>();
        if (rows != null) all.AddRange(rows.Select(r => r.Civ ?? ""));

        foreach (var c in _civStats?.Civs ?? new List<Models.Multiplayer.CivStatEntry>())
            all.Add(c.Civ ?? "");

        foreach (var m in _matchups?.Matchups ?? new List<Models.Multiplayer.MatchupEntry>())
        {
            all.Add(m.CivA ?? "");
            all.Add(m.CivB ?? "");
        }

        return all.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// This mod's vocabulary if it has already been read, for the surfaces that only want a
    /// name or a flag. Null until the background pass has run; every caller treats that as
    /// "not yet" and draws what the server sent.
    /// </summary>
    private Services.Multiplayer.DeckCardNames.Vocabulary? StatsVocabulary()
        => Services.Multiplayer.DeckCardNames.Peek(StatsModId());

    /// <summary>
    /// A civilization as the player saw it.
    ///
    /// <para><b>Not the server's string.</b> That is the mod's INTERNAL name, and for a
    /// reskinned civilization it names one nobody played: the block Struggle of Indonesia calls
    /// <c>Ottomans</c> is Surakarta on screen. Falls back to the raw value only when the mod
    /// cannot be read at all, which is the existing state for the two mods that keep
    /// <c>civs.xml</c> packed.</para>
    /// </summary>
    private string StatsCivLabel(string? civ)
        => StatsVocabulary() is { } v && !string.IsNullOrWhiteSpace(civ)
            ? v.CivOf(civ)
            : civ ?? "";

    /// <summary>
    /// Why the deck table is empty, and what would fill it.
    ///
    /// <para>It is opt-in, so "nobody has shared one" is a state and not a failure. The one
    /// thing this must not do is imply the launcher knows what people PLAY: no recording
    /// carries the card that was played, only the deck a player brought.</para>
    /// </summary>
    private static UIElement BuildDecksEmptyState()
    {
        var body = new StackPanel { Margin = new Thickness(13, 12, 13, 13) };

        var title = new TextBlock
        {
            Text = Strings.Get("MpStatsDecksEmptyTitle"),
            FontWeight = FontWeights.SemiBold,
        };
        title.SetResourceReference(TextBlock.FontSizeProperty, "MpBodySize");
        title.SetResourceReference(TextBlock.ForegroundProperty, "MpTextHeading");
        body.Children.Add(title);

        foreach (var key in new[] { "MpStatsDecksEmptyBody", "MpStatsDecksEmptyAction" })
        {
            var line = new TextBlock
            {
                Text = Strings.Get(key),
                Margin = new Thickness(0, 7, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            line.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
            line.SetResourceReference(TextBlock.ForegroundProperty, "MpTextMuted");
            body.Children.Add(line);
        }

        return body;
    }

    /// <summary>The four rules, as four short lines. They replace the same sentences scattered
    /// around the page as paragraphs nobody finishes.</summary>
    private static UIElement BuildHowMeasuredCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(StatsSectionLabel(Strings.Get("MpStatsHowMeasured")));

        var body = new StackPanel { Margin = new Thickness(13, 11, 13, 12) };
        foreach (var key in new[]
        {
            "MpStatsHowRated", "MpStatsHowDecided", "MpStatsHowNoSample", "MpStatsHowOld",
        })
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8),
            };
            var dot = new Border
            {
                Width = 4, Height = 4,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 6, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            dot.SetResourceReference(Border.BackgroundProperty, "MpOk");
            row.Children.Add(dot);

            var text = new TextBlock
            {
                Text = Strings.Get(key),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 250,
            };
            text.SetResourceReference(TextBlock.FontSizeProperty, "MpPillSize");
            text.SetResourceReference(TextBlock.ForegroundProperty, "MpTextMuted");
            row.Children.Add(text);

            body.Children.Add(row);
        }

        stack.Children.Add(StatsCard(body));
        return stack;
    }

    /// <summary>
    /// The columns become <c>ColumnDefinition</c>s in ONE place, like the ladder's — header and
    /// rows drifting apart misaligns every row, and no compile step can see it.
    /// </summary>
    private static Grid BuildCivGrid(
        IReadOnlyList<Services.Multiplayer.CivColumnSpec>? columns = null)
    {
        var grid = new Grid();
        var specs = columns ?? Services.Multiplayer.CivTableLayout.All;
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var trailing = i == specs.Count - 1 ? 0 : Services.Multiplayer.CivTableLayout.ColumnGap;
            var column = new ColumnDefinition
            {
                Width = spec.FixedWidth.HasValue
                    ? new GridLength(spec.FixedWidth.Value + trailing)
                    : new GridLength(1, GridUnitType.Star),
            };
            if (spec.MaxWidth.HasValue) column.MaxWidth = spec.MaxWidth.Value + trailing;
            grid.ColumnDefinitions.Add(column);
        }
        return grid;
    }

    /// <param name="count">How many columns the table has. Passed rather than read off
    /// <c>CivTableLayout.All</c> because the matchup table below has one fewer, and a hard-coded
    /// count would give its LAST column a trailing gap — twelve pixels of drift against the civ
    /// table it is supposed to line up with.</param>
    private static double CivTrailingGap(int index, int count)
        => index == count - 1 ? 0 : Services.Multiplayer.CivTableLayout.ColumnGap;

    /// <summary>
    /// The matchup table: one row per pair of civilizations that has actually met.
    ///
    /// <para>Hidden whole — title, hint and card — when the list is null OR empty. Null means the
    /// server has no such route yet, empty means nobody has played a rated 1v1 with both
    /// civilizations resolved. Neither is worth a heading over an empty box, and the first is not
    /// even this launcher's business to announce.</para>
    /// </summary>
    /// <summary>
    /// Civilization against civilization, under the civilization table it belongs beside.
    ///
    /// <para>It painted into its own hosts at the foot of the page for one build, below both
    /// columns, which left the widest column on the screen ending halfway down with nothing
    /// under it. It is the same shape and the same width as the table above it and it answers
    /// the next question that table raises, so it goes there.</para>
    /// </summary>
    private UIElement BuildMatchupCard()
        => BuildPairCard(
            _matchups?.Matchups,
            StatsTeamMode() ? "MpStatsRivalsTitle" : "MpStatsMatchupsTitle",
            StatsTeamMode() ? "MpStatsRivalsHint" : "MpStatsMatchupsHint");

    /// <summary>
    /// Who a civilization is played WITH, which only exists in a team game.
    ///
    /// <para>The same table as the one above it, deliberately: "played with" and "played
    /// against" are only comparable if they are counted and drawn the same way, and they are
    /// read one after the other. Empty in 1v1, where nobody has an ally, and the card is then
    /// absent rather than empty.</para>
    /// </summary>
    private UIElement BuildAlliesCard()
        => BuildPairCard(_matchups?.Allies, "MpStatsAlliesTitle", "MpStatsAlliesHint",
                         "MpStatsAlliesColPair");

    /// <summary>One table of civilization pairs: rivals or allies, told apart by their words.</summary>
    private UIElement BuildPairCard(
        List<Models.Multiplayer.MatchupEntry>? rows, string titleKey, string hintKey,
        string? pairColumnKey = null)
    {
        if (rows == null || rows.Count == 0) return new StackPanel();

        var stack = new StackPanel();
        stack.Children.Add(StatsSectionLabel(Strings.Get(titleKey)));

        var body = new StackPanel();
        body.Children.Add(BuildMatchupHeader(pairColumnKey));
        foreach (var row in rows)
        {
            var v = StatsVocabulary();
            body.Children.Add(BuildMatchupRow(
                row,
                StatsCivLabel(row.CivA),
                StatsCivLabel(row.CivB),
                BuildCivFlag(v, row.CivA, 16),
                BuildCivFlag(v, row.CivB, 16)));
        }

        stack.Children.Add(StatsCard(body));
        stack.Children.Add(StatsFootnote(Strings.Get(hintKey)));
        return stack;
    }

    /// <summary>
    /// How the team games split between 2v2 and 3v3.
    ///
    /// <para>Derived by the server from the number of participants, because nothing stores a
    /// format. A 4v4 cannot appear: the server refuses to rate an eight-player match as a team
    /// game at all, so it never carries the mode this page is scoped to.</para>
    /// </summary>
    private UIElement BuildTeamFormatsCard()
    {
        var formats = _statsCommunity?.Totals?.TeamFormats;
        if (!StatsTeamMode() || formats == null || formats.Count == 0) return new StackPanel();

        var stack = new StackPanel();
        stack.Children.Add(StatsSectionLabel(Strings.Get("MpStatsFormatsTitle")));

        var body = new StackPanel { Margin = new Thickness(13, 12, 13, 13) };
        var figures = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var format in formats)
        {
            if (format.Matches <= 0) continue;
            StatsFigure(figures, format.Matches.ToString("N0"), FormatLabel(format.Players),
                        "MpTextPrimary");
        }
        body.Children.Add(figures);

        stack.Children.Add(StatsCard(body));
        return stack;
    }

    /// <summary>"2v2" from four participants. Named from the count and never from a stored
    /// format, because there is no stored format; an odd count says so rather than halving it
    /// into a lie.</summary>
    private static string FormatLabel(int players)
        => players >= 2 && players % 2 == 0
            ? $"{players / 2}v{players / 2}"
            : Strings.Format("MpStatsFormatPlayers", players);

    /// <summary>
    /// The community card table: which cards people BRING, most-carried first.
    ///
    /// <para>The hint says "bring" and it has to. A deck holds 25 cards and a match may use
    /// five, and no recording carries the card that was played — the engine plays one by deck
    /// slot and never transmits an identifier. Reading this as "most-played cards" overstates
    /// it by a factor nothing on screen could reveal.</para>
    ///
    /// <para>Hidden whole when the list is null (no such route yet) or empty (nobody has
    /// opted in). Neither is worth a heading over an empty box.</para>
    /// </summary>
    private UIElement BuildDeckCard()
    {
        var rows = _deckStats?.Cards;

        // Nothing arrived at all: a backend without the route answers 404, which is "not
        // deployed" and not "nobody has shared one". Those are different sentences and only
        // the second one is worth printing.
        if (_deckStats == null) return new StackPanel();

        var stack = new StackPanel();

        if (rows == null || rows.Count == 0)
        {
            // The empty state keeps the bare label: there is no census to put beside it.
            stack.Children.Add(StatsSectionLabel(Strings.Get("MpStatsCommunityDecksTitle")));
            // The card STAYS, with an explanation. This table is opt-in on the launcher side
            // and the server has no way to know that, so an empty one is the ordinary state
            // rather than a fault - and hiding it entirely is how a whole feature came to
            // look like it did not exist. Same treatment the civilization vacancy gets.
            stack.Children.Add(StatsCard(BuildDecksEmptyState()));
            return stack;
        }

        // What is already known for this mod, without waiting. Null means it has not been
        // read yet: the rows draw identifiers now and the names arrive on the repaint the
        // request below triggers.
        var names = Services.Multiplayer.DeckCardNames.Peek(StatsModId());
        _ = EnsureDeckNamesAsync(rows);

        var vocabulary = names ?? Services.Multiplayer.DeckCardNames.Vocabulary.None;
        var groups = Services.Multiplayer.DeckStatsView.Group(
            rows, vocabulary.NameOf, vocabulary.CivOf, _deckTailsOpen);

        // Opened ONCE, not on every repaint: the page is rebuilt whole whenever a payload
        // lands, and re-deciding this each time would slam shut a group the player had just
        // opened. The biggest one, per the handoff - never all four.
        if (!_deckCivsSeeded && groups.Count > 0)
        {
            _deckCivsSeeded = true;
            _deckCivsOpen.Add(groups[0].Civ);
        }

        int raw = groups
            .SelectMany(g => g.Shown.Concat(g.Tail))
            .Count(r => names == null || string.Equals(r.Label, r.Card, StringComparison.Ordinal));

        var drawnGroups = _deckCivsExpanded
            ? groups
            : groups.Take(Services.Multiplayer.DeckStatsView.CivGroupsShown).ToList();

        int distinct = groups.Sum(g => g.DistinctCards);
        stack.Children.Add(StatsSectionLabel(
            Strings.Get("MpStatsCommunityDecksTitle"),
            Strings.Format("MpStatsDecksCardCount", distinct)));

        var body = new StackPanel();
        for (int i = 0; i < drawnGroups.Count; i++)
            body.Children.Add(BuildDeckCivGroup(drawnGroups[i], vocabulary, i == drawnGroups.Count - 1));

        if (drawnGroups.Count < groups.Count)
            body.Children.Add(BuildDeckMoreCivsRow(groups.Count - drawnGroups.Count));

        stack.Children.Add(StatsCard(body));

        // Why the table folds at all, said once under it rather than per group.
        if (groups.Any(g => g.Tail.Count > 0))
            stack.Children.Add(StatsFootnote(Strings.Get("MpStatsTailDecksWhy")));

        // Said once, under the table, whenever ANY row is still an identifier - not only when
        // every one of them is. The mixed case is the common one and it was the one with no
        // explanation at all: a mod resolves its own cards and leaves another mod's alone, so
        // half the table read as names and half as identifiers with nothing saying why.
        //
        // Two different sentences because they are two different facts. The alternative to both
        // was hiding the table, which trades an honest limit for a missing feature.
        if (raw > 0)
        {
            stack.Children.Add(StatsFootnote(Strings.Get(
                names == null || !names.Resolved
                    ? "MpStatsDecksNotResolved"
                    : "MpStatsDecksPartlyResolved")));
        }

        // The contributor count is part of the honesty, not decoration: this is opt-in, so a
        // table built from three people must say it was built from three people.
        stack.Children.Add(StatsFootnote(
            Strings.Format("MpStatsCommunityDecksHint", _deckStats!.Contributors)));
        return stack;
    }

    /// <summary>
    /// Which civilization groups are open, and which have had their tail expanded.
    ///
    /// <para>On the tab, not in the view, because <see cref="RenderStatsTab"/> rebuilds this
    /// whole page every time a payload lands — it only restores the scroll offset. State held
    /// anywhere below that would be thrown away under the player's hands.</para>
    /// </summary>
    private readonly HashSet<string> _deckCivsOpen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deckTailsOpen = new(StringComparer.Ordinal);

    /// <summary>Cards showing what they do, keyed by civilization and card.</summary>
    private readonly HashSet<string> _deckCardsOpen = new(StringComparer.Ordinal);

    /// <summary>Whether the default-open group has been chosen. Once per session, so a
    /// repaint cannot re-close what the player opened.</summary>
    private bool _deckCivsSeeded;

    /// <summary>Whether the player asked to see every civilization rather than the first
    /// <see cref="Services.Multiplayer.DeckStatsView.CivGroupsShown"/>.</summary>
    private bool _deckCivsExpanded;

    /// <summary>
    /// One civilization: a header that folds, and its rows when open.
    ///
    /// <para>The civilization used to be a COLUMN, repeating the same value twelve and twenty
    /// times down the table — half the width spent on a constant. As a header it is said once
    /// and it earns its keep, because the denominator behind every percentage in the group is
    /// this civilization's deck count and nothing else.</para>
    /// </summary>
    private FrameworkElement BuildDeckCivGroup(
        Services.Multiplayer.DeckCivGroup group,
        Services.Multiplayer.DeckCardNames.Vocabulary vocabulary,
        bool isLast)
    {
        bool open = _deckCivsOpen.Contains(group.Civ);

        var stack = new StackPanel();
        stack.Children.Add(BuildDeckCivHeader(group, open, vocabulary));

        if (open)
        {
            foreach (var row in group.Shown)
            {
                // Keyed by civilization AND card: the same card appears under several
                // civilizations, and opening one must not open the others.
                var key = group.Civ + "\u0000" + row.Card;
                stack.Children.Add(BuildDeckCardRow(
                    row,
                    vocabulary,
                    _deckCardsOpen.Contains(key),
                    () =>
                    {
                        if (!_deckCardsOpen.Add(key)) _deckCardsOpen.Remove(key);
                        RenderStatsTab();
                    }));
            }

            if (group.Tail.Count > 0)
                stack.Children.Add(BuildDeckTailRow(group));
        }

        var host = new Border
        {
            Child = stack,
            BorderThickness = new Thickness(0, 0, 0, isLast ? 0 : 1),
        };
        host.SetResourceReference(Border.BorderBrushProperty, "MpRimHair");
        return host;
    }

    /// <summary>
    /// A civilization's flag, at the size the caller asks for, or null when the mod ships none.
    ///
    /// <para>The picture comes from the MOD's own art, which is what makes a reskin come out
    /// right: Struggle of Indonesia's block is still named <c>Ottomans</c> internally and ships
    /// Surakarta's flag, and Surakarta is the civilization the player saw.</para>
    ///
    /// <para>Absent is ordinary, not a fault. One real Wars of Liberty portrait path names a
    /// file that does not exist, and two of the five catalogued mods keep <c>civs.xml</c> inside
    /// <c>Data.bar</c> — those resolve no NAME either, so a missing flag is the same state and
    /// not a new one. Callers reserve the space either way so names do not shift between rows
    /// that have one and rows that do not.</para>
    /// </summary>
    private static FrameworkElement? BuildCivFlag(
        Services.Multiplayer.DeckCardNames.Vocabulary? names, string? civ, double size)
    {
        var flag = names?.CivIconOf(civ);
        if (flag == null) return null;

        var image = new Image
        {
            Source = flag,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 128x128 textures coming down to under twenty pixels. Without this they alias into
        // mush, which is the same reason the deck tiles set it.
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    /// <summary>The fold header: the civilization, and how many distinctive cards it has.</summary>
    private FrameworkElement BuildDeckCivHeader(
        Services.Multiplayer.DeckCivGroup group,
        bool open,
        Services.Multiplayer.DeckCardNames.Vocabulary? names = null)
    {
        var grid = new Grid { Margin = new Thickness(14, 9, 14, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        // The flag's column, reserved whether or not there is one to put in it.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var caret = new TextBlock
        {
            Text = open ? "\u25be" : "\u25b8",
            VerticalAlignment = VerticalAlignment.Center,
        };
        caret.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
        caret.SetResourceReference(TextBlock.ForegroundProperty, "MpTextSecondary");
        grid.Children.Add(WithColumn(caret, 0));

        var flag = BuildCivFlag(names, group.Civ, 18);
        if (flag != null) grid.Children.Add(WithColumn(flag, 1));

        var name = new TextBlock
        {
            Text = group.CivLabel,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 12, 0),
        };
        name.SetResourceReference(TextBlock.FontSizeProperty, "MpBodySize");
        name.SetResourceReference(TextBlock.ForegroundProperty, "MpTextPrimary");
        grid.Children.Add(WithColumn(name, 2));

        var count = new TextBlock
        {
            Text = Strings.Format("MpStatsDecksCivCards", group.DistinctCards),
            VerticalAlignment = VerticalAlignment.Center,
        };
        count.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        count.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
        count.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
        grid.Children.Add(WithColumn(count, 3));

        // The whole header is the hit target, not the caret. A four-pixel triangle is not a
        // button on anybody's screen.
        var button = new Button { Content = grid, Cursor = System.Windows.Input.Cursors.Hand };
        button.SetResourceReference(FrameworkElement.StyleProperty, "MpBareButton");
        button.Click += (_, _) =>
        {
            if (!_deckCivsOpen.Add(group.Civ)) _deckCivsOpen.Remove(group.Civ);
            RenderStatsTab();
        };
        return button;
    }

    /// <summary>One card.</summary>
    /// <remarks><c>internal static</c> so the tests can build the real row — nothing else
    /// constructs it and no compile step checks a resource looked up by name.</remarks>
    /// <param name="open">Whether this row is showing what the card does.</param>
    /// <param name="onToggle">Invoked when the row is clicked. Null makes the row inert, which
    /// is what the tests want and what a row with nothing to say gets anyway.</param>
    internal static FrameworkElement BuildDeckCardRow(
        Services.Multiplayer.DeckCardRow row,
        Services.Multiplayer.DeckCardNames.Vocabulary? names = null,
        bool open = false,
        Action? onToggle = null)
    {
        // Resolved from the mod's OWN tech tree, and the internal name is the fallback rather
        // than the value. For one build a comment claimed the resolution happened while the
        // line under it assigned the identifier untouched, which is how HCXPRefrigeration
        // reached a player - the launcher's oldest rule is that an internal name never does.
        var vocabulary = names ?? Services.Multiplayer.DeckCardNames.Vocabulary.None;

        // The modder's own sentence when it wrote one, then the effects with their numbers.
        // Reading only the sentence showed nothing at all for most of the table: every unit
        // shipment and crate carries no RolloverTextID, so the description has to be BUILT
        // from the card's effects, which is what the deck detail panel already does.
        var lines = vocabulary.DescriptionLinesOf(row.Card);
        bool raw = string.Equals(row.Label, row.Card, StringComparison.Ordinal);
        var icon = vocabulary.IconOf(row.Card);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Margin = new Thickness(14, 0, 14, 0);
        grid.MinHeight = 34;

        var meta = (double)Application.Current.FindResource("MpMetaSize");

        // The card's own art, when the mod is on disk to read it from. Absent is ordinary and
        // costs nothing: the column is reserved either way so the names stay in one line.
        if (icon != null)
        {
            grid.Children.Add(WithColumn(new Image
            {
                Source = icon,
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
            }, 0));
        }

        var nameText = new TextBlock
        {
            Foreground = (Brush)Application.Current.FindResource(
                raw ? "MpTextGhost" : "MpTextPrimary"),
            FontSize = meta,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 12, 0),
        };

        // Painted, not printed. The name arrives carrying the mod's own colour span, and the
        // one thing that must not happen is the span reaching the screen as text.
        Services.GameText.Fill(nameText, vocabulary.NameMarkupOf(row.Card));

        // An unresolved identifier is set in the same monospace every other raw value on this
        // page uses, so it reads as an id rather than as a badly written name.
        //
        // ASSIGNED ONLY WHEN IT APPLIES. A null FontFamily is not "inherit the parent's": WPF
        // rejects it outright with an ArgumentException, which took down the whole right-hand
        // column and the deck table with it, since every card that DID resolve went through
        // this line. An initialiser cannot express "leave this property alone".
        if (raw) nameText.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");

        // The caret rides INSIDE the name cell rather than taking a column of its own: the icon
        // already owns column 0, and a fifth column would push every figure on the page.
        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (lines.Count > 0)
        {
            var caret = new TextBlock
            {
                Text = open ? "\u25be" : "\u25b8",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            caret.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
            caret.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
            head.Children.Add(caret);
        }
        nameText.Margin = new Thickness(0);
        head.Children.Add(nameText);

        grid.Children.Add(WithColumn(head, 1));
        grid.Children.Add(WithColumn(BuildDeckCountCell(row, meta), 2));

        var body = new StackPanel();
        body.Children.Add(grid);

        // Opened: what the card actually does, with the numbers. No height cap - a card with a
        // dozen effects is long, and trimming it would hide precisely what was clicked for.
        if (open && lines.Count > 0)
        {
            var text = new StackPanel { Margin = new Thickness(46, 0, 14, 10) };
            foreach (var line in lines)
            {
                var block = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap };
                block.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
                block.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
                block.Margin = new Thickness(0, 0, 0, 3);
                text.Children.Add(block);
            }
            body.Children.Add(text);
        }

        var shell = new Border
        {
            Child = body,
            BorderBrush = (Brush)Application.Current.FindResource("MpRimHair"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        // A row with nothing to say is NOT a button. Half this table is unit shipments and
        // crates whose only description is their own name - the engine has no wording for an
        // effect aimed at the player - and a caret that opens nothing is a promise the data
        // cannot keep. Those rows stay exactly as they were.
        if (lines.Count == 0 || onToggle == null) return shell;

        var button = new Button { Content = shell, Cursor = System.Windows.Input.Cursors.Hand };
        button.SetResourceReference(FrameworkElement.StyleProperty, "MpBareButton");
        button.Click += (_, _) => onToggle();
        return button;
    }

    /// <summary>
    /// The figure on the right: how many decks carry the card, and what share of them.
    ///
    /// <para>The percentage is drawn ONLY when the view computed one — below the sample
    /// minimum it is null, and then nothing at all goes in its place. Not a dash, never a
    /// "0 %": the same rule the civilization balance follows, for the same reason.</para>
    /// </summary>
    private static FrameworkElement BuildDeckCountCell(
        Services.Multiplayer.DeckCardRow row, double meta)
    {
        var cell = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = meta,
            FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"),
            // A count of one is a fact, not a finding, so it does not get the weight.
            FontWeight = row.Players > 1 ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)Application.Current.FindResource(
                row.Players > 1 ? "MpTextPrimary" : "MpTextGhost"),
            Text = row.Percent == null
                ? row.Players.ToString("N0")
                : Strings.Format("MpStatsDecksCountAndShare", row.Players, row.Percent.Value),
        };
        return cell;
    }

    /// <summary>
    /// The folded tail: how many cards were seen once, which ones, and a way to see them all.
    ///
    /// <para>Built like the maps list's own tail row, deliberately. Hundreds of lines each
    /// saying "1" are the absence of a sample printed out, and this page had already decided
    /// what to do about that one table over.</para>
    /// </summary>
    private FrameworkElement BuildDeckTailRow(Services.Multiplayer.DeckCivGroup group)
    {
        var grid = new Grid { Margin = new Thickness(13, 9, 13, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var words = new StackPanel();
        var head = new TextBlock
        {
            Text = Strings.Format("MpStatsTailDecks", group.Tail.Count),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        head.SetResourceReference(TextBlock.FontSizeProperty, "MpLabelSize");
        head.SetResourceReference(TextBlock.ForegroundProperty, "MpTextSecondary");
        words.Children.Add(head);

        var names = new TextBlock
        {
            Text = string.Join(", ", group.Tail.Take(4).Select(r => r.Label))
                + (group.Tail.Count > 4 ? "\u2026" : ""),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 8, 0),
        };
        names.SetResourceReference(TextBlock.FontSizeProperty, "MpTagSize");
        names.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
        words.Children.Add(names);

        grid.Children.Add(WithColumn(words, 0));

        var total = new TextBlock
        {
            Text = group.Tail.Count.ToString("N0"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 10, 0),
            FontWeight = FontWeights.SemiBold,
        };
        total.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        total.SetResourceReference(TextBlock.FontSizeProperty, "MpFigureSize");
        total.SetResourceReference(TextBlock.ForegroundProperty, "MpTextGhost");
        grid.Children.Add(WithColumn(total, 1));

        var seeAll = new Button
        {
            Content = Strings.Get("MpStatsSeeAll"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        seeAll.SetResourceReference(FrameworkElement.StyleProperty, "MpLinkButton");
        seeAll.Click += (_, _) => { _deckTailsOpen.Add(group.Civ); RenderStatsTab(); };
        grid.Children.Add(WithColumn(seeAll, 2));

        var row = new Border { Child = grid };
        row.SetResourceReference(Border.BackgroundProperty, "MpPanelDim");
        return row;
    }

    /// <summary>
    /// The civilizations past the cap. Wars of Liberty ships 188 of them and this route is not
    /// bounded by civilization, so without this the page could trade sixty meaningless rows
    /// for a hundred and eighty meaningless headers.
    /// </summary>
    private FrameworkElement BuildDeckMoreCivsRow(int remaining)
    {
        var grid = new Grid { Margin = new Thickness(13, 9, 13, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var head = new TextBlock
        {
            Text = Strings.Format("MpStatsDecksMoreCivs", remaining),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        head.SetResourceReference(TextBlock.FontSizeProperty, "MpLabelSize");
        head.SetResourceReference(TextBlock.ForegroundProperty, "MpTextSecondary");
        grid.Children.Add(WithColumn(head, 0));

        var seeAll = new Button
        {
            Content = Strings.Get("MpStatsSeeAll"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        seeAll.SetResourceReference(FrameworkElement.StyleProperty, "MpLinkButton");
        seeAll.Click += (_, _) => { _deckCivsExpanded = true; RenderStatsTab(); };
        grid.Children.Add(WithColumn(seeAll, 1));

        var row = new Border { Child = grid };
        row.SetResourceReference(Border.BackgroundProperty, "MpPanelDim");
        return row;
    }

    /// <param name="pairColumnKey">What the first column is called. The allies table shares
    /// every other heading with the rivals table above it but not this one: two civilizations
    /// on the SAME side are a pair, and calling that column "matchup" said the opposite of what
    /// the footnote under the table said.</param>
    private static FrameworkElement BuildMatchupHeader(string? pairColumnKey = null)
    {
        var specs = Services.Multiplayer.CivTableLayout.Matchups;
        var grid = BuildCivGrid(specs);
        grid.Margin = new Thickness(14, 10, 14, 10);

        for (var i = 0; i < specs.Count; i++)
        {
            grid.Children.Add(WithColumn(new TextBlock
            {
                Text = Strings.Get(
                    pairColumnKey != null
                    && specs[i].Column == Services.Multiplayer.CivColumn.Civ
                        ? pairColumnKey
                        : Services.Multiplayer.CivTableLayout.MatchupHeaderKey(specs[i].Column)),
                Foreground = (Brush)Application.Current.FindResource("MpTableHeader"),
                FontSize = (double)Application.Current.FindResource("MpSectionLabelSize"),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = specs[i].RightAligned
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, CivTrailingGap(i, specs.Count), 0),
            }, i));
        }

        return new Border
        {
            Child = grid,
            BorderBrush = (Brush)Application.Current.FindResource("MpRimHair"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>One pairing.</summary>
    /// <remarks>
    /// <c>internal static</c> so <c>DialogXamlTests</c> can build the real row — nothing else
    /// constructs it and no compile step checks a resource looked up by name.
    /// </remarks>
    /// <param name="labelA">The first civilization as the player saw it; falls back to the
    /// server's string when the mod cannot be read.</param>
    /// <param name="labelB">The same for the second.</param>
    /// <param name="flagA">Its flag, or null.</param>
    /// <param name="flagB">The other's.</param>
    internal static FrameworkElement BuildMatchupRow(
        Models.Multiplayer.MatchupEntry row,
        string? labelA = null,
        string? labelB = null,
        FrameworkElement? flagA = null,
        FrameworkElement? flagB = null)
    {
        var meta = (double)Application.Current.FindResource("MpMetaSize");
        var mono = (FontFamily)Application.Current.FindResource("MonoFont");
        var specs = Services.Multiplayer.CivTableLayout.Matchups;

        var grid = BuildCivGrid(specs);
        grid.Margin = new Thickness(14, 0, 14, 0);
        grid.MinHeight = 34;

        // The record below belongs to the FIRST of the two, so the pair has to read in that
        // order — "A vs B" with B's record would be the same numbers meaning the opposite.
        var nameA = string.IsNullOrWhiteSpace(labelA) ? row.CivA : labelA!;
        var nameB = string.IsNullOrWhiteSpace(labelB) ? row.CivB : labelB!;

        var pair = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, CivTrailingGap(0, specs.Count), 0),
            // The whole pair, in one piece, for when the cell is too narrow to show it.
            ToolTip = TooltipHelper.Wrap(Strings.Format("MpMatchupPair", nameA, nameB)),
        };

        TextBlock Word(string text, string brush, double left, double right) =>
            new()
            {
                Text = text,
                Foreground = (Brush)Application.Current.FindResource(brush),
                FontSize = meta,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(left, 0, right, 0),
            };

        if (flagA != null) { flagA.Margin = new Thickness(0, 0, 7, 0); pair.Children.Add(flagA); }
        pair.Children.Add(Word(nameA, "MpTextPrimary", 0, 0));
        pair.Children.Add(Word(Strings.Get("MpMatchupVs"), "MpTextGhost", 6, 6));
        if (flagB != null) { flagB.Margin = new Thickness(0, 0, 7, 0); pair.Children.Add(flagB); }
        pair.Children.Add(Word(nameB, "MpTextPrimary", 0, 0));

        grid.Children.Add(WithColumn(pair, 0));

        void Number(int column, string text, string brush)
            => grid.Children.Add(WithColumn(new TextBlock
            {
                Text = text,
                Foreground = (Brush)Application.Current.FindResource(brush),
                FontSize = meta,
                FontFamily = mono,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, CivTrailingGap(column, specs.Count), 0),
            }, column));

        Number(1, row.Played.ToString(), "MpTextSecondary");
        Number(2, row.WinsA.ToString() + "-" + row.LossesA.ToString(), "MpTextSecondary");

        // The SAME bar as the civ table directly above and as the Profile card, through the same
        // method — so no two surfaces in the launcher can disagree about when there is enough
        // behind a number to state a rate. Below it nothing is drawn: not an em dash, never a 0.
        var stat = new Services.Multiplayer.CivStatRow(row.CivA, row.Played, row.WinsA, row.LossesA);
        var pct = Services.Multiplayer.CivStatsView.WinPercent(stat);
        Number(3, pct == null ? "" : pct.Value.ToString() + " %",
               pct == null ? "MpTextFaint"
                           : Services.Multiplayer.RankingTableLayout.PercentBrushKey(pct.Value));

        return new Border
        {
            Child = grid,
            BorderBrush = (Brush)Application.Current.FindResource("MpRimHair"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private static FrameworkElement BuildCivHeader()
    {
        var grid = BuildCivGrid();
        grid.Margin = new Thickness(14, 10, 14, 10);

        var specs = Services.Multiplayer.CivTableLayout.All;
        for (var i = 0; i < specs.Count; i++)
        {
            grid.Children.Add(WithColumn(new TextBlock
            {
                Text = Strings.Get(Services.Multiplayer.CivTableLayout.HeaderKey(specs[i].Column)),
                Foreground = (Brush)Application.Current.FindResource("MpTableHeader"),
                FontSize = (double)Application.Current.FindResource("MpSectionLabelSize"),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = specs[i].RightAligned
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, CivTrailingGap(i, specs.Count), 0),
            }, i));
        }

        return new Border
        {
            Child = grid,
            BorderBrush = (Brush)Application.Current.FindResource("MpRimHair"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>One civilization.</summary>
    /// <remarks>
    /// <para><c>internal static</c> so <c>DialogXamlTests</c> can build the real row — nothing
    /// else constructs it and no compile step checks a resource looked up by name.</para>
    ///
    /// <para>It takes the four figures rather than the server's DTO, because the same row now
    /// draws two different aggregates: the community table from <c>/stats/civs</c>, and the
    /// viewer's own computed here by <c>CivStatsView</c> from their history. One builder for
    /// both is what stops the two disagreeing about when a percentage may be shown.</para>
    /// </remarks>
    /// <param name="civ">The civilization AS THE PLAYER SAW IT — never the server's internal
    /// string, which for a reskin names one nobody played.</param>
    /// <param name="flag">Its flag, or null. Drawn inside the name cell rather than as a column
    /// of its own, so the shared column contract in <c>CivTableLayout</c> is untouched.</param>
    internal static FrameworkElement BuildCivRow(
        string civ, int played, int wins, int losses, int? avgSeconds,
        FrameworkElement? flag = null)
    {
        var meta = (double)Application.Current.FindResource("MpMetaSize");
        var mono = (FontFamily)Application.Current.FindResource("MonoFont");
        int columns = Services.Multiplayer.CivTableLayout.All.Count;

        var grid = BuildCivGrid();
        grid.Margin = new Thickness(14, 0, 14, 0);
        grid.MinHeight = 34;

        var nameCell = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, CivTrailingGap(0, columns), 0),
        };
        if (flag != null)
        {
            flag.Margin = new Thickness(0, 0, 7, 0);
            nameCell.Children.Add(flag);
        }
        nameCell.Children.Add(new TextBlock
        {
            Text = civ,
            Foreground = (Brush)Application.Current.FindResource("MpTextPrimary"),
            FontSize = meta,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        grid.Children.Add(WithColumn(nameCell, 0));

        void Number(int column, string text, string brush)
            => grid.Children.Add(WithColumn(new TextBlock
            {
                Text = text,
                Foreground = (Brush)Application.Current.FindResource(brush),
                FontSize = meta,
                FontFamily = mono,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, CivTrailingGap(column, columns), 0),
            }, column));

        Number(1, played.ToString(), "MpTextSecondary");
        Number(2, wins.ToString() + "-" + losses.ToString(), "MpTextSecondary");

        // The SAME rule the Profile card uses, so the two surfaces can never disagree about
        // when there is enough behind a civilization to state a rate.
        var stat = new Services.Multiplayer.CivStatRow(civ, played, wins, losses);
        var pct = Services.Multiplayer.CivStatsView.WinPercent(stat);

        // The bar carries the balance without the number: green over red, green being wins
        // over decided, so the red IS the losses. With no sample it is an empty grey channel
        // and not a bar at 0 % - which would state the very thing the blank percentage
        // beside it is refusing to state.
        var bar = BuildWinBar(wins, wins + losses, pct != null);
        bar.Margin = new Thickness(0, 0, CivTrailingGap(3, columns), 0);
        grid.Children.Add(WithColumn(bar, 3));

        // Nothing is drawn where there is no percentage — not an em dash, and never a 0.
        // That is the repo's rule and it is stated for this exact table.
        Number(4, pct == null ? "" : pct.Value.ToString() + " %",
               pct == null ? "MpTextFaint"
                           : Services.Multiplayer.RankingTableLayout.PercentBrushKey(pct.Value));

        Number(5,
            avgSeconds is > 0
                ? Strings.Format("MpResultMinutes", Math.Max(1, avgSeconds.Value / 60))
                : "",
            "MpTextFaint");

        return new Border
        {
            Child = grid,
            BorderBrush = (Brush)Application.Current.FindResource("MpRimHair"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>
    /// Fetches the civilization table, at most once a minute — the same window the server
    /// memoises for, so a request inside it would have been answered from memory anyway.
    /// </summary>
    private Models.Multiplayer.DeckStatsResponse? _deckStats;
    private DateTime _deckStatsFetchedUtc = DateTime.MinValue;
    private bool _deckStatsInFlight;
    private bool _decksUploadedThisSession;

    /// <summary>
    /// Which cards the community brings, for the STATS page.
    ///
    /// <para>A backend without the route answers 404, which lands in the catch and leaves
    /// <c>_deckStats</c> null — and null hides the whole section, so this ships before the
    /// server does.</para>
    /// </summary>
    private async Task RefreshDeckStatsAsync()
    {
        // A preview is not a stale cache: it is a deliberate state, and a real payload landing
        // on top of it half a second later leaves the page showing two sources at once.
        if (_demoStats) return;
        if (_session?.Api == null) return;
        if (_deckStatsInFlight) return;
        if (DateTime.UtcNow - _deckStatsFetchedUtc < ActivityMaxAge) return;

        _deckStatsInFlight = true;
        try
        {
            var stats = await _session.Api.GetDeckStatsAsync(StatsModId());
            _deckStatsFetchedUtc = DateTime.UtcNow;
            // The preview may have started while this request was already in flight, so
            // the guard at the top of the method is not enough: the check has to happen
            // where the field is WRITTEN. Without it the demo comes up, a reply from a
            // second ago lands on top of it, and the page shows one mod's table over
            // another payload's totals.
            if (_demoStats) return;
            _deckStats = stats;

            DiagnosticLog.Write(
                $"Deck stats: {stats?.Cards.Count ?? 0} cards from "
                + $"{stats?.Contributors ?? 0} contributors.");

            if (_activeSubtab == Subtab.Stats) RenderStatsTab();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Deck stats fetch failed: {ex.Message}");
        }
        finally { _deckStatsInFlight = false; }
    }

    /// <summary>
    /// Contributes this machine's decks, ONCE per session and only while the player has opted
    /// in.
    ///
    /// <para><b>The consent check is re-read every time and is never cached</b> — somebody who
    /// turns the switch off in Settings has turned it off, including for the upload that would
    /// otherwise have happened later in the same session.</para>
    ///
    /// <para>Once per session because the server REPLACES the account's rows: sending the same
    /// decks again changes nothing, and this is a courtesy to the community rather than
    /// something worth spending anybody's request budget on. It reads the disk off the UI
    /// thread — the deck files are small, but they are still files.</para>
    ///
    /// <para>Silent either way. A player who opted in did so to help a table they may never
    /// look at; telling them it worked is noise, and telling them it failed asks them to do
    /// something about a thing that does not matter.</para>
    /// </summary>
    private async Task MaybeUploadDecksAsync()
    {
        if (_decksUploadedThisSession) return;
        if (_config?.ShareDeckStats != true) return;
        if (_session?.Api == null || _session.Status != MultiplayerSession.SessionStatus.SignedIn) return;

        var profile = _getActiveProfile?.Invoke();
        if (profile == null || profile.IsStockGame) return;

        // Set BEFORE the await, not after: this is reached from several places and two of them
        // landing together would otherwise both read false and upload twice.
        _decksUploadedThisSession = true;

        try
        {
            var req = await Task.Run(() =>
            {
                var folder = Services.UserDataService.GetUserDataFolder(
                    Services.UserDataService.ResolveFolderName(profile, _config));
                if (string.IsNullOrEmpty(folder)) return null;

                var profiles = Services.HomeCityDeckService.Read(folder!);
                if (profiles.Count == 0) return null;

                // Grouped by CIVILIZATION, not by deck: the server counts people per card, and
                // a player's two decks for one civilization are still one person carrying
                // those cards. The deck NAME is deliberately not sent — it is whatever they
                // typed, and it identifies nothing.
                var byCiv = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var hc in profiles)
                {
                    if (string.IsNullOrWhiteSpace(hc.Civ)) continue;
                    if (!byCiv.TryGetValue(hc.Civ, out var cards))
                        byCiv[hc.Civ] = cards = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var deck in hc.Decks)
                        foreach (var card in deck.Cards)
                            if (!string.IsNullOrWhiteSpace(card.InternalName))
                                cards.Add(card.InternalName);
                }

                if (byCiv.Count == 0) return null;

                return new Models.Multiplayer.DeckUploadRequest
                {
                    ModId = profile.Id,
                    Decks = byCiv.Select(kv => new Models.Multiplayer.DeckUploadEntry
                    {
                        Civ = kv.Key,
                        Cards = kv.Value.ToList(),
                    }).ToList(),
                };
            });

            if (req == null)
            {
                DiagnosticLog.Write("Deck upload: nothing to send (no decks on disk).");
                return;
            }

            await _session.Api.UploadDecksAsync(req);
            DiagnosticLog.Write(
                $"Deck upload: {req.Decks.Count} civilizations for {req.ModId}.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Deck upload failed: {ex.Message}");
        }
    }

    private Models.Multiplayer.MatchupsResponse? _matchups;
    private DateTime _matchupsFetchedUtc = DateTime.MinValue;
    private bool _matchupsInFlight;

    /// <summary>
    /// Civilization against civilization, for the STATS page.
    ///
    /// <para>A backend without the route answers 404, which lands in the catch and leaves
    /// <c>_matchups</c> null — and null hides the whole section. So this ships before the server
    /// does, and turns itself on when the server is next deployed, with nothing to coordinate.
    /// The failure is logged rather than shown: a table that has not landed yet is not something
    /// to put in front of a player.</para>
    /// </summary>
    private async Task RefreshMatchupsAsync()
    {
        // A preview is not a stale cache: it is a deliberate state, and a real payload landing
        // on top of it half a second later leaves the page showing two sources at once.
        if (_demoStats) return;
        if (_session?.Api == null) return;
        if (_matchupsInFlight) return;
        if (DateTime.UtcNow - _matchupsFetchedUtc < ActivityMaxAge) return;

        _matchupsInFlight = true;
        try
        {
            var stats = await _session.Api.GetMatchupsAsync(StatsModId(), StatsMode());
            // Stamped AFTER the await, like the civ fetch beside it: a request that failed must
            // not burn the window, or retrying — the one right instinct — does nothing.
            _matchupsFetchedUtc = DateTime.UtcNow;
            // The preview may have started while this request was already in flight, so
            // the guard at the top of the method is not enough: the check has to happen
            // where the field is WRITTEN. Without it the demo comes up, a reply from a
            // second ago lands on top of it, and the page shows one mod's table over
            // another payload's totals.
            if (_demoStats) return;
            _matchups = stats;

            // Logged on success too. This table is empty by construction for its first weeks, so
            // "the server has nothing yet" and "the request never went out" look identical on
            // screen AND in a diagnostic bundle unless the success is written down.
            DiagnosticLog.Write($"Matchups: {stats?.Matchups.Count ?? 0} pairs.");

            if (_activeSubtab == Subtab.Stats) RenderStatsTab();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Matchups fetch failed: {ex.Message}");
        }
        finally { _matchupsInFlight = false; }
    }

    /// <summary>
    /// Which mods the server has data for, and which of them have team games.
    ///
    /// <para>Not scoped to anything: it is the catalogue the two pickers are built from, so it
    /// has to describe every mod at once. A backend without the route answers 404 and the
    /// launcher keeps the behaviour it had — installed mods only, 1v1 only — rather than
    /// showing an error over a page that is otherwise complete.</para>
    /// </summary>
    private async Task RefreshStatsModsAsync()
    {
        if (_demoStats) return;
        if (_session?.Api == null) return;
        if (_statsModsInFlight) return;
        if (DateTime.UtcNow - _statsModsFetchedUtc < ActivityMaxAge) return;

        _statsModsInFlight = true;
        try
        {
            var mods = await _session.Api.GetStatsModsAsync();
            _statsModsFetchedUtc = DateTime.UtcNow;
            if (_demoStats) return;
            _statsMods = mods?.Mods;

            DiagnosticLog.Write($"Stats mods: {_statsMods?.Count ?? 0} with matches.");

            // Both pickers are built from this, so the page is repainted even though no table
            // changed: the mod row may have gained a chip and the mode switch may have just
            // become offerable.
            if (_activeSubtab == Subtab.Stats) RenderStatsTab();
        }
        catch (Exception ex)
        {
            // Includes the 404 from a backend without the route, which is not a failure worth
            // showing anybody: the picker simply keeps offering the installed mods.
            DiagnosticLog.Write($"Stats mods fetch failed: {ex.Message}");
        }
        finally { _statsModsInFlight = false; }
    }

    private async Task RefreshCivStatsAsync()
    {
        // A preview is not a stale cache: it is a deliberate state, and a real payload landing
        // on top of it half a second later leaves the page showing two sources at once.
        if (_demoStats) return;
        if (_session?.Api == null) return;
        if (_civStatsInFlight) return;
        if (DateTime.UtcNow - _civStatsFetchedUtc < ActivityMaxAge) return;

        _civStatsInFlight = true;
        try
        {
            var stats = await _session.Api.GetCivStatsAsync(StatsModId(), StatsMode());
            // Stamped AFTER the await on purpose: a fetch that failed must not burn the window,
            // or the one state where retrying is the right instinct is the one where it does
            // nothing. Same rule as RefreshActivityStripAsync.
            _civStatsFetchedUtc = DateTime.UtcNow;
            // The preview may have started while this request was already in flight, so
            // the guard at the top of the method is not enough: the check has to happen
            // where the field is WRITTEN. Without it the demo comes up, a reply from a
            // second ago lands on top of it, and the page shows one mod's table over
            // another payload's totals.
            if (_demoStats) return;
            _civStats = stats;

            // Logged on SUCCESS too, not only on failure. This table is empty by construction for
            // its first weeks, so "the server has nothing yet" and "the request never went out"
            // look identical on screen — and with only a failure line they look identical in a
            // diagnostic bundle as well, which is the shape of bug this project keeps re-learning.
            DiagnosticLog.Write(
                $"Civ stats: {stats?.Civs.Count ?? 0} rows over "
                + $"{stats?.RatedMatchesWithCiv ?? 0} rated matches.");

            // The data can land after either page is already on screen: the STATS tables show
            // it in full and the ranking's right-hand column shows the top of it, so both have
            // to be repainted rather than only the one that asked.
            if (_activeSubtab == Subtab.Stats) RenderStatsTab();
            else if (_activeSubtab == Subtab.Ranking) RenderRankingSummaryCards();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Civ stats fetch failed: {ex.Message}");
        }
        finally { _civStatsInFlight = false; }
    }

    /// <summary>
    /// The page's own text around the table: title, the size of the league, the two scope
    /// chips and the footnote.
    ///
    /// <para>The scope chips STATE rather than filter — <c>/stats/community</c> takes neither
    /// a mod nor a window — so the time-window chip is drawn only when the server actually
    /// sent a window to name, and never as a hardcoded "30 days".</para>
    /// </summary>
    private void RenderRankingChrome(int shown)
    {
        RankingTitleText.Text = Strings.Get("MpSubtabRanking");
        RankingEloHelpButton.Content = Strings.Get("MpRankEloHelp");
        // The TOTAL on the ladder, which is not the length of the list once the league
        // outgrows the server's page. 0 means an older backend: we then say how many are
        // shown rather than inventing a total.
        var total = Services.Multiplayer.CommunityStatsView.RankedPlayers(
            _communityStats, _rankingShowsTeam);
        RankingSubtitleText.Text = Strings.Format(
            "MpRankSubtitle", total > 0 ? total : shown);

        var days = _communityStats?.Totals?.WindowDays ?? 0;
        RankingScopeWindowChip.Visibility = days > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (days > 0) RankingScopeWindowText.Text = Strings.Format("MpRankScopeWindow", days);

        var required = Services.Multiplayer.CommunityStatsView.RequiredDecided(_communityStats);
        RankingFootnoteText.Text = required.HasValue
            ? Strings.Format("MpRankFootnote", required.Value)
            : "";
    }

    /// <summary>
    /// Shows the pinned copy of the viewer's row only while their real one is scrolled out
    /// of the table's viewport — the handoff's "you always know where you are without
    /// looking for yourself".
    ///
    /// <para>It is deliberately NOT "always append my row at the bottom": a player who can
    /// already see themselves would then be listed twice, which is the confusion this is
    /// supposed to prevent rather than a second helping of the fix.</para>
    /// </summary>
    private void UpdateRankingPinnedRow()
    {
        if (RankingPinnedRow == null || RankingRowsScroll == null) return;
        if (_rankingOwnRow == null || RankingPinnedRow.Children.Count == 0)
        {
            RankingPinnedRow.Visibility = Visibility.Collapsed;
            return;
        }

        var visible = false;
        try
        {
            var top = _rankingOwnRow.TranslatePoint(new Point(0, 0), RankingRowsScroll).Y;
            var bottom = top + _rankingOwnRow.ActualHeight;
            // Half the row showing counts as showing: a row clipped to a sliver at the edge
            // is not something you can read your own position off.
            var half = _rankingOwnRow.ActualHeight / 2;
            visible = bottom > half && top < RankingRowsScroll.ViewportHeight - half;
        }
        catch (InvalidOperationException)
        {
            // TranslatePoint throws when the two are not in one visual tree yet — during the
            // first layout, or after the subtab was swapped out. Not knowing means leave it
            // hidden rather than pin a row over a table nobody is looking at.
        }

        RankingPinnedRow.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RankingRowsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        => UpdateRankingPinnedRow();

    /// <summary>
    /// Opens the page that explains how the rating works — the footnote's link.
    /// </summary>
    private void RankingEloHelpButton_Click(object sender, RoutedEventArgs e)
        => Services.SafeUrl.TryOpen(LauncherConfig.RatingHelpUrl);

    /// <summary>The viewer's own row in the table, for the pinned-copy rule.</summary>
    private FrameworkElement? _rankingOwnRow;

    /// <summary>
    /// The ladder's column headings.
    ///
    /// <para>The widths come from <see cref="Services.Multiplayer.RankingTableLayout"/>, which
    /// <see cref="BuildLeaderboardRow"/> also reads. They used to be a list of literals in each
    /// of these two methods, kept in step by a comment in both asking the next reader to
    /// remember — and header and rows drifting apart misaligns every row in the table, in a way
    /// no compile can see.</para>
    /// </summary>
    private UIElement BuildRankingHeader()
    {
        var grid = BuildRankingGrid();
        grid.Margin = new Thickness(14, 10, 14, 10);

        var specs = Services.Multiplayer.RankingTableLayout.All;
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var t = new TextBlock
            {
                Text = Strings.Get(Services.Multiplayer.RankingTableLayout.HeaderKey(spec.Column)),
                Foreground = (Brush)Application.Current.FindResource("MpTextLabel"),
                FontSize = (double)Application.Current.FindResource("MpSectionLabelSize"),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = spec.RightAligned
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left,
                // THE SAME trailing gap the row's own cells carry. A fixed column's width
                // INCLUDES the gap (see BuildRankingGrid), so a right-aligned heading with no
                // margin sits 12 px to the right of the value under it — which shipped, and is
                // visible in a screenshot as a heading that does not line up with its column.
                Margin = new Thickness(0, 0, ColumnTrailingGap(i), 0),
            };
            Grid.SetColumn(t, i);
            grid.Children.Add(t);
        }

        return new Border
        {
            Child = grid,
            BorderBrush = (Brush)Application.Current.FindResource("MpRimHair"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>
    /// The gap that belongs to the RIGHT of column <paramref name="index"/>, and zero for the
    /// last one. Shared by the header and the rows so a heading cannot drift from the values
    /// beneath it — which it had, by exactly one gap.
    /// </summary>
    private static double ColumnTrailingGap(int index)
        => index < Services.Multiplayer.RankingTableLayout.All.Count - 1
            ? Services.Multiplayer.RankingTableLayout.ColumnGap
            : 0;

    /// <summary>
    /// One Grid laid out to the ladder's columns. The single place those widths are turned
    /// into ColumnDefinitions, so the header and every row are the same shape by construction.
    /// </summary>
    private static Grid BuildRankingGrid()
    {
        var grid = new Grid();
        var gap = Services.Multiplayer.RankingTableLayout.ColumnGap;
        var specs = Services.Multiplayer.RankingTableLayout.All;

        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var trailing = i < specs.Count - 1 ? gap : 0;
            var column = new ColumnDefinition
            {
                // For a FIXED column the gap rides on the width rather than on each cell's
                // margin: a margin would have to be repeated on every cell of every row, and
                // one that was missed would shift that row alone. The two flexible columns
                // carry their own trailing gap in their cell content instead, because a star
                // width has nothing to add it to.
                Width = spec.FixedWidth == null
                    ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(spec.FixedWidth.Value + trailing),
            };
            // What stops PLAYER from eating the whole window now that the page stretches.
            if (spec.MaxWidth is double max) column.MaxWidth = max + trailing;
            grid.ColumnDefinitions.Add(column);
        }
        return grid;
    }


    /// <summary>
    /// Fetches the signed-in user's last 50 matches, then hands them to
    /// <see cref="RenderHistory"/>. Idempotent; runs whenever the History subtab is opened.
    /// Anonymous viewers (not signed in) see an empty list.
    ///
    /// <para>Fetch and render are SEPARATE so that changing the filter, or the language, can
    /// redraw the page without asking the server again — <c>/matches/history</c> shares the
    /// same per-IP budget as everything else, and that IP is shared behind a Radmin network.
    /// </para>
    /// </summary>
    private async Task RefreshHistoryAsync()
    {
        if (_session?.CurrentUser == null || _isRefreshingHistory) return;
        _isRefreshingHistory = true;
        try
        {
            // No repaint on the way IN: the render that kicked this already drew the
            // "Loading…" state, because _isRefreshingHistory was set above before the first
            // await. Re-entering the Profile with a page in hand repaints nothing at all,
            // which is what stops the list flickering every time the tab is opened.
            var resp = await _session.Api.GetHistoryAsync(_session.CurrentUser.Id);
            _historyRows = resp.Matches;
            _historyError = null;
        }
        catch (Exception ex)
        {
            // ALWAYS LOGGED. This used to be written only when a page was already cached, which
            // meant the FIRST fetch — the one that actually fails — left no trace at all: a
            // diagnostic bundle from a launcher stuck on "Loading…" contained not one line about
            // it, and the silence read as "the request never happened".
            DiagnosticLog.Write($"MultiplayerTab: history fetch failed"
                + (_historyRows != null ? " (keeping the cached page)" : "") + $": {ex}");

            // A failed REFRESH keeps whatever was already on screen: the page we have is still
            // true, and replacing a list of real matches with an error line because the server
            // hiccuped would be losing information to report a transient.
            if (_historyRows == null) _historyError = ex.Message;
        }
        finally
        {
            // ONE repaint, and it happens AFTER the flag is cleared. Repainting from inside the
            // catch is what turned this into a hang: the spinner branch tests "no rows AND still
            // refreshing", both of which were still true at that moment, so it matched and the
            // error line below it could never be reached. See MatchHistoryView.SectionFor.
            _isRefreshingHistory = false;
            if (_profileWindow != null) RenderProfileTab();
        }
    }

    /// <summary>The last page of history fetched, so a filter change costs no request.</summary>
    private IReadOnlyList<MatchHistoryRow>? _historyRows;

    /// <summary>
    /// Why the first fetch failed, when it did and there was no cached page to fall back on.
    /// Held rather than painted on the spot because the section that shows it is rebuilt from
    /// scratch on every render.
    /// </summary>
    private string? _historyError;

    private Services.Multiplayer.HistoryFilter _historyFilter =
        Services.Multiplayer.HistoryFilter.All;

    /// <summary>
    /// Changing the filter costs no request — it re-reads the page already in hand. The whole
    /// profile is redrawn rather than just the list, because the chips' own active state is
    /// part of it.
    /// </summary>
    private void SetHistoryFilter(Services.Multiplayer.HistoryFilter filter)
    {
        _historyFilter = filter;
        RenderProfileTab();
    }

    /// <summary>
    /// The date separator above a day's matches.
    ///
    /// <para>It exists so the date stops being repeated inside every card — a page of six
    /// matches from one evening used to print the same date six times, with the time as the
    /// only thing that differed between them.</para>
    /// </summary>
    private static UIElement BuildHistoryDayHeader(DateTime localDate)
    {
        var grid = new Grid { Margin = new Thickness(0, 18, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            // A match whose timestamp could not be read is filed under MinValue; saying so is
            // better than printing "01 JAN 0001" as if it were a day somebody played.
            Text = localDate == DateTime.MinValue.Date
                ? Strings.Get("MpHistoryDayUnknown")
                : localDate.ToString("dd MMM yyyy", System.Globalization.CultureInfo.CurrentCulture)
                           .ToUpperInvariant(),
            Foreground = (Brush)Application.Current.FindResource("MpTextLabel"),
            FontSize = (double)Application.Current.FindResource("MpSectionLabelSize"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(10, 0, 0, 0),
            Background = (Brush)Application.Current.FindResource("MpRimHair"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(rule, 1);
        grid.Children.Add(rule);
        return grid;
    }

    /// <summary>
    /// One match, as a card.
    ///
    /// <para><b>The result is read in one glance, which is what the rebuild was for.</b> A
    /// coloured stripe down the left says what happened before any word is read; the delta is
    /// the largest type on the card and sits hard right, where a column of them lines up down
    /// the page; the players are underneath in fixed columns. The old card said the same thing
    /// three times — a "Loss" pill, a "-117" pill and then the per-player lines — and put the
    /// full date inside every one of them.</para>
    ///
    /// <para><b>A match that did not count is grey and says why.</b> Never "Draw": 0.5 means
    /// the outcome could not be read, and most stored rows are that. The reason comes from the
    /// server's own <c>unrated_reason</c> through the same mapping the end-of-match card uses,
    /// so the two surfaces cannot tell a player different things about one match.</para>
    /// </summary>
    /// <remarks><c>internal</c> for the same reason as <see cref="BuildLeaderboardRow"/>.</remarks>
    internal Border BuildHistoryRow(MatchHistoryRow row, string? meId)
    {
        var verdict = MatchOutcomeView.Classify(row.Result);
        var rated = Services.Multiplayer.MatchHistoryView.IsRated(row);

        var body = new StackPanel { Margin = new Thickness(15, 13, 15, 13) };
        // Each player line is its own Grid, so without a shared scope "Won" and "Lost" —
        // different widths, and much more so in Spanish — would put the two deltas at two
        // different x positions. Scoped to THIS card: sharing across cards would make every
        // match in the list as wide as the longest name anywhere in it.
        Grid.SetIsSharedSizeScope(body, true);

        // ---- line 1 + the delta, side by side --------------------------------------
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headLeft = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = Strings.Get(verdict switch
            {
                MatchVerdict.Win => "MpHistoryWin",
                MatchVerdict.Loss => "MpHistoryLoss",
                _ => "MpResultNone",
            }),
            Foreground = (Brush)Application.Current.FindResource(
                rated ? "MpTextHeading" : "MpTextBody"),
            FontSize = (double)Application.Current.FindResource("MpBodySize"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // "against {name}" only in a one-on-one — past two players it would be naming one
        // person out of several, and the roster underneath already lists them all.
        var rival = Services.Multiplayer.MatchHistoryView.SoleOpponent(row, meId);
        if (rated && rival != null)
        {
            titleRow.Children.Add(new TextBlock
            {
                Text = Strings.Format("MpHistoryAgainst", rival),
                Margin = new Thickness(9, 0, 0, 0),
                Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
                FontSize = (double)Application.Current.FindResource("MpMetaSize"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        if (!rated)
        {
            titleRow.Children.Add(BuildHistoryTag(Strings.Get("MpHistoryNotCounted")));
        }
        headLeft.Children.Add(titleRow);

        // Who played, resolved before the meta line because it decides what goes in it.
        var players = MatchParticipantsView.Build(row.Participants, meId);

        // ---- line 2: mod · map · start · end ---------------------------------------
        var parts = new System.Collections.Generic.List<string> { ResolveModDisplayName(row.ModId) };
        if (!string.IsNullOrWhiteSpace(row.MapName))
            parts.Add(row.MapName.Replace('_', ' '));   // "ESOC_Arizona" is a file name
        var startedLocal = Services.Multiplayer.MatchHistoryView.ParseLocal(row.StartedAt);
        var endedLocal = Services.Multiplayer.MatchHistoryView.ParseLocal(row.EndedAt);
        if (startedLocal.HasValue) parts.Add(startedLocal.Value.ToString("t", System.Globalization.CultureInfo.CurrentCulture));
        if (endedLocal.HasValue) parts.Add(endedLocal.Value.ToString("t", System.Globalization.CultureInfo.CurrentCulture));
        // The head count survives only when there are no NAMES to replace it. "2 players"
        // above a list of those two players is noise; above nothing it is all we can say,
        // which is the case for every backend older than the participants field.
        if (players.Count == 0 && row.PlayerCount > 0)
            parts.Add(Strings.Format("MpHistoryPlayers", row.PlayerCount));

        headLeft.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", parts),
            Foreground = (Brush)Application.Current.FindResource("MpTextDim"),
            FontSize = (double)Application.Current.FindResource("MpMetaSize"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0),
        });
        Grid.SetColumn(headLeft, 0);
        head.Children.Add(headLeft);

        // The delta, as the card's headline. Painted only when BOTH ends are known, which is
        // the same refusal the end-of-match card makes: a match stored without being rated
        // shows an em dash rather than a "+0" claiming it was played for nothing.
        var delta = MatchOutcomeView.Delta(row.RatingBefore, row.RatingAfter);
        var deltaText = RatingDisplay.FormatDelta(delta);
        var headRight = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        headRight.Children.Add(new TextBlock
        {
            Text = deltaText ?? Strings.Get("MpDash"),
            HorizontalAlignment = HorizontalAlignment.Right,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont"),
            FontSize = (double)Application.Current.FindResource("MpHistoryDeltaSize"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource(
                delta == null ? "MpTextDim"
                : delta.Value >= 0 ? "MpOkTextAlt"
                : "MpDestructiveText"),
        });

        // "1500 → 1383" underneath, or the rating it stayed at when nothing moved.
        var trail = row.RatingBefore.HasValue && row.RatingAfter.HasValue
            ? Strings.Format("MpHistoryRatingMove",
                             (int)Math.Round(row.RatingBefore.Value),
                             (int)Math.Round(row.RatingAfter.Value))
            : row.RatingAfter.HasValue
                ? ((int)Math.Round(row.RatingAfter.Value)).ToString()
                : null;
        if (trail != null)
        {
            headRight.Children.Add(new TextBlock
            {
                Text = trail,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = (Brush)Application.Current.FindResource("MpTextDim"),
                FontSize = (double)Application.Current.FindResource("MpMicroSize"),
            });
        }
        Grid.SetColumn(headRight, 1);
        head.Children.Add(headRight);
        body.Children.Add(head);

        // ---- the players, behind an inner rule -------------------------------------
        if (players.Count > 0)
        {
            var roster = new StackPanel
            {
                Margin = new Thickness(0, 11, 0, 0),
                // Capped, because the card is not. Each line is a name on the left and a
                // verdict plus a delta on the right, and on a full-width card that is a sweep
                // of nearly two thousand pixels for two words. The RULE above it still crosses
                // the whole card — what is bounded is the content, not the separator.
                MaxWidth = HistoryInnerBlockWidth,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            // Sides, but ONLY when the match has them. A 1v1 reports team 0 for both players,
            // and so does every match stored before the launcher could work teams out —
            // HasTeams is false for all of those and this collapses back to the flat list it
            // has always been.
            if (Services.Multiplayer.MatchParticipantsView.HasTeams(players))
            {
                foreach (var side in players.GroupBy(pl => pl.Team).OrderBy(g => g.Key))
                {
                    roster.Children.Add(new TextBlock
                    {
                        Text = Strings.Format("MpHistoryTeam", side.Key + 1),
                        Foreground = (Brush)Application.Current.FindResource("MpTextLabel"),
                        FontSize = (double)Application.Current.FindResource("MpSectionLabelSize"),
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 8, 0, 2),
                    });
                    foreach (var player in side)
                        roster.Children.Add(BuildHistoryPlayerRow(player));
                }
            }
            else
            {
                foreach (var player in players)
                    roster.Children.Add(BuildHistoryPlayerRow(player));
            }

            body.Children.Add(new Border
            {
                Child = roster,
                BorderBrush = (Brush)Application.Current.FindResource("MpRimHair"),
                BorderThickness = new Thickness(0, 1, 0, 0),
            });
        }

        // ---- why it did not count --------------------------------------------------
        if (!rated) body.Children.Add(BuildHistoryUnratedNote(row));

        // ---- actions ----------------------------------------------------------------
        // "Replay" only, and only when there IS one. The reference also draws a "Rematch"
        // button; it is not here because nothing behind it exists — creating a room and
        // inviting the opponent is a feature, and a button that looks like one and does
        // nothing is worse than its absence (the Workshop's disabled pill taught that once).
        if (!string.IsNullOrEmpty(row.ReplayObjectKey))
        {
            var replay = new Button
            {
                Content = Strings.Get("MpHistoryReplay"),
                Style = (Style)Application.Current.FindResource("MpSecondaryButton"),
                FontSize = (double)Application.Current.FindResource("MpLabelSize"),
                Height = 30,
                Padding = new Thickness(13, 0, 13, 0),
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = row.Id,
            };
            replay.Click += (_, _) =>
            {
                try
                {
                    // Opened in the browser — the backend streams it back with a
                    // Content-Disposition: attachment header, so it saves rather than renders.
                    var uri = new Uri(_session!.Api.BaseUri, $"replays/{row.Id}");
                    Services.SafeUrl.TryOpen(uri.AbsoluteUri);
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"MultiplayerTab: replay open: {ex.Message}");
                }
            };
            body.Children.Add(replay);
        }

        // ---- the card ----------------------------------------------------------------
        var card = new Grid();
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var stripe = new Border
        {
            Background = (Brush)Application.Current.FindResource(
                !rated ? "MpNoResult"
                : verdict == MatchVerdict.Win ? "MpOk"
                : verdict == MatchVerdict.Loss ? "MpDestructive"
                : "MpNoResult"),
        };
        Grid.SetColumn(stripe, 0);
        card.Children.Add(stripe);
        Grid.SetColumn(body, 1);
        card.Children.Add(body);

        return new Border
        {
            Child = card,
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = (CornerRadius)Application.Current.FindResource("RadiusPanel"),
            // Clipped, or the stripe's square corners poke out of the rounded card.
            ClipToBounds = true,
            Background = (Brush)Application.Current.FindResource(rated ? "MpPanel" : "MpPanelDim"),
            BorderBrush = (Brush)Application.Current.FindResource(
                rated ? "MpRimFaint" : "MpRimHair"),
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>
    /// The amber note under a match that did not count, naming the REAL reason and linking to
    /// the page that explains the rule.
    ///
    /// <para>The reason is the server's, through
    /// <see cref="MatchOutcomeView.UnratedNoteKey"/> — the same mapping the end-of-match card
    /// uses. Working it out here instead would put a copy of the server's policy in the client,
    /// which is what drifted the last time it was tried: the card told a player the match had
    /// counted towards nobody's rating while the backend was rating it.</para>
    ///
    /// <para>A null reason falls through that mapping to the missing-recording message, which
    /// is the overwhelmingly common cause and is what an older backend's rows land on.</para>
    /// </summary>
    private UIElement BuildHistoryUnratedNote(MatchHistoryRow row)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bang = new TextBlock
        {
            Text = "!",
            Foreground = (Brush)Application.Current.FindResource("MpCaution"),
            FontSize = (double)Application.Current.FindResource("MpMetaSize"),
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 9, 0),
        };
        Grid.SetColumn(bang, 0);
        grid.Children.Add(bang);

        var text = new TextBlock
        {
            Text = Strings.Get(MatchOutcomeView.UnratedNoteKey(row.UnratedReason)),
            Foreground = (Brush)Application.Current.FindResource("MpCautionText"),
            FontSize = (double)Application.Current.FindResource("MpMetaSize"),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var how = new Button
        {
            Content = Strings.Get("MpHistorySeeHow"),
            Style = (Style)Application.Current.FindResource("MpNoteLinkButton"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 0, 0, 0),
        };
        how.Click += (_, _) => Services.SafeUrl.TryOpen(LauncherConfig.RatingHelpUrl);
        Grid.SetColumn(how, 2);
        grid.Children.Add(how);

        return new Border
        {
            Child = grid,
            Margin = new Thickness(0, 11, 0, 0),
            Padding = new Thickness(11, 9, 11, 9),
            // Same reason as the roster: the note is a sentence with a link at the end of it,
            // and stretched across a full-width card the link ends up a metre from the text
            // that explains why it is there.
            MaxWidth = HistoryNoteWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = (CornerRadius)Application.Current.FindResource("RadiusControl"),
            Background = (Brush)Application.Current.FindResource("MpNoteBg"),
            BorderBrush = (Brush)Application.Current.FindResource("MpNoteRim"),
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>
    /// How wide the blocks INSIDE a history card are allowed to get.
    ///
    /// <para>The card itself fills the window; these two do not, and that is the whole trick
    /// that makes filling it safe. Both are "something on the left, something on the right"
    /// laid out in a row, which is unreadable once the row is two thousand pixels wide. The
    /// delta in the card's head is the deliberate exception — it lands at the same x on every
    /// card, so it reads as a column rather than as a stray number.</para>
    /// </summary>
    private const double HistoryInnerBlockWidth = 620;

    private const double HistoryNoteWidth = 760;

    /// <summary>
    /// The colourless "DIDN'T COUNT" tag. No hue on purpose: the whole claim is that the match
    /// says nothing, and any colour would be the wrong kind of emphasis on it.
    /// </summary>
    private static UIElement BuildHistoryTag(string text)
        => new Border
        {
            Margin = new Thickness(9, 0, 0, 0),
            Padding = new Thickness(6, 3, 6, 3),
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = (CornerRadius)Application.Current.FindResource("RadiusSm"),
            Background = (Brush)Application.Current.FindResource("MpNeutralBadgeBg"),
            Child = new TextBlock
            {
                Text = text,
                Foreground = (Brush)Application.Current.FindResource("MpNeutralBadgeText"),
                FontSize = (double)Application.Current.FindResource("MpSectionLabelSize"),
                FontWeight = FontWeights.SemiBold,
            },
        };

    /// <summary>
    /// One player under a history row: avatar, name, whether they won, what it cost them.
    ///
    /// <para>A Grid and not a horizontal StackPanel, for the reason the rooms table
    /// documents: a horizontal StackPanel measures its children with INFINITE width, so the
    /// name's <c>CharacterEllipsis</c> would never fire and a long one would push the verdict
    /// off the card instead of trimming.</para>
    /// </summary>
    /// <remarks>
    /// <c>internal</c> so <c>DialogXamlTests</c> can build the real thing rather than a
    /// hand-copied imitation of it — nothing else in the launcher constructs this, and the
    /// History tab is not a surface the startup smoke test ever opens.
    /// </remarks>
    internal static FrameworkElement BuildHistoryPlayerRow(MatchParticipantLine player)
    {
        var caption = (double)Application.Current.FindResource("FontSizeCaption");

        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Auto, and shared with the sibling rows of the same card — see the scope set on
        // their container. SharedSizeGroup only works on Auto or absolute widths.
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
            SharedSizeGroup = "HistoryPlayerVerdict",
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
            SharedSizeGroup = "HistoryPlayerDelta",
        });

        var avatar = BuildAvatarDisc(player.Name, player.AvatarUrl, 22);
        avatar.Margin = new Thickness(0, 0, 9, 0);
        grid.Children.Add(WithColumn(avatar, 0));

        // Name and "(you)" as two Runs of ONE TextBlock, so the ellipsis applies to the pair
        // and the marker cannot be trimmed away while the name it belongs to survives.
        var name = new TextBlock
        {
            Foreground = (Brush)Application.Current.FindResource("MpTextPrimary"),
            FontSize = caption,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.Inlines.Add(new System.Windows.Documents.Run(player.Name));
        if (player.IsMe)
        {
            name.Inlines.Add(new System.Windows.Documents.Run(
                "  (" + Strings.Get("MpOnlinePlayersYou") + ")")
            {
                Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
            });
        }

        // A Run of the SAME TextBlock rather than a column of its own: the grid already has
        // four, and a fifth would take width from the name on every row including the many
        // that have no civilization to show. Last, so it is what the ellipsis eats first —
        // whose name it is matters more than what they played.
        if (player.Civ != null)
        {
            name.Inlines.Add(new System.Windows.Documents.Run("  ·  " + player.Civ)
            {
                Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
            });
        }

        // And the home city after it, for the same reasons and in the same TextBlock. It is the
        // CITY and not the deck: a recording names the deck's file and never which of its decks
        // was used, and the cards live on that player's own machine.
        if (player.HomeCity != null)
        {
            name.Inlines.Add(new System.Windows.Documents.Run("  ·  " + player.HomeCity)
            {
                Foreground = (Brush)Application.Current.FindResource("MpTextDim"),
            });
        }
        grid.Children.Add(WithColumn(name, 1));

        // NOTHING for a 0.5, which is the majority of stored matches — and it is why this
        // block is still worth drawing for them: it names who was there without claiming to
        // know how it ended, where the old row could only say "2 players".
        if (player.Verdict != MatchVerdict.NoResult)
        {
            var won = player.Verdict == MatchVerdict.Win;
            grid.Children.Add(WithColumn(new TextBlock
            {
                Text = (won ? "\u2713 " : "\u2715 ")
                     + Strings.Get(won ? "MpHistoryPlayerWon" : "MpHistoryPlayerLost"),
                Foreground = (Brush)Application.Current.FindResource(
                    won ? "MpOk" : "MpDestructiveText"),
                FontSize = caption,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }, 2));
        }

        // Null when either end of the rating is unknown — never a "+0", the same refusal the
        // match card makes: not knowing what a match did is not the same as it doing nothing.
        var delta = RatingDisplay.FormatDelta(player.RatingDelta);
        if (delta != null)
        {
            grid.Children.Add(WithColumn(new TextBlock
            {
                Text = delta,
                Foreground = (Brush)Application.Current.FindResource(
                    delta.StartsWith('-') ? "MpDestructiveText" : "MpOk"),
                FontSize = caption,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(12, 0, 0, 0),
                MinWidth = 44,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            }, 3));
        }

        return grid;
    }

    // ---------- Sign in / out ----------

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        SignInButton.IsEnabled = false;
        SignInErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var dlg = new GitHubLoginDialog(_session)
            {
                Owner = Window.GetWindow(this),
            };
            var ok = dlg.ShowDialog();
            if (ok == true)
            {
                await RefreshRoomsListAsync();
                await RefreshQuotaAsync();
            }
        }
        catch (Exception ex)
        {
            SignInErrorText.Text = ex.Message;
            SignInErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Signs out. Public because the affordance moved to the title-bar account menu
    /// when the account row was removed — this is the same path that link used, not a
    /// second one, so the rating cache is cleared here and nowhere else.
    /// </summary>
    public void SignOut()
    {
        // Or the next person to sign in on this machine would be shown the previous
        // player's rating until the launcher restarts.
        _cachedStanding = null;
        CloseProfileWindow();
        _session?.SignOut();
    }

    /// <summary>Whether a session is signed in — the profile window has nothing to show
    /// otherwise, and the sign-in panel lives on the tab rather than in the window.</summary>
    public bool IsSignedIn => _session?.Status == MultiplayerSession.SessionStatus.SignedIn;

    /// <summary>
    /// Opens the player's profile in its own window. Called from the account block in the
    /// launcher's nav bar and from the "your match was scored after all" notification.
    ///
    /// <para>Renders BEFORE Show(), like <c>OpenLobbyWindow</c>: a window shown empty and
    /// filled a frame later flashes.</para>
    /// </summary>
    public void OpenProfileWindow()
    {
        if (_profileWindow != null)
        {
            _profileWindow.Activate();
            return;
        }

        var w = new ProfileWindow();
        _profileWindow = w;
        RenderProfileTab();

        // ReferenceEquals: a replacement may already have been opened by the time this fires.
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(_profileWindow, w))
                _profileWindow = null;
        };

        w.Show();
    }

    /// <summary>
    /// Closes it from the launcher's side. Null the field FIRST — the same order
    /// <c>CloseLobbyWindow</c> uses — so the Closed handler cannot clear a window opened in
    /// its place.
    /// </summary>
    internal void CloseProfileWindow()
    {
        var stale = _profileWindow;
        if (stale == null) return;
        _profileWindow = null;
        try { stale.Close(); } catch { }
    }

    /// <summary>Shortest time the refresh button stays in its busy state.</summary>
    private const int RefreshSpinMinMs = 500;

    /// <summary>
    /// Manual refresh. It holds the busy state for at least
    /// <see cref="RefreshSpinMinMs"/> even when the round-trip beats it, which the
    /// reference asks for so "el clic se sienta": the usual response is fast enough that
    /// the list often comes back identical, and with no acknowledgement at all the button
    /// reads as broken. Waiting out the remainder is the cheapest honest feedback — it
    /// delays nothing the user can act on.
    /// </summary>
    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshSpinning) return;
        _refreshSpinning = true;
        var started = Environment.TickCount64;
        RefreshButton.IsEnabled = false;
        try
        {
            await RefreshRoomsListAsync();
            await RefreshQuotaAsync();
        }
        finally
        {
            var left = RefreshSpinMinMs - (int)(Environment.TickCount64 - started);
            if (left > 0) await Task.Delay(left);
            RefreshButton.IsEnabled = true;
            _refreshSpinning = false;
        }
    }

    /// <summary>Guards the manual refresh's minimum-spin window.</summary>
    private bool _refreshSpinning;

    /// <summary>Retry after a failed rooms fetch — the link in the amber error line.</summary>
    private async void RoomsErrorRetry_Click(object sender, RoutedEventArgs e)
        => await RefreshRoomsListAsync();

    private async void CreateRoomButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null || _getActiveProfile == null || _computeModFingerprint == null) return;

        // Build the list of mods the host can pick from. We restrict
        // the dropdown to mods that are actually installed on this PC
        // — picking an uninstalled mod would just fail at fingerprint
        // time. The active profile (from the Play tab) is highlighted
        // as the default but the host can change it.
        var allProfiles = ModRegistry.All;
        var installedProfiles = new List<ModProfile>();
        foreach (var p in allProfiles)
        {
            var installPath = _session.Api != null
                ? GetInstallPath(p)
                : null;
            if (!string.IsNullOrEmpty(installPath))
                installedProfiles.Add(p);
        }

        if (installedProfiles.Count == 0)
        {
            await MpAlertOverlay.NoticeAsync(
                TabRootGrid,
                Strings.Get("MpNoticeModNotInstalledTitle"),
                Strings.Get("MpNoticeModNotInstalledBody"),
                Strings.Get("MpAlertOk"));
            return;
        }

        var initiallySelected = _getActiveProfile() ?? installedProfiles[0];

        var dlg = new CreateLobbyDialog(
            _session,
            installedProfiles,
            initiallySelected,
            // The dialog hands us each picked profile and we return
            // its on-disk fingerprint. Bridge through the same
            // callback the tab already received from MainWindow.
            profile => _computeModFingerprint!(profile),
            // Copy-awareness: which installed copies the mod has + which is
            // active. Lets the host SEE and (for the active mod) CHOOSE the copy.
            BuildCopyInfo,
            // Choosing a copy rotates the active copy (single source of truth);
            // multiplayer launches / fingerprints the active copy.
            installId => _switchActiveCopy != null ? _switchActiveCopy(installId) : Task.CompletedTask,
            // Same answer the room rows ask for, from the same field, so the dialog cannot
            // offer to create a seat the list would refuse to fill.
            developerMode: ObserversUnlocked)
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true || dlg.CreatedLobby == null) return;

        try
        {
            var createdModId = dlg.CreatedLobbyProfile?.Id ?? "";
            DiagnosticLog.Write($"CreateRoom: dialog returned lobby id {dlg.CreatedLobby.Id} (mod={createdModId}), entering room");
            // Stamp the current room's mod id so LaunchActiveModGame
            // picks the right profile, even if the user later
            // switches the Play tab to a different mod while still
            // inside the room. CreateLobbyResponse doesn't carry
            // the mod id back (it only has id + status), so we read
            // it from the dialog's selected profile.
            _currentLobbyModId = createdModId;
            _currentLobbyMaxPlayers = dlg.CreatedLobbyMaxPlayers;
            _currentLobbyIsPrivate = dlg.CreatedLobbyIsPrivate;
            _currentLobbyIsCompetitive = dlg.CreatedLobbyIsCompetitive;
            _currentLobbySpectatorSlots = dlg.CreatedLobbySpectatorSlots;
            // Never a bracket slot: this dialog makes ordinary rooms, and a tournament room
            // is only ever minted by POST /tournaments/:id/matches/:mid/lobby.
            _currentLobbyTournamentMatchId = null;
            // We just created it — the POST returns no created_at, so ~now is the
            // room's open time (good to the second). Drives the "open for X" counter.
            _currentLobbyCreatedUtc = DateTime.UtcNow;
            await _session.EnterHostedLobbyAsync(dlg.CreatedLobby, dlg.CreatedLobbyTitle);
            // Optimistic host flag — we created the room, so we ARE the
            // host. The WS room_state frame will reaffirm this when it
            // arrives. Setting it here means the Start button shows up
            // immediately even if the WS hiccups (e.g. a tunnel idle
            // drop) before room_state lands.
            _isHostInCurrentRoom = true;
            RenderRoomPanel();
            // Said in the room, because that is where they are now looking and because a host
            // who believes their rating is on the line will play accordingly. A silent downgrade
            // is the one outcome worth ruling out here.
            if (dlg.CreatedLobbyCompetitiveDowngraded)
                AppendChatSystem(Strings.Get("MpCreateDialogCompetitiveDowngraded"), ChatSeverity.Warning);
            DiagnosticLog.Write(
                $"CreateRoom: EnterHostedLobbyAsync completed for {dlg.CreatedLobby.Id} " +
                $"(competitive={dlg.CreatedLobbyIsCompetitive})");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"CreateRoom: EnterHostedLobbyAsync THREW: {ex.GetType().Name}: {ex.Message}");
            await MpAlertOverlay.NoticeAsync(
                TabRootGrid,
                Strings.Get("MpNoticeCreateFailedTitle"),
                ex.Message,
                Strings.Get("MpAlertOk"));
            SignInErrorText.Text = ex.Message;
            SignInErrorText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Look up the user's install path for a given mod profile via
    /// LauncherConfig — without that, we'd be hammering the disk
    /// inside the dialog for every selection change.
    /// </summary>
    private string? GetInstallPath(ModProfile profile)
    {
        // The launcher config is owned by MainWindow; we don't have a
        // direct reference here. Probe heuristically: try the saved
        // path via the same registry the rest of the launcher uses,
        // then fall back to "any non-empty install probe file under
        // the default folder".
        var cfg = WarsOfLibertyLauncher.Models.LauncherConfig.Load();
        var saved = cfg.GetState(profile.Id).InstallPath;
        if (!string.IsNullOrEmpty(saved)) return saved;

        // The stock Age of Empires III profile is never "installed" through
        // the launcher, so it has no saved path. Resolve it CONFIG-AWARE from the
        // detected AoE3 install (honours a manually-pointed / non-standard folder
        // via config.GameExecutable + the durable config.Aoe3ManualPath, which the
        // bare AoE3Detector.FindInstallRoot() can't see) so it still shows up as
        // host-able / join-able and can be fingerprinted for the version-parity check.
        if (profile.IsStockGame)
            return GameLauncher.FindAoe3InstallRoot(cfg);

        return null;
    }

    /// <summary>
    /// Copy-awareness for the create-room dialog: does this mod have multiple
    /// installed copies, which is active, and can we switch it from here (only
    /// when it's the active dashboard mod, since the switch rotates the active
    /// copy). Labels are disambiguated so two copies sharing a folder name are
    /// still distinguishable. Read-only for the stock game / single-install mods.
    /// </summary>
    private Models.ModCopyInfo BuildCopyInfo(ModProfile profile)
    {
        var st = WarsOfLibertyLauncher.Models.LauncherConfig.Load().GetState(profile.Id);
        if (profile.IsStockGame || !st.HasMultipleInstalls)
            return new Models.ModCopyInfo(false, false, System.Array.Empty<Models.ModCopyChoice>());

        var raw = new List<(string Id, string Label, string Path, bool Active)>
        {
            (st.ActiveInstallId, CopyLeaf(st.InstallPath), st.InstallPath, true),
        };
        foreach (var o in st.OtherInstalls)
            raw.Add((o.Id, CopyLeaf(o.InstallPath), o.InstallPath, false));

        var labels = Services.PathDisplay.DisambiguateLabels(
            raw.Select(r => (r.Label, r.Path)).ToList());

        var choices = new List<Models.ModCopyChoice>();
        for (int i = 0; i < raw.Count; i++)
            choices.Add(new Models.ModCopyChoice(raw[i].Id, labels[i], raw[i].Active));

        var active = _getActiveProfile?.Invoke();
        bool canSwitch = active != null
            && string.Equals(active.Id, profile.Id, StringComparison.OrdinalIgnoreCase);
        return new Models.ModCopyInfo(true, canSwitch, choices);
    }

    /// <summary>Folder-leaf label of an install path (the copy's own folder name).</summary>
    private static string CopyLeaf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var leaf = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(leaf) ? path! : leaf;
    }


    // ---------- Rooms list polling + rendering ----------

    /// <summary>
    /// Fetch <c>GET /lobbies</c> and render the rooms browser. Called both
    /// as an explicit refresh and as a 10 s background auto-refresh — the
    /// <paramref name="quiet"/> flag is what separates the two.
    /// </summary>
    /// <param name="quiet">
    /// When true this is a background auto-refresh (the 10 s
    /// <see cref="_roomsListTimer"/> tick): skip the "loading" skeleton,
    /// don't repaint when the result matches what's already rendered, and
    /// swallow transient errors instead of wiping the list. A false
    /// (default) call is an explicit refresh — manual Actualizar button,
    /// sign-in, tab activation, leave-room — and always re-renders with the
    /// skeleton + error banner.
    /// </param>
    private async Task RefreshRoomsListAsync(bool quiet = false)
    {
        if (_session == null || _isRefreshingList) return;
        _isRefreshingList = true;
        try
        {
            // Loading skeleton: a single dim line so the user knows
            // a fetch is in flight. The empty-state card and error
            // box are siblings (not children of RoomsListPanel) so
            // we hide both while loading and re-decide afterwards.
            // Skipped on a quiet auto-refresh — flashing "loading…"
            // every 10 s would be worse than swapping the (usually
            // unchanged) rows once the result lands.
            if (!quiet)
            {
                RoomsListPanel.Children.Clear();
                RoomsEmptyState.Visibility = Visibility.Collapsed;
                RoomsErrorBox.Visibility = Visibility.Collapsed;
                for (int i = 0; i < SkeletonRowCount; i++)
                    RoomsListPanel.Children.Add(BuildRoomSkeletonRow());
            }

            var list = await _session.Api.ListLobbiesAsync();
            // Cache the snapshot so the room view (and any other
            // consumer that needs MaxPlayers / IsPrivate / ModId
            // for the current lobby) can read it without an extra
            // round-trip.
            _lastBrowserList = list.Lobbies as List<LobbySummary> ?? new List<LobbySummary>(list.Lobbies);

            // Stamp the "last updated" label on every successful fetch — even a
            // quiet poll that skips the re-render still confirmed the list is
            // current, so the header should reflect it.
            _lastRoomsRenderedAt = DateTime.Now;
            UpdateRoomsUpdatedLabel();

            // The backend just answered after failing. Deliberately here, above the quiet
            // diff's early return: a poll that finds the rooms unchanged repaints nothing
            // but still proves the server is back, and that is the case this exists for.
            if (_roomsFetchFailed)
            {
                _roomsFetchFailed = false;
                // Only when it is still missing — a standing we already have is not
                // re-fetched, and LoadStandingAsync's own in-flight guard covers the rest.
                if (_cachedStanding == null && _session?.CurrentUser != null)
                {
                    DiagnosticLog.Write(
                        "MultiplayerTab: backend recovered — re-fetching the standing that "
                        + "was lost while it was down");
                    _ = LoadStandingAsync();
                }
            }

            // Quiet auto-refresh: bail out without touching the visual
            // tree when the rooms are exactly what we already rendered.
            // That keeps Join buttons, hover and scroll position intact
            // (a rebuild would reset them) and leaves the PING column to
            // _roomsPingTimer, which updates it in place. A full/manual
            // refresh always re-renders.
            var signature = BuildRoomsSignature(list.Lobbies);
            if (quiet && signature == _lastRenderedRoomsSignature)
                return;

            RoomsListPanel.Children.Clear();
            RoomsErrorBox.Visibility = Visibility.Collapsed;
            _roomPingCells.Clear();
            _roomAgeCells.Clear();

            if (list.Lobbies.Count == 0)
            {
                // One line, not a card: the activity strip and the join-by-code
                // row below stay on screen, which is where someone with no rooms
                // to join actually has something to do.
                RoomsEmptyState.Visibility = Visibility.Visible;
                UpdateRoomsShowingCount(0);
                _lastRenderedRoomsSignature = signature;
                _roomIdsSeeded = true;
                return;
            }
            RoomsEmptyState.Visibility = Visibility.Collapsed;

            // Render each room as a table row, in the user's chosen sort order
            // (server order by default). The signature above is built from the
            // server order so the quiet diff stays stable regardless of sort.
            var ordered = ApplyRoomSort(list.Lobbies);
            int idx = 0;
            foreach (var lobby in ordered)
                RoomsListPanel.Children.Add(BuildRoomCard(lobby, idx++));
            // From here on a room this render didn't know about is genuinely new. Set
            // AFTER the loop, so the first paint teaches the set instead of flashing
            // every row in it.
            _roomIdsSeeded = true;
            _lastRenderedRoomsSignature = signature;
            UpdateRoomsShowingCount(ordered.Count);
        }
        catch (Exception ex)
        {
            // A quiet background poll must not wipe the list the user
            // is looking at over a transient network blip — keep the
            // last good render and just log. Manual / activation
            // refreshes still surface the error banner.
            // Marked whichever way the failure is surfaced: the next success is a
            // recovery regardless of whether this attempt was a quiet poll or a manual one.
            _roomsFetchFailed = true;

            if (quiet)
            {
                DiagnosticLog.Write($"RefreshRoomsList (quiet) failed: {ex.Message}");
            }
            else
            {
                RoomsListPanel.Children.Clear();
                // The error REPLACES the empty state; it does not overlay it. Both are
                // top-aligned siblings in the same cell, so a fetch that failed right after
                // an empty render drew the amber line straight over "no rooms right now".
                RoomsEmptyState.Visibility = Visibility.Collapsed;
                RoomsErrorText.Text = ex.Message;
                RoomsErrorRetry.Content = Strings.Get("MpRoomsErrorRetry");
                RoomsErrorBox.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _isRefreshingList = false;
        }
    }

    /// <summary>
    /// Compact signature of the rooms list as the server returned it,
    /// covering every field <see cref="BuildRoomCard"/> renders (in order)
    /// so the quiet auto-refresh can tell "nothing changed" from "repaint
    /// needed" with a single string compare. Ping is deliberately excluded
    /// — it's your own latency, identical across rows, and owned by
    /// <see cref="RefreshRoomPingCells"/>.
    /// </summary>
    private static string BuildRoomsSignature(IReadOnlyList<LobbySummary> lobbies)
    {
        var sb = new System.Text.StringBuilder(lobbies.Count * 48);
        foreach (var l in lobbies)
        {
            sb.Append(l.Id).Append('|')
              .Append(l.Status).Append('|')
              .Append(l.CurrentPlayers).Append('/').Append(l.MaxPlayers).Append('|')
              .Append(l.IsPrivate ? '1' : '0').Append('|')
              .Append(l.Title).Append('|')
              .Append(l.ModId).Append('|')
              .Append(l.Host.DisplayName).Append('|')
              .Append(l.Host.DiscordUsername).Append('\n');
        }
        return sb.ToString();
    }

    // ---------- Rooms table sorting / footer / header alignment ----------

    /// <summary>
    /// Which column the rooms table is sorted by (None = server order).
    ///
    /// <para>Only the three the reference keeps orderable. Mod, Host and Status were
    /// dropped along with their headers — a value in an enum whose only remaining use is
    /// a switch arm nothing can reach is a claim that the feature exists.</para>
    /// </summary>
    private enum RoomSort { None, Room, Players, Ping }
    private RoomSort _roomsSort = RoomSort.None;
    private bool _roomsSortAsc = true;

    /// <summary>
    /// A column header was clicked: same column toggles asc/desc, a new column
    /// selects it ascending. Re-renders from the cached list (no network).
    /// </summary>
    private void RoomHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        if (!Enum.TryParse<RoomSort>(tag, out var col)) return;
        if (_roomsSort == col) _roomsSortAsc = !_roomsSortAsc;
        else { _roomsSort = col; _roomsSortAsc = true; }
        UpdateSortArrows();
        // Refresh the header line too, or its "sorted by X" suffix lags behind the
        // arrows until the 3 s ping timer next fires.
        UpdateRoomsUpdatedLabel();
        RerenderRoomsFromCache();
    }

    /// <summary>Paint each header's sort arrow: ⇅ idle, ↑/↓ on the active column.</summary>
    private void UpdateSortArrows()
    {
        var active = (Brush)Application.Current.FindResource("MpTextPrimary");
        var idle = (Brush)Application.Current.FindResource("MpTextLabel");
        void Set(TextBlock? arrow, RoomSort col)
        {
            if (arrow == null) return;
            if (_roomsSort == col) { arrow.Text = _roomsSortAsc ? "↑" : "↓"; arrow.Foreground = active; }
            else { arrow.Text = "⇅"; arrow.Foreground = idle; }
        }
        Set(SortArrowRoom, RoomSort.Room);
        Set(SortArrowPlayers, RoomSort.Players);
        Set(SortArrowPing, RoomSort.Ping);
    }

    /// <summary>How many placeholder rows the loading state shows.</summary>
    private const int SkeletonRowCount = 3;

    /// <summary>
    /// One placeholder row for the loading state. The reference asks for three of these
    /// instead of the single italic "cargando…" line that was here, and the reason is
    /// that they occupy the space the rooms are about to take: the list stops jumping
    /// when the answer lands, and an empty result is visibly different from a slow one.
    ///
    /// <para>Deliberately not animated. A shimmer would need an animated brush on a
    /// Border, and the brushes here are frozen DynamicResources — animating one throws,
    /// which is what froze the countdown line once.</para>
    /// </summary>
    private Border BuildRoomSkeletonRow()
    {
        var bar = (Brush)Application.Current.FindResource("MpField");
        var row = new Border
        {
            Style = (Style)FindResource("MpRoomCard"),
            Opacity = 0.55,
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = bar,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 11, 0),
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(new Border
        {
            Height = 10,
            Width = 168,
            CornerRadius = new CornerRadius(3),
            Background = bar,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        lines.Children.Add(new Border
        {
            Height = 8,
            Width = 104,
            CornerRadius = new CornerRadius(3),
            Background = bar,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
        });
        Grid.SetColumn(lines, 1);
        grid.Children.Add(lines);

        var action = new Border
        {
            Width = 84,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            Background = bar,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);

        row.Child = grid;
        return row;
    }

    /// <summary>
    /// The reference's capacity indicator: a fixed row of four bars, filled in
    /// proportion to how full the room is.
    ///
    /// <para>Four regardless of the room's size, so every row's indicator is the same
    /// width and the column stays a column — one bar per SLOT would make a 2-player
    /// room and an 8-player room draw different-width cells. It is a proportion, not a
    /// headcount, which is also why it is paired with the exact "1/8" above it.</para>
    ///
    /// <para>Rounds UP for any non-zero occupancy, so a room with one player in eight
    /// still lights a bar: showing none would read as empty, which is the one thing the
    /// indicator must never say about a room somebody is waiting in.</para>
    /// </summary>
    private static StackPanel BuildCapacityBars(int current, int max)
    {
        const int Segments = 4;
        var filledBrush = (Brush)Application.Current.FindResource("MpAction");
        var emptyBrush = (Brush)Application.Current.FindResource("MpCapacityEmpty");

        int filled = 0;
        if (max > 0 && current > 0)
            filled = Math.Min(Segments, (int)Math.Ceiling(current / (double)max * Segments));

        var bars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
        };
        for (var i = 0; i < Segments; i++)
        {
            bars.Children.Add(new Border
            {
                Width = 9,
                Height = 5,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, i == Segments - 1 ? 0 : 3, 0),
                Background = i < filled ? filledBrush : emptyBrush,
            });
        }
        return bars;
    }

    /// <summary>Mod display name for sorting (falls back to the raw id).</summary>
    private static string ModSortName(LobbySummary l)
    {
        var n = ModRegistry.Find(l.ModId)?.DisplayName;
        return string.IsNullOrWhiteSpace(n) ? l.ModId : n!;
    }

    /// <summary>Host name for sorting (display → Discord username → em-dash).</summary>
    private static string HostSortName(LobbySummary l)
    {
        var n = l.Host.DisplayName;
        if (string.IsNullOrWhiteSpace(n) || n == "-") n = l.Host.DiscordUsername;
        return string.IsNullOrWhiteSpace(n) || n == "-" ? "" : n;
    }

    /// <summary>Rank for status sorting: Waiting &lt; Full &lt; In Game.</summary>
    private static int StatusRank(LobbySummary l)
        => l.Status == "in_game" ? 2 : (l.CurrentPlayers >= l.MaxPlayers ? 1 : 0);

    /// <summary>
    /// Return a copy of the rooms ordered by the active sort. Server order when
    /// <see cref="_roomsSort"/> is None. Stable (OrderBy) so equal keys keep
    /// their relative order; a descending sort reverses the ascending result.
    /// </summary>
    private List<LobbySummary> ApplyRoomSort(IEnumerable<LobbySummary> src)
    {
        var listCopy = src.ToList();
        if (_roomsSort == RoomSort.None) return listCopy;
        IEnumerable<LobbySummary> ordered = _roomsSort switch
        {
            RoomSort.Room => listCopy.OrderBy(l => l.Title, StringComparer.OrdinalIgnoreCase),
            RoomSort.Players => listCopy.OrderBy(l => l.CurrentPlayers).ThenBy(l => l.MaxPlayers),
            RoomSort.Ping => listCopy, // your latency is identical across rows — no-op
            _ => listCopy,
        };
        var result = ordered.ToList();
        if (!_roomsSortAsc) result.Reverse();
        return result;
    }

    /// <summary>
    /// Re-render the rooms list from the cached snapshot applying the current
    /// sort — used by a header click (no network). Leaves the render signature
    /// untouched so a following quiet poll with unchanged data still skips.
    /// </summary>
    private void RerenderRoomsFromCache()
    {
        if (_lastBrowserList == null || _lastBrowserList.Count == 0) return;
        RoomsEmptyState.Visibility = Visibility.Collapsed;
        RoomsListPanel.Children.Clear();
        _roomPingCells.Clear();
        _roomAgeCells.Clear();
        // Filter BEFORE sorting: the sort is stable, so filtering first keeps the
        // surviving rooms in exactly the order they would have had anyway, and the
        // "Showing N" footer then counts what is actually on screen.
        var ordered = ApplyRoomSort(RoomSearchFilter.Apply(_lastBrowserList, _roomsQuery));

        // A search that matches nothing must SAY so. Without this the panel simply
        // renders empty, which is indistinguishable from "there are no rooms" — and
        // the rooms are still there, just filtered out.
        if (ordered.Count == 0)
        {
            RoomsListPanel.Children.Add(new TextBlock
            {
                Text = Strings.Get("MpRoomsNoMatches"),
                Foreground = (Brush)Application.Current.FindResource("MpTextFaint"),
                FontSize = 13,
                Margin = new Thickness(30, 18, 30, 18),
            });
            UpdateRoomsShowingCount(0);
            return;
        }

        int idx = 0;
        foreach (var lobby in ordered)
            RoomsListPanel.Children.Add(BuildRoomCard(lobby, idx++));
        UpdateRoomsShowingCount(ordered.Count);
    }

    /// <summary>
    /// Switches the right panel between Chat and Players. They are two siblings toggled
    /// by visibility rather than one swapped child, so the chat keeps its scroll
    /// position — and its 200-row ring keeps filling — while the user is on Players.
    /// </summary>
    /// <summary>
    /// A quick-reply pill FILLS the composer; it does not send. The three are openers
    /// for a quiet channel, and one that fired on a single click would be a way to spam
    /// the room by accident — the server's own slow-mode would then be the only thing
    /// between a stray double-click and a timeout.
    /// </summary>
    /// <summary>
    /// Enter is live only once something is typed — an empty submit can only produce
    /// "room not available", which reads as a failure rather than as "you typed nothing".
    /// </summary>
    private void JoinByCodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = JoinByCodeBox.Text ?? string.Empty;
        JoinByCodePlaceholder.Visibility =
            text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Never re-enable it while the tab is offline: this fires on every keystroke, and it
        // would undo ApplyOfflineDisable the moment somebody typed into a greyed-out field.
        JoinByCodeButton.IsEnabled = !_offlineMode && text.Trim().Length > 0;
    }

    /// <summary>Return submits, so pasting a code and pressing enter is the whole flow.</summary>
    private void JoinByCodeBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Return) return;
        e.Handled = true;
        SubmitRoomCode();
    }

    private void JoinByCodeButton_Click(object sender, RoutedEventArgs e) => SubmitRoomCode();

    /// <summary>
    /// Joins by a typed room id. Delegates to the SAME path the deep link and the invite
    /// toast use, so a pasted code, a Discord link and an invite cannot diverge in what
    /// they check before letting you in.
    ///
    /// <para>The box is cleared straight away: the id is consumed, and leaving it there
    /// invites a second click that would resolve the room a second time.</para>
    /// </summary>
    private void SubmitRoomCode()
    {
        var code = (JoinByCodeBox.Text ?? string.Empty).Trim();
        if (code.Length == 0) return;
        JoinByCodeBox.Text = string.Empty;
        _ = JoinByLobbyIdAsync(code);
    }

    /// <summary>
    /// When the community payload was last fetched, or MinValue for never.
    ///
    /// <para>This used to be a one-shot bool, which meant a player never saw their own
    /// place on the ladder change without restarting the launcher — and with a team ladder
    /// arriving, the first thing anybody will want to do after a match is look. The window
    /// below is the SERVER's own cache duration, so re-asking inside it would have been
    /// answered from memory anyway; asking less often than that buys nothing.</para>
    /// </summary>
    private DateTime _activityFetchedUtc = DateTime.MinValue;

    /// <summary>Matches the backend's 60 s memo on /stats/community. Change both together.</summary>
    private static readonly TimeSpan ActivityMaxAge = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The community payload, at NO mod scope: everybody, every mod.
    ///
    /// <para>Read by the Rooms strip, the Ranking page and the Profile header - all three of
    /// which are about the whole community. The ladder especially: a rating is per player, not
    /// per mod.</para>
    /// </summary>
    private CommunityStats? _communityStats;

    /// <summary>
    /// The same shape of payload, but scoped to the mod the STATISTICS page is about.
    ///
    /// <para>Separate from <see cref="_communityStats"/> rather than sharing it, because the two
    /// pages want different questions answered and one field could only serve one of them. It
    /// served Statistics, and the strip on Rooms silently inherited that mod.</para>
    /// </summary>
    private CommunityStats? _statsCommunity;

    /// <summary>Throttles <see cref="RefreshStatsCommunityAsync"/>, like its four siblings.</summary>
    private DateTime _statsCommunityFetchedUtc = DateTime.MinValue;

    /// <summary>Which ladder the Ranking subtab is showing.</summary>
    /// <summary>Which table the CLASIFICACIÓN page is showing.</summary>
    /// <summary>
    /// The ladder's two ladders. A third CIVS value lived here; civilizations are a page of
    /// their own now (the STATS subtab), because the ask was to see them BESIDE the ladder and
    /// a segment can only swap the table's contents.
    /// </summary>
    private enum RankingMode { Solo, Team }

    private RankingMode _rankingMode = RankingMode.Solo;

    /// <summary>Kept as a property so every existing reader means what it always did.</summary>
    private bool _rankingShowsTeam => _rankingMode == RankingMode.Team;

    /// <summary>The civilization table, and when it was fetched. Its own 60 s window, matching
    /// the server's memo — see <c>ActivityMaxAge</c>, which exists for the same reason.</summary>
    private Models.Multiplayer.CivStatsResponse? _civStats;
    private DateTime _civStatsFetchedUtc = DateTime.MinValue;
    private bool _civStatsInFlight;

    /// <summary>Whether the recent-matches card is showing EVERYONE's matches or, on a
    /// backend that cannot answer that, the viewer's own. It decides which heading is
    /// used, and it has to survive a language switch — hence a field.</summary>
    private bool _activityRecentIsCommunity;

    /// <summary>
    /// Fills the community-activity strip.
    ///
    /// <para><b>It IS on a timer now, and this comment used to say the opposite</b> ("fetched
    /// once per session, never on a timer") — which described the one-shot bool that
    /// <see cref="_activityFetchedUtc"/> replaced, and stayed here saying so afterwards. The
    /// strip not moving until you left the tab and came back was reported as a bug, and it was
    /// exactly this: every caller was an activation edge.</para>
    ///
    /// <para>The 60-second window below is what makes the timer affordable, so it is load-
    /// bearing in both directions: it stops the 5-second tick that now calls this from becoming
    /// twelve requests a minute, and it matches the server's own memo so a fetch inside it would
    /// have been answered from memory anyway. The caller adds the other half of the budget — it
    /// only asks while the window is in the FOREGROUND. See the rooms-list tick for the
    /// arithmetic; the daily per-IP cap is the constraint, not the per-minute one.</para>
    ///
    /// <para>Stays hidden on an empty history or any failure. A card headed "recent
    /// matches" with nothing under it invites the reading that the matches were lost,
    /// which for someone who has played none is simply wrong.</para>
    /// </summary>
    private bool _activityInFlight;

    private async Task RefreshActivityStripAsync()
    {
        // See the guard on its three siblings: the statistics preview owns these fields while
        // it is showing, and the ranking page reads the same payload, so this returns rather
        // than replacing it.
        if (_demoStats) return;

        if (_session?.CurrentUser == null || ActivityStrip == null) return;
        if (DateTime.UtcNow - _activityFetchedUtc < ActivityMaxAge) return;

        // IN-FLIGHT GUARD, which its four siblings have and this one did not. The stamp below
        // is written AFTER the await on purpose, so a failure does not burn the window — but
        // that also means two callers landing inside the same round trip both pass the check
        // above and both go on to repaint. Entering this subtab is one such pair, since the
        // click handler and RefreshStatsForMod both ask.
        if (_activityInFlight) return;
        _activityInFlight = true;

        try
        {
            // COMMUNITY DATA FIRST, and nothing here may depend on the viewer's own
            // history. It used to: the method opened by fetching the caller's matches and
            // returned when there were none, so a player who had not played saw an empty
            // panel — in a strip headed "community activity", whose other two cards are
            // about everyone and had data all along. They were never even requested.
            Models.Multiplayer.CommunityStats? stats = null;
            try
            {
                // NO MOD SCOPE, and that is the fix. This asked with StatsModId() - the mod
                // picked on the STATISTICS subtab - so choosing a mod nobody plays there emptied
                // a strip headed "Community activity" on a different page: the peak-hours card
                // vanished and the totals dropped to "0 matches". It was wrong even untouched,
                // because StatsModId() falls back to the mod being PLAYED, so those totals were
                // never the community's. Unscoped is what the heading has always promised, and
                // what the rooms list underneath it already shows.
                //
                // No `mode` either: one payload carries BOTH ladders (Leaderboard and
                // LeaderboardTeam), so the Ranking page's 1v1/Teams toggle does not need it.
                stats = await _session.Api.GetCommunityStatsAsync();
            }
            catch (Services.Multiplayer.LobbyApiException ex)
            {
                // TOLD APART, because the card's absence used to mean five different things
                // at once. A refusal we understand is remembered so the card can say the
                // launcher could not load it, instead of looking exactly like a quiet
                // community.
                _activityError = ex.Code == "rate_limited"
                    ? ActivityFailure.RateLimited
                    : ActivityFailure.Failed;
                DiagnosticLog.Write($"Community stats: fetch failed ({ex.Code}): {ex.Message}");
            }
            catch (Exception ex)
            {
                _activityError = ActivityFailure.Failed;
                DiagnosticLog.Write($"Community stats: fetch failed: {ex.Message}");
            }

            // Cleared only on an answer. Anything else leaves the last failure standing, so a
            // card that could not load keeps saying so rather than blinking between states.
            if (stats != null) _activityError = ActivityFailure.None;

            // Stamped only once an answer is IN HAND. It used to be stamped before the await,
            // so a failed request burned the whole 60-second window and the strip stayed dead
            // for a minute however many times the user asked — the one state where retrying
            // is exactly the right instinct was the one where it did nothing.
            _activityFetchedUtc = DateTime.UtcNow;

            // Kept for the Ranking subtab, which draws the FULL table from the very same
            // payload — one request feeds both, which is why the limit asks for the server's
            // maximum rather than the three the strip shows.
            // The preview may have started while this request was already in flight, so
            // the guard at the top of the method is not enough: the check has to happen
            // where the field is WRITTEN. Without it the demo comes up, a reply from a
            // second ago lands on top of it, and the page shows one mod's table over
            // another payload's totals.
            if (_demoStats) return;
            _communityStats = stats;
            if (_activeSubtab == Subtab.Ranking) RenderRanking();
            // AND Statistics, which is where this payload's maps and head counts are drawn.
            // SubtabStats_Click is what kicks this fetch off, and for one build it was the
            // only subtab that never heard the answer: the map table stayed empty until the
            // user left the tab and came back, which reads as "no data" rather than as "not
            // yet". Three ifs and no else-if, for the reason the next comment gives.
            if (_activeSubtab == Subtab.Stats) RenderStatsTab();
            // The profile reads this payload too — for the ladder's entry bar, the size of the
            // league and the viewer's own place in it — so it has to repaint when it lands or
            // those three stay blank until the tab is left and re-entered.
            //
            // TWO ifs, not an else-if. That else was only ever safe because the subtabs are
            // mutually exclusive — and the profile is a WINDOW now, so it can be open OVER the
            // ranking page. Whichever lost the else would silently keep stale numbers.
            if (_profileWindow != null) RenderProfileTab();

            // The one thing on this page that still needs the network, and only on the
            // legacy branch. Fetched HERE so that every paint after it - including the one a
            // language change asks for - is pure.
            await CacheFallbackMatchesAsync(stats);
            RenderActivityStrip();
        }
        catch (Exception ex)
        {
            // Best-effort decoration: it must never be why the rooms list looks broken.
            DiagnosticLog.Write($"Activity strip: {ex.Message}");
        }
        finally
        {
            // A finally and not a line at the end: the demo guard returns from INSIDE the try,
            // and a flag left set there would silence this fetch for the rest of the session.
            _activityInFlight = false;
        }
    }

    /// <summary>
    /// Draw the whole activity strip from what is already in hand.
    ///
    /// <para><b>Separate from the fetch, and that separation is the fix.</b> This used to be
    /// the tail of <see cref="RefreshActivityStripAsync"/>, behind its await, so the only way
    /// to repaint the strip was to ask the server - and that request is capped at one a minute
    /// (<c>ActivityMaxAge</c>) and only fires while the window is in the foreground. So a
    /// language change had nothing it could call: the headings switched, the SENTENCES did not,
    /// and they caught up whenever the next poll happened to land. Calling the async one would
    /// not have helped either - inside the 60-second window it returns before painting anything.
    /// </para>
    ///
    /// <para>Reads <see cref="_communityStats"/> and <see cref="_activityFallbackMatches"/>,
    /// both already cached, so it costs nothing and can be called as often as anything wants.
    /// </para>
    /// </summary>
    private void RenderActivityStrip()
    {
        if (ActivityStrip == null) return;

        // The matches card is about its list again — the totals it used to footer are in
        // the strip's header row now, so this card is shown for its own content alone.
        var recentDrew = FillRecentMatches(_communityStats);
        ActivityRecentCard.Visibility = recentDrew ? Visibility.Visible : Visibility.Collapsed;

        // Still counts towards "is there anything to show": a header line carrying the
        // community's numbers is content even when all three cards come up empty.
        var any = recentDrew;
        any |= FillCommunityTotals(_communityStats);
        any |= FillCommunityMiddle(_communityStats);
        any |= FillPeakHours(_communityStats);

        LayOutActivityColumns();
        ActivityStrip.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Give every card that is showing a third of the width, and every card that is not,
    /// none of it — then draw a divider only BETWEEN two cards that are actually there.
    ///
    /// <para>Both halves are needed and they fail differently. A star column keeps its
    /// share whatever its child does, so hiding a card on its own reserved a blank third;
    /// and the dividers were unconditional, so that blank third came framed by two rules.
    /// Together they are why the middle of this strip read as broken rather than as
    /// empty.</para>
    /// </summary>
    private void LayOutActivityColumns()
    {
        var star = new GridLength(1, GridUnitType.Star);
        var none = new GridLength(0);

        var recent = ActivityRecentCard.Visibility == Visibility.Visible;
        var middle = ActivityMiddleCard.Visibility == Visibility.Visible;
        var peak = ActivityPeakCard.Visibility == Visibility.Visible;

        ActivityColPeak.Width = peak ? star : none;
        ActivityColRecent.Width = recent ? star : none;
        ActivityColMiddle.Width = middle ? star : none;

        // The GAPS are what the vertical rules used to be, and they collapse for the same
        // reason: a gap is only a gap when there is something on both sides of it. Left over
        // beside a hidden card it is a stray inset that pushes the survivors off-centre.
        // Note the left gap asks "is there anything AFTER the peak card", which is the recent
        // card or — when that one is absent too — the ranking; it is not simply "peak && recent".
        ActivityGapLeft.Width = peak && (recent || middle) ? new GridLength(ActivityCardGap) : none;
        ActivityGapRight.Width = recent && middle ? new GridLength(ActivityCardGap) : none;
    }

    /// <summary>The handoff's 11-px gutter between the three activity cards.</summary>
    private const double ActivityCardGap = 11;

    /// <summary>
    /// The recent-matches card: everyone's matches, or the viewer's own as a fallback.
    ///
    /// <para>The community list is what the strip's own heading promises, and it is the
    /// only version a player who has never played can learn anything from. A backend that
    /// predates the field sends none, and the card then shows the viewer's history under
    /// its old heading — byte for byte what it did before.</para>
    /// </summary>
    /// <summary>
    /// The viewer's own matches, kept so the card can be redrawn without asking again.
    ///
    /// <para>Only ever filled on the compatibility branch below. It exists because the strip
    /// has to be repaintable from memory - a language change repaints it - and this was the one
    /// part of it that reached for the network.</para>
    /// </summary>
    private List<Models.Multiplayer.MatchHistoryRow>? _activityFallbackMatches;

    /// <summary>
    /// Fetch the one thing the recent-matches card cannot get from the community payload.
    ///
    /// <para>A backend that predates <c>recent_matches</c> sends none, and the card then falls
    /// back to the viewer's own history. That request lives here, on its own, so that
    /// <see cref="FillRecentMatches"/> - and therefore the whole strip - is pure.</para>
    /// </summary>
    private async Task CacheFallbackMatchesAsync(Models.Multiplayer.CommunityStats? stats)
    {
        if (CommunityStatsView.RecentMatches(stats).Count > 0) return;
        try
        {
            var resp = await _session!.Api.GetHistoryAsync(_session.CurrentUser!.Id);
            _activityFallbackMatches = resp?.Matches;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Activity strip: history fetch failed: {ex.Message}");
            _activityFallbackMatches = null;
        }
    }

    private bool FillRecentMatches(Models.Multiplayer.CommunityStats? stats)
    {
        // Cleared for BOTH branches, before either builds anything: the rows below are about to
        // be replaced, and a list still holding the previous ones would tick TextBlocks that are
        // no longer in the tree — every minute, for the life of the session. Same reason
        // _roomAgeCells is cleared at each of its two re-render sites.
        _activityAgeCells.Clear();

        var community = CommunityStatsView.RecentMatches(stats);
        if (community.Count > 0)
        {
            _activityRecentIsCommunity = true;
            ActivityRecentTitle.Text = Strings.Get("MpActivityRecentCommunityTitle");
            ActivityRecentList.Children.Clear();
            foreach (var m in community.Take(3))
                ActivityRecentList.Children.Add(BuildCommunityMatchRow(m, _activityAgeCells));
            ActivityRecentCard.Visibility = Visibility.Visible;
            return true;
        }

        _activityRecentIsCommunity = false;
        ActivityRecentTitle.Text = Strings.Get("MpActivityRecentTitle");

        var rows = _activityFallbackMatches;
        if (rows == null || rows.Count == 0)
        {
            ActivityRecentCard.Visibility = Visibility.Collapsed;
            return false;
        }

        ActivityRecentList.Children.Clear();
        // Registers nothing in _activityAgeCells, and has nothing to register: this is the
        // fallback for a backend too old to send recent_matches, and its row carries no age
        // at all — just mod, map and whether the match counted.
        foreach (var m in rows.Take(3))
            ActivityRecentList.Children.Add(BuildActivityMatchRow(m));
        ActivityRecentCard.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>
    /// The middle third: the community's numbers, and under them the ladder — or, while
    /// nobody qualifies for it, what it takes to get in.
    /// </summary>
    /// <summary>
    /// The community's numbers, on ONE line in the strip's header row.
    ///
    /// <para>They were a footer under the recent matches, behind a 1-px rule: first three
    /// stacked one-fact rows under a "COMMUNITY" heading, then two rows without it. Either way
    /// they made that card the tallest of the three — and the cards share a grid row, so the
    /// tallest card IS the strip's height. Up in the header they cost nothing at all: that row
    /// already existed and ran empty from the title to the far edge.</para>
    ///
    /// <para>Each window travels with its own figure because they differ — matches over 30
    /// days, players over 7 — so the single "last 30 days" label this replaced could only ever
    /// have restated one of them. The map goes last: the line trims from the right, so the
    /// segment lost first on a narrow window is the one worth least.</para>
    /// </summary>
    private bool FillCommunityTotals(Models.Multiplayer.CommunityStats? stats)
    {
        var totals = CommunityStatsView.Totals(stats);
        if (totals == null)
        {
            ActivityStripTotals.Inlines.Clear();
            return false;
        }

        // The FIGURES are lifted out of the sentence and painted bright, the same treatment the
        // peak card's line gives its two hours a few inches to the left. The line as a whole sat
        // in MpTextDim, the faintest rung of the ramp — measured against the tab background it
        // was 4.84:1, and the numbers are the only part of it anybody reads. The words are
        // MpTextBody now (10.17:1) and the figures MpTextHeading SemiBold (15.63:1).
        ActivityStripTotals.Inlines.Clear();
        foreach (var run in BuildEmphasisRuns(
                     Strings.Get("MpActivityTotalsCounts"),
                     totals.Matches.ToString(),
                     totals.WindowDays.ToString(),
                     totals.Players.ToString(),
                     totals.PlayersWindowDays.ToString()))
        {
            ActivityStripTotals.Inlines.Add(run);
        }

        // Appended only when there IS one: a trailing separator states nothing and spends the
        // width the counts before it need. Plain, because a map name is not a figure.
        //
        // LABELLED, not bare. It shipped for one round as just the name, and a proper noun
        // arriving after two labelled figures does not announce itself as a map — reported the
        // same day. The label costs width only where there is width to spare: this line trims
        // from the right and the map is last, so on a narrow window the two are lost together,
        // which is the right order to lose them in.
        if (!string.IsNullOrWhiteSpace(totals.TopMap))
        {
            ActivityStripTotals.Inlines.Add(new System.Windows.Documents.Run(
                " · " + Strings.Format(
                    "MpActivityTotalsTopMap", totals.TopMap!.Replace('_', ' '))));
        }

        return true;
    }

    /// <summary>The ladder, or why it is empty. Its card holds nothing else now.</summary>
    private bool FillCommunityMiddle(Models.Multiplayer.CommunityStats? stats)
    {
        var shown = false;
        var rows = CommunityStatsView.Rows(stats);
        var required = CommunityStatsView.RequiredDecided(stats);
        if (rows.Count > 0)
        {
            ActivityRankingList.Children.Clear();
            var meId = _session?.CurrentUser?.Id;
            foreach (var row in rows.Take(5))
            {
                var isMe = !string.IsNullOrEmpty(meId)
                    && string.Equals(row.UserId, meId, StringComparison.Ordinal);
                ActivityRankingList.Children.Add(BuildStripLeaderboardRow(row, isMe));
            }
            ActivityRankingSeeAll.Visibility = Visibility.Visible;
            ActivityRankingList.Visibility = Visibility.Visible;
            ActivityRankingEmpty.Visibility = Visibility.Collapsed;
            ActivityRankingCard.Visibility = Visibility.Visible;
            shown = true;
        }
        else if (required.HasValue)
        {
            // RequiredDecided gates this AND fills it: there is an entry bar again, so the
            // sentence names it, and the figure is the SERVER's rather than a literal here —
            // those two have disagreed before, and this text is exactly where a player would
            // have read the wrong one. The title stays; the link goes, because there is
            // nothing more to see and the full table would be a second empty list.
            ActivityRankingSeeAll.Visibility = Visibility.Collapsed;
            ActivityRankingList.Visibility = Visibility.Collapsed;
            ActivityRankingEmpty.Text = Strings.Format("MpActivityRankingEmpty", required.Value);
            ActivityRankingEmpty.Visibility = Visibility.Visible;
            ActivityRankingCard.Visibility = Visibility.Visible;
            shown = true;
        }
        else ActivityRankingCard.Visibility = Visibility.Collapsed;

        ActivityMiddleCard.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        return shown;
    }

    /// <summary>The peak-hours card, unchanged but for where its numbers are read.</summary>
    /// <summary>Why the peak card has no figure to draw. Absence used to mean all of these.</summary>
    private enum ActivityFailure
    {
        /// <summary>Nothing went wrong \u2014 either it loaded, or it has not been asked yet.</summary>
        None,

        /// <summary>The request was refused or never arrived.</summary>
        Failed,

        /// <summary>
        /// 429. Its own value because its own answer: the quota is per IP and per DAY, so
        /// "try again" is wrong advice \u2014 two launchers behind one address can spend it by
        /// lunchtime, and the honest thing is to say it will be back rather than to blink.
        /// </summary>
        RateLimited,
    }

    /// <summary>The last thing that went wrong fetching the community payload, or None.</summary>
    private ActivityFailure _activityError = ActivityFailure.None;

    private bool FillPeakHours(Models.Multiplayer.CommunityStats? stats)
    {
        var activity = stats?.Activity;
        if (activity == null || ActivityPeakBars == null)
        {
            // NOT the same as a quiet community, and no longer drawn the same. With nothing
            // in hand the card says the launcher could not read it; the bars and the sentence
            // stay hidden, because inventing either would be worse than the blank.
            return ShowPeakUnavailable();
        }

        var utc = new int[24];
        foreach (var h in activity.Hours)
            if (h.Hour >= 0 && h.Hour < 24) utc[h.Hour] = h.Count;

        var local = CommunityStatsView.ToLocalHours(
            utc, TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow));
        // A three-hour stretch, not the single tallest bar — see PeakWindow, where the
        // measured tie that motivated it is written down.
        var peak = CommunityStatsView.PeakWindow(local, activity.Total);
        if (!peak.HasValue)
        {
            // THE ONE HONEST SILENCE. Below MinSampleRooms there is no busiest hour to
            // name - saying one from four rooms would be inventing a pattern - so this
            // stays hidden, exactly as before. Everything else that used to land here now
            // goes through ShowPeakUnavailable instead.
            ActivityPeakCard.Visibility = Visibility.Collapsed;
            return false;
        }

        // An answer arrived, so no stale failure line survives it.
        ActivityPeakNotice.Visibility = Visibility.Collapsed;
        ActivityPeakBars.Visibility = Visibility.Visible;
        ActivityPeakLine.Visibility = Visibility.Visible;
        ActivityPeakSubtitle.Visibility = Visibility.Visible;

        // The handoff puts the window INSIDE the sentence, in bold, instead of shouting it
        // above as a 16-px headline over its own echo. Built from Runs so the two hours can
        // carry the emphasis while the sentence around them stays translatable as one string.
        var from = Strings.Format("MpActivityPeakHour", peak.Value);
        var to = Strings.Format(
            "MpActivityPeakHour", (peak.Value + CommunityStatsView.PeakWindowHours) % 24);
        ActivityPeakLine.Inlines.Clear();
        foreach (var run in BuildEmphasisRuns(Strings.Get("MpActivityPeakLine"), from, to))
            ActivityPeakLine.Inlines.Add(run);

        ActivityPeakSubtitle.Text = Strings.Format(
            "MpActivityPeakSample", activity.Total, activity.WindowDays);
        DrawPeakBars(local, peak.Value);
        ActivityPeakCard.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>
    /// The card, kept, saying it could not be read.
    ///
    /// <para><b>Only for a failure, never for a quiet community.</b> Too few rooms is a fact
    /// about the community and the card stays away for it; a request that failed or was
    /// rate-limited is the launcher's problem, and hiding that made the two
    /// indistinguishable \u2014 which is exactly the report: "sometimes it takes ages, or it
    /// just never loads", with no way to tell which.</para>
    /// </summary>
    private bool ShowPeakUnavailable()
    {
        if (ActivityPeakCard == null || ActivityPeakNotice == null) return false;
        if (_activityError == ActivityFailure.None)
        {
            // Never asked yet, or asked and told there is nothing. Neither is worth a line.
            ActivityPeakCard.Visibility = Visibility.Collapsed;
            return false;
        }

        ActivityPeakNotice.Text = Strings.Get(_activityError == ActivityFailure.RateLimited
            ? "MpActivityPeakRateLimited"
            : "MpActivityPeakUnavailable");
        ActivityPeakNotice.Visibility = Visibility.Visible;

        // The furniture goes, not the card: bars drawn from no data would be a shape the
        // reader would take for a measurement.
        if (ActivityPeakBars != null) ActivityPeakBars.Visibility = Visibility.Collapsed;
        if (ActivityPeakLine != null) ActivityPeakLine.Visibility = Visibility.Collapsed;
        if (ActivityPeakSubtitle != null) ActivityPeakSubtitle.Visibility = Visibility.Collapsed;
        ActivityPeakCard.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>
    /// One community match: who beat whom, and under it the mod, the map and how long ago.
    ///
    /// <para>The sentence is only written for a two-player match with a readable winner —
    /// <see cref="CommunityStatsView.Describe"/> refuses everything else, and that is most
    /// stored matches. Those keep the old shape: the mod and the map, and "didn't count"
    /// said out loud rather than implied by a grey dot nobody can interpret.</para>
    /// </summary>
    /// <param name="ageCells">
    /// Where to register the "N ago" label so it can be ticked in place later. Optional, and
    /// null from the test that builds a row on its own — a row with nowhere to register simply
    /// keeps the age it was born with, which is what every row did before.
    /// </param>
    internal static UIElement BuildCommunityMatchRow(
        Models.Multiplayer.CommunityMatch m,
        System.Collections.Generic.List<(TextBlock Text, DateTime ReportedUtc)>? ageCells = null)
    {
        var line = CommunityStatsView.Describe(m);

        // Dot, text, and the age hard RIGHT — the handoff's shape. The age used to be the last
        // separated segment of the line below, which made a list of matches read as a stack of
        // paragraphs: nothing lined up, so the eye had no column to run down.
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(WithColumn(new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = (Brush)Application.Current.FindResource(line.Decided ? "MpOk" : "MpTextFaint"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 8, 0),
        }, 0));

        var mod = ResolveModDisplayName(m.ModId);
        var map = string.IsNullOrWhiteSpace(m.MapName) ? null : m.MapName!.Replace('_', ' ');
        var reportedUtc = Services.RoomAgeFormat.ParseCreatedUtc(m.ReportedAt);
        var ago = AgoFrom(reportedUtc);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = line.Decided
                ? Strings.Format("MpActivityWon", line.Winner!, line.Loser!)
                : Join(mod, map),
            Foreground = (Brush)Application.Current.FindResource(
                line.Decided ? "MpTextPrimary" : "MpTextFaint"),
            FontSize = (double)Application.Current.FindResource("MpActivityBodySize"),
            FontWeight = line.Decided ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        // The second line keeps the mod and the map — the handoff has no such line only because
        // its sample data carried neither, and they are real information about the match.
        // The matchup goes LAST because this line trims from the right: on a narrow card the
        // segment lost first should be the one worth least, which is the same order the totals
        // line puts its map in.
        var matchup = line.HasMatchup
            ? Strings.Format("MpResultCivMatchup", line.WinnerCiv!, line.LoserCiv!)
            : null;
        var under = line.Decided
            ? Join(mod, map, matchup)
            : Strings.Get("MpActivityNotCounted");
        if (!string.IsNullOrWhiteSpace(under))
        {
            stack.Children.Add(new TextBlock
            {
                Text = under,
                Foreground = (Brush)Application.Current.FindResource("MpTextFaint"),
                FontSize = (double)Application.Current.FindResource("MpActivityTitleSize"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }
        grid.Children.Add(WithColumn(stack, 1));

        // Null when the stamp was unusable, and then no cell at all rather than a blank one —
        // an empty column would still claim its width and pull the sentence short.
        if (!string.IsNullOrWhiteSpace(ago))
        {
            var agoText = new TextBlock
            {
                Text = ago,
                Foreground = (Brush)Application.Current.FindResource("MpTextFaint"),
                FontSize = (double)Application.Current.FindResource("MpActivityTitleSize"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 0, 0, 0),
            };
            grid.Children.Add(WithColumn(agoText, 2));
            if (ageCells != null && reportedUtc.HasValue) ageCells.Add((agoText, reportedUtc.Value));
        }
        return grid;
    }

    /// <summary>Join the parts that exist with the separator this tab uses everywhere.</summary>
    private static string Join(params string?[] parts) =>
        string.Join(" \u00b7 ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>
    /// "2 h ago", or null when the timestamp is unusable.
    ///
    /// <para>Null rather than "just now": a missing or unparsable stamp is not a recent
    /// match, and the segment is simply dropped — the same rule the rest of the meta line
    /// follows.</para>
    /// </summary>
    private static string? AgoFrom(DateTime? when)
    {
        if (when == null) return null;
        var elapsed = DateTime.UtcNow - when.Value;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return Strings.Format("MpActivityAgo", Services.RoomAgeFormat.Coarse(elapsed));
    }

    /// <summary>
    /// The histogram: ONE BAR PER HOUR, twenty-four of them, tallest normalised to full height.
    ///
    /// <para>It briefly became eight three-hour bars, on the reasoning that a bar should be the
    /// same unit as the answer. The resolution is what was actually wanted: three hours per bar
    /// says "evening", twenty-four says which hour, and the sentence underneath already states
    /// the stretch in words. Reverted on request — and an hour axis under the bars was tried
    /// after that and reverted too: at ~8 px per column a label is clipped mid-glyph ("0(" for
    /// "00"), because a TextBlock measured against less room than it needs cuts the text.</para>
    ///
    /// <para><b>What is NOT reverted is which bar lights up.</b> The 24-bar version this returns
    /// to highlighted <c>DateTime.Now.Hour</c> — the current hour, which has nothing to do with
    /// the sentence beside it: a bright bar answering a question nobody asked, next to a sentence
    /// answering one it did not mark. The peak window's own hours are lit instead, so the picture
    /// and the sentence say the same thing.</para>
    ///
    /// <para>Every bar is the one blue at an opacity that follows its value, per the handoff's
    /// 0.35-0.6 range, with the peak hours solid. Two channels for one number is deliberate: the
    /// short bars of a quiet community are only a few pixels tall, and the tint is what keeps
    /// them readable as "some" rather than "none".</para>
    /// </summary>
    private void DrawPeakBars(int[] local, int peakStartHour)
    {
        ActivityPeakBars.Children.Clear();

        var max = 0;
        foreach (var c in local) if (c > max) max = c;
        if (max <= 0) return;

        var accent = (Color)((SolidColorBrush)FindResource("MpAction")).Color;

        for (var h = 0; h < 24; h++)
        {
            var frac = local[h] / (double)max;

            // The lit hours are the window the sentence names, wrapping midnight with it.
            var inPeak = false;
            for (var i = 0; i < CommunityStatsView.PeakWindowHours; i++)
                if ((peakStartHour + i) % 24 == h) { inPeak = true; break; }

            Brush fill;
            if (inPeak)
            {
                fill = (Brush)FindResource("MpAction");
            }
            else
            {
                var alpha = (byte)Math.Round(255 * (0.35 + 0.25 * frac));
                var tint = new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B));
                tint.Freeze();
                fill = tint;
            }

            ActivityPeakBars.Children.Add(new Border
            {
                Background = fill,
                CornerRadius = new CornerRadius(1),
                Margin = new Thickness(1, 0, 1, 0),
                // Bottom-aligned so the bars grow from a common baseline, and a minimum sliver
                // for a non-empty hour so "one room" is visible at all.
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = local[h] == 0 ? 1 : Math.Max(3, frac * 30),
                // KNOWN NOT TO FIRE IN PRACTICE, and left here deliberately rather than deleted.
                // A Border is hit-testable only over the rectangle it paints, so on real data this
                // target is a few pixels tall; wrapping each hour in a full-height transparent
                // host was tried and MEASURED (the target went 12x3 -> 12x34) and the maintainer
                // still got nothing on his machine, so it was reverted on his instruction. What
                // was ruled out with evidence: an ancestor with IsHitTestVisible=False, an
                // ancestor tooltip winning, the ScrollViewer, the app-wide ToolTip style,
                // TooltipHelper.Wrap, and MpAlertOverlay's scrim (it removes itself from the
                // tree). The remaining suspect is WIDTH: a column is ~8-12 px and the tooltip
                // wants the pointer still inside it for the 400 ms default delay. Wider columns
                // mean fewer bars, and 24 bars is what was asked for — so do not "fix" this by
                // bucketing hours again; that was proposed, built and rejected.
            });
        }
    }

    /// <summary>
    /// Splits a two-placeholder template into runs, emphasising what goes in the holes.
    ///
    /// <para>The alternative — three separate strings glued together — puts the word order in
    /// the code, where a translator cannot reach it. Here the sentence stays one entry and only
    /// its two values are picked out.</para>
    /// </summary>
    private static System.Collections.Generic.IEnumerable<System.Windows.Documents.Run> BuildEmphasisRuns(
        string template, params string[] values)
    {
        // As many holes as the caller passes values for. It took exactly two while the peak
        // sentence was its only caller; the community totals have four, and they are the reason
        // this generalised rather than gaining a second copy beside it.
        var markers = new string[values.Length];
        for (var i = 0; i < values.Length; i++) markers[i] = "{" + i + "}";
        var parts = template.Split(markers, StringSplitOptions.None);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                yield return new System.Windows.Documents.Run(parts[i]);
            if (i < values.Length && i < parts.Length - 1)
                yield return new System.Windows.Documents.Run(values[i])
                {
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)Application.Current.FindResource("MpTextHeading"),
                };
        }
    }

    /// <summary>One ladder row: rank, player, rating, decided games, win rate.</summary>
    /// <summary>
    /// One ladder row as the STRIP shows it: rank, avatar, name, rating — the handoff's shape.
    ///
    /// <para><b>A separate method from <see cref="BuildLeaderboardRow"/> on purpose.</b> That one
    /// is shared with the RANKING subtab, where the whole table lives and the decided count and
    /// win rate belong; here there is room for four fields and a face, and the columns those two
    /// extra numbers need are what left no width for a name. The strip is a teaser, so it shows
    /// who and how much and sends you to the table for the rest.</para>
    ///
    /// <para>The viewer's own row is tinted rather than bolded: the whole point of finding
    /// yourself in a list is a glance, and weight alone does not survive one.</para>
    /// </summary>
    private UIElement BuildStripLeaderboardRow(Models.Multiplayer.LeaderboardRow row, bool isMe)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = string.IsNullOrEmpty(row.DisplayName) ? row.DiscordUsername : row.DisplayName;

        var rank = new TextBlock
        {
            Text = row.Rank.ToString(),
            Foreground = (Brush)FindResource(isMe ? "MpActionText" : "MpTextFaint"),
            FontSize = (double)FindResource("MpActivityBodySize"),
            FontWeight = FontWeights.SemiBold,
            MinWidth = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(rank, 0);
        grid.Children.Add(rank);

        var avatar = BuildAvatarDisc(name, row.AvatarUrl, 18);
        avatar.Margin = new Thickness(9, 0, 9, 0);
        avatar.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(avatar, 1);
        grid.Children.Add(avatar);

        var who = new TextBlock
        {
            Text = isMe ? Strings.Get("MpActivityYou") : name,
            Foreground = (Brush)FindResource(isMe ? "MpTextHeading" : "MpTextBody"),
            FontSize = (double)FindResource("MpActivityBodySize"),
            FontWeight = isMe ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(who, 2);
        grid.Children.Add(who);

        // RANK, FACE, NAME, RATING — and nothing else. A match-count column was added here to
        // explain the order (the table is ranked by rating MINUS its deviation, so the numbers do
        // not descend) and removed again: a bare "13" beside an ELO, with no header over it, was
        // read as an unexplained second number. The full table behind "See all" has real column
        // headers — DECIDED and win % — which is where a figure like that explains itself.
        //
        // The cost is real and is accepted: nothing in this card now says why a higher rating can
        // sit below a lower one. It does not show today (two players, both orderings agree) and
        // will the day somebody arrives with a high rating and few matches.
        //
        // NO "?" HERE EITHER, and that is not an oversight. A provisional marker keyed on rd > 110 marked
        // every single row: measured in this repo, the deviation does not cross 110 until about
        // the fourteenth rated match and NEVER for a player who keeps winning, because a rising
        // rating re-inflates it as fast as the update shrinks it — so the community's best player
        // would have carried "provisional" for ever. A mark on 100% of the rows distinguishes
        // nothing anyway, and the match count in the column to the left is the honest version of
        // the same explanation. (MatchOutcomeView.IsProvisional stays: on the result card and the
        // profile it answers a different question, about ONE player rather than a ranking.)
        var rating = new TextBlock
        {
            Text = ((int)Math.Round(row.Rating)).ToString(),
            Foreground = (Brush)FindResource("MpTextHeading"),
            FontSize = (double)FindResource("MpActivityBodySize"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(rating, 3);
        grid.Children.Add(rating);

        // The tint bleeds OUT to the card's padding edge — negative margin against its own
        // padding — so the highlighted row reads as a band across the card instead of a
        // floating pill, and none of the four columns shifts when it appears.
        return new Border
        {
            Child = grid,
            Background = isMe ? (Brush)FindResource("MpActivityOwnRow") : null,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(-6, 0, -6, 3),
        };
    }

    /// <summary>
    /// One row of the Clasificación table.
    ///
    /// <para>Six columns, shaped by <see cref="Services.Multiplayer.RankingTableLayout"/> — the
    /// same definition the header reads, which is what keeps the two aligned.</para>
    ///
    /// <para><b>The rating carries a bar</b>, and it is not decoration: the table is ordered by
    /// the CONSERVATIVE rating (rating minus twice its deviation), so the printed numbers do not
    /// descend down the page and a reader comparing two adjacent rows can be left thinking the
    /// table is broken. The bar shows the distance between places without contradicting the
    /// number beside it.</para>
    ///
    /// <para><b>There is deliberately no PROVISIONAL tag here, and the reference asks for one.</b>
    /// It was measured in this repo and it marks EVERYBODY: the deviation does not fall under
    /// 110 until roughly the fourteenth rated match, and never at all for a player who keeps
    /// winning, because a rising rating re-inflates it as fast as the update shrinks it — so the
    /// community's best player would wear "provisional" for ever. Every row marked distinguishes
    /// nothing. The DECIDED and RECORD columns are the honest version of the same caveat, which
    /// is most of why RECORD was added. (The tag stays on the PROFILE, where it answers a
    /// different question — whether THIS player is on the ladder yet — and where it can be
    /// false.)</para>
    /// </summary>
    /// <remarks>
    /// <c>internal</c> so <c>DialogXamlTests</c> can build the real row rather than a stand-in.
    /// A code-built card is checked by nothing at compile time, and this one is only ever
    /// drawn once somebody has signed in and opened a subtab the smoke-launch never reaches.
    /// </remarks>
    internal UIElement BuildLeaderboardRow(
        Models.Multiplayer.LeaderboardRow row, double lowest, double highest, bool isMe)
    {
        var grid = BuildRankingGrid();
        grid.Margin = new Thickness(14, 0, 14, 0);
        grid.MinHeight = 42;

        var name = string.IsNullOrEmpty(row.DisplayName) ? row.DiscordUsername : row.DisplayName;

        // First place in gold, and only the number. The launcher's own accent would be louder
        // than the row it sits in; this is the handoff's paler one.
        var rank = new TextBlock
        {
            Text = row.Rank.ToString(),
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("DisplayFont"),
            FontSize = (double)Application.Current.FindResource("MpBodySize"),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource(
                row.Rank == 1 ? "MpRankGold" : isMe ? "MpLinkText" : "MpTextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(rank, 0);
        grid.Children.Add(rank);

        var who = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            // PLAYER is a star column now, so its trailing gap cannot ride on the column
            // width the way a fixed column's does — see BuildRankingGrid.
            Margin = new Thickness(0, 0, Services.Multiplayer.RankingTableLayout.ColumnGap, 0),
        };
        var avatar = BuildAvatarDisc(name, row.AvatarUrl, 24);
        avatar.VerticalAlignment = VerticalAlignment.Center;
        who.Children.Add(avatar);
        who.Children.Add(new TextBlock
        {
            Text = name,
            Margin = new Thickness(9, 0, 0, 0),
            Foreground = (Brush)Application.Current.FindResource(
                isMe ? "MpTextHeading" : "MpTextPrimary"),
            FontSize = (double)Application.Current.FindResource("MpBodySize"),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(who, 1);
        grid.Children.Add(who);

        // Rating: the number, then the bar taking what is left of the column.
        var ratingCell = new Grid { VerticalAlignment = VerticalAlignment.Center };
        ratingCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ratingCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var ratingText = new TextBlock
        {
            Text = ((int)Math.Round(row.Rating)).ToString(),
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont"),
            FontSize = (double)Application.Current.FindResource("MpBodySize"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("MpTextHeading"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(ratingText, 0);
        ratingCell.Children.Add(ratingText);

        var track = new Border
        {
            Height = 4,
            Margin = new Thickness(9, 0, 12, 0),
            CornerRadius = new CornerRadius(2),
            Background = (Brush)Application.Current.FindResource("MpBarTrack"),
            VerticalAlignment = VerticalAlignment.Center,
            // The fill is a child sized by a star/star pair rather than by a width in pixels,
            // so the bar re-proportions with the column instead of needing a measured width.
            Child = BuildRatingBar(
                Services.Multiplayer.RankingTableLayout.BarFraction(row.Rating, lowest, highest),
                isMe ? "MpLinkText" : "MpAction"),
        };
        Grid.SetColumn(track, 1);
        ratingCell.Children.Add(track);

        Grid.SetColumn(ratingCell, 2);
        grid.Children.Add(ratingCell);

        var decided = PlayerStanding.DecidedGames(row.Wins, row.Losses);
        Number(3, decided.ToString(), isMe ? "MpTextSecondary" : "MpTextBody", FontWeights.Normal);
        Number(4, Strings.Format("MpRankRecordValue", row.Wins, row.Losses),
               isMe ? "MpTextSecondary" : "MpTextBody", FontWeights.Normal);

        // Empty, never "0 %", when nothing has been decided — the same refusal the Profile tab
        // makes about the very same number. Coloured when there IS one, which is the only
        // reason this column earns its width: a table of bare percentages is read a row at a
        // time and a coloured one is read at a glance.
        var pct = CommunityStatsView.WinPercent(row);
        Number(5,
               pct.HasValue ? Strings.Format("MpRankPercentValue", pct.Value) : "",
               pct.HasValue
                   ? Services.Multiplayer.RankingTableLayout.PercentBrushKey(pct.Value)
                   : "MpTextDim",
               FontWeights.SemiBold);

        // The tint bleeds out to the card's own padding edge, so the highlighted row reads as
        // a band across the card rather than as a floating pill, and no column shifts when it
        // appears. Same trick the community strip's own row uses.
        return new Border
        {
            Child = grid,
            Background = isMe ? (Brush)Application.Current.FindResource("MpActivityOwnRow") : null,
            BorderBrush = (Brush)Application.Current.FindResource("MpRimHair"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        void Number(int col, string text, string brush, FontWeight weight)
        {
            var tb = new TextBlock
            {
                Text = text,
                // Monospace, and this is what keeps a column of numbers comparable: with
                // proportional digits "11" and "44" are different widths and the column reads
                // ragged even when it is aligned.
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont"),
                FontSize = (double)Application.Current.FindResource("MpMetaSize"),
                FontWeight = weight,
                Foreground = (Brush)Application.Current.FindResource(brush),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                // Nothing on the LAST column: its gap would push the % away from the card's
                // own padding, and there is no next column for it to separate this from.
                Margin = new Thickness(0, 0, ColumnTrailingGap(col), 0),
            };
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }
    }

    /// <summary>
    /// The filled part of a rating bar, as a fraction of its track.
    ///
    /// <para>Two star columns rather than a pixel width, so the bar keeps its proportion when
    /// the column is resized — a measured width would be right once and wrong after the first
    /// window resize.</para>
    /// </summary>
    private static UIElement BuildRatingBar(double fraction, string brushKey)
    {
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(fraction, GridUnitType.Star),
        });
        bar.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0, 1 - fraction), GridUnitType.Star),
        });

        var fill = new Border
        {
            CornerRadius = new CornerRadius(2),
            Background = (Brush)Application.Current.FindResource(brushKey),
        };
        Grid.SetColumn(fill, 0);
        bar.Children.Add(fill);
        return bar;
    }

    /// <summary>
    /// One line of the recent-matches card: a dot, the mod, and the map.
    ///
    /// <para>The dot is GREEN only when the match was actually decided. A 0.5 means the
    /// result could not be read — no recording, a team game — and those are the majority
    /// of stored rows, so painting them like wins would misreport most of the list. They
    /// get a grey dot and dimmed text, and say so.</para>
    /// </summary>
    private static UIElement BuildActivityMatchRow(MatchHistoryRow m)
    {
        bool decided = m.Result >= 0.999 || m.Result <= 0.001;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 5),
        };
        row.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = (Brush)Application.Current.FindResource(decided ? "MpOk" : "MpTextFaint"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });

        var parts = new System.Collections.Generic.List<string> { ResolveModDisplayName(m.ModId) };
        if (!string.IsNullOrWhiteSpace(m.MapName)) parts.Add(m.MapName!);
        if (!decided) parts.Add(Strings.Get("MpActivityNotCounted"));

        row.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p))),
            Foreground = (Brush)Application.Current.FindResource(decided ? "MpTextBody" : "MpTextFaint"),
            FontSize = (double)Application.Current.FindResource("MpActivityBodySize"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    private void QuickReply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Content is not string text) return;
        GlobalChatInput.Text = text;
        GlobalChatInput.CaretIndex = GlobalChatInput.Text.Length;
        GlobalChatInput.Focus();
    }

    private void PanelTab_Click(object sender, RoutedEventArgs e)
        => ShowPanelTab(ReferenceEquals(sender, PanelTabPlayers));

    private void ShowPanelTab(bool players)
    {
        var chatVis = players ? Visibility.Collapsed : Visibility.Visible;
        PanelChatBody.Visibility = chatVis;
        // The composer goes with the chat: a message box under a list of players would
        // have nowhere to send to.
        PanelChatComposer.Visibility = chatVis;
        PlayersScroll.Visibility = players ? Visibility.Visible : Visibility.Collapsed;

        PanelTabChat.Tag = players ? null : "active";
        PanelTabPlayers.Tag = players ? "active" : null;
    }

    /// <summary>The rooms-browser search text. Empty means no filtering.</summary>
    private string _roomsQuery = string.Empty;

    /// <summary>
    /// Re-renders from the cache as the user types. Purely local — the list already in
    /// hand is filtered, so typing costs no request and doesn't disturb the 5 s poll
    /// (whose quiet diff compares the SERVER payload, not what is displayed).
    /// </summary>
    private void RoomSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _roomsQuery = RoomSearchBox.Text ?? string.Empty;
        RoomSearchPlaceholder.Visibility =
            string.IsNullOrEmpty(_roomsQuery) ? Visibility.Visible : Visibility.Collapsed;
        RerenderRoomsFromCache();
    }

    /// <summary>Set the "Showing N rooms" footer count.</summary>
    private void UpdateRoomsShowingCount(int n)
    {
        // The header pill and the footer count are the same fact, so they are set
        // together — two writers would eventually disagree after a search or a filter.
        if (RoomsCountPill != null)
        {
            RoomsCountPillText.Text = n.ToString();
            RoomsCountPill.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (RoomsShowingCount == null) return;
        RoomsShowingCount.Text = Strings.Format("MpRoomsShowingCount", n);
    }

    /// <summary>
    /// Re-resolve which columns fit and, when the answer changed, apply them to the header and
    /// re-render the rows.
    ///
    /// <para><b>Only when the SET changed</b> — not on every resize tick. Dragging the window
    /// edge raises this per pixel, and rebuilding every row each time would make the drag crawl
    /// and flicker; the visible outcome only changes when a column appears or disappears.</para>
    ///
    /// <para>The width comes from the rooms card itself rather than the window, because
    /// <see cref="UiScale"/> lays this tab out at a scaled logical size — the card's own
    /// ActualWidth is already in the units the columns are measured in, so there is no scale
    /// factor to reason about here.</para>
    /// </summary>
    private void ApplyRoomColumns()
    {
        if (RoomsHeaderStrip == null) return;

        var available = RoomsHeaderStrip.ActualWidth;
        if (available <= 0) return;   // not laid out yet; the next SizeChanged will do it

        var resolved = Services.RoomsTableLayout.Resolve(available);
        // _roomColumnsApplied FIRST: the set matching on the very first call is the normal
        // case (the field is seeded with the full set), and skipping then leaves the header
        // on its XAML placeholder for the whole session. See the field's own comment.
        if (_roomColumnsApplied && Services.RoomsTableLayout.SameColumns(resolved, _roomColumns))
            return;

        _roomColumnsApplied = true;
        _roomColumns = resolved;

        RoomsHeaderStrip.ColumnDefinitions.Clear();
        foreach (var spec in resolved)
            RoomsHeaderStrip.ColumnDefinitions.Add(new ColumnDefinition
            {
                // A null FixedWidth is the reference's `1fr`: the Room column absorbs
                // whatever the fixed ones leave. MinWidth stays 0 so it can shrink and
                // let its text ellipsise, rather than pushing the fixed columns off the
                // edge of a list that does not scroll horizontally.
                Width = spec.FixedWidth is double w
                    ? new GridLength(w, GridUnitType.Pixel)
                    : new GridLength(1, GridUnitType.Star),
            });

        for (var i = 0; i < resolved.Count; i++)
        {
            var header = HeaderElementFor(resolved[i].Column);
            if (header == null) continue;
            Grid.SetColumn(header, i);
            header.Visibility = Visibility.Visible;
        }
        foreach (var dropped in Services.RoomsTableLayout.Hidden(resolved))
        {
            var header = HeaderElementFor(dropped);
            if (header != null) header.Visibility = Visibility.Collapsed;
        }

        RerenderRoomsFromCache();
    }

    /// <summary>The header element for a column, so it can be moved or hidden with its cells.</summary>
    private FrameworkElement? HeaderElementFor(Services.RoomColumn column) => column switch
    {
        Services.RoomColumn.Room => ColButtonRoom,
        // ANFITRIÓN is a plain TextBlock now, not a sort button — the column can still
        // be moved and hidden, it just can't be clicked.
        Services.RoomColumn.Host => ColHeaderHost,
        Services.RoomColumn.Players => ColButtonPlayers,
        Services.RoomColumn.Ping => ColButtonPing,
        Services.RoomColumn.Action => ColHeaderAction,
        _ => null,
    };

    // ---------- Global chat ----------

    /// <summary>
    /// Open the global chat / presence socket whenever the user is signed in,
    /// and close it on sign-out. Idempotent — safe to call from session-state
    /// changes and Attach.
    ///
    /// **Deliberately NOT gated on tab/window visibility.** This socket is what
    /// makes the user appear "connected" (present) to everyone else — it must
    /// stay open while the launcher runs and is signed in, even when the user is
    /// on another tab or the window is minimised to the tray (background
    /// presence, GameRanger-style). The 30 s ping heartbeat lives on the socket's
    /// own Task (not the UI thread), so a tray socket survives the backend's 90 s
    /// idle-kick. The visibility-gated pollers (quota/rooms/radmin) stay in
    /// <see cref="OnVisibleChangedTabGate"/>; only THIS socket is always-on.
    /// </summary>
    private void SyncGlobalChat()
    {
        var shouldConnect = _session?.Status == MultiplayerSession.SessionStatus.SignedIn;
        if (shouldConnect) OpenGlobalChat();
        else CloseGlobalChat();
    }

    private void OpenGlobalChat()
    {
        if (_globalChatSocket != null) return;             // already connected
        var token = _session?.SessionToken;
        if (_session == null || string.IsNullOrEmpty(token)) return;
        try
        {
            var uri = LobbyWebSocket.BuildWsUri(_session.Api.BaseUri, "global/ws");
            var sock = new LobbyWebSocket(uri, LobbyWebSocket.HelloMode.SessionToken, token);
            sock.FrameReceived += OnGlobalChatFrame;
            _globalChatSocket = sock;
            _globalChatRendered = false;
            sock.Start();
            UpdateGlobalChatEmptyHint();   // shows "connecting…" until global_state lands
            DiagnosticLog.Write($"Global chat: connecting to {uri}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Global chat: open failed: {ex.Message}");
        }
    }

    private void CloseGlobalChat()
    {
        var sock = _globalChatSocket;
        if (sock == null) return;
        _globalChatSocket = null;
        sock.FrameReceived -= OnGlobalChatFrame;
        // DisposeAsync aborts the socket synchronously (no polite close
        // frame) so the fire-and-forget never actually blocks.
        _ = sock.DisposeAsync();
        _globalChatRendered = false;
        GlobalChatPanel.Children.Clear();
        _lastGlobalChatAuthor = null;
        _lastGlobalChatDate = null;
        GlobalChatPresenceText.Text = "";
        if (PanelTabChat != null) PanelTabChat.ToolTip = null;
        GlobalChatNotice.Visibility = Visibility.Collapsed;
        UpdateGlobalChatEmptyHint();
    }

    /// <summary>
    /// WS frame handler for the global room. Fires on a background thread;
    /// we marshal to the dispatcher and ignore frames from a socket we've
    /// since replaced/closed (a close can race the last receive).
    /// </summary>
    private void OnGlobalChatFrame(object? sender, LobbyWebSocket.FrameReceivedEventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(sender, _globalChatSocket)) return;
            try
            {
                switch (e.Type)
                {
                    case "global_state":
                        RenderGlobalChatState(e.Json);
                        break;
                    case "chat":
                        if (e.Json.TryGetProperty("line", out var line))
                        {
                            AppendGlobalChatLine(line, scroll: true);
                            // Live incoming message → chat blip, unless it's our own.
                            // (History is replayed via RenderGlobalChatState, which
                            // never reaches this frame handler, so it stays silent.)
                            var lineUserId = line.TryGetProperty("userId", out var luid)
                                ? (luid.GetString() ?? "") : "";
                            if (!string.Equals(lineUserId, _session?.CurrentUser?.Id, StringComparison.Ordinal))
                                Services.SoundService.PlayChat();
                        }
                        break;
                    case "presence":
                        ParseOnlineUsers(e.Json);
                        if (e.Json.TryGetProperty("online", out var on) && on.TryGetInt32(out var n))
                            UpdateGlobalPresence(n);
                        break;
                    case "invite":
                        HandleInviteFrame(e.Json);
                        break;
                    case "invite_sent":
                        HandleInviteSentFrame(e.Json);
                        break;
                    case "lobby_created":
                        HandleLobbyCreatedFrame(e.Json);
                        break;
                    case "match_rated":
                        HandleMatchRatedFrame(e.Json);
                        break;
                    case "tournament_update":
                        HandleTournamentUpdateFrame(e.Json.GetRawText());
                        break;
                    case "error":
                        var code = e.Json.TryGetProperty("code", out var c) ? (c.GetString() ?? "") : "";
                        DiagnosticLog.Write($"Global chat error frame: {code}");
                        // Invite-flow errors are shown as a toast (the sender is usually
                        // inside a room, not looking at the global-chat composer); other
                        // errors keep the inline composer notice.
                        if (code.StartsWith("invite_", StringComparison.Ordinal))
                            HandleInviteError(code);
                        else
                            ShowGlobalChatNoticeFor(code);
                        break;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"Global chat frame handling failed: {ex.Message}");
            }
        });

    /// <summary>Cooldown window between accepted invites from the SAME sender (anti-spam).</summary>
    private const long InviteCooldownMs = 60_000;
    /// <summary>Last-accepted invite tick per sender key, for the cooldown gate.</summary>
    private readonly Dictionary<string, long> _inviteCooldownByUser = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Senders the user chose to silence THIS session (in-memory, cleared on restart).</summary>
    private readonly HashSet<string> _ignoredInviters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A room invite arrived (someone invited me to their room). Show an in-app
    /// toast with Join / Mute. Join reuses the same path as the deep link.
    ///
    /// Receiver-side anti-spam (complements the backend's sender-side rate-limit +
    /// room-membership validation): three gates drop the invite SILENTLY (no toast,
    /// no sound) — the global <c>ReceiveInvites</c> opt-out, a per-sender ~60 s
    /// cooldown (kills a flood), and a session-only "silence this player" set. The
    /// sender key is <c>from.userId</c> (fallback <c>from.id</c>, fallback login) so
    /// it stays stable across a griefer's repeat invites.
    /// </summary>
    private void HandleInviteFrame(JsonElement json)
    {
        var lobbyId = json.TryGetProperty("lobbyId", out var l) ? (l.GetString() ?? "") : "";
        if (string.IsNullOrEmpty(lobbyId)) return;
        var roomName = json.TryGetProperty("roomName", out var rn) ? (rn.GetString() ?? "") : "";
        var modId = json.TryGetProperty("modId", out var m) ? (m.GetString() ?? "") : "";
        var fromLogin = "";
        var fromId = "";
        if (json.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object)
        {
            fromLogin = from.TryGetProperty("login", out var fl) ? (fl.GetString() ?? "") : "";
            fromId = from.TryGetProperty("userId", out var fu) ? (fu.GetString() ?? "") : "";
            if (string.IsNullOrEmpty(fromId))
                fromId = from.TryGetProperty("id", out var fi2) ? (fi2.GetString() ?? "") : "";
        }
        if (string.IsNullOrWhiteSpace(fromLogin)) fromLogin = Strings.Get("MpRoomTitleGeneric");
        // Stable identity for the anti-spam gates: userId when present, else the login.
        var senderKey = !string.IsNullOrEmpty(fromId) ? fromId : fromLogin;

        // Gate 1: global opt-out (Settings → "Receive invitations").
        if (!(_config?.ReceiveInvites ?? true))
        {
            DiagnosticLog.Write($"Invite from '{fromLogin}' dropped — invitations disabled in settings.");
            return;
        }
        // Gate 2: session mute of this sender.
        if (_ignoredInviters.Contains(senderKey))
        {
            DiagnosticLog.Write($"Invite from '{fromLogin}' dropped — sender silenced this session.");
            return;
        }
        // Gate 3: per-sender cooldown (anti-flood).
        var now = Environment.TickCount64;
        if (_inviteCooldownByUser.TryGetValue(senderKey, out var last) && now - last < InviteCooldownMs)
        {
            DiagnosticLog.Write($"Invite from '{fromLogin}' dropped — within {InviteCooldownMs / 1000}s cooldown.");
            return;
        }
        _inviteCooldownByUser[senderKey] = now;

        var modName = ResolveModDisplayName(modId);
        var body = string.IsNullOrWhiteSpace(roomName)
            ? modName
            : $"{roomName}  ·  {modName}";

        var muteLabel = fromLogin;   // capture for the confirmation toast
        _showAppToast?.Invoke(new AppToast.ToastOptions(
            "📨",   // 📨
            Strings.Format("MpInviteToastTitle", fromLogin),
            body,
            new[]
            {
                new AppToast.ToastAction(Strings.Get("MpToastJoin"), true,
                    () => _ = JoinByLobbyIdAsync(lobbyId)),
                // "Mute" silences this sender for the session (the ✕ / auto-dismiss
                // still handle "ignore just this one").
                new AppToast.ToastAction(Strings.Get("MpToastMute"), false, () =>
                {
                    _ignoredInviters.Add(senderKey);
                    // On the desktop too: this is the reply to a button pressed on a
                    // desktop card, and it should appear where the hand already was.
                    _showAppToast?.Invoke(new AppToast.ToastOptions(
                        "🔕", Strings.Format("MpInviteMutedConfirm", muteLabel), null,
                        System.Array.Empty<AppToast.ToastAction>(),
                        AutoDismissMs: 4000, PreferDesktop: true));
                }),
            },
            // ALWAYS on the desktop, even with the launcher in front. An invite expires
            // and needs a click; drawn inside the window it is missed from another tab or
            // another monitor, which is exactly what was reported.
            PreferDesktop: true));
        Services.SoundService.PlayConnect();
    }

    /// <summary>Surface an invite-flow error (offline / rate-limited / not-in-room) as a toast.</summary>
    private void HandleInviteError(string code)
    {
        var key = code switch
        {
            "invite_target_offline" => "MpInviteErrOffline",
            "invite_rate_limited" => "MpInviteErrRate",
            "invite_not_in_room" => "MpInviteErrNotInRoom",
            _ => "MpInviteErrGeneric",
        };
        _showAppToast?.Invoke(new AppToast.ToastOptions(
            "⚠", Strings.Get(key), null, System.Array.Empty<AppToast.ToastAction>(), AutoDismissMs: 5000));
    }

    /// <summary>The server confirmed our invite reached the target — brief toast.</summary>
    private void HandleInviteSentFrame(JsonElement json)
    {
        var login = json.TryGetProperty("login", out var l) ? (l.GetString() ?? "") : "";
        if (string.IsNullOrWhiteSpace(login)) return;
        _showAppToast?.Invoke(new AppToast.ToastOptions(
            "✉",   // ✉
            Strings.Format("MpInviteSent", login),
            null,
            System.Array.Empty<AppToast.ToastAction>(),
            AutoDismissMs: 4000));
    }

    /// <summary>
    /// A new room was created (real-time push over /global/ws). Hand it to
    /// MainWindow, which shares the room dedup + tab/subtab dots with the 90 s
    /// fallback poll and shows the in-app toast (with a Join action).
    /// </summary>
    /// <summary>
    /// A match we played, reported without a result, was decided later — by our own late
    /// reading or by the other player's.
    ///
    /// <para>Rides the global socket because the room is long gone by then. Everything is
    /// optional except the match id: an older backend, or a rating that could not be computed,
    /// simply means the bell says less rather than nothing.</para>
    /// </summary>
    private void HandleMatchRatedFrame(JsonElement json)
    {
        var matchId = json.TryGetProperty("match_id", out var mid) ? (mid.GetString() ?? "") : "";
        if (string.IsNullOrEmpty(matchId)) return;

        static double? Num(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble() : null;

        _onMatchRated?.Invoke(new MatchRatedNotice(
            matchId,
            json.TryGetProperty("mod_id", out var mo) ? (mo.GetString() ?? "") : "",
            json.TryGetProperty("map_name", out var mp) && mp.ValueKind == JsonValueKind.String
                ? mp.GetString() : null,
            Num(json, "result"),
            Num(json, "rating_before"),
            Num(json, "rating_after")));
    }

    private void HandleLobbyCreatedFrame(JsonElement json)
    {
        if (!json.TryGetProperty("lobby", out var lobby) || lobby.ValueKind != JsonValueKind.Object) return;
        var id = lobby.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
        if (string.IsNullOrEmpty(id)) return;
        var title = lobby.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
        var modId = lobby.TryGetProperty("modId", out var m) ? (m.GetString() ?? "") : "";
        var hostUserId = "";
        var hostLogin = "";
        if (lobby.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.Object)
        {
            hostUserId = host.TryGetProperty("userId", out var hu) ? (hu.GetString() ?? "") : "";
            hostLogin = host.TryGetProperty("login", out var hl) ? (hl.GetString() ?? "") : "";
        }
        // maxPlayers is on the frame; the count is not, and does not need to be —
        // the announcement is emitted by POST /lobbies, which inserts the row with
        // current_players = 1. "1/8" is therefore the capacity AT THAT INSTANT, the
        // same snapshot semantics the host name and the mod already have on a log
        // line. A room whose max is missing (an older backend) shows the mod alone.
        var maxPlayers = lobby.TryGetProperty("maxPlayers", out var mp) && mp.TryGetInt32(out var mpv) ? mpv : 0;
        _onNewRoomFromWs?.Invoke(id, title, modId, hostUserId, hostLogin);
        AppendGlobalChatRoomEvent(id, modId, hostLogin, hostUserId, maxPlayers);
    }

    /// <summary>
    /// A "someone opened a room" card, inserted into the global chat flow (design
    /// handoff 1a).
    ///
    /// <para>It exists because the toast is transient and the dot is only a hint: a
    /// room announced while you were reading the chat left nothing behind. Here it
    /// stays in the log with a way in.</para>
    ///
    /// <para>Unlike the toast, this is NOT filtered — a room whose mod you don't have,
    /// or your own, still reads as activity, which is the point of a room feed. Only
    /// the JOIN link is gated, since offering a way in that cannot work is worse than
    /// offering none.</para>
    /// </summary>
    private void AppendGlobalChatRoomEvent(
        string lobbyId, string modId, string hostLogin, string hostUserId, int maxPlayers)
    {
        if (GlobalChatPanel == null || !_globalChatRendered) return;

        var modName = ResolveModDisplayName(modId);
        var card = new Border
        {
            Background = (Brush)Application.Current.FindResource("MpEventBg"),
            BorderBrush = (Brush)Application.Current.FindResource("MpEventRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0, 8, 0, 8),
        };

        // One row: glyph tile, text, link. The text column is the only star-sized
        // one, which is what makes its ellipsis fire — a horizontal StackPanel would
        // measure with infinite width and let a long mod name run under the link.
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The glyph sits on a rounded tile, not loose in the text. MpEventRim is the
        // reference's own tile fill (the accent at 20% alpha) and is reused rather
        // than duplicated under a second name.
        var glyphTile = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.FindResource("MpEventRim"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
            Child = new TextBlock
            {
                Text = "⚑",
                Foreground = (Brush)Application.Current.FindResource("MpActionText"),
                FontSize = (double)Application.Current.FindResource("FontSizeCaption"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(glyphTile, 0);
        row.Children.Add(glyphTile);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = Strings.Format("MpChatRoomOpened", string.IsNullOrWhiteSpace(hostLogin) ? "—" : hostLogin),
            Foreground = (Brush)Application.Current.FindResource("MpTextSecondary"),
            FontSize = (double)Application.Current.FindResource("MpLabelSize"),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // Mod and capacity — NOT the room title. The reference drops it here because
        // the host's name is already the line above and the title would push the one
        // fact that decides whether to click (is there room?) off the end.
        var detail = maxPlayers > 0 ? $"{modName} · 1/{maxPlayers}" : modName;
        stack.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
            FontSize = (double)Application.Current.FindResource("MpLabelSize"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(stack, 1);
        row.Children.Add(stack);

        // Not my room, and I have the mod: the two things that decide whether joining
        // can actually work. Same gates the toast applies, minus the dedup — a chat
        // line is a log entry, so it is written once by construction.
        var me = _session?.CurrentUser;
        bool mine = me != null && !string.IsNullOrEmpty(hostUserId)
            && string.Equals(hostUserId, me.Id, StringComparison.Ordinal);
        if (!mine && IsModInstalledLocally(modId))
        {
            var join = new Button
            {
                Content = Strings.Get("MpRoomJoin"),
                Style = (Style)Application.Current.FindResource("MpLinkButton"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0),
            };
            join.Click += async (_, _) => await JoinByLobbyIdAsync(lobbyId);
            Grid.SetColumn(join, 2);
            row.Children.Add(join);
        }

        card.Child = row;
        GlobalChatPanel.Children.Add(card);

        // A room card breaks the "same author = continuation" run, or the next message
        // would tuck itself under a header that is no longer above it.
        _lastGlobalChatAuthor = null;
        TrimGlobalChat();
        ScrollGlobalChatToEnd();
    }

    /// <summary>Resolve a mod id to its display name for toasts (falls back to the id).</summary>
    private static string ResolveModDisplayName(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return "";
        try { return ModRegistry.Find(modId)?.DisplayName ?? modId; }
        catch { return modId; }
    }

    private void RenderGlobalChatState(JsonElement json)
    {
        GlobalChatPanel.Children.Clear();
        _lastGlobalChatAuthor = null;
        _lastGlobalChatDate = null;
        _globalChatRendered = true;
        if (json.TryGetProperty("history", out var hist) && hist.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in hist.EnumerateArray())
                AppendGlobalChatLine(line, scroll: false);
        }
        ParseOnlineUsers(json);
        if (json.TryGetProperty("online", out var on) && on.TryGetInt32(out var n))
            UpdateGlobalPresence(n);
        UpdateGlobalChatEmptyHint();
        ScrollGlobalChatToEnd();
    }

    /// <summary>
    /// Cache the global-chat "who's online" list (with each player's live status)
    /// from a presence / global_state frame's optional <c>onlineUsers</c> array,
    /// then (re)render the live players panel. Only replaces the cache when the
    /// frame carries the array — an old backend that sends only the count leaves
    /// the list untouched (back-compat; the panel then shows empty). Each entry's
    /// <c>status</c> is <c>in_game</c> / <c>in_room</c> / <c>idle</c> (missing →
    /// idle, so an old backend puts everyone "in launcher").
    /// </summary>
    private void ParseOnlineUsers(JsonElement frame)
    {
        if (frame.TryGetProperty("onlineUsers", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            _globalOnlineUsers.Clear();
            var myId = _session?.CurrentUser?.Id;
            bool playedConnect = false;
            foreach (var u in arr.EnumerateArray())
            {
                var userId = u.TryGetProperty("userId", out var idEl) ? (idEl.GetString() ?? "") : "";
                var login = u.TryGetProperty("login", out var lEl) ? (lEl.GetString() ?? "") : "";
                var avatarUrl = u.TryGetProperty("avatarUrl", out var avEl) ? avEl.GetString() : null;
                var status = u.TryGetProperty("status", out var stEl) ? (stEl.GetString() ?? "idle") : "idle";
                double? rating = u.TryGetProperty("rating", out var rtEl)
                                 && rtEl.ValueKind == JsonValueKind.Number ? rtEl.GetDouble() : null;
                // The deviation travels with it: the rating alone cannot say whether a 1500
                // was earned. Absent on an older backend, which reads as "don't know" and
                // keeps painting the number — see RatingDisplay.IsUnrated.
                double? rd = u.TryGetProperty("rd", out var rdEl)
                             && rdEl.ValueKind == JsonValueKind.Number ? rdEl.GetDouble() : null;
                _globalOnlineUsers.Add((userId, login, avatarUrl, status, rating, rd));

                // A genuinely new arrival (after the baseline, not us) pops once.
                if (_presenceBaselineSeeded
                    && !string.IsNullOrEmpty(userId)
                    && !_presenceSeenIds.Contains(userId)
                    && !string.Equals(userId, myId, StringComparison.Ordinal)
                    && !playedConnect)
                {
                    Services.SoundService.PlayConnect();
                    playedConnect = true;   // one pop per frame; throttle covers the rest
                }
            }

            // Refresh the seen-set to this frame's roster; the first frame just
            // seeds the baseline silently.
            _presenceSeenIds.Clear();
            foreach (var user in _globalOnlineUsers)
                if (!string.IsNullOrEmpty(user.userId)) _presenceSeenIds.Add(user.userId);
            _presenceBaselineSeeded = true;
        }
        RenderPlayersPanel();
    }

    private void AppendGlobalChatLine(JsonElement line, bool scroll)
    {
        var login = line.TryGetProperty("login", out var l) ? (l.GetString() ?? "") : "";
        var body = line.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
        long at = line.TryGetProperty("at", out var a) && a.TryGetInt64(out var ms) ? ms : 0;
        var avatarUrl = line.TryGetProperty("avatarUrl", out var av) ? av.GetString() : null;
        if (string.IsNullOrEmpty(body)) return;
        AppendGlobalChatRow(login, body, at, avatarUrl);
        UpdateGlobalChatEmptyHint();
        if (scroll) ScrollGlobalChatToEnd();
    }

    /// <summary>Author of the last appended global-chat row, so consecutive
    /// messages from the same person render as continuations (no repeated
    /// avatar/name). Reset to null whenever the panel is cleared.</summary>
    private string? _lastGlobalChatAuthor;
    // Local date of the last rendered message. A message on a NEW day forces a
    // full header (avatar + name + dated timestamp) even if it's the same
    // author, so the first message of each day always shows its date — this is
    // what keeps a same-author run that crosses midnight from hiding the date.
    private DateTime? _lastGlobalChatDate;

    /// <summary>
    /// One chat row: avatar, then a name + time header over the message body.
    ///
    /// <para>No bubbles. The body used to sit in a rounded filled Border, and the
    /// reference drops it — in a narrow column a bubble per line turns the log into a
    /// column of boxes and the text into the smaller thing inside them. Without the fill
    /// the reading text is the widest, brightest element in the panel, which is what a
    /// chat should be.</para>
    ///
    /// <para>Consecutive messages from the SAME author render as continuations: body
    /// only, aligned under the first. That grouping survived the bubble removal because
    /// it is what keeps a fast exchange from repeating the same avatar six times.</para>
    /// </summary>
    private void AppendGlobalChatRow(string login, string body, long atMs, string? avatarUrl)
    {
        var nameBrush = (Brush)Application.Current.FindResource("MpTextSecondary");
        var timeBrush = (Brush)Application.Current.FindResource("MpTextDim");
        var bodyBrush = (Brush)Application.Current.FindResource("MpTextBody");
        var avatarBg = (Brush)Application.Current.FindResource("MpField");

        // A message written on a different day than the previous one breaks the
        // "same author = continuation" grouping, so the new day's first message
        // renders a fresh, dated header.
        DateTime? msgDate = atMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(atMs).LocalDateTime.Date
            : (DateTime?)null;
        bool dayChanged = msgDate != null && _lastGlobalChatDate != null && msgDate != _lastGlobalChatDate;

        // The reference marks a change of day with a centred separator rather than by
        // dating every message. Emitted for the FIRST dated message too, so a backlog
        // replayed on join is anchored instead of starting mid-air. This is why the
        // per-message stamp below is a bare time: the date is the separator's job now,
        // and repeating it on each line was what made an old backlog read as today.
        if (msgDate != null && (_lastGlobalChatDate == null || dayChanged))
            GlobalChatPanel.Children.Add(BuildChatDateSeparator(msgDate.Value));

        bool sameAuthor = !dayChanged
            && !string.IsNullOrEmpty(login)
            && string.Equals(login, _lastGlobalChatAuthor, StringComparison.Ordinal);

        var bodyText = new TextBlock
        {
            Text = body,
            Foreground = bodyBrush,
            FontSize = (double)Application.Current.FindResource("MpBodySize"),
            // The reference's line-height 1.5 on a 12.5 body. WPF wants the absolute
            // value, not the ratio.
            LineHeight = 19,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap,
        };

        var grid = new Grid
        {
            // Tight gap for a continuation, a clearer gap when the author changes.
            Margin = new Thickness(0, sameAuthor ? 1 : 5, 0, 0),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(33) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (sameAuthor)
        {
            Grid.SetColumn(bodyText, 1);
            grid.Children.Add(bodyText);
        }
        else
        {
            // Avatar: monogram fallback with the real Discord photo painted on
            // top when we have a URL (if the image fails to load, the monogram
            // underneath stays visible).
            var avatarInner = new Grid();
            avatarInner.Children.Add(new TextBlock
            {
                Text = Monogram(login),
                Foreground = nameBrush,
                FontWeight = FontWeights.Bold,
                FontSize = (double)Application.Current.FindResource("MpMicroSize"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (!string.IsNullOrEmpty(avatarUrl))
            {
                var photo = new System.Windows.Shapes.Ellipse { Width = 24, Height = 24 };
                try
                {
                    photo.Fill = new ImageBrush(
                        new System.Windows.Media.Imaging.BitmapImage(new Uri(avatarUrl, UriKind.Absolute)))
                    {
                        Stretch = Stretch.UniformToFill,
                    };
                }
                catch { /* malformed URL → leave the monogram visible */ }
                avatarInner.Children.Add(photo);
            }
            var avatar = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = avatarBg,
                VerticalAlignment = VerticalAlignment.Top,
                Child = avatarInner,
            };
            Grid.SetColumn(avatar, 0);
            grid.Children.Add(avatar);

            var stack = new StackPanel();
            Grid.SetColumn(stack, 1);

            // Baseline-aligned name and time, per the reference.
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(login) ? "—" : login,
                Foreground = nameBrush,
                FontWeight = FontWeights.SemiBold,
                FontSize = (double)Application.Current.FindResource("MpMetaSize"),
                VerticalAlignment = VerticalAlignment.Bottom,
            });
            if (atMs > 0)
            {
                header.Children.Add(new TextBlock
                {
                    // Time only — the day is carried by the separator above.
                    Text = DateTimeOffset.FromUnixTimeMilliseconds(atMs).LocalDateTime.ToString("HH:mm"),
                    // Full date + time on hover, for precision.
                    ToolTip = FormatChatTimeFull(atMs),
                    Foreground = timeBrush,
                    FontSize = (double)Application.Current.FindResource("MpPillSize"),
                    Margin = new Thickness(7, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                });
            }
            stack.Children.Add(header);
            stack.Children.Add(bodyText);
            grid.Children.Add(stack);
        }

        GlobalChatPanel.Children.Add(grid);
        _lastGlobalChatAuthor = login;
        if (msgDate != null) _lastGlobalChatDate = msgDate;

        TrimGlobalChat();
    }

    /// <summary>
    /// Cap the rendered chat to the last N rows. The presence socket is always-on (see
    /// SyncGlobalChat), so while the launcher sits in the tray for hours these rows
    /// would otherwise accumulate unbounded in a hidden panel — a slow memory leak.
    ///
    /// <para>Its own method because EVERY writer to the panel has to call it, not just
    /// message rows: day dividers and room-opened cards go into the same panel, and one
    /// that skipped the trim would leak exactly as the messages once did.</para>
    /// </summary>
    private void TrimGlobalChat()
    {
        const int MaxGlobalChatRows = 200;
        while (GlobalChatPanel.Children.Count > MaxGlobalChatRows)
            GlobalChatPanel.Children.RemoveAt(0);
    }

    private void UpdateGlobalPresence(int online)
    {
        // The presence dot lives in the merged header now, so just the count text.
        // Just the number on the tab: it shares half a ~290px strip with the title, and
        // "N conectados" does not fit beside it. The green dot already says what the
        // dropped word said, and the full sentence moves to the tooltip — which works
        // here because this is ordinary client area, unlike the title bar.
        GlobalChatPresenceText.Text = online.ToString(System.Globalization.CultureInfo.CurrentCulture);
        if (PanelTabChat != null)
            PanelTabChat.ToolTip = TooltipHelper.Wrap(Strings.Format("MpGlobalChatPresence", online));
        // This live presence is also the top-bar "players online" source now, so
        // both read the same real connected-user count.
        _lastGlobalOnline = online;
        UpdateTopBarCounts();
    }

    /// <summary>
    /// Toggle the centered hint shown when the message list is empty:
    /// "connecting…" before the first <c>global_state</c>, "no messages yet"
    /// after.
    /// </summary>
    private void UpdateGlobalChatEmptyHint()
    {
        if (GlobalChatPanel.Children.Count > 0)
        {
            GlobalChatEmptyHint.Visibility = Visibility.Collapsed;
            return;
        }
        GlobalChatEmptyHint.Text = _globalChatRendered
            ? Strings.Get("MpGlobalChatEmpty")
            : Strings.Get("MpGlobalChatConnecting");
        GlobalChatEmptyHint.Visibility = Visibility.Visible;
    }

    private void ScrollGlobalChatToEnd() => GlobalChatScroll.ScrollToEnd();

    /// <summary>
    /// Surface a localized hint above the composer when the server drops a
    /// message (slow-mode / rate-limit / auto-timeout / too-long). Unknown or
    /// transport-level error codes aren't user-facing — they only get logged.
    /// </summary>
    private void ShowGlobalChatNoticeFor(string code)
    {
        var key = code switch
        {
            "chat_slow_mode" => "MpGlobalChatSlowMode",
            "chat_rate_limited" => "MpGlobalChatRateLimited",
            "chat_muted" => "MpGlobalChatMuted",
            "chat_timeout" => "MpGlobalChatTimedOut",
            "chat_too_long" => "MpGlobalChatTooLong",
            _ => null,
        };
        if (key == null) return;
        GlobalChatNotice.Text = Strings.Get(key);
        GlobalChatNotice.Visibility = Visibility.Visible;
    }

    private void SendGlobalChat()
    {
        var sock = _globalChatSocket;
        var body = GlobalChatInput.Text?.Trim() ?? "";
        if (sock == null || string.IsNullOrEmpty(body)) return;
        GlobalChatInput.Clear();   // the server echoes the message back to us
        _ = sock.SendChatAsync(body);
    }

    private void GlobalChatSendButton_Click(object sender, RoutedEventArgs e) => SendGlobalChat();

    private void GlobalChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter is reserved for a future multiline box.
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendGlobalChat();
        }
    }

    private void GlobalChatInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        GlobalChatPlaceholder.Visibility = string.IsNullOrEmpty(GlobalChatInput.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        // Typing again dismisses any throttle hint.
        if (GlobalChatNotice.Visibility == Visibility.Visible)
            GlobalChatNotice.Visibility = Visibility.Collapsed;
    }

    private static string Monogram(string login) =>
        string.IsNullOrWhiteSpace(login) ? "?" : login.Substring(0, 1).ToUpperInvariant();

    /// <summary>
    /// A circular avatar disc: the user's Discord photo when we have a URL, with a
    /// coloured-hash monogram underneath (visible when there's no photo or it fails
    /// to load). Reused by the roster, the rooms-list host cell and the room-peek
    /// popup so Discord avatars render consistently everywhere — matching the webhook.
    /// </summary>
    /// <param name="cornerRadius">
    /// Null — the default — means a circle. The profile header's 56-px avatar is the one place
    /// that asks for a rounded SQUARE, which the design handoff gives it so the one portrait
    /// that heads a page does not read as another roster face.
    /// </param>
    private static FrameworkElement BuildAvatarDisc(
        string name, string? avatarUrl, double size, double? cornerRadius = null)
    {
        var radius = cornerRadius ?? size / 2;
        var disc = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        disc.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(radius),
            Background = HostMonogramBrush(name),
            Child = new TextBlock
            {
                Text = Monogram(name),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                // Scaled to the disc rather than fixed, or the 56-px profile avatar would
                // carry the same 13-px letter as an 18-px roster face.
                FontSize = size >= 40
                    ? size * 0.4
                    : (double)Application.Current.FindResource("FontSizeCaption"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        if (!string.IsNullOrEmpty(avatarUrl))
        {
            try
            {
                // A Rectangle with a clip radius rather than an Ellipse, so the same helper
                // can produce both shapes; an Ellipse cannot be squared off.
                disc.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = size,
                    Height = size,
                    RadiusX = radius,
                    RadiusY = radius,
                    Fill = new ImageBrush(
                        new System.Windows.Media.Imaging.BitmapImage(new Uri(avatarUrl, UriKind.Absolute)))
                    {
                        Stretch = Stretch.UniformToFill,
                    },
                });
            }
            catch { /* malformed URL → monogram stays visible */ }
        }
        return disc;
    }

    /// <summary>
    /// The reference's day divider: a hairline with the date centred on it. Replaces
    /// dating every message — repeating the date on each line is what made an old
    /// backlog read as today, and it is why the per-message stamp is now a bare time.
    /// </summary>
    private static UIElement BuildChatDateSeparator(DateTime day)
    {
        var rule = (Brush)Application.Current.FindResource("MpRimFaint");
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Border Rule() => new()
        {
            Height = 1,
            Background = rule,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var left = Rule();
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // The wording rule lives in ChatTimeFormat beside the message stamp's, so the
        // two can't disagree about when a day stops being "yesterday".
        var text = new TextBlock
        {
            Text = ChatTimeFormat.DateLabel(
                day, DateTime.Today,
                Strings.Get("MpChatToday"), Strings.Get("MpChatYesterday"),
                ChatDateCulture()).ToUpperInvariant(),
            Foreground = (Brush)Application.Current.FindResource("MpTextDim"),
            FontSize = (double)Application.Current.FindResource("MpPillSize"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var right = Rule();
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>Full date + time for the hover tooltip on a message's timestamp.</summary>
    private static string FormatChatTimeFull(long atMs)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(atMs).LocalDateTime;
        return ChatTimeFormat.FormatFull(local, ChatDateCulture());
    }

    // Month/day names follow the app's UI language (Strings.Language), not the
    // OS culture, so a Spanish UI shows "15 jul" and an English one "15 Jul".
    private static System.Globalization.CultureInfo ChatDateCulture()
        => System.Globalization.CultureInfo.GetCultureInfo(
            Strings.Language == Strings.LangEs ? "es" : "en");

    // Top-bar count sources. "players online" prefers the LIVE global-chat
    // presence (the same number the chat shows as "N connected" — the users
    // actually connected right now), which is why it now matches the chat and
    // no longer shows the /quota in-lobby count. `_lastGlobalOnline` stays null
    // until the first presence frame arrives, so until then we fall back to the
    // /quota active-players count. "active rooms" stays from /quota.
    private int? _lastGlobalOnline;
    private int _lastQuotaPlayers;
    private int _lastActiveRooms;

    // The connected global-chat users + each one's live status, cached from the
    // presence / global_state frames' onlineUsers array (see ParseOnlineUsers).
    // Status: "in_game" / "in_room" / "idle". Rendered by RenderPlayersPanel.
    private readonly List<(string userId, string login, string? avatarUrl, string status, double? rating, double? rd)> _globalOnlineUsers = new();

    // Presence "someone came online" sound: the set of userIds seen in the last
    // presence frame + a one-time baseline flag. The FIRST frame seeds the set
    // silently (no burst of pops when we first connect and receive the whole
    // roster); afterwards a userId that's genuinely new (and not our own) plays
    // the connect sound. SoundService's own Connect throttle smooths clusters.
    private readonly HashSet<string> _presenceSeenIds = new(StringComparer.Ordinal);
    private bool _presenceBaselineSeeded;

    /// <summary>
    /// The header counts this filled are gone with the bar-2 redesign — the reference
    /// keeps that bar to navigation and actions, and both numbers already appear in the
    /// right-hand panel ("Players · N" and the chat's connected count), which is where
    /// the handoff puts them. The underlying fields stay: they still feed that panel.
    /// </summary>
    private void UpdateTopBarCounts()
    {
    }

    /// <summary>
    /// (Re)render the live players panel (right column, bottom half) from the
    /// cached presence list, grouped by status into three sections —
    /// 🟢 In game / 🟡 In a room / ⚪ In launcher — each with a count. One row per
    /// player: <see cref="BuildAvatarDisc"/> + name, own row tagged "· you".
    /// Called on every presence / global_state frame (cheap, ≤~60 rows). Empty
    /// (old backend / no presence yet) → a neutral hint.
    /// </summary>
    private void RenderPlayersPanel()
    {
        if (PlayersPanel == null) return;
        PlayersPanel.Children.Clear();
        PlayersPanelTitle.Text = Strings.Format("MpPlayersPanelTitle", _globalOnlineUsers.Count);

        Brush R(string k) => (Brush)Application.Current.FindResource(k);
        double F(string k) => (double)Application.Current.FindResource(k);

        if (_globalOnlineUsers.Count == 0)
        {
            PlayersPanel.Children.Add(new TextBlock
            {
                Text = Strings.Get("MpOnlinePlayersEmpty"),
                Foreground = R("MpTextMuted"),
                FontSize = F("FontSizeCaption"),
                Margin = new Thickness(4, 6, 0, 0),
            });
            return;
        }

        var me = _session?.CurrentUser;
        bool IsMe((string userId, string login, string? avatarUrl, string status, double? rating, double? rd) u) =>
            me != null && (
                (!string.IsNullOrEmpty(u.userId) && string.Equals(u.userId, me.Id, StringComparison.Ordinal))
                || (!string.IsNullOrEmpty(u.login)
                    && string.Equals(u.login, me.DiscordUsername, StringComparison.OrdinalIgnoreCase)));

        void Section(string statusKey, string headerKey, string dotBrushKey)
        {
            var members = _globalOnlineUsers.Where(u => u.status == statusKey).ToList();
            // Category header: a status dot + "<label> · N" (always shown).
            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 2) };
            headerRow.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7, Height = 7, Fill = R(dotBrushKey),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            });
            headerRow.Children.Add(new TextBlock
            {
                Text = Strings.Format(headerKey, members.Count),
                Foreground = R("MpTextMuted"),
                FontSize = F("FontSizeCaption"),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            PlayersPanel.Children.Add(headerRow);

            // Show the per-row invite affordance only while I'm actually in a room
            // (there's something to invite people TO).
            bool inRoom = !string.IsNullOrEmpty(_session?.CurrentLobbyId);

            foreach (var u in members)
            {
                // Grid: [avatar][name *][action] so the invite icon / "you" tag
                // sits flush-right regardless of name length.
                // Auto | Auto | Auto | * | Auto — same shape as the rooms table's host cell,
                // for the same reason: the star sits AFTER the rating and soaks up the slack,
                // so the name and the number stay together on the left while the invite icon
                // and the "you" tag still end up flush right. A star on the NAME column (what
                // this was) stretched it and stranded the number at the far edge.
                var row = new Grid { Margin = new Thickness(6, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var disc = BuildAvatarDisc(u.login, u.avatarUrl, 20);
                disc.Margin = new Thickness(0, 0, 7, 0);
                Grid.SetColumn(disc, 0);
                row.Children.Add(disc);

                var nameText = new TextBlock
                {
                    Text = u.login,
                    Foreground = R("MpTextPrimary"),
                    FontSize = F("FontSizeCaption"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    // An Auto column measures with infinite width, so without this the
                    // ellipsis never fires and a long name pushes the rating off the panel.
                    // 120, not 150: the "ELO" beside the number needs the difference.
                    MaxWidth = 120,
                };
                Grid.SetColumn(nameText, 1);
                row.Children.Add(nameText);

                // Everyone's rating, glued to the name. No leading "·" — with the "you" tag
                // beside it that produced "· 1500 · tú", two separators in a row. One point
                // larger and SemiBold because digits are cap-height only: measured, a name
                // spans 16px here where the number at the same size spans 11.
                if (RatingDisplay.ShouldShow(u.rating))
                {
                    var eloText = BuildRatingText(
                        u.rating!.Value, u.rd, numberSize: F("FontSizeBody"), unitSize: 10.5);
                    Grid.SetColumn(eloText, 2);
                    row.Children.Add(eloText);
                }

                if (IsMe(u))
                {
                    var youTag = new TextBlock
                    {
                        Text = "· " + Strings.Get("MpOnlinePlayersYou"),
                        Foreground = R("MpTextMuted"),
                        FontSize = F("FontSizeCaption"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6, 0, 4, 0),
                    };
                    Grid.SetColumn(youTag, 4);
                    row.Children.Add(youTag);
                }
                else if (!string.IsNullOrEmpty(u.userId))
                {
                    // Always show the invite icon (active in a room, dimmed otherwise)
                    // — no more hidden/ugly right-click menu.
                    var inviteBtn = BuildInviteIconButton(u.userId, u.login, enabled: inRoom);
                    Grid.SetColumn(inviteBtn, 4);
                    row.Children.Add(inviteBtn);
                }
                PlayersPanel.Children.Add(row);
            }
        }

        // Ordered: playing → waiting in a room → idle in the launcher.
        Section("in_game", "MpPlayersInGame", "MpStatusInGame");
        Section("in_room", "MpPlayersInRoom", "MpStatusFull");
        Section("idle", "MpPlayersInLauncher", "MpTextMuted");
    }

    /// <summary>
    /// A compact, discoverable "invite" icon (person +) shown on every OTHER player's
    /// row in the Players panel. <paramref name="enabled"/> = I'm currently in a room
    /// (something to invite them TO): active = subtle at rest, brightens on hover,
    /// clickable, "Invite to your room" tooltip; disabled = dimmed, no hover, not
    /// clickable, "Join a room to invite" tooltip. Built as a Border (not a Button) to
    /// dodge the global gold Button style. Replaced the old right-click menu (whose
    /// default MenuItem icon gutter rendered as an ugly white box).
    /// </summary>
    private Border BuildInviteIconButton(string targetUserId, string targetLogin, bool enabled)
    {
        Brush Res(string k) => (Brush)Application.Current.FindResource(k);
        var glyph = new TextBlock
        {
            Text = "\uE8FA",   // Segoe MDL2 AddFriend (person with +)
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = Res(enabled ? "MpBlue" : "MpTextMuted"),
        };
        var btn = new Border
        {
            Child = glyph,
            Background = Res("MpRowHighlight"),               // visible chip (was transparent)
            BorderBrush = Res(enabled ? "MpCardBorder" : "MpRimFaint"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(4, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = enabled ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
            Opacity = enabled ? 1.0 : 0.55,                // active: crisp; disabled: muted but visible
            ToolTip = Strings.Get(enabled ? "MpInviteTooltip" : "MpInviteTooltipDisabled"),
        };
        if (enabled)
        {
            btn.MouseEnter += (_, _) =>
            {
                btn.Background = Res("MpBlue");             // illuminate: solid blue + white glyph
                btn.BorderBrush = Res("MpBlue");
                glyph.Foreground = System.Windows.Media.Brushes.White;
            };
            btn.MouseLeave += (_, _) =>
            {
                btn.Background = Res("MpRowHighlight");
                btn.BorderBrush = Res("MpCardBorder");
                glyph.Foreground = Res("MpBlue");
            };
            btn.MouseLeftButtonUp += (_, _) => SendInvite(targetUserId, targetLogin);
        }
        return btn;
    }

    /// <summary>Send a room invite for the CURRENT room to <paramref name="targetUserId"/>.</summary>
    private void SendInvite(string targetUserId, string targetLogin)
    {
        var sock = _globalChatSocket;
        var lobbyId = _session?.CurrentLobbyId;
        if (sock == null || string.IsNullOrEmpty(lobbyId) || string.IsNullOrEmpty(targetUserId)) return;
        DiagnosticLog.Write($"Sending room invite to '{targetLogin}' for lobby '{lobbyId}'.");
        _ = sock.SendAsync(new { type = "invite", target_user_id = targetUserId, lobby_id = lobbyId });
    }

    // The room-roster "peek" popup (see who's in a room without joining). Single
    // instance; cleared DEFERRED on Closed so a re-click on the same cell toggles
    // it off instead of reopening (the StaysOpen=false auto-dismiss + click race).
    private System.Windows.Controls.Primitives.Popup? _peekPopup;

    private async void PlayersPeek_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement anchor || anchor.Tag is not LobbySummary lobby || _session == null)
            return;
        // Toggle: a re-click on the same cell closes the open peek.
        if (_peekPopup != null) { _peekPopup.IsOpen = false; return; }

        Brush R(string k) => (Brush)Application.Current.FindResource(k);
        double F(string k) => (double)Application.Current.FindResource(k);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(lobby.Title) ? Strings.Get("MpRoomPeekTitle") : lobby.Title,
            Foreground = R("MpTextPrimary"),
            FontWeight = FontWeights.Bold,
            FontSize = F("FontSizeBodyStrong"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var loading = new TextBlock
        {
            Text = Strings.Get("MpRoomPeekLoading"),
            Foreground = R("MpTextMuted"),
            FontSize = F("FontSizeBody"),
        };
        panel.Children.Add(loading);

        var card = new Border
        {
            Background = R("MpPanel"),
            BorderBrush = R("MpCardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.FindResource("RadiusLg"),
            Padding = new Thickness(14),
            MinWidth = 220,
            MaxWidth = 300,
            Child = panel,
        };
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            Child = card,
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };
        ChromePopups.Track(popup, anchor);
        popup.Closed += (_, _) => Dispatcher.BeginInvoke(
            new Action(() => { if (ReferenceEquals(_peekPopup, popup)) _peekPopup = null; }),
            System.Windows.Threading.DispatcherPriority.Background);
        _peekPopup = popup;
        popup.IsOpen = true;

        try
        {
            var detail = await _session.Api.GetLobbyByIdAsync(lobby.Id);
            if (!ReferenceEquals(_peekPopup, popup)) return; // closed while loading
            panel.Children.Remove(loading);
            panel.Children.Add(new TextBlock
            {
                Text = $"👤 {detail.CurrentPlayers} / {detail.MaxPlayers}",
                Foreground = R("MpTextMuted"),
                FontSize = F("FontSizeCaption"),
                Margin = new Thickness(0, 0, 0, 8),
            });
            if (detail.Members.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = Strings.Get("MpRoomPeekEmpty"),
                    Foreground = R("MpTextMuted"),
                    FontSize = F("FontSizeBody"),
                });
            }
            foreach (var m in detail.Members)
            {
                var display = !string.IsNullOrWhiteSpace(m.DisplayName) ? m.DisplayName : m.DiscordUsername;
                bool isHost = string.Equals(m.Id, detail.HostUserId, StringComparison.Ordinal);
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 3, 0, 3),
                };
                var disc = BuildAvatarDisc(display, m.AvatarUrl, 24);
                disc.Margin = new Thickness(0, 0, 8, 0);
                row.Children.Add(disc);
                row.Children.Add(new TextBlock
                {
                    Text = display,
                    Foreground = isHost ? R("AccentBrush") : R("MpTextPrimary"),
                    FontSize = F("FontSizeBody"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 190,
                });
                if (isHost)
                    row.Children.Add(new TextBlock
                    {
                        Text = "  · " + Strings.Get("MpRoomBadgeHost"),
                        Foreground = R("AccentBrush"),
                        FontSize = F("FontSizeCaption"),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                else if (m.IsReady)
                    row.Children.Add(new TextBlock
                    {
                        Text = "  ✓ " + Strings.Get("MpRoomReady"),
                        Foreground = R("MpStatusReady"),
                        FontSize = F("FontSizeCaption"),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                panel.Children.Add(row);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Room peek failed: {ex.Message}");
            if (!ReferenceEquals(_peekPopup, popup)) return;
            panel.Children.Remove(loading);
            panel.Children.Add(new TextBlock
            {
                Text = Strings.Get("MpRoomPeekError"),
                Foreground = R("ErrorBrush"),
                FontSize = F("FontSizeBody"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 260,
            });
        }
    }

    private async Task RefreshQuotaAsync()
    {
        if (_session == null) return;
        try
        {
            var q = await _session.Api.GetQuotaAsync();
            // Render in the "12 players online · 4 active rooms" style. The /max
            // counter lives in the tooltip so the header strip stays compact.
            _lastQuotaPlayers = q.Players.Active;
            _lastActiveRooms = q.Lobbies.Active;
            UpdateTopBarCounts();
        }
        catch
        {
            // Quota fetch failed. Nothing to blank any more — the header labels this
            // used to clear are gone, and the right-hand panel is presence-driven, so
            // it simply keeps its last known values.
        }
    }

    /// <summary>
    /// Build one room as a full-width CARD styled like a table row: SALA
    /// (mod icon disc — ★ fallback — + title + mod/private chips), ANFITRIÓN,
    /// JUGADORES, PING, ESTADO, ACCIÓN. The six column widths mirror the header Grid in
    /// MultiplayerTab.xaml (and its 31px side margin) so the columns line up
    /// under the labels. Hover lift comes from the MpRoomCard style.
    /// </summary>
    private Border BuildRoomCard(LobbySummary lobby, int rowIndex)
    {
        // Is the lobby's mod actually installed on this PC? If not, the user
        // can't join (they wouldn't pass the fingerprint check). The card is
        // dimmed with a "mod not installed" note so it's obvious why Join is off.
        var modInstalled = IsModInstalledLocally(lobby.ModId);
        var inGame = lobby.Status == "in_game";

        // Seats, split by kind. "Full" used to be one comparison; with observer seats it is
        // two that run out separately, and asking the old question of a room with a free
        // watching seat would hide a room anybody could still join.
        var seats = Services.Multiplayer.RoomFormats.SeatsOf(
            lobby.MaxPlayers, lobby.SpectatorSlots,
            lobby.CurrentPlayers, lobby.SpectatorsPresent);

        // ONE decision, not two booleans. Whether observing is available changes what "full"
        // MEANS, so it cannot be applied by dropping the Watch button afterwards — a room
        // with the game full and a watching seat free is not `seats.Full`, and the row would
        // fall through to Join and offer a seat the server refuses. See RoomFormats.OfferFor.
        var offer = Services.Multiplayer.RoomFormats.OfferFor(seats, ObserversUnlocked);
        var isFull = offer == Services.Multiplayer.RoomOffer.Full;
        var watchOnly = offer == Services.Multiplayer.RoomOffer.Watch;
        var me = _session?.CurrentUser;

        // A room whose mod you don't have reads as unavailable through its TEXT, never
        // through an Opacity layer on the card — which is what this used to do
        // (`Opacity = modInstalled ? 1.0 : 0.6`). An opacity below 1 on a container forces
        // the whole subtree through a composition pass: ClearType is disabled for every
        // glyph inside it, AND the real contrast of the faint labels collapses to roughly
        // 2:1. Blurry and washed out at once, which is exactly how it was reported.
        //
        // The signal survives without it: both text roles step down one rung of the ramp,
        // and the action button is already disabled for a mod you don't have.
        var textPrimary = (Brush)Application.Current.FindResource(
            modInstalled ? "MpTextPrimary" : "MpTextMuted");
        var textSecondary = (Brush)Application.Current.FindResource(
            modInstalled ? "MpTextMuted" : "MpTextFaint");

        var card = new Border
        {
            // MpRoomCard is a LOCAL UserControl resource (not app-global like
            // the brushes), so resolve it via this control's FindResource, not
            // Application.Current.FindResource (which only sees merged app dicts).
            Style = (Style)FindResource("MpRoomCard"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0),
            Tag = lobby,
        };

        // (The card's illumination — a STATIC, subtle BLUE rim + faint blue
        // glow — lives in the MpRoomCard style now; no per-card animation.)

        // The set comes from Services/RoomsTableLayout, which the header strip reads too —
        // one list rather than two lists of literals kept in step by a comment. It also
        // shrinks: on a narrow window the least useful columns are dropped and their values
        // move into the room's sub-line below, so nothing is lost at any width.
        // ClipToBounds because a Grid cell does not clip on its own.
        var grid = new Grid { ClipToBounds = true };
        foreach (var spec in _roomColumns)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                // A null FixedWidth is the reference's `1fr`: the Room column absorbs
                // whatever the fixed ones leave. MinWidth stays 0 so it can shrink and
                // let its text ellipsise, rather than pushing the fixed columns off the
                // edge of a list that does not scroll horizontally.
                Width = spec.FixedWidth is double w
                    ? new GridLength(w, GridUnitType.Pixel)
                    : new GridLength(1, GridUnitType.Star),
            });

        // === Col 0: ROOM — mod icon disc (★ fallback) + title (wraps to 2
        // lines, never hard-cut) over an optional sub-line (🔒 private / "not
        // installed"). A Grid{Auto,*} — NOT a horizontal StackPanel — so the
        // title is width-constrained and wrapping/ellipsis actually engage. ===
        var modProfile = ModRegistry.Find(lobby.ModId);
        var modName = modProfile?.DisplayName;
        if (string.IsNullOrWhiteSpace(modName)) modName = lobby.ModId;

        // Host name resolved HERE, above the ROOM cell, because when the HOST column is
        // dropped at a narrow width the room's sub-line has to show it — and re-deriving it
        // down in the host cell would be a second copy of this fallback chain to keep in step.
        // Same order as everywhere else: display name → Discord username → me → em-dash.
        var hostName = lobby.Host.DisplayName;
        if (string.IsNullOrWhiteSpace(hostName) || hostName == "-")
            hostName = lobby.Host.DiscordUsername;
        if (string.IsNullOrWhiteSpace(hostName) || hostName == "-")
        {
            var hostIsMe = me != null
                && _isHostInCurrentRoom
                && string.Equals(lobby.Id, _session?.CurrentLobbyId, StringComparison.Ordinal);
            if (hostIsMe)
                hostName = string.IsNullOrEmpty(me!.DiscordUsername) ? me.DisplayName : me.DiscordUsername;
        }
        var hostNameKnown = !string.IsNullOrWhiteSpace(hostName) && hostName != "-";
        if (!hostNameKnown) hostName = "—";

        var salaCell = new Grid { VerticalAlignment = VerticalAlignment.Center };
        salaCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        salaCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var modIconBrush = ResolveRoomModIcon(modProfile);
        FrameworkElement disc;
        if (modIconBrush != null)
        {
            // 30px rounded SQUARE, not a circle: the reference shows the mod's own
            // artwork, and a circular crop eats the corners of a square icon. The Border
            // background is clipped to CornerRadius, so the UniformToFill brush is
            // centre-cropped to that shape.
            disc = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(6),
                Background = modIconBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
        }
        else
        {
            disc = new TextBlock
            {
                Text = "★",
                Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
                // Mod-icon fallback glyph — sized to the icon slot, not a type token.
                FontSize = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
        }
        Grid.SetColumn(disc, 0);
        salaCell.Children.Add(disc);

        var salaText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // The room NAME gets the whole cell — it is a direct child of the vertical stack,
        // with nothing beside it to take width away.
        //
        // It used to share a Grid{*,Auto} with the chips, and THAT was the truncation people
        // reported. BuildRoomChip states outright that a chip never shrinks, so the name was
        // the only thing in the row that could yield: the gold "COMPETITIVA / 1v1" badge took
        // about 180 px of a ~300 px cell, the name kept ~115, and it ellipsised at "Sala de
        // W...". Raising MpSectionLabelSize from 9.5 to 11 with the rest of the type scale had
        // just made the badge wider still — the reminder that a TYPE token moves WIDTHS, not
        // only heights. The chips moved down to the sub-line, which had the room all along.
        var titleBlock = new TextBlock
        {
            Text = lobby.Title,
            Foreground = textPrimary,
            FontSize = (double)Application.Current.FindResource("FontSizeBodyStrong"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            // TWO lines before it gives up. Moving the badge to the sub-line took the name from
            // ~115 px to ~272, which fixed most titles but not the long ones — and there is
            // nothing left in this cell to take width from, so the remaining answer is height.
            TextWrapping = TextWrapping.Wrap,
            // "At most two lines", spelled the way WPF spells it: there is no MaxLines here,
            // that is WinUI. The cap is a MaxHeight of exactly two line boxes, which is why
            // LineHeight is pinned rather than left to the font.
            LineHeight = 20,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 40,
            // Now trims the SECOND line: the title field takes 64 characters, so there are
            // still names that do not fit two lines at a narrow width.
            TextTrimming = TextTrimming.CharacterEllipsis,
            // KEEPS ITS OWN TOOLTIP, and it is the one trimmed block in the launcher that
            // has to. RevealText now gives every other one a hover for free — this line was
            // going to be deleted as the hand-rolled version of it — but RevealText declines
            // a WRAPPING block on purpose: a two-line cap is cut by HEIGHT, and no width
            // measurement can see that. So this is exactly the excluded case, and dropping
            // the tooltip would have left the longest room names unreadable again while
            // every shorter label around them gained a way to be read.
            ToolTip = TooltipHelper.Wrap(lobby.Title),
        };
        salaText.Children.Add(titleBlock);

        // Chips ride the SUB-LINE now; the row itself is assembled at the end of this cell,
        // once the subtitle text is known. The competitive one comes FIRST because it is the
        // fact that changes what the room IS, where "private" only changes how you get in.
        var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (lobby.Competitive)
        {
            // Derived from the server's boolean, never from words in the title: anyone can
            // type "competitive" into a room name, and a badge that a stranger can forge is
            // worth less than no badge at all.
            // ...and it names the format, from the same derivation the room itself uses, so a
            // browser row and the room you join agree about what you are walking into.
            var fmtKey = Services.Multiplayer.RoomFormats.LabelKey(
                Services.Multiplayer.RoomFormats.Resolve(
                    lobby.Competitive, lobby.MaxPlayers, lobby.SpectatorSlots));
            var compChip = BuildRoomChip(
                fmtKey == null
                    ? Strings.Get("MpRoomCompetitiveBadge")
                    : Strings.Get("MpRoomCompetitiveBadge") + " · " + Strings.Get(fmtKey),
                (Brush)Application.Current.FindResource("MpCompetitiveBg"),
                (Brush)Application.Current.FindResource("MpCompetitiveText"));
            compChip.ToolTip = TooltipHelper.Wrap(Strings.Get("MpRoomCompetitiveTooltip"));
            chips.Children.Add(compChip);
        }
        if (lobby.IsPrivate)
        {
            // Same rounded-pill look as the competitive chip, tinted purple: a low-alpha
            // purple fill (mirrors the "Ready" pill idiom #223FB950) + the solid
            // MpStatusLocked purple text. Reuses MpRoomStatusLocked ("Private"/"Privada").
            //
            // It is ALWAYS shown, and that is the point: a private room whose STATUS is
            // In game / Full outranks the purple "Private" dot, so without this chip such a
            // room would give no hint at all that it is private.
            var privateChip = BuildRoomChip(
                Strings.Get("MpRoomStatusLocked"),
                (Brush)Application.Current.FindResource("MpPrivateBg"),
                (Brush)Application.Current.FindResource("MpPrivateText"));
            chips.Children.Add(privateChip);
        }

        // ONE subtitle line, per the reference: "{mod} · {context} · hace {t}".
        // The mod name lives here now rather than in a column of its own — it reads as
        // context, and nobody sorted by it. Anything the current width could not fit as a
        // column joins it, so narrowing the window hides the COLUMN but never the fact.
        // Built from the same resolved set the columns came from, so the two can't
        // disagree about what is on screen.
        var subtitle = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(modName)) subtitle.Add(modName!);

        // The reference's middle segment: WHY this row is different from the others.
        // Being the host outranks the password note — if it's yours you already know it
        // is private, and the action button says "Re-enter" rather than offering a way
        // in. Computed from the same host identity the action button uses further down.
        var meCtx = _session?.CurrentUser;
        bool ctxMine = meCtx != null && lobby.Host != null && (
            (!string.IsNullOrEmpty(lobby.Host.Id)
                && string.Equals(lobby.Host.Id, meCtx.Id, StringComparison.Ordinal))
            || (!string.IsNullOrEmpty(lobby.Host.DiscordUsername)
                && string.Equals(lobby.Host.DiscordUsername, meCtx.DiscordUsername, StringComparison.OrdinalIgnoreCase)));
        if (ctxMine) subtitle.Add(Strings.Get("MpRoomCtxYouHost"));
        else if (lobby.IsPrivate) subtitle.Add(Strings.Get("MpRoomCtxNeedsPassword"));
        foreach (var dropped in Services.RoomsTableLayout.Hidden(_roomColumns))
        {
            switch (dropped)
            {
                case Services.RoomColumn.Host when hostNameKnown:
                    // The rating comes down with the name, never on its own — it lives in
                    // that cell, so the two are folded by the same case rather than by an
                    // order-of-drops rule that could put a bare number here.
                    subtitle.Add(!RatingDisplay.ShouldShow(lobby.Host?.Rating)
                        ? hostName!
                        : RatingDisplay.IsUnrated(lobby.Host!.Rd, gamesPlayed: null)
                            ? hostName + " " + Strings.Get("MpEloUnrated")
                            : hostName + " " + Strings.Format(
                                  "MpChipElo", (int)Math.Round(lobby.Host!.Rating!.Value)));
                    break;
                // Ping is deliberately absent: it is YOUR latency, identical on every row,
                // so repeating it per room would add noise rather than information.
            }
        }
        if (!modInstalled) subtitle.Add(Strings.Get("MpRoomModNotInstalled"));

        // The open time is the line's tail and ticks in place on the rooms timer, so the
        // rest of the line travels with it (see _roomAgeCells).
        var roomCreatedUtc = Services.RoomAgeFormat.ParseCreatedUtc(lobby.CreatedAt);
        var prefix = string.Join(" · ", subtitle);
        TextBlock? subTb = null;
        if (roomCreatedUtc.HasValue || prefix.Length > 0)
        {
            var age = roomCreatedUtc.HasValue
                ? Strings.Format("MpRoomOpenedAgo", Services.RoomAgeFormat.Compact(DateTime.UtcNow - roomCreatedUtc.Value))
                : "";
            subTb = new TextBlock
            {
                Text = prefix.Length > 0 && age.Length > 0 ? prefix + " · " + age
                     : prefix.Length > 0 ? prefix : age,
                Foreground = (Brush)Application.Current.FindResource("MpTextFaint"),
                FontSize = (double)Application.Current.FindResource("MpLabelSize"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (roomCreatedUtc.HasValue) _roomAgeCells.Add((subTb, roomCreatedUtc.Value, prefix));
        }

        // The sub-line carries the chips now, as a Grid{Auto,*} and NOT a horizontal
        // StackPanel: a horizontal StackPanel measures its children with INFINITE width, so
        // the text's ellipsis would never fire and it would simply run past the cell. Chips in
        // the Auto column, text in the star — so the thing that yields is still the TEXT, never
        // a half-trimmed "COMPETITIV...". That is the same rule this row inherits from the
        // title, only now the text beside the chips is the one that can afford to lose a word.
        //
        // Built when there is EITHER a chip or a subtitle: a competitive room with no subtitle
        // text had no second row at all before, and would have lost its badge with it.
        if (chips.Children.Count > 0 || subTb != null)
        {
            var subRow = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (chips.Children.Count > 0)
            {
                Grid.SetColumn(chips, 0);
                subRow.Children.Add(chips);
            }
            if (subTb != null)
            {
                Grid.SetColumn(subTb, 1);
                subRow.Children.Add(subTb);
            }
            salaText.Children.Add(subRow);
        }
        Grid.SetColumn(salaText, 1);
        salaCell.Children.Add(salaText);
        PlaceRoomCell(grid, Services.RoomColumn.Room, salaCell);

        // === HOST — avatar disc, name, and the host's RATING right beside it. hostName was
        // resolved above the ROOM cell because the sub-line needs it when this column drops.
        //
        // Auto | Auto | Auto | *, and the trailing star is the load-bearing part: it absorbs
        // the leftover width so the name and the number stay together on the LEFT. A star on
        // the NAME column (what this was) expands it instead, which is what pushed the rating
        // ~100px away from the person it describes — and, before the cell was widened, hard
        // against the edge where it collided with the "1/2" of PLAYERS. The name keeps its
        // ellipsis via MaxWidth, since an Auto column measures with infinite width and would
        // never trim. ===
        var hostCell = new Grid { VerticalAlignment = VerticalAlignment.Center };
        hostCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hostCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hostCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hostCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var hostDisc = BuildAvatarDisc(hostName, lobby.Host?.AvatarUrl, 20);
        hostDisc.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(hostDisc, 0);
        hostCell.Children.Add(hostDisc);
        var hostNameText = new TextBlock
        {
            Text = hostName,
            Foreground = (Brush)Application.Current.FindResource("MpTextSecondary"),
            // Was a bare 12 while the column HEADER above it read MpPillSize. Half the row
            // was hardcoded, so raising the tokens alone would have left every heading
            // larger than the value under it.
            FontSize = (double)Application.Current.FindResource("MpMetaSize"),
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            // What makes the ellipsis work at all in an Auto column.
            MaxWidth = 120,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(hostNameText, 1);
        hostCell.Children.Add(hostNameText);

        // The host's rating, glued to the name. One point LARGER and SemiBold, and that is
        // not decoration: measured on a real screenshot, "Gorgorito12" spans 14px while
        // "1500" at the SAME FontSize spans 11 — a name has ascenders and descenders, digits
        // are cap-height only, so matching the size still reads as smaller. The bump brings
        // the digits level with the name's capitals without towering over the row.
        if (RatingDisplay.ShouldShow(lobby.Host?.Rating))
        {
            var hostElo = BuildRatingText(
                lobby.Host!.Rating!.Value, lobby.Host.Rd, numberSize: 13, unitSize: 10);
            Grid.SetColumn(hostElo, 2);
            hostCell.Children.Add(hostElo);
        }

        PlaceRoomCell(grid, Services.RoomColumn.Host, hostCell);

        // === PLAYERS — "1/8" plus the reference's capacity segments: four bars that
        // fill in proportion to how full the room is, so occupancy reads at a glance
        // without parsing two numbers. Still clickable to PEEK the roster without
        // joining. The count and the bars live in a vertical stack, and the whole cell
        // carries the click so the small bars are not the only target. ===
        var playersCell = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,   // or the gaps between children swallow the click
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = lobby,
            ToolTip = Strings.Get("MpRoomPeekTooltip"),
        };
        var playersCount = new TextBlock
        {
            Text = $"{lobby.CurrentPlayers}/{lobby.MaxPlayers}",
            Foreground = textPrimary,
            FontSize = (double)Application.Current.FindResource("MpMetaSize"),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        playersCell.Children.Add(playersCount);
        playersCell.Children.Add(BuildCapacityBars(lobby.CurrentPlayers, lobby.MaxPlayers));
        playersCell.MouseEnter += (_, _) => playersCount.TextDecorations = TextDecorations.Underline;
        playersCell.MouseLeave += (_, _) => playersCount.TextDecorations = null;
        playersCell.MouseLeftButtonUp += PlayersPeek_Click;
        PlaceRoomCell(grid, Services.RoomColumn.Players, playersCell);

        // === Col 4: PING — registered so RefreshRoomPingCells() updates it in
        // place (no rebuild). It's YOUR internet latency (same for every row;
        // /lobbies has no per-host IP). ===
        var pingCell = BuildPingCell(_connectionPingMs >= 0 ? _connectionPingMs : (double?)null);
        pingCell.ToolTip = Strings.Get("MpRoomPingTooltip");
        _roomPingCells.Add(pingCell);
        // Wrapped in a clipping Grid: the inner StackPanel stays exactly as it is (the ping
        // timer mutates its children in place), but it can no longer draw past its column.
        PlaceRoomCell(grid, Services.RoomColumn.Ping, WrapCell(pingCell));

        // STATUS had its own column and no longer does: the reference lets the ACTION
        // button carry it, since "In game" and "Full" are already the reason that button
        // is disabled and its caption says so. statusKind/statusLabel are gone with it;
        // the flags they were derived from (inGame, isFull, IsPrivate) still drive the
        // action below, and the purple PRIVADA chip beside the title still marks a
        // private room at every status.
        // === Col 6: ACTION — gold-outline button. SAME priority logic: in this
        // room → Re-enter; our own room → "Your room" (disabled); in game →
        // disabled; full → disabled; mod not installed → disabled Join; else →
        // enabled Join. Enabled Join / Re-enter are the gold outline; disabled
        // states fall back to the neutral secondary style. ===
        var iAmInThisRoom = string.Equals(lobby.Id, _session?.CurrentLobbyId, StringComparison.Ordinal);
        var iAmHost = me != null && (
            (!string.IsNullOrEmpty(lobby.Host.Id)
                && string.Equals(lobby.Host.Id, me.Id, StringComparison.Ordinal))
            || (!string.IsNullOrEmpty(lobby.Host.DiscordUsername)
                && string.Equals(lobby.Host.DiscordUsername, me.DiscordUsername, StringComparison.OrdinalIgnoreCase)));

        // The reference lifts YOUR OWN room out of the list with a brighter fill and a
        // stronger rim. Set locally rather than in the MpRoomCard style, because "this
        // one is mine" is per-row DATA, not a control state a trigger could express.
        // Placed here because it reuses the two flags the action button already derives.
        if (iAmInThisRoom || iAmHost)
        {
            card.Background = (Brush)Application.Current.FindResource("MpRowHighlight");
            card.BorderBrush = (Brush)Application.Current.FindResource("MpRimMedium");
        }

        var actionBtn = new Button
        {
            // Compact + centred so the button stays button-sized (not stretched)
            // now that the ACTION column has no MaxWidth and can grow wide.
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 96,
            MaxWidth = 130,
            Padding = new Thickness(10, 4, 10, 4),
            Tag = lobby,
        };
        // Solid = "come in here"; ghost = "go back to where you already are"; neutral =
        // can't act. Three weights for three meanings, instead of one outline for all.
        var solid = (Style)Application.Current.FindResource("MpRoomActionPrimary");
        var ghost = (Style)Application.Current.FindResource("MpRoomActionGhost");
        var secondary = (Style)Application.Current.FindResource("MpSecondaryButton");
        if (iAmInThisRoom)
        {
            actionBtn.Style = ghost;
            actionBtn.Content = Strings.Get("MpRoomReenter");
            actionBtn.Click += (_, _) => OpenLobbyWindow();
        }
        else if (iAmHost)
        {
            actionBtn.Style = secondary;
            actionBtn.Content = Strings.Get("MpRoomYours");
            actionBtn.IsEnabled = false;
        }
        else if (inGame)
        {
            actionBtn.Style = secondary;
            actionBtn.Content = Strings.Get("MpRoomStatusInGame");
            actionBtn.IsEnabled = false;
        }
        else if (isFull)
        {
            actionBtn.Style = secondary;
            actionBtn.Content = Strings.Get("MpRoomFull");
            actionBtn.IsEnabled = false;
        }
        else
        {
            // A private room says so on the button: the click opens a password prompt,
            // and finding that out only after committing is a small ambush. A disabled
            // Join (mod missing) keeps the plain caption — the reason is already the
            // dimmed row and its "mod not installed" sub-line.
            // WATCH, when the game is full but the room is not. The offer is exclusive
            // rather than a second button: the row has one action cell, and there is only
            // one kind of seat left to take. It stays SECONDARY even when the mod is
            // installed, because watching is not what most people came to the row for and a
            // solid button here would read as "join this game".
            actionBtn.Style = watchOnly ? secondary : (modInstalled ? solid : secondary);
            actionBtn.Content = watchOnly
                ? Strings.Get("MpRoomWatch")
                : lobby.IsPrivate && modInstalled
                    ? Strings.Get("MpRoomJoinPrivate")
                    : Strings.Get("MpRoomJoin");
            actionBtn.IsEnabled = modInstalled;
            if (watchOnly) actionBtn.Click += WatchRoomButton_Click;
            else actionBtn.Click += JoinRoomButton_Click;
        }
        PlaceRoomCell(grid, Services.RoomColumn.Action, actionBtn);

        // Double-click anywhere on the row does whatever its button does. It fires the
        // button rather than duplicating the decision above, so a disabled Join stays
        // disabled and the row can never take an action its own button refuses.
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.ClickCount < 2 || !actionBtn.IsEnabled) return;
            actionBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        };

        // A room that appeared while the user was looking at the list gets a one-second
        // blue wash, so a new room announces itself instead of silently shifting the
        // rows. _knownRoomIds is seeded on the first render, or every room would flash
        // on the first paint and the cue would mean nothing.
        if (_roomIdsSeeded && !string.IsNullOrEmpty(lobby.Id) && !_knownRoomIds.Contains(lobby.Id))
            FlashNewRoom(card);
        if (!string.IsNullOrEmpty(lobby.Id)) _knownRoomIds.Add(lobby.Id);

        card.Child = grid;
        return card;
    }

    /// <summary>
    /// Whether this launcher offers observing at all — developer mode, and nothing else.
    ///
    /// <para><b>Shut on purpose, not unfinished.</b> The seats work end to end, but the half
    /// that lives inside AoE3 is unverified: an observer only starts empty-handed on a map
    /// whose script puts them in the extra slot. On any other map they arrive as an ordinary
    /// player, with a town centre, and the match is quietly uneven for the five other people
    /// in it — a failure nobody sees until the game is over. That is not a rough edge to
    /// ship behind a warning.</para>
    ///
    /// <para>Read at RENDER time rather than cached, and that works because
    /// <see cref="_config"/> is the SAME instance <c>MainWindow</c> hands to
    /// <c>LauncherSettingsDialog</c> — so unlocking developer mode and coming back to the
    /// list is enough, with no restart. Null config (a tab attached without one) means
    /// closed, which is the safe direction.</para>
    /// </summary>
    private bool ObserversUnlocked => _config?.DeveloperMode == true;

    /// <summary>Room ids already rendered at least once, so a new one can be spotted.</summary>
    private readonly HashSet<string> _knownRoomIds = new(StringComparer.Ordinal);

    /// <summary>False until the first render has filled <see cref="_knownRoomIds"/>.</summary>
    private bool _roomIdsSeeded;

    /// <summary>
    /// One-second blue wash over a row that has just appeared.
    ///
    /// <para>The animated brush is a fresh local <see cref="SolidColorBrush"/>, never the
    /// row's own: the palette brushes are frozen DynamicResources and animating one
    /// throws — the bug that once froze the countdown line. The animation restores the
    /// row's normal fill by animating back to whatever colour it started with.</para>
    /// </summary>
    private static void FlashNewRoom(Border card)
    {
        var resting = (card.Background as SolidColorBrush)?.Color
            ?? ((SolidColorBrush)Application.Current.FindResource("MpPanel")).Color;
        var flash = ((SolidColorBrush)Application.Current.FindResource("MpEventBg")).Color;

        var brush = new SolidColorBrush(resting);
        card.Background = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation
        {
            From = flash,
            To = resting,
            Duration = new Duration(TimeSpan.FromMilliseconds(1000)),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        });
    }

    /// <summary>
    /// Puts a cell in its column, or drops it when this width doesn't show that column.
    ///
    /// <para>Column indices used to be written into each cell by hand (0..6), which only
    /// worked while all seven were always present. Looking the index up from the resolved set
    /// is what lets columns disappear without every cell after them landing in the wrong
    /// place.</para>
    /// </summary>
    /// <summary>
    /// Puts a horizontal StackPanel cell inside a clipping Grid so it can't overflow its column.
    /// A StackPanel measures its children with INFINITE width, so nothing inside one ever trims —
    /// that is what let the ping bars and the status badge draw over their neighbours.
    /// </summary>
    private static FrameworkElement WrapCell(FrameworkElement inner)
    {
        var host = new Grid { ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
        inner.HorizontalAlignment = HorizontalAlignment.Left;
        host.Children.Add(inner);
        return host;
    }

    private void PlaceRoomCell(Grid grid, Services.RoomColumn column, UIElement cell)
    {
        var index = -1;
        for (var i = 0; i < _roomColumns.Count; i++)
            if (_roomColumns[i].Column == column) { index = i; break; }

        if (index < 0) return;   // dropped at this width — its value moved to the sub-line
        Grid.SetColumn(cell, index);
        grid.Children.Add(cell);
    }

    /// <summary>Small rounded badge for a room row (the MOD name). Bordered so
    /// it stays legible over the row's hover fill.</summary>
    private Border BuildRoomChip(string text, Brush bg, Brush fg) => new Border
    {
        // No border: the reference's chip is a tinted fill only. It also never shrinks —
        // the room name beside it takes the ellipsis instead, because a half-trimmed
        // "PRIVAD…" would be worse than a shorter title.
        Background = bg,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 2, 6, 2),
        Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            Foreground = fg,
            FontSize = (double)Application.Current.FindResource("MpSectionLabelSize"),
            FontWeight = FontWeights.SemiBold,
        },
    };

    /// <summary>
    /// Per-mod-id cache of the resolved rooms-browser icon brush, so a quiet
    /// list refresh (every 10 s) doesn't re-decode the same icon each tick.
    /// Only successful brushes are cached — a mod whose catalog icon hasn't
    /// been fetched yet (LocalIconPath still null) is retried on the next
    /// render so a late-arriving icon still shows.
    /// </summary>
    private readonly Dictionary<string, ImageBrush> _roomModIconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve a room's mod icon (cached catalog icon.png → live remote URL →
    /// built-in packed icon, via <see cref="ModProfile.ResolveIconSource"/>)
    /// to a UniformToFill brush for the card's leading disc, or null when the
    /// mod ships no resolvable icon (caller falls back to ★). A room for a mod
    /// the user hasn't installed paints its icon straight from the catalog URL
    /// — nothing is written to the mod-asset disk cache for it.
    /// Mirrors <c>CreateLobbyDialog.LoadIconBrush</c>.
    /// </summary>
    private ImageBrush? ResolveRoomModIcon(ModProfile? profile)
    {
        if (profile == null) return null;
        if (_roomModIconCache.TryGetValue(profile.Id, out var cached)) return cached;

        string? uri = profile.ResolveIconSource();
        if (string.IsNullOrEmpty(uri)) return null;

        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 48; // disc is 24 logical px; cap the decoded copy
            bmp.UriSource = new Uri(uri, UriKind.Absolute);
            bmp.EndInit();
            // A remote icon is still downloading here: it can't be frozen yet
            // (unconditional Freeze throws) and, left unfrozen, the brush
            // repaints itself when the download completes. Evict the memo on a
            // failed download so the next quiet refresh retries.
            if (bmp.IsDownloading)
            {
                var modId = profile.Id;
                bmp.DownloadFailed += (_, _) => _roomModIconCache.Remove(modId);
            }
            if (bmp.CanFreeze) bmp.Freeze();
            var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            if (brush.CanFreeze) brush.Freeze();
            _roomModIconCache[profile.Id] = brush;
            return brush;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Refresh the "last updated" label in the rooms header from
    /// <see cref="_lastRoomsRenderedAt"/>. Called after each successful
    /// /lobbies fetch and ticked by the rooms ping timer so the relative
    /// time stays current ("hace 10 s").
    /// </summary>
    private DateTime _lastRoomsRenderedAt = DateTime.MinValue;

    private void UpdateRoomsUpdatedLabel()
    {
        if (RoomsUpdatedText == null) return;
        if (_lastRoomsRenderedAt == DateTime.MinValue)
        {
            RoomsUpdatedText.Text = "";
            return;
        }
        var secs = (int)(DateTime.Now - _lastRoomsRenderedAt).TotalSeconds;
        var stamp = secs < 5
            ? Strings.Get("MpRoomsUpdatedNow")
            : secs < 60
                ? Strings.Format("MpRoomsUpdatedSecs", secs)
                : Strings.Format("MpRoomsUpdatedMins", secs / 60);

        // The reference ends this line with "· sorted by ping". It is NOT hardcoded
        // here, for two reasons: the user can sort by any column, and — until the
        // backend exposes each host's Radmin IP — the ping shown is YOUR latency,
        // identical on every row, so sorting by it does nothing. Claiming an order the
        // table isn't in, by a value that can't order it, is worse than saying nothing.
        // Naming the ACTUAL sort keeps the reference's intent and stays true.
        var sortKey = _roomsSort switch
        {
            RoomSort.Room => "MpColRoom",
            RoomSort.Players => "MpColPlayers",
            RoomSort.Ping => "MpColPing",
            _ => null,
        };
        RoomsUpdatedText.Text = sortKey == null
            ? stamp
            : stamp + " · " + Strings.Format("MpRoomsSortedBy", Strings.Get(sortKey).ToLowerInvariant());
    }

    /// <summary>
    /// Render the Ping column for a row. <paramref name="rttMs"/>
    /// null = no value yet (em-dash + muted); otherwise a small
    /// "signal bars" glyph coloured by RTT bucket plus the number.
    /// </summary>
    private StackPanel BuildPingCell(double? rttMs)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        FillPingCell(panel, rttMs);
        return panel;
    }

    /// <summary>
    /// (Re)populate a ping cell. Split out from <see cref="BuildPingCell"/>
    /// so <see cref="RefreshRoomPingCells"/> can refresh the rooms-browser
    /// cells in place without rebuilding rows (a rebuild would disrupt the
    /// Join button mid-hover/click).
    /// </summary>
    private void FillPingCell(StackPanel panel, double? rttMs)
    {
        panel.Children.Clear();
        if (rttMs is null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "—",
                Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
                FontSize = (double)Application.Current.FindResource("FontSizeBody"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        // The reference's thresholds (60 / 150), tighter than the 80 / 200 this used.
        // Only the rooms list follows them: the in-game and lobby readouts keep their own,
        // because those measure a live match where a looser amber is the honest signal.
        var rtt = rttMs.Value;
        var brush = rtt < 60
            ? (Brush)Application.Current.FindResource("MpPingGood")
            : rtt < 150
                ? (Brush)Application.Current.FindResource("MpPingMedium")
                : (Brush)Application.Current.FindResource("MpPingBad");

        panel.Children.Add(new TextBlock
        {
            // Just the number, coloured by bucket. The reference drops the "▂▄▆" bar
            // glyphs that used to precede it: the colour already carries the same
            // three-way reading, and the bars doubled the cell's width to repeat it.
            Text = $"{(int)rtt} ms",
            Foreground = brush,
            FontSize = (double)Application.Current.FindResource("MpMetaSize"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    /// <summary>
    /// Refresh the rooms-browser PING cells in place from the cached
    /// <see cref="_connectionPingMs"/> — your internet latency, the same
    /// value for every row because the launcher can't ping each host
    /// individually (no per-host Radmin IP).
    /// </summary>
    private void RefreshRoomPingCells()
    {
        double? p = _connectionPingMs >= 0 ? _connectionPingMs : (double?)null;
        foreach (var cell in _roomPingCells)
            FillPingCell(cell, p);
    }

    /// <summary>
    /// Tick the community strip's "N ago" cells up in place, on the same ~3 s ping timer.
    ///
    /// <para>Free: no request, no rebuild, just the text of at most three labels. That is why
    /// it has none of the gating the strip's FETCH carries.</para>
    /// </summary>
    private void RefreshActivityAgeCells()
    {
        if (_activityAgeCells.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var (text, reportedUtc) in _activityAgeCells)
        {
            var elapsed = now - reportedUtc;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            text.Text = Strings.Format("MpActivityAgo", Services.RoomAgeFormat.Coarse(elapsed));
        }
    }

    /// <summary>Tick the per-room "open for X" sub-lines up in place (rooms ping
    /// timer, ~3 s) so the counter is live without re-rendering the whole list.</summary>
    private void RefreshRoomAgeCells()
    {
        var now = DateTime.UtcNow;
        foreach (var (text, createdUtc, prefix) in _roomAgeCells)
        {
            var age = Strings.Format("MpRoomOpenedAgo", Services.RoomAgeFormat.Compact(now - createdUtc));
            text.Text = prefix.Length > 0 ? prefix + " · " + age : age;
        }
    }

    /// <summary>"open for X" text for the CURRENT lobby, or "" when the open time
    /// is unknown.</summary>
    private string LobbyOpenAgeText()
        => _currentLobbyCreatedUtc.HasValue
            ? Strings.Format("MpRoomOpenedAgo", Services.RoomAgeFormat.Compact(DateTime.UtcNow - _currentLobbyCreatedUtc.Value))
            : "";

    /// <summary>Tick the lobby header's "open for X" run up in place (lobby ping
    /// timer, ~2.5 s), without rebuilding the whole meta line.</summary>
    private void RefreshLobbyOpenAge()
    {
        if (_lobbyAgeRun != null && _currentLobbyCreatedUtc.HasValue)
            _lobbyAgeRun.Text = LobbyOpenAgeText();
    }

    /// <summary>Which status a room row is showing (drives the dot colour).</summary>
    private enum RoomStatusKind { Waiting, InGame, Full, Locked }

    /// <summary>
    /// Status cell: coloured dot + label. Waiting = blue dot, In Game = green
    /// dot (bold green label), Full = amber dot.
    /// </summary>
    private FrameworkElement BuildStatusCell(string label, RoomStatusKind kind)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var dotKey = kind switch
        {
            RoomStatusKind.InGame => "MpStatusInGame",
            RoomStatusKind.Full => "MpStatusFull",
            RoomStatusKind.Locked => "MpStatusLocked",
            _ => "MpStatusWaiting",
        };
        panel.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            Fill = (Brush)Application.Current.FindResource(dotKey),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)Application.Current.FindResource(
                kind == RoomStatusKind.InGame ? "MpStatusInGame" : "MpTextPrimary"),
            FontSize = (double)Application.Current.FindResource("FontSizeBody"),
            FontWeight = kind == RoomStatusKind.InGame ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return panel;
    }

    /// <summary>
    /// A stable, name-derived colour for a host's monogram disc (like the
    /// reference mockup's coloured initials). Same name → same colour.
    /// </summary>
    private static readonly string[] _hostMonogramColors =
    {
        "#3F6FAE", "#4FA66A", "#B5794A", "#8E6FB5",
        "#B85A6A", "#4A9DB5", "#B59A4A", "#6A8E5A",
    };

    private static Brush HostMonogramBrush(string name)
    {
        int h = 0;
        foreach (char c in name ?? "") h = unchecked(h * 31 + c) & 0x7fffffff;
        var hex = _hostMonogramColors[h % _hostMonogramColors.Length];
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Quick local check: do we have a saved install path for the
    /// given mod id? Used to grey out rooms whose mod isn't installed
    /// on this PC. We don't probe the actual files — the saved path
    /// in LauncherConfig is already gated by an on-disk probe at
    /// install time, so it's a safe proxy.
    /// </summary>
    private bool IsModInstalledLocally(string modId)
    {
        try
        {
            var cfg = WarsOfLibertyLauncher.Models.LauncherConfig.Load();
            var state = cfg.GetState(modId);
            if (!string.IsNullOrEmpty(state.InstallPath)) return true;

            // The stock Age of Empires III profile is never installed through
            // the launcher, so it has no saved path. Detect the base game on
            // disk instead so stock rooms aren't greyed out or blocked at join.
            var profile = ModRegistry.Find(modId);
            if (profile is { IsStockGame: true })
                return !string.IsNullOrEmpty(GameLauncher.FindAoe3InstallRoot(cfg));

            return false;
        }
        catch
        {
            return false;
        }
    }

    private async void JoinRoomButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not LobbySummary lobby) return;
        btn.IsEnabled = false;
        try { await JoinLobbyCoreAsync(lobby); }
        finally { btn.IsEnabled = true; }
    }

    /// <summary>
    /// Take one of the room's watching seats instead of a playing one.
    ///
    /// <para>A separate handler rather than a flag read off the button, because the button's
    /// Tag already carries the room and the two intentions must not be able to be confused:
    /// arriving as a player in a room that offered a seat to watch would put somebody into a
    /// match, with a town centre, that the other side thinks is even.</para>
    /// </summary>
    private async void WatchRoomButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not LobbySummary lobby) return;
        btn.IsEnabled = false;
        try { await JoinLobbyCoreAsync(lobby, asSpectator: true); }
        finally { btn.IsEnabled = true; }
    }

    /// <summary>
    /// Runs the full join flow for an already-resolved <paramref name="lobby"/>:
    /// mod-match / install gate (auto-switching the active mod when the room's mod
    /// is installed locally), fingerprint, password prompt for private rooms, then
    /// the session join. Shared by the Join button and the deep-link auto-join
    /// (<see cref="JoinByLobbyIdAsync"/>). Assumes the caller already ensured
    /// sign-in; all failures surface as in-tab notices.
    /// </summary>
    private async Task JoinLobbyCoreAsync(LobbySummary lobby, bool asSpectator = false)
    {
        if (_session == null || _getActiveProfile == null || _computeModFingerprint == null) return;

        var profile = _getActiveProfile();
        if (profile == null)
        {
            SignInErrorText.Text = Strings.Get("MpModNotInstalled");
            SignInErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Profile-vs-room mod resolution. Three cases:
        //   1. Active profile == lobby.ModId      → proceed.
        //   2. Active profile != lobby.ModId but
        //      the room's mod IS installed locally → auto-switch
        //      to it (silently, no popup) and proceed.
        //   3. Active profile != lobby.ModId AND
        //      the room's mod is NOT installed    → tell the user
        //      they need to install it first.
        //
        // Path #2 replaces the older "Wrong mod active" popup that
        // told the user to manually go switch the mod — a
        // frustrating UX since the launcher knows the right mod
        // already.
        if (!string.Equals(profile.Id, lobby.ModId, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsModInstalledLocally(lobby.ModId))
            {
                // Resolve a friendly display name for the message.
                string displayName = lobby.ModId;
                foreach (var p in ModRegistry.All)
                {
                    if (string.Equals(p.Id, lobby.ModId, StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = p.DisplayName;
                        break;
                    }
                }
                await MpAlertOverlay.NoticeAsync(
                    TabRootGrid,
                    Strings.Get("MpNoticeRoomModMissingTitle"),
                    Strings.Format("MpNoticeRoomModMissingBody", displayName),
                    Strings.Get("MpAlertOk"));
                return;
            }

            // Find the target profile in the registry.
            ModProfile? target = null;
            foreach (var p in ModRegistry.All)
            {
                if (string.Equals(p.Id, lobby.ModId, StringComparison.OrdinalIgnoreCase))
                {
                    target = p;
                    break;
                }
            }
            if (target == null)
            {
                await MpAlertOverlay.NoticeAsync(
                    TabRootGrid,
                    Strings.Get("MpNoticeUnknownModTitle"),
                    Strings.Format("MpNoticeUnknownModBody", lobby.ModId),
                    Strings.Get("MpAlertOk"));
                return;
            }

            // Ask MainWindow to switch the active profile. It runs
            // the same path the Play-tab tiles use (LoadModProfile),
            // including the busy-state pre-flight (in-progress
            // install / game running blocks the switch).
            if (_switchActiveMod == null || !_switchActiveMod(target))
            {
                await MpAlertOverlay.NoticeAsync(
                    TabRootGrid,
                    Strings.Get("MpNoticeSwitchFailedTitle"),
                    Strings.Format("MpNoticeSwitchFailedBody", target.DisplayName),
                    Strings.Get("MpAlertOk"));
                return;
            }
            // Use the new profile from here on. The active-profile
            // getter would also return it now, but reading the
            // local variable is faster and avoids an extra ref-eq.
            profile = target;
            DiagnosticLog.Write($"JoinRoom: auto-switched active mod to '{target.Id}' to match lobby '{lobby.Id}'");
        }

        string fingerprint;
        try
        {
            fingerprint = await _computeModFingerprint(profile);
        }
        catch (Exception ex)
        {
            await MpAlertOverlay.NoticeAsync(
                TabRootGrid,
                Strings.Get("MpNoticeFingerprintTitle"),
                ex.Message,
                Strings.Get("MpAlertOk"));
            return;
        }

        string? password = null;
        if (lobby.IsPrivate)
        {
            var prompt = new PasswordPromptDialog()
            {
                Owner = Window.GetWindow(this),
            };
            if (prompt.ShowDialog() != true || string.IsNullOrEmpty(prompt.EnteredPassword)) return;
            password = prompt.EnteredPassword;
        }

        try
        {
            // Stamp the room's mod id so LaunchActiveModGame uses
            // the right profile when the host starts the game — see
            // the same step in the create-room path above.
            _currentLobbyModId = lobby.ModId;
            _currentLobbyMaxPlayers = lobby.MaxPlayers;
            _currentLobbyIsPrivate = lobby.IsPrivate;
            _currentLobbyIsCompetitive = lobby.Competitive;
            _currentLobbySpectatorSlots = lobby.SpectatorSlots;
            _currentLobbyTournamentMatchId = lobby.TournamentMatchId;
            // We joined from the browser summary, which carries the real open time.
            _currentLobbyCreatedUtc = Services.RoomAgeFormat.ParseCreatedUtc(lobby.CreatedAt);
            // Host vs joiner is decided by the WS room_state frame that
            // arrives once we connect — clearing it here is just for the
            // brief window before that frame lands.
            // Pass the title from the browser summary so the in-room
            // header reads the real room name immediately, not the id.
            await _session.JoinLobbyAsync(
                lobby.Id, fingerprint, password, lobby.Title, asSpectator);
        }
        catch (LobbyApiException ex) when (ex.Code == "mod_mismatch")
        {
            await MpAlertOverlay.NoticeAsync(
                TabRootGrid,
                Strings.Get("MpNoticeMismatchTitle"),
                Strings.Get("MpNoticeMismatchBody"),
                Strings.Get("MpAlertOk"));
        }
        catch (LobbyApiException ex) when (ex.Code == "launcher_too_old")
        {
            await ShowLauncherTooOldAsync(ex);
        }
        catch (Exception ex)
        {
            await MpAlertOverlay.NoticeAsync(
                TabRootGrid,
                Strings.Get("MpNoticeJoinFailedTitle"),
                ex.Message,
                Strings.Get("MpAlertOk"));
        }
    }

    /// <summary>
    /// Deep-link auto-join: join a room by its id (from a Discord "Join" link).
    /// Ensures the user is signed in (opening the sign-in dialog if needed),
    /// resolves the <see cref="LobbySummary"/> from the live list (there is no
    /// get-by-id API), navigates to Rooms, and runs the shared join flow. If we're
    /// already in that room it just re-opens the lobby window. All failures surface
    /// as in-tab notices.
    /// </summary>
    public async Task JoinByLobbyIdAsync(string lobbyId)
    {
        if (_session == null || string.IsNullOrWhiteSpace(lobbyId)) return;
        DiagnosticLog.Write($"DeepLink: auto-join requested for lobby '{lobbyId}'.");

        // 1. Ensure signed in (reuse the same modal the Sign in button opens).
        if (_session.Status != MultiplayerSession.SessionStatus.SignedIn)
        {
            try
            {
                var dlg = new GitHubLoginDialog(_session) { Owner = Window.GetWindow(this) };
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"DeepLink: sign-in dialog failed: {ex.Message}");
            }
            if (_session.Status != MultiplayerSession.SessionStatus.SignedIn)
            {
                await MpAlertOverlay.NoticeAsync(
                    TabRootGrid,
                    Strings.Get("MpDeepLinkSignInTitle"),
                    Strings.Get("MpDeepLinkSignInBody"),
                    Strings.Get("MpAlertOk"));
                return;
            }
        }

        ShowRooms();

        // 2. Already in this room? Just bring its window up.
        if (string.Equals(_session.CurrentLobbyId, lobbyId, StringComparison.OrdinalIgnoreCase))
        {
            OpenLobbyWindow();
            return;
        }

        // 3. Resolve the room. The live list FIRST, because its LobbySummary is the
        //    complete record; then, if the id isn't in it, the public GET /lobbies/:id.
        //    That second step is what makes a pasted code work at all: this used to give
        //    up after the list scan, with a comment claiming no get-by-id endpoint
        //    existed — it does, and the roster "peek" popup has been using it all along.
        //    So any room absent from the list reported itself as "no longer open".
        LobbySummary? lobby = null;
        try
        {
            var list = await _session.Api.ListLobbiesAsync();
            foreach (var l in list.Lobbies)
            {
                if (string.Equals(l.Id, lobbyId, StringComparison.OrdinalIgnoreCase)) { lobby = l; break; }
            }

            if (lobby == null)
            {
                var detail = await _session.Api.GetLobbyByIdAsync(lobbyId);
                if (detail != null && !string.IsNullOrEmpty(detail.Id))
                {
                    // The join flow reads exactly these six fields. CreatedAt is the one
                    // LobbyDetail doesn't carry, and its only consumer is the "open for
                    // X" counter, which simply doesn't render without it.
                    lobby = new LobbySummary
                    {
                        Id = detail.Id,
                        Title = detail.Title,
                        ModId = detail.ModId,
                        MaxPlayers = detail.MaxPlayers,
                        CurrentPlayers = detail.CurrentPlayers,
                        IsPrivate = detail.IsPrivate,
                        Status = detail.Status,
                    };
                    DiagnosticLog.Write($"Resolved lobby '{lobbyId}' by id (absent from the list).");
                }
            }
        }
        catch (Exception ex)
        {
            await MpAlertOverlay.NoticeAsync(
                TabRootGrid,
                Strings.Get("MpDeepLinkFailedTitle"),
                ex.Message,
                Strings.Get("MpAlertOk"));
            return;
        }

        if (lobby == null)
        {
            await MpAlertOverlay.NoticeAsync(
                TabRootGrid,
                Strings.Get("MpDeepLinkNotFoundTitle"),
                Strings.Get("MpDeepLinkNotFoundBody"),
                Strings.Get("MpAlertOk"));
            return;
        }

        await JoinLobbyCoreAsync(lobby);
    }

    // ---------- In-room actions ----------

    private async void ReadyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.CurrentUser == null) return;

        // Toggle locally first so the UI gives instant feedback even
        // if the WS is mid-reconnect. The server-side member_ready
        // frame from the next room_state will reconcile if anything
        // drifted. Without this the button felt dead during the
        // brief WS hiccups caused by quick-tunnel idle disconnects.
        var meId = _session.CurrentUser.Id;
        var ready = !(_roomMembers.TryGetValue(meId, out var prev) && prev.Ready);
        if (_roomMembers.TryGetValue(meId, out var entry))
            entry.Ready = ready;
        RenderRoomMembers();
        // If I'm the host and this readied me up as the last one, auto-start.
        MaybeAutoStartOnAllReady();

        if (_session.RoomSocket == null)
        {
            AppendChatSystem(Strings.Get("MpChatReadySavedLocally"));
            return;
        }

        try { await _session.RoomSocket.SendReadyAsync(ready); }
        catch (Exception ex) { DiagnosticLog.Write($"MultiplayerTab.Ready: {ex.Message}"); }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;

        // The same button is the red "Cancel" during the countdown (for
        // host AND joiner — see ApplyMatchPhaseUi). Route the click to the
        // abort path instead of (re)starting a game.
        if (_matchPhase == MatchPhase.Starting)
        {
            CancelCountdownByUser();
            return;
        }

        await BeginHostStart();
    }

    /// <summary>
    /// Host-side "start the game" flow, shared by the manual Start button and
    /// the auto-start-when-all-ready path (<see cref="MaybeAutoStartOnAllReady"/>).
    /// Semantics:
    ///   1. Tell the Worker to start. It broadcasts `game_countdown` back to
    ///      every member (host + joiners), whose handler runs the local
    ///      countdown timer and launches AoE3 at 0 — symmetric pre-game UX.
    ///   2. If the WS is dead (tunnel idle drop, network blink) we won't get an
    ///      echo, so after a short grace window we start the countdown locally
    ///      so a solo host can still launch. The countdown handler is a no-op
    ///      once phase is already Starting, so a late server echo can't double.
    /// </summary>
    private async Task BeginHostStart()
    {
        if (_session == null) return;

        // Asked here rather than in the button handler, because this is the one choke point
        // both starts pass through — the manual button AND MaybeAutoStartOnAllReady. Gating
        // only the button would let the commoner path skip it entirely.
        if (_currentLobbyIsCompetitive && !await ConfirmRecordGameAsync()) return;

        AppendChatSystem(Strings.Get("MpChatStartingGame"));

        if (_session.RoomSocket != null)
        {
            try
            {
                await _session.RoomSocket.SendStartAsync();
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(2000);
                    if (_matchPhase == MatchPhase.Lobby)
                    {
                        DiagnosticLog.Write("MultiplayerTab.Start: server didn't echo countdown in 2s, " +
                            "starting local fallback countdown");
                        StartCountdown(10000);
                    }
                });
                return;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"MultiplayerTab.Start (notify peers): {ex.Message}");
                // Fall through to the offline-host path below.
            }
        }
        else
        {
            DiagnosticLog.Write("MultiplayerTab.Start: WS down — peers will pick up via room_state on reconnect");
        }
        // WS unavailable / SendStart threw: still kick off the
        // local countdown so the host can launch solo. Peers won't
        // hear about it but a single-player test session works.
        StartCountdown(10000);
    }

    /// <summary>
    /// Auto-start the countdown when the room is FULL and EVERY member (host
    /// included) is marked ready — only the host triggers it (so a single
    /// start), only from the Lobby phase, and only with ≥2 members (a solo
    /// host readying up must not launch). Reuses <see cref="BeginHostStart"/>,
    /// so the manual Start button still works and the two paths share one flow.
    /// Guarded by <see cref="_autoStartInFlight"/> so it fires once per ready-up.
    ///
    /// The FULL-room gate exists because "everyone present is ready" launched a
    /// 6-slot room with 3 players the moment those 3 readied up, stranding the
    /// other 3 — the host had no way to wait. Auto-start is now strictly the
    /// convenience for a room that filled up; the manual Start button is the
    /// host's deliberate early/force-start (it never checked ready state), so
    /// playing 3-of-6 is still one click.
    ///
    /// Capacity comes from <see cref="TryGetCurrentLobbyMaxPlayers"/> — the SAME
    /// resolution behind the "3 / 6" stat and the roster's open-slot rows, so the
    /// gate can never contradict what the host is looking at. An UNKNOWN capacity
    /// must NOT auto-start: without that guard a max of 0 makes "Count >= max"
    /// trivially true and this would fire more eagerly than the bug it replaces.
    /// </summary>
    private void MaybeAutoStartOnAllReady()
    {
        if (!_isHostInCurrentRoom) return;          // only the host triggers the start
        if (_matchPhase != MatchPhase.Lobby) return; // not during countdown / in a match
        if (_autoStartInFlight) return;              // already triggered this ready-up
        if (_roomMembers.Count < 2) return;          // don't auto-launch a solo room
        if (!TryGetCurrentLobbyMaxPlayers(out var max) || max <= 0) return; // capacity unknown
        if (_roomMembers.Count < max) return;        // room not full — host starts manually
        foreach (var m in _roomMembers.Values)
            if (!m.Ready) return;                    // everyone (host too) must be ready

        _autoStartInFlight = true;
        AppendChatSystem(Strings.Get("MpChatAutoStartAllReady"));
        _ = BeginHostStart();
    }

    private async void LeaveRoomButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;

        // The other half of the same guard that lives in LobbyWindow.OnClosing — this button is
        // reachable during the countdown, when InGameOverlay is not covering the column yet, and
        // it kills everyone's game just as thoroughly as the ✕ does. The window's own close is
        // suppressed afterwards, so the question is only ever asked once.
        if (!await ConfirmLeaveRoomAsync()) return;
        _lobbyWindow?.SuppressLeaveConfirm();

        try { await _session.LeaveCurrentLobbyAsync(); }
        catch (Exception ex) { DiagnosticLog.Write($"MultiplayerTab.Leave: {ex.Message}"); }
        finally
        {
            await RefreshRoomsListAsync();
        }
    }

    private async void ChatSendButton_Click(object sender, RoutedEventArgs e) =>
        await SendChatAsync();

    private async void ChatInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SendChatAsync();
    }

    private async Task SendChatAsync()
    {
        if (_session?.CurrentUser == null) return;
        if (_lobbyWindow == null) return;
        var text = _lobbyWindow!.ChatInputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        _lobbyWindow!.ChatInputBox.Text = "";

        var login = string.IsNullOrEmpty(_session.CurrentUser.DiscordUsername)
            ? _session.CurrentUser.DisplayName
            : _session.CurrentUser.DiscordUsername;

        if (_session.RoomSocket == null)
        {
            // Offline echo so the user still gets visual feedback. The
            // line is local-only — the server never sees it. Marker
            // makes that obvious.
            AppendChatLine(new WsPeerlessChatLine($"{login} (pending): {text}"));
            return;
        }

        // Optimistic local echo first so the message appears the very
        // moment the user presses Enter — independent of WS round-trip
        // latency. The matching server broadcast that lands a few
        // hundred ms later is suppressed by AppendChatLine via the
        // _recentLocalEchoes registry below, so no double-up.
        AppendChatRow(
            timestamp: DateTime.Now,
            isSystem: false,
            authorLogin: login,
            authorUserId: _session.CurrentUser.Id,
            body: text,
            severity: ChatSeverity.Info);
        _recentLocalEchoes.Add((text, Environment.TickCount64));

        try { await _session.RoomSocket.SendChatAsync(text); }
        catch (Exception ex) { DiagnosticLog.Write($"MultiplayerTab.Chat: {ex.Message}"); }
    }

    /// <summary>Tiny wrapper to render a non-server chat line via the
    /// same path AppendChatLine uses for real ones.</summary>
    private sealed class WsPeerlessChatLine : WsChatLine
    {
        public WsPeerlessChatLine(string body)
        {
            Login = "system";
            Body = body;
            AtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    /// <summary>
    /// Launch the active mod's executable when a <c>game_started</c>
    /// frame arrives. Every member of the room sees the same frame and
    /// fires this — that's intentional: each player launches AoE3 on
    /// their own machine, then AoE3's own LAN code (broadcasting on
    /// the ZeroTier network) discovers the other peers.
    ///
    /// We use the launch callback MainWindow injected via Attach so
    /// this control doesn't need direct access to LauncherConfig. The
    /// callback returns the started Process; we subscribe to Exited so
    /// the post-game flow (replay upload, match reporting) can run
    /// without spinning up a watcher thread of our own.
    /// </summary>
    private System.Diagnostics.Process? LaunchActiveModGame()
    {
        if (_launchGame == null || _getActiveProfile == null) return null;

        // Pick the profile to launch from the ROOM, not the Play
        // tab's currently-active mod. The room carries its own
        // mod_id (chosen by the host at create time); launching
        // whatever happens to be selected on the Play tab is wrong
        // — it'd open AoE3 from a different mod's folder and the
        // peer's mod fingerprint check would reject the session.
        //
        // Source of the mod id, in priority order:
        //   1. _currentLobbyModId — stamped at create / join time,
        //      so it works even for brand-new rooms that aren't in
        //      the browser snapshot yet.
        //   2. The cached browser snapshot (_lastBrowserList) —
        //      backup for cases where the user pre-existed the
        //      current session somehow.
        //   3. The active profile from the Play tab — last-resort
        //      defensive fallback so the launcher never throws.
        ModProfile? profile = null;
        var lobbyId = _session?.CurrentLobbyId;
        if (!string.IsNullOrEmpty(_currentLobbyModId))
        {
            foreach (var candidate in ModRegistry.All)
            {
                if (string.Equals(candidate.Id, _currentLobbyModId, StringComparison.OrdinalIgnoreCase))
                {
                    profile = candidate;
                    break;
                }
            }
        }
        if (profile == null && !string.IsNullOrEmpty(lobbyId) && _lastBrowserList != null)
        {
            foreach (var l in _lastBrowserList)
            {
                if (!string.Equals(l.Id, lobbyId, StringComparison.Ordinal)) continue;
                foreach (var candidate in ModRegistry.All)
                {
                    if (string.Equals(candidate.Id, l.ModId, StringComparison.OrdinalIgnoreCase))
                    {
                        profile = candidate;
                        break;
                    }
                }
                break;
            }
        }
        if (profile == null)
        {
            profile = _getActiveProfile();
        }
        if (profile == null)
        {
            AppendChatSystem(Strings.Get("MpChatCannotLaunchNoProfile"));
            return null;
        }
        DiagnosticLog.Write($"MultiplayerTab.LaunchActiveModGame: launching profile '{profile.Id}' ({profile.DisplayName}) for lobby '{lobbyId}'");

        try
        {
            var extraArgs = BuildMultiplayerLaunchArgs();
            DiagnosticLog.Write($"MultiplayerTab.LaunchActiveModGame: extraArgs='{extraArgs}'");

            // With n2n in place, this method used to bring up a Detours-
            // based hook DLL inside age3y.exe and a named-pipe bridge
            // that forwarded each WinSock send/recv across PeerMesh.
            // None of that exists anymore — every peer in the room is
            // already on the same 10.99.0.0/24 virtual LAN via the
            // edge.exe process the session spun up at join time, so
            // AoE3's stock LAN multiplayer code just works.

            var gameStartedAt = DateTime.UtcNow;
            var launch = _launchGame(profile, async (_, _) =>
            {
                // Run on the UI thread so we can render chat messages
                // and access session state safely.
                await Dispatcher.InvokeAsync(async () =>
                {
                    // The OS-side "game closed" path: exit InGame and
                    // run the post-match flow. If the user cancels
                    // via the popup, ExitInGamePhase has already
                    // fired — calling it again is a no-op.
                    if (_matchPhase == MatchPhase.InGame) ExitInGamePhase();
                    await OnGameExitedAsync(profile, gameStartedAt);
                });
            }, extraArgs);

            // Only when NOTHING started. A launch with no process handle but a live pid is a
            // running game — that is the elevated path, and the whole point of the type is that
            // it can no longer be mistaken for a failure.
            if (launch.Failed)
            {
                AppendChatSystem(Strings.Get("MpChatCouldNotSpawn"));
                return null;
            }
            var process = launch.Process;

            // Surface the Radmin state so a launch that can't see the host's
            // LAN game isn't a silent failure (the DeLos diagnostic bundle:
            // AoE3 launched with no OverrideAddress → bound to the wrong NIC →
            // couldn't join). Two levels, keyed off whether we actually got the
            // flag in: NO OverrideAddress ⇒ no 26.x adapter at all (strong
            // warning); flag present but Radmin not "ready" (GUI closed / powered
            // off) ⇒ soft warning — we bound the right NIC but the VPN link isn't up.
            var injectedOverride = extraArgs.Contains("OverrideAddress", StringComparison.Ordinal);
            if (!injectedOverride)
            {
                AppendChatSystem(Strings.Get("MpChatRadminNoAdapter"));
            }
            else if (!RadminVpnService.GetStatus().IsServiceRunning)
            {
                AppendChatSystem(Strings.Get("MpChatRadminNotReady"));
            }

            // n2n virtual-LAN flow: every peer's edge.exe presents the
            // room as a real LAN segment on 10.99.0.0/24, so both host
            // and joiner just walk through AoE3's stock LAN UI — no
            // virtual IPs to copy, no Direct IP textbox to paste into.
            AppendChatSystem(Strings.Get("MpChatGameLaunched"));

            // Age of Empires III keeps a "Record Game" box on its own setup screen, separate from
            // the option the launcher writes into the profile, and nothing here can see or reach
            // it. MEASURED: the box does NOT inherit from that option — tested with
            // optionrecordgame=true already on disk before the game started, and it still came up
            // unchecked. So it has to be ticked by hand, and if it resets every match then the
            // reminder is needed every match.
            //
            // This used to stop for good once a recording had been read, on the theory that a
            // success proved it was working. That theory is what the measurement above killed:
            // it would have gone quiet after the first match and let every one after it go
            // unrecorded in silence. Only an explicit mute stops it now.
            //
            // Host only — his recording is the one whose result is read. A chat line rather than
            // a toast: the lobby window is on screen at this exact moment, and the game is about
            // to take over the screen.
            if (_isHostInCurrentRoom
                && _config?.EnableGameRecording == true
                && !_config.GameRecordingReminderMuted)
                AppendChatSystem(Strings.Get("MpChatRecordReminder"), ChatSeverity.Warning);

            return process;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"MultiplayerTab.LaunchActiveModGame: {ex.Message}");
            AppendChatSystem(Strings.Format("MpChatLaunchFailed", ex.Message));
        }
        return null;
    }

    /// <summary>
    /// Builds the AoE3 command-line tail for the current room context.
    /// All flag names here were verified against age3y.exe's string
    /// table (Wars of Liberty 1.2.0c2). Confirmed flags (with the
    /// descriptive text the engine prints when it lists switches):
    ///   * <c>+noIntroCinematics</c> — "suppresses intro cinematics on app start"
    ///   * <c>+disableESOProfile</c> — "toggles the use of ESO for storing the player profile"
    ///   * <c>+dontDetectNAT</c>     — "Doth we not detect NAT addresses?"
    ///
    /// AoE3 has NO command-line flag to auto-host or auto-join a LAN
    /// game (we searched for hostmpgame / joinIPaddr / joinmpgame /
    /// jumpTo etc. — none exist), so the player still has to click
    /// "Multiplayer → LAN" once after the game opens. The launcher
    /// cuts every other startup delay it can.
    /// </summary>
    private string BuildMultiplayerLaunchArgs()
    {
        // The intro / ESO / NAT skips are always safe to apply: they
        // just kill the splash + the long "connecting to ESO" wait.
        var sb = new System.Text.StringBuilder();
        sb.Append("+noIntroCinematics +disableESOProfile +dontDetectNAT");

        // Bind AoE3's DirectPlay LAN discovery to the Radmin VPN
        // adapter when it's up. Without this, AoE3 broadcasts on the
        // physical wifi NIC and peers on different networks can't see
        // each other's lobbies — works for two PCs on the same wifi,
        // breaks the moment one switches to mobile data or another
        // network. The 26.x.x.x address belongs to Radmin's virtual
        // /8 overlay and is reachable from every Radmin peer
        // regardless of physical location.
        //
        // The community tutorial that goes around the AoE3 forums
        // tells users to add `OverrideAddress="<your radmin ip>"` to
        // the launch line by hand; this just does it automatically.
        // When Radmin isn't running, we omit the flag entirely so
        // local-LAN play (e.g. two laptops on the same router with no
        // Radmin) keeps working unmodified.
        //
        // Syntax — DO NOT "fix" this back to `+OverrideAddress`. This has
        // flip-flopped twice already and the `+` form is the BROKEN one,
        // confirmed by a real in-game capture (the lobby's "Dirección IP"
        // showed 192.168.56.1 — a VirtualBox host-only adapter — instead of
        // the 26.x Radmin IP). The form age3y.exe actually honours is the
        // community tutorial's literal `OverrideAddress="<ip>"`: NO `+`
        // prefix, an `=` assignment, and the IP in double quotes. The
        // string reaches the game's command line verbatim (single Arguments
        // string, UseShellExecute=false), exactly like the shortcut Target.
        //   * `+OverrideAddress <ip>` is silently ignored — OverrideAddress
        //     is NOT a `+`-prefixed console cvar (those are a different
        //     mechanism), so the engine drops it and auto-picks whatever
        //     adapter IP it finds first (the VirtualBox NIC, here).
        //   * The skip-intro switches above (`+noIntroCinematics`, etc.)
        //     legitimately ARE `+` cvars and stay prefixed — they work.
        //
        // Bind to the adapter IP directly (RadminVpnService.TryGetAdapterIp),
        // NOT to the readiness-gated RadminStatus.AdapterIp. The background
        // RvControlSvc keeps the 26.x adapter Up even with the Radmin app
        // closed or powered off, so the IP is readable — and worth injecting —
        // regardless of the "ready to play" banner. This is the fix for the
        // silent-omission bug (a joiner whose Radmin GUI was closed launched
        // AoE3 with no OverrideAddress → it bound to VirtualBox/wifi → couldn't
        // see the host's LAN game). The chat warnings live in LaunchActiveModGame.
        var adapterIp = RadminVpnService.TryGetAdapterIp();
        // Snapshot the full Radmin state at the launch instant so the bundle
        // shows exactly why the flag went in or not (app/power/adapter).
        var radminState = RadminVpnService.DescribeStateForLog();
        if (!string.IsNullOrEmpty(adapterIp))
        {
            sb.Append(" OverrideAddress=\"").Append(adapterIp).Append('"');
            DiagnosticLog.Write($"MultiplayerTab.BuildMultiplayerLaunchArgs: OverrideAddress injected 26.x={adapterIp} [{radminState}]");
        }
        else
        {
            DiagnosticLog.Write($"MultiplayerTab.BuildMultiplayerLaunchArgs: OverrideAddress OMITTED — no 26.x Radmin adapter Up [{radminState}]");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Called when the AoE3 process spawned by <see cref="LaunchActiveModGame"/>
    /// exits. Best-effort post-game flow: (1) read the recording the game just
    /// wrote, which is where the result lives; (2) if we're the host, report the
    /// finished match to the backend so it shows up in every participant's
    /// History tab; (3) surface the recording in the chat.
    ///
    /// <para><b>The recording is read BEFORE reporting, and that order is
    /// load-bearing</b> — the analysis needs the room's head count to tell our
    /// recording from someone else's, and the report is what consumes it. It used
    /// to be the other way round, to keep a missing user-data folder from skipping
    /// the report; that is handled now by the analysis returning null instead of
    /// returning early.</para>
    ///
    /// <para><b>The match context is cleared here, not in the report.</b> It used to be
    /// cleared in <see cref="TryReportMatchAsync"/>'s <c>finally</c>, which sits
    /// below five early returns — so on a joiner, who leaves at the very first of
    /// them, it was never cleared at all and survived into the next match. It belongs
    /// to the MATCH, and this method is the end of the match on every client, host or
    /// not.</para>
    ///
    /// <para><b>Everything below reads <paramref name="ctx"/>, never the live room.</b> The
    /// whole point of <see cref="Services.Multiplayer.MatchContext"/> is that by the time this
    /// runs the room may already be closed and the roster gone — that is precisely the case
    /// that used to lose the match.</para>
    /// </summary>
    private async Task OnGameExitedAsync(ModProfile profile, DateTime gameStartedAtUtc)
    {
        // Taken once, up front: the rest of this method can await for several seconds while the
        // recording finishes flushing, and the room can go away underneath it.
        var ctx = _matchContext;
        // The upper edge of this match's own window, for ordering replay candidates. Taken
        // before the awaits below, which can run for seconds.
        var exitedAtUtc = DateTime.UtcNow;
        try
        {
            AppendChatSystem(Strings.Get("MpChatGameClosed"));

            // A multiplayer game is still a game played, so its settings become the shared copy too —
            // otherwise someone who only plays multiplayer would never see settings carry over.
            // Opt-in and best-effort; the match report below matters far more than this.
            if (_config?.GetState(profile.Id).SyncGameSettings == true)
            {
                try { Services.GameSettingsStore.CaptureFrom(profile, _config); }
                catch (Exception ex) { DiagnosticLog.Write($"Capturing shared settings failed: {ex.Message}"); }
            }

            // The decks as they were for THIS match. The recording will name the home city; this
            // is the only record of what was inside it, because the file keeps changing. The
            // store debounces, so the dashboard's own exit handler capturing the same match a
            // moment later leaves one entry rather than two.
            if (_config != null)
            {
                var deckFolder = Services.UserDataService.GetUserDataFolder(
                    Services.UserDataService.ResolveFolderName(profile, _config));
                Services.DeckSnapshotStore.Capture(deckFolder, profile.Id, exitedAtUtc);
            }

            // Where the result comes from. Never throws, returns null when there is
            // nothing usable — the report then carries the draws it always did.
            //
            // ONE pass, deliberately. When the recording is there and readable this finds it
            // and nothing is lost; when it is not, the report goes out immediately with no
            // result and the remaining attempts run BEHIND it (see the continuation below).
            // Waiting here instead would make every match with no recording — the majority,
            // since AoE3's per-match box comes up unticked — slower for the benefit of a few.
            // Hold the room shut from here. Competitive rooms only — everyone else sees no
            // change at all — and only until the result is settled or the ceiling in
            // RoomMatchState.ResultGraceSeconds is reached. It covers BOTH players, for two
            // different reasons: see RoomMatchState.HoldLeave.
            SetResultPhase(Services.Multiplayer.RoomMatchState.ResultPhase.ReadingRecording);

            var analysis = await AnalyseMatchReplayAsync(
                profile, ctx, gameStartedAtUtc,
                firstPassOnly: true,
                preferBeforeUtc: exitedAtUtc + ReplayWindowMargin);
            var replayInfo = analysis.Info;
            _lastLocalReadFailure = analysis.Failure;
            if (analysis.Info != null) SetLastRecordingPath(analysis.Info.File.FullName);

            SetResultPhase(Services.Multiplayer.RoomMatchState.ResultPhase.SendingResult);
            var report = await TryReportMatchAsync(profile, ctx, replayInfo);
            var roomClosedByReport = report.ClosedRoom;

            // Everyone who is NOT the host sends their own reading of the same match. The
            // host's went out in the report above; this is the second opinion nobody was
            // collecting.
            await TryConfirmMatchAsync(ctx, replayInfo);

            // The host learns the room closed from their own POST, not from the socket —
            // both arrive, and whichever is first wins. Entering here as well makes the
            // host's card deterministic instead of dependent on that race.
            if (roomClosedByReport && _matchPhase != MatchPhase.Result)
                EnterResultPhase(ctx, report.Response, replayInfo);

            // The guest's side of that same moment, and until now it was blank. Nothing of ours
            // is outstanding — we cannot report — so all that is left is the host's machine, and
            // the player was made to watch that happen against an empty panel. Gated on the match
            // having actually been played: promising a result for a solo launch would be a
            // promise nothing can keep.
            else if (ctx is { IsHost: false }
                     && _roomMatchLive
                     && _matchPhase != MatchPhase.Result
                     && ctx.LooksLikeAPlayedMatch(exitedAtUtc, MinReportableSeconds).Ok)
                EnterAwaitingResultPhase(ctx);

            // If the match wasn't reported+closed (solo / short / failed report), the
            // HOST tells the server the game ended so the room reverts in_game → open
            // (and Discord flips back to "Waiting") instead of staying stuck "In game".
            // A reported match already CLOSED the room, so skip it then.
            //
            // GameRestartedSince is what keeps this from arriving too late: the awaits above can
            // take the best part of ten seconds, and if the player reopened their game in the
            // meantime this frame would make the server broadcast game_cancelled to everyone
            // EXCEPT us — killing the AoE3 that all the other players had just relaunched.
            if (!roomClosedByReport
                && !GameRestartedSince()
                && ctx?.IsHost == true
                && _session?.RoomSocket != null
                && !string.IsNullOrEmpty(_session.CurrentLobbyId))
            {
                try
                {
                    await _session.RoomSocket.SendGameEndedAsync();
                    // The server broadcasts the resulting game_cancelled to everyone EXCEPT the
                    // sender, so nobody is going to tell us the room reopened — clear it here or
                    // our own Start button keeps reading "Reopen the game" forever.
                    _roomMatchLive = false;
                    RefreshRejoinButton();
                }
                catch (Exception ex) { DiagnosticLog.Write($"MultiplayerTab.OnGameExitedAsync: SendGameEnded — {ex.Message}"); }
            }

            // The report has gone out, so the rest of the search costs nobody any latency —
            // which is the only reason it is allowed to exist. A reading that lands now is a
            // CORRECTION: it goes back through the confirmation path, which is what lets a
            // late reading still decide a match the server could not.
            if (replayInfo?.HostResult == null)
            {
                SetResultPhase(Services.Multiplayer.RoomMatchState.ResultPhase.ReadingRecording);
                replayInfo = await ContinueSearchingForResultAsync(
                    profile, ctx, gameStartedAtUtc, exitedAtUtc, replayInfo, report.Response);
            }

            // Already found above, so no second walk over the folder.
            //
            // The MAP is what actually distinguishes one of these from another: AoE3 names
            // every recording "Record Game N" and renumbers after each match, so this line used
            // to hand the player a name that belonged to a different game by the time they went
            // looking. Measured on a real bundle — three matches in one evening, all three
            // announced as "Record Game 1.age3Yrec". The name stays because it is correct at
            // this instant; it is simply no longer the only thing said.
            if (replayInfo != null)
                AppendChatSystem(Strings.Format(
                    string.IsNullOrWhiteSpace(replayInfo.MapName)
                        ? "MpChatReplaySaved"
                        : "MpChatReplaySavedMap",
                    replayInfo.File.Name, replayInfo.File.Length / 1024, replayInfo.MapName ?? ""));

            MaybeReportMissingRecording(profile, ctx, replayInfo);
            RememberRecordingOutcome(profile, ctx, replayInfo);

            // The room is still playing and our game is not — offer the way back in, and say the
            // part nobody can guess: leaving the room now is one-way, because the backend refuses
            // a re-join with Conflict('Lobby already in game.') until the match ends.
            if (Services.Multiplayer.RoomMatchState.ShouldOfferRejoin(
                    _roomMatchLive, _matchPhase == MatchPhase.InGame, _isHostInCurrentRoom))
                AppendChatSystem(Strings.Get("MpChatRoomStillPlaying"), ChatSeverity.Warning);
        }
        finally
        {
            // Released here rather than on the success path, so a throw anywhere above cannot
            // leave the player shut in the room. The ResultGraceSeconds ceiling is the second
            // belt on the same trouser, not a substitute for this one.
            SetResultPhase(Services.Multiplayer.RoomMatchState.ResultPhase.None);

            // Two guards, and both are needed. ReferenceEquals: this can run seconds after the
            // game died, by which time an entirely new match may have captured its own context,
            // and dropping THAT one would unreport it. GameRestartedSince: the player may have
            // reopened the game they had closed, which deliberately keeps the SAME context — so
            // the instance matches and clearing it would still be wrong.
            if (!GameRestartedSince() && ReferenceEquals(_matchContext, ctx))
            {
                // Handed to the result path rather than dropped. Our GAME is over; the MATCH is
                // not, and on a guest's machine the difference is measured in seconds during
                // which the answer arrives. See _pendingResultContext.
                _matchContext = null;
                if (ctx != null && _matchPhase != MatchPhase.Result) SetPendingResultContext(ctx);
            }
        }
    }

    /// <summary>
    /// The format of the room we are in — 1v1, 2v2, 3v3, or none.
    ///
    /// <para>Derived rather than stored, because a competitive room's PLAYING size names its
    /// format one-to-one and the server refuses any other for one (see
    /// <see cref="Services.Multiplayer.RoomFormats"/>). Capacity comes from the resolver the
    /// player-count stat already uses, so the badge and the stat can never disagree.</para>
    ///
    /// <para>Observer seats come off before the format is read, which is what lets a 2v2 be
    /// cast: five seats used to match no format at all, so the room was created casual and the
    /// match quietly did not score.</para>
    /// </summary>
    private Services.Multiplayer.RoomFormat CurrentRoomFormat()
        => TryGetCurrentLobbyMaxPlayers(out var max)
            ? Services.Multiplayer.RoomFormats.Resolve(
                _currentLobbyIsCompetitive, max, _currentLobbySpectatorSlots)
            : Services.Multiplayer.RoomFormat.Casual;

    /// <summary>
    /// Every room member's self-reported AoE3 profile name, for the team map.
    ///
    /// <para>A member who has not reported one is simply left out — which makes the head count
    /// disagree with the recording's and refuses the whole map. That is deliberate: a team map
    /// missing one player is a map that puts somebody on the wrong side.</para>
    /// </summary>
    private Dictionary<string, string> InGameNamesInRoom()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _roomMembers)
            if (!string.IsNullOrWhiteSpace(kv.Value.InGameName))
                names[kv.Key] = kv.Value.InGameName!;
        return names;
    }

    /// <summary>
    /// The civilization each account played, by name, or null when nothing could be resolved.
    ///
    /// <para>Two independent things can refuse, and both are ordinary. The slot map refuses when
    /// the recording and the room do not describe the same set of people; the resolver refuses
    /// when the mod ships no loose civ list (Improvement Mod and Napoleonic Era keep theirs
    /// packed) or when the display name cannot be looked up. Either way the field goes down null,
    /// which is what it was for every match before this.</para>
    /// </summary>
    private IReadOnlyDictionary<string, string>? ResolveCivNames(
        // Nullable because ModRegistry.Find is: a match can name a mod that has since left the
        // catalog, and this runs while a finished match is being reported and drawn. There is no
        // install to look in for such a mod, which is the same "no civ" answer as every other
        // refusal here.
        ModProfile? profile,
        IReadOnlyDictionary<string, ReplayParserService.ReplayPlayer>? slots)
    {
        if (profile == null || slots == null || slots.Count == 0) return null;

        var installPath = GetInstallPath(profile);
        if (string.IsNullOrWhiteSpace(installPath)) return null;

        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (userId, player) in slots)
        {
            var name = Services.Multiplayer.CivNameResolver.Resolve(installPath, player.Civilization);
            if (!string.IsNullOrWhiteSpace(name)) named[userId] = name!;
        }

        // All or nothing is NOT wanted here, unlike the team map: a civ belongs to one player and
        // an unresolved one costs only that player's badge, where a half-filled TEAM map would
        // put somebody on the wrong side. So partial is fine and gets reported as it is.
        DiagnosticLog.Write(
            $"MultiplayerTab: civilizations resolved {named.Count}/{slots.Count} for mod '{profile.Id}'.");
        return named.Count == 0 ? null : named;
    }

    /// <summary>
    /// Which home CITY each player brought, from the same slot map the civilizations come from.
    ///
    /// <para>Needs no install and no mod: the recording spells the file name out, and
    /// <see cref="Services.Multiplayer.LocalMatchView.HomeCityFrom"/> refuses anything not
    /// shaped like one rather than handing back a half-trimmed word. Partial is fine, for the
    /// same reason a civ is: an unresolved one costs that player's word and nobody else's.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ResolveHomeCities(
        IReadOnlyDictionary<string, ReplayParserService.ReplayPlayer>? slots)
    {
        if (slots == null || slots.Count == 0) return null;

        var cities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (userId, player) in slots)
        {
            var city = Services.Multiplayer.LocalMatchView.HomeCityFrom(player.HomeCityFile);
            if (city.Length > 0) cities[userId] = city;
        }

        return cities.Count == 0 ? null : cities;
    }

    /// <summary>
    /// The team split as one short string for the log — "2/2", "3/3" or "none".
    ///
    /// <para>This is the only place we will find out whether the identity bridge actually works
    /// in the field, since nothing on screen shows it while team games stay unrated.</para>
    /// </summary>
    private static string DescribeTeams(IReadOnlyDictionary<string, int>? teams)
        => teams == null
            ? "none"
            : string.Join("/", teams.GroupBy(kv => kv.Value).OrderBy(g => g.Key).Select(g => g.Count()));

    /// <summary>
    /// Whether a game is running RIGHT NOW, asked by the tail of a previous game's exit handler
    /// to find out whether it is still speaking for the current state or has been overtaken.
    ///
    /// <para>Its own named method because it guards two unrelated-looking things — telling the
    /// server the match ended, and dropping the match context — that fail the same way: the exit
    /// flow can take the best part of ten seconds (the recording retries), and anything it does
    /// afterwards is acting on a match that is no longer the one being played.</para>
    /// </summary>
    private bool GameRestartedSince() => _matchPhase == MatchPhase.InGame;

    /// <summary>What the recording contributes to the report, or null when it says nothing.</summary>
    /// <param name="HostResult">
    /// The score of whoever RECORDED this file — which is always this machine, never
    /// necessarily the host of the room. <c>AnalyseMatchReplayAsync</c> identifies the
    /// recording by this machine's own AoE3 profile name and matches the trailer's
    /// recorder slot against it, so on the host's PC this is the host's result and on
    /// anyone else's it is theirs.
    ///
    /// <para>The name reads the other way round and is kept only because
    /// <c>ResolveHostResult</c> / <c>HostResultFrom</c> in the tested pure service are
    /// named to match; renaming half of them would leave the set more confusing, not
    /// less. Both readers of this field depend on the meaning above.</para>
    /// </param>
    /// <param name="RandomSeed">
    /// The recording's map seed and the host clock beside it — the match's own
    /// fingerprint. Sent to the server so it can tell whether two players read the SAME
    /// game, and so one game cannot score twice even if the file's bytes change. Zero
    /// when the recording did not carry them.
    /// </param>
    /// <param name="Players">
    /// The recording's slots, carried so the report can work out who was on which team without
    /// inflating the file a second time. Null on the announce-only path, which never parsed a
    /// header — and null is simply "no teams", the same answer the launcher has always sent.
    /// </param>
    private sealed record MatchReplayInfo(
        System.IO.FileInfo File,
        string? MapName,
        // The POOL the map came from, which the header has always carried beside the map and
        // which nothing ever stored. Null when the recording did not name one.
        string? MapPool,
        double? HostResult,
        uint RandomSeed = 0,
        uint HostTime = 0,
        System.Collections.Generic.IReadOnlyList<ReplayParserService.ReplayPlayer>? Players = null,
        // The slot the trailer named as the loser, or -1. HostResult already answers the
        // 1v1 question and this changes nothing about it; what it adds is the only fact a
        // TEAM match needs, because naming one loser names a whole side.
        int LoserSlot = -1,
        // Every slot the recording's trailer named, LAST ELIMINATION FIRST. Diagnostic only:
        // LoserSlot is still the one that decides, and it is this list's first entry by
        // construction. It exists to answer, from a real team match, whether one block is
        // written per casualty and whether the losing side appears whole.
        System.Collections.Generic.IReadOnlyList<int>? EliminatedSlots = null);

    /// <summary>
    /// Finds the recording the game just wrote and reads the result out of it.
    ///
    /// <para><b>Runs off the UI thread.</b> Each candidate costs an inflate of a file that
    /// can be several megabytes, and this fires the moment the game closes — the player is
    /// looking at the launcher again by then, and must not be looking at a frozen one.</para>
    ///
    /// <para>The two facts that identify OUR recording are the name the host plays under and
    /// how many people were in the room. The head count comes from <paramref name="ctx"/>
    /// rather than from the live roster — it was captured when the game launched, which is the
    /// only moment it is knowable, and the room may since have emptied or closed.</para>
    ///
    /// <para>Every failure returns null, which the caller reports as a draw. A recording
    /// that can't be found or read is not a reason to interrupt anyone.</para>
    /// </summary>
    /// <summary>
    /// What the recording search produced, and — when it produced nothing usable — WHY.
    ///
    /// <para>The reason used to exist only in the diagnostic log, so every one of the five
    /// ways this can fail reached the player as the same sentence: "it was not recorded,
    /// tick Record Game". That is right for one of them and points at the wrong thing for
    /// the rest. Carrying it out of here is what lets the end-of-match card say something
    /// the player can act on.</para>
    /// </summary>
    private sealed record MatchReplayResult(MatchReplayInfo? Info, Services.Multiplayer.LocalReadFailure Failure);

    /// <param name="firstPassOnly">
    /// Run attempt 0 and stop, however it went.
    ///
    /// <para><b>This is what keeps the report instant.</b> When a recording is there and
    /// readable the first pass finds it and nothing is lost; when it is not, the caller reports
    /// immediately with no result and runs the full ladder AFTERWARDS, so the retries are a
    /// correction rather than latency every player pays. The alternative — waiting before
    /// reporting — makes the majority of matches, which have no recording at all, slower for
    /// the benefit of a few.</para>
    /// </param>
    /// <param name="preferBeforeUtc">
    /// Upper edge of this match's own window, normally when the game closed plus a margin.
    /// Ordering only, never a filter — see <see cref="ReplayUploadService.FindMatchReplay"/>.
    /// </param>
    private async Task<MatchReplayResult> AnalyseMatchReplayAsync(
        ModProfile profile, Services.Multiplayer.MatchContext? ctx, DateTime startedUtc,
        bool firstPassOnly = false, DateTime? preferBeforeUtc = null)
    {
        try
        {
            // Documents/My Games/<userDataFolder>, via the central helper so it honours the
            // dual-root rule (redirected OneDrive Documents vs the physical folder).
            var modUserData = UserDataService.GetUserDataFolder(
                UserDataService.ResolveFolderName(profile, _config));
            if (string.IsNullOrEmpty(modUserData))
                return new MatchReplayResult(null, Services.Multiplayer.LocalReadFailure.NoProfileName);

            var hostName = UserDataService.GetInGameName(profile, _config);
            var expectedHumans = ctx?.ExpectedHumans ?? 0;

            // Without a name there is no way to tell our recording from one the player was
            // sent, so nothing may be READ from it. The chat line doesn't need identity
            // though, and announcing the newest file is what happened before any of this
            // existed — so that much is kept rather than quietly lost.
            //
            // A head count of zero lands here for the same reason and is spelled out beside it
            // rather than left to the identity check: an empty roster means the room's members
            // were lost, so LooksLikeThisMatch would (correctly) confirm nothing, and without
            // this branch the recording would stop being announced at all as a side effect.
            if (string.IsNullOrWhiteSpace(hostName) || expectedHumans <= 0)
            {
                DiagnosticLog.Write(
                    "MultiplayerTab.AnalyseMatchReplayAsync: " +
                    (string.IsNullOrWhiteSpace(hostName)
                        ? $"no in-game name for '{profile.DisplayName}'"
                        : "the room's participants were not known") +
                    " — announcing the recording but reporting no result");

                var newest = await Task.Run(
                    () => ReplayUploadService.FindLatestReplay(modUserData, startedUtc));
                // Two different causes share this exit; they are told apart here so the
                // card can name the right one.
                var why = string.IsNullOrWhiteSpace(hostName)
                    ? Services.Multiplayer.LocalReadFailure.NoProfileName
                    : Services.Multiplayer.LocalReadFailure.RosterUnknown;
                return new MatchReplayResult(
                    newest == null ? null : new MatchReplayInfo(newest, null, null, null), why);
            }

            MatchReplayInfo? found = null;
            // Kept across attempts so the tail can tell "files were there but none could be
            // read" from "there was nothing to read" — different causes, different advice.
            var lastSearch = new ReplayUploadService.ReplaySearch(null, 0, 0);
            // Human names carried by recordings that parsed fine and turned out to be somebody
            // else's match. Only ever read when nothing matched — see LocalReadFailure.RecordingNotOurs.
            var seenNames = new List<string>();
            // Whether the accepted recording ended with the outcome signature at all. That is
            // what tells "the game never wrote its ending" apart from "it wrote one we cannot
            // use" — different causes, and only one of them is the player's to avoid. It used
            // to be carried as the trailer's slot field, which stopped working the moment the
            // local slot started coming from the player's name instead.
            var signaturePresent = false;

            // The search runs the instant the game process dies, so the recording we want is
            // often still being flushed — it fails to parse, and the match silently becomes a
            // draw. Retrying only costs time in the case that is currently wrong anyway; the
            // delays back off, and ShouldRetry stops immediately unless something was actually
            // unreadable, so a match whose recording simply isn't there reports at once.
            // Read from the match SNAPSHOT, not from the live room: by now the room may be
            // closed, and this decides how hard to look for the evidence of what happened in it.
            var thorough = ctx?.IsCompetitive == true;
            var delays = thorough ? ReplayRetryDelaysCompetitiveMs : ReplayRetryDelaysMs;

            for (var attempt = 0; attempt < delays.Length; attempt++)
            {
                if (attempt > 0) await Task.Delay(delays[attempt]);

                var (info, search, hadSignature) = await Task.Run(() =>
                {
                    ReplayParserService.ReplayHeader? header = null;
                    ReplayParserService.ReplayOutcome? outcome = null;

                    var result = ReplayUploadService.FindMatchReplay(
                        modUserData, startedUtc, candidate =>
                    {
                        // One inflate per candidate, used for both the identity check and the
                        // result — the outcome trailer is also what names the slot that recorded.
                        var data = ReplayParserService.TryReadContainer(
                            System.IO.File.ReadAllBytes(candidate.FullName));
                        if (data == null) return ReplayUploadService.CandidateVerdict.Unreadable;

                        var h = ReplayParserService.ParseHeader(data);
                        if (h == null) return ReplayUploadService.CandidateVerdict.Unreadable;

                        var o = ReplayParserService.ReadOutcome(data, h);
                        if (!ReplayParserService.LooksLikeThisMatch(h, hostName!, expectedHumans))
                        {
                            // Remembered so the card can name them. "None of these are yours" is
                            // a dead end on its own; the names beside the profile we read are
                            // what make a profile-name mismatch — which fails EVERY match until
                            // it is fixed — visible to the person who can fix it.
                            foreach (var p in h.Players)
                                if (p.IsHuman && !string.IsNullOrWhiteSpace(p.Name)
                                    && !seenNames.Contains(p.Name))
                                    seenNames.Add(p.Name);
                            return ReplayUploadService.CandidateVerdict.NotOurs;
                        }

                        // The tail whenever the outcome was not settled, not only when the
                        // signature is missing: a block just past the slack window and a
                        // partially-written one both read as "no result", and under the older,
                        // narrower rule neither left any evidence. 48 bytes because a healthy
                        // block is 32, so 16 could not show one that was merely misplaced.
                        //
                        // The twin of this dump is DiagnosticLog.StageReplayIndex, which puts
                        // the same bytes in the bundle — change both or they disagree. Between
                        // them they are what will answer whether a TEAM game writes an outcome
                        // block at all, which no recording anyone has been able to inspect says.
                        if (o.Confidence != ReplayParserService.ReplayOutcomeConfidence.Confident
                            && data.Length >= 48)
                            DiagnosticLog.Write(
                                $"Replay: '{candidate.Name}' outcome unsettled ({o.Confidence}, " +
                                $"signature={o.SignaturePresent}); last 48 bytes = " +
                                BitConverter.ToString(data, data.Length - 48));

                        header = h;
                        outcome = o;
                        return ReplayUploadService.CandidateVerdict.Match;
                    }, preferBeforeUtc, thorough);

                    if (result.File == null || header == null) return (null as MatchReplayInfo, result, false);

                    // BY NAME, never from the trailer. The trailer's second field was read as
                    // "the slot that recorded this file"; in multiplayer it is the loser, so
                    // this resolved to the loser on both machines and HostResultFrom could
                    // only ever answer 0.0 — a host who won never scored. The profile name is
                    // the one thing in the file that differs between the two players.
                    var hostSlot = ReplayParserService.FindPlayerSlot(header, hostName!);
                    var hostResult = ReplayParserService.HostResultFrom(outcome, hostSlot);

                    DiagnosticLog.Write(
                        $"MultiplayerTab.AnalyseMatchReplayAsync: '{result.File.Name}' map='{header.MapName}' " +
                        $"hostSlot={hostSlot} outcome={outcome.Confidence} " +
                        $"result={(hostResult.HasValue ? hostResult.Value.ToString("0.0") : "none")}");

                    return (new MatchReplayInfo(
                        result.File, header.MapName, header.MapPool, hostResult,
                        header.RandomSeed, header.HostTime, header.Players,
                        outcome.LoserSlot, outcome.EliminatedSlots),
                        result, outcome!.SignaturePresent);
                });

                lastSearch = search;
                signaturePresent = hadSignature;
                if (info != null) { found = info; break; }

                // The caller wants the answer NOW so it can report without waiting; whatever
                // else is on disk is its follow-up pass's problem, not this one's.
                if (firstPassOnly) break;

                if (!ReplayUploadService.ShouldRetry(search, attempt, delays.Length))
                {
                    DiagnosticLog.Write(
                        "MultiplayerTab.AnalyseMatchReplayAsync: no recording of this match was found " +
                        $"(host='{hostName}' humans={expectedHumans} readable={search.Parsed} " +
                        $"unreadable={search.Unreadable}) — reporting without a result");
                    break;
                }

                DiagnosticLog.Write(
                    $"MultiplayerTab.AnalyseMatchReplayAsync: {search.Unreadable} recording(s) not readable yet " +
                    $"— retrying in {delays[attempt + 1]} ms");
            }

            // Nothing is recorded about "recording works now". A successful read proves only that
            // THIS match recorded — AoE3's per-match box does not inherit from the profile
            // setting (measured), so it may well need ticking again next time. Inferring
            // otherwise is what would silence the reminder exactly when it is still needed; only
            // an explicit mute does that. See LauncherConfig.GameRecordingReminderMuted.
            //
            // "Unreadable" only when files were actually opened and none could be parsed —
            // otherwise there was simply nothing there, which is the ordinary case and the
            // one whose advice ("tick Record Game") is correct.
            Services.Multiplayer.LocalReadFailure failure;
            if (found != null)
            {
                failure = found.HostResult != null
                    ? Services.Multiplayer.LocalReadFailure.None
                    // No signature means the 12×00 + 8×FF was not at the end of the file at
                    // all: the game never finished writing its ending. That has its own advice
                    // — leave the match to the menu before closing AoE3 — where a trailer we
                    // simply cannot use has none.
                    : !signaturePresent
                        ? Services.Multiplayer.LocalReadFailure.RecordingNoOutcome
                        : Services.Multiplayer.LocalReadFailure.RecordingAmbiguous;
            }
            else if (lastSearch.Unreadable > 0 && lastSearch.Parsed == 0)
            {
                failure = Services.Multiplayer.LocalReadFailure.RecordingUnreadable;
            }
            else if (lastSearch.Parsed > 0)
            {
                // Recordings were found and read PERFECTLY, and not one of them is this match.
                // This used to fall through to NoRecordingFound — "it was not recorded, tick
                // Record Game" — which is the single piece of advice that is certainly wrong
                // here, and it is wrong every time for anyone whose AoE3 profile name differs
                // from the name they play under.
                failure = Services.Multiplayer.LocalReadFailure.RecordingNotOurs;
                _lastLocalReadDetail = seenNames.Count > 0
                    ? Strings.Format("MpResultNotOursDetail", hostName, string.Join(", ", seenNames))
                    : null;
            }
            else
            {
                failure = Services.Multiplayer.LocalReadFailure.NoRecordingFound;
            }

            if (failure != Services.Multiplayer.LocalReadFailure.RecordingNotOurs)
                _lastLocalReadDetail = null;

            return new MatchReplayResult(found, failure);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"MultiplayerTab.AnalyseMatchReplayAsync: {ex.Message}");
            return new MatchReplayResult(null, Services.Multiplayer.LocalReadFailure.NoRecordingFound);
        }
    }

    /// <summary>
    /// Keep looking for this match's recording AFTER the report has gone out, and send whatever
    /// it turns up as a correction.
    ///
    /// <para><b>This is what makes the retries affordable.</b> They used to run in front of the
    /// report, so every match with no recording — the majority — paid seconds of latency for the
    /// benefit of the few whose file was still being flushed. Behind the report they cost nobody
    /// anything, which is also why the search may now retry on an EMPTY folder (see
    /// <see cref="ReplayUploadService.ShouldRetry"/>) rather than only on an unreadable file.</para>
    ///
    /// <para>Returns the better reading when there is one, otherwise whatever the caller already
    /// had. Never throws: this runs while the player is watching their game close.</para>
    /// </summary>
    private async Task<MatchReplayInfo?> ContinueSearchingForResultAsync(
        ModProfile profile,
        Services.Multiplayer.MatchContext? ctx,
        DateTime startedUtc,
        DateTime exitedUtc,
        MatchReplayInfo? soFar,
        ReportMatchResponse? report)
    {
        if (ctx == null) return soFar;

        // Nothing a recording could change. The server refused this match for a reason that is
        // not about the outcome — a team game, an unranked mod, a duplicate — so reading one
        // would spend seconds of disk to learn something nobody can act on. A null report is the
        // guest (who never posts one) and a host whose POST failed outright; both are worth
        // continuing for, since the guest's reading is the whole point of this path.
        if (report != null
            && (report.Rated
                || (report.UnratedReason != null && report.UnratedReason != "no_decided_result")))
            return soFar;

        // One analysis at a time: this and the early read fired by match_reported both write
        // _lastLocalReadFailure, and interleaved they would let the later, worse answer win.
        if (System.Threading.Interlocked.CompareExchange(ref _replayAnalysisInFlight, 1, 0) != 0)
            return soFar;

        try
        {
            var again = await AnalyseMatchReplayAsync(
                profile, ctx, startedUtc, preferBeforeUtc: exitedUtc + ReplayWindowMargin);

            // A later pass may only IMPROVE the diagnosis. Downgrading a specific failure to
            // "no recording found" would put the one message we know to be wrong back on the
            // card — which is the bug this whole area exists to fix.
            if (again.Failure != Services.Multiplayer.LocalReadFailure.NoRecordingFound
                || _lastLocalReadFailure == Services.Multiplayer.LocalReadFailure.NoRecordingFound
                || _lastLocalReadFailure == Services.Multiplayer.LocalReadFailure.ReadPending)
                _lastLocalReadFailure = again.Failure;

            var better = again.Info ?? soFar;
            if (again.Info != null) SetLastRecordingPath(again.Info.File.FullName);

            if (again.Info?.HostResult != null)
            {
                DiagnosticLog.Write(
                    "MultiplayerTab.ContinueSearchingForResultAsync: a late reading of " +
                    $"'{again.Info.File.Name}' gave {again.Info.HostResult:0.0} — sending it as a correction");

                // allowHost only when our OWN report went out undecided: confirming a report we
                // made ourselves proves nothing in general, but a host who reported 0.5-0.5 and
                // then found his recording is the one case where his second reading is new
                // information rather than an echo.
                await TryConfirmMatchAsync(
                    ctx, again.Info,
                    allowHost: report?.UnratedReason == "no_decided_result");
            }

            // The card is already on screen saying whatever was known a moment ago. Repaint it
            // rather than leaving the player looking at a stale reason.
            RepaintMatchResult();
            return better;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"MultiplayerTab.ContinueSearchingForResultAsync: {ex.Message}");
            return soFar;
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _replayAnalysisInFlight, 0);
        }
    }

    /// <summary>
    /// Rebuild and repaint the end-of-match card from whatever is known NOW.
    ///
    /// <para>Deliberately not <see cref="EnterResultPhase"/>: that method clears
    /// <c>_roomMatchLive</c>, drops the process handle, kills the tick timer, stops the socket's
    /// reconnect and suppresses the leave confirm. Re-running all of that over an already
    /// terminal state is a different bug. Only the MODEL is rebuilt here.</para>
    /// </summary>
    private void RepaintMatchResult()
    {
        if (_matchPhase != MatchPhase.Result) return;
        var rebuild = _outcomeRebuilder;
        if (rebuild == null) return;
        try { ShowMatchResult(rebuild()); }
        catch (Exception ex) { DiagnosticLog.Write($"MultiplayerTab.RepaintMatchResult: {ex.Message}"); }
    }

    /// <summary>
    /// Tells the host why a match went down without a result, and what to do about it.
    ///
    /// <para><b>Why this is worth a message at all.</b> Age of Empires III keeps a "Record Game"
    /// box on its own setup screen, separate from the one in Options that the launcher sets, and
    /// nothing here can see it. So a match can end with recording enabled in the profile and no
    /// recording written — and before this, the only trace was a line in the debug log while the
    /// player watched their game get stored as a draw.</para>
    ///
    /// <para>The profile is read back so the message names the actual cause rather than listing
    /// possibilities: still on means the per-match box, switched off means the game overwrote us
    /// when it exited. Only fires under the conditions where a recording SHOULD exist — the host,
    /// a real match, recording wanted — and only when none was found at all. A team game whose
    /// result simply could not be derived is not a recording failure and says nothing here.</para>
    ///
    /// <para>Toast first: a successful report closes the room, which tears the lobby window down
    /// for everyone, so a chat line can be gone milliseconds after it is written.</para>
    /// </summary>
    private void MaybeReportMissingRecording(
        ModProfile profile, Services.Multiplayer.MatchContext? ctx, MatchReplayInfo? replay)
    {
        if (replay != null) return;
        if (_config?.EnableGameRecording != true) return;
        // Same question the report asks, so the two can't disagree about whether this was a real
        // host-side match — and asked of the captured context, not the room we may have left.
        if (ctx == null || !ctx.CanReport(DateTime.UtcNow, MinReportableSeconds).Ok) return;

        var key = "MpNoRecordingUnknown";
        try
        {
            var path = Services.GameSettingsStore.ProfilePathFor(profile, _config);
            var current = path == null
                ? null
                : GameSettingsSync.ReadSetting(
                    System.IO.File.ReadAllText(path, System.Text.Encoding.Unicode),
                    GameSettingsSync.GameOptionsSection, GameSettingsSync.RecordGameSetting);

            if (string.Equals(current, "true", StringComparison.OrdinalIgnoreCase)) key = "MpNoRecordingCheckbox";
            else if (string.Equals(current, "false", StringComparison.OrdinalIgnoreCase)) key = "MpNoRecordingProfileOff";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"MultiplayerTab.MaybeReportMissingRecording: {ex.Message}");
        }

        var body = Strings.Get(key);
        DiagnosticLog.Write($"MultiplayerTab.MaybeReportMissingRecording: {key}");
        AppendChatSystem(body);
        try
        {
            _showAppToast?.Invoke(new AppToast.ToastOptions(
                "⚠", Strings.Get("MpNoRecordingTitle"), body,
                System.Array.Empty<AppToast.ToastAction>(), AutoDismissMs: 12000));
        }
        catch (Exception ex) { DiagnosticLog.Write($"Missing-recording toast failed: {ex.Message}"); }
    }

    /// <summary>
    /// Host-only, best-effort report of the just-finished match to the backend
    /// (<c>POST /matches</c>). Populates every participant's History tab with an
    /// unranked record: mod, player count, duration, timestamps. Uses the
    /// lobby_id branch, which the backend host-validates and which CLOSES the
    /// room (+ fires the Discord "match ended" webhook) — the maintainer's
    /// "close the room when the match ends" choice. Never throws; a failure
    /// (offline, room already GC'd, non-host) is swallowed with a log line so
    /// the post-match flow is unaffected.
    ///
    /// <para><paramref name="replay"/> carries what the recording said, when there was one:
    /// the map, and the host's score in a 1v1. Everything it can't answer stays the 0.5
    /// draw this used to send unconditionally.</para>
    /// </summary>
    /// <returns>True only when a successful report CLOSED the room; false on any
    /// skip (not host / &lt;2 players / &lt;3 min) or failure — the caller then sends
    /// game_ended so the room reverts to open instead of staying stuck in_game.</returns>
    /// <summary>
    /// What reporting did. <see cref="ClosedRoom"/> keeps the exact meaning the old
    /// boolean had — the caller decides whether to send <c>game_ended</c> from it, so its
    /// semantics must not drift. <see cref="Response"/> is the addition: the POST answers
    /// with every participant's rating change, and until now that was thrown away, which
    /// is why the end-of-match card would otherwise need a second HTTP request to learn
    /// something the server had already told us.
    /// </summary>
    private sealed record ReportOutcome(bool ClosedRoom, ReportMatchResponse? Response);

    /// <summary>
    /// Send our own reading of a match somebody else is reporting.
    ///
    /// <para>Reporting is host-only and stays that way — every client reaches the end of
    /// the match, and N reporters would insert N copies of it. But the guest's launcher
    /// has been reading its own recording all along and discarding the answer, and two
    /// honest recordings of one match cannot disagree: the trailer names winner and loser
    /// by ABSOLUTE slot, so this is a real second measurement of the same fact rather than
    /// an echo of the first.</para>
    ///
    /// <para><b>It can DECIDE the match, in one bounded case.</b> This started as evidence
    /// that gated nothing, and that is no longer what it is: the server now rates a match
    /// the host stored with <c>no_decided_result</c> from this reading alone — the recording
    /// the host never had — under an asymmetric rule, conceding your own defeat freely and
    /// claiming your own victory only when the fingerprint the reporter already stored
    /// matches yours. Which is why the seed and the hash below are not optional extras: they
    /// are the corroboration. Nothing else is rescuable this way; a team game or an unranked
    /// mod stays unrated whatever anybody read.</para>
    ///
    /// <para>Best-effort in every direction, like the report itself: this runs while the
    /// player is watching their game close, and it must never be why that goes wrong.</para>
    /// </summary>
    /// <param name="allowHost">
    /// Let the HOST send one too. Off by default and for a good reason — confirming your own
    /// report against itself proves nothing — but there is one case where it is new information
    /// rather than an echo: the report now goes out on the first pass, so a host can report
    /// 0.5-0.5 and find his recording seconds later. That reading has never reached the server
    /// in any form, and it is the only thing that can decide the match.
    /// </param>
    /// <summary>
    /// Every player's score in a team match, or null when the sides cannot be established.
    ///
    /// <para>Shared by the host's REPORT and by every player's CONFIRMATION, and that sharing
    /// is the point: the two are meant to be independent readings of the same file, so they
    /// must apply the same rule to it. If they did not, two honest players could contradict
    /// each other over a match they both read correctly — and this rule's whole purpose is to
    /// make a contradiction mean something.</para>
    /// </summary>
    private static System.Collections.Generic.IReadOnlyDictionary<string, double>? ResolveTeamResults(
        Services.Multiplayer.MatchContext ctx,
        MatchReplayInfo? replay,
        System.Collections.Generic.IReadOnlyDictionary<string, int>? teams)
    {
        if (!Services.Multiplayer.RoomFormats.IsTeam(ctx.Format)) return null;

        teams ??= Services.Multiplayer.MatchTeamMap.Resolve(replay?.Players, ctx.InGameNames);
        if (teams == null) return null;
        if (!Services.Multiplayer.RoomFormats.TeamsAgreeWithFormat(ctx.Format, teams)) return null;

        return Services.Multiplayer.MatchResultResolver.ResolveTeamResults(
            teams, ctx.InGameNames, replay?.Players, replay?.LoserSlot ?? -1);
    }

    /// <summary>
    /// The recording's casualty list with each one's side, LAST ELIMINATION FIRST, for the
    /// diagnostic line above — never for a decision.
    ///
    /// <para>It answers the two questions a single measured file could not: whether AoE3
    /// writes one outcome block per casualty, and whether the losing side appears whole.
    /// Everything about it is best-effort — an unresolvable slot prints <c>?</c> rather than
    /// dropping out, because a name the room never published is exactly the kind of thing
    /// this is meant to show.</para>
    /// </summary>
    private static string DescribeEliminations(
        MatchReplayInfo? replay,
        Services.Multiplayer.MatchContext ctx,
        System.Collections.Generic.IReadOnlyDictionary<string, int>? teams)
    {
        var slots = replay?.EliminatedSlots;
        if (slots == null || slots.Count == 0) return "none";

        var parts = new System.Collections.Generic.List<string>();
        foreach (var slot in slots)
        {
            var name = replay?.Players?.FirstOrDefault(p => p.Slot == slot)?.Name;
            var side = "?";
            if (!string.IsNullOrWhiteSpace(name) && teams != null && ctx.InGameNames != null)
            {
                foreach (var (userId, declared) in ctx.InGameNames)
                {
                    if (!string.Equals(declared?.Trim(), name!.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (teams.TryGetValue(userId, out var t)) side = t.ToString();
                    break;
                }
            }
            parts.Add($"{slot}:{(string.IsNullOrWhiteSpace(name) ? "?" : name)}=t{side}");
        }
        return string.Join(" <- ", parts);
    }

    private async Task TryConfirmMatchAsync(
        Services.Multiplayer.MatchContext? ctx, MatchReplayInfo? replay, bool allowHost = false)
    {
        if (ctx == null) return;
        // The host's reading is the report; confirming it against itself proves nothing.
        if (ctx.IsHost && !allowHost) return;
        if (string.IsNullOrEmpty(ctx.LobbyId)) return;
        if (_session?.Api == null) return;

        try
        {
            // NOTE ON THE NAME: MatchReplayInfo.HostResult means "the result of whoever
            // recorded THIS file", not "the host of the room" — AnalyseMatchReplayAsync
            // identifies the recording by THIS machine's own profile name and slot. On the
            // host's machine that is the host; here it is us. See the comment on the record.
            // In a TEAM match HostResult is always null — ReadOutcome refuses to name a
            // winner past two players — so without this every confirmation of a 2v2 would
            // be a 0.5, land as 'inconclusive', and the evidence rule could never be
            // satisfied by anybody. The team ladder would be built and never move.
            var ownId = _session.CurrentUser?.Id;
            var teamResults = ResolveTeamResults(ctx, replay, null);
            double? ownTeamResult =
                teamResults != null && ownId != null && teamResults.TryGetValue(ownId, out var tr)
                    ? tr
                    : null;

            var ownResult = ownTeamResult
                            ?? replay?.HostResult
                            ?? Services.Multiplayer.MatchResultResolver.Unknown;

            // 0.5 is sent, not swallowed. How often a player cannot read their own
            // recording is exactly the number that decides whether agreement could ever be
            // required, and staying quiet about it would leave the evidence counting only
            // the matches that went well.
            string? replaySha = null;
            if (replay?.File != null)
            {
                try { replaySha = await HashService.ComputeSha256Async(replay.File.FullName); }
                catch (Exception hashEx)
                {
                    DiagnosticLog.Write(
                        $"MultiplayerTab.TryConfirmMatchAsync: could not hash '{replay.File.Name}' — {hashEx.Message}");
                }
            }

            var resp = await _session.Api.ConfirmMatchAsync(new ConfirmMatchRequest
            {
                LobbyId = ctx.LobbyId!,
                Result = ownResult,
                ReplaySha256 = replaySha,
                // The half that makes this a real cross-check rather than two opinions
                // about possibly different games: the seed is shared by both players of
                // one match, so the server can tell whether we read the same one.
                GameSeed = replay is { RandomSeed: > 0 } ? replay.RandomSeed : null,
                GameHostTime = replay is { HostTime: > 0 } ? replay.HostTime : null,
            });

            DiagnosticLog.Write(
                $"MultiplayerTab.TryConfirmMatchAsync: sent own reading {ownResult:0.0} " +
                $"for lobby={ctx.LobbyId} (host had already reported: {resp?.Matched})");
        }
        catch (Exception ex)
        {
            // Including a 403 from a room whose roster we are not in, and a 404 from one
            // the sweep already removed. Nothing here is worth telling the player about:
            // it changes nothing they can see.
            DiagnosticLog.Write($"MultiplayerTab.TryConfirmMatchAsync: {ex.Message}");
        }
    }

    private async Task<ReportOutcome> TryReportMatchAsync(
        ModProfile profile, Services.Multiplayer.MatchContext? ctx, MatchReplayInfo? replay)
    {
        // Every skip below is LOGGED with its reason — before this, a match that
        // didn't record looked identical whether it was skipped (not host, too
        // short, solo) or failed (404/403/offline), so "nothing happened" was
        // undiagnosable. The reason line in launcher-debug.log is the first thing
        // to check when a real game doesn't show up in History.

        // The four gates that describe the MATCH — are we its host, is it a real multiplayer
        // game of a real length, do we know which room and mod — live in MatchContext.CanReport,
        // which cannot see live room state and therefore cannot be changed by leaving the room.
        // That is the entire point: the version of this method that asked those questions of the
        // session lost a real match because the host closed the room a moment before the game
        // exited. "Only the host reports" is still the load-bearing one — the exit runs on EVERY
        // player's client and the POST writes a row per participant, so a second reporter means
        // the same match twice.
        if (ctx == null)
        {
            DiagnosticLog.Write("MultiplayerTab.TryReportMatchAsync: skipped — no match context");
            return new ReportOutcome(false, null);
        }

        var endedAt = DateTime.UtcNow;
        var (ok, reason) = ctx.CanReport(endedAt, MinReportableSeconds);
        if (!ok)
        {
            DiagnosticLog.Write($"MultiplayerTab.TryReportMatchAsync: skipped — {reason}");
            return new ReportOutcome(false, null);
        }

        // Live on purpose, and the exception that proves the rule: these are what the POST needs
        // in order to be SENT (an authenticated client and the hash routine), not facts about
        // the match. If the session is gone there is nothing to send with, whatever the context
        // says about the game that was played.
        if (_session?.Api == null || _computeModFingerprint == null)
        {
            DiagnosticLog.Write("MultiplayerTab.TryReportMatchAsync: skipped — no session / fingerprint hook");
            return new ReportOutcome(false, null);
        }

        // Non-null past this point: CanReport refuses a context missing either of them, which is
        // the check the compiler can't see through.
        var lobbyId = ctx.LobbyId!;
        var modId = ctx.ModId!;
        var participantIds = ctx.Participants;
        var durationSeconds = ctx.DurationSeconds(endedAt);

        try
        {
            var hash = await _computeModFingerprint(profile);
            var hostId = ctx.ReporterUserId;
            var decision = Services.Multiplayer.MatchResultResolver.ResolveHostResult(
                replay?.HostResult, participantIds, hostId);
            var hostResult = decision.Result;
            if (hostResult == null)
                DiagnosticLog.Write(
                    $"MultiplayerTab.TryReportMatchAsync: no result — {decision.Reason}; logged as a draw");

            // Who played on which side. The recording knows the teams; the room knows the
            // accounts; the AoE3 profile name each player published is the only thing that joins
            // them. Null — a 1v1, a free-for-all, or one member who never reported a name — means
            // the match goes down with no teams, which is what every match before this one did.
            // From the CONTEXT, not the live roster: by now the loser has usually left the
            // room and their name would be gone with them, which would refuse the map for the
            // exact matches it exists for. Same reason the roster itself is frozen at Start.
            var teams = Services.Multiplayer.MatchTeamMap.Resolve(replay?.Players, ctx.InGameNames);

            // The room's format was a promise; this is where it is kept. Sides that do not match
            // what the room said it would play are dropped rather than written into everyone's
            // history — a 2v2 room actually played 1v3 would otherwise record real-but-wrong
            // teams for four people, and nothing downstream could tell.
            if (!Services.Multiplayer.RoomFormats.TeamsAgreeWithFormat(ctx.Format, teams))
            {
                DiagnosticLog.Write(
                    $"MultiplayerTab.TryReportMatchAsync: the recording's teams ({DescribeTeams(teams)}) " +
                    $"do not match the room's declared {ctx.Format} — reporting without teams.");
                teams = null;
            }

            // A team match's scores come from the SIDE the recording names, never from the
            // host's own row: ParticipantResult mirrors the host onto everybody else, which
            // in a 2v2 marks the host's own teammate a loser. Null falls back to that mirror,
            // and for a 1v1 this block does not run at all.
            var teamResults = ResolveTeamResults(ctx, replay, teams);

            // A recording of MORE THAN TWO humans does carry outcome blocks — measured, on a
            // four-player game that turned out to hold two of them. What is still unmeasured
            // is a real TEAM game: that file was a free-for-all, so it says nothing about
            // whether the losing SIDE appears whole, or whether one block is written per
            // casualty. This line is how the first real team matches answer it, so do not
            // remove it until they have: with no usable block the match reports 0.5 for
            // everyone and the team ladder never moves, and nothing else would say why.
            if (Services.Multiplayer.RoomFormats.IsTeam(ctx.Format))
            {
                DiagnosticLog.Write(
                    $"MultiplayerTab.TryReportMatchAsync: {ctx.Format} recording — " +
                    $"loserSlot={replay?.LoserSlot ?? -1} teams={DescribeTeams(teams)} " +
                    $"sides={(teamResults == null ? "unresolved" : "resolved")} " +
                    $"eliminations={DescribeEliminations(replay, ctx, teams)}");
            }

            // Which SLOT each account played. Deliberately not MatchTeamMap: that one refuses
            // every slot whose teamid is negative, which is what all fourteen measured 1v1
            // recordings carry — asking it would leave the civilization empty for exactly the
            // matches that rate. Same join, without the team rules.
            // Resolved ONCE and used twice: the same slot-to-player map answers which civ each
            // player picked and which home city they brought.
            var slots = Services.Multiplayer.MatchSlotMap.Resolve(replay?.Players, ctx.InGameNames);
            var civs = ResolveCivNames(profile, slots);
            var homeCities = ResolveHomeCities(slots);

            // Hashed here rather than server-side, because the server never sees the
            // file. Best-effort: a recording we cannot read is not a reason to lose the
            // match report, it just means this one carries no fingerprint.
            string? replaySha = null;
            if (replay?.File != null)
            {
                try { replaySha = await HashService.ComputeSha256Async(replay.File.FullName); }
                catch (Exception hashEx)
                {
                    DiagnosticLog.Write(
                        $"MultiplayerTab.TryReportMatchAsync: could not hash '{replay.File.Name}' — {hashEx.Message}");
                }
            }

            var req = new ReportMatchRequest
            {
                LobbyId = lobbyId,
                ModId = modId,
                ModCombinedHash = hash,
                MapName = replay?.MapName,
                // The pool the map came from, parsed all along and stored by nobody. Empty
                // string is normalised away: the parser returns "" for a key the header did
                // not carry, and a blank pool is not a pool.
                MapPool = string.IsNullOrWhiteSpace(replay?.MapPool) ? null : replay!.MapPool,
                StartedAt = ctx.StartedAtUtc.ToString("o"),
                EndedAt = endedAt.ToString("o"),
                DurationSeconds = durationSeconds,
                // 0.5 across the board is the fallback, not the design: it is what every
                // match got before the recording could be read, and it is still what a
                // team game, an unreadable recording or a refused one gets.
                //
                // Civ is the NAME now, never the raw index. The recording carries an index that
                // means different civilizations in different mods, so CivNameResolver turns it
                // into what that mod calls it — and stays null whenever it cannot be sure, which
                // is what this field was for every match before it existed.
                Participants = participantIds.Select(id => new MatchParticipantReport
                {
                    UserId = id,
                    // 0 for everyone when the map refused — see MatchTeamMap, where every rule is
                    // a refusal, because a HALF-filled map would put a real person on the wrong
                    // side of a real match in somebody else's history.
                    Team = teams != null && teams.TryGetValue(id, out var t) ? t : 0,
                    Civ = civs != null && civs.TryGetValue(id, out var civ) ? civ : null,
                    // The home CITY, which is as far as a recording goes: it names the deck's
                    // file and never which of that file's decks was used, and the cards
                    // themselves are on that player's own machine.
                    HomeCity = homeCities != null && homeCities.TryGetValue(id, out var city)
                        ? city
                        : null,
                    Score = 0,
                    Result = teamResults != null && teamResults.TryGetValue(id, out var tr)
                        ? tr
                        : hostResult == null
                            ? Services.Multiplayer.MatchResultResolver.Unknown
                            : Services.Multiplayer.MatchResultResolver.ParticipantResult(
                                hostResult.Value, id == hostId),
                }).ToList(),
                ReplaySha256 = replaySha,
                // Null rather than 0 when the recording did not carry them: the server
                // indexes this pair to stop one game scoring twice, and a row of zeroes
                // would collide with every other recording that also lacked them.
                GameSeed = replay is { RandomSeed: > 0 } ? replay.RandomSeed : null,
                GameHostTime = replay is { HostTime: > 0 } ? replay.HostTime : null,
            };

            var response = await _session.Api.ReportMatchAsync(req);

            // The match just moved the rating, so the cached standing is stale. This covers the
            // case where the report does NOT end in a result phase; when it does, EnterResultPhase
            // both drops it and re-fetches, because the card puts that tally on screen.
            _cachedStanding = null;

            DiagnosticLog.Write(
                $"MultiplayerTab.TryReportMatchAsync: reported match lobby={lobbyId} " +
                $"players={participantIds.Count} duration={durationSeconds}s " +
                $"map='{replay?.MapName}' teams={DescribeTeams(teams)} " +
                $"hostResult={(hostResult.HasValue ? hostResult.Value.ToString("0.0") : "draw (no result)")} " +
                $"rated={response.Rated} reason={response.UnratedReason ?? "-"}");
            // Visible confirmation — and it has to survive the room closing, which is what
            // success itself causes, so it goes through the helper that falls back to a toast
            // when the lobby window is already gone.
            var recorded = Strings.Format("MpChatMatchRecorded", participantIds.Count);
            if (hostResult.HasValue)
                recorded += " " + Strings.Get(
                    hostResult.Value > 0.5 ? "MpChatMatchResultWin" : "MpChatMatchResultLoss");
            AnnounceMatchOutcome(recorded, Strings.Get("MpMatchReportedTitle"), "🏆");
            return new ReportOutcome(true, response);   // succeeded → backend closed the room
        }
        catch (LobbyApiException apiEx)
        {
            // On failure the room STAYS OPEN (no close happened), so this chat
            // line is actually visible to the host. Surface the HTTP status/code
            // so a failed report is diagnosable at a glance: 404 = room already
            // gone, 403 = host migrated mid-game, 401 = session expired, etc.
            DiagnosticLog.Write(
                $"MultiplayerTab.TryReportMatchAsync: report FAILED status={apiEx.Status} code={apiEx.Code} — {apiEx.Message}");
            AnnounceMatchOutcome(
                Strings.Format("MpChatMatchNotRecorded", apiEx.Status, apiEx.Code),
                Strings.Get("MpMatchNotReportedTitle"), "⚠");
            return new ReportOutcome(false, null);   // failed → room open (caller sends game_ended)
        }
        catch (Exception ex)
        {
            // Offline / transient (no HTTP status). Still surface it.
            DiagnosticLog.Write($"MultiplayerTab.TryReportMatchAsync: report FAILED — {ex.Message}");
            AnnounceMatchOutcome(
                Strings.Format("MpChatMatchNotRecorded", 0, "offline"),
                Strings.Get("MpMatchNotReportedTitle"), "⚠");
            return new ReportOutcome(false, null);
        }
        // The context is deliberately NOT cleared here. A finally in this method sits below the
        // early returns above, so a joiner — who leaves at the very first of them — kept the
        // previous match's roster forever. OnGameExitedAsync owns the clear now: it is the end of
        // the match on every client, not just the host's.
    }

    // The room-shape rule that used to live here is now
    // Services.Multiplayer.MatchResultResolver — pure and unit-tested, because it is the one
    // line where a mistake moves rating points between two real people.

    /// <summary>
    /// "Don't show this again" on the Record Game band — the ONLY thing that silences that
    /// reminder, here and in the chat line at launch, since both read the same flag.
    ///
    /// <para>Deliberately an explicit action and never an inference. Gating it on "a recording
    /// turned up, so it must be working" was the original design and it was wrong: AoE3's
    /// per-match box does not inherit from anything the launcher writes, so one match that
    /// recorded says nothing about the next, and that rule would have gone quiet exactly when
    /// the player still needed telling.</para>
    /// </summary>
    /// <summary>
    /// The two-item "before you start" checklist that replaced the amber reminder band
    /// and its "don't show this again".
    ///
    /// <para><b>The first item is honest but not verified.</b> It ticks because everyone in
    /// the room passed <c>POST /lobbies/:id/join</c>, which rejects a mismatched
    /// <c>mod_combined_hash</c> with <c>mod_mismatch</c> — so the claim follows from the
    /// fact that they are here. What the launcher CANNOT do is check it per member: the
    /// room-state frame carries no per-member hash. Anyone adding one should tick this
    /// from that, not from the count.</para>
    ///
    /// <para><b>The second never ticks, and that is the point.</b> AoE3's per-match Record
    /// Game box is the thing that decides whether the match has a winner, it comes up
    /// unticked every time, and nothing here can read it. It also can no longer be
    /// silenced — but it now costs two lines instead of seven, which is what made the old
    /// band worth silencing.</para>
    ///
    /// <para><b>The third is a RULE rather than a task, and it is here because the rule was
    /// written in exactly one place the person it punishes never sees.</b> The abandonment
    /// penalty is spelled out in the create-room dialog's hint — read only by the HOST. A
    /// guest got the competitive badge, whose tooltip says "this match counts towards the
    /// rating", and nothing more. The first player it cost 176 points had left at 4:40 of a
    /// match he had no way of knowing carried a five-minute forfeit line. Showing it here
    /// is the other half of fixing that; the backend half is that the rule now measures
    /// when he LEFT rather than when the host reported (see src/elo/abandon.ts).</para>
    ///
    /// <para>Reading <see cref="_currentLobbyIsCompetitive"/> — LIVE room state — is correct
    /// here and only here: this is a pre-match surface shown while the room exists, the same
    /// field and the same method that already drive the competitive badge. The rule about
    /// never reading it live belongs to the POST-match path, where the room may be gone.</para>
    ///
    /// <para>Called from <see cref="RenderRoomPanel"/> rather than once at open, so a host
    /// migration re-words it for the new host for free.</para>
    /// </summary>
    private void RefreshPreflightChecklist()
    {
        if (_lobbyWindow == null) return;

        _lobbyWindow.PreflightModsText.Text =
            Strings.Format("MpPreflightModsMatch", Math.Max(1, _roomMembers.Count));

        // Two Runs so the checkbox's own name stands out inside a localized sentence. It
        // stays English because that is what AoE3 shows on its setup screen.
        var name = Strings.Get("MpCreateDialogRecordWarnName");
        var parts = Strings.Format("MpPreflightRecordGame", "\u0000").Split('\u0000');
        var text = _lobbyWindow.PreflightRecordText;
        text.Inlines.Clear();
        text.Inlines.Add(new System.Windows.Documents.Run(parts[0]));
        text.Inlines.Add(new System.Windows.Documents.Run(name)
        {
            Foreground = (Brush)Application.Current.FindResource("MpCautionTextAlt"),
            FontWeight = FontWeights.SemiBold,
        });
        if (parts.Length > 1) text.Inlines.Add(new System.Windows.Documents.Run(parts[1]));

        // 1v1 ONLY, not "competitive" — and the difference is not cosmetic. decideByAbandon
        // refuses anything but two participants, so in a 2v2 or 3v3 room this line threatened a
        // forfeit the server does not carry out. It said "competitive" when it was written,
        // before a room could declare a team format.
        _lobbyWindow.PreflightAbandonText.Text = Strings.Get("MpPreflightAbandon");
        _lobbyWindow.PreflightAbandonRow.Visibility =
            Services.Multiplayer.RoomFormats.AbandonmentApplies(CurrentRoomFormat())
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>Last time the room was announced in the global chat, for the cooldown.</summary>
    private long _lastSoloAnnounceTicks;

    /// <summary>How long the announce button stays spent after a click.</summary>
    private const long SoloAnnounceCooldownMs = 10_000;

    /// <summary>
    /// Copy the room code so it can be pasted to somebody. Mirrors the header's own copy
    /// button, including the tick that confirms it worked.
    /// </summary>
    private void CopyRoomCodeFromSolo()
    {
        var code = _session?.CurrentLobbyId;
        if (string.IsNullOrEmpty(code) || _lobbyWindow == null) return;
        try
        {
            System.Windows.Clipboard.SetText(code);
            _lobbyWindow.InGameSoloCopyButton.Content = Strings.Get("MpRoomCopied");
        }
        catch (Exception ex) { DiagnosticLog.Write($"Copying the room code failed: {ex.Message}"); }
    }

    /// <summary>
    /// Say in the global chat that this room is open and short of players.
    ///
    /// <para><b>The cooldown is not politeness, it is protection.</b> Global chat enforces
    /// a 1.5 s slow mode and mutes a sender for 30 s after five violations, so an impatient
    /// double-click would silence the very player who is trying to find an opponent. The
    /// button is spent for ten seconds after a click, and disabled outright when there is
    /// no chat socket to send on.</para>
    /// </summary>
    private void AnnounceRoomInGlobalChat()
    {
        if (_lobbyWindow == null) return;
        var now = Environment.TickCount64;
        if (now - _lastSoloAnnounceTicks < SoloAnnounceCooldownMs) return;
        if (_globalChatSocket == null)
        {
            _lobbyWindow.InGameSoloAnnounceButton.IsEnabled = false;
            return;
        }

        _lastSoloAnnounceTicks = now;
        _lobbyWindow.InGameSoloAnnounceButton.IsEnabled = false;
        var mod = ResolveModDisplayName(_currentLobbyModId ?? "");
        var code = _session?.CurrentLobbyId ?? "";
        _ = _globalChatSocket.SendChatAsync(Strings.Format("MpAnnounceRoomInGlobal", mod, code));

        // Re-armed on the tick that passes the cooldown, so the button explains itself by
        // being unavailable rather than by silently ignoring the click.
        _lobbyWindow.InGameSoloAnnounceButton.Content = Strings.Get("MpInGameSoloAnnounced");
    }

    /// <summary>
    /// Where AoE3 keeps the per-match recording checkbox. A notice rather than a setting:
    /// there is nothing the launcher can toggle here, which is the whole reason the
    /// checklist item exists.
    /// </summary>
    private void ShowRecordHelp()
        => _ = MpAlertOverlay.NoticeAsync(
            TabRootGrid,
            Strings.Get("MpPreflightHelpTitle"),
            Strings.Get("MpPreflightHelpBody"),
            Strings.Get("MpAlertOk"));

    /// <summary>
    /// Invite someone to this room. Reuses the players panel's own invite path — the
    /// right-click menu there is where a player is picked — so this points at it rather
    /// than building a second chooser.
    /// </summary>
    private void ShowInviteHint()
        => _ = MpAlertOverlay.NoticeAsync(
            TabRootGrid,
            Strings.Get("MpRoomInviteTitle"),
            Strings.Get("MpRoomInviteBody"),
            Strings.Get("MpAlertOk"));

    // ==================================================================
    // Lobby window lifecycle (single-instance open/close)
    // ==================================================================
    //
    // The whole "floating popup drag + resize + Canvas position" block
    // that used to live here is gone — the lobby is now a real
    // top-level Window (LobbyWindow.xaml) with native OS chrome that
    // handles drag, resize and edge clamping for free. What's left is
    // just the open/close lifecycle:
    //
    //   • OpenLobbyWindow() is idempotent. If a window already exists,
    //     Activate()s it (so a duplicate Create/Join click brings the
    //     existing one to front instead of spawning a second). The
    //     callbacks point each click handler back to the methods that
    //     used to be wired via XAML Click="…" — the logic itself
    //     stayed in this class for now (close coupling with
    //     MultiplayerSession state, telemetry, etc.); the Window is
    //     a thin forwarder.
    //   • CloseLobbyWindow() is idempotent. The Closed event handler
    //     fires HandleLobbyWindowClosed which nulls the field and (if
    //     we're still in a session-tracked room) triggers the
    //     leave-room flow — same single rendezvous point regardless
    //     of how the user dismissed (✕ / Esc / Alt+F4 / our own Close).

    private void OpenLobbyWindow()
    {
        if (_lobbyWindow != null)
        {
            _lobbyWindow.Activate();
            return;
        }
        if (_session == null) return;

        var w = new LobbyWindow(_session)
        {
            // No Owner: the lobby is an INDEPENDENT top-level window with its
            // own Windows taskbar button (ShowInTaskbar=True). Minimizing the
            // launcher doesn't hide it, and it isn't pinned above the launcher
            // — the user can alt-tab / move it to another monitor freely.

            // Click forwarders. The handler bodies stayed in this
            // class (where the Multiplayer state lives); LobbyWindow's
            // XAML buttons fire Action callbacks instead of using
            // XAML Click="…" wires.
            OnLeaveRoom = () => LeaveRoomButton_Click(this, new RoutedEventArgs()),
            OnReady = () => ReadyButton_Click(this, new RoutedEventArgs()),
            OnStart = () => StartButton_Click(this, new RoutedEventArgs()),
            OnInGameCancel = () => InGameCancelButton_Click(this, new RoutedEventArgs()),
            OnRejoinGame = RejoinGame,
            OnRenameRoom = () => _ = RenameRoomAsync(),
            OnClearChat = () => ClearChatButton_Click(this, new RoutedEventArgs()),
            OnInvitePlayers = ShowInviteHint,
            OnRecordHelp = ShowRecordHelp,
            OnCopyRoomCode = CopyRoomCodeFromSolo,
            OnAnnounceRoom = AnnounceRoomInGlobalChat,
            OnSendChat = () => ChatSendButton_Click(this, new RoutedEventArgs()),
            OnEmoji = () => ChatEmojiButton_Click(this, new RoutedEventArgs()),
            // The existing TextChanged / KeyDown handlers take WPF
            // routed event args we don't construct here — call them
            // through with a synthetic args object (the args aren't
            // read by the handler bodies, only Key on KeyDown).
            OnChatTextChanged = () => ChatInputBox_TextChanged(this, null!),
            OnChatKeyDown = e => ChatInputBox_KeyDown(this, e),

            // Closing the lobby is how the room got destroyed mid-match once, in silence.
            // The hold is included, or closing the window would slip past a question the Leave
            // button asks: OnClosing only consults ConfirmLeave when this says there is something
            // to ask, so a hold with no warning beside it would let the ✕ walk straight out.
            NeedsLeaveConfirm = () => ResultHoldActive()
                || CurrentLeaveWarning() != Services.Multiplayer.RoomMatchState.LeaveWarning.None,
            ConfirmLeave = ConfirmLeaveRoomAsync,
        };

        _lobbyWindow = w;

        // Localise the static labels and paint the current room state
        // before Show() so there's no English/empty flash on open.
        ApplyLobbyStaticLabels();
        RenderRoomPanel();
        UpdateChatEmptyState();

        // Poll the connection ping while the lobby is open so the header's
        // CONNECTION stat stays live even before a match starts. ~2.5 s
        // cadence; KickConnectionPing guards against overlapping probes.
        _lobbyPingTimer?.Stop();
        _lobbyPingTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(2500),
        };
        _lobbyPingTimer.Tick += (_, _) =>
        {
            KickConnectionPing();
            UpdateLobbyPing();
            RefreshLobbyOpenAge();
            // Also probe peers pre-match so the roster health dots are live before
            // anyone hits Start (needs our own IP reported first).
            MaybeReportRadminIp();
            KickPeerPings();
            RefreshRosterLiveCells();
        };
        _lobbyPingTimer.Start();
        // Re-announce our Radmin IP to THIS room's socket immediately. The dedup
        // guard is per-launcher-session (only reset in EnterInGamePhase), so
        // entering a SECOND room in one session with the same IP would otherwise
        // early-return in MaybeReportRadminIp (Equals true) → set_radmin_ip never
        // reaches the new socket → we'd read "Esperando VPN" forever in room #2.
        // Clearing it here makes every room re-report; the immediate call also
        // kills the ~2.5 s "Esperando VPN" flicker before the first timer Tick.
        _lastReportedRadminIp = null;
        MaybeReportRadminIp();
        // Same reset, same reason: see MaybeReportInGameName.
        _lastReportedInGameName = null;
        MaybeReportInGameName();
        KickConnectionPing();
        UpdateLobbyPing();

        // Race-safe field clear in Closed: a follow-up OpenLobbyWindow
        // call between Close() and Closed firing must not clobber the
        // new instance, so we only null the field if it still points
        // at THIS window.
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(_lobbyWindow, w))
                _lobbyWindow = null;
            HandleLobbyWindowClosed();
        };

        w.Show();
    }

    private void CloseLobbyWindow()
    {
        if (_lobbyWindow == null) return;
        var stale = _lobbyWindow;
        // Null the field FIRST so any in-flight render that races
        // with Close() sees "no window" instead of a half-disposed
        // one. The Closed handler's ReferenceEquals guard makes the
        // null-then-Close ordering safe.
        _lobbyWindow = null;
        // Every caller of this method is a close the LAUNCHER decided on — kicked, signed out,
        // the tab tearing the room down. None of them is the player choosing to leave, so none of
        // them may ask the player to confirm it.
        stale.SuppressLeaveConfirm();
        stale.Close();
    }

    /// <summary>
    /// What leaving the room right now would cost, from live state — the two flags the rule needs
    /// are exactly the ones that describe "is my game running" and "is the room's match running".
    /// </summary>
    /// <summary>
    /// Make the host say out loud that they will tick Record Game, before a competitive start.
    ///
    /// <para><b>Every match, not once per room</b>, and that is the whole point rather than an
    /// oversight. AoE3's per-match box comes up unticked every single time — measured, twice —
    /// and the launcher has no way to tick it: neither the profile setting nor a <c>+RecordGame</c>
    /// launch argument moves it. So "they were told once" is worth nothing by the third game, and
    /// deriving "it must be working now" from a match that happened to record is precisely the
    /// reasoning that let every match afterwards go unrecorded in silence.</para>
    ///
    /// <para>Not a danger confirm: nothing is being destroyed, and painting it red would teach
    /// people to click through the ones that are.</para>
    /// </summary>

    // ANSWERED, and the probe that asked is gone. AoE3 writes the recording at the END of the
    // match, not at the start — so there is nothing to check at 90 seconds and the idea of
    // warning a host mid-match that nothing is recording is dead. It is recorded here rather
    // than deleted with the code, because the next person to have the idea deserves the
    // measurement instead of a second round of instrumentation.
    //
    // The evidence is direct, and it is not the probe's own count. In one player's bundle three
    // competitive matches each produced a recording whose last-write time was 28, 42 and 54
    // seconds BEFORE the launcher analysed it — and that analysis runs the instant the game
    // process exits. Three out of three, written as the match ended.
    //
    // The probe's counts were consistent with that (0, 1, 0 recordings newer than the launch),
    // and the 1 has no explanation: the only recording of that session carried a timestamp two
    // minutes and twenty seconds EARLIER than the launch it was compared against. Left written
    // down rather than smoothed over. It does not move the conclusion — a file's timestamp is
    // direct evidence and a count is not — but if this ever comes up again, that reading is the
    // loose end.

    /// <summary>
    /// Write down whether this competitive match produced a recording, so the next start can lead
    /// with a fact instead of repeating the reminder.
    ///
    /// <para>Asked with the SAME inputs as <see cref="MaybeReportMissingRecording"/> right above
    /// it, so the two can never disagree about whether this was a real host-side match. The rules
    /// live in <see cref="Services.Multiplayer.RecordingMemory"/>; the null it can return means
    /// "we learned nothing here", and must leave the previous memory untouched — otherwise one
    /// casual game in between would quietly clear a warning that had been earned.</para>
    /// </summary>
    private void RememberRecordingOutcome(
        ModProfile profile, Services.Multiplayer.MatchContext? ctx, MatchReplayInfo? replay)
    {
        try
        {
            if (_config == null) return;
            var verdict = Services.Multiplayer.RecordingMemory.Evaluate(
                competitive: ctx?.IsCompetitive == true,
                reportable: ctx?.CanReport(DateTime.UtcNow, MinReportableSeconds).Ok == true,
                recordingFound: replay != null);
            if (verdict == null) return;

            var state = _config.GetState(profile.Id);
            if (state.LastMatchHadNoRecording == verdict) return;
            state.LastMatchHadNoRecording = verdict;
            _config.Save();
            DiagnosticLog.Write(
                $"RecordingMemory: '{profile.Id}' last competitive match " +
                $"{(verdict == true ? "produced NO recording" : "was recorded")}.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"MultiplayerTab.RememberRecordingOutcome: {ex.Message}");
        }
    }

    private async Task<bool> ConfirmRecordGameAsync()
    {
        if (_lobbyWindow == null) return true;

        // Lead with the fact when there is one. The plain reminder stays for everyone else: AoE3's
        // box comes up unticked EVERY match, so the first game of a session is the one most often
        // forgotten and would otherwise get nothing at all.
        // The ROOM's mod, which needn't be the one on the dashboard — the memory is per mod
        // because AoE3's recording setting is per mod profile.
        var profileId = _currentLobbyModId ?? _getActiveProfile?.Invoke()?.Id;
        bool? lastFailed = null;
        if (_config != null && !string.IsNullOrEmpty(profileId)
            && _config.Mods.TryGetValue(profileId!, out var st) && st != null)
        {
            lastFailed = st.LastMatchHadNoRecording;
        }

        if (Services.Multiplayer.RecordingMemory.ShouldEscalate(lastFailed))
        {
            return await MpAlertOverlay.ConfirmAsync(
                _lobbyWindow.LobbyRootGrid,
                Strings.Get("MpStartConfirmRecordTitleAgain"),
                Strings.Get("MpStartConfirmRecordBodyAgain"),
                Strings.Get("MpStartConfirmRecordYes"),
                Strings.Get("MpStartConfirmRecordNo"),
                danger: false);
        }

        return await MpAlertOverlay.ConfirmAsync(
            _lobbyWindow.LobbyRootGrid,
            Strings.Get("MpStartConfirmRecordTitle"),
            Strings.Get("MpStartConfirmRecordBody"),
            Strings.Get("MpStartConfirmRecordYes"),
            Strings.Get("MpStartConfirmRecordNo"),
            danger: false);
    }

    /// <summary>
    /// Say that this build is too old for multiplayer, name the version needed, and offer the
    /// update — which is the only thing the player can do about it.
    ///
    /// <para>The required version comes from the SERVER's own answer (<c>min_version</c>), never
    /// from anything the launcher knows: the launcher cannot know what a backend it has not been
    /// updated for requires, and inventing a number here would send people looking for a release
    /// that may not be the one that matters.</para>
    /// </summary>
    private async Task ShowLauncherTooOldAsync(LobbyApiException? ex)
    {
        var min = "";
        if (ex?.Details != null && ex.Details.TryGetValue("min_version", out var raw))
            min = raw?.ToString() ?? "";

        var body = string.IsNullOrWhiteSpace(min)
            ? Strings.Get("MpNoticeLauncherTooOldBody")
            : Strings.Format("MpNoticeLauncherTooOldBodyVersion", min);

        DiagnosticLog.Write(
            $"Multiplayer refused: launcher too old (server requires '{min}', " +
            $"this build is {Services.LauncherUpdateService.CurrentInformationalTag}).");

        await MpAlertOverlay.NoticeAsync(
            TabRootGrid,
            Strings.Get("MpNoticeLauncherTooOldTitle"),
            body,
            Strings.Get("MpAlertOk"));

        // After the notice, not instead of it: the update dialog is the action, the notice is
        // the explanation, and one without the other leaves the player guessing.
        _onLauncherTooOld?.Invoke(min);
    }

    /// <summary>
    /// Whether the Leave button is currently held shut while the match result is settled.
    ///
    /// <para>Reads <see cref="MatchContext.IsCompetitive"/> — the snapshot taken at launch — and
    /// not <see cref="_currentLobbyIsCompetitive"/>: by the time the game closes the room may be
    /// gone, and asking a room that no longer exists is the bug MatchContext was written for.</para>
    /// </summary>
    private bool ResultHoldActive()
        => Services.Multiplayer.RoomMatchState.HoldLeave(
            ResultContext()?.IsCompetitive ?? false,
            _resultPhase,
            (DateTime.UtcNow - _resultPhaseSinceUtc).TotalSeconds);

    /// <summary>Arm or release the post-match hold. Idempotent; only the transition restamps the clock.</summary>
    private void SetResultPhase(Services.Multiplayer.RoomMatchState.ResultPhase phase)
    {
        if (_resultPhase == phase) return;
        // The clock starts when the hold BEGINS and is deliberately not restarted when the phase
        // advances from reading to sending: the ceiling is on the whole wait, not on each step,
        // or a stalled ladder could keep buying itself another thirty seconds.
        if (_resultPhase == Services.Multiplayer.RoomMatchState.ResultPhase.None)
            _resultPhaseSinceUtc = DateTime.UtcNow;
        _resultPhase = phase;
    }

    private Services.Multiplayer.RoomMatchState.LeaveWarning CurrentLeaveWarning()
        => Services.Multiplayer.RoomMatchState.WarnOnLeave(
            _roomMatchLive,
            _matchPhase == MatchPhase.InGame || _matchPhase == MatchPhase.Starting,
            _isHostInCurrentRoom);

    /// <summary>
    /// Ask before walking out on a live match. True = go ahead and leave.
    ///
    /// <para>Leaving the room used to be the one destructive multiplayer action with no
    /// confirmation at all, and that is how a real match was lost: the host closed the lobby
    /// while everyone was playing, which killed every player's Age of Empires III and — because
    /// the same teardown wiped the room state a moment before the resulting game-exit handler ran
    /// — the match was never even recorded. It is asked here rather than only when the launcher
    /// closes, which is the sole place that ever asked.</para>
    ///
    /// <para>Whose text appears comes from the captured match, not the live flag: if the room has
    /// already fallen over, <c>_isHostInCurrentRoom</c> reads false and the host would be told the
    /// mild guest version of what they are about to do.</para>
    /// </summary>
    private async Task<bool> ConfirmLeaveRoomAsync()
    {
        // Held, not asked. While the result is still being resolved this is not a choice with a
        // downside to weigh: leaving hands the host role to the opponent, and the server then
        // refuses our report outright — so the match is lost for both players with nothing on
        // screen to explain it. Bounded by ResultGraceSeconds, so it can never trap anyone.
        if (ResultHoldActive())
        {
            if (_lobbyWindow != null)
            {
                await MpAlertOverlay.NoticeAsync(
                    _lobbyWindow.LobbyRootGrid,
                    Strings.Get("MpLeaveBlockedTitle"),
                    // Host first, because BOTH of the phase-specific messages are about the
                    // report — "the match will not count for either of you" — and that is simply
                    // false for a guest, who does not report. From his side the whole window is
                    // one thing, whichever phase it is in: the result is seconds away, and
                    // leaving now costs him the sight of it and nothing else.
                    Strings.Get(ResultContext()?.IsHost != true
                        ? "MpLeaveBlockedWaitingHost"
                        : _resultPhase == Services.Multiplayer.RoomMatchState.ResultPhase.SendingResult
                            ? "MpLeaveBlockedReporting"
                            : "MpLeaveBlockedReading"),
                    Strings.Get("MpAlertOk"));
            }
            return false;
        }

        var warning = CurrentLeaveWarning();
        if (warning == Services.Multiplayer.RoomMatchState.LeaveWarning.None) return true;
        if (_lobbyWindow == null) return true;

        var wasHost = _matchContext?.IsHost ?? _isHostInCurrentRoom;
        var body = warning switch
        {
            Services.Multiplayer.RoomMatchState.LeaveWarning.RoomStillPlayingCannotRejoin
                => Strings.Get("MpLeaveDuringMatchCannotRejoin"),
            _ when wasHost => Strings.Get("MpLeaveDuringMatchHost"),
            _ => Strings.Get("MpLeaveDuringMatchGuest"),
        };

        return await MpAlertOverlay.ConfirmAsync(
            _lobbyWindow.LobbyRootGrid,
            Strings.Get("MpLeaveDuringMatchTitle"),
            body,
            Strings.Get("MpLeaveDuringMatchYes"),
            Strings.Get("MpLeaveDuringMatchNo"),
            danger: true);
    }

    /// <summary>
    /// Single rendezvous point for "lobby window dismissed". Runs on
    /// the Closed event for the ✕, Esc, Alt+F4, OS chrome close, AND
    /// our own <see cref="CloseLobbyWindow"/> path. If we still appear
    /// to be in a room (session state hasn't already moved past
    /// InLobby/InGame), trigger the leave-room flow so the server
    /// doesn't keep us as a ghost member.
    /// </summary>
    private void HandleLobbyWindowClosed()
    {
        _lobbyPingTimer?.Stop();
        _lobbyPingTimer = null;

        // The match phase belongs to the room, and the room is gone. Left at Result or
        // AwaitingResult it survives into the NEXT room the player opens, where ApplyMatchPhaseUi
        // reads it and paints a fresh lobby with the result overlay up and the whole left column
        // collapsed — no roster, no Ready, no Start, and nothing on screen explaining why.
        if (_matchPhase is MatchPhase.Result or MatchPhase.AwaitingResult)
            _matchPhase = MatchPhase.Lobby;
        ClearPendingResult();
        SetResultPhase(Services.Multiplayer.RoomMatchState.ResultPhase.None);

        var s = _session;
        if (s == null) return;

        // If we're already past lobby (RoomLeft drove the close) the
        // leave-room call is a no-op / errors; skip.
        if (s.Lobby != MultiplayerSession.LobbyStatus.InLobby
            && s.Lobby != MultiplayerSession.LobbyStatus.InGame)
            return;

        // Fire-and-forget the leave; failures are user-visible via
        // the standard error banner path inside MultiplayerSession.
        _ = s.LeaveCurrentLobbyAsync();
    }

    // ==================================================================
    // Match lifecycle: phases + countdown + in-game overlay
    // ==================================================================

    /// <summary>
    /// Apply visual state for the current <see cref="_matchPhase"/>:
    /// shows / hides the overlays and updates the Cancel/Leave button
    /// caption. Idempotent — safe to call on every state change.
    ///
    /// Pre-Window refactor, this method also locked the popup's
    /// header-drag cursor and hid a custom close-X / resize-thumb
    /// during Starting / InGame. Those concerns are gone because the
    /// OS chrome handles drag/resize natively and the title bar's
    /// close X is independent of the lobby — match-phase locking now
    /// only needs to flip the two overlays and the cancel button.
    /// </summary>
    private void ApplyMatchPhaseUi()
    {
        // No window open → nothing to render. Fires when phase changes
        // arrive from session events after we've already left the room.
        if (_lobbyWindow == null) return;

        var starting = _matchPhase == MatchPhase.Starting;

        // Overlays — Visibility set via the prefixed accessors (the
        // null-forgiving '!' is safe because of the guard above).
        _lobbyWindow!.CountdownOverlay.Visibility = starting
            ? Visibility.Visible : Visibility.Collapsed;
        _lobbyWindow!.InGameOverlay.Visibility = _matchPhase == MatchPhase.InGame
            ? Visibility.Visible : Visibility.Collapsed;
        _lobbyWindow!.MatchResultOverlay.Visibility =
            _matchPhase is MatchPhase.Result or MatchPhase.AwaitingResult
                ? Visibility.Visible : Visibility.Collapsed;

        // And HIDE what those two overlays stand in front of, rather than trusting them to
        // cover it. They are opaque Borders in the same cell as the left column, which is
        // occlusion by z-order — and that only holds while the thing underneath stays
        // inside the cell. It does not: the column is one star row over three Auto rows,
        // so a short window collapses the star to zero and the Auto rows lay themselves
        // out past the bottom of the grid, where nothing is covering them. The reported
        // symptom was the Ready/Leave pair showing in a band under "Abort match".
        // Collapsing the column also stops measuring and rendering a whole panel that
        // nobody can see for the length of a match.
        bool columnCovered =
            _matchPhase is MatchPhase.InGame or MatchPhase.Result or MatchPhase.AwaitingResult;
        _lobbyWindow!.LobbyLeftColumn.Visibility = columnCovered
            ? Visibility.Collapsed : Visibility.Visible;

        // NO glow call here — load-bearing. The countdown is now a live
        // line INSIDE the chat, whose CountdownOverlay Border uses a shared,
        // frozen DynamicResource (MpBlue) BorderBrush and has no Effect.
        // Calling StartCountdownGlow() on it threw InvalidOperationException
        // (a frozen Freezable can't be animated), and because that throw
        // happened RIGHT AFTER the Visibility line above but BEFORE the
        // button-swap below — and before StartCountdown reached
        // UpdateCountdownTick — the symptom was: the bar appeared but froze
        // at the XAML-default number, the Start button never became Cancel,
        // and the "starting in N" chat line never posted. Don't re-add a
        // glow call unless the chat-line Border is given a LOCAL unfrozen
        // SolidColorBrush + a DropShadowEffect first (see CLAUDE.md).

        // The big left-column Start button DOUBLES as the countdown's
        // Cancel. During Starting it turns red, reads "Cancel", and is
        // shown + enabled for EVERYONE (host and joiner) so anyone can
        // abort the launch; StartButton_Click routes to CancelCountdownByUser
        // while in this phase. Outside the countdown, ownership of the
        // button returns to RenderRoomPanel (blue "Start game", host-only)
        // — we mirror that block here so the restore is immediate even
        // before the next room_state refresh lands.
        if (starting)
        {
            _lobbyWindow!.StartButton.Style = (Style)Application.Current.FindResource("MpDangerButton");
            _lobbyWindow!.StartButton.Visibility = Visibility.Visible;
            _lobbyWindow!.StartButton.IsEnabled = true;
            _lobbyWindow!.StartButton.Content = "✕  " + Strings.Get("MpCountdownCancel");
        }
        else
        {
            _lobbyWindow!.StartButton.Style = (Style)Application.Current.FindResource("MpPrimaryButton");
            _lobbyWindow!.StartButton.Visibility = _isHostInCurrentRoom
                ? Visibility.Visible : Visibility.Collapsed;
            _lobbyWindow!.StartButton.IsEnabled = _isHostInCurrentRoom && (_session?.IsInLobby ?? false);
            _lobbyWindow!.StartButton.Content = StartButtonCaption();
        }

        // The guest's counterpart to that Start button: their game closed, the room did not stop.
        RefreshRejoinButton();

        // In-game cancel caption: "Abort match" (for everyone) while the grace
        // window is open, else "Leave" (just you). RefreshInGamePanel re-applies
        // this each tick so it flips when the window elapses mid-match.
        _lobbyWindow!.InGameCancelButton.Content = WithinAbortWindow
            ? Strings.Get("MpInGameAbort")
            : Strings.Get("MpInGameLeave");
    }

    /// <summary>
    /// Begin the local 3-second countdown after receiving
    /// <c>game_countdown</c> from the Worker. <paramref name="startsAtMsUnix"/>
    /// is the server-issued epoch time at which AoE3 should launch;
    /// every client uses the same value so the countdown stays in
    /// sync across peers regardless of WS latency.
    /// </summary>
    private void StartCountdown(int durationMs)
    {
        _matchPhase = MatchPhase.Starting;
        // The one point every member passes through when a match begins — including one whose
        // launch is about to fail, which is precisely who will need the reopen button. Setting it
        // in EnterInGamePhase instead would miss them.
        _roomMatchLive = true;
        _countdownStartedAtTicks = Environment.TickCount64;
        _countdownDurationMs = Math.Max(500, durationMs);   // sanity floor
        DiagnosticLog.Write($"MultiplayerTab.StartCountdown: duration={_countdownDurationMs}ms, phase=Starting");
        ApplyMatchPhaseUi();

        _countdownTickTimer?.Stop();
        _countdownTickTimer = new System.Windows.Threading.DispatcherTimer
        {
            // 100 ms tick keeps the number animation crisp without
            // flickering — UI only repaints when the displayed digit
            // changes (see UpdateCountdownTick).
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _countdownTickTimer.Tick += (_, _) => UpdateCountdownTick();
        _countdownTickTimer.Start();
        UpdateCountdownTick();
    }

    private void UpdateCountdownTick()
    {
        if (_lobbyWindow == null) return;
        // Pure local timer — no server timestamp involved, so clock
        // skew between client and server can't shortcut the wait.
        var elapsedMs = Environment.TickCount64 - _countdownStartedAtTicks;
        var remainingMs = _countdownDurationMs - elapsedMs;
        if (remainingMs <= 0)
        {
            _countdownTickTimer?.Stop();
            _lobbyWindow!.CountdownNumber.Text = Strings.Get("MpCountdownGo");
            DiagnosticLog.Write("MultiplayerTab.UpdateCountdownTick: countdown expired, launching AoE3");
            // This is the *only* path that launches AoE3 in the
            // happy case. If for any reason we're already in InGame
            // (defensive), don't re-launch.
            if (_matchPhase != MatchPhase.InGame)
            {
                var process = LaunchActiveModGame();
                EnterInGamePhase(process);
            }
            return;
        }
        var seconds = Math.Max(1, (int)Math.Ceiling(remainingMs / 1000.0));
        _lobbyWindow!.CountdownNumber.Text = seconds.ToString();
    }

    private void CancelLocalCountdownIfRunning()
    {
        _countdownTickTimer?.Stop();
        _countdownTickTimer = null;
    }

    /// <summary>
    /// User pressed Cancel on the pre-launch countdown overlay. No AoE3
    /// process exists yet during the countdown, so this routes through
    /// <see cref="EndMatchAsync"/> only to stop the local timer + return
    /// to the lobby (via ExitInGamePhase) and — for the host — broadcast
    /// game_cancelled so every peer's countdown stops too.
    /// </summary>
    private async void CancelCountdownByUser()
    {
        if (_matchPhase != MatchPhase.Starting) return;
        // During the countdown ANY member may abort for everyone (we're inside
        // the grace window). The server validates and broadcasts game_cancelled.
        await EndMatchAsync("aborted", sendCancel: true);
    }

    /// <summary>
    /// Enter the InGame phase: lock the popup, start the match
    /// timer + the 1-Hz refresh of the P2P status panel. Caches
    /// the spawned AoE3 process so Cancel can kill it.
    /// </summary>
    /// <param name="resume">
    /// True when the player is REOPENING the game they had closed while the room kept playing
    /// (see <see cref="RejoinGame"/>) — same match, second launch. It must not re-stamp the
    /// match clock, and this is the delicate part of that feature rather than a nicety:
    /// <see cref="WithinAbortWindow"/> is "InGame and less than a minute since
    /// <see cref="_matchTimerStartTicks"/>", so re-stamping would REOPEN the abort window
    /// twenty minutes into a game and the overlay's button would go back to reading "Abort
    /// match" — which cuts the match for everybody — when it should only say "Leave". It would
    /// also reset MATCH TIME to 00:00 and lose the accumulated TRAFFIC, and hand the reporter a
    /// duration measured from the relaunch, short enough to drop a real match on the floor.
    /// </param>
    private void EnterInGamePhase(System.Diagnostics.Process? gameProcess, bool resume = false)
    {
        _matchPhase = MatchPhase.InGame;
        _aoe3Process = gameProcess;

        // A new match supersedes whatever the last one was still owed. Without this the ceiling
        // would be the only thing clearing it, and a quick rematch would carry a stale context
        // into a frame meant for the game just started.
        ClearPendingResult();

        if (!resume)
        {
            _matchTimerStartTicks = Environment.TickCount64;

            // Capture the facts of this match — roster, room, our role, the clock — so the
            // report at the end reads them instead of asking a room that may be gone by then.
            // See Services/Multiplayer/MatchContext.cs; this line is the fix's whole premise.
            // Resolved ONCE, here, and never in the in-game panel's tick: GetInGameName
            // reads the profile XML off disk, and that cell repaints on a timer.
            _canIdentifyPlayerInReplay = true;
            _lastLocalReadFailure = Services.Multiplayer.LocalReadFailure.None;
            _lastLocalReadDetail = null;
            _lastRecordingPath = null;
            _outcomeRebuilder = null;
            try
            {
                var activeProfile = _currentLobbyModId != null
                    ? ModRegistry.Find(_currentLobbyModId) : null;
                if (activeProfile != null && _config != null)
                {
                    _canIdentifyPlayerInReplay = !string.IsNullOrWhiteSpace(
                        UserDataService.GetInGameName(activeProfile, _config));
                    if (!_canIdentifyPlayerInReplay)
                        DiagnosticLog.Write(
                            $"MultiplayerTab: no readable AoE3 profile name for " +
                            $"'{activeProfile.DisplayName}' — this match will not be identifiable " +
                            "in its own recording");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"MultiplayerTab: profile-name probe failed: {ex.Message}");
            }

            _matchContext = Services.Multiplayer.MatchContext.Capture(
                _roomMembers.Keys,
                _session?.CurrentLobbyId,
                _currentLobbyModId,
                _session?.CurrentUser?.Id,
                _isHostInCurrentRoom,
                DateTime.UtcNow,
                _currentLobbyIsCompetitive,
                InGameNamesInRoom(),
                CurrentRoomFormat());

            // Snapshot the Radmin adapter's byte counter so the TRAFFIC stat
            // can show bytes moved during THIS match (delta).
            var baseline = RadminVpnService.GetAdapterBytes();
            _matchBaselineBytes = baseline.HasValue ? baseline.Value.sent + baseline.Value.received : -1;
        }

        // Reset the connection-ping readout for the new game either way — the number describes
        // the process that is starting now, not the one that just died.
        _connectionPingMs = -1;

        // Report our Radmin IP so peers can ping us (the per-player ping column).
        // Done at LAUNCH, not join: at join time the user often isn't on the VPN
        // yet (AdapterIp null). Re-checked each tick in case they connect later.
        _lastReportedRadminIp = null;
        MaybeReportRadminIp();
        _lastReportedInGameName = null;
        MaybeReportInGameName();

        CancelLocalCountdownIfRunning();
        ApplyMatchPhaseUi();

        _inGameTickTimer?.Stop();
        _inGameTickTimer = new System.Windows.Threading.DispatcherTimer
        {
            // 1 s is plenty — RTT / bytes counters drift slowly. The
            // pulsing "live" dot animates via XAML opacity ticks
            // independent of this timer to stay smooth.
            Interval = TimeSpan.FromSeconds(1),
        };
        _inGameTickTimer.Tick += (_, _) => RefreshInGamePanel();
        _inGameTickTimer.Start();
        RefreshInGamePanel();
    }

    /// <summary>
    /// Open Age of Empires III again after it closed while the room kept playing.
    ///
    /// <para>Deliberately the same two calls the countdown makes when it expires, and
    /// <b>nothing else</b> — no frame is sent to the server. The room is already <c>in_game</c>
    /// there, and the launch arguments carry the player's OWN Radmin address, so the game finds
    /// the host's LAN match over the VPN exactly as it did the first time. A player relaunching
    /// is invisible to everyone else, which is the whole point: the alternative they were left
    /// with was leaving the room, and the backend refuses to let them back in.</para>
    ///
    /// <para><c>resume: true</c> keeps the match clock and the captured context — see
    /// <see cref="EnterInGamePhase"/> for why re-stamping them would quietly hand everyone an
    /// "Abort match" button twenty minutes in.</para>
    /// </summary>
    private void RejoinGame()
    {
        // Re-checked rather than trusted: the button is only shown when this holds, but a frame
        // can land between the paint and the click.
        if (!Services.Multiplayer.RoomMatchState.ShouldOfferRejoin(
                _roomMatchLive, _matchPhase == MatchPhase.InGame, _isHostInCurrentRoom))
        {
            RefreshRejoinButton();
            return;
        }

        DiagnosticLog.Write("MultiplayerTab.RejoinGame: reopening AoE3 for a match already in progress");
        AppendChatSystem(Strings.Get("MpChatRejoiningGame"));

        var process = LaunchActiveModGame();
        EnterInGamePhase(process, resume: true);
    }

    /// <summary>
    /// Show or hide "Open the game". Called from the two places that own the lobby's buttons —
    /// <see cref="ApplyMatchPhaseUi"/> for phase changes and <see cref="RenderRoomPanel"/> for
    /// room changes — the same split <c>RefreshReadyButton</c> already lives under.
    /// </summary>
    private void RefreshRejoinButton()
    {
        if (_lobbyWindow == null) return;

        var offer = Services.Multiplayer.RoomMatchState.ShouldOfferRejoin(
            _roomMatchLive, _matchPhase == MatchPhase.InGame, _isHostInCurrentRoom);

        _lobbyWindow!.RejoinGameButton.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
        if (!offer) return;

        _lobbyWindow!.RejoinGameButton.Content = "▶  " + Strings.Get("MpRoomRejoinGame");
        _lobbyWindow!.RejoinGameButton.ToolTip = TooltipHelper.Wrap(Strings.Get("MpRoomRejoinTooltip"));
    }

    /// <summary>
    /// Caption for the host's Start button, from ONE place because two methods write it
    /// (<see cref="ApplyMatchPhaseUi"/> and <see cref="RenderRoomPanel"/>) and they drift
    /// otherwise.
    ///
    /// <para>While the room is still in a match it reads "Reopen the game", because that is
    /// literally what pressing it does: the host is the LAN server, so their way back is to
    /// restart the countdown for everybody. "Start game" there invites the reading that they are
    /// beginning a second, separate match.</para>
    /// </summary>
    private string StartButtonCaption()
        => "▶  " + Strings.Get(_roomMatchLive ? "MpRoomReopenGame" : "MpRoomStart");

    private void ExitInGamePhase()
    {
        _matchPhase = MatchPhase.Lobby;
        _aoe3Process = null;
        _inGameTickTimer?.Stop();
        _inGameTickTimer = null;
        CancelLocalCountdownIfRunning();
        // Allow the auto-start to fire again for the next ready-up (e.g. after a
        // cancelled countdown or a returned-from-match room).
        _autoStartInFlight = false;
        ApplyMatchPhaseUi();
    }

    /// <summary>
    /// The match is over and this room will not host another: show the result.
    ///
    /// <para><b>Why a phase and not just a panel.</b> A reported match closes the room on
    /// the backend, which shuts the socket with 4007 — and nothing in the launcher ever
    /// reacted to that. <see cref="LobbyWebSocket"/> treated it as a dropped connection
    /// and retried forever, backing off to 30 s, so the window survived with a dead chat,
    /// live buttons and a room that no longer existed. That zombie was the de-facto
    /// end-of-match state; this makes it deliberate.</para>
    ///
    /// <para>Three things have to happen together, and each of them is load-bearing:</para>
    /// <list type="number">
    /// <item><b>Clear <c>_roomMatchLive</c>.</b> On the reported path nothing else does —
    /// the <c>game_ended</c> branch that normally clears it is skipped precisely because
    /// the report already closed the room. Left set, <c>WarnOnLeave</c> tells the player
    /// they "will not be able to come back" while they are looking at their own
    /// result.</item>
    /// <item><b>Stop the retries, keep the socket object.</b> Disposing it or nulling
    /// <c>RoomSocket</c> raises the session state change that closes the lobby window —
    /// which is the window the card lives in.</item>
    /// <item><b>Suppress the leave confirmation.</b> The room is gone; there is nothing
    /// left to warn about, and asking would imply there is.</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// The server telling the room how the match ended.
    ///
    /// <para>This is what the GUEST never had. Reporting is host-only, and the room used
    /// to just close underneath everyone else, so the only way to learn whether you had
    /// won was to poll your own match history — three times, over fifteen seconds, hoping
    /// the row had been written. The frame carries every participant's result and rating
    /// change, so the card is complete the moment it arrives, without a single request.</para>
    ///
    /// <para><see cref="ResolveGuestResultAsync"/> stays as the fallback for a backend
    /// that does not send this yet; it only runs when no frame arrived.</para>
    /// </summary>
    private void HandleMatchReported(JsonElement json)
    {
        // Only for a match we were actually in. A spectator who joined the room without
        // playing gets the frame too, and has no business being shown a result card.
        // ResultContext, not _matchContext: on a guest the exit handler has already handed the
        // match over to the pending slot by the time this arrives. Reading the live field alone
        // is what silently dropped this frame — see _pendingResultContext.
        var ctx = ResultContext();
        if (ctx == null)
        {
            // Logged, because a bare return here is indistinguishable from a frame that never
            // arrived, and that is precisely what made the original fault take an hour to find.
            DiagnosticLog.Write(
                "match_reported: no match context, live or pending — ignoring " +
                $"(phase={_matchPhase})");
            return;
        }

        var myId = _session?.CurrentUser?.Id ?? ctx.ReporterUserId ?? "";
        if (string.IsNullOrEmpty(myId))
        {
            DiagnosticLog.Write("match_reported: no user id to match against — ignoring");
            return;
        }

        var report = new ReportMatchResponse
        {
            MatchId = json.TryGetProperty("match_id", out var mid) ? (mid.GetString() ?? "") : "",
            Rated = json.TryGetProperty("rated", out var rd2) && rd2.ValueKind == JsonValueKind.True,
            UnratedReason = json.TryGetProperty("unrated_reason", out var ur)
                            && ur.ValueKind == JsonValueKind.String
                ? ur.GetString() : null,
        };

        double? myResult = null;
        if (json.TryGetProperty("participants", out var parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var pEl in parts.EnumerateArray())
            {
                var uid = pEl.TryGetProperty("user_id", out var u) ? (u.GetString() ?? "") : "";
                if (string.IsNullOrEmpty(uid)) continue;
                var change = new RatingChange
                {
                    UserId = uid,
                    Result = pEl.TryGetProperty("result", out var r)
                             && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : null,
                    RatingBefore = pEl.TryGetProperty("rating_before", out var rb)
                                   && rb.ValueKind == JsonValueKind.Number ? rb.GetDouble() : null,
                    RatingAfter = pEl.TryGetProperty("rating_after", out var ra)
                                  && ra.ValueKind == JsonValueKind.Number ? ra.GetDouble() : null,
                };
                report.RatingChanges.Add(change);
                if (string.Equals(uid, myId, StringComparison.Ordinal)) myResult = change.Result;
            }
        }

        // Not in the list: we were in the room but not in the match. Nothing to show.
        if (myResult == null && !report.RatingChanges.Exists(
                c => string.Equals(c.UserId, myId, StringComparison.Ordinal)))
        {
            DiagnosticLog.Write("match_reported: we are not among the participants — ignoring");
            return;
        }

        var map = json.TryGetProperty("map_name", out var mn) && mn.ValueKind == JsonValueKind.String
            ? mn.GetString() : null;

        DiagnosticLog.Write(
            $"match_reported: match={report.MatchId} rated={report.Rated} " +
            $"reason={report.UnratedReason ?? "-"} myResult=" +
            (myResult.HasValue ? myResult.Value.ToString("0.0") : "none"));

        // Idempotent: the host has already painted its own card from the POST's answer,
        // and it receives this frame too.
        if (_matchPhase == MatchPhase.Result) return;

        // Read BEFORE EnterResultPhase, which sets the phase to Result and drops the process.
        var ourGameStillRunning = _matchPhase == MatchPhase.InGame;

        // Nothing is known about OUR recording yet, because our AoE3 is still open — and the
        // card is about to go up. Letting it fall through to the generic "the match was not
        // recorded" is the exact lie this whole area exists to remove: a player who leaves the
        // game open sees it for as long as it stays open, while their own recording sits on
        // disk naming the winner. It is a wait, not a failure, and it is replaced below.
        if (ourGameStillRunning
            && _lastLocalReadFailure == Services.Multiplayer.LocalReadFailure.None)
            _lastLocalReadFailure = Services.Multiplayer.LocalReadFailure.ReadPending;

        EnterResultPhase(ctx, report, null, myResult, map);

        // And try to end that wait now rather than whenever the player gets round to closing
        // the game. Measured cost of not doing this: nine minutes, in a real match whose
        // result was readable the whole time.
        if (ourGameStillRunning && !report.Rated) _ = TryEarlyReplayReadAsync(ctx);
    }

    /// <summary>
    /// Read our own recording while AoE3 is STILL OPEN, after the match was reported without a
    /// result.
    ///
    /// <para><b>Best-effort in one direction only.</b> Whether AoE3 has finished — or even
    /// started — writing the file at this point is not established, and it may well hold it
    /// open with a lock. So a failure here is discarded in SILENCE and every field it touched
    /// is put back: counting a locked file as "unreadable" would move the card from "waiting"
    /// to a stated cause that is wrong, which is the failure mode this is meant to remove. Only
    /// a real result is kept.</para>
    /// </summary>
    private async Task TryEarlyReplayReadAsync(Services.Multiplayer.MatchContext ctx)
    {
        var profile = string.IsNullOrWhiteSpace(ctx.ModId) ? null : ModRegistry.Find(ctx.ModId!);
        if (profile == null) return;

        if (System.Threading.Interlocked.CompareExchange(ref _replayAnalysisInFlight, 1, 0) != 0)
            return;

        var previousFailure = _lastLocalReadFailure;
        var previousDetail = _lastLocalReadDetail;
        try
        {
            // One pass. If the file is not readable yet, the exit handler's full ladder will
            // get it — there is nothing to gain from waiting here with the game still running.
            var early = await AnalyseMatchReplayAsync(profile, ctx, ctx.StartedAtUtc, firstPassOnly: true);
            if (early.Info?.HostResult == null)
            {
                _lastLocalReadFailure = previousFailure;
                _lastLocalReadDetail = previousDetail;
                return;
            }

            DiagnosticLog.Write(
                $"MultiplayerTab.TryEarlyReplayReadAsync: read '{early.Info.File.Name}' " +
                $"while the game was still open — {early.Info.HostResult:0.0}");

            _lastLocalReadFailure = Services.Multiplayer.LocalReadFailure.None;
            _lastLocalReadDetail = null;
            SetLastRecordingPath(early.Info.File.FullName);
            await TryConfirmMatchAsync(ctx, early.Info, allowHost: true);
            RepaintMatchResult();
        }
        catch (Exception ex)
        {
            _lastLocalReadFailure = previousFailure;
            _lastLocalReadDetail = previousDetail;
            DiagnosticLog.Write($"MultiplayerTab.TryEarlyReplayReadAsync: {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _replayAnalysisInFlight, 0);
        }
    }

    private void EnterResultPhase(
        Services.Multiplayer.MatchContext? ctx = null,
        ReportMatchResponse? report = null,
        MatchReplayInfo? replay = null,
        double? resultOverride = null,
        string? mapOverride = null)
    {
        _matchPhase = MatchPhase.Result;
        _aoe3Process = null;
        _inGameTickTimer?.Stop();
        _inGameTickTimer = null;
        _autoStartInFlight = false;
        _roomMatchLive = false;

        try { _session?.RoomSocket?.StopReconnect(); }
        catch (Exception ex) { DiagnosticLog.Write($"EnterResultPhase: StopReconnect — {ex.Message}"); }

        _lobbyWindow?.SuppressLeaveConfirm();
        ShowResultPending();
        ApplyMatchPhaseUi();

        // The HOST already has everything: the POST answered with every participant's
        // rating change, so their card costs no extra request. Everyone else has to go
        // looking for it — see ResolveGuestResultAsync.
        var context = ctx ?? ResultContext();
        if (!string.IsNullOrEmpty(report?.MatchId)) _lastResultMatchId = report!.MatchId;

        // The wait is over, whichever way it ended.
        ClearPendingResult();

        // The DECIDED cell counts this match too, and the cached tally predates it — so the
        // card would announce a victory beside the record from before it. Both roles pass
        // through here, which the host-only drop inside TryReportMatchAsync does not.
        //
        // DROPPED FIRST ON PURPOSE: if the request fails, the cell shows the em dash — "I do
        // not know" — instead of a plausible wrong number. "0-1 · 0 %" over a Victoria is
        // worse than saying nothing. LoadStandingAsync's in-flight guard covers the double
        // entry (the socket frame and the POST both reach this method).
        _cachedStanding = null;
        _ = LoadStandingAsync();

        // The community strip's own list just gained a match — yours — so its window is dropped
        // for the same reason and at the same point: this is where BOTH roles arrive.
        //
        // It does NOT make the match appear instantly, and the comment says so rather than the
        // code implying otherwise: /stats/community is memoised for 60 s server-side in a single
        // slot shared by every client, so a fetch right now can still answer with the list as it
        // stood before. What this buys is that our window is aligned with the EVENT instead of
        // with whenever the user last looked at the tab, which is what could add a second minute
        // on top. No fetch is kicked from here either — the strip is not on screen at this
        // moment (the lobby window is), and the rooms tick will ask within five seconds of the
        // user getting back to it.
        _activityFetchedUtc = DateTime.MinValue;

        if (report != null && context != null)
        {
            // Captured rather than painted once: the local reading of the recording can land
            // seconds or minutes after this, and the card has to be able to replace what it
            // says. Everything the closure reads that can change — _lastLocalReadFailure and
            // its detail — is read at CALL time, not now.
            _outcomeRebuilder = () => BuildOutcome(context, report, replay, resultOverride, mapOverride);
            ShowMatchResult(_outcomeRebuilder());
        }
        else if (context != null) _ = ResolveGuestResultAsync(context);
    }

    /// <summary>
    /// Turn the report's answer into the card's model. Host-side, so our own participant
    /// row is the one to read.
    /// </summary>
    /// <param name="resultOverride">
    /// Our own score, when it came from the server rather than from a recording. The
    /// GUEST has no recording, so the resolver below has nothing to work with and would
    /// answer "unknown" for a match they had just won — the server's per-participant
    /// result is the only thing that can tell them.
    /// </param>
    /// <param name="mapOverride">Map name from the same frame, for the same reason.</param>
    private MatchOutcomeView BuildOutcome(
        Services.Multiplayer.MatchContext ctx, ReportMatchResponse report, MatchReplayInfo? replay,
        double? resultOverride = null, string? mapOverride = null)
    {
        var myId = ctx.ReporterUserId ?? "";
        RatingChange? mine = null;
        RatingChange? rival = null;
        foreach (var change in report.RatingChanges ?? new List<RatingChange>())
        {
            if (string.Equals(change.UserId, myId, StringComparison.Ordinal)) mine = change;
            else rival ??= change;
        }

        // Our own score, from the same resolver the report itself used — so the card and
        // the row in History can never disagree about who won.
        double myResult;
        if (resultOverride.HasValue)
        {
            myResult = resultOverride.Value;
        }
        else
        {
            var hostResult = replay?.HostResult;
            var decision = Services.Multiplayer.MatchResultResolver.ResolveHostResult(
                hostResult, ctx.Participants, myId);
            myResult = decision.Result ?? Services.Multiplayer.MatchResultResolver.Unknown;
        }

        // Only a 1v1 has "the opponent"; past two players naming one would be a fiction.
        string? rivalLogin = null;
        if (ctx.Participants.Count == 2 && rival != null
            && _roomMembers.TryGetValue(rival.UserId, out var rivalEntry))
            rivalLogin = rivalEntry.Login;

        // The same join and the same resolver the report itself uses, so the card and the row
        // stored on the server can never name different civilizations for one match.
        var civs = ResolveCivNames(
            Services.ModRegistry.Find(ctx.ModId ?? ""),
            Services.Multiplayer.MatchSlotMap.Resolve(replay?.Players, ctx.InGameNames));

        return new MatchOutcomeView(
            MatchOutcomeView.Classify(myResult),
            ctx.ModId,
            mapOverride ?? replay?.MapName,
            ctx.DurationSeconds(DateTime.UtcNow),
            ctx.Participants.Count,
            mine?.RatingBefore,
            mine?.RatingAfter,
            rivalLogin,
            rival?.RatingAfter,
            _cachedStanding?.Wins ?? 0,
            _cachedStanding?.Losses ?? 0,
            _cachedStanding?.Rd,
            // Straight through from the server. The card explains WHY a match did not
            // count, and only the server knows: the launcher used to guess, and guessed
            // wrong for every team game and every unranked mod.
            report.UnratedReason,
            // Subordinate to it, and consulted only when the server's answer was the
            // generic "nobody won" — the one thing the server cannot explain is why our
            // own reading of the recording failed.
            _lastLocalReadFailure,
            _lastLocalReadDetail,
            _lastRecordingPath,
            CivOf(civs, myId),
            // Only a 1v1 has an opponent to attribute a civilization to, which is the same
            // rule rivalLogin above follows.
            ctx.Participants.Count == 2 ? CivOf(civs, rival?.UserId) : null);
    }

    /// <summary>
    /// The civilization on a stored history row — the caller's own, or the other player's.
    ///
    /// <para>"The other player" only means something in a 1v1, which is why the caller gates on
    /// the head count rather than this method guessing from the roster.</para>
    /// </summary>
    private static string? CivFromRow(MatchHistoryRow row, string myId, bool mine)
    {
        var parts = row.Participants;
        if (parts == null) return null;

        foreach (var p in parts)
        {
            var isMe = string.Equals(p.UserId, myId, StringComparison.Ordinal);
            if (isMe != mine) continue;
            return string.IsNullOrWhiteSpace(p.Civ) ? null : p.Civ!.Trim();
        }
        return null;
    }

    /// <summary>One entry of a civ map, or null — including when there is no map at all.</summary>
    private static string? CivOf(IReadOnlyDictionary<string, string>? civs, string? userId)
        => civs != null && !string.IsNullOrEmpty(userId) && civs.TryGetValue(userId!, out var civ)
            ? civ
            : null;

    /// <summary>
    /// Find the match in our own history, for everyone who did not report it.
    ///
    /// <para>A guest gets no frame carrying the result — the room is simply closed — so the
    /// only route is <c>GET /matches/history</c>, and the row may not be written yet when
    /// the socket closes. Three attempts at 0 / 6 / 15 s, then a terminal line pointing at
    /// the History tab. Never a timer beyond that: the endpoint allows 20/min and 500/day
    /// per IP, shared behind NAT or an active Radmin network.</para>
    ///
    /// <para>Skipped outright for a match that was never reportable anyway (solo, or under
    /// three minutes) — there is no row to find, so asking for one three times would spend
    /// somebody else's budget to learn nothing.</para>
    /// </summary>
    private async Task ResolveGuestResultAsync(Services.Multiplayer.MatchContext ctx)
    {
        var api = _session?.Api;
        var myId = _session?.CurrentUser?.Id;
        if (api == null || string.IsNullOrEmpty(myId)) return;
        if (ctx.Participants.Count < 2
            || ctx.DurationSeconds(DateTime.UtcNow) < MinReportableSeconds)
        {
            ShowResultUnavailable();
            return;
        }

        foreach (var delayMs in new[] { 0, 6_000, 15_000 })
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            // The player may have closed the card, or started another match, while we
            // waited — either way this answer is no longer about anything on screen.
            if (_matchPhase != MatchPhase.Result) return;

            try
            {
                var history = await api.GetHistoryAsync(myId);
                var row = Services.Multiplayer.MatchHistoryMatcher.PickForMatch(
                    history?.Matches, ctx.ModId, ctx.StartedAtUtc);
                if (row == null) continue;

                Func<MatchOutcomeView> build = () => new MatchOutcomeView(
                    MatchOutcomeView.Classify(row.Result),
                    row.ModId,
                    row.MapName,
                    row.DurationSeconds,
                    row.PlayerCount,
                    row.RatingBefore,
                    row.RatingAfter,
                    // The opponent's name is local: the roster we played with. Their rating
                    // would be a second request per match, which this budget cannot pay.
                    RivalLoginFrom(ctx, myId),
                    null,
                    _cachedStanding?.Wins ?? 0,
                    _cachedStanding?.Losses ?? 0,
                    _cachedStanding?.Rd,
                    // A history row carries no reason, so a 0.5 here used to fall on the
                    // generic "it was not recorded" — the same wrong message, reached by the
                    // other door. Our own reading knows better and belongs on this card too.
                    null,
                    _lastLocalReadFailure,
                    _lastLocalReadDetail,
                    // The guest reads his own recording too — AnalyseMatchReplayAsync is not
                    // gated on the host — so this card can point at a file just as the host's can.
                    _lastRecordingPath,
                    // Straight off the stored row rather than re-resolved: by this point the
                    // server has the match, and reading it back is what guarantees the card
                    // agrees with the History row the player can scroll to a second later.
                    CivFromRow(row, myId, mine: true),
                    row.PlayerCount == 2 ? CivFromRow(row, myId, mine: false) : null);
                _outcomeRebuilder = build;
                ShowMatchResult(build());
                return;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"ResolveGuestResultAsync: {ex.Message}");
            }
        }

        ShowResultUnavailable();
    }

    /// <summary>The other player's login in a 1v1, or null past two players.</summary>
    private string? RivalLoginFrom(Services.Multiplayer.MatchContext ctx, string myId)
    {
        if (ctx.Participants.Count != 2) return null;
        foreach (var id in ctx.Participants)
        {
            if (string.Equals(id, myId, StringComparison.Ordinal)) continue;
            return _roomMembers.TryGetValue(id, out var entry) ? entry.Login : null;
        }
        return null;
    }

    /// <summary>Paint the finished card into whichever host is available.</summary>
    private void ShowMatchResult(MatchOutcomeView model)
    {
        var host = _lobbyWindow?.MatchResultHost;
        if (host == null)
        {
            // The window is gone — the player closed it, or a teardown took it — and until now
            // the result was computed and then dropped on the floor. Nothing here reopens the
            // window: they closed it on purpose. It just stops being a secret.
            AnnounceResultWithoutAWindow(model);
            return;
        }
        host.Children.Clear();
        host.Children.Add(MatchResultCard.Build(model, new MatchResultCard.Actions(
            // Rematch is not offered yet: it has to leave the closed room BEFORE creating
            // the next one, or it collides with the backend's "one active lobby" guard,
            // and getting that sequence wrong strands the player in neither room.
            OnRematch: null,
            OnDismiss: ExitResultPhase)));
    }

    /// <summary>
    /// The terminal "we could not find it" state. Says where the result WILL appear rather
    /// than leaving the card spinning, because the row does land eventually — the launcher
    /// just cannot keep asking for it.
    /// </summary>
    private void ShowResultUnavailable()
    {
        var host = _lobbyWindow?.MatchResultHost;
        if (host == null) return;

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(new TextBlock
        {
            Text = Strings.Get("MpResultPendingTimeout"),
            Foreground = (Brush)Application.Current.FindResource("MpTextFaint"),
            FontSize = (double)Application.Current.FindResource("FontSizeCaption"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });

        // Without this the panel is a dead end: it covers the left column, and the Leave button
        // it covers is the only other way out of the room.
        var back = new Button
        {
            Content = Strings.Get("MpResultBackToRooms"),
            Style = (Style)Application.Current.FindResource("MpSecondaryButton"),
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        back.Click += (_, _) => ExitResultPhase();
        stack.Children.Add(back);

        host.Children.Clear();
        host.Children.Add(stack);
    }

    /// <summary>
    /// Dismiss the result and go back to the rooms list.
    ///
    /// <para>Closing the window is the whole exit: its <c>Closed</c> handler already
    /// leaves the lobby, which flips the session to Idle and re-renders the browser. The
    /// REST leave will 404 on the closed room, which that path already swallows.</para>
    /// </summary>
    private void ExitResultPhase()
    {
        _matchPhase = MatchPhase.Lobby;
        ClearPendingResult();
        // The card is gone; a late reading must not repaint a window that moved on.
        _outcomeRebuilder = null;
        CloseLobbyWindow();
    }

    /// <summary>
    /// Say how the match ended when there is no room window left to say it in.
    ///
    /// <para>Both surfaces on purpose, and they do different jobs: the toast is seen NOW (and
    /// asks for the desktop, since the launcher may well be minimised by this point), while the
    /// bell is what the player can still find in ten minutes. The window is deliberately not
    /// reopened — they closed it.</para>
    /// </summary>
    private void AnnounceResultWithoutAWindow(MatchOutcomeView model)
    {
        var verdict = model.Verdict switch
        {
            Services.Multiplayer.MatchVerdict.Win => Strings.Get("MpResultWin"),
            Services.Multiplayer.MatchVerdict.Loss => Strings.Get("MpResultLoss"),
            _ => Strings.Get("MpResultPendingTimeout"),
        };
        var delta = model.RatingDelta;
        var body = delta.HasValue
            ? $"{verdict}  ({delta.Value:+#;-#;0} ELO)"
            : verdict;

        try
        {
            _showAppToast?.Invoke(new AppToast.ToastOptions(
                "\U0001F3C1", Strings.Get("MpResultTitle"), body,
                System.Array.Empty<AppToast.ToastAction>(),
                AutoDismissMs: 12000, PreferDesktop: true));
        }
        catch (Exception ex) { DiagnosticLog.Write($"Result toast failed: {ex.Message}"); }

        // Only with an id: the bell dedupes on it, and a blank one would let the same match
        // arrive twice if the server later sends its own match_rated for it.
        if (string.IsNullOrEmpty(_lastResultMatchId)) return;
        try
        {
            _onMatchRated?.Invoke(new Models.Multiplayer.MatchRatedNotice(
                _lastResultMatchId!, model.ModId ?? "", model.MapName,
                model.Verdict switch
                {
                    Services.Multiplayer.MatchVerdict.Win => 1.0,
                    Services.Multiplayer.MatchVerdict.Loss => 0.0,
                    _ => (double?)null,
                },
                model.RatingBefore, model.RatingAfter));
        }
        catch (Exception ex) { DiagnosticLog.Write($"Result bell failed: {ex.Message}"); }
    }

    /// <summary>
    /// Remember the match we are owed a result for, and start the clock that gives up on it.
    /// </summary>
    private void SetPendingResultContext(Services.Multiplayer.MatchContext ctx)
    {
        // Idempotent, and that matters: EnterAwaitingResultPhase sets this on the way through
        // OnGameExitedAsync and the exit handler's own finally sets it again a few seconds later.
        // Restamping there would restart the ceiling from the END of the retry ladder rather than
        // from the moment the game closed — the same reasoning SetResultPhase already encodes.
        if (ReferenceEquals(_pendingResultContext, ctx)) return;
        _pendingResultContext = ctx;
        _pendingResultSinceUtc = DateTime.UtcNow;
    }

    /// <summary>Forget it, and stop the clock. Safe to call when there is nothing pending.</summary>
    private void ClearPendingResult()
    {
        _pendingResultContext = null;
        _resultWaitTimer?.Stop();
        _resultWaitTimer = null;
    }

    /// <summary>
    /// Our game is closed, the match is not settled, and there is nothing left for this launcher
    /// to do but wait for the host.
    ///
    /// <para><b>This is not <see cref="EnterResultPhase"/> with a different label, and the
    /// difference is the reason the phase exists.</b> That method calls <c>StopReconnect</c> —
    /// and the socket it hangs up on is the one the <c>match_reported</c> frame still has to
    /// arrive on. Entering the result phase here would hang up on the answer we are waiting
    /// for.</para>
    /// </summary>
    private void EnterAwaitingResultPhase(Services.Multiplayer.MatchContext ctx)
    {
        SetPendingResultContext(ctx);
        SetResultPhase(Services.Multiplayer.RoomMatchState.ResultPhase.WaitingForHost);
        _matchPhase = MatchPhase.AwaitingResult;
        ApplyMatchPhaseUi();
        ShowResultWaitingForHost();

        DiagnosticLog.Write(
            $"Awaiting the host's result for lobby={ctx.LobbyId} " +
            $"(ceiling {Services.Multiplayer.RoomMatchState.ResultWaitCeilingSeconds:0}s).");

        _resultWaitTimer?.Stop();
        _resultWaitTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _resultWaitTimer.Tick += (_, _) =>
        {
            // The hold on the Leave button releases itself on the much shorter grace; this only
            // decides when to stop SAYING a result is coming.
            var waited = (DateTime.UtcNow - _pendingResultSinceUtc).TotalSeconds;
            if (waited < Services.Multiplayer.RoomMatchState.ResultWaitCeilingSeconds) return;
            DiagnosticLog.Write(
                $"Gave up waiting for the host's result after {waited:0}s.");
            FinishWaitingUnresolved();
        };
        _resultWaitTimer.Start();
    }

    /// <summary>
    /// Stop promising a result that is not coming, without pretending the match did not happen.
    ///
    /// <para>The phase is deliberately left at <c>AwaitingResult</c> so the explanation stays on
    /// screen; the card's own "back to rooms" button is the way out. Dropping straight back to
    /// the lobby would replace the answer with a room the player can no longer start.</para>
    /// </summary>
    private void FinishWaitingUnresolved()
    {
        ClearPendingResult();
        SetResultPhase(Services.Multiplayer.RoomMatchState.ResultPhase.None);
        ShowResultUnavailable();
    }

    /// <summary>
    /// The guest's waiting card: what is being waited for, and — when their game closed while the
    /// room kept playing — the way back into it.
    ///
    /// <para>That second half is not a nicety. The launcher cannot tell "my game closed because
    /// the match ended" from "my game crashed mid-match", so the card offers both readings rather
    /// than guessing one: if the match really did end, the frame replaces this within seconds; if
    /// it did not, the player has the same way back in they would have had anyway.</para>
    /// </summary>
    private void ShowResultWaitingForHost()
    {
        var host = _lobbyWindow?.MatchResultHost;
        if (host == null) return;

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(new TextBlock
        {
            Text = Strings.Get("MpResultWaitingHost"),
            Foreground = (Brush)Application.Current.FindResource("MpTextFaint"),
            FontSize = (double)Application.Current.FindResource("FontSizeCaption"),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });

        if (Services.Multiplayer.RoomMatchState.ShouldOfferRejoin(
                _roomMatchLive, ourGameRunning: false, _isHostInCurrentRoom))
        {
            var rejoin = new Button
            {
                Content = Strings.Get("MpRoomRejoinGame"),
                Style = (Style)Application.Current.FindResource("MpSecondaryButton"),
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            rejoin.Click += (_, _) => RejoinGame();
            stack.Children.Add(rejoin);
        }

        host.Children.Clear();
        host.Children.Add(stack);
    }

    /// <summary>
    /// The card's waiting state. It goes up the instant the game exits, before the result
    /// is known: the numbers do not exist until the report comes back, and the recording
    /// search alone can take the best part of ten seconds. An empty window for ten seconds
    /// reads as a failure; a line that says what is happening does not.
    /// </summary>
    private void ShowResultPending()
    {
        if (_lobbyWindow?.MatchResultHost == null) return;
        _lobbyWindow.MatchResultHost.Children.Clear();
        _lobbyWindow.MatchResultHost.Children.Add(new TextBlock
        {
            Text = Strings.Get("MpResultPending"),
            Foreground = (Brush)Application.Current.FindResource("MpTextFaint"),
            FontSize = (double)Application.Current.FindResource("FontSizeCaption"),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    /// <summary>
    /// Repaint the InGame status overlay from local data: match timer, traffic,
    /// your internet CONNECTION, and a per-peer row list with each peer's derived
    /// <see cref="PeerLinkState"/> (health dot + real ICMP RTT to their Radmin IP,
    /// or "Esperando VPN" / "Sin conexión"). Also posts a chat line on the
    /// Online↔Lost edge. The ICMP "Lost" is INDICATIVE only (Radmin/Windows may
    /// block inbound echo); member_left is the authoritative "left" signal.
    /// </summary>
    private void RefreshInGamePanel()
    {
        // Lobby window closed → nothing to refresh. The 1-s timer that
        // drives this method might still tick once after the window
        // closed (Closed/RoomLeft race); the guard makes that harmless.
        if (_lobbyWindow == null) return;

        // Match timer.
        var elapsedMs = Environment.TickCount64 - _matchTimerStartTicks;
        var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, elapsedMs));
        _lobbyWindow!.InGameMatchTimer.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");

        var bridgeReady = _session?.IsInLobby ?? false;

        // Traffic: delta of the Radmin adapter's byte counters since the
        // match started (the OS counter is cumulative + whole-adapter, so
        // we show the per-match delta). "—" when the adapter wasn't found.
        var bytesNow = RadminVpnService.GetAdapterBytes();
        if (bytesNow.HasValue && _matchBaselineBytes >= 0)
        {
            var moved = Math.Max(0, (bytesNow.Value.sent + bytesNow.Value.received) - _matchBaselineBytes);
            _lobbyWindow!.InGameTrafficText.Text = FormatBytes(moved);
        }
        else
        {
            _lobbyWindow!.InGameTrafficText.Text = "—";
        }

        // Connection latency: show the cached internet RTT (your link
        // quality — NOT a per-rival ping) colour-coded by health, and kick
        // a fresh probe for the next tick.
        _lobbyWindow!.InGameConnectionText.Text = _connectionPingMs >= 0 ? $"{_connectionPingMs} ms" : "…";
        _lobbyWindow!.InGameConnectionText.Foreground = (Brush)Application.Current.FindResource(
            _connectionPingMs < 0 ? "MpTextFaint"
            : _connectionPingMs < 80 ? "MpOk"
            : _connectionPingMs < 200 ? "MpCaution"
            : "MpDestructiveText");
        KickConnectionPing();
        // Keep reporting our Radmin IP (user may have joined the VPN after launch)
        // and refresh the per-peer pings for the rows below.
        MaybeReportRadminIp();
        KickPeerPings();
        // Flip the cancel button from "Abort match" to "Leave" the moment the
        // grace window elapses mid-match (no phase transition fires for that).
        _lobbyWindow!.InGameCancelButton.Content = WithinAbortWindow
            ? Strings.Get("MpInGameAbort")
            : Strings.Get("MpInGameLeave");

        // What the mode badge used to say, moved onto the CONNECTION cell as a tooltip.
        // The reference has no room for a fourth line, but "waiting for the lobby" is a
        // real state and dropping it outright would lose the only hint that the room, not
        // the network, is what is not ready.
        _lobbyWindow!.InGameConnectionText.ToolTip = TooltipHelper.Wrap(Strings.Get(
            bridgeReady ? "MpInGameModeInLobby" : "MpInGameModeWaitingLobby"));

        // RECORDING. Three states, and none of them claims the game IS recording — see
        // RecordingIndicator for why that claim cannot be made from here.
        var recording = Services.Multiplayer.RecordingIndicator.Classify(
            _config?.EnableGameRecording == true,
            _currentLobbyModId != null ? _config?.GetState(_currentLobbyModId).GameRecordingApplied : null,
            _canIdentifyPlayerInReplay);
        var (recKey, recBrush, recTip) = recording switch
        {
            Services.Multiplayer.RecordingState.Requested =>
                ("MpInGameRecordingOn", "MpOk", "MpInGameRecordingTooltip"),
            Services.Multiplayer.RecordingState.Off =>
                ("MpInGameRecordingOff", "MpCaution", "MpInGameRecordingTooltip"),
            // Says the wrong noun on purpose — the cell is labelled RECORDING and this is
            // not about recording — because it answers the question the cell is really
            // for: whether this match is going to count. The tooltip carries the detail
            // and the fix.
            Services.Multiplayer.RecordingState.ProfileUnreadable =>
                ("MpInGameRecordingNoProfile", "MpCaution", "MpInGameRecordingNoProfileTooltip"),
            _ => ("MpInGameRecordingUnknown", "MpCaution", "MpInGameRecordingTooltip"),
        };
        _lobbyWindow!.InGameRecordingText.Text = Strings.Get(recKey);
        _lobbyWindow!.InGameRecordingText.Foreground = (Brush)Application.Current.FindResource(recBrush);
        _lobbyWindow!.InGameRecordingText.ToolTip = TooltipHelper.Wrap(Strings.Get(recTip));

        // Peer list. We just enumerate room members minus ourselves
        // — every member that's in the lobby IS reachable on the
        // virtual LAN as long as their edge is connected.
        _lobbyWindow!.InGamePeersPanel.Children.Clear();
        var me = _session?.CurrentUser;
        if (me != null)
        {
            _lobbyWindow!.InGamePeersPanel.Children.Add(BuildInGamePeerRow(
                login: string.IsNullOrEmpty(me.DiscordUsername) ? me.DisplayName : me.DiscordUsername,
                state: PeerLinkState.Online,   // your own row is always "you" / green
                rttMs: 0,
                isSelf: true));
        }
        int peerCount = 0;
        foreach (var member in _roomMembers.Values)
        {
            if (me != null && string.Equals(member.UserId, me.Id, StringComparison.Ordinal))
                continue;
            peerCount++;
            var state = PeerNetHealth.Classify(
                !string.IsNullOrEmpty(member.RadminIp), member.PingMs, member.ConsecutiveFails);
            // Chat notice only on the Online↔Lost edge (debounced by the fail
            // threshold), never on the transient Unstable/WaitingVpn steps.
            if (state == PeerLinkState.Lost && member.LastLinkState != PeerLinkState.Lost)
                AppendChatSystem(Strings.Format("MpChatPeerLost", member.Login));
            else if (state == PeerLinkState.Online && member.LastLinkState == PeerLinkState.Lost)
                AppendChatSystem(Strings.Format("MpChatPeerReconnected", member.Login));
            member.LastLinkState = state;
            _lobbyWindow!.InGamePeersPanel.Children.Add(BuildInGamePeerRow(
                login: member.Login,
                state: state,
                rttMs: member.PingMs,
                isSelf: false));
        }

        // Alone in the room: an amber box with two things to press, above the peer list.
        // It used to be an italic line INSIDE that list with no actions at all, which
        // states the problem and hands it back to the player.
        _lobbyWindow!.InGameSoloBox.Visibility = peerCount == 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (_lastSoloAnnounceTicks > 0
            && Environment.TickCount64 - _lastSoloAnnounceTicks >= SoloAnnounceCooldownMs
            && !_lobbyWindow!.InGameSoloAnnounceButton.IsEnabled
            && _globalChatSocket != null)
        {
            _lobbyWindow!.InGameSoloAnnounceButton.IsEnabled = true;
            _lobbyWindow!.InGameSoloAnnounceButton.Content = Strings.Get("MpInGameSoloAnnounce");
        }

        // "Pulsing" dot — toggle opacity for a breathing effect.
        _lobbyWindow!.InGameLiveDot.Opacity = _lobbyWindow!.InGameLiveDot.Opacity > 0.6 ? 0.4 : 1.0;
    }

    /// <summary>
    /// Fire-and-forget refresh of <see cref="_connectionPingMs"/> via an
    /// internet ICMP probe (see <see cref="PingInternetRttMsAsync"/>).
    /// Guarded so a fast tick can call it repeatedly without stacking
    /// overlapping pings (each probe can take up to its timeout to fail).
    /// </summary>
    private async void KickConnectionPing()
    {
        if (_connectionPingInFlight) return;
        _connectionPingInFlight = true;
        try
        {
            _connectionPingMs = await PingInternetRttMsAsync();
        }
        finally
        {
            _connectionPingInFlight = false;
        }
    }

    /// <summary>
    /// Ping a reliable public anycast resolver (Cloudflare 1.1.1.1, then
    /// Google 8.8.8.8 as fallback) and return the round-trip time in ms, or
    /// -1 if neither answered. This is the user's general INTERNET latency,
    /// shown everywhere a "ping" appears (in-game overlay, lobby header,
    /// rooms browser). Chosen over a Radmin seed-peer ping because it always
    /// resolves to a number — the seed depended on one peer being online AND
    /// you already being on the VPN, so it usually showed "—".
    /// </summary>
    private static async Task<int> PingInternetRttMsAsync()
    {
        foreach (var host in new[] { "1.1.1.1", "8.8.8.8" })
        {
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync(host, 1000).ConfigureAwait(false);
                if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                    return (int)reply.RoundtripTime;
            }
            catch
            {
                // Try the next host; return -1 if all fail.
            }
        }
        return -1;
    }

    // -------- Per-peer ping (in-game) -------------------------------

    /// <summary>Our last-reported Radmin IP, so we don't re-send an unchanged one.</summary>
    private string? _lastReportedRadminIp;

    /// <summary>Our last-reported AoE3 profile name, so an unchanged one is not re-sent.</summary>
    private string? _lastReportedInGameName;
    private bool _peerPingInFlight;

    /// <summary>
    /// Report our current Radmin VPN IP (26.x) to the room so peers can ping us,
    /// but only when it's known AND changed since last time. Cheap no-op when the
    /// user isn't on Radmin yet (no 26.x adapter) or hasn't changed.
    ///
    /// Reads the IP via <see cref="RadminVpnService.TryGetAdapterIp"/> — the
    /// GATE-FREE enumeration of the 26.x NIC — NOT <c>GetStatus().AdapterIp</c>,
    /// which is null unless the full readiness gate passes (GUI RvRvpnGui.exe
    /// alive + power ≠ Off + adapter Up). This is the SAME IP the game binds
    /// <c>OverrideAddress</c> to at launch, so the roster's health-dot reflects
    /// the address that actually plays: a user whose Radmin GUI is merely closed
    /// (but the background RvControlSvc keeps the 26.x adapter Up) would otherwise
    /// launch bound to the correct NIC yet be reported to everyone as
    /// "Esperando VPN" — a real diagnostic bundle (serviceRunning=False,
    /// adapter=26.58.19.45). Gating this on GetStatus was the exact bug the
    /// OverrideAddress injection already fixed; keep the two paths reading the
    /// same IP. "Esperando VPN" now only means "no 26.x adapter at all".
    /// </summary>
    private void MaybeReportRadminIp()
    {
        var ip = RadminVpnService.TryGetAdapterIp();
        if (string.IsNullOrEmpty(ip) || string.Equals(ip, _lastReportedRadminIp, StringComparison.Ordinal))
            return;
        var sock = _session?.RoomSocket;
        if (sock == null) return;
        _lastReportedRadminIp = ip;
        _ = sock.SendSetRadminIpAsync(ip);
    }

    /// <summary>
    /// Tell the room which AoE3 profile we play under, so the host can work out who was on which
    /// team when the match is reported.
    ///
    /// <para>Reads it per MOD from the room's own mod, not the dashboard's: those differ whenever
    /// somebody hosts a room for a mod other than the one on screen, and the profile name is a
    /// property of the mod's own My Games folder.</para>
    ///
    /// <para>Shaped exactly like <see cref="MaybeReportRadminIp"/> above, INCLUDING the reset of
    /// the dedup guard on room entry — without that, a second room in the same session short-
    /// circuits on the unchanged name, never sends it to the new socket, and every team game
    /// played from that room silently loses its teams. That precise bug already happened once
    /// with the Radmin IP.</para>
    /// </summary>
    private void MaybeReportInGameName()
    {
        if (_config == null) return;
        var profile = _currentLobbyModId != null ? ModRegistry.Find(_currentLobbyModId) : null;
        if (profile == null) return;

        var name = UserDataService.GetInGameName(profile, _config);
        if (string.IsNullOrWhiteSpace(name)
            || string.Equals(name, _lastReportedInGameName, StringComparison.Ordinal))
            return;

        var sock = _session?.RoomSocket;
        if (sock == null) return;
        _lastReportedInGameName = name;
        _ = sock.SendSetInGameNameAsync(name!);
    }

    /// <summary>
    /// Fire-and-forget refresh of every peer's ICMP RTT to their Radmin IP, in
    /// parallel (so N timeouts don't serialise into N seconds). Stores the result
    /// on each <see cref="RoomMemberEntry.PingMs"/>; the next RefreshInGamePanel
    /// paints it. Guarded so a fast tick can't stack overlapping probe rounds.
    /// </summary>
    private async void KickPeerPings()
    {
        if (_peerPingInFlight) return;
        _peerPingInFlight = true;
        try
        {
            var myId = _session?.CurrentUser?.Id;
            var targets = _roomMembers.Values
                .Where(m => !string.IsNullOrEmpty(m.RadminIp)
                            && (myId == null || !string.Equals(m.UserId, myId, StringComparison.Ordinal)))
                .ToList();
            await Task.WhenAll(targets.Select(async m =>
            {
                var rtt = await PingPeerAsync(m.RadminIp!);
                m.PingMs = rtt;
                // Track the short failure/success history the health classifier reads.
                // A single answered probe clears the fail streak (and vice-versa) so
                // "Lost" needs sustained silence, not one dropped packet.
                if (rtt >= 0) { m.ConsecutiveOks++; m.ConsecutiveFails = 0; }
                else { m.ConsecutiveFails++; m.ConsecutiveOks = 0; }
            }));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"MultiplayerTab.KickPeerPings: {ex.Message}");
        }
        finally
        {
            _peerPingInFlight = false;
        }
    }

    /// <summary>ICMP RTT (ms) to a peer's Radmin IP, or -1 on timeout/error.
    /// Same shape as <see cref="PingInternetRttMsAsync"/> but a single host.</summary>
    private static async Task<int> PingPeerAsync(string ip)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(ip, 1000).ConfigureAwait(false);
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                return (int)reply.RoundtripTime;
        }
        catch { /* unreachable / not on VPN yet → -1 */ }
        return -1;
    }

    /// <summary>
    /// Repaint the lobby header's CONNECTION stat from the cached
    /// <see cref="_connectionPingMs"/> (your internet latency, not a
    /// per-rival ping). Same colour thresholds as the in-game CONNECTION
    /// stat. No-op when the lobby window is gone.
    /// </summary>
    private void UpdateLobbyPing()
    {
        if (_lobbyWindow == null) return;
        _lobbyWindow.RoomConnText.Text = _connectionPingMs >= 0 ? $"{_connectionPingMs} ms" : "…";
        _lobbyWindow.RoomConnText.Foreground = (Brush)Application.Current.FindResource(
            _connectionPingMs < 0 ? "MpTextFaint"
            : _connectionPingMs < 80 ? "MpOk"
            : _connectionPingMs < 200 ? "MpCaution"
            : "MpDestructiveText");
    }

    /// <summary>
    /// One peer row for the in-game panel: [health dot] [name — star, ellipsis]
    /// [ping-or-status — right aligned]. Star-sizing the name is load-bearing —
    /// the old fixed 180+110+80 columns overflowed the ~284 px panel and clipped
    /// the ping off-screen (the "no ping shows" bug). Self renders as "you" / "—".
    /// </summary>
    private FrameworkElement BuildInGamePeerRow(
        string login, PeerLinkState state, double rttMs, bool isSelf)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // dot
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // ping/status

        var dotBrush = isSelf
            ? (Brush)Application.Current.FindResource("MpOk")
            : PeerDotBrush(state);
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            Fill = dotBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(dot, 0);
        row.Children.Add(dot);

        var nameTb = new TextBlock
        {
            Text = login,
            Foreground = (Brush)Application.Current.FindResource("MpTextPrimary"),
            FontSize = (double)Application.Current.FindResource("FontSizeBody"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(nameTb, 1);
        row.Children.Add(nameTb);

        // Right cell: your own row shows "you"; a peer shows its real RTT
        // ("NN ms", coloured by health) when Online, else a localized status
        // ("Esperando VPN" / "…" / "Sin conexión").
        string statusText;
        Brush statusBrush;
        if (isSelf)
        {
            statusText = Strings.Get("MpPeerYou");
            statusBrush = (Brush)Application.Current.FindResource("MpOk");
        }
        else if (state == PeerLinkState.Online && rttMs >= 0)
        {
            statusText = $"{(int)rttMs} ms";
            statusBrush = (Brush)Application.Current.FindResource(
                rttMs < 80 ? "MpOk" : rttMs < 200 ? "MpCaution" : "MpDestructiveText");
        }
        else
        {
            statusText = state switch
            {
                PeerLinkState.WaitingVpn => Strings.Get("MpPeerWaitingVpn"),
                PeerLinkState.Lost => Strings.Get("MpPeerLost"),
                _ => "…",
            };
            statusBrush = PeerDotBrush(state);
        }
        var statusTb = new TextBlock
        {
            Text = statusText,
            Foreground = statusBrush,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont"),
            FontSize = (double)Application.Current.FindResource("FontSizeCaption"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(statusTb, 2);
        row.Children.Add(statusTb);

        return row;
    }

    /// <summary>Health-dot / status brush for a derived <see cref="PeerLinkState"/>:
    /// grey (waiting) / green (online) / amber (unstable) / red (lost).</summary>
    private static Brush PeerDotBrush(PeerLinkState state) => (Brush)Application.Current.FindResource(
        state switch
        {
            PeerLinkState.Online => "MpOk",
            PeerLinkState.Unstable => "MpCaution",
            PeerLinkState.Lost => "MpDestructiveText",
            _ => "MpTextMuted",
        });

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    /// <summary>
    /// Cancel / Leave game button click.
    /// Host → asks the Worker to broadcast game_cancelled so every
    /// peer kills its AoE3 and the room returns to "open" status.
    /// Non-host → just kills the local AoE3 process and leaves the
    /// room; the other players keep playing.
    /// </summary>
    private async void InGameCancelButton_Click(object sender, RoutedEventArgs e)
    {
        // Themed in-lobby confirm (replaces the OS MessageBox). Host =
        // "cancel for everyone" (danger, broadcasts game_cancelled); joiner
        // = "leave the game" (only this player drops, room plays on). Needs
        // the lobby window open to host the overlay — it always is here
        // (this button lives in that window), but guard anyway.
        if (_lobbyWindow == null) return;
        // Within the grace window → "abort for everyone" (any member); after it →
        // "leave" (just you). The server is the final authority on the window.
        bool canAbort = WithinAbortWindow;
        bool confirmed = await MpAlertOverlay.ConfirmAsync(
            _lobbyWindow.LobbyRootGrid,
            canAbort ? Strings.Get("MpConfirmAbortTitle") : Strings.Get("MpConfirmLeaveTitle"),
            canAbort ? Strings.Get("MpConfirmAbortBody") : Strings.Get("MpConfirmLeaveBody"),
            canAbort ? Strings.Get("MpConfirmAbortYes") : Strings.Get("MpConfirmLeaveYes"),
            Strings.Get("MpAlertCancel"),
            danger: true);
        if (!confirmed) return;

        await EndMatchAsync(canAbort ? "aborted" : "left", sendCancel: canAbort);
    }

    /// <summary>
    /// Shared kill path: stops the local AoE3 process, exits the
    /// InGame phase locally, and if the user is the host, asks the
    /// Worker to broadcast game_cancelled. Idempotent — calling
    /// twice (e.g. host cancel + window-close confirm) is safe.
    /// </summary>
    private async Task EndMatchAsync(string reason, bool sendCancel)
    {
        try
        {
            var p = _aoe3Process;
            if (p != null)
            {
                // Off the UI thread — the kill confirms with a WaitForExit.
                await Task.Run(() => Services.GameProcessCloser.Stop(p, killEntireTree: true));
            }
        }
        finally
        {
            ExitInGamePhase();
        }

        // sendCancel = "abort for everyone" (within the grace window). The server
        // re-checks the window and broadcasts game_cancelled, or replies
        // grace_window_closed if we raced past it — either way we've already left
        // locally, so a late race just means the others keep playing.
        if (sendCancel && _session?.RoomSocket != null)
        {
            try { await _session.RoomSocket.SendCancelGameAsync(reason); }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"MultiplayerTab.EndMatch: SendCancelGameAsync — {ex.Message}");
            }
        }
        AppendChatSystem(sendCancel
            ? Strings.Get("MpChatYouCancelled")
            : Strings.Get("MpChatYouLeftGame"));
    }

    /// <summary>
    /// True while a game is actively running locally. Used by
    /// MainWindow.OnClosing to confirm with the user before
    /// terminating, since closing the launcher mid-match would
    /// kill AoE3 without giving the host the chance to cancel
    /// cleanly first.
    ///
    /// <para><b>It also covers the seconds AFTER the game closes</b>, which the phase alone does
    /// not: <c>ExitInGamePhase</c> sets the phase back to Lobby the instant the process dies,
    /// while the launcher is still reading the recording and sending the result. Quitting in that
    /// window loses the correction for good — nothing on disk remembers it was owed — so it
    /// deserves the same question. Bounded by the same grace as the room hold, so a stalled read
    /// cannot make the launcher permanently reluctant to close.</para>
    /// </summary>
    public bool IsMatchActive
        => _matchPhase == MatchPhase.InGame
           || _matchPhase == MatchPhase.Starting
           || ResultHoldActive();

    /// <summary>
    /// Called from MainWindow.OnClosing when the user attempts to
    /// close the launcher with an active game. Confirms and (on
    /// yes) cancels cleanly. Returns false if the user said "no"
    /// so the close can be aborted.
    ///
    /// <para><b>Stays a <see cref="MessageBox"/>, unlike every other confirmation in the
    /// multiplayer surface.</b> MainWindow.OnClosing is synchronous and blocks on this task with
    /// a ten-second <c>Wait</c>; an awaited <c>MpAlertOverlay</c> needs the UI thread that the
    /// <c>Wait</c> is holding, so switching it would produce a ten-second freeze followed by a
    /// launcher that will not close. The lobby's own leave confirmation
    /// (<see cref="ConfirmLeaveRoomAsync"/>) is free to use the overlay because nothing is
    /// blocking on it.</para>
    /// </summary>
    public async Task<bool> ConfirmCloseDuringMatchAsync()
    {
        // The game has ALREADY closed and we are only finishing the result. Saying "this closes
        // the game for everyone" here would be plainly false, and there is nothing to end —
        // EndMatchAsync would send a cancel for a match that is over.
        var stillPlaying = _matchPhase == MatchPhase.InGame || _matchPhase == MatchPhase.Starting;
        if (!stillPlaying)
        {
            var quit = MessageBox.Show(
                Strings.Get("MpCloseDuringResultBody"), Strings.Get("MpLeaveDuringMatchTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return quit == MessageBoxResult.Yes;
        }

        // Host or guest is read from the CAPTURED match, not the live flag: if the room has
        // already collapsed, the live one says "not host" and the host would be shown the mild
        // version of what closing is about to do to everybody else.
        var msg = (_matchContext?.IsHost ?? _isHostInCurrentRoom)
            ? Strings.Get("MpLeaveDuringMatchHost")
            : Strings.Get("MpLeaveDuringMatchGuest");
        var r = MessageBox.Show(
            msg, Strings.Get("MpLeaveDuringMatchTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return false;
        // Closing within the grace window aborts for everyone; after it, only we drop.
        await EndMatchAsync("launcher_closed", sendCancel: WithinAbortWindow);
        return true;
    }

}
