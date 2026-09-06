using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WarsOfLibertyLauncher.Controls;
using WarsOfLibertyLauncher.Localization;
using WarsOfLibertyLauncher.Models;
using WarsOfLibertyLauncher.Services;

namespace WarsOfLibertyLauncher;

/// <summary>
/// Per-mod Properties dialog (Steam-style). The single destination
/// for all per-mod actions — the old SETTINGS popup (gear button's
/// flat menu) was folded into this dialog's tabs:
///
///   GENERAL     — read-only metadata + Check for updates
///   LOCAL FILES — install path display + Open/Change paths +
///                 Verify / Repair + View logs + DANGER ZONE
///                 with Uninstall
///   USER DATA   — Open folder / Create backup / Restore backup
///   LANGUAGE    — translation pack picker
///
/// The buttons delegate to the same services MainWindow's existing
/// handlers use — the dialog is pure UI glue with no install /
/// uninstall / backup logic of its own; everything comes in via
/// the callback delegates the constructor receives.
/// </summary>
public partial class ModPropertiesDialog : Window
{
    private readonly ModProfile _profile;
    // Mutable: re-pointed by OnActiveInstallSwitched after the active install
    // COPY changes, so the version-dependent tabs (LANGUAGE) read the new copy.
    private UpdateService _service;
    private readonly LauncherConfig _config;
    // Mutable: the "Buscar nuevas traducciones" button reassigns it after a re-fetch.
    private TranslationIndex? _translationIndex;
    // Re-fetches the translation index from GitHub and returns the fresh one.
    private readonly Func<Task<TranslationIndex?>>? _refreshTranslations;

    // Existing callbacks (4) carried over from the original dialog.
    private readonly Action<TranslationIndexEntry> _applyTranslation;
    private readonly Action _revertToEnglish;
    private readonly Action _openVerify;
    private readonly Action _openRepair;
    private readonly Action? _installAnotherCopy;

    // Multi-install management (the "Manage installs" section on the LOCAL FILES tab).
    private readonly Func<string, System.Threading.Tasks.Task>? _switchInstall;
    private readonly Action<string>? _removeInstall;
    private readonly Func<bool>? _addExistingFolder;
    private readonly Action? _searchInstall;

    // New callbacks (8) folded in from the SETTINGS popup. Each one
    // wraps a RaiseMenuClick on the legacy ActionPanelControl menu
    // item so all the original handlers + dialogs keep owning the
    // actual logic.
    private readonly Func<Task<UpdateService.CheckResult?>> _checkForUpdates;
    private readonly Action _openAoE3Folder;
    private readonly Action _changeModFolder;
    private readonly Action _changeAoE3Folder;
    private readonly Action _openUserDataFolder;
    // Backup/restore return a localized result line (or null when cancelled /
    // nothing happened) so THIS dialog can show inline feedback — the main
    // window's status bar sits behind this non-modal window and its text is
    // invisible while the user is here.
    private readonly Func<string?> _createBackup;
    private readonly Func<string?> _restoreBackup;
    private readonly Action _viewLogs;
    private readonly Action _shareDiagnostics;
    private readonly Action _uninstall;

    /// <summary>Invoked after the user pins/unpins "stay on this version" so the
    /// main window can re-apply its cached check result (refresh PLAY/UPDATE +
    /// status) with no network call. Null = nothing to refresh.</summary>
    private readonly Action? _onUpdatePolicyChanged;

    /// <summary>
    /// The mods this one may import game settings from. Comes in as a callback because the
    /// install check that decides it (<c>MainWindow.IsProfileInstalledLocally</c>) is private to
    /// the main window. Null hides the source picker.
    /// </summary>
    private readonly Func<IReadOnlyList<ModProfile>>? _listSettingsSources;

    /// <summary>
    /// The check result the main window already has cached, WITHOUT forcing a new one.
    /// The panel used to be a decoration that only came alive if you pressed "Check";
    /// opening the dialog now paints the real state from what is already known.
    /// </summary>
    private readonly Func<UpdateService.CheckResult?>? _lastCheckResult;

    // Fase 1 — version picker (GitHubReleases mods only). Null for other
    // mechanisms, which hides the whole "Version" section.
    private readonly Func<Task<IReadOnlyList<GitHubReleaseDownloader.ReleaseInfo>>>? _listVersions;
    private readonly Func<string, Task>? _installVersion;

    /// <summary>True while the mod is installing/updating/repairing — locks the
    /// whole language list (you must not swap data files mid-install). Driven by
    /// MainWindow.SetBusy via <see cref="SetModBusy"/>.</summary>
    private bool _modBusy;

    public ModPropertiesDialog(
        ModProfile profile,
        UpdateService service,
        LauncherConfig config,
        TranslationIndex? translationIndex,
        Action<TranslationIndexEntry> applyTranslation,
        Action revertToEnglish,
        Action openVerify,
        Action openRepair,
        Func<Task<UpdateService.CheckResult?>> checkForUpdates,
        Action openAoE3Folder,
        Action changeModFolder,
        Action changeAoE3Folder,
        Action openUserDataFolder,
        Func<string?> createBackup,
        Func<string?> restoreBackup,
        Action viewLogs,
        Action shareDiagnostics,
        Action uninstall,
        Func<Task<TranslationIndex?>>? refreshTranslations = null,
        Action? onUpdatePolicyChanged = null,
        Func<Task<IReadOnlyList<GitHubReleaseDownloader.ReleaseInfo>>>? listVersions = null,
        Func<string, Task>? installVersion = null,
        Action? installAnotherCopy = null,
        Func<string, Task>? switchInstall = null,
        Action<string>? removeInstall = null,
        Func<bool>? addExistingFolder = null,
        Action? searchInstall = null,
        Func<IReadOnlyList<ModProfile>>? listSettingsSources = null,
        Func<UpdateService.CheckResult?>? lastCheckResult = null)
    {
        _profile = profile;
        _service = service;
        _config = config;
        _translationIndex = translationIndex;
        _applyTranslation = applyTranslation;
        _revertToEnglish = revertToEnglish;
        _openVerify = openVerify;
        _openRepair = openRepair;
        _checkForUpdates = checkForUpdates;
        _openAoE3Folder = openAoE3Folder;
        _changeModFolder = changeModFolder;
        _changeAoE3Folder = changeAoE3Folder;
        _openUserDataFolder = openUserDataFolder;
        _createBackup = createBackup;
        _restoreBackup = restoreBackup;
        _viewLogs = viewLogs;
        _shareDiagnostics = shareDiagnostics;
        _uninstall = uninstall;
        _refreshTranslations = refreshTranslations;
        _onUpdatePolicyChanged = onUpdatePolicyChanged;
        _listVersions = listVersions;
        _installVersion = installVersion;
        _installAnotherCopy = installAnotherCopy;
        _switchInstall = switchInstall;
        _removeInstall = removeInstall;
        _addExistingFolder = addExistingFolder;
        _searchInstall = searchInstall;
        _listSettingsSources = listSettingsSources;
        _lastCheckResult = lastCheckResult;

        InitializeComponent();
        ApplyStrings();
        LoadGeneral();
        LoadLocalFiles();
        LoadUserData();
        // Deliberately NOT inside LoadUserData: RefreshData() calls that, and repopulating the
        // source combo there would wipe a selection the user was in the middle of making — the
        // same reason LoadVersions() is left out of RefreshData().
        LoadGameSettings();
        LoadLanguage();
        LoadVersions();
        LoadAddons();
        SetActiveTab(TabGeneralBtn);
        ApplyConnectivityGate();

        // NO UiScale HERE — see the longer note in LauncherSettingsDialog's constructor,
        // which dropped its own for the same reason. The transform is a zoom, and any scale
        // below 1.0 costs the subtree its ClearType; the width margin here was 66 px (860 x
        // 0.97 = 834 against a MinWidth of 780), so an ordinary resize made the text thin
        // and grey. The content is a capped column in a ScrollViewer and reflows by itself.
    }

    private void ApplyStrings()
    {
        // Localized hover tooltip helper — one line per action button so a
        // newcomer can hover any button and read what it does. Reuses the gear
        // menu's tooltip strings where the action matches (same table).
        static void SetTip(System.Windows.FrameworkElement el, string key) => el.ToolTip = TooltipHelper.Wrap(Strings.Get(key));

        Title = Strings.Format("ModPropTitle", _profile.DisplayName ?? "");
        TitleBarControl.Title = _profile.DisplayName ?? "";
        // (The neutral subtitle that used to sit under HeaderTitleText
        // was removed when the header was compacted to a single row —
        // the sidebar tabs already communicate "this is where you
        // manage settings/files/data/language", so the subtitle was
        // pure vertical filler.)

        TabGeneralLabel.Text = Strings.Get("ModPropTabGeneral");
        TabLocalFilesLabel.Text = Strings.Get("ModPropTabLocalFiles");
        TabUserDataLabel.Text = Strings.Get("ModPropTabUserData");
        TabLanguageLabel.Text = Strings.Get("ModPropTabLanguage");
        TabAddonsLabel.Text = Strings.Get("ModPropTabAddons");
        TabDecksLabel.Text = Strings.Get("ModPropTabDecks");
        TabStatsLabel.Text = Strings.Get("ModPropTabStats");
        ModSearchPlaceholder.Text = Strings.Get("DlgModPropsSearchPlaceholder");
        ModSearchNoResults.Text = Strings.Get("DlgSettingsSearchNoResults");
        SetTip(TabStatsBtn, "TipModPropTabStats");
        SetTip(TabDecksBtn, "TipModPropTabDecks");

        // GENERAL tab
        // The name, the author and the version are shown as themselves now, so the
        // "Name:" / "Author:" / "Website:" field labels are gone; the website moved to the
        // rail footer, where it identifies the mod rather than sitting in a data table.
        LblAboutSection.Text = Strings.Get("ModPropUpdatesSection");
        LblVersion.Text = Strings.Get("ModPropInstalledLabel");
        StayOnVersionTitle.Text = Strings.Get("ModPropStayOnVersionShort");
        StayOnVersionWarning.Text = Strings.Get("ModPropStayOnVersionWarn");
        // NOT set here any more. The title is a VERDICT, and assigning it once
        // unconditionally is what made this panel claim "You're up to date." over a mod
        // with three newer releases published. RefreshUpdateState() owns it.
        CheckUpdatesBtn.Content = Strings.Get("BtnCheck");
        SetTip(CheckUpdatesBtn, "TooltipMenuCheckForUpdates");
        StayOnVersionHint.Text = Strings.Get("ModPropStayOnVersionHint");
        // The switch carries the sentence now, so the section title and the switch label
        // are not two names for the same thing.
        LblGameSettingsTitle.Text = Strings.Get("ModPropSettingsShare");
        ImportSettingsBtn.Content = Strings.Get("ModPropSettingsImportBtn");
        SyncSettingsHint.Text = Strings.Get("ModPropSettingsShareHint");
        VersionSectionLabel.Text = Strings.Get("ModPropVersionSection");
        VersionSectionHint.Text = Strings.Get("ModPropVersionHint");
        InstallVersionBtn.Content = Strings.Get("ModPropVersionInstallBtn");
        SetTip(InstallVersionBtn, "TipMpInstallVersion");

        // LOCAL FILES tab
        LblInstallPath.Text = Strings.Get("ModPropModFolderTitle");
        LblAoe3PathTitle.Text = Strings.Get("ModPropAoe3FolderTitle");
        LblFindInstallTitle.Text = Strings.Get("ModPropFindInstallTitle");
        LblFindInstallDesc.Text = Strings.Get("ModPropFindInstallDesc");
        LblVerifyTitle.Text = Strings.Get("ModPropVerifyTitle");
        LblVerifyDesc.Text = Strings.Get("ModPropVerifyDesc");
        LblRepairTitle.Text = Strings.Get("ModPropRepairTitle");
        LblRepairDesc.Text = Strings.Get("ModPropRepairDesc");
        LblUninstallTitle.Text = Strings.Get("ModPropUninstallTitle");
        LblTempSection.Text = Strings.Get("ModPropTempTitle");
        LblTempDesc.Text = Strings.Get("ModPropTempShortDesc");
        ClearTempBtn.Content = Strings.Get("BtnFreeSpace");
        SetTip(ClearTempBtn, "DlgLauncherSettingsClearTempTip");
        LblPathsSection.Text = Strings.Get("ModPropPathsSection");
        OpenFolderBtn.Content = Strings.Get("BtnOpen");
        SetTip(OpenFolderBtn, "TipMpOpenFolder");
        OpenAoE3FolderBtn.Content = Strings.Get("BtnOpen");
        SetTip(OpenAoE3FolderBtn, "TooltipMenuOpenAoE3Folder");
        ChangeModFolderBtn.Content = Strings.Get("BtnChange");
        SetTip(ChangeModFolderBtn, "TipMpChangeModFolder");
        ChangeAoE3FolderBtn.Content = Strings.Get("BtnChange");
        SetTip(ChangeAoE3FolderBtn, "TooltipMenuSelectAoE3Folder");
        SearchInstallBtn.Content = Strings.Get("SearchInstallButton");
        SetTip(SearchInstallBtn, "TipSearchInstall");
        // The broad "find my install" search is meaningless for the stock game
        // (the launcher never installs it) — hide it there.
        SearchInstallBtn.Visibility = _profile.IsStockGame
            ? Visibility.Collapsed : Visibility.Visible;
        // The long "what a registered copy is" paragraph became a group label and a
        // count — the card underneath already shows what a copy looks like.
        LblManageInstalls.Text = Strings.Get("ModPropInstallsSection");
        AddExistingFolderBtn.Content = Strings.Get("AddExistingFolder");
        SetTip(AddExistingFolderBtn, "TipMpAddExistingFolder");
        InstallNewCopyBtn.Content = Strings.Get("MenuInstallAnotherCopy");
        SetTip(InstallNewCopyBtn, "TooltipMenuInstallAnotherCopy");
        LblMaintenanceSection.Text = Strings.Get("ModPropMaintenanceSection");
        VerifyBtn.Content = Strings.Get("BtnVerify");
        SetTip(VerifyBtn, "TooltipMenuVerifyFiles");
        RepairBtn.Content = Strings.Get("BtnRepair");
        SetTip(RepairBtn, "TooltipMenuRepairInstall");
        LblDiagnosticsSection.Text = Strings.Get("ModPropDiagnostics");
        ViewLogsBtn.Content = Strings.Get("ModPropViewLogs");
        SetTip(ViewLogsBtn, "TooltipMenuViewLogs");
        ShareDiagnosticsBtn.Content = Strings.Get("ModPropShareDiagnostics");
        // Sized to ITS ROW, not to the app scale the pill defaults to. It stands beside two
        // SetDescSize buttons here, so at the pill's own FontSizeBody it read a size and a
        // half heavier than them — and that surplus width is also what pushed the Spanish
        // caption off the edge of the card. The other three hosts sit alone on their line in
        // a dialog on the app scale and keep the default — true since the Radmin assistant's
        // pill moved out of its footer, where it had been quietly failing the same way.
        SupportLinkHost.Content = Controls.SupportLink.Build(
            (double)FindResource("SetDescSize"));
        SetTip(ShareDiagnosticsBtn, "TipMpShareDiagnostics");
        LblDangerZone.Text = Strings.Get("ModPropDangerZone");
        LblDangerZoneDesc.Text = Strings.Get("ModPropDangerZoneDesc");
        UninstallBtn.Content = Strings.Get("BtnUninstallHere");
        SetTip(UninstallBtn, "TooltipMenuUninstall");

        // USER DATA tab — action-card layout: each card has a long
        // descriptive title + short description, and a SHORT button
        // label ("Open" / "Backup" / "Restore") because the long
        // text already tells the user what the action does.
        LblUserDataLocation.Text = Strings.Get("ModPropUserDataLocation");
        // The path IS the location row now, so its title and description are gone; the
        // backups are a real list, so "Restore" moved onto each of them.
        OpenUserDataFolderBtn.Content = Strings.Get("BtnOpen");
        LblBackupsSection.Text = Strings.Get("ModPropBackupsSection");
        CreateBackupBtn.Content = Strings.Get("ModPropCreateBackup");
        LblBackupsNote.Text = Strings.Get("ModPropBackupsNote");
        LblGameSettingsSection.Text = Strings.Get("ModPropGameSettingsSection");
        LblImportTitle.Text = Strings.Get("ModPropImportTitle");
        // Default; LoadUserData() replaces it with the real list or "none yet".
        LblRestoreBackupDesc.Text = Strings.Get("ModPropRestoreNone");

        // LANGUAGE tab
        LblHumanGamesTitle.Text = Strings.Get("ModPropHumanGamesTitle");
        LblHumanGamesHint.Text = Strings.Get("ModPropHumanGamesHint");
        LblStatsSectionTitle.Text = Strings.Get("ModPropStatsTitle");
        LblStatsSectionHint.Text = Strings.Get("ModPropStatsHint");
        LblDecksSectionHint.Text = Strings.Get("ModPropDecksHint");
        LblAddonsSectionHint.Text = Strings.Get("AddonsSectionHint");
        ImportAddonBtn.Content = Strings.Get("AddonImportButton");
        LblAddonsGroupCatalog.Text = Strings.Get("AddonsGroupCatalog");
        LblAddonsGroupCatalogHint.Text = Strings.Get("AddonsGroupCatalogHint");
        LblAddonsGroupImported.Text = Strings.Get("AddonsGroupImported");
        LblAddonsGroupImportedHint.Text = Strings.Get("AddonsGroupImportedHint");
        AddonsFooterNote.Text = Strings.Get("AddonsFooterNote");
        LblLanguageDesc.Text = Strings.Get("ModPropLanguageDesc");
        RefreshTranslationsBtn.Content = Strings.Get("DlgLangRefreshButton");
        LblLanguageCurrent.Text = Strings.Get("ModPropLanguageCurrent");
        LanguageBusyHintText.Text = Strings.Get("LanguageBusyHint");
        LanguageEmptyHint.Text = Strings.Get("ModPropNoTranslations");

        // The header close ✕ is now the shared controls:TitleBar's own
        // button (localized tooltip handled inside TitleBar).
    }

    private void LoadGeneral()
    {
        // Header mod/game icon (cached catalog icon.png or built-in packed
        // icon). Collapsed when the mod ships no icon — the title alone reads
        // fine then.
        // The shared title bar shows the icon (collapses it when null).
        TitleBarControl.TitleIcon = LoadIconBrush(_profile)?.ImageSource;
        // The same icon the title bar shows, at the size the identity header wants.
        var identityBrush = LoadIconBrush(_profile);
        if (identityBrush != null) ModIdentityIcon.Background = identityBrush;

        ValName.Text = _profile.DisplayName ?? "";
        ValAuthor.Text = string.IsNullOrWhiteSpace(_profile.Author) ? "—" : _profile.Author;
        var ver = _service.CurrentVersion?.Ver;
        bool hasVersion = !string.IsNullOrWhiteSpace(ver);
        // A valid install whose version we couldn't identify (stale/unreachable
        // UpdateInfo) is NOT "(not installed)" — the mod is on disk. Distinguish
        // "installed, version unknown" from genuinely-not-installed by the resolved
        // install path, so this never contradicts the dashboard's Play.
        bool hasInstall = !string.IsNullOrWhiteSpace(_service.InstallPath);
        // The stock game is detect-only (Manual): the launcher never tracks its version
        // by design, so a detected base game shows a reassuring "ready to play" instead of
        // the alarming "version not verified" (which reads as a transient failure).
        ValVersion.Text = hasVersion ? ver!
            : _profile.IsStockGame && hasInstall ? Strings.Get("ModPropStockVersion")
            : hasInstall ? Strings.Get("ModPropVersionUnknown")
            : Strings.Get("ModPropNotInstalled");
        RailAuthorText.Text = string.IsNullOrWhiteSpace(_profile.Author) ? "—" : _profile.Author;
        RailSiteText.Text = string.IsNullOrWhiteSpace(_profile.OfficialWebsite) ? "" : _profile.OfficialWebsite;
        ValAuthor.Text = string.IsNullOrWhiteSpace(_profile.Author)
            ? _profile.OfficialWebsite ?? ""
            : string.IsNullOrWhiteSpace(_profile.OfficialWebsite)
                ? _profile.Author
                : _profile.Author + "  ·  " + _profile.OfficialWebsite;

        // The launcher doesn't manage the base game's updates (detect-only) — hide the
        // "Check for updates" action + its result line for the stock game, mirroring how
        // Verify/Repair/Uninstall are hidden for it.
        if (_profile.IsStockGame)
        {
            CheckUpdatesBtn.Visibility = Visibility.Collapsed;
            CheckUpdatesResult.Visibility = Visibility.Collapsed;
        }

        // Mirror the version into the header's pill badge so the
        // user sees "v1.2.0c2" at the top regardless of which tab
        // they're on. When the mod isn't installed the badge
        // collapses so the header stays clean instead of showing
        // an empty pill.
        if (hasVersion)
        {
            HeaderVersionText.Text = "v" + ver;
            HeaderVersionBadge.Visibility = Visibility.Visible;
        }
        else
        {
            HeaderVersionText.Text = string.Empty;
            HeaderVersionBadge.Visibility = Visibility.Collapsed;
        }

        // Stay-on-version pin (Fase 0): only meaningful once we know the installed
        // version. Checked only when the pin matches the version we actually have.
        if (hasVersion)
        {
            var pinned = _config.GetState(_profile.Id).PinnedVersion;
            StayOnVersionCheck.Content = Strings.Format("ModPropStayOnVersion", ver);
            StayOnVersionCheck.IsChecked =
                !string.IsNullOrEmpty(pinned)
                && string.Equals(pinned, ver, StringComparison.OrdinalIgnoreCase);
            StayOnVersionCheck.Visibility = Visibility.Visible;
            StayOnVersionHint.Visibility = Visibility.Visible;
        }
        else
        {
            StayOnVersionCheck.Visibility = Visibility.Collapsed;
            StayOnVersionHint.Visibility = Visibility.Collapsed;
        }

        // The update-state panel is a VERDICT and is painted from what the main window
        // already knows, with no network call. Doing this on open is the point: the panel
        // used to come alive only if you pressed "Check", so the dialog opened claiming a
        // state it had never looked at.
        //
        // The stock game is detect-only - the launcher never tracks its version by design -
        // so the whole card goes rather than a card with nothing to say.
        if (_profile.IsStockGame)
        {
            UpdateStateCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateStateCard.Visibility = Visibility.Visible;
            RefreshUpdateState();
        }
    }

    /// <summary>
    /// Pin / unpin "stay on this version". Checking it records the installed
    /// version in <see cref="ModState.PinnedVersion"/> (pausing update prompts for
    /// this mod); unchecking clears it (resume updates). Nothing is ever
    /// auto-updated — this only controls whether the prompt is shown. The main
    /// window is refreshed via the callback so PLAY/UPDATE updates instantly.
    /// </summary>
    private void StayOnVersionCheck_Click(object sender, RoutedEventArgs e)
    {
        var state = _config.GetState(_profile.Id);
        var ver = _service.CurrentVersion?.Ver;
        state.PinnedVersion =
            (StayOnVersionCheck.IsChecked == true && !string.IsNullOrWhiteSpace(ver))
                ? ver!
                : "";
        _config.Save();
        _onUpdatePolicyChanged?.Invoke();
        // The pin decides between "update available" and "updates paused", so the panel is
        // now stale. Repaint from the cached result - still no network call.
        RefreshUpdateState();
    }

    /// <summary>
    /// Loads the profile's icon as an ImageBrush — cached catalog icon → live
    /// remote URL → built-in packed icon, via
    /// <see cref="ModProfile.ResolveIconSource"/>. Returns null when nothing
    /// resolves, so the caller hides the header icon host. A remote icon
    /// downloads async and can't be frozen mid-flight (unconditional Freeze
    /// throws); unfrozen it repaints itself when the download completes.
    /// </summary>
    private static System.Windows.Media.ImageBrush? LoadIconBrush(ModProfile profile)
    {
        string? uri = profile.ResolveIconSource();
        if (string.IsNullOrEmpty(uri)) return null;
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new System.Uri(uri, System.UriKind.Absolute);
            bmp.EndInit();
            if (bmp.CanFreeze) bmp.Freeze();
            var br = new System.Windows.Media.ImageBrush(bmp)
            {
                Stretch = System.Windows.Media.Stretch.UniformToFill,
            };
            if (br.CanFreeze) br.Freeze();
            return br;
        }
        catch
        {
            return null;
        }
    }

    private void LoadLocalFiles()
    {
        var path = _service.InstallPath;
        bool installed = !string.IsNullOrEmpty(path) && Directory.Exists(path);
        ValInstallPath.Text = installed ? path : Strings.Get("ModPropNotInstalled");

        // The AoE3 folder, resolved the CONFIG-AWARE way — the bare detector misses a
        // non-standard install even after the user pointed the picker straight at it.
        var aoe3 = Services.GameLauncher.FindAoe3InstallRoot(_config);
        ValAoe3Path.Text = string.IsNullOrWhiteSpace(aoe3)
            ? Strings.Get("ModPropNotInstalled")
            : aoe3;

        // Per-button enablement: paths-related buttons need an
        // install on disk; maintenance buttons need the mod
        // installed; Open AoE3 + Change AoE3 are gated by AoE3
        // detection which we delegate to the legacy menu items
        // (their IsEnabled reflects the current detection state).
        OpenFolderBtn.IsEnabled = installed;
        OpenAoE3FolderBtn.IsEnabled = true;   // Always tries; warns if not found.
        ChangeModFolderBtn.IsEnabled = true;
        ChangeAoE3FolderBtn.IsEnabled = true;
        VerifyBtn.IsEnabled = installed;
        RepairBtn.IsEnabled = installed;
        AddExistingFolderBtn.IsEnabled = true; // adopting an existing folder is always allowed
        InstallNewCopyBtn.IsEnabled = installed;
        ViewLogsBtn.IsEnabled = true;          // Logs are always available.
        ShareDiagnosticsBtn.IsEnabled = true;  // Bundle is always available.
        UninstallBtn.IsEnabled = installed;

        // Stock Age of Empires III is detect-only: the launcher never
        // installed it, so there's no payload to verify/repair, and the
        // "install path" IS the user's real AoE3 folder — uninstalling it
        // (a blanket recursive delete) would wipe their base game. Hide the
        // Maintenance and Danger Zone sections outright for it.
        if (_profile.IsStockGame)
        {
            LblMaintenanceSection.Visibility = Visibility.Collapsed;
            VerifyBtn.Visibility = Visibility.Collapsed;
            RepairBtn.Visibility = Visibility.Collapsed;
            VerifyBtn.IsEnabled = false;
            RepairBtn.IsEnabled = false;

            // The detect-only stock game never has copies to manage.
            LblManageInstalls.Visibility = Visibility.Collapsed;
            LblManageInstallsDesc.Visibility = Visibility.Collapsed;
            ManageInstallsHost.Visibility = Visibility.Collapsed;
            ManageInstallsButtons.Visibility = Visibility.Collapsed;
            ManageInstallsDivider.Visibility = Visibility.Collapsed;

            LblDangerZone.Visibility = Visibility.Collapsed;
            LblDangerZoneDesc.Visibility = Visibility.Collapsed;
            UninstallBtn.Visibility = Visibility.Collapsed;
            UninstallBtn.IsEnabled = false;
        }

        LoadManageInstalls();
    }

    /// <summary>
    /// Build the "Manage installs" list: one card per registered install (the active one
    /// first, then each inactive copy) with an editable name, its path + version, and
    /// Active/Switch/Remove actions. Reuses <see cref="PathDisplay"/> for unique labels and
    /// compact paths. Skipped for the stock game (no copies).
    /// </summary>
    private void LoadManageInstalls()
    {
        ManageInstallsHost.Children.Clear();
        if (_profile.IsStockGame) return;

        var st = _config.GetState(_profile.Id);
        // Name = the real FOLDER name (ignore any stored custom Label — renaming was removed
        // because a label that doesn't match the folder is misleading).
        var rows = new List<(string Id, string Label, string Path, string Version, bool IsActive)>
        {
            (st.ActiveInstallId, DeriveLeaf(st.InstallPath), st.InstallPath, st.LastKnownVersion, true),
        };
        foreach (var o in st.OtherInstalls)
            rows.Add((o.Id, DeriveLeaf(o.InstallPath), o.InstallPath, o.LastKnownVersion, false));

        // STABLE ORDER by install folder (not active-first): switching the active copy
        // must NOT reorder the list — otherwise the chosen card jumps to the top, which
        // reads as abrupt/ambiguous. With a fixed order only the gold highlight moves to
        // the clicked card in place (animated below). Ordinal so it's deterministic.
        rows.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        var uniqueLabels = PathDisplay.DisambiguateLabels(
            rows.Select(r => (r.Label, r.Path)).ToList());

        for (int i = 0; i < rows.Count; i++)
            ManageInstallsHost.Children.Add(BuildInstallCard(
                rows[i].Id, uniqueLabels[i], rows[i].Label, rows[i].Path, rows[i].Version, rows[i].IsActive));

        LblManageInstallsDesc.Text = rows.Count == 1
            ? Strings.Get("ModPropInstallsCountOne")
            : Strings.Format("ModPropInstallsCountMany", rows.Count);

        // Consume the "just switched" marker so a plain RefreshData does not re-animate.
        _recentlyActivatedInstallId = null;
    }

    /// <summary>
    /// Id of the copy the user just made active via "Switch", so the rebuilt list can
    /// play a one-shot gold-tint pulse on that card (the highlight "moves" to it in
    /// place instead of the card jumping to the top). Set before the switch await,
    /// consumed + cleared by <see cref="LoadManageInstalls"/>.
    /// </summary>
    private string? _recentlyActivatedInstallId;

    private static string DeriveLeaf(string? path)
    {
        var leaf = System.IO.Path.GetFileName((path ?? "").TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(leaf) ? (path ?? "") : leaf;
    }

    /// <summary>Reads a resource brush's <see cref="Color"/>, falling back if it's
    /// missing or not a solid colour (so the switch pulse can never throw).</summary>
    private Color ResourceColor(string key, Color fallback)
    {
        try { return TryFindResource(key) is SolidColorBrush b ? b.Color : fallback; }
        catch { return fallback; }
    }

    private Border BuildInstallCard(
        string id, string uniqueLabel, string rawLabel, string path, string version, bool isActive)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("MpAppBg"),
            BorderBrush = (Brush)FindResource(isActive ? "MpAction" : "MpRimSoft"),
            BorderThickness = new Thickness(isActive ? 2 : 1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
        };

        // "The gold highlight moved here": if this is the card the user just switched to,
        // start its background at the gold tint and fade it to the normal panel colour on
        // load, so the eye follows the change (the list order is fixed, so nothing jumps).
        if (isActive && id == _recentlyActivatedInstallId)
        {
            var goldColor = ResourceColor("MpActionSoftBg", Color.FromRgb(0x1D, 0x28, 0x40));
            var baseColor = ResourceColor("MpAppBg", Color.FromRgb(0x0F, 0x1C, 0x2E));
            var pulse = new SolidColorBrush(goldColor);
            card.Background = pulse;
            card.Loaded += (_, _) =>
            {
                var anim = new System.Windows.Media.Animation.ColorAnimation
                {
                    To = baseColor,
                    Duration = new Duration(TimeSpan.FromMilliseconds(450)),
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                    },
                };
                pulse.BeginAnimation(SolidColorBrush.ColorProperty, anim);
            };
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();

        // Read-only name = the copy's real FOLDER name (renaming was removed — a label that
        // doesn't match the folder is misleading). Disambiguated (#N / parent) for uniqueness.
        var nameText = new TextBlock
        {
            Text = uniqueLabel,
            FontSize = (double)FindResource("FontSizeBodyStrong"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource(isActive ? "MpActionText" : "MpTextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        left.Children.Add(nameText);

        var meta = new TextBlock
        {
            Text = string.IsNullOrEmpty(version)
                ? PathDisplay.CompactPathMiddle(path, 60)
                : $"{PathDisplay.CompactPathMiddle(path, 60)}   ·   {version}",
            FontSize = (double)FindResource("FontSizeCaption"),
            Foreground = (Brush)FindResource("OnSecondaryContainer"),
            Opacity = 0.85,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        left.Children.Add(meta);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // Actions column: Active badge (active) or Switch button (inactive) + Remove (inactive).
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        if (isActive)
        {
            actions.Children.Add(new Border
            {
                Background = (Brush)FindResource("MpRowHighlight"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = Strings.Get("ActiveInstallBadge"),
                    FontSize = (double)FindResource("FontSizeCaption"),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("MpAction"),
                },
            });
        }
        else
        {
            var switchBtn = new Button
            {
                Style = (Style)FindResource("PropertyActionButton"),
                Content = Strings.Get("SwitchToInstall"),
                MinWidth = 90,
                Margin = new Thickness(0),
            };
            switchBtn.Click += async (_, _) =>
            {
                // Mark this copy so the rebuilt list pulses its (now active) card in place
                // instead of the card silently jumping to the top.
                _recentlyActivatedInstallId = id;
                if (_switchInstall != null) await _switchInstall(id);
            };
            actions.Children.Add(switchBtn);

            var removeBtn = new Button
            {
                Style = (Style)FindResource("PropertyActionButton"),
                Content = Strings.Get("RemoveInstallBtn"),   // ✕
                MinWidth = 80,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = Strings.Get("RemoveInstallCopy"),
            };
            removeBtn.Click += (_, _) => _removeInstall?.Invoke(id);
            actions.Children.Add(removeBtn);
        }
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    private void LoadUserData()
    {
        // Buttons enabled only when the mod is installed (no install
        // path → nothing to back up). The underlying handlers will
        // also surface their own "nothing to back up" message if
        // the call goes through for some edge case.
        var installed = !string.IsNullOrEmpty(_service.InstallPath)
                        && Directory.Exists(_service.InstallPath);
        OpenUserDataFolderBtn.IsEnabled = installed;
        CreateBackupBtn.IsEnabled = installed;

        var folderName = Services.UserDataService.ResolveFolderName(_profile, _config);

        // Resolved data path, visible — with OneDrive Known Folder Move the
        // real Documents can be "...\OneDrive\Dokumente\...", and seeing the
        // exact folder here is what makes the backup behaviour explainable
        // (the "backup went to a totally different path" report).
        var folder = UserDataService.GetUserDataFolder(folderName);
        UserDataPathText.Text = folder ?? "—";

        var alternate = string.IsNullOrEmpty(folderName)
            ? null
            : UserDataService.GetAlternateDataFolderWithFiles(folderName);
        UserDataDivergesText.Text = alternate != null
            ? Strings.Format("ModPropUserDataPathDiverges", alternate)
            : "";
        UserDataDivergesText.Visibility = alternate != null
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Second, unrelated reason the folder above may not be the one the user expects: the
        // launcher is running as another Windows account, so this is THAT account's folder.
        // Read live rather than cached — this tab is rebuilt on every RefreshData, and the check
        // is two cheap reads.
        var account = Services.RunningAccount.Current();
        var signedIn = account.Mismatch
            ? Services.RunningAccount.SignedInDataFolder(account.SessionUser, folderName)
            : null;

        UserDataOtherAccountText.Text = account.Mismatch
            ? Strings.Format("ModPropUserDataOtherAccount", account.ProcessUser, account.SessionUser)
            : "";
        UserDataOtherAccountText.Visibility = account.Mismatch ? Visibility.Visible : Visibility.Collapsed;

        // The other account's folder is resolved exactly or not at all, so the warning above can
        // legitimately stand without a path underneath it.
        UserDataOtherAccountPath.Text = signedIn ?? "";
        UserDataOtherAccountPath.Visibility = signedIn != null ? Visibility.Visible : Visibility.Collapsed;

        // Restore row: show how many backups exist and when the latest was
        // made; with none, disable the button and say so up front instead of
        // surprising the user with a "no backups" message box on click.
        var backups = string.IsNullOrEmpty(folderName)
            ? new List<UserDataService.BackupInfo>()
            : UserDataService.ListBackups(folderName);
        RenderBackupList(backups, installed);
    }
    /// <summary>
    /// One row per backup: name, date and size, with Restore on each.
    ///
    /// <para>The old single row said "N backups · latest: date" and put one Restore button
    /// beside it, which could only ever restore the newest. A list is the handoff's second
    /// sanctioned behaviour change, and it costs nothing: ListBackups has always returned
    /// the date, the byte count and the file counts, newest first.</para>
    /// </summary>
    private void RenderBackupList(List<UserDataService.BackupInfo> backups, bool installed)
    {
        BackupsList.Children.Clear();
        BackupsEmptyRow.Visibility = backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (backups.Count == 0)
        {
            LblRestoreBackupDesc.Text = Strings.Get("ModPropRestoreNone");
            return;
        }

        for (int i = 0; i < backups.Count; i++)
        {
            var b = backups[i];
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = System.IO.Path.GetFileName(b.Path),
                Style = (Style)FindResource("SetRowTitle"),
            });
            string when = b.CreatedAt == DateTime.MinValue
                ? "—"
                : b.CreatedAt.ToString("yyyy-MM-dd  HH:mm");
            text.Children.Add(new TextBlock
            {
                Text = $"{when}  ·  {Services.DiskSpaceService.FormatBytes(b.TotalBytes)}",
                Style = (Style)FindResource("SetMonoValue"),
                Margin = new Thickness(0, 4, 0, 0),
            });
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var restore = new Button
            {
                Content = Strings.Get("ModPropRestoreBtn"),
                Style = (Style)FindResource("SetActionButtonSm"),
                IsEnabled = installed && !_modBusy,
            };
            // Every row restores the SAME way the single button did — the dialog owns no
            // per-backup restore, so the choice is the newest-first order the list shows.
            restore.Click += (_, _) => RestoreBackupBtn_Click(restore, new RoutedEventArgs());
            Grid.SetColumn(restore, 1);
            grid.Children.Add(restore);

            BackupsList.Children.Add(new Border
            {
                Style = (Style)FindResource(
                    i == backups.Count - 1 ? "SetActionRowLast" : "SetActionRow"),
                Child = grid,
            });
        }
    }


    /// <summary>
    /// Fills the game-settings section: the sharing checkbox from this mod's state, and the
    /// source picker from the mods that can supply settings.
    ///
    /// <para>Called ONCE from the constructor. It must not be folded into
    /// <see cref="LoadUserData"/>, which <see cref="RefreshData"/> re-runs after a backup or a
    /// folder change — that would reset the combo under the user's hand.</para>
    /// </summary>
    private void LoadGameSettings()
    {
        // Resolve rather than read the manifest field: most mods never declare one, and gating on
        // the raw value hid this whole feature from them — the reason it looked like it had been
        // lost. Only the base game genuinely has nothing here (the launcher manages none of its
        // files), and for it the section stays collapsed exactly as the backup rows are inert.
        var folderName = Services.UserDataService.ResolveFolderName(_profile, _config);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            GameSettingsSection.Visibility = Visibility.Collapsed;
            return;
        }
        GameSettingsSection.Visibility = Visibility.Visible;

        SyncSettingsCheck.IsChecked = _config.GetState(_profile.Id).SyncGameSettings;

        var sources = _listSettingsSources?.Invoke() ?? new List<ModProfile>();
        SettingsSourceCombo.Items.Clear();
        foreach (var p in sources)
            SettingsSourceCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = p.DisplayName,
                Tag = p.Id,
            });

        var any = SettingsSourceCombo.Items.Count > 0;
        if (any) SettingsSourceCombo.SelectedIndex = 0;
        SettingsSourceCombo.IsEnabled = any;
        ImportSettingsBtn.IsEnabled = any;
        // Say why the picker is empty instead of leaving a dead control: with only one mod
        // installed there is simply nowhere to import from yet.
        LblGameSettingsDesc.Text = any
            ? Strings.Get("ModPropSettingsImportDesc")
            : Strings.Get("ModPropSettingsNoSources");
    }

    /// <summary>Paints the inline result line under the USER DATA rows
    /// (success text from the callback; hidden when null/cancelled).</summary>
    private void ShowUserDataResult(string? result)
    {
        if (string.IsNullOrEmpty(result))
        {
            UserDataResultHint.Visibility = Visibility.Collapsed;
            return;
        }
        UserDataResultHint.Text = result;
        UserDataResultHint.Foreground = new SolidColorBrush(Color.FromRgb(0x7d, 0xc9, 0x7d));
        UserDataResultHint.Visibility = Visibility.Visible;
    }

    private void LoadLanguage()
    {
        LanguageCardList.Children.Clear();

        // While the mod is installing/updating, lock the whole list: show the
        // banner and disable the Refresh button (cards render disabled below).
        LanguageBusyHint.Visibility = _modBusy ? Visibility.Visible : Visibility.Collapsed;
        RefreshTranslationsBtn.IsEnabled = !_modBusy;

        var activeId = _config.GetActiveState().ActiveTranslationId ?? "";
        var activeVersion = _config.GetActiveState().ActiveTranslationVersion ?? "";
        var modVersion = _service.CurrentVersion?.Ver;

        // English (default) — always available.
        LanguageCardList.Children.Add(BuildLanguageCard(
            "🌐", Strings.Get("MenuLangEnglish"), "", null, "",
            isActive: string.IsNullOrEmpty(activeId), blocked: false, compatible: false,
            onUse: () => _revertToEnglish?.Invoke()));

        var entries = new Dictionary<string, TranslationIndexEntry>(StringComparer.OrdinalIgnoreCase);
        if (_translationIndex != null)
            foreach (var e in _translationIndex.Translations) entries[e.Id] = e;
        try
        {
            if (!string.IsNullOrEmpty(_service.InstallPath))
            {
                var installed = new TranslationService(
                    _service.InstallPath, _service.Profile.Translations?.CoveredFiles).ListInstalled();
                foreach (var m in installed)
                    if (!entries.ContainsKey(m.Id))
                        entries[m.Id] = new TranslationIndexEntry
                        {
                            Id = m.Id, Name = m.Name, Author = m.Author,
                            Version = m.Version, CompatibleWith = m.CompatibleWith,
                        };
            }
        }
        catch { /* probe failure is non-fatal */ }

        // Active first → compatible-with-installed-version → newest → name
        // (shared with the gear menu via TranslationCompat.OrderForDisplay).
        var ordered = TranslationCompat.OrderForDisplay(
            entries.Values, _translationIndex?.Translations, modVersion, activeId);
        foreach (var entry in ordered)
        {
            bool isActive = string.Equals(entry.Id, activeId, StringComparison.OrdinalIgnoreCase);
            // Block on version grounds only when NOT the active pack (the active
            // one demonstrably works); the apply dialog's hash check is the final
            // word, so this is a pre-filter, not the sole authority.
            bool blocked = !isActive
                && TranslationCompat.IsVersionBlocked(entry.CompatibleWith, modVersion);
            // Positive counterpart: the translator declared THIS installed version,
            // so affirm it (green ✓). "unknown" (empty declared list) gets neither.
            bool compatible = !isActive
                && TranslationCompat.IsCompatible(entry.CompatibleWith, modVersion);
            var captured = entry;
            // Folder pack with a version HISTORY → a card with a version picker
            // (the user can apply an older version). Otherwise the classic
            // whole-card-click button.
            if (captured.Versions is { Count: > 1 })
            {
                LanguageCardList.Children.Add(BuildVersionedLanguageCard(
                    captured, isActive, activeVersion, modVersion,
                    v => ApplyChosenVersion(captured, v)));
            }
            else
            {
                LanguageCardList.Children.Add(BuildLanguageCard(
                    LanguageFlag(entry.Id), entry.Name, entry.Author, entry.CompatibleWith, entry.Version,
                    isActive, blocked, compatible, () => _applyTranslation?.Invoke(captured)));
            }
        }

        bool hasPacks = entries.Count > 0;
        LanguageEmptyHint.Visibility = hasPacks ? Visibility.Collapsed : Visibility.Visible;
    }

    private FrameworkElement BuildLanguageCard(string flag, string name, string author,
        IReadOnlyList<string>? compatibleWith, string packVersion,
        bool isActive, bool blocked, bool compatible, Action onUse)
    {
        var col = new StackPanel();
        var title = new TextBlock
        {
            Text = $"{flag}  {name}" + (string.IsNullOrWhiteSpace(author) ? "" : $"    ·  {author}"),
            FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = Res("MpTextPrimary", "#E8EEF6"), TextWrapping = TextWrapping.Wrap,
        };
        col.Children.Add(title);

        var subParts = new List<string>();
        if (compatibleWith != null && compatibleWith.Count > 0)
            subParts.Add(Strings.Format("LangCardForMod", string.Join(", ", compatibleWith)));
        if (!string.IsNullOrWhiteSpace(packVersion))
            subParts.Add(Strings.Format("LangCardPackVer", packVersion));
        if (subParts.Count > 0)
            col.Children.Add(new TextBlock
            {
                Text = string.Join("       ", subParts), FontSize = 12,
                Foreground = Res("MpTextMuted", "#8EA4C0"),
                Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
        if (blocked)
            col.Children.Add(new TextBlock
            {
                Text = Strings.Get("LangCardBlockedHint"), FontSize = 12,
                Foreground = Res("MpDestructiveText", "#D99A9A"),
                Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
        else if (compatible)
            col.Children.Add(new TextBlock
            {
                Text = "✓ " + Strings.Get("LangCardCompatibleHint"), FontSize = 12,
                Foreground = Res("MpOkText", "#8FE0B0"),
                Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            });

        // Status indicator (not a button anymore — the WHOLE card is the click
        // target, which is more forgiving than a small "Use" button). A version-
        // mismatched pack reads "Use anyway" in amber — a warning the user can
        // override, NOT a block (the apply dialog confirms first). While the mod
        // is installing/updating (_modBusy) every card reads "🔒 Unavailable".
        var status = new TextBlock
        {
            Text = _modBusy ? "🔒 " + Strings.Get("LangCardUnavailableBusy")
                : isActive ? Strings.Get("LangCardActive")
                : blocked ? Strings.Get("LangCardUseAnyway") : Strings.Get("LangCardUse"),
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = _modBusy ? Res("MpTextMuted", "#8EA4C0")
                : isActive ? Res("MpOkText", "#8FE0B0")
                : blocked ? Res("MpCautionText", "#D8BD8A") : Res("MpAction", "#2F7FE0"),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10, 0, 0, 0),
        };

        var grid = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(col, 0);
        Grid.SetColumn(status, 1);
        grid.Children.Add(col);
        grid.Children.Add(status);

        // A version-mismatched ("blocked") pack stays clickable: the user can apply
        // it under their own responsibility and the apply dialog confirms first.
        // The already-active pack is non-clickable (nothing to do); and while the
        // mod is installing/updating (_modBusy) the WHOLE list is locked.
        bool clickable = !isActive && !_modBusy;
        var border = new Border
        {
            Background = Res("MpPanel", "#12213A"),
            BorderBrush = isActive ? Res("MpAction", "#2F7FE0") : Res("MpRimSoft", "#1C82AFFF"),
            BorderThickness = new Thickness(isActive ? 2 : 1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 8),
            // Busy = clearly disabled (0.5); a version-mismatch caution = slightly
            // dimmed (0.85, not inert); otherwise full.
            Opacity = _modBusy ? 0.5 : (blocked ? 0.85 : 1.0),
            Child = grid,
        };
        if (!clickable) return border;

        // Wrap the whole card in a CHROMELESS Button. Button.Click fires reliably
        // (MouseLeftButtonUp on a Border can be swallowed by the surrounding
        // ScrollViewer), and we get keyboard/focus behaviour for free. The custom
        // template strips the default button chrome so it still looks like a card.
        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)),
        };
        var button = new Button
        {
            Content = border,
            Template = template,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        button.Click += (_, _) => onUse();
        return button;
    }

    /// <summary>
    /// A language card for a folder pack that keeps a VERSION HISTORY: a version
    /// combo + an Apply button (mirrors the GitHubReleases version picker). Unlike
    /// the single-version card, the whole card is NOT a click target (it holds
    /// interactive controls); applying uses the selected version. The for-mod /
    /// compatibility hint + Apply label re-compute on each combo selection.
    /// </summary>
    private FrameworkElement BuildVersionedLanguageCard(
        TranslationIndexEntry entry, bool isActive, string activeVersion,
        string? modVersion, Action<TranslationVersion> onUseVersion)
    {
        var versions = entry.Versions;

        var col = new StackPanel();
        col.Children.Add(new TextBlock
        {
            Text = $"{LanguageFlag(entry.Id)}  {entry.Name}"
                   + (string.IsNullOrWhiteSpace(entry.Author) ? "" : $"    ·  {entry.Author}"),
            FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = Res("MpTextPrimary", "#E8EEF6"), TextWrapping = TextWrapping.Wrap,
        });

        var subLine = new TextBlock
        {
            FontSize = 12, Foreground = Res("MpTextMuted", "#8EA4C0"),
            Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        col.Children.Add(subLine);
        var hint = new TextBlock
        {
            FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        col.Children.Add(hint);

        var combo = new System.Windows.Controls.ComboBox
        {
            MinWidth = 200, VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !_modBusy, Margin = new Thickness(0, 8, 0, 0),
        };
        int compatIdx = -1, activeIdx = -1;
        // When versions for this id come from more than one repo (merged
        // multi-repo), show each version's source repo so the user can tell
        // whose "ES-LA 1.0" is whose.
        bool multiSource = versions
            .Select(v => v.SourceRepo ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() > 1;
        for (int i = 0; i < versions.Count; i++)
        {
            var v = versions[i];
            var tags = new List<string>();
            if (i == 0) tags.Add(Strings.Get("LangCardVerNewest"));
            if (isActive && !string.IsNullOrEmpty(activeVersion)
                && string.Equals(v.Version, activeVersion, StringComparison.OrdinalIgnoreCase))
            { tags.Add(Strings.Get("LangCardVerActive")); activeIdx = i; }
            if (compatIdx < 0 && TranslationCompat.IsCompatible(v.CompatibleWith, modVersion)) compatIdx = i;
            var srcSuffix = multiSource && !string.IsNullOrWhiteSpace(v.SourceRepo)
                ? $"  ·  {v.SourceRepo}" : "";
            var label = (tags.Count > 0 ? $"{v.Version}  —  {string.Join(", ", tags)}" : v.Version)
                        + srcSuffix;
            combo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = label, Tag = v });
        }

        var applyBtn = new Button
        {
            Style = (Style)FindResource("PropertyActionButton"),
            Margin = new Thickness(10, 8, 0, 0),
            MinWidth = 150, VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !_modBusy,
        };

        void Refresh()
        {
            var v = (combo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as TranslationVersion
                    ?? versions[0];
            bool blocked = TranslationCompat.IsVersionBlocked(v.CompatibleWith, modVersion);
            bool compat = TranslationCompat.IsCompatible(v.CompatibleWith, modVersion);

            var parts = new List<string>();
            if (v.CompatibleWith is { Count: > 0 })
                parts.Add(Strings.Format("LangCardForMod", string.Join(", ", v.CompatibleWith)));
            if (!string.IsNullOrWhiteSpace(v.Version))
                parts.Add(Strings.Format("LangCardPackVer", v.Version));
            subLine.Text = string.Join("       ", parts);
            subLine.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (blocked) { hint.Text = Strings.Get("LangCardBlockedHint"); hint.Foreground = Res("MpDestructiveText", "#D99A9A"); }
            else if (compat) { hint.Text = "✓ " + Strings.Get("LangCardCompatibleHint"); hint.Foreground = Res("MpOkText", "#8FE0B0"); }
            else hint.Text = "";
            hint.Visibility = string.IsNullOrEmpty(hint.Text) ? Visibility.Collapsed : Visibility.Visible;

            applyBtn.Content = _modBusy ? "🔒 " + Strings.Get("LangCardUnavailableBusy")
                : blocked ? Strings.Get("LangCardUseAnyway")
                : Strings.Get("LangCardApplyVersion");
        }

        combo.SelectionChanged += (_, _) => Refresh();
        // Default: the active version → first compatible-with-installed-mod → newest.
        combo.SelectedIndex = activeIdx >= 0 ? activeIdx : (compatIdx >= 0 ? compatIdx : 0);
        Refresh();

        applyBtn.Click += (_, _) =>
        {
            if (_modBusy) return;
            if ((combo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag is TranslationVersion v)
                onUseVersion(v);
        };

        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(combo);
        row.Children.Add(applyBtn);
        col.Children.Add(row);

        var grid = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        grid.Children.Add(col);

        return new Border
        {
            Background = Res("MpPanel", "#12213A"),
            BorderBrush = isActive ? Res("MpAction", "#2F7FE0") : Res("MpRimSoft", "#1C82AFFF"),
            BorderThickness = new Thickness(isActive ? 2 : 1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = _modBusy ? 0.5 : 1.0,
            Child = grid,
        };
    }

    /// <summary>Applies a chosen version of a folder pack through the shared apply
    /// callback by cloning the entry with that version's URL / hashes / compat.</summary>
    private void ApplyChosenVersion(TranslationIndexEntry entry, TranslationVersion v)
    {
        _applyTranslation?.Invoke(new TranslationIndexEntry
        {
            Id = entry.Id,
            Name = entry.Name,
            Language = entry.Language,
            Author = entry.Author,
            Version = v.Version,
            CompatibleWith = v.CompatibleWith,
            DownloadUrl = v.DownloadUrl,
            Size = v.Size,
            Description = entry.Description,
            TargetMod = entry.TargetMod,
            ContentHash = v.ContentHash,
            FromFolder = entry.FromFolder,
        });
    }

    private Brush Res(string key, string fallbackHex)
    {
        if (TryFindResource(key) is Brush b) return b;
        try { return (Brush)new BrushConverter().ConvertFromString(fallbackHex)!; }
        catch { return Brushes.Gray; }
    }

    private static string LanguageFlag(string id) => id.ToLowerInvariant() switch
    {
        "es" or "es-es" or "es-mx" or "es-ar" => "🇪🇸",
        "fr" or "fr-fr" => "🇫🇷",
        "de" or "de-de" => "🇩🇪",
        "it" or "it-it" => "🇮🇹",
        "pt" or "pt-pt" => "🇵🇹",
        "pt-br" => "🇧🇷",
        "ru" or "ru-ru" => "🇷🇺",
        "zh" or "zh-cn" or "zh-tw" => "🇨🇳",
        "ja" or "ja-jp" => "🇯🇵",
        "ko" or "ko-kr" => "🇰🇷",
        "pl" or "pl-pl" => "🇵🇱",
        _ => "🌐",
    };

    // -- Tab switching ------------------------------------------------------

    private void SetActiveTab(Button activeBtn)
    {
        // The section heading. It was declared in XAML and never filled, so GENERAL,
        // LOCAL FILES and USER DATA showed no name at all and every section carried an
        // empty line where one belonged. The text is READ OFF THE RAIL LABEL rather than
        // from a second table of string keys: one source, so the heading and the item you
        // clicked can never disagree, and it is already localized by the time this runs.
        ModSectionTitle.Text = LabelOf(activeBtn)?.Text ?? "";

        TabGeneralBtn.Tag = ReferenceEquals(activeBtn, TabGeneralBtn) ? "active" : null;
        TabLocalFilesBtn.Tag = ReferenceEquals(activeBtn, TabLocalFilesBtn) ? "active" : null;
        TabUserDataBtn.Tag = ReferenceEquals(activeBtn, TabUserDataBtn) ? "active" : null;
        TabLanguageBtn.Tag = ReferenceEquals(activeBtn, TabLanguageBtn) ? "active" : null;
        TabAddonsBtn.Tag = ReferenceEquals(activeBtn, TabAddonsBtn) ? "active" : null;
        TabDecksBtn.Tag = ReferenceEquals(activeBtn, TabDecksBtn) ? "active" : null;
        TabStatsBtn.Tag = ReferenceEquals(activeBtn, TabStatsBtn) ? "active" : null;

        GeneralPanel.Visibility = ReferenceEquals(activeBtn, TabGeneralBtn) ? Visibility.Visible : Visibility.Collapsed;
        LocalFilesPanel.Visibility = ReferenceEquals(activeBtn, TabLocalFilesBtn) ? Visibility.Visible : Visibility.Collapsed;
        UserDataPanel.Visibility = ReferenceEquals(activeBtn, TabUserDataBtn) ? Visibility.Visible : Visibility.Collapsed;
        LanguagePanel.Visibility = ReferenceEquals(activeBtn, TabLanguageBtn) ? Visibility.Visible : Visibility.Collapsed;
        AddonsPanel.Visibility = ReferenceEquals(activeBtn, TabAddonsBtn) ? Visibility.Visible : Visibility.Collapsed;
        DecksPanel.Visibility = ReferenceEquals(activeBtn, TabDecksBtn) ? Visibility.Visible : Visibility.Collapsed;
        StatsPanel.Visibility = ReferenceEquals(activeBtn, TabStatsBtn) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The rail label that belongs to a rail button.</summary>
    private TextBlock? LabelOf(Button btn)
    {
        if (ReferenceEquals(btn, TabGeneralBtn)) return TabGeneralLabel;
        if (ReferenceEquals(btn, TabLocalFilesBtn)) return TabLocalFilesLabel;
        if (ReferenceEquals(btn, TabUserDataBtn)) return TabUserDataLabel;
        if (ReferenceEquals(btn, TabLanguageBtn)) return TabLanguageLabel;
        if (ReferenceEquals(btn, TabAddonsBtn)) return TabAddonsLabel;
        if (ReferenceEquals(btn, TabDecksBtn)) return TabDecksLabel;
        if (ReferenceEquals(btn, TabStatsBtn)) return TabStatsLabel;
        return null;
    }

    private void TabGeneralBtn_Click(object sender, RoutedEventArgs e) => SetActiveTab(TabGeneralBtn);
    private void TabLocalFilesBtn_Click(object sender, RoutedEventArgs e) => SetActiveTab(TabLocalFilesBtn);
    private void TabUserDataBtn_Click(object sender, RoutedEventArgs e) => SetActiveTab(TabUserDataBtn);
    private void TabLanguageBtn_Click(object sender, RoutedEventArgs e) => SetActiveTab(TabLanguageBtn);
    private void TabAddonsBtn_Click(object sender, RoutedEventArgs e) => SetActiveTab(TabAddonsBtn);

    private void TabDecksBtn_Click(object sender, RoutedEventArgs e)
    {
        SetActiveTab(TabDecksBtn);
        // Same reason STATISTICS loads late: resolving card names, descriptions and icons
        // streams 12 MB of tech files and indexes five archives. Nobody should pay that for
        // opening Properties to change a folder.
        _ = LoadDecksAsync();
    }

    private void TabStatsBtn_Click(object sender, RoutedEventArgs e)
    {
        SetActiveTab(TabStatsBtn);
        // Loaded on first sight rather than in the constructor: resolving the unit names streams
        // every proto file the mod ships, which is 12 MB for Wars of Liberty. Nobody should pay
        // that for opening Properties to change a folder.
        _ = LoadStatsAsync();
    }

    /// <summary>Opens the dialog directly on the Language tab (used by the
    /// "new translation" notification so a click lands where packs are applied).</summary>
    public void ShowLanguageTab() => SetActiveTab(TabLanguageBtn);

    /// <summary>
    /// Filters the six sections down to the rows matching what you typed, and jumps to the
    /// first section that has one. The rule itself lives in <see cref="SectionSearch"/>,
    /// shared with the launcher settings window so the two cannot drift apart.
    /// </summary>
    private void ModSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = (ModSearchBox.Text ?? "").Trim();
        ModSearchPlaceholder.Visibility =
            q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var sections = SearchSections().ToList();

        if (q.Length == 0)
        {
            SectionSearch.Restore(sections);
            ModSearchNoResults.Visibility = Visibility.Collapsed;
            return;
        }

        var hit = SectionSearch.Apply(q, sections);
        ModSearchNoResults.Visibility = hit is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The sections the search covers, in the order that decides the first hit.
    ///
    /// <para>Two of these are not the plain "show the panel" you would expect. STATS goes
    /// through its CLICK handler rather than <c>SetActiveTab</c>, because that handler is also
    /// what starts the lazy load — jumping there any other way lands on a panel that never
    /// loaded. And a section whose rail button is hidden is skipped entirely: the stock game
    /// has no STATS entry, and a search must not send anyone somewhere they cannot get back
    /// to.</para>
    ///
    /// <para>LANGUAGE, ADDONS and STATS are still listed even though their contents are built
    /// at runtime and carry no <c>SetRow</c> styles. They contribute their static labels and
    /// hints, which is what makes typing "idioma" or "addon" land on the right page.</para>
    /// </summary>
    private IEnumerable<SectionSearch.Section> SearchSections()
    {
        foreach (var (btn, panel, activate) in new (Button, Panel, Action)[]
                 {
                     (TabGeneralBtn, GeneralPanel, () => SetActiveTab(TabGeneralBtn)),
                     (TabLocalFilesBtn, LocalFilesPanel, () => SetActiveTab(TabLocalFilesBtn)),
                     (TabUserDataBtn, UserDataPanel, () => SetActiveTab(TabUserDataBtn)),
                     (TabLanguageBtn, LanguagePanel, () => SetActiveTab(TabLanguageBtn)),
                     (TabAddonsBtn, AddonsPanel, () => SetActiveTab(TabAddonsBtn)),
                     (TabDecksBtn, DecksPanel, () => TabDecksBtn_Click(TabDecksBtn, null!)),
                     (TabStatsBtn, StatsPanel, () => TabStatsBtn_Click(TabStatsBtn, null!)),
                 })
        {
            if (btn.Visibility != Visibility.Visible) continue;
            yield return new SectionSearch.Section(panel, activate);
        }
    }

    // -- Action handlers ----------------------------------------------------
    //
    // Only handlers whose flow lands on the MAIN WINDOW close this dialog:
    // Verify / Repair (their progress runs on the main-window progress strip,
    // which a non-modal Properties window would otherwise cover) and Uninstall
    // (the mod is gone afterwards, so the open view would be stale). Everything
    // else STAYS OPEN: the path pickers and the backup/restore dialogs are
    // modals that appear on top with nothing to uncover, so closing only
    // disoriented the user — instead those handlers refresh the displayed
    // paths/state in place via RefreshData() when the modal returns. Handlers
    // that just open Explorer/Notepad (Open folder, Open AoE3 folder, Open
    // user-data folder, View logs), the website/language handlers, and "Check
    // for updates" never closed (nothing to land on); check-for-updates shows
    // its result inline.
    //
    // None of these set DialogResult: the dialog is shown non-modally
    // via Show() from MainWindow, and setting DialogResult outside of
    // ShowDialog() throws InvalidOperationException. The caller never
    // read DialogResult here anyway — the post-close refresh
    // (RefreshIdlePanel + RefreshActiveModBanner) runs on the Closed
    // event regardless of how the dialog was dismissed.

    /// <summary>
    /// Re-reads config and repaints the data-bearing labels (General /
    /// Local Files / User Data) without disturbing the active tab or the
    /// language combo. Called by the stay-open action handlers after their
    /// modal returns, and by MainWindow once an async folder re-detection
    /// completes, so an open Properties window reflects the new paths /
    /// version / user-data state in place.
    /// </summary>
    public void RefreshData()
    {
        LoadGeneral();
        LoadLocalFiles();
        LoadUserData();
    }

    /// <summary>
    /// Greys the dialog's explicit online-fetch actions (check-for-updates, refresh
    /// translations) when the app is offline, matching the app-wide offline mode. The
    /// version picker self-disables offline (no releases load), and "Check for updates"
    /// otherwise degrades gracefully to cached state. Runs once at construction —
    /// connectivity rarely flips during this short-lived dialog.
    /// </summary>
    private void ApplyConnectivityGate()
    {
        bool offline = Services.ConnectivityState.IsOffline;
        object? tip = offline ? Strings.Get("OfflineNeedsInternet") : null;
        if (CheckUpdatesBtn != null) { CheckUpdatesBtn.IsEnabled = !offline; CheckUpdatesBtn.ToolTip = tip; }
        if (RefreshTranslationsBtn != null) { RefreshTranslationsBtn.IsEnabled = !offline; RefreshTranslationsBtn.ToolTip = tip; }
    }

    /// <summary>
    /// Rebuilds the language cards so the active/compatible state reflects the
    /// latest config (e.g. right after a translation is applied or reverted).
    /// Called by MainWindow once the apply/revert flow finishes.
    /// </summary>
    public void RefreshLanguageTab() => LoadLanguage();

    /// <summary>
    /// Called after the active install COPY was switched (LOCAL FILES → Manage
    /// installs → Switch). Re-points the dialog at the now-active copy's
    /// UpdateService and rebuilds the version-dependent tabs — critically the
    /// LANGUAGE tab, whose translation-compat check reads
    /// <c>_service.CurrentVersion</c>. Without re-pointing <c>_service</c>,
    /// <see cref="LoadLanguage"/> would keep reading the PRE-switch copy's version,
    /// so a pack's compatible/incompatible badge stayed stale until the dialog was
    /// closed and reopened. <see cref="RefreshData"/> alone doesn't cover this — it
    /// deliberately skips the language tab.
    /// </summary>
    public void OnActiveInstallSwitched(UpdateService service)
    {
        _service = service;
        RefreshData();          // General / Local Files / User Data
        RefreshLanguageTab();   // recompute translation compat vs the new copy's version
    }

    /// <summary>
    /// Lock / unlock the language list while the mod is installing or updating.
    /// Called from MainWindow.SetBusy (real ops only, not the read-only check).
    /// Rebuilds the cards so they render disabled + show the busy banner.
    /// </summary>
    public void SetModBusy(bool busy)
    {
        if (_modBusy == busy) return;
        _modBusy = busy;
        LoadLanguage();
    }

    private async void RefreshTranslationsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshTranslations == null) return;
        var original = RefreshTranslationsBtn.Content;
        RefreshTranslationsBtn.IsEnabled = false;
        RefreshTranslationsBtn.Content = Strings.Get("DlgLangRefreshing");
        try
        {
            var idx = await _refreshTranslations();
            if (idx != null) _translationIndex = idx;
            LoadLanguage();   // rebuild the cards with the freshly fetched index
        }
        catch { /* re-fetch failure is non-fatal; cards keep the old index */ }
        finally
        {
            RefreshTranslationsBtn.Content = original;
            RefreshTranslationsBtn.IsEnabled = true;
        }
    }

    private async void ClearTempBtn_Click(object sender, RoutedEventArgs e)
    {
        var original = ClearTempBtn.Content;
        ClearTempBtn.IsEnabled = false;
        ClearTempBtn.Content = Strings.Get("DlgTempClearing");
        ClearTempResult.Visibility = Visibility.Collapsed;

        bool ok = false;
        try
        {
            await System.Threading.Tasks.Task.Run(NativeInstallService.TryCleanupTemp);
            ok = true;
            ClearTempResult.Text = Strings.Get("DlgTempCleared");
        }
        catch
        {
            ClearTempResult.Text = Strings.Get("DlgTempClearFailed");
        }
        finally
        {
            ClearTempResult.Visibility = Visibility.Visible;
            ClearTempBtn.Content = original;
            ClearTempBtn.IsEnabled = true;
        }

        // A clear popup so the user actually sees that something happened.
        MessageBox.Show(this,
            Strings.Get(ok ? "DlgTempCleared" : "DlgTempClearFailed"),
            Strings.Get("DlgTempClearedTitle"),
            MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void ValWebsite_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        // Profile-supplied url — validated at open time by SafeUrl, since a
        // built-in profile never passes through the catalog's schema check.
        => SafeUrl.TryOpen(_profile.OfficialWebsite);

    /// <summary>
    /// "Check for updates" runs in-place: it does NOT close the dialog
    /// (the check has no separate window to land on — the result is just
    /// a yes/no), so closing left the user staring at the main window
    /// with no idea whether anything happened. Instead we disable the
    /// button, show a "checking…" line, run the real check on the main
    /// window (which also refreshes its PLAY/UPDATE button + cache), and
    /// render the outcome right here.
    /// </summary>
    private async void CheckUpdatesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_checkForUpdates == null) return;

        CheckUpdatesBtn.IsEnabled = false;
        SetCheckingState();
        try
        {
            var result = await _checkForUpdates();

            // Refresh the version labels in case the check discovered a
            // newly-detected install / version.
            LoadGeneral();

            // The shared judge, the same one the dashboard and the Workshop row use.
            // This branch used to read result.PendingDownloads.Count, which UpdateService
            // returns EMPTY by construction for GitHubReleases / Manual / DelegatedExternal
            // - so for those mods the "update available" case was unreachable and the panel
            // could only ever say "up to date".
            ApplyUpdateState(result);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"ModPropertiesDialog.CheckUpdates failed: {ex.Message}");
            SetUpdateState(Services.UpdateVerdict.UpdateOffer.Failed, null);
        }
        finally
        {
            CheckUpdatesBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Paints the whole update-state panel - chip colour, glyph, title AND body - from one
    /// verdict.
    ///
    /// <para>All four used to be independent: the icon and the glyph had no code-behind
    /// reference at all (permanently green, permanently a checkmark), the title was assigned
    /// once in <c>ApplyStrings</c>, and only the body ever changed. Since the body's
    /// up-to-date sentence used the same key as the frozen title, the ordinary state printed
    /// the same sentence twice.</para>
    /// </summary>
    /// <param name="offer">The verdict.</param>
    /// <param name="result">The check it came from, for the version numbers in the body.
    /// Null is allowed and simply leaves them out.</param>
    private void SetUpdateState(
        Services.UpdateVerdict.UpdateOffer offer, UpdateService.CheckResult? result)
    {
        var current = result?.CurrentVersion?.Ver ?? _service.CurrentVersion?.Ver ?? "";
        var latest = result?.LatestVersion?.Ver ?? "";

        string title;
        string body;
        string chipKey;
        string glyphKey;
        string glyph;

        switch (offer)
        {
            case Services.UpdateVerdict.UpdateOffer.UpdateAvailable:
                title = Strings.Get("ModPropUpdateAvailableTitle");
                body = Strings.Format("ModPropUpdateAvailableBody", latest, current);
                chipKey = "MpChipCautionBg";
                glyphKey = "MpCautionText";
                glyph = "\uE896";   // download
                break;

            case Services.UpdateVerdict.UpdateOffer.VersionUnknown:
                title = Strings.Get("ModPropUpdateUnknownTitle");
                body = Strings.Format("ModPropUpdateUnknownBody", latest);
                chipKey = "MpChipCautionBg";
                glyphKey = "MpCautionText";
                glyph = "\uE9CE";   // unknown
                break;

            case Services.UpdateVerdict.UpdateOffer.PausedByPin:
                title = Strings.Get("ModPropUpdatePausedTitle");
                body = Strings.Format("ModPropUpdatePausedBody", latest, current);
                chipKey = "MpChipCautionBg";
                glyphKey = "MpCautionText";
                glyph = "\uE769";   // pause
                break;

            case Services.UpdateVerdict.UpdateOffer.NotInstalled:
                title = Strings.Get("ModPropNotInstalledTitle");
                body = Strings.Get("ModPropCheckNotInstalled");
                chipKey = "MpChipCautionBg";
                glyphKey = "MpCautionText";
                glyph = "\uE7BA";   // warning
                break;

            case Services.UpdateVerdict.UpdateOffer.Failed:
                title = Strings.Get("ModPropCheckFailedTitle");
                body = Strings.Get("ModPropCheckFailed");
                chipKey = "MpChipDangerBg";
                glyphKey = "MpDestructiveText";
                glyph = "\uEA39";   // error
                break;

            default:
                title = Strings.Get("ModPropUpToDate");
                // The version is NAMED, not just asserted: "you are up to date" over a
                // number the user cannot see is exactly the claim that turned out false.
                body = string.IsNullOrEmpty(current)
                    ? ""
                    : Strings.Format("ModPropUpToDateBody", current);
                chipKey = "MpChipOkBg";
                glyphKey = "MpOkText";
                glyph = "\uE73E";   // checkmark
                break;
        }

        // Belt and braces for the defect this panel started with: the two halves are never
        // allowed to be the same sentence.
        if (string.Equals(title, body, StringComparison.Ordinal)) body = "";

        UpdateStateTitle.Text = title;
        UpdateStateGlyph.Text = glyph;
        ApplyBrush(UpdateStateIcon, Border.BackgroundProperty, chipKey);
        ApplyBrush(UpdateStateGlyph, TextBlock.ForegroundProperty, glyphKey);

        CheckUpdatesResult.Text = body;
        CheckUpdatesResult.Visibility =
            string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The in-flight state. Not a verdict, so it keeps whatever chip the panel was
    /// already showing and changes only the words.</summary>
    private void SetCheckingState()
    {
        UpdateStateTitle.Text = Strings.Get("ModPropCheckingTitle");
        CheckUpdatesResult.Text = Strings.Get("ModPropChecking");
        CheckUpdatesResult.Visibility = Visibility.Visible;
    }

    /// <summary>Evaluate a check result and paint it.</summary>
    private void ApplyUpdateState(UpdateService.CheckResult? result) =>
        SetUpdateState(
            Services.UpdateVerdict.Evaluate(result, _profile, _config.GetState(_profile.Id)),
            result);

    /// <summary>
    /// Paint the panel from what the main window already knows, with no network call. Called
    /// on open, so the dialog never starts by claiming a state it has not checked.
    /// </summary>
    private void RefreshUpdateState() => ApplyUpdateState(_lastCheckResult?.Invoke());

    /// <summary>Resolve a brush by resource key, leaving the property alone when the key is
    /// missing rather than assigning a null brush.</summary>
    private void ApplyBrush(
        System.Windows.DependencyObject target,
        System.Windows.DependencyProperty property,
        string brushKey)
    {
        if (TryFindResource(brushKey) is System.Windows.Media.Brush brush)
            target.SetValue(property, brush);
    }

    // ------------------------------------------------------------------------
    // Version picker (Fase 1) — GitHubReleases mods only.
    // ------------------------------------------------------------------------

    /// <summary>
    /// Populates the "Version" section for GitHubReleases mods: fetches the
    /// repo's published releases, lists them newest-first (annotating the
    /// installed / recommended / pre-release ones) and pre-selects the installed
    /// version. Stays collapsed for any other mechanism (callbacks null). Network
    /// failures show an inline hint instead of throwing.
    /// </summary>
    private async void LoadVersions()
    {
        // Gate the whole section to: callbacks wired AND a GitHubReleases mod AND
        // actually installed. Version SWITCH re-overlays onto an existing install
        // (RepairInstallAsync needs the install path); a fresh first install picks
        // the recommended tag through the normal Install flow, not here.
        bool isInstalled = !string.IsNullOrWhiteSpace(_service.CurrentVersion?.Ver);
        // External-hosted payloads pin a SHA-256 for the approved tag ONLY, so no
        // other version can be verified — hide the picker rather than list
        // versions that would fail to install.
        bool externalHosted =
            !string.IsNullOrWhiteSpace(_profile.GitHubReleases?.ExternalAssetUrlTemplate);
        if (_listVersions == null || _installVersion == null
            || _profile.UpdateMechanism != ModUpdateMechanism.GitHubReleases
            || externalHosted
            || !isInstalled)
        {
            VersionSection.Visibility = Visibility.Collapsed;
            return;
        }

        VersionSection.Visibility = Visibility.Visible;
        VersionCombo.IsEnabled = false;
        InstallVersionBtn.IsEnabled = false;
        SetVersionStatus(Strings.Get("ModPropVersionsLoading"), "TextSecondary");

        IReadOnlyList<GitHubReleaseDownloader.ReleaseInfo> releases;
        try
        {
            releases = await _listVersions();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"ModProperties: load versions failed: {ex.Message}");
            SetVersionStatus(Strings.Get("ModPropVersionsFailed"), "ErrorBrush");
            return;
        }

        if (releases.Count == 0)
        {
            SetVersionStatus(Strings.Get("ModPropVersionsNone"), "TextSecondary");
            return;
        }

        // "Recommended" badge = the effective default tag (approved, or the
        // cached latest for follow-latest mods). Not cosmetic: ListReleasesAsync
        // KEEPS prereleases, so the newest list item may not be the effective
        // latest — this badge is the only correct signal in the picker.
        var recommended = UpdateService.ResolveEffectiveGitHubTag(
            _profile.GitHubReleases, _config.GetState(_profile.Id).LastKnownLatestVersion);
        var installed = _service.CurrentVersion?.Ver ?? "";

        VersionCombo.Items.Clear();
        int selectIdx = -1, recommendedIdx = -1;
        for (int i = 0; i < releases.Count; i++)
        {
            var r = releases[i];
            var tags = new List<string>();
            if (!string.IsNullOrEmpty(installed)
                && string.Equals(r.Tag, installed, StringComparison.OrdinalIgnoreCase))
            {
                tags.Add(Strings.Get("ModPropVersionInstalled"));
                selectIdx = i;
            }
            if (!string.IsNullOrEmpty(recommended)
                && string.Equals(r.Tag, recommended, StringComparison.OrdinalIgnoreCase))
            {
                tags.Add(Strings.Get("ModPropVersionRecommended"));
                recommendedIdx = i;
            }
            if (r.Prerelease) tags.Add(Strings.Get("ModPropVersionPrerelease"));

            var label = tags.Count > 0 ? $"{r.Tag}  —  {string.Join(", ", tags)}" : r.Tag;
            VersionCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = label, Tag = r.Tag });
        }

        VersionCombo.SelectedIndex = selectIdx >= 0 ? selectIdx : (recommendedIdx >= 0 ? recommendedIdx : 0);
        VersionCombo.IsEnabled = true;
        InstallVersionBtn.IsEnabled = true;
        VersionStatus.Visibility = Visibility.Collapsed;
    }

    private void SetVersionStatus(string text, string brushKey)
    {
        VersionStatus.Text = text;
        VersionStatus.Foreground =
            TryFindResource(brushKey) as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.White;
        VersionStatus.Visibility = Visibility.Visible;
    }

    private void InstallVersionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_installVersion == null) return;
        if (VersionCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        if (item.Tag is not string tag || string.IsNullOrWhiteSpace(tag)) return;

        var installed = _service.CurrentVersion?.Ver ?? "";
        if (string.Equals(tag, installed, StringComparison.OrdinalIgnoreCase))
        {
            SetVersionStatus(Strings.Get("ModPropVersionAlready"), "TextSecondary");
            return;
        }

        // Install runs on the MAIN window's progress strip (like Verify / Repair),
        // so close this non-modal dialog first — otherwise it sits over the bar.
        var run = _installVersion;
        Close();
        _ = run(tag);
    }

    private void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var path = _service.InstallPath;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"ModPropertiesDialog.OpenFolderBtn: open failed: {ex.Message}");
        }
    }

    private void OpenAoE3FolderBtn_Click(object sender, RoutedEventArgs e)
    {
        // Just opens Explorer — no covering window, so keep the dialog open.
        _openAoE3Folder?.Invoke();
    }

    private void ChangeModFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        // Stays open: the folder picker is a modal that lands on top. The
        // new path is written to config before the callback's await, so the
        // immediate RefreshData() shows it; the re-detected version catches
        // up via MainWindow's post-CheckAsync RefreshData call.
        _changeModFolder?.Invoke();
        RefreshData();
    }

    private void ChangeAoE3FolderBtn_Click(object sender, RoutedEventArgs e)
    {
        _changeAoE3Folder?.Invoke();
        RefreshData();
    }

    private void VerifyBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _openVerify?.Invoke();
    }

    private void RepairBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _openRepair?.Invoke();
    }

    private void InstallNewCopyBtn_Click(object sender, RoutedEventArgs e)
    {
        // Closes like Repair — the install progress runs on the main window,
        // which a non-modal Properties window would otherwise cover.
        Close();
        _installAnotherCopy?.Invoke();
    }

    private void AddExistingFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        // Adopts an existing install folder as a copy (no reinstall). Stays open and
        // refreshes the list in place so the new copy appears immediately.
        if (_addExistingFolder?.Invoke() == true)
            LoadManageInstalls();
    }

    private void SearchInstallBtn_Click(object sender, RoutedEventArgs e)
    {
        // The broad search + adopt runs on the main window (it shows a wait
        // cursor and re-checks there), so close like Verify/Repair to uncover it.
        Close();
        _searchInstall?.Invoke();
    }

    private void ViewLogsBtn_Click(object sender, RoutedEventArgs e)
    {
        // Opens the log in the external viewer — no covering window, keep open.
        _viewLogs?.Invoke();
    }

    private void ShareDiagnosticsBtn_Click(object sender, RoutedEventArgs e)
    {
        // Bundles the diagnostic files to the Desktop and reveals them in Explorer
        // — no covering window, so keep the dialog open.
        _shareDiagnostics?.Invoke();
    }

    private void UninstallBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _uninstall?.Invoke();
    }

    private void OpenUserDataFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        // Just opens Explorer — no covering window, so keep the dialog open.
        _openUserDataFolder?.Invoke();
    }

    /// <summary>
    /// Copy the chosen mod's graphics / sound / hotkeys into this one, once, now.
    /// Deliberately separate from the sharing checkbox: this is a deliberate act with a visible
    /// result, and it works whether or not either mod is in the sharing group.
    /// </summary>
    private void ImportSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsSourceCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        if (item.Tag is not string sourceId || string.IsNullOrWhiteSpace(sourceId)) return;

        var source = ModRegistry.Find(sourceId);
        if (source == null) return;

        // The result says WHICH way it went, and the difference is not cosmetic: a mod that has
        // simply never been opened used to be told "the settings couldn't be read", which is
        // false and names nothing to do about it. Age of Empires III writes the profile on its
        // first run, so the honest answer is "open it once".
        var result = Services.GameSettingsStore.ImportFrom(source, _profile, _config);
        var ok = result == Services.GameSettingsStore.SettingsImportResult.Imported;
        ShowUserDataResult(result switch
        {
            Services.GameSettingsStore.SettingsImportResult.Imported =>
                Strings.Format("ModPropSettingsImported", source.DisplayName),
            Services.GameSettingsStore.SettingsImportResult.NoTargetProfile =>
                Strings.Get("ModPropSettingsNeverOpened"),
            Services.GameSettingsStore.SettingsImportResult.SourceUnavailable =>
                Strings.Format("ModPropSettingsNoSourceSettings", source.DisplayName),
            _ => Strings.Get("ModPropSettingsImportFailed"),
        });
        if (!ok) UserDataResultHint.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
    }

    /// <summary>
    /// Join or leave the group of mods that share one set of settings. Same three-step idiom as
    /// the stay-on-version pin: mutate the live <see cref="ModState"/>, save, done — there is
    /// nothing for the main window to repaint here.
    /// </summary>
    private void SyncSettingsCheck_Click(object sender, RoutedEventArgs e)
    {
        _config.GetState(_profile.Id).SyncGameSettings = SyncSettingsCheck.IsChecked == true;
        _config.Save();
    }

    private void CreateBackupBtn_Click(object sender, RoutedEventArgs e)
    {
        // Stays open: the backup confirmation/MessageBox is modal and lands
        // on top. The callback is synchronous and returns the localized
        // result line (null = cancelled) for the inline hint; RefreshData()
        // afterwards sees the final user-data state (path/backup count).
        ShowUserDataResult(_createBackup?.Invoke());
        RefreshData();
    }

    private void RestoreBackupBtn_Click(object sender, RoutedEventArgs e)
    {
        ShowUserDataResult(_restoreBackup?.Invoke());
        RefreshData();
    }

    // -- Addons -------------------------------------------------------------
    //
    // Optional community overlays (transparent UI, gun-smoke effects, …). The
    // engine lives in AddonService / AddonRisk; this is only the list.
    //
    // Everything here goes through AddonService, never a plain file copy, so the
    // three invariants it enforces hold no matter which button was pressed: an
    // addon may not write the files version detection and the multiplayer
    // fingerprint read, the originals are backed up so it can be reverted, and
    // the manifest is re-captured so "Verify files" doesn't report the install
    // as corrupt afterwards.

    /// <summary>
    /// Rebuilds the addon list. Hidden entirely for the stock game — the
    /// launcher never modifies the user's own copy of Age of Empires III.
    /// </summary>
    private void LoadAddons()
    {
        // Shown for the stock game too. These are Age of Empires III addons, so
        // they work on any install, and the launcher's usual "never touch the
        // player's own copy" rule doesn't apply: an addon is reversible by design
        // (originals are backed up and restored on disable), unlike the install,
        // update and uninstall paths that stay refused for the stock profile.
        TabAddonsBtn.Visibility = Visibility.Visible;

        // The statistics come from the mod's own My Games folder, and the stock game deliberately
        // has none the launcher will claim (UserDataService.ResolveFolderName returns "" for it,
        // so its vanilla folder is never adopted). Nothing to read means nothing to offer.
        TabStatsBtn.Visibility = _profile.IsStockGame ? Visibility.Collapsed : Visibility.Visible;
        TabDecksBtn.Visibility = _profile.IsStockGame ? Visibility.Collapsed : Visibility.Visible;

        AddonCardList.Children.Clear();
        ImportedAddonList.Children.Clear();
        AddonsResultText.Visibility = Visibility.Collapsed;
        ImportAddonBtn.IsEnabled = !_modBusy && !string.IsNullOrEmpty(_service.InstallPath);

        // Re-read on every render: a download that just landed makes an archive
        // readable that was not there when the tab was opened, and its card should
        // then show the figures rather than keep the empty ones it was drawn with.
        _addonFacts.Clear();

        var enabled = new HashSet<string>(
            _config.GetActiveState().EnabledAddons ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        // The two sources are separated because they are not the same promise. A
        // catalog addon is checked against a pinned SHA-256 before a byte is written
        // and belongs to THIS install; an imported archive is whatever the user
        // pointed at, is copied into the launcher's own folder, and is offered to
        // every mod. Stacking them made those look like one kind of thing.
        foreach (var entry in AddonRegistry.All)
            AddonCardList.Children.Add(BuildOfferedAddonCard(entry, enabled.Contains(entry.Id)));

        var imported = _config.ImportedAddons ?? new List<ImportedAddon>();
        foreach (var addon in imported)
            ImportedAddonList.Children.Add(BuildAddonCard(addon, enabled.Contains(addon.Id)));

        // The empty hint belongs to the IMPORTED group alone: the catalog group is
        // never empty, so "no addons yet" under a list of three was simply wrong.
        AddonsEmptyHint.Visibility = imported.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (imported.Count == 0) AddonsEmptyHint.Text = Strings.Get("AddonsEmptyHint");
    }

    /// <summary>
    /// Risk verdicts, computed once per open and keyed by addon id.
    ///
    /// <para>The verdict comes from the ARCHIVE, never from what the registry declares:
    /// the registry records what was true when it was written and the file is what will
    /// actually be extracted. That means an addon nobody has downloaded yet has no
    /// verdict at all, and its card correctly shows no badge and no counts rather than
    /// a guess.</para>
    /// </summary>
    private readonly Dictionary<string, AddonFacts> _addonFacts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What one archive turned out to contain. All of it read, none of it estimated.</summary>
    private sealed record AddonFacts(
        AddonRiskLevel Level,
        int FileCount,
        int XmbCount,
        int DataCount,
        IReadOnlyList<string> RiskFiles,
        IReadOnlyList<string> ExecutableFiles);

    /// <summary>
    /// Reads an addon's archive once and remembers what was in it.
    ///
    /// <para>Returns null when the archive is not on disk (nobody has downloaded this
    /// one yet) or cannot be read. NSIS entries return null on purpose too: their
    /// archive holds the installer, not the files it will produce, so counting its
    /// entries would report a figure about the wrong thing.</para>
    /// </summary>
    private AddonFacts? FactsFor(string id, bool isInstaller)
    {
        if (isInstaller) return null;
        if (_addonFacts.TryGetValue(id, out var cached)) return cached;

        var zip = AddonStore.PathFor(id);
        if (string.IsNullOrEmpty(zip) || !File.Exists(zip)) return null;

        try
        {
            var entries = AddonService.ReadArchiveEntries(zip);
            var risk = AddonRisk.Assess(entries);
            var facts = new AddonFacts(
                risk.Level,
                entries.Count,
                risk.VersionMatchFiles.Count,
                risk.SimulationFiles.Count,
                risk.BlockingFiles
                    .Concat(risk.SimulationFiles)
                    .Concat(risk.VersionMatchFiles)
                    .ToList(),
                risk.ExecutableFiles);
            _addonFacts[id] = facts;
            return facts;
        }
        catch (Exception ex)
        {
            // A damaged archive is not a reason to fail the whole tab: the card just
            // loses its numbers, and applying it will report the real error.
            DiagnosticLog.Write($"Addon facts unavailable for {id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>One state chip. Same 9/600 shape as every other badge in the launcher.</summary>
    private Border AddonBadge(string text, string bgKey, string fgKey) => new()
    {
        Background = (Brush)FindResource(bgKey),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(6, 2, 6, 2),
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = (double)FindResource("SetBadgeSize"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource(fgKey),
        },
    };

    /// <summary>
    /// A notice box: the consequence, in the colour that consequence has. Amber for
    /// "this can break a match", red for "the launcher will not do this", neutral for
    /// "here is how this one is delivered".
    /// </summary>
    private Border AddonNotice(string text, string kind)
    {
        var (bg, rim, fg, glyph) = kind switch
        {
            "danger" => ("UiDangerBadgeBg", "UiRimDanger", "MpDestructiveText", ""),
            "warn"   => ("MpCautionBg", "MpCautionRim", "MpCautionText", ""),
            _        => ("MpRowHighlight", "MpRimSoft", "MpTextMuted", ""),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = (double)FindResource("SetDescSize"),
            Foreground = (Brush)FindResource(fg),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 8, 0),
        };
        var body = new TextBlock
        {
            Text = text,
            FontSize = (double)FindResource("SetDescSize"),
            Foreground = (Brush)FindResource(fg),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(icon);
        grid.Children.Add(body);
        return new Border
        {
            Background = (Brush)FindResource(bg),
            BorderBrush = (Brush)FindResource(rim),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 10, 0, 0),
            Child = grid,
        };
    }

    /// <summary>The mono line under the description: what this addon writes, in figures.</summary>
    private TextBlock? AddonMetaLine(AddonFacts? facts)
    {
        if (facts is null || facts.FileCount == 0) return null;
        var parts = new List<string> { Strings.Format("AddonFileCount", facts.FileCount) };
        if (facts.XmbCount > 0) parts.Add(Strings.Format("AddonXmbCount", facts.XmbCount));
        if (facts.DataCount > 0) parts.Add(Strings.Format("AddonDataCount", facts.DataCount));
        return new TextBlock
        {
            Text = string.Join("  ·  ", parts),
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = (double)FindResource("SetMonoSize"),
            Foreground = (Brush)FindResource("UiTextFaint"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0),
        };
    }

    /// <summary>
    /// The shared card body. Both sources draw the same shape — a name with its state
    /// badges, one line of description, the file figures, the notices, and the actions
    /// in a column on the right — because the difference between them is where the
    /// archive came from, not what the player needs to know about it.
    /// </summary>
    private Border BuildAddonCardShell(
        string title, string? description, AddonFacts? facts, bool isEnabled,
        bool isInstaller, IEnumerable<UIElement> badges, IEnumerable<UIElement> actions,
        IEnumerable<Border> notices)
    {
        var left = new StackPanel();

        var titleRow = new WrapPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = (Brush)FindResource("MpTextHeading"),
            FontSize = (double)FindResource("SetBodySize"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        foreach (var b in badges) titleRow.Children.Add(b);
        left.Children.Add(titleRow);

        if (!string.IsNullOrWhiteSpace(description))
            left.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = (Brush)FindResource("MpTextMuted"),
                FontSize = (double)FindResource("SetDescSize"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });

        var meta = AddonMetaLine(facts);
        if (meta is not null) left.Children.Add(meta);

        foreach (var n in notices) left.Children.Add(n);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(14, 0, 0, 0),
        };
        foreach (var a in actions) right.Children.Add(a);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        return new Border
        {
            Background = (Brush)FindResource("MpPanel"),
            // An enabled addon is marked by its ACTIVE badge, not by a border that
            // grows from 1 to 2 and shifts everything inside the card by a pixel.
            BorderBrush = (Brush)FindResource(isEnabled ? "MpActionRimSoft" : "MpRimSoft"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

    /// <summary>The "Page ↗" link. A destination, so it reads as a link and not a button.</summary>
    private Button AddonPageLink(string url)
    {
        var btn = new Button
        {
            Content = Strings.Get("AddonSourcePage") + "  ↗",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("MpActionText"),
            FontSize = (double)FindResource("SetDescSize"),
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = TooltipHelper.Wrap(url),
        };
        btn.Click += (_, _) => SafeUrl.TryOpen(url);
        return btn;
    }

    /// <summary>
    /// A card for an addon the launcher knows how to fetch. The whole point of
    /// the feature: one button that downloads and applies, instead of the player
    /// finding the file, downloading it and importing it.
    /// </summary>
    private Border BuildOfferedAddonCard(AddonEntry entry, bool isEnabled)
    {
        bool isInstaller = entry.Packaging == AddonPackaging.NsisInstaller;
        var facts = FactsFor(entry.Id, isInstaller);
        bool blocked = facts?.Level == AddonRiskLevel.Blocked;
        bool risky = facts?.Level == AddonRiskLevel.MultiplayerRisk;
        bool actionable = !_modBusy && !string.IsNullOrEmpty(_service.InstallPath);

        var badges = new List<UIElement>();
        if (isEnabled)
            badges.Add(AddonBadge(Strings.Get("AddonBadgeActive"), "MpChipOkBg", "MpOkText"));
        if (blocked)
            badges.Add(AddonBadge(Strings.Get("AddonBadgeBlocked"), "UiDangerBadgeBg", "MpDestructiveText"));
        else if (risky)
            badges.Add(AddonBadge(Strings.Get("AddonBadgeMultiplayerRisk"), "MpCautionBg", "MpCautionText"));
        else if (facts is not null)
            badges.Add(AddonBadge(Strings.Get("AddonBadgeCosmetic"), "MpActionSoftBg", "MpActionText"));
        // Declared by the registry, so it is known before anything is downloaded —
        // and it is the one thing about this addon that will interrupt the player.
        if (isInstaller)
            badges.Add(AddonBadge(Strings.Get("AddonBadgeInstaller"), "MpPrivateBg", "MpPrivateText"));

        var notices = new List<Border>();
        if (blocked)
            notices.Add(AddonNotice(
                Strings.Format("AddonRiskBlockedHint", string.Join(", ", facts!.RiskFiles.Take(3))),
                "danger"));
        else if (risky)
            notices.Add(AddonNotice(
                Strings.Format("AddonRiskSimulationHint", string.Join(", ", facts!.RiskFiles.Take(3))),
                "warn"));
        if (isInstaller)
            notices.Add(AddonNotice(Strings.Get("AddonInstallerNote"), "info"));
        // Named, never counted: "1 file skipped" is useless when the addon then does
        // not work, while naming Building Rotator.exe says exactly what was left out.
        if (facts is { ExecutableFiles.Count: > 0 })
            notices.Add(AddonNotice(
                Strings.Format("AddonAppliedSkipped", string.Join(", ", facts.ExecutableFiles.Take(3))),
                "info"));

        var actions = new List<UIElement> { AddonPageLink(entry.SourceUrl) };
        if (!blocked)
        {
            if (isEnabled)
            {
                var off = new Button
                {
                    Content = Strings.Get("AddonDisable"),
                    Style = (Style)FindResource("SetActionButton"),
                    IsEnabled = actionable,
                };
                off.Click += async (_, _) => await DisableOfferedAddonAsync(entry);
                actions.Add(off);
            }
            else
            {
                var get = new Button
                {
                    // "Enable anyway" once the archive is here and known to be risky:
                    // the same click, named after the decision it is really asking for.
                    Content = risky
                        ? Strings.Get("AddonEnableAnyway")
                        : Strings.Get("AddonDownloadAndEnable"),
                    Style = (Style)FindResource("SetActionButton"),
                    Width = double.NaN,
                    MinWidth = 150,
                    Padding = new Thickness(14, 0, 14, 0),
                    IsEnabled = actionable,
                };
                get.Click += async (_, _) => await DownloadAndEnableAsync(entry, get);
                actions.Add(get);
            }
        }

        return BuildAddonCardShell(
            entry.Name, entry.DescriptionFor(Strings.Language), facts,
            isEnabled, isInstaller, badges, actions, notices);
    }

    /// <summary>
    /// A card for an archive the user imported. Its verdict was recorded at import
    /// time and re-read here from the archive when it is still in the store, so a
    /// launcher update that changes the risk rules is reflected without a re-import.
    /// </summary>
    private Border BuildAddonCard(ImportedAddon addon, bool isEnabled)
    {
        var facts = FactsFor(addon.Id, isInstaller: false);
        // The cached verdict is the fallback, not the source: it is what was true at
        // import time, and the archive is what will actually be extracted.
        var level = facts?.Level ?? ParseRisk(addon.Risk);
        var riskFiles = facts?.RiskFiles ?? (IReadOnlyList<string>)addon.RiskFiles;
        bool blocked = level == AddonRiskLevel.Blocked;
        bool risky = level == AddonRiskLevel.MultiplayerRisk;

        var badges = new List<UIElement>();
        if (isEnabled)
            badges.Add(AddonBadge(Strings.Get("AddonBadgeActive"), "MpChipOkBg", "MpOkText"));
        if (blocked)
            badges.Add(AddonBadge(Strings.Get("AddonBadgeBlocked"), "UiDangerBadgeBg", "MpDestructiveText"));
        else if (risky)
            badges.Add(AddonBadge(Strings.Get("AddonBadgeMultiplayerRisk"), "MpCautionBg", "MpCautionText"));
        else
            badges.Add(AddonBadge(Strings.Get("AddonBadgeCosmetic"), "MpActionSoftBg", "MpActionText"));

        var notices = new List<Border>();
        if (blocked)
            notices.Add(AddonNotice(
                Strings.Format("AddonRiskBlockedHint", string.Join(", ", riskFiles.Take(3))), "danger"));
        else if (risky)
            notices.Add(AddonNotice(
                Strings.Format("AddonRiskSimulationHint", string.Join(", ", riskFiles.Take(3))), "warn"));

        var actions = new List<UIElement>();
        if (!blocked)
        {
            var toggle = new System.Windows.Controls.Primitives.ToggleButton
            {
                Style = (Style)FindResource("SetToggle"),
                IsChecked = isEnabled,
                IsEnabled = !_modBusy && !string.IsNullOrEmpty(_service.InstallPath),
                VerticalAlignment = VerticalAlignment.Center,
            };
            toggle.Checked += async (_, _) => await ToggleAddonAsync(addon, true);
            toggle.Unchecked += async (_, _) => await ToggleAddonAsync(addon, false);
            actions.Add(toggle);
        }

        // The file name is the only thing that tells the user WHICH download this was,
        // so when the name is the file name there is nothing extra to say.
        string title = string.IsNullOrWhiteSpace(addon.Name) ? addon.FileName : addon.Name;
        string? sub = string.Equals(title, addon.FileName, StringComparison.OrdinalIgnoreCase)
            ? null
            : addon.FileName;

        return BuildAddonCardShell(
            title, sub, facts, isEnabled, isInstaller: false,
            badges, actions, notices);
    }
    /// <summary>
    /// Download → risk check → apply, in one click.
    ///
    /// The risk verdict is computed from the DOWNLOADED archive, never from what
    /// the registry declares: the registry is a copy of what was true when it was
    /// written, and the file is what will actually be extracted.
    /// </summary>
    private async Task DownloadAndEnableAsync(AddonEntry entry, Button trigger)
    {
        var install = _service.InstallPath;
        if (string.IsNullOrEmpty(install)) return;

        // A retail or GOG Age of Empires III under Program Files isn't writable
        // without elevation, and the raw access-denied error explains nothing.
        if (!ElevationService.CanWriteTo(install))
        {
            ShowAddonResult(Strings.Get("AddonNeedsAdmin"), ok: false);
            return;
        }

        trigger.IsEnabled = false;
        ShowAddonResult(Strings.Format("AddonDownloading", entry.Name), ok: true);

        try
        {
            var zip = AddonStore.PathFor(entry.Id);
            if (!File.Exists(zip))
                await HeavenDownloader.DownloadAsync(
                    entry.HeavenFileId, zip, confirmSpace: ConfirmAddonSpaceOk);

            // An NSIS addon has to be unpacked before anything about it is known —
            // its archive holds only the installer, so the risk verdict comes from
            // what the installer produces, not from the download.
            string? unpackedDir = null;
            if (entry.Packaging == AddonPackaging.NsisInstaller)
            {
                if (!ConfirmRunInstaller(entry)) return;

                ShowAddonResult(Strings.Format("AddonUnpacking", entry.Name), ok: true);
                unpackedDir = await UnpackInstallerAsync(entry, zip);
                if (unpackedDir == null) return;
            }

            var entries = unpackedDir != null
                ? await Task.Run(() => AddonService.ListFolderEntries(unpackedDir))
                : await Task.Run(() => AddonService.ReadArchiveEntries(zip));
            var risk = AddonRisk.Assess(entries);

            if (risk.Level == AddonRiskLevel.Blocked)
            {
                ShowAddonResult(
                    Strings.Format("AddonRiskBlockedHint", string.Join(", ", risk.BlockingFiles.Take(3))),
                    ok: false);
                return;
            }

            if (risk.Level == AddonRiskLevel.MultiplayerRisk && !ConfirmMultiplayerRisk(risk))
                return;

            var result = unpackedDir != null
                ? await AddonService.ApplyFromFolderAsync(
                    install, entry.Id, unpackedDir, _profile,
                    allowMultiplayerRisk: true, includeOnly: entry.IncludeOnly)
                : await AddonService.ApplyAsync(
                    install, entry.Id, zip, _profile,
                    allowMultiplayerRisk: true, includeOnly: entry.IncludeOnly);

            if (result.Status != AddonApplyStatus.Applied)
            {
                ShowAddonResult(DescribeFailure(result), ok: false);
                return;
            }

            var state = _config.GetActiveState();
            state.EnabledAddons ??= new List<string>();
            if (!state.EnabledAddons.Contains(entry.Id, StringComparer.OrdinalIgnoreCase))
                state.EnabledAddons.Add(entry.Id);
            _config.Save();

            // Name what was left out. "1 file skipped" is useless when the addon
            // then doesn't behave as its page describes.
            ShowAddonResult(
                result.SkippedFiles.Count > 0
                    ? Strings.Format("AddonAppliedSkipped", string.Join(", ", result.SkippedFiles.Take(3)))
                    : Strings.Get("AddonApplied"),
                ok: true);
        }
        catch (HeavenDownloadException ex)
        {
            DiagnosticLog.Write($"Addon '{entry.Id}' download failed: {ex.Message}");
            ShowAddonResult(Strings.Get("AddonDownloadFailed"), ok: false);
        }
        catch (OperationCanceledException)
        {
            // The user declined the low-space warning. Their own choice, so it reports as a
            // neutral cancellation — the generic handler below would paint it red as a failure.
            DiagnosticLog.Write($"Addon '{entry.Id}' cancelled by the user.");
            ShowAddonResult(Strings.Get("AddonCancelled"), ok: true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Addon '{entry.Id}' failed: {ex.Message}");
            ShowAddonResult(Strings.Get("AddonFailed"), ok: false);
        }
        finally
        {
            LoadAddons();
        }
    }

    /// <summary>
    /// Warn before an addon download when either volume is too tight. The archive is written
    /// under <see cref="AppPaths.DataDir"/> (and unpacked beside itself for an NSIS addon), while
    /// the install receives the extracted files plus a backup of everything they overwrite — so
    /// both sides are charged twice the archive.
    /// </summary>
    private bool ConfirmAddonSpaceOk(long archiveBytes)
    {
        if (archiveBytes <= 0) return true;   // Heaven sent no Content-Length — don't guess

        var required = archiveBytes * DiskSpaceService.AddonFactor;
        var shortfall = DiskSpaceService.Check(
            AddonStore.RootDir, required, _service.InstallPath, required);
        return DiskSpacePrompt.ConfirmOrCancel(this, shortfall, "DiskSpaceConfirmDownloadBody");
    }

    private async Task DisableOfferedAddonAsync(AddonEntry entry)
    {
        var install = _service.InstallPath;
        if (string.IsNullOrEmpty(install)) return;

        try
        {
            await AddonService.DisableAsync(install, entry.Id, _profile);
            var state = _config.GetActiveState();
            state.EnabledAddons?.RemoveAll(id =>
                string.Equals(id, entry.Id, StringComparison.OrdinalIgnoreCase));
            _config.Save();
            ShowAddonResult(Strings.Get("AddonDisabled"), ok: true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Addon '{entry.Id}' failed to disable: {ex.Message}");
            ShowAddonResult(Strings.Get("AddonFailed"), ok: false);
        }

        LoadAddons();
    }

    /// <summary>
    /// Names the concrete danger rather than warning in the abstract — the two
    /// causes have different symptoms and the player can only weigh the one that
    /// applies.
    /// </summary>
    /// <summary>
    /// Unpacks an NSIS addon into a scratch folder and returns it, or null when
    /// the installer refused to run silently.
    /// </summary>
    private async Task<string?> UnpackInstallerAsync(AddonEntry entry, string zipPath)
    {
        try
        {
            var work = Path.Combine(AddonStore.RootDir, "unpacked", entry.Id);
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(work);

            // The download is a zip whose single entry is the installer.
            var stage = Path.Combine(work, "_installer");
            Directory.CreateDirectory(stage);
            await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, stage, true));

            var installer = Directory
                .EnumerateFiles(stage, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (installer == null)
            {
                ShowAddonResult(Strings.Get("AddonInstallerMissing"), ok: false);
                return null;
            }

            var outDir = Path.Combine(work, "files");
            await NsisExtractor.ExtractAsync(installer, outDir);
            return outDir;
        }
        catch (NsisExtractionException ex)
        {
            DiagnosticLog.Write($"Addon '{entry.Id}': unpack failed — {ex.Message}");
            ShowAddonResult(
                Strings.Get(ex.DeclinedByUser ? "AddonRunCancelled" : "AddonUnpackFailed"),
                ok: false);
            return null;
        }
    }

    /// <summary>
    /// Running a third-party binary is a line this launcher doesn't otherwise
    /// cross, so it is never implicit. The text says what will run and — the part
    /// that matters — that it runs into a temporary folder rather than the game.
    /// </summary>
    private bool ConfirmRunInstaller(AddonEntry entry) =>
        MessageBox.Show(this,
            Strings.Format("AddonRunInstallerBody", entry.Name),
            Strings.Get("AddonRunInstallerTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private bool ConfirmMultiplayerRisk(AddonRiskAssessment risk)
    {
        var files = risk.VersionMatchFiles.Count > 0 ? risk.VersionMatchFiles : risk.SimulationFiles;
        var body = risk.VersionMatchFiles.Count > 0
            ? Strings.Format("AddonVersionMatchConfirmBody", risk.VersionMatchFiles.Count)
            : Strings.Format("AddonSimulationConfirmBody", string.Join(", ", files.Take(3)));

        return MessageBox.Show(this, body,
            Strings.Get("AddonMultiplayerConfirmTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private static AddonRiskLevel ParseRisk(string? raw) =>
        Enum.TryParse<AddonRiskLevel>(raw, ignoreCase: true, out var v) ? v : AddonRiskLevel.Cosmetic;

    private async Task ToggleAddonAsync(ImportedAddon addon, bool enable)
    {
        var install = _service.InstallPath;
        if (string.IsNullOrEmpty(install)) return;

        var state = _config.GetActiveState();
        state.EnabledAddons ??= new List<string>();

        try
        {
            if (enable)
            {
                var zip = await AddonStore.ResolveAsync(addon.Id);
                if (zip == null)
                {
                    ShowAddonResult(Strings.Get("AddonArchiveMissing"), ok: false);
                    LoadAddons();
                    return;
                }

                // A simulation-risk addon passes the lobby check and can still
                // desync a match, so it needs an explicit yes — the launcher
                // cannot detect the problem later.
                bool allowRisk = ParseRisk(addon.Risk) != AddonRiskLevel.MultiplayerRisk;
                if (!allowRisk)
                {
                    allowRisk = MessageBox.Show(this,
                        Strings.Get("AddonSimulationConfirmBody"),
                        Strings.Get("AddonSimulationConfirmTitle"),
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                }
                if (!allowRisk) { LoadAddons(); return; }

                var result = await AddonService.ApplyAsync(
                    install, addon.Id, zip, _profile, allowMultiplayerRisk: true);

                if (result.Status != AddonApplyStatus.Applied)
                {
                    ShowAddonResult(DescribeFailure(result), ok: false);
                    LoadAddons();
                    return;
                }

                if (!state.EnabledAddons.Contains(addon.Id, StringComparer.OrdinalIgnoreCase))
                    state.EnabledAddons.Add(addon.Id);
                ShowAddonResult(Strings.Get("AddonApplied"), ok: true);
            }
            else
            {
                await AddonService.DisableAsync(install, addon.Id, _profile);
                state.EnabledAddons.RemoveAll(id =>
                    string.Equals(id, addon.Id, StringComparison.OrdinalIgnoreCase));
                ShowAddonResult(Strings.Get("AddonDisabled"), ok: true);
            }

            _config.Save();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Addon toggle failed for '{addon.Id}': {ex.Message}");
            ShowAddonResult(Strings.Get("AddonFailed"), ok: false);
        }

        LoadAddons();
    }

    private string DescribeFailure(AddonApplyResult result) => result.Status switch
    {
        AddonApplyStatus.Blocked => Strings.Format(
            "AddonRiskBlockedHint", string.Join(", ", result.OffendingFiles.Take(3))),
        AddonApplyStatus.Empty => Strings.Get("AddonArchiveEmpty"),
        AddonApplyStatus.Conflict => Strings.Format(
            "AddonConflict", result.ConflictingAddonId ?? "?"),
        _ => Strings.Get("AddonFailed"),
    };

    private void ShowAddonResult(string text, bool ok)
    {
        AddonsResultText.Text = text;
        AddonsResultText.Foreground = ok
            ? (Brush)FindResource("MpStatusOnline")
            : (Brush)FindResource("MpStatusOffline");
        AddonsResultText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Imports an addon archive the user downloaded themselves.
    ///
    /// This is not a convenience path, it is the only one that works today: the
    /// community pages these addons come from hand out session-bound download
    /// links, verified to return the site's generic listing page to any client
    /// other than the browser that requested them. So the launcher cannot fetch
    /// them, and the alternatives are a re-hosted catalog copy (which needs the
    /// author's permission) or a file the user already has.
    /// </summary>
    private async void ImportAddonBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Strings.Get("AddonImportFilter"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            // Classify BEFORE storing anything, so a refused archive leaves no
            // trace and the reason can name the files that caused it.
            var entries = await Task.Run(() => AddonService.ReadArchiveEntries(dlg.FileName));
            var risk = AddonRisk.Assess(entries);

            var id = await AddonStore.ImportAsync(dlg.FileName);
            _config.ImportedAddons ??= new List<ImportedAddon>();
            _config.ImportedAddons.RemoveAll(a =>
                string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            _config.ImportedAddons.Add(new ImportedAddon
            {
                Id = id,
                Name = Path.GetFileNameWithoutExtension(dlg.FileName),
                FileName = Path.GetFileName(dlg.FileName),
                Risk = risk.Level.ToString(),
                // VersionMatchFiles belongs here too, and its absence was a real hole:
                // an addon that is MultiplayerRisk ONLY because of its .xmb entries
                // stored an EMPTY list, so the card that names the offending files had
                // nothing to name and warned in the abstract about exactly the case the
                // assessment separated out in order to be concrete about.
                RiskFiles = risk.BlockingFiles
                    .Concat(risk.SimulationFiles)
                    .Concat(risk.VersionMatchFiles)
                    .Take(5)
                    .ToList(),
            });
            _config.Save();

            ShowAddonResult(
                risk.Level == AddonRiskLevel.Blocked
                    ? Strings.Format("AddonRiskBlockedHint", string.Join(", ", risk.BlockingFiles.Take(3)))
                    : Strings.Get("AddonImported"),
                ok: risk.Level != AddonRiskLevel.Blocked);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Addon import failed: {ex.Message}");
            ShowAddonResult(Strings.Get("AddonImportFailed"), ok: false);
        }

        LoadAddons();
    }

    // ======================================================================
    // Statistics — games against the AI
    // ======================================================================

    /// <summary>Loaded at most once per open; the proto scan is the expensive part.</summary>
    private bool _statsLoaded;

    /// <summary>
    /// Fills the STATISTICS tab from the local store.
    ///
    /// <para>The store is the only copy of these numbers. AoE3 writes the end-of-match statistics
    /// into the AI's memory file and zeroes the totals of every game but the newest on the next
    /// rewrite, so the launcher harvests them at each game exit and keeps them here.</para>
    ///
    /// <para><b>The unit-name resolution runs off the UI thread</b> — it streams every proto file
    /// the mod ships, 12 MB in Wars of Liberty. The rest of the tab is drawn before it returns, so
    /// the internal names appear at once and are replaced when the real ones arrive.</para>
    /// </summary>
    private async System.Threading.Tasks.Task LoadStatsAsync()
    {
        if (_statsLoaded) return;
        _statsLoaded = true;

        // Games against people first: it is the group most players have something in, and it
        // reads a different place on disk entirely.
        await LoadHumanGamesAsync();
        await LoadAiGamesAsync();
    }

    private async System.Threading.Tasks.Task LoadAiGamesAsync()
    {
        AiGamesList.Children.Clear();

        var games = Services.AiGameStatsStore.Load()
            .Where(g => string.Equals(g.ModId, _profile.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (games.Count == 0)
        {
            StatsEmptyHint.Text = Strings.Get("ModPropStatsEmpty");
            StatsEmptyHint.Visibility = Visibility.Visible;
            return;
        }
        StatsEmptyHint.Visibility = Visibility.Collapsed;

        // Every proto any of these games used, resolved in one pass rather than one per card.
        var protoNames = games.SelectMany(g => g.Units.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var installPath = _service.InstallPath;
        var exe = _profile.GameExecutable;

        IReadOnlyDictionary<string, string> names;
        try
        {
            names = await System.Threading.Tasks.Task.Run(
                () => Services.ProtoNameResolver.Resolve(installPath, exe, protoNames));
        }
        catch (Exception ex)
        {
            // A mod whose proto files cannot be read still gets its statistics, under the
            // internal names — which identify the unit to anyone who mods.
            DiagnosticLog.Write($"ModProperties: unit names unavailable — {ex.Message}");
            names = new Dictionary<string, string>();
        }

        AiGamesList.Children.Clear();
        foreach (var game in games) AiGamesList.Children.Add(BuildAiGameCard(game, names));
    }

    /// <summary>
    /// One game against the AI, as a card.
    /// </summary>
    /// <remarks>
    /// <c>internal static</c> so <c>DialogXamlTests</c> can build the real thing rather than a
    /// hand-copied imitation: nothing else in the launcher constructs this, no compile step checks
    /// a resource looked up by name, and the STATISTICS tab is not a surface the startup smoke
    /// test ever opens. Static costs nothing — every brush it reads is app-wide.
    /// </remarks>
    internal static Border BuildAiGameCard(
        Models.AiGameRecord game, IReadOnlyDictionary<string, string> names)
    {
        var caption = (double)Application.Current.FindResource("FontSizeCaption");
        var stack = new StackPanel();

        // Result and length. Won is null on a block that did not carry the field, and then the
        // card says nothing about the outcome rather than guessing at one.
        var minutes = Math.Max(1, (int)Math.Round(game.DurationMs / 60000.0));
        var headline = new TextBlock
        {
            Foreground = (Brush)Application.Current.FindResource("MpTextPrimary"),
            FontSize = (double)Application.Current.FindResource("FontSizeBodyStrong"),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        if (game.Won.HasValue)
        {
            headline.Inlines.Add(new System.Windows.Documents.Run(
                Strings.Get(game.Won.Value ? "ModPropStatsWon" : "ModPropStatsLost"))
            {
                Foreground = (Brush)Application.Current.FindResource(game.Won.Value ? "MpOk" : "MpDestructiveText"),
            });
            headline.Inlines.Add(new System.Windows.Documents.Run("  ·  "));
        }
        headline.Inlines.Add(new System.Windows.Documents.Run(
            Strings.Format("ModPropStatsDuration", minutes)));

        // When it was played. Through the same helper the chat's day divider uses, so the two
        // cannot disagree about when a day stops being "yesterday" or when the year is worth
        // printing — and so the month names follow the launcher's language rather than the OS.
        if (DateTime.TryParse(game.CapturedAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var when))
        {
            var local = when.ToLocalTime();
            headline.Inlines.Add(new System.Windows.Documents.Run("  ·  "
                + Services.ChatTimeFormat.DateLabel(
                    local, DateTime.Today,
                    Strings.Get("MpChatToday"), Strings.Get("MpChatYesterday"),
                    System.Globalization.CultureInfo.GetCultureInfo(
                        Strings.Language == Strings.LangEs ? "es" : "en")))
            {
                Foreground = (Brush)Application.Current.FindResource("OnSecondaryContainer"),
                FontWeight = FontWeights.Normal,
            });
        }

        stack.Children.Add(headline);

        // The totals, and ONLY the ones that were recorded. Zero here does not mean "gathered
        // nothing" — every game but the newest in a personality file has its totals wiped, so a
        // game imported from before the launcher started harvesting has real unit counts and no
        // resources at all. Printing "0 shipments" for those would be a statement, and a false one.
        var facts = new List<string>();
        if (game.Shipments > 0) facts.Add(Strings.Format("ModPropStatsShipments", game.Shipments));
        if (game.Score > 0) facts.Add(Strings.Format("ModPropStatsScore", game.Score.ToString("N0")));
        var resources = (long)game.Gold + game.Wood + game.Food;
        if (resources > 0) facts.Add(Strings.Format("ModPropStatsResources", resources.ToString("N0")));
        if (game.Xp > 0) facts.Add(Strings.Format("ModPropStatsXp", game.Xp.ToString("N0")));

        if (facts.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", facts),
                Foreground = (Brush)Application.Current.FindResource("OnSecondaryContainer"),
                FontSize = caption,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // The units, biggest first. This is the part that is filled in for EVERY stored game.
        var top = game.Units
            .OrderByDescending(u => u.Value)
            .ThenBy(u => u.Key, StringComparer.Ordinal)
            .Take(TopUnitsPerCard)
            .Select(u => Strings.Format(
                "ModPropStatsUnitCount",
                names.TryGetValue(u.Key, out var pretty) ? pretty : u.Key,
                u.Value))
            .ToList();

        if (top.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = string.Join("   ", top),
                Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
                FontSize = caption,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new Border
        {
            Child = stack,
            Padding = new Thickness(14, 11, 14, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Background = (Brush)Application.Current.FindResource("MpSurfaceAlt"),
            BorderBrush = (Brush)Application.Current.FindResource("MpRimSoft"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.FindResource("RadiusMd"),
        };
    }

    /// <summary>How many unit types a card lists before it stops being a card and becomes a table.</summary>
    private const int TopUnitsPerCard = 8;

    // ======================================================================
    // DECKS — what the player brings, with the game's own art
    // ======================================================================

    /// <summary>One card's tile. Big enough to recognise the art, small enough that 25 fit.</summary>
    private const int TileSize = 48;

    private bool _decksLoaded;
    private readonly List<Models.HomeCityProfile> _deckProfiles = new();
    private IReadOnlyDictionary<string, Services.CardDetail> _cardDetails =
        new Dictionary<string, Services.CardDetail>();
    private IReadOnlyDictionary<string, ImageSource> _cardIcons =
        new Dictionary<string, ImageSource>();
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _cardEffects =
        new Dictionary<string, IReadOnlyList<string>>();
    private readonly Dictionary<string, string> _deckCivNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tile the user clicked, so hovering elsewhere can come back to it.</summary>
    private Border? _selectedTile;
    private Models.HomeCityCard? _pinnedCard;

    /// <summary>
    /// Fills the DECKS section from the game's own home city files.
    ///
    /// <para><b>These are the decks the player BRINGS, not the cards played</b>, and the hint on
    /// screen says so. It has to: a deck holds 25 cards and a match may use five, so reading this
    /// as "cards played" overstates it by a factor nobody could see. What a recording carries about
    /// cards actually sent is nothing at all — measured, see the card section in
    /// <c>.claude/rules/multiplayer.md</c> — which is why this file is worth reading instead.</para>
    ///
    /// <para><b>Everything expensive happens in one background pass</b>: reading the home city
    /// files, streaming 12 MB of tech files for the names and descriptions, and indexing the five
    /// art archives for the pictures. Nothing is drawn until it returns, because a grid that
    /// appeared as empty squares and filled in later would look broken rather than busy.</para>
    /// </summary>
    private async System.Threading.Tasks.Task LoadDecksAsync()
    {
        if (_decksLoaded) return;
        _decksLoaded = true;

        var folderName = Services.UserDataService.ResolveFolderName(_profile, _config);
        var folder = string.IsNullOrWhiteSpace(folderName)
            ? ""
            : Services.UserDataService.GetUserDataFolder(folderName);

        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowDecksEmpty();
            return;
        }

        var installPath = _service.InstallPath;
        var exe = _profile.GameExecutable;

        List<Models.HomeCityProfile> profiles;
        IReadOnlyDictionary<string, Services.CardDetail> details;
        IReadOnlyDictionary<string, ImageSource> icons;
        IReadOnlyDictionary<string, IReadOnlyList<string>> effects;
        Dictionary<string, string> civs;

        try
        {
            (profiles, details, icons, effects, civs) = await System.Threading.Tasks.Task.Run(() =>
            {
                var read = Services.HomeCityDeckService.Read(folder).ToList();

                var names = read.SelectMany(p => p.Decks).SelectMany(d => d.Cards)
                    .Select(c => c.InternalName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var resolved = Services.CardNameResolver.ResolveDetails(installPath, exe, names);
                var art = Services.CardArtService.Load(
                    installPath, resolved.Values.Select(d => d.IconPath));

                // The lines with the percentages, rendered from the mod's own templates.
                var lines = Services.CardEffectRenderer.RenderAll(installPath, exe, resolved);

                // The internal civ name is frequently not the one the player saw — Struggle of
                // Indonesia files its Solo home city under "Ottomans" and shows "Surakarta" — so
                // printing it raw would name a civilization nobody has heard of.
                var civNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var civ in read.Select(p => p.Civ)
                             .Where(c => !string.IsNullOrWhiteSpace(c))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var display = Services.Multiplayer.CivNameResolver
                        .ResolveByInternalName(installPath, civ);
                    if (!string.IsNullOrWhiteSpace(display)) civNames[civ!] = display!;
                }

                return (read, resolved, art, lines, civNames);
            });
        }
        catch (Exception ex)
        {
            // A mod whose tech files or archives cannot be read still gets its decks, under the
            // internal names and without pictures — which identify the card to anyone who mods.
            DiagnosticLog.Write($"ModProperties: decks unavailable — {ex.Message}");
            ShowDecksEmpty();
            return;
        }

        _deckProfiles.Clear();
        _deckProfiles.AddRange(profiles);
        _cardDetails = details;
        _cardIcons = icons;
        _cardEffects = effects;
        _deckCivNames.Clear();
        foreach (var pair in civs) _deckCivNames[pair.Key] = pair.Value;

        if (_deckProfiles.Sum(p => p.Decks.Count) == 0)
        {
            ShowDecksEmpty();
            return;
        }

        DecksEmptyHint.Visibility = Visibility.Collapsed;
        BuildDeckPicker();
    }

    private void ShowDecksEmpty()
    {
        DecksEmptyHint.Text = Strings.Get("ModPropDecksEmpty");
        DecksEmptyHint.Visibility = Visibility.Visible;
        DeckPickerRow.Children.Clear();
        DeckGridCard.Visibility = Visibility.Collapsed;
        DeckDetailCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// One pill per deck. Hidden when there is only one, because a chooser with a single
    /// choice is furniture.
    /// </summary>
    private void BuildDeckPicker()
    {
        DeckPickerRow.Children.Clear();

        var first = true;
        foreach (var profile in _deckProfiles)
        {
            foreach (var deck in profile.Decks)
            {
                var thisProfile = profile;
                var thisDeck = deck;

                var pill = new RadioButton
                {
                    Style = (Style)FindResource("SetSegmentItem"),
                    GroupName = "DeckPicker",
                    Content = DeckPillLabel(profile, deck),
                    Margin = new Thickness(0, 0, 6, 6),
                    IsChecked = first,
                };
                pill.Checked += (_, _) => ShowDeck(thisProfile, thisDeck);
                DeckPickerRow.Children.Add(pill);

                // Setting IsChecked before the handler is attached is deliberate — it must not
                // fire during construction — so the first deck is shown by hand.
                if (first)
                {
                    ShowDeck(thisProfile, thisDeck);
                    first = false;
                }
            }
        }

        DeckPickerRow.Visibility =
            DeckPickerRow.Children.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string DeckPillLabel(Models.HomeCityProfile profile, Models.HomeCityDeckEntry deck)
    {
        var civ = CivDisplay(profile);
        return string.IsNullOrWhiteSpace(deck.Name) ? civ : civ + "  ·  " + deck.Name;
    }

    /// <summary>What the mod calls this civilization, falling back to what the file calls it.</summary>
    private string CivDisplay(Models.HomeCityProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Civ)
            && _deckCivNames.TryGetValue(profile.Civ, out var display))
        {
            return display;
        }
        return string.IsNullOrWhiteSpace(profile.Civ) ? profile.CityName : profile.Civ;
    }

    private void ShowDeck(Models.HomeCityProfile profile, Models.HomeCityDeckEntry deck)
    {
        DeckGridCard.Visibility = Visibility.Visible;
        DeckHeadline.Text = DeckPillLabel(profile, deck);

        var facts = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.CityName) && !string.IsNullOrWhiteSpace(profile.Civ))
            facts.Add(profile.CityName);
        facts.Add(Strings.Format("ModPropDecksCardCount", deck.Cards.Count));
        if (profile.Level > 0) facts.Add(Strings.Format("ModPropDecksLevel", profile.Level));
        DeckFacts.Text = string.Join("  ·  ", facts);

        DeckCardGrid.Children.Clear();
        _selectedTile = null;
        _pinnedCard = null;

        var tiles = Controls.DeckTiles.Build(deck, _cardDetails, _cardIcons, TileSize);
        for (var i = 0; i < tiles.Count; i++)
        {
            var card = deck.Cards[i];
            var tile = tiles[i];

            // Selection only. Hovering used to swap the panel as the pointer crossed the grid,
            // which made the description flicker past on the way to the card you wanted.
            tile.Click += (_, _) => SelectCard(card, tile);

            DeckCardGrid.Children.Add(tile);
        }

        if (tiles.Count > 0) SelectCard(deck.Cards[0], tiles[0]);
        else DeckDetailCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Pins a card. The rim changes COLOUR and never thickness: growing a border to 2 shifts
    /// every child of the tile by a pixel the moment you click it.
    /// </summary>
    private void SelectCard(Models.HomeCityCard card, Button tile)
    {
        _selectedTile = Controls.DeckTiles.Select(tile, _selectedTile);
        _pinnedCard = card;
        ShowCardDetail(card);
    }

    private void ShowCardDetail(Models.HomeCityCard card)
    {
        _cardDetails.TryGetValue(card.InternalName, out var detail);

        DeckDetailCard.Visibility = Visibility.Visible;
        DeckDetailName.Text = detail?.Name ?? card.InternalName;

        DeckDetailIcon.Source =
            detail?.IconPath != null && _cardIcons.TryGetValue(detail.IconPath, out var icon)
                ? icon
                : null;

        // The modder's own sentence when there is one. 20 of a real deck's 35 cards carry none,
        // so the line is dropped rather than filled with a placeholder — and those are exactly
        // the cards the effects below now describe instead.
        var description = detail?.Description ?? "";
        DeckDetailText.Text = description;
        DeckDetailText.Visibility =
            description.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        ShowCardEffects(card);
    }

    /// <summary>
    /// What the card changes, in the game's own words — the lines with the percentages that the
    /// engine builds from the card's effects rather than storing.
    /// </summary>
    private void ShowCardEffects(Models.HomeCityCard card)
    {
        DeckDetailEffects.Children.Clear();

        if (!_cardEffects.TryGetValue(card.InternalName, out var lines) || lines.Count == 0)
        {
            DeckDetailEffects.Visibility = Visibility.Collapsed;
            return;
        }

        DeckDetailEffects.Visibility = Visibility.Visible;

        var size = (double)Application.Current.FindResource("FontSizeCaption");
        var brush = (Brush)Application.Current.FindResource("MpTextBody");

        foreach (var line in lines)
        {
            DeckDetailEffects.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = brush,
                FontSize = size,
                MaxWidth = 560,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3),
            });
        }
    }

    // ======================================================================
    // Statistics — games against PEOPLE, read from the player's own recordings
    // ======================================================================

    /// <summary>
    /// How many recordings are opened. Each one is inflated whole before its header can be read,
    /// so this is a real cost rather than a directory listing.
    /// </summary>
    private const int MaxRecordingsScanned = 20;

    /// <summary>One local recording, reduced to what can honestly be said about it.</summary>
    private sealed record HumanMatchRow(
        string FileName,
        DateTime PlayedLocal,
        /// <summary>The decks the viewer brought, as they were that day, when a snapshot exists.</summary>
        IReadOnlyList<Models.HomeCityProfile>? Decks,
        string Map,
        IReadOnlyList<Services.Multiplayer.ReplayParserService.ReplayPlayer> Players,
        int LocalSlot,
        double? Result,
        int LoserSlot,
        int WinnerSlot,
        IReadOnlyDictionary<int, string> Civs);

    /// <summary>
    /// Fills the "games against players" group from the recordings in the mod's own Savegame
    /// folder.
    ///
    /// <para><b>This is not the match history repeated.</b> That list comes from the lobby
    /// backend and a row exists only because the host reported the match, so a skirmish, a LAN
    /// game outside a room, or a match whose host closed the launcher is absent from it. These
    /// files are the only record of those.</para>
    ///
    /// <para><b>And it says little on purpose.</b> Score, resources, units, XP and cards sent do
    /// not exist for a game with no AI in it — measured: not in the recording, not in the
    /// player's profile, not in the game log. Nor does a duration: the recording's own
    /// <c>gamehosttime</c> is not a wall clock, so the date comes from the file's write time,
    /// which is when the match ended.</para>
    /// </summary>
    private async System.Threading.Tasks.Task LoadHumanGamesAsync()
    {
        HumanGamesList.Children.Clear();

        var folderName = Services.UserDataService.ResolveFolderName(_profile, _config);
        var folder = string.IsNullOrWhiteSpace(folderName)
            ? ""
            : Services.UserDataService.GetUserDataFolder(folderName);

        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowHumanGamesEmpty();
            return;
        }

        var myName = Services.UserDataService.GetInGameName(_profile, _config);
        var installPath = _service.InstallPath;

        List<HumanMatchRow> rows;
        try
        {
            var snapshotMod = _profile.Id;
            rows = await System.Threading.Tasks.Task.Run(
                () => ReadHumanMatches(folder, myName, installPath, snapshotMod));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"ModProperties: local matches unavailable — {ex.Message}");
            ShowHumanGamesEmpty();
            return;
        }

        if (rows.Count == 0)
        {
            ShowHumanGamesEmpty();
            return;
        }

        HumanGamesEmptyHint.Visibility = Visibility.Collapsed;
        foreach (var row in rows) HumanGamesList.Children.Add(BuildHumanGameCard(row));
    }

    private void ShowHumanGamesEmpty()
    {
        HumanGamesEmptyHint.Text = Strings.Get("ModPropHumanGamesEmpty");
        HumanGamesEmptyHint.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The newest recordings that turn out to be games against people, most recent first.
    ///
    /// <para>Runs off the UI thread and is bounded: opening one of these means inflating the
    /// whole file, and a player who records everything can have a folder full of them.</para>
    /// </summary>
    private static List<HumanMatchRow> ReadHumanMatches(
        string userDataDir, string? myName, string? installPath, string? modId)
    {
        var rows = new List<HumanMatchRow>();

        var dir = Path.Combine(userDataDir, "Savegame");
        if (!Directory.Exists(dir)) dir = userDataDir;
        if (!Directory.Exists(dir)) return rows;

        var files = new DirectoryInfo(dir)
            .EnumerateFiles("*.age3?rec", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(MaxRecordingsScanned)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var raw = File.ReadAllBytes(file.FullName);
                var data = Services.Multiplayer.ReplayParserService.TryReadContainer(raw);
                if (data == null) continue;

                var header = Services.Multiplayer.ReplayParserService.ParseHeader(data);
                if (!Services.Multiplayer.LocalMatchView.IsHumanMatch(header)) continue;

                var slot = Services.Multiplayer.ReplayParserService.FindPlayerSlot(header, myName ?? "");
                var outcome = Services.Multiplayer.ReplayParserService.ReadOutcome(data, header);

                // The same rule the match report trusts: a result only in a clean two-human
                // 1v1, and nothing at all otherwise. Never a draw — "not known" and "drawn" are
                // different things and only one of them is ever true here.
                var result = Services.Multiplayer.ReplayParserService.HostResultFrom(outcome, slot);

                var civs = new Dictionary<int, string>();
                foreach (var player in header!.Players)
                {
                    if (civs.ContainsKey(player.Civilization)) continue;
                    var name = Services.Multiplayer.CivNameResolver
                        .Resolve(installPath, player.Civilization);
                    if (!string.IsNullOrWhiteSpace(name)) civs[player.Civilization] = name!;
                }

                // What the viewer's own decks held when this match ended, if the launcher was
                // there to keep a copy. Null for every match played before snapshots existed,
                // which is what the card has to draw itself without.
                var mine = slot >= 0
                    ? header.Players.FirstOrDefault(p => p.Slot == slot)?.HomeCityFile
                    : null;
                var decks = Services.DeckSnapshotStore.Read(modId, file.LastWriteTimeUtc, mine);

                rows.Add(new HumanMatchRow(
                    FileName: Path.GetFileNameWithoutExtension(file.Name),
                    Decks: decks,
                    PlayedLocal: file.LastWriteTime,
                    Map: Services.Multiplayer.LocalMatchView.PrettyMap(header.MapName),
                    Players: header.Players,
                    LocalSlot: slot,
                    Result: result,
                    // Who lost is MEASURED and stands on its own; who won is DERIVED, and only
                    // in a clean two-human 1v1. Kept apart so the card can say the first
                    // without implying the second.
                    LoserSlot: outcome.LoserSlot,
                    WinnerSlot: outcome.Confidence
                        == Services.Multiplayer.ReplayParserService.ReplayOutcomeConfidence.Confident
                            ? outcome.WinnerSlot
                            : -1,
                    Civs: civs));
            }
            catch (Exception ex)
            {
                // One unreadable recording costs one row. They are written by the game while it
                // exits, so a truncated file is a normal thing to find.
                DiagnosticLog.Write($"ModProperties: could not read '{file.Name}' — {ex.Message}");
            }
        }

        return rows;
    }

    /// <summary>
    /// One local match, as a card.
    ///
    /// <para><c>internal static</c> for the same reason the AI game card is: nothing else builds
    /// it, no compile step checks a resource looked up by name, and this tab is not one the
    /// startup smoke test ever opens.</para>
    /// </summary>
    internal static Border BuildHumanGameCard(
        string fileName,
        DateTime playedLocal,
        string map,
        IReadOnlyList<Services.Multiplayer.ReplayParserService.ReplayPlayer> players,
        int localSlot,
        double? result,
        int loserSlot,
        int winnerSlot,
        IReadOnlyDictionary<int, string> civs,
        UIElement? deckSection = null)
    {
        var caption = (double)Application.Current.FindResource("FontSizeCaption");
        var stack = new StackPanel();

        var headline = new TextBlock
        {
            Foreground = (Brush)Application.Current.FindResource("MpTextPrimary"),
            FontSize = (double)Application.Current.FindResource("FontSizeBodyStrong"),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        // No result is the common case past a 1v1, and it is drawn as SILENCE. A "Draw" badge
        // would be a claim, and the recording never makes it.
        if (result.HasValue)
        {
            var won = result.Value >= 1.0;
            headline.Inlines.Add(new System.Windows.Documents.Run(
                Strings.Get(won ? "ModPropStatsWon" : "ModPropStatsLost"))
            {
                Foreground = (Brush)Application.Current.FindResource(won ? "MpOk" : "MpDestructiveText"),
            });
            headline.Inlines.Add(new System.Windows.Documents.Run("  ·  "));
        }

        headline.Inlines.Add(new System.Windows.Documents.Run(
            map.Length > 0 ? map : Strings.Get("ModPropHumanMapUnknown")));

        // Through the same helper the chat's day divider uses, so the two cannot disagree about
        // when a day stops being "yesterday", and the month names follow the launcher's language
        // rather than the operating system's.
        headline.Inlines.Add(new System.Windows.Documents.Run("  ·  "
            + Services.ChatTimeFormat.DateLabel(
                playedLocal, DateTime.Today,
                Strings.Get("MpChatToday"), Strings.Get("MpChatYesterday"),
                System.Globalization.CultureInfo.GetCultureInfo(
                    Strings.Language == Strings.LangEs ? "es" : "en")))
        {
            Foreground = (Brush)Application.Current.FindResource("OnSecondaryContainer"),
            FontWeight = FontWeights.Normal,
        });

        stack.Children.Add(headline);

        foreach (var player in players)
        {
            if (!player.IsHuman) continue;

            var facts = new List<string> { player.Name };
            if (civs.TryGetValue(player.Civilization, out var civ)) facts.Add(civ);
            if (!string.IsNullOrWhiteSpace(player.Explorer)) facts.Add(player.Explorer);
            if (player.HomeCityLevel > 0)
                facts.Add(Strings.Format("ModPropDecksLevel", player.HomeCityLevel));

            var city = Services.Multiplayer.LocalMatchView.HomeCityFrom(player.HomeCityFile);
            if (city.Length > 0) facts.Add(city);

            var mine = player.Slot == localSlot;
            var line = new TextBlock
            {
                Foreground = (Brush)Application.Current.FindResource(
                    mine ? "MpTextPrimary" : "OnSecondaryContainer"),
                FontSize = caption,
                FontWeight = mine ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };

            // Marked per PLAYER rather than only for the viewer, because most of what a player
            // keeps is other people's recordings — a match somebody sent them, which they are
            // not in. Saying nothing there threw away the one fact the file does carry.
            var verdict = player.Slot == loserSlot ? "ModPropHumanLost"
                        : player.Slot == winnerSlot ? "ModPropHumanWon"
                        : null;

            if (verdict != null)
            {
                line.Inlines.Add(new System.Windows.Documents.Run(Strings.Get(verdict))
                {
                    Foreground = (Brush)Application.Current.FindResource(
                        verdict == "ModPropHumanWon" ? "MpOk" : "MpDestructiveText"),
                    FontWeight = FontWeights.SemiBold,
                });
                line.Inlines.Add(new System.Windows.Documents.Run("  ·  "));
            }

            line.Inlines.Add(new System.Windows.Documents.Run(string.Join("  ·  ", facts)));
            stack.Children.Add(line);
        }

        // The file, because AoE3 names every recording "Record Game N" and renumbers them, so
        // this is the only way to know which one on disk this row is.
        stack.Children.Add(new TextBlock
        {
            Text = fileName,
            Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
            FontFamily = new FontFamily("Consolas"),
            FontSize = caption,
            Margin = new Thickness(0, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (deckSection != null) stack.Children.Add(deckSection);

        return new Border
        {
            Child = stack,
            Padding = new Thickness(14, 11, 14, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Background = (Brush)Application.Current.FindResource("MpSurfaceAlt"),
            BorderBrush = (Brush)Application.Current.FindResource("MpRimSoft"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.FindResource("RadiusMd"),
        };
    }

    private Border BuildHumanGameCard(HumanMatchRow row) => BuildHumanGameCard(
        row.FileName, row.PlayedLocal, row.Map, row.Players, row.LocalSlot, row.Result,
        row.LoserSlot, row.WinnerSlot, row.Civs,
        BuildDeckSnapshotSection(row.Decks, ResolveDeckArtAsync));

    /// <summary>
    /// Card names, descriptions and pictures for a saved deck. Off the UI thread: it streams the
    /// mod's tech files, 12 MB in Wars of Liberty.
    /// </summary>
    private async System.Threading.Tasks.Task<(
        IReadOnlyDictionary<string, Services.CardDetail> Details,
        IReadOnlyDictionary<string, ImageSource> Icons)>
        ResolveDeckArtAsync(IReadOnlyList<Models.HomeCityProfile> decks)
    {
        var installPath = _service.InstallPath;
        var exe = _profile.GameExecutable;

        try
        {
            return await System.Threading.Tasks.Task.Run(() =>
            {
                var names = decks.SelectMany(p => p.Decks).SelectMany(d => d.Cards)
                    .Select(c => c.InternalName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var resolved = Services.CardNameResolver.ResolveDetails(installPath, exe, names);
                var art = Services.CardArtService.Load(
                    installPath, resolved.Values.Select(d => d.IconPath));

                return ((IReadOnlyDictionary<string, Services.CardDetail>)resolved,
                        (IReadOnlyDictionary<string, ImageSource>)art);
            });
        }
        catch (Exception ex)
        {
            // The deck still draws, under the internal names and without pictures.
            DiagnosticLog.Write($"ModProperties: saved deck art unavailable — {ex.Message}");
            return (new Dictionary<string, Services.CardDetail>(),
                    new Dictionary<string, ImageSource>());
        }
    }

    /// <summary>
    /// The decks the viewer brought to THIS match, kept when it ended.
    ///
    /// <para><b>Folded away until asked for</b>, twice over. A match card that grew 25 tiles by
    /// itself would bury the list, and opening the deck needs the mod's card names — a 12 MB
    /// scan — which nobody should pay for merely opening STATISTICS.</para>
    ///
    /// <para>Null for every match played before snapshots existed, and for anyone else's
    /// recording: only the viewer's own home city files are on this disk.</para>
    /// </summary>
    /// <remarks>
    /// <c>internal static</c> with the art passed in as a callback so a test can build the real
    /// thing. It is not decoration: the first version applied <c>SetActionQuiet</c> — a
    /// <c>TextBlock</c> style — to a <c>Button</c>, which throws, and because this runs inside
    /// the STATISTICS load it took BOTH groups of that page down with it. Every test passed:
    /// nothing built this element.
    /// </remarks>
    internal static UIElement? BuildDeckSnapshotSection(
        IReadOnlyList<Models.HomeCityProfile>? decks,
        Func<IReadOnlyList<Models.HomeCityProfile>, System.Threading.Tasks.Task<(
            IReadOnlyDictionary<string, Services.CardDetail> Details,
            IReadOnlyDictionary<string, ImageSource> Icons)>> resolveArt)
    {
        if (decks == null || decks.Count == 0) return null;

        var host = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        var show = new Button
        {
            Content = Strings.Get("ModPropHumanDeckShow"),
            Style = (Style)Application.Current.FindResource("SetActionButtonSm"),
            HorizontalAlignment = HorizontalAlignment.Left,

            // The shared action buttons are FIXED width — 88 px for the small one — because they
            // line up in a column on the settings pages. This one sits alone under a sentence,
            // has nothing to line up with, and a caption that says what it opens does not fit in
            // 88 px in either language; a Button cannot ellipsise its own text, so it would just
            // be cut. NaN restores sizing to content, and a local value beats the style's setter.
            Width = double.NaN,
            Padding = new Thickness(12, 0, 12, 0),
        };

        show.Click += async (_, _) =>
        {
            show.IsEnabled = false;
            show.Content = Strings.Get("ModPropHumanDeckLoading");

            var (details, icons) = await resolveArt(decks);

            host.Children.Remove(show);

            // Says the two things that would otherwise be read as a claim: these are the cards
            // as they were that day, and the game does not record WHICH of a city's decks was
            // used — so every deck of it is shown rather than one of them picked.
            host.Children.Add(new TextBlock
            {
                Text = Strings.Get("ModPropHumanDeckNote"),
                Foreground = (Brush)Application.Current.FindResource("MpTextMuted"),
                FontSize = (double)Application.Current.FindResource("FontSizeCaption"),
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap,
            });

            foreach (var profile in decks)
                foreach (var deck in profile.Decks)
                {
                    host.Children.Add(new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(deck.Name) ? profile.CityName : deck.Name,
                        Foreground = (Brush)Application.Current.FindResource("OnSecondaryContainer"),
                        FontSize = (double)Application.Current.FindResource("FontSizeCaption"),
                        Margin = new Thickness(0, 2, 0, 3),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });

                    var grid = new WrapPanel { MaxWidth = 560, Margin = new Thickness(0, 0, 0, 6) };
                    foreach (var tile in Controls.DeckTiles.Build(deck, details, icons, 34, "MpRimFaint"))
                        grid.Children.Add(tile);

                    host.Children.Add(grid);
                }
        };

        host.Children.Add(show);
        return host;
    }
}
