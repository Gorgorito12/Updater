using System;
using System.Collections.Generic;

namespace WarsOfLibertyLauncher.Localization;

/// <summary>
/// Centralized string table for the launcher UI.
///
/// English is the default language. To add a new language, add a new key to
/// each entry's inner dictionary. Strings missing a translation fall back
/// to English; if even English is missing, the key itself is returned so
/// missing translations are visible in the UI.
///
/// Use <see cref="Format"/> for parameterized messages (uses string.Format
/// semantics with {0}, {1}, ... placeholders).
///
/// Diagnostic log messages are NOT localized — they're always in English
/// because they're meant for developers and bug reports.
/// </summary>
public static class Strings
{
    public const string LangEn = "en";
    public const string LangEs = "es";

    public static string Language { get; set; } = LangEn;

    /// <summary>Raised whenever <see cref="Language"/> changes so the UI can refresh.</summary>
    public static event Action? LanguageChanged;

    public static void SetLanguage(string lang)
    {
        if (lang != LangEn && lang != LangEs) lang = LangEn;
        if (Language == lang) return;
        Language = lang;
        LanguageChanged?.Invoke();
    }

    public static string Get(string key)
    {
        if (Table.TryGetValue(key, out var langs))
        {
            if (langs.TryGetValue(Language, out var localized)) return localized;
            if (langs.TryGetValue(LangEn, out var fallback)) return fallback;
        }
        return key;     // visible signal of missing translation
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(Get(key), args);

    /// <summary>
    /// The value for a SPECIFIC language, ignoring <see cref="Language"/>.
    ///
    /// <para>For the one case <see cref="Get"/> cannot serve: a string that was once written
    /// into data which OUTLIVES the session that wrote it. The room title is the example -
    /// it is persisted on the lobby server, so recognising a title this launcher produced in
    /// an earlier build means enumerating what it would have said in every language, not just
    /// the one that happens to be selected now.</para>
    /// </summary>
    internal static string GetIn(string lang, string key)
    {
        if (Table.TryGetValue(key, out var langs))
        {
            if (langs.TryGetValue(lang, out var localized)) return localized;
            if (langs.TryGetValue(LangEn, out var fallback)) return fallback;
        }
        return key;
    }

    /// <summary><see cref="GetIn"/> with <see cref="string.Format(string, object?[])"/>.</summary>
    internal static string FormatIn(string lang, string key, params object?[] args) =>
        string.Format(GetIn(lang, key), args);

    // ------------------------------------------------------------------------
    // String table: ordered roughly by where strings appear in the launcher.
    // Keep keys descriptive and stable — they're referenced from XAML/code.
    // ------------------------------------------------------------------------
    private static readonly Dictionary<string, Dictionary<string, string>> Table = new()
    {
        // -------- Window / labels --------
        // {0} is the active mod profile's display name (e.g. "Wars of Liberty",
        // "Improvement Mod"). Both fields used to be hard-coded to WoL.
        ["WindowTitle"] = new()
        {
            [LangEn] = "{0} Launcher",
            [LangEs] = "{0} Launcher",
        },
        // -------- Global title-bar caption-button tooltips --------
        ["TitleBarMinimize"] = new()
        {
            [LangEn] = "Minimize",
            [LangEs] = "Minimizar",
        },
        ["TitleBarMaximize"] = new()
        {
            [LangEn] = "Maximize",
            [LangEs] = "Maximizar",
        },
        ["TitleBarRestore"] = new()
        {
            [LangEn] = "Restore",
            [LangEs] = "Restaurar",
        },
        ["TitleBarClose"] = new()
        {
            [LangEn] = "Close",
            [LangEs] = "Cerrar",
        },
        // -------- Mod selector popup --------
        ["ModSelectorInstalled"] = new()
        {
            [LangEn] = "Installed · v{0}",
            [LangEs] = "Instalado · v{0}",
        },
        ["ModSelectorInstalledNoVersion"] = new()
        {
            [LangEn] = "Installed",
            [LangEs] = "Instalado",
        },
        ["ModSelectorNotInstalled"] = new()
        {
            [LangEn] = "Not installed",
            [LangEs] = "No instalado",
        },
        // -------- Mod-switch blocking dialogs --------
        ["DlgModSwitchBlockedTitle"] = new()
        {
            [LangEn] = "Can't switch mod right now",
            [LangEs] = "No se puede cambiar de mod ahora",
        },
        ["DlgModSwitchBusyBody"] = new()
        {
            [LangEn] = "An operation is in progress. Wait for it to finish " +
                       "(or cancel it from the toolbar) before switching mods.",
            [LangEs] = "Hay una operación en curso. Espera a que termine " +
                       "(o cancélala desde la barra) antes de cambiar de mod.",
        },
        ["DlgModSwitchGameRunningBody"] = new()
        {
            [LangEn] = "The game is currently running. Close it before " +
                       "switching to another mod.",
            [LangEs] = "El juego está abierto. Ciérralo antes de cambiar a otro mod.",
        },
        // -------- New layout: top mods bar, sidebar labels, tabs --------
        ["ModsBarLabel"] = new()
        {
            [LangEn] = "MODS",
            [LangEs] = "MODS",
        },
        ["ActionsLabel"] = new()
        {
            [LangEn] = "MOD ACTIONS",
            [LangEs] = "ACCIONES DEL MOD",
        },
        ["TabNoticias"] = new()
        {
            [LangEn] = "News",
            [LangEs] = "Noticias",
        },
        ["TabChangelog"] = new()
        {
            [LangEn] = "Changelog",
            [LangEs] = "Changelog",
        },
        ["TabAyuda"] = new()
        {
            [LangEn] = "Help",
            [LangEs] = "Ayuda",
        },
        // Help tab body (per-mod help text). Profile-specific overrides
        // come from ModProfile.HelpText when populated.
        ["HelpDefaultBody"] = new()
        {
            [LangEn] = "No additional help is available for this mod yet. " +
                       "If you run into trouble, open the gear menu in the " +
                       "sidebar — it has tools to verify files, swap folders, " +
                       "back up your user data, and uninstall.",
            [LangEs] = "Todavía no hay ayuda adicional para este mod. Si " +
                       "tienes algún problema, abre el menú de configuración " +
                       "en la barra lateral — tiene herramientas para " +
                       "verificar archivos, cambiar carpetas, hacer backup " +
                       "de tus datos y desinstalar.",
        },
        ["ChangelogPlaceholder"] = new()
        {
            [LangEn] = "No changelog available for this mod yet.",
            [LangEs] = "Todavía no hay changelog para este mod.",
        },
        // -------- Inline progress panel (top of news content) --------
        ["ProgressPanelHeader"] = new()
        {
            [LangEn] = "INSTALL / UPDATE PROGRESS",
            [LangEs] = "PROGRESO DE INSTALACIÓN / ACTUALIZACIÓN",
        },
        // Per-operation header — shown in the small label at the top of
        // the panel. Used to disambiguate which action is running.
        ["ProgressLabelInstall"] = new()
        {
            [LangEn] = "INSTALL PROGRESS",
            [LangEs] = "PROGRESO DE INSTALACIÓN",
        },
        ["ProgressLabelUpdate"] = new()
        {
            [LangEn] = "UPDATE PROGRESS",
            [LangEs] = "PROGRESO DE ACTUALIZACIÓN",
        },
        ["ProgressLabelRepair"] = new()
        {
            [LangEn] = "REPAIR PROGRESS",
            [LangEs] = "PROGRESO DE REPARACIÓN",
        },
        ["ProgressLabelVerify"] = new()
        {
            [LangEn] = "FILE VERIFICATION",
            [LangEs] = "VERIFICACIÓN DE ARCHIVOS",
        },
        ["ProgressLabelUninstall"] = new()
        {
            [LangEn] = "UNINSTALL PROGRESS",
            [LangEs] = "PROGRESO DE DESINSTALACIÓN",
        },
        // Bar labels (one per operation flavor — Repair calls them
        // Verify / Repair, Uninstall calls them Process / Cleanup, etc.)
        ["ProgressBarDownload"] = new()
        {
            [LangEn] = "Download",
            [LangEs] = "Descarga",
        },
        ["ProgressBarInstall"] = new()
        {
            [LangEn] = "Installation",
            [LangEs] = "Instalación",
        },
        ["ProgressBarVerify"] = new()
        {
            [LangEn] = "Verification",
            [LangEs] = "Verificación",
        },
        ["ProgressBarRepair"] = new()
        {
            [LangEn] = "Repair",
            [LangEs] = "Reparación",
        },
        ["ProgressBarProcess"] = new()
        {
            [LangEn] = "Process",
            [LangEs] = "Proceso",
        },
        ["ProgressBarCleanup"] = new()
        {
            [LangEn] = "Cleanup",
            [LangEs] = "Limpieza",
        },
        // -------- Idle state of the bottom-left panel --------
        // The panel that used to host "Game detected" now mirrors the mod's
        // current status when no operation is running: Ready / Update
        // available / Not installed / AoE3 missing. Header label sits
        // above the icon + title row.
        // -------- New StatusCard rows (top of sidebar) --------
        ["StatusCardCurrentVersion"] = new()
        {
            [LangEn] = "Current version:",
            [LangEs] = "Versión actual:",
        },
        ["StatusCardLatestVersion"] = new()
        {
            [LangEn] = "Latest version:",
            [LangEs] = "Última versión:",
        },
        ["StatusCardInstalled"] = new()
        {
            [LangEn] = "Installed",
            [LangEs] = "Instalado",
        },
        ["StatusCardNotInstalled"] = new()
        {
            [LangEn] = "Not installed",
            [LangEs] = "No instalado",
        },
        // -------- ProgressPanel idle (bottom of sidebar) --------
        // Neutral "ready for any operation" look. Color flips to whatever
        // operation is running once StartProgressPanel takes over.
        ["ProgressIdleHeader"] = new()
        {
            [LangEn] = "PROGRESS PANEL",
            [LangEs] = "PANEL DE PROGRESO",
        },
        ["ProgressIdleTitle"] = new()
        {
            [LangEn] = "Ready for operations",
            [LangEs] = "Listo para operaciones",
        },
        ["IdleStateUpdateAvailable"] = new()
        {
            [LangEn] = "Update available",
            [LangEs] = "Actualización disponible",
        },
        // {0} = mod display name (e.g. "Wars of Liberty").
        ["IdleStateUnknownVersion"] = new()
        {
            [LangEn] = "Version not recognised",
            [LangEs] = "Versión no reconocida",
        },
        ["IdleStateGameMissing"] = new()
        {
            [LangEn] = "Age of Empires III not found",
            [LangEs] = "Age of Empires III no encontrado",
        },
        // {0} = mod display name, {1} = installed version.
        // {0} = current version, {1} = latest version.
        // Inline button shown in the panel's idle state when AoE3 isn't
        // detected — replaces the old "..." button in the game footer.
        ["BtnFindAoE3"] = new()
        {
            [LangEn] = "Find AoE3",
            [LangEs] = "Buscar AoE3",
        },
        // Title shown in the panel header during uninstall.
        ["ProgressTitleUninstalling"] = new()
        {
            [LangEn] = "Uninstalling {0}",
            [LangEs] = "Desinstalando {0}",
        },
        // Subtitle for the uninstall flow.
        ["ProgressSubRemoving"] = new()
        {
            [LangEn] = "Removing mod files...",
            [LangEs] = "Eliminando archivos del mod...",
        },
        // Title row at the top of the panel — "Installing/Updating {0}".
        ["ProgressTitleInstalling"] = new()
        {
            [LangEn] = "Installing {0}",
            [LangEs] = "Instalando {0}",
        },
        ["ProgressTitleUpdating"] = new()
        {
            [LangEn] = "Updating {0}",
            [LangEs] = "Actualizando {0}",
        },
        ["ProgressTitleRepairing"] = new()
        {
            [LangEn] = "Repairing {0}",
            [LangEs] = "Reparando {0}",
        },
        ["ProgressTitleVerifying"] = new()
        {
            [LangEn] = "Verifying {0}",
            [LangEs] = "Verificando {0}",
        },
        ["ProgressTitleCompleted"] = new()
        {
            [LangEn] = "Completed",
            [LangEs] = "Completado",
        },
        ["ProgressTitleError"] = new()
        {
            [LangEn] = "Operation failed",
            [LangEs] = "La operación falló",
        },
        ["ProgressTitleCancelled"] = new()
        {
            [LangEn] = "Cancelled",
            [LangEs] = "Cancelado",
        },
        // End-state banners inside the panel.
        ["ProgressCompletedMessage"] = new()
        {
            [LangEn] = "All done. The mod is ready to play.",
            [LangEs] = "Listo. El mod ya está disponible para jugar.",
        },
        ["ProgressCancelledMessage"] = new()
        {
            [LangEn] = "The operation was cancelled. You can resume by retrying.",
            [LangEs] = "La operación fue cancelada. Puedes reintentar para retomarla.",
        },
        // Sub-step phrases used as the panel's subtitle.
        ["ProgressSubDownloading"] = new()
        {
            [LangEn] = "Downloading...",
            [LangEs] = "Descargando...",
        },
        ["ProgressSubVerifying"] = new()
        {
            [LangEn] = "Verifying files...",
            [LangEs] = "Verificando archivos...",
        },
        // Step counter in the top-right of the panel — "Step {0} of {1}".
        ["ProgressStepFormat"] = new()
        {
            [LangEn] = "Step {0} of {1}",
            [LangEs] = "Paso {0} de {1}",
        },
        // Action button labels inside the panel.
        ["BtnRetry"] = new()
        {
            [LangEn] = "Retry",
            [LangEs] = "Reintentar",
        },
        ["BtnClose"] = new()
        {
            [LangEn] = "Close",
            [LangEs] = "Cerrar",
        },
        // Shown in the INSTALLED VERSION card for mods that don't have
        // Status line for mods whose updates are managed outside the
        // launcher (their own patcher, ModDB, etc.). {0} = mod name.
        ["StatusReadyExternalUpdates"] = new()
        {
            [LangEn] = "Ready to play. Updates for {0} are managed externally.",
            [LangEs] = "Listo para jugar. Las actualizaciones de {0} se manejan por fuera del launcher.",
        },
        ["StatusModNotInstalledExternal"] = new()
        {
            [LangEn] = "{0} isn't installed yet. Install it from its own site, then come back here to play.",
            [LangEs] = "{0} todavía no está instalado. Instálalo desde su sitio y vuelve aquí para jugar.",
        },
        ["StatusStockReady"] = new()
        {
            [LangEn] = "{0} detected. Ready to play.",
            [LangEs] = "{0} detectado. Listo para jugar.",
        },
        ["StatusStockNotDetected"] = new()
        {
            [LangEn] = "{0} wasn't found on this PC. Install it from Steam, GOG or your retail disc, then reopen the launcher.",
            [LangEs] = "No se encontró {0} en esta PC. Instálalo desde Steam, GOG o tu disco original y vuelve a abrir el launcher.",
        },
        ["Subtitle"] = new()
        {
            [LangEn] = "Launcher",
            [LangEs] = "Launcher",
        },
        ["ChangePathButton"] = new()
        {
            [LangEn] = "Change...",
            [LangEs] = "Cambiar...",
        },

        // -------- Uninstall --------
        ["BtnUninstall"] = new()
        {
            [LangEn] = "Uninstall",
            [LangEs] = "Desinstalar",
        },
        ["MenuUninstall"] = new()
        {
            [LangEn] = "Uninstall mod...",
            [LangEs] = "Desinstalar mod...",
        },
        // {0} = mod display name (e.g. "Wars of Liberty", "Improvement Mod")
        ["DlgUninstallTitle"] = new()
        {
            [LangEn] = "Uninstall {0}",
            [LangEs] = "Desinstalar {0}",
        },
        ["DlgUninstallHeader"] = new()
        {
            [LangEn] = "Uninstall {0}",
            [LangEs] = "Desinstalar {0}",
        },
        // {0} = mod display name
        ["DlgUninstallDescription"] = new()
        {
            [LangEn] = "This will delete the entire {0} install folder. Your Age of Empires III base game lives in a separate folder and will not be touched.",
            [LangEs] = "Esto eliminará la carpeta completa de {0}. Tu instalación de Age of Empires III está en otra carpeta y no será modificada.",
        },
        ["DlgUninstallInstallPathLabel"] = new()
        {
            [LangEn] = "INSTALL FOLDER",
            [LangEs] = "CARPETA A ELIMINAR",
        },
        ["DlgUninstallOptionsTitle"] = new()
        {
            [LangEn] = "ALSO CLEAN UP",
            [LangEs] = "TAMBIÉN LIMPIAR",
        },
        ["DlgUninstallOptShortcuts"] = new()
        {
            [LangEn] = "Remove desktop and Start Menu shortcuts",
            [LangEs] = "Eliminar accesos directos del escritorio y menú inicio",
        },
        ["DlgUninstallOptRegistry"] = new()
        {
            [LangEn] = "Remove Windows registry entry (Add/Remove Programs)",
            [LangEs] = "Eliminar entrada del registro de Windows (Programas y características)",
        },
        ["DlgUninstallOptResetConfig"] = new()
        {
            [LangEn] = "Reset launcher config to defaults",
            [LangEs] = "Restablecer la configuración del launcher",
        },
        ["DlgUninstallAoE3SafeNote"] = new()
        {
            [LangEn] = "✓ Your Age of Empires III install (in Steam\\steamapps\\common\\Age Of Empires 3 or wherever it lives) will not be modified.",
            [LangEs] = "✓ Tu instalación de Age of Empires III (en Steam\\steamapps\\common\\Age Of Empires 3 o donde la tengas) no será modificada.",
        },
        ["DlgUninstallValidDetail"] = new()
        {
            [LangEn] = "{0} files in {1} folders will be removed.",
            [LangEs] = "Se eliminarán {0} archivos en {1} carpetas.",
        },
        // {0} = mod display name (uppercased in the UI styling, not in the string)
        ["DlgUninstallNotValidTitle"] = new()
        {
            [LangEn] = "✗ NOT A VALID {0} INSTALL",
            [LangEs] = "✗ NO ES UNA INSTALACIÓN VÁLIDA DE {0}",
        },
        // {0} = folder path the user pointed at, {1} = probe file the mod
        // expects there (e.g. "age3m.exe", "data\\stringtabley.xml"), {2} =
        // mod display name.
        ["DlgUninstallNotValidDetail"] = new()
        {
            [LangEn] = "The folder '{0}' does not contain the {2} marker ({1}). For safety, the launcher refuses to delete it.\n\nIf this is a real {2} install with broken files, run Verify first to repair it.",
            [LangEs] = "La carpeta '{0}' no contiene el marcador de {2} ({1}). Por seguridad, el launcher se niega a eliminarla.\n\nSi es una instalación real de {2} con archivos rotos, ejecuta Verificar primero para repararla.",
        },
        ["DlgUninstallNothingTitle"] = new()
        {
            [LangEn] = "NOTHING TO UNINSTALL",
            [LangEs] = "NADA QUE DESINSTALAR",
        },
        ["DlgUninstallNothingDetail"] = new()
        {
            [LangEn] = "No installation was detected.",
            [LangEs] = "No se detectó ninguna instalación.",
        },

        // {0} = mod display name
        ["StatusUninstalling"] = new()
        {
            [LangEn] = "Uninstalling {0}...",
            [LangEs] = "Desinstalando {0}...",
        },
        // {0} = mod display name, {1} = file count removed.
        ["StatusUninstallSuccess"] = new()
        {
            [LangEn] = "{0} was uninstalled successfully ({1} files removed).",
            [LangEs] = "{0} se desinstaló correctamente ({1} archivos eliminados).",
        },
        ["StatusUninstallPartial"] = new()
        {
            [LangEn] = "Uninstall finished with {0} error(s). Check the log for details.",
            [LangEs] = "Desinstalación terminada con {0} error(es). Revisa el log para más detalles.",
        },
        ["NewsPlaceholder"] = new()
        {
            [LangEn] = "News from the latest update will appear here.",
            [LangEs] = "Las novedades de la última actualización aparecerán aquí.",
        },

        // -------- Buttons --------
        ["BtnUpdate"] = new()
        {
            [LangEn] = "Update",
            [LangEs] = "Actualizar",
        },
        ["BtnConfig"] = new()
        {
            [LangEn] = "Settings",
            [LangEs] = "Configuración",
        },
        // Section headers inside the Settings menu — small-caps gray
        // labels grouping the items below each one. Not clickable.
        ["MenuSectionPaths"] = new()
        {
            [LangEn] = "PATHS",
            [LangEs] = "RUTAS",
        },
        ["MenuSectionUserData"] = new()
        {
            [LangEn] = "USER DATA",
            [LangEs] = "DATOS DE USUARIO",
        },
        ["MenuSectionLanguage"] = new()
        {
            [LangEn] = "LANGUAGE",
            [LangEs] = "IDIOMA",
        },
        ["MenuSectionMaintenance"] = new()
        {
            [LangEn] = "MAINTENANCE",
            [LangEs] = "MANTENIMIENTO",
        },
        ["MenuSectionAdvanced"] = new()
        {
            [LangEn] = "ADVANCED",
            [LangEs] = "AVANZADO",
        },
        ["MenuSectionDanger"] = new()
        {
            [LangEn] = "DANGER",
            [LangEs] = "PELIGRO",
        },

        // -------- Launcher Settings dialog (Tier 1) --------
        // "Ajustes", not the repo house rule's preferred "Configuración": the design
        // handoff names this window and the maintainer asked for the reference verbatim.
        ["DlgLauncherSettingsTitle"] = new()
        {
            [LangEn] = "Launcher settings",
            [LangEs] = "Ajustes del launcher",
        },
        ["DlgLauncherSettingsSectionGeneral"] = new()
        {
            [LangEn] = "General",
            [LangEs] = "General",
        },
        ["DlgLauncherSettingsSectionUpdates"] = new()
        {
            [LangEn] = "UPDATES",
            [LangEs] = "ACTUALIZACIONES",
        },
        ["DlgLauncherSettingsSectionCatalog"] = new()
        {
            [LangEn] = "CATALOGUE",
            [LangEs] = "CATÁLOGO",
        },
        ["DlgLauncherSettingsCatalogSubheader"] = new()
        {
            [LangEn] = "Mods catalog",
            [LangEs] = "Catálogo de mods",
        },
        ["DlgLauncherSettingsTxSourcesHeader"] = new()
        {
            // UPPERCASE like every other SetGroupLabel beside it (ACTUALIZACIONES,
            // CATALOGO, MANTENIMIENTO): WPF has no text-transform, so the case lives in
            // the string and this one was the only lowercase label on the page.
            [LangEn] = "TRANSLATION SOURCES",
            [LangEs] = "FUENTES DE TRADUCCIONES",
        },
        ["DlgLauncherSettingsSectionInterface"] = new()
        {
            [LangEn] = "Interface",
            [LangEs] = "Interfaz",
        },
        // --- Text size (Settings -> Interface) ---
        // It scales the TYPE and nothing else, and the label says so, because the obvious
        // reading of "text size" in a launcher is the zoom this deliberately is not.
        ["DlgSettingsTextScaleLabel"] = new()
        {
            [LangEn] = "Text size",
            [LangEs] = "Tamaño del texto",
        },
        ["DlgSettingsTextScaleHint"] = new()
        {
            [LangEn] = "Makes the lettering bigger without changing the layout. "
                     + "Automatic works it out from your monitor.",
            [LangEs] = "Agranda la letra sin cambiar la distribución de la pantalla. "
                     + "Automático lo calcula según tu monitor.",
        },
        ["DlgSettingsTextScaleTip"] = new()
        {
            [LangEn] = "Only the text is scaled — spacing, buttons and rows keep their size, "
                     + "so nothing moves out of place. The change applies right away.",
            [LangEs] = "Solo se escala el texto: los espacios, los botones y las filas mantienen "
                     + "su tamaño, así que nada se descoloca. El cambio se aplica al momento.",
        },
        ["DlgSettingsTextScaleAuto"] = new()
        {
            [LangEn] = "Automatic",
            [LangEs] = "Automático",
        },
        ["DlgSettingsTextScalePercent"] = new()
        {
            [LangEn] = "{0} %",
            [LangEs] = "{0} %",
        },
        // What Automatic decided, and what from. A default that picks a size without ever
        // saying which one is a setting nobody can check.
        ["DlgSettingsTextScaleResolved"] = new()
        {
            [LangEn] = "Detected: {0}\" screen at {1}x{2} → {3} %",
            [LangEs] = "Detectado: pantalla de {0}\" a {1}x{2} → {3} %",
        },
        // The panel didn't report its size — a remote session, a virtual display, a
        // projector. Says so instead of implying a measurement was taken.
        ["DlgSettingsTextScaleResolvedUnknown"] = new()
        {
            [LangEn] = "Your monitor doesn't report its size, so it stays at {0} %. "
                     + "Pick a percentage if you want it bigger.",
            [LangEs] = "Tu monitor no informa de su tamaño, así que se queda en {0} %. "
                     + "Elige un porcentaje si lo quieres más grande.",
        },
        ["DlgLauncherSettingsTabOrderLabel"] = new()
        {
            [LangEn] = "TAB ORDER",
            [LangEs] = "ORDEN DE PESTAÑAS",
        },
        ["DlgLauncherSettingsTabOrderHint"] = new()
        {
            [LangEn] = "The first tab is the one that opens when you start the launcher. Use the arrows to reorder.",
            [LangEs] = "La primera pestaña es la que se abre al iniciar el launcher. Usa las flechas para reordenar.",
        },
        // Small accent badge next to whichever tab sits first in the
        // reorder list. Uppercase to read as a tag, not a sentence.
        ["DlgLauncherSettingsTabOrderOpensFirst"] = new()
        {
            [LangEn] = "OPENS ON LAUNCH",
            [LangEs] = "ABRE AL INICIAR",
        },
        // --- The three sections the redesign introduces (7 rail entries -> 5).
        // Maintenance + Privacy + Developer were three entries that between them
        // filled half a screen; Updates and Catalog explained each other, since the
        // update channel sat apart from the catalog the mods come from. The rail
        // labels are sentence case because they are names, not headers — the
        // UPPERCASE keys above them survive as the GROUP labels inside a card.
        // The product name on its own, for surfaces that show it as an identity
        // rather than as a tooltip (the settings rail footer). Deliberately not
        // reusing TrayTooltip, which happens to hold the same words for a
        // different reason.
        ["AppProductName"] = new()
        {
            [LangEn] = "AoE3 Mod Launcher",
            [LangEs] = "AoE3 Mod Launcher",
        },
        // Opens the GAMES section. It states the consequence once, at the top,
        // instead of repeating a four-line paragraph under each switch.
        ["DlgSettingsGamesIntro"] = new()
        {
            [LangEn] = "Age of Empires III does not record games by default, and the recording is the only place the launcher can read who won from. Without it a match cannot be rated.",
            [LangEs] = "Age of Empires III no graba las partidas por defecto, y la grabación es el único sitio del que el launcher puede leer quién ganó. Sin ella, la partida no cuenta para el ELO.",
        },
        // --- Generic action labels. The redesign puts the verb on a fixed-width button
        // and the subject in the row title beside it, so the labels are one word and
        // shared across rows instead of each button restating its whole sentence.
        ["BtnClear"] = new()
        {
            [LangEn] = "Clear",
            [LangEs] = "Vaciar",
        },
        ["BtnDelete"] = new()
        {
            [LangEn] = "Delete",
            [LangEs] = "Borrar",
        },
        ["BtnOpen"] = new()
        {
            [LangEn] = "Open",
            [LangEs] = "Abrir",
        },
        ["BtnCheck"] = new()
        {
            [LangEn] = "Check",
            [LangEs] = "Comprobar",
        },
        // Not "BtnInstall": that key already exists further down as the dashboard CTA
        // ("INSTALL MOD"), and this dictionary is built with the indexer, so the later
        // entry silently wins and the button here read "INSTALL MOD".
        ["BtnInstallHere"] = new()
        {
            [LangEn] = "Install",
            [LangEs] = "Instalar",
        },
        ["BtnView"] = new()
        {
            [LangEn] = "View",
            [LangEs] = "Ver",
        },
        // Same collision as BtnInstallHere — "BtnUninstall" is taken.
        ["BtnUninstallHere"] = new()
        {
            [LangEn] = "Uninstall…",
            [LangEs] = "Desinstalar…",
        },
        // --- Row titles for ADVANCED (4c). They used to be the button captions.
        ["DlgSettingsAdvIconsTitle"] = new()
        {
            [LangEn] = "Clear the icon cache",
            [LangEs] = "Vaciar la caché de iconos",
        },
        ["DlgSettingsAdvTempTitle"] = new()
        {
            [LangEn] = "Delete temporary files",
            [LangEs] = "Borrar archivos temporales",
        },
        ["DlgSettingsAdvDataFolderTitle"] = new()
        {
            [LangEn] = "Data folder",
            [LangEs] = "Carpeta de datos",
        },
        ["DlgSettingsAdvVersionTitle"] = new()
        {
            [LangEn] = "Launcher version",
            [LangEs] = "Versión del launcher",
        },
        ["DlgSettingsAdvInstallTitle"] = new()
        {
            [LangEn] = "Install on this PC",
            [LangEs] = "Instalar en este PC",
        },
        ["DlgSettingsAdvTelemetryTitle"] = new()
        {
            [LangEn] = "Local diagnostic log",
            [LangEs] = "Registro local de diagnóstico",
        },
        ["DlgSettingsAdvPrivacyTitle"] = new()
        {
            [LangEn] = "Privacy policy",
            [LangEs] = "Política de privacidad",
        },
        ["DlgSettingsAdvPrivacyDesc"] = new()
        {
            [LangEn] = "The launcher collects no analytics. Multiplayer sends the server only what those features need.",
            [LangEs] = "El launcher no recoge analíticas. Multijugador envía al servidor solo lo que esas funciones necesitan.",
        },
        ["DlgSettingsAdvUninstallTitle"] = new()
        {
            [LangEn] = "Uninstall from this PC",
            [LangEs] = "Desinstalar de este PC",
        },
        // --- The developer block, folded until the switch in GENERAL is on.
        ["DlgSettingsAdvDevTitle"] = new()
        {
            [LangEn] = "Mod and translation tools",
            [LangEs] = "Herramientas de mod y traducción",
        },
        ["DlgSettingsAdvDevDesc"] = new()
        {
            [LangEn] = "Test a mod.json, package a translation, generate incremental patches.",
            [LangEs] = "Probar un mod.json, empaquetar una traducción, generar parches incrementales.",
        },
        // Shown ONLY after a switch-off made in this window — never on a cold open. It has
        // to name the way back, because the panel it was in has just vanished and the gesture
        // is deliberately undiscoverable.
        ["DlgSettingsAdvDevOff"] = new()
        {
            [LangEn] = "Developer tools hidden. To bring them back, click the version number "
                     + "at the bottom of the list on the left seven times.",
            [LangEs] = "Herramientas de desarrollo ocultas. Para recuperarlas, haz clic siete "
                     + "veces sobre el número de versión, abajo en la lista de la izquierda.",
        },
        // --- MODS AND UPDATES (4d).
        ["BtnChange"] = new()
        {
            [LangEn] = "Change",
            [LangEs] = "Cambiar",
        },
        ["BtnRefresh"] = new()
        {
            [LangEn] = "Refresh",
            [LangEs] = "Refrescar",
        },
        ["DlgSettingsUpdAutoTitle"] = new()
        {
            [LangEn] = "Update mods automatically",
            [LangEs] = "Actualizar los mods automáticamente",
        },
        ["DlgSettingsUpdAutoDesc"] = new()
        {
            [LangEn] = "If your version does not match the host's, you cannot join their room.",
            [LangEs] = "Si tu versión no coincide con la del anfitrión, no puedes entrar en su sala.",
        },
        ["DlgSettingsUpdDeltaTitle"] = new()
        {
            [LangEn] = "Download only what changed",
            [LangEs] = "Descargar solo lo que cambió",
        },
        ["DlgSettingsUpdDeltaDesc"] = new()
        {
            [LangEn] = "Incremental patches instead of the whole mod. Turn it off if an update fails.",
            [LangEs] = "Parches incrementales en vez del mod entero. Desactívalo si una actualización falla.",
        },
        ["DlgSettingsUpdChannelTitle"] = new()
        {
            [LangEn] = "Channel",
            [LangEs] = "Canal",
        },
        ["DlgSettingsUpdChannelDesc"] = new()
        {
            [LangEn] = "Beta gets patches sooner, with more risk of bugs.",
            [LangEs] = "Beta recibe antes los parches, con más riesgo de fallos.",
        },
        ["DlgSettingsUpdChannelStable"] = new()
        {
            [LangEn] = "Stable",
            [LangEs] = "Estable",
        },
        ["DlgSettingsUpdChannelBeta"] = new()
        {
            [LangEn] = "Beta",
            [LangEs] = "Beta",
        },
        ["DlgSettingsUpdLimitTitle"] = new()
        {
            [LangEn] = "Download limit",
            [LangEs] = "Límite de descarga",
        },
        ["DlgSettingsUpdLimitDesc"] = new()
        {
            [LangEn] = "So an update does not wreck your ping in the middle of a match.",
            [LangEs] = "Para que una actualización no te tire el ping en mitad de una partida.",
        },
        ["DlgSettingsUpdLimitNone"] = new()
        {
            [LangEn] = "No limit",
            [LangEs] = "Sin límite",
        },
        ["DlgSettingsGroupInstalledMods"] = new()
        {
            [LangEn] = "INSTALLED MODS",
            [LangEs] = "MODS INSTALADOS",
        },
        ["DlgSettingsModsUpToDate"] = new()
        {
            [LangEn] = "Up to date",
            [LangEs] = "Al día",
        },
        ["DlgSettingsModsUpdate"] = new()
        {
            [LangEn] = "Update",
            [LangEs] = "Actualizar",
        },
        ["DlgSettingsModsNone"] = new()
        {
            [LangEn] = "No mods installed yet.",
            [LangEs] = "Todavía no tienes mods instalados.",
        },
        ["DlgSettingsCatalogSourceTitle"] = new()
        {
            [LangEn] = "Catalogue source",
            [LangEs] = "Origen del catálogo",
        },
        ["DlgSettingsCatalogRefreshTitle"] = new()
        {
            [LangEn] = "Refresh the catalogue",
            [LangEs] = "Refrescar el catálogo",
        },
        ["DlgSettingsCatalogCount"] = new()
        {
            [LangEn] = "{0} mods available",
            [LangEs] = "{0} mods disponibles",
        },
        ["DlgSettingsVerifyTitle"] = new()
        {
            [LangEn] = "Verify the signature of downloads",
            [LangEs] = "Verificar la firma de las descargas",
        },
        ["DlgSettingsVerifyDesc"] = new()
        {
            [LangEn] = "Checks each file's hash before installing it.",
            [LangEs] = "Comprueba el hash de cada archivo antes de instalarlo.",
        },
        // --- GAMES (4b).
        ["DlgSettingsGroupRanking"] = new()
        {
            [LangEn] = "RANKING",
            [LangEs] = "CLASIFICACIÓN",
        },
        ["DlgSettingsRecOffTitle"] = new()
        {
            [LangEn] = "AoE3 recording is switched off",
            [LangEs] = "La grabación de AoE3 está desactivada",
        },
        ["DlgSettingsRecOffBody"] = new()
        {
            [LangEn] = "Read from {0}'s profile. Your next matches will not count.",
            [LangEs] = "Leído del perfil de {0}. Tus próximas partidas no contarán.",
        },
        ["DlgSettingsRecOffAction"] = new()
        {
            [LangEn] = "Switch it on",
            [LangEs] = "Activarla ahora",
        },
        ["DlgSettingsShowEloTitle"] = new()
        {
            [LangEn] = "Show my rating to other players",
            [LangEs] = "Mostrar mi ELO a otros jugadores",
        },
        ["DlgSettingsShowEloDesc"] = new()
        {
            [LangEn] = "It shows in rooms, the lobby and the ranking. Turning it off leaves the public table.",
            [LangEs] = "Aparece en las salas, el lobby y la clasificación. Al desactivarlo sales de la tabla pública.",
        },
        ["DlgSettingsReplayUpTitle"] = new()
        {
            [LangEn] = "Upload the replay when a match ends",
            [LangEs] = "Subir la repetición al terminar",
        },
        ["DlgSettingsReplayUpDesc"] = new()
        {
            [LangEn] = "Lets a match be reviewed and a disputed result settled.",
            [LangEs] = "Permite revisar la partida y resolver resultados en disputa.",
        },
        ["DlgSettingsReplayAsk"] = new()
        {
            [LangEn] = "Ask",
            [LangEs] = "Preguntar",
        },
        ["DlgSettingsReplayAlways"] = new()
        {
            [LangEn] = "Always",
            [LangEs] = "Siempre",
        },
        ["DlgSettingsReplayNever"] = new()
        {
            [LangEn] = "Never",
            [LangEs] = "Nunca",
        },
        ["DlgLauncherSettingsSectionGames"] = new()
        {
            [LangEn] = "Games",
            [LangEs] = "Partidas",
        },
        ["DlgLauncherSettingsSectionModsUpdates"] = new()
        {
            [LangEn] = "Mods and updates",
            [LangEs] = "Mods y actualizaciones",
        },
        ["DlgLauncherSettingsSectionAdvanced"] = new()
        {
            [LangEn] = "Advanced",
            [LangEs] = "Avanzado",
        },
        ["DlgSettingsSearchPlaceholder"] = new()
        {
            [LangEn] = "Search settings",
            [LangEs] = "Buscar ajuste",
        },
        // The mod window's own placeholder. Not the one above: half of what you look for
        // there is a folder, a language pack or an addon, and "ajuste" names none of those.
        ["DlgModPropsSearchPlaceholder"] = new()
        {
            [LangEn] = "Search this mod",
            [LangEs] = "Buscar en este mod",
        },
        // Shown INSTEAD of the sections when a search matches nothing anywhere. Without it
        // the query just emptied the page, and a search that found nothing looked exactly
        // like one that had broken.
        ["DlgSettingsSearchNoResults"] = new()
        {
            [LangEn] = "Nothing matches your search.",
            [LangEs] = "No hay nada que coincida con tu búsqueda.",
        },
        // Replaces the "(recommended)" that used to be glued onto a label and the
        // defensive paragraph that used to sit under it.
        ["DlgSettingsBadgeRecommended"] = new()
        {
            [LangEn] = "RECOMMENDED",
            [LangEs] = "RECOMENDADO",
        },
        ["DlgSettingsGroupStartup"] = new()
        {
            [LangEn] = "STARTUP",
            [LangEs] = "INICIO",
        },
        ["DlgSettingsGroupNotices"] = new()
        {
            [LangEn] = "NOTIFICATIONS",
            [LangEs] = "AVISOS",
        },
        ["DlgSettingsGroupConnection"] = new()
        {
            [LangEn] = "CONNECTION",
            [LangEs] = "CONEXIÓN",
        },
        ["DlgSettingsGroupRecording"] = new()
        {
            [LangEn] = "RECORDING",
            [LangEs] = "GRABACIÓN",
        },
        ["DlgSettingsSoundTest"] = new()
        {
            [LangEn] = "Test",
            [LangEs] = "Probar",
        },
        ["DlgSettingsSoundTestTip"] = new()
        {
            [LangEn] = "Plays the notification sound so you can hear it at your current volume.",
            [LangEs] = "Reproduce el sonido de notificación para que lo escuches a tu volumen actual.",
        },
        ["DlgSettingsLanguageInstant"] = new()
        {
            [LangEn] = "Applies instantly.",
            [LangEs] = "Se aplica al instante.",
        },
        // The footer counts what is pending instead of showing a permanent
        // Cancel/Save pair, so an untouched window has nothing to decide.
        // The footer line when nothing is pending. It is a claim about behaviour, so
        // it is only allowed to exist because ApplyInstantSettings makes it true; the
        // two settings that can be REFUSED are counted by the two keys below instead.
        ["DlgSettingsAppliesInstantly"] = new()
        {
            [LangEn] = "Changes apply instantly.",
            [LangEs] = "Los cambios se aplican al instante.",
        },
        ["DlgSettingsUnsavedOne"] = new()
        {
            [LangEn] = "1 unsaved change",
            [LangEs] = "1 cambio sin guardar",
        },
        ["DlgSettingsUnsavedMany"] = new()
        {
            [LangEn] = "{0} unsaved changes",
            [LangEs] = "{0} cambios sin guardar",
        },
        ["BtnDiscard"] = new()
        {
            [LangEn] = "Discard",
            [LangEs] = "Descartar",
        },
        ["DlgLauncherSettingsLanguageLabel"] = new()
        {
            [LangEn] = "Launcher language",
            [LangEs] = "Idioma del launcher",
        },
        // (Theme picker strings removed — see LauncherSettingsDialog.xaml.
        //  The launcher is dorado-imperial dark-only now.)
        ["NewsReadMore"] = new()
        {
            [LangEn] = "Read more →",
            [LangEs] = "Leer más →",
        },
        // Sidebar nav labels (Fase 2 redesign). Keys kept as TopTab* for
        // backward compat with the existing ApplyStrings wiring; the
        // labels now read "DASHBOARD / CATALOG / ..." to match the
        // Stitch sidebar instead of the old "PLAY / MODS" horizontal
        // tab strip.
        ["TopTabPlay"] = new()
        {
            [LangEn] = "LIBRARY",
            [LangEs] = "BIBLIOTECA",
        },
        ["TopTabMods"] = new()
        {
            [LangEn] = "WORKSHOP",
            [LangEs] = "WORKSHOP",
        },
        ["TopTabMultiplayer"] = new()
        {
            [LangEn] = "MULTIPLAYER",
            [LangEs] = "MULTIJUGADOR",
        },
        // "SWITCH GAME" pill below the cinema-dashboard PLAY button. Opens
        // a popup to switch the active mod/game (built-in profiles + the mods
        // the user added via the Workshop). Player-facing wording ("juego")
        // chosen over "mods" so newcomers understand it switches what plays.
        ["DashboardChangeMod"] = new()
        {
            [LangEn] = "SWITCH GAME",
            [LangEs] = "CAMBIAR JUEGO",
        },
        // Per-row recency hint in the SWITCH GAME popup. {0} is a compact single-unit
        // duration from RoomAgeFormat.Coarse ("5 min", "2 h", "3 d") — the units read
        // the same in both languages, so only this wrapper is translated.
        ["ModSwitchPlayedAgo"] = new()
        {
            [LangEn] = "Played {0} ago",
            [LangEs] = "Jugado hace {0}",
        },
        ["ModSwitchNeverPlayed"] = new()
        {
            [LangEn] = "Not played yet",
            [LangEs] = "Sin jugar",
        },
        // Tooltip on the hero's active-copy chip (shown only with 2+ installed copies).
        ["DashboardActiveCopyTooltip"] = new()
        {
            [LangEn] = "Active copy — click to switch",
            [LangEs] = "Copia activa — clic para cambiar",
        },

        // --- Dashboard mod-button tooltips (hover detail for newcomers) ---
        ["TipCtaPlay"] = new()
        {
            [LangEn] = "Launch the mod and start playing.",
            [LangEs] = "Abre el mod y empieza a jugar.",
        },
        ["TipCtaInstall"] = new()
        {
            [LangEn] = "Download and install this mod on your PC. Needs your Age of Empires III installed first.",
            [LangEs] = "Descarga e instala este mod en tu PC. Necesita tu Age of Empires III ya instalado.",
        },
        ["TipCtaUpdate"] = new()
        {
            [LangEn] = "Install the latest version. Keep your mod current to play online with everyone.",
            [LangEs] = "Instala la última versión. Mantén tu mod al día para jugar online con todos.",
        },
        ["TipCtaStop"] = new()
        {
            [LangEn] = "The game is running. Close it to play again.",
            [LangEs] = "El juego está abierto. Ciérralo para volver a jugar.",
        },
        ["TipGearButton"] = new()
        {
            [LangEn] = "Mod options: verify files, repair, change folder, view logs, uninstall…",
            [LangEs] = "Opciones del mod: verificar archivos, reparar, cambiar carpeta, ver registros, desinstalar…",
        },
        ["TipChangeMod"] = new()
        {
            [LangEn] = "Switch between your installed mods.",
            [LangEs] = "Cambia entre tus mods instalados.",
        },
        ["TipSearchInstall"] = new()
        {
            [LangEn] = "Already have this mod? Search your PC for it instead of downloading again.",
            [LangEs] = "¿Ya tienes este mod? Búscalo en tu PC en vez de descargarlo de nuevo.",
        },
        ["TipPauseResume"] = new()
        {
            [LangEn] = "Pause or resume the download.",
            [LangEs] = "Pausar o reanudar la descarga.",
        },
        ["TipCancel"] = new()
        {
            [LangEn] = "Cancel this operation.",
            [LangEs] = "Cancelar esta operación.",
        },

        // --- Mod Properties dialog button tooltips ---
        ["TipMpInstallVersion"] = new()
        {
            [LangEn] = "Install the version selected above. Useful to go back to an older version.",
            [LangEs] = "Instala la versión elegida arriba. Útil para volver a una versión anterior.",
        },
        ["TipMpOpenFolder"] = new()
        {
            [LangEn] = "Open the mod's install folder in Explorer.",
            [LangEs] = "Abre la carpeta donde está instalado el mod en el Explorador.",
        },
        ["TipMpChangeModFolder"] = new()
        {
            [LangEn] = "Point the launcher at the mod's folder if you moved it or installed it elsewhere.",
            [LangEs] = "Indícale al launcher dónde está la carpeta del mod si la moviste o la instalaste en otro lado.",
        },
        ["TipMpAddExistingFolder"] = new()
        {
            [LangEn] = "Register a mod folder you already have on disk, without downloading it again.",
            [LangEs] = "Registra una carpeta de mod que ya tienes en el disco, sin volver a descargarla.",
        },
        ["TipMpShareDiagnostics"] = new()
        {
            [LangEn] = "Bundle the logs into a zip to attach to a bug report on Discord.",
            [LangEs] = "Junta los registros en un zip para adjuntar a un reporte de error en Discord.",
        },

        // --- Workshop (mods browser) tooltips ---
        ["TipWsRefreshCatalog"] = new()
        {
            [LangEn] = "Get the newest list of community mods.",
            [LangEs] = "Trae la lista más nueva de mods de la comunidad.",
        },
        ["TipWsPublish"] = new()
        {
            [LangEn] = "For mod authors: submit your mod to the community catalog.",
            [LangEs] = "Para autores de mods: envía tu mod al catálogo de la comunidad.",
        },
        ["TipWsSubTabMyMods"] = new()
        {
            [LangEn] = "The mods you have installed.",
            [LangEs] = "Los mods que tienes instalados.",
        },
        ["TipWsSubTabCatalog"] = new()
        {
            [LangEn] = "All the mods available to install.",
            [LangEs] = "Todos los mods disponibles para instalar.",
        },
        ["TipWsFilterAll"] = new()
        {
            [LangEn] = "Show every mod.",
            [LangEs] = "Mostrar todos los mods.",
        },
        ["TipWsFilterInstalled"] = new()
        {
            [LangEn] = "Show only the mods you have installed.",
            [LangEs] = "Mostrar solo los mods que tienes instalados.",
        },
        ["TipWsFilterNotInstalled"] = new()
        {
            [LangEn] = "Show only mods you haven't installed yet.",
            [LangEs] = "Mostrar solo los mods que todavía no instalaste.",
        },
        ["TipWsFilterUpdates"] = new()
        {
            [LangEn] = "Show only mods with an update available.",
            [LangEs] = "Mostrar solo los mods con una actualización disponible.",
        },
        ["TipWsFilterCompatible"] = new()
        {
            [LangEn] = "Show only mods compatible with your Age of Empires III.",
            [LangEs] = "Mostrar solo los mods compatibles con tu Age of Empires III.",
        },
        // Header caption for the cinema-dashboard gear popup (the
        // redesigned per-mod settings menu that replaced the legacy
        // WPF ContextMenu with a MODS-style popup + sub-views).
        // Steam-style brand menu — opens when the user clicks the
        // "AOE3 LAUNCHER" wordmark in the top-left of the sidebar.
        // Holds launcher-wide actions (settings dialog, about, exit)
        // — per-mod settings live in the dashboard gear popup, not
        // here.
        ["BrandMenuTitle"] = new()
        {
            [LangEn] = "AOE3 LAUNCHER",
            [LangEs] = "AOE3 LAUNCHER",
        },
        ["BrandMenuLauncherSettings"] = new()
        {
            [LangEn] = "Launcher settings",
            [LangEs] = "Configuración del launcher",
        },
        ["BrandMenuLauncherSettingsSubtitle"] = new()
        {
            [LangEn] = "Language, autostart, notifications…",
            [LangEs] = "Idioma, inicio automático, notificaciones…",
        },
        ["BrandMenuAbout"] = new()
        {
            [LangEn] = "About",
            [LangEs] = "Acerca de",
        },
        ["BrandMenuAboutSubtitle"] = new()
        {
            [LangEn] = "Version and credits",
            [LangEs] = "Versión y créditos",
        },
        // The project's Discord, in the launcher's own menu. It sits between About and Exit
        // because it belongs to the same family — things about the launcher rather than about a
        // mod — and because it is the only always-reachable route to a human.
        ["BrandMenuCommunity"] = new()
        {
            [LangEn] = "Discord community",
            [LangEs] = "Comunidad de Discord",
        },
        ["BrandMenuCommunitySubtitle"] = new()
        {
            [LangEn] = "Support, news and people to play with",
            [LangEs] = "Soporte, novedades y gente con quien jugar",
        },
        // Every place this pill appears is a place where something has already gone wrong, so
        // it asks the player's question back at them rather than advertising a server.
        ["SupportDiscordHelpLabel"] = new()
        {
            [LangEn] = "Need help? Ask on Discord",
            [LangEs] = "¿Necesitas ayuda? Pregunta en Discord",
        },
        ["BrandMenuExit"] = new()
        {
            [LangEn] = "Exit",
            [LangEs] = "Salir",
        },
        ["BrandMenuExitSubtitle"] = new()
        {
            [LangEn] = "Close the launcher",
            [LangEs] = "Cerrar el launcher",
        },
        ["AboutDialogTitle"] = new()
        {
            [LangEn] = "About AoE3 Mod Launcher",
            [LangEs] = "Acerca de AoE3 Mod Launcher",
        },
        ["AboutDialogBody"] = new()
        {
            [LangEn] = "AoE3 Mod Launcher\nVersion {0}\n\nA mod manager for Age of Empires III.\n\nMade by Gorgorito",
            [LangEs] = "AoE3 Mod Launcher\nVersión {0}\n\nGestor de mods para Age of Empires III.\n\nHecho por Gorgorito",
        },
        // Right-click context menu (Steam-style) on mod rows in the
        // MODS popup and Workshop. Each row's right-click opens a
        // ContextMenu with Play / Manage submenu / Favorite toggle
        // / Properties — mirrors the per-game context menu Steam
        // surfaces in its library list.
        ["ModContextRepair"] = new()
        {
            [LangEn] = "Repair install",
            [LangEs] = "Reparar instalación",
        },
        ["ModContextVerify"] = new()
        {
            [LangEn] = "Verify files",
            [LangEs] = "Verificar archivos",
        },
        // ModPropertiesDialog labels (Fase 3b).
        ["ModPropTitle"] = new()
        {
            [LangEn] = "{0} — Properties",
            [LangEs] = "{0} — Propiedades",
        },
        ["ModPropTabGeneral"] = new()
        {
            [LangEn] = "GENERAL",
            [LangEs] = "GENERAL",
        },
        ["ModPropTabLocalFiles"] = new()
        {
            [LangEn] = "LOCAL FILES",
            [LangEs] = "ARCHIVOS LOCALES",
        },
        ["ModPropTabLanguage"] = new()
        {
            [LangEn] = "LANGUAGE",
            [LangEs] = "IDIOMA",
        },

        // -- Addons tab -------------------------------------------------------
        // Optional community overlays (transparent UI, gun smoke, …). Applied
        // through AddonService, which backs up the originals, refuses anything
        // that would break version detection or multiplayer, and re-captures the
        // manifest so "Verify files" stays clean afterwards.
        ["StatusReapplyingAddons"] = new()
        {
            [LangEn] = "Re-applying your addons...",
            [LangEs] = "Volviendo a aplicar tus addons...",
        },
        ["ModPropTabAddons"] = new()
        {
            [LangEn] = "ADDONS",
            [LangEs] = "ADDONS",
        },
        ["AddonsSectionTitle"] = new()
        {
            [LangEn] = "Optional addons",
            [LangEs] = "Addons opcionales",
        },
        // --- STATISTICS tab: the games AoE3 recorded against the AI ---------------
        ["ModPropTabDecks"] = new()
        {
            [LangEn] = "DECKS",
            [LangEs] = "MAZOS",
        },
        ["ModPropTabStats"] = new()
        {
            [LangEn] = "STATISTICS",
            [LangEs] = "ESTADÍSTICAS",
        },
        ["ModPropStatsTitle"] = new()
        {
            [LangEn] = "Games against the AI",
            [LangEs] = "Partidas contra la IA",
        },
        // Says the two things a player will otherwise wonder about: why their multiplayer games
        // are missing, and why the launcher has numbers the game does not show any more.
        ["ModPropStatsHint"] = new()
        {
            [LangEn] = "The game only records these statistics when an AI is playing — that is "
                     + "why the matches above have none. It also forgets them: it keeps the "
                     + "totals of the last game only, so the launcher saves them each time you "
                     + "finish one.",
            [LangEs] = "El juego sólo guarda estas estadísticas cuando juega una IA — por eso las "
                     + "partidas de arriba no las tienen. Además las olvida: conserva los totales "
                     + "de la última partida nada más, por eso el launcher los guarda cada vez "
                     + "que terminas una.",
        },
        ["ModPropHumanGamesTitle"] = new()
        {
            [LangEn] = "Games against players",
            [LangEs] = "Partidas contra jugadores",
        },
        // Says the two things that would otherwise be read as a bug: why there are no numbers
        // here, and why an old match may be missing. Both are the game's doing, not the
        // launcher's.
        ["ModPropHumanGamesHint"] = new()
        {
            [LangEn] = "Read from your own recordings, so matches the launcher's lobby never saw "
                     + "are here too. The game writes no end-of-match statistics unless an AI is "
                     + "playing — no score, no resources, no units, no cards sent — so what a "
                     + "recording can say is who played, as whom, and who lost. Recordings the "
                     + "game names itself are deleted except the newest ten: rename one to keep it.",
            [LangEs] = "Salen de tus propias grabaciones, así que acá también están las partidas "
                     + "que la sala del launcher nunca vio. El juego no escribe estadísticas de "
                     + "fin de partida si no juega una IA — ni puntuación, ni recursos, ni "
                     + "unidades, ni cartas enviadas — así que lo que una grabación puede decir "
                     + "es quién jugó, con qué civilización y quién perdió. Las grabaciones que "
                     + "el juego nombra solo se borran salvo las diez más nuevas: renombra la que "
                     + "quieras conservar.",
        },
        ["ModPropHumanGamesEmpty"] = new()
        {
            [LangEn] = "No recordings of matches against people yet. The game saves one per match "
                     + "while game recording is on.",
            [LangEs] = "Todavía no hay grabaciones de partidas contra personas. El juego guarda "
                     + "una por partida mientras la grabación esté activada.",
        },
        // Third person, and about a PLAYER rather than about you: most recordings a player
        // keeps are somebody else's, so "you won" would be false on most of these cards.
        ["ModPropHumanWon"] = new()
        {
            [LangEn] = "Won",
            [LangEs] = "Ganó",
        },
        ["ModPropHumanLost"] = new()
        {
            [LangEn] = "Lost",
            [LangEs] = "Perdió",
        },
        ["ModPropHumanDeckShow"] = new()
        {
            [LangEn] = "See the deck you brought",
            [LangEs] = "Ver el mazo que llevaste",
        },
        ["ModPropHumanDeckLoading"] = new()
        {
            [LangEn] = "Reading...",
            [LangEs] = "Leyendo...",
        },
        // Two claims that would be false if left unsaid: these are the cards of THAT day, and
        // the game never records which of a home city's decks was used, so all of them are here
        // rather than one of them picked.
        ["ModPropHumanDeckNote"] = new()
        {
            [LangEn] = "The cards as they were that day. The game does not record which of that "
                     + "home city's decks you used, so all of them are here.",
            [LangEs] = "Las cartas tal como estaban ese día. El juego no guarda cuál de los mazos "
                     + "de esa ciudad natal usaste, así que están todos.",
        },
        ["ModPropHumanMapUnknown"] = new()
        {
            [LangEn] = "Unknown map",
            [LangEs] = "Mapa desconocido",
        },
        ["ModPropStatsEmpty"] = new()
        {
            [LangEn] = "Nothing yet. Play a game against the AI and it will show up here when you close it.",
            [LangEs] = "Todavía nada. Juega una partida contra la IA y aparecerá acá al cerrarla.",
        },
        ["ModPropStatsWon"] = new() { [LangEn] = "Won", [LangEs] = "Ganaste" },
        ["ModPropStatsLost"] = new() { [LangEn] = "Lost", [LangEs] = "Perdiste" },
        ["ModPropStatsDuration"] = new() { [LangEn] = "{0} min", [LangEs] = "{0} min" },
        ["ModPropStatsShipments"] = new()
        {
            [LangEn] = "{0} shipments",
            [LangEs] = "{0} envíos",
        },
        ["ModPropStatsScore"] = new() { [LangEn] = "Score {0}", [LangEs] = "Puntuación {0}" },
        ["ModPropStatsResources"] = new()
        {
            [LangEn] = "{0} resources gathered",
            [LangEs] = "{0} recursos recolectados",
        },
        ["ModPropStatsXp"] = new() { [LangEn] = "{0} XP", [LangEs] = "{0} XP" },
        ["ModPropStatsUnitCount"] = new() { [LangEn] = "{0} x{1}", [LangEs] = "{0} x{1}" },
        ["ModPropDecksTitle"] = new()
        {
            [LangEn] = "Your home city decks",
            [LangEs] = "Tus mazos de ciudad natal",
        },
        // Says outright that this is what the player BRINGS. A deck holds 25 cards and a match may
        // use five, so letting anyone read it as "cards played" would overstate it by a factor
        // nothing on screen could reveal.
        ["ModPropDecksHint"] = new()
        {
            [LangEn] = "The decks you have built, straight from the game — one per civilization "
                     + "you have played. These are the cards you TAKE into a match, not the ones "
                     + "you ended up sending: the game does not record that anywhere the launcher "
                     + "can read.",
            [LangEs] = "Los mazos que armaste, directo del juego — uno por civilización que hayas "
                     + "jugado. Son las cartas que LLEVAS a la partida, no las que terminaste "
                     + "enviando: eso el juego no lo guarda en ningún lado que el launcher pueda "
                     + "leer.",
        },
        ["ModPropDecksEmpty"] = new()
        {
            [LangEn] = "No decks yet. Open the home city in the game, build one, and it will show up here.",
            [LangEs] = "Todavía no hay mazos. Abre la ciudad natal en el juego, arma uno y aparecerá acá.",
        },
        ["ModPropDecksCardCount"] = new() { [LangEn] = "{0} cards", [LangEs] = "{0} cartas" },
        ["ModPropDecksLevel"] = new() { [LangEn] = "level {0}", [LangEs] = "nivel {0}" },
        ["TipModPropTabStats"] = new()
        {
            [LangEn] = "What you built, sent and gathered in your games against the AI.",
            [LangEs] = "Qué construiste, enviaste y recolectaste en tus partidas contra la IA.",
        },
        ["TipModPropTabDecks"] = new()
        {
            [LangEn] = "The cards in each deck you have built, with their art and what they do.",
            [LangEs] = "Las cartas de cada mazo que armaste, con su arte y lo que hacen.",
        },
        ["AddonsSectionHint"] = new()
        {
            [LangEn] = "Small community tweaks — a transparent interface, gun smoke, "
                     + "and the like. They stay applied when the mod updates, and "
                     + "turning one off puts the original files back.",
            [LangEs] = "Retoques pequeños de la comunidad — interfaz transparente, humo "
                     + "de armas y cosas así. Se mantienen cuando el mod se actualiza, y "
                     + "al desactivar uno vuelven los archivos originales.",
        },
        ["AddonsEmptyHint"] = new()
        {
            [LangEn] = "Nothing imported yet. Download an addon and add it with the button above.",
            [LangEs] = "Todavía no importaste ninguno. Descarga un addon y agrégalo con el botón de arriba.",
        },
        ["AddonImportButton"] = new()
        {
            [LangEn] = "Add addon from file…",
            [LangEs] = "Agregar addon desde archivo…",
        },
        ["AddonImportFilter"] = new()
        {
            [LangEn] = "Addon archive (*.zip)|*.zip",
            [LangEs] = "Archivo de addon (*.zip)|*.zip",
        },
        ["AddonEnable"] = new()
        {
            [LangEn] = "Enable",
            [LangEs] = "Activar",
        },
        ["AddonEnabled"] = new()
        {
            [LangEn] = "Enabled",
            [LangEs] = "Activado",
        },
        ["AddonImported"] = new()
        {
            [LangEn] = "Addon added.",
            [LangEs] = "Addon agregado.",
        },
        ["AddonApplied"] = new()
        {
            [LangEn] = "Addon enabled.",
            [LangEs] = "Addon activado.",
        },
        ["AddonDisabled"] = new()
        {
            [LangEn] = "Addon disabled — the original files are back.",
            [LangEs] = "Addon desactivado — los archivos originales volvieron.",
        },
        ["AddonImportFailed"] = new()
        {
            [LangEn] = "That file could not be read as an addon archive.",
            [LangEs] = "No se pudo leer ese archivo como un addon.",
        },
        ["AddonFailed"] = new()
        {
            [LangEn] = "The addon could not be applied. See the logs for details.",
            [LangEs] = "No se pudo aplicar el addon. Revisa los registros para más detalles.",
        },
        ["AddonArchiveMissing"] = new()
        {
            [LangEn] = "The addon's file is missing. Add it again from the original archive.",
            [LangEs] = "Falta el archivo del addon. Vuelve a agregarlo desde el archivo original.",
        },
        ["AddonArchiveEmpty"] = new()
        {
            [LangEn] = "That archive has no files to apply.",
            [LangEs] = "Ese archivo no tiene nada que aplicar.",
        },
        ["AddonConflict"] = new()
        {
            [LangEn] = "Another addon ({0}) already changes the same files. Turn it off first.",
            [LangEs] = "Otro addon ({0}) ya modifica los mismos archivos. Desactívalo primero.",
        },
        // Naming the files matters: "this addon is dangerous" is unactionable,
        // "it replaces data\protoy.xml" tells the user (or its author) exactly why.
        ["AddonRiskBlockedHint"] = new()
        {
            [LangEn] = "Can't be used: it replaces files the launcher needs to identify "
                     + "your version and to let you into multiplayer rooms ({0}).",
            [LangEs] = "No se puede usar: reemplaza archivos que el launcher necesita para "
                     + "identificar tu versión y para dejarte entrar a las salas ({0}).",
        },
        ["AddonRiskSimulationHint"] = new()
        {
            [LangEn] = "Changes game data ({0}). Rooms won't reject you, but a match with "
                     + "players who don't have it can desync.",
            [LangEs] = "Modifica datos del juego ({0}). Las salas no te van a rechazar, pero "
                     + "una partida con jugadores que no lo tengan puede desincronizarse.",
        },
        // --- 5d: the two groups, the state badges and the file counts. -----------
        // The list used to be one undifferentiated stack of cards where nothing said
        // what an addon touches, so "transparent interface" and "replaces 25 files the
        // game compares between players" looked identical.
        ["AddonsGroupCatalog"] = new()
        {
            [LangEn] = "FROM THE CATALOG",
            [LangEs] = "DEL CATÁLOGO",
        },
        ["AddonsGroupCatalogHint"] = new()
        {
            [LangEn] = "enabled in this install",
            [LangEs] = "activados en esta instalación",
        },
        ["AddonsGroupImported"] = new()
        {
            [LangEn] = "IMPORTED",
            [LangEs] = "IMPORTADOS",
        },
        ["AddonsGroupImportedHint"] = new()
        {
            [LangEn] = "available to every mod",
            [LangEs] = "disponibles en todos los mods",
        },
        ["AddonBadgeActive"] = new()
        {
            [LangEn] = "ACTIVE",
            [LangEs] = "ACTIVO",
        },
        ["AddonBadgeCosmetic"] = new()
        {
            [LangEn] = "COSMETIC",
            [LangEs] = "COSMÉTICO",
        },
        ["AddonBadgeMultiplayerRisk"] = new()
        {
            [LangEn] = "MULTIPLAYER RISK",
            [LangEs] = "RIESGO MULTIJUGADOR",
        },
        ["AddonBadgeBlocked"] = new()
        {
            [LangEn] = "BLOCKED",
            [LangEs] = "BLOQUEADO",
        },
        ["AddonBadgeInstaller"] = new()
        {
            [LangEn] = "INSTALLER",
            [LangEs] = "INSTALADOR",
        },
        ["AddonFileCount"] = new()
        {
            [LangEn] = "{0} files",
            [LangEs] = "{0} archivos",
        },
        ["AddonXmbCount"] = new()
        {
            [LangEn] = "{0} of them .xmb",
            [LangEs] = "{0} de ellos .xmb",
        },
        ["AddonDataCount"] = new()
        {
            [LangEn] = "{0} inside data\\",
            [LangEs] = "{0} dentro de data\\",
        },
        ["AddonInstallerNote"] = new()
        {
            [LangEn] = "Ships as a Windows installer. The launcher runs it in a temporary "
                     + "folder and applies the result; Windows will ask you to confirm.",
            [LangEs] = "Se distribuye como instalador de Windows. El launcher lo ejecuta en "
                     + "una carpeta temporal y aplica el resultado; Windows pedirá tu "
                     + "confirmación.",
        },
        ["AddonEnableAnyway"] = new()
        {
            [LangEn] = "Enable anyway...",
            [LangEs] = "Activar igual...",
        },
        ["AddonsFooterNote"] = new()
        {
            [LangEn] = "Catalog addons are checked against their SHA-256 before anything is "
                     + "written. Imported ones are copied into the launcher's folder so they "
                     + "can be re-applied after every update.",
            [LangEs] = "Los del catálogo se verifican con su SHA-256 antes de escribir nada. "
                     + "Los importados se copian a la carpeta del launcher para poder volver "
                     + "a aplicarlos tras cada actualización.",
        },
        ["AddonDownloadAndEnable"] = new()
        {
            [LangEn] = "Download and enable",
            [LangEs] = "Descargar y activar",
        },
        ["AddonDisable"] = new()
        {
            [LangEn] = "Disable",
            [LangEs] = "Desactivar",
        },
        ["AddonOpenPage"] = new()
        {
            [LangEn] = "Open its page",
            [LangEs] = "Abrir su página",
        },
        ["AddonSourcePage"] = new()
        {
            [LangEn] = "Page",
            [LangEs] = "Página",
        },
        ["AddonDownloading"] = new()
        {
            [LangEn] = "Downloading {0}...",
            [LangEs] = "Descargando {0}...",
        },
        ["AddonDownloadFailed"] = new()
        {
            [LangEn] = "The download failed. Its page may have changed — open it and "
                     + "add the file by hand.",
            [LangEs] = "La descarga falló. Puede que su página haya cambiado — ábrela y "
                     + "agrega el archivo a mano.",
        },
        ["AddonAppliedSkipped"] = new()
        {
            [LangEn] = "Addon enabled. Not copied into the game: {0} — the launcher never "
                     + "installs programs, only game files.",
            [LangEs] = "Addon activado. No se copió al juego: {0} — el launcher nunca "
                     + "instala programas, solo archivos del juego.",
        },
        ["AddonNeedsAdmin"] = new()
        {
            [LangEn] = "This game folder can only be changed with administrator rights. "
                     + "Close the launcher and reopen it as administrator to use addons here.",
            [LangEs] = "Esta carpeta del juego solo se puede modificar con permisos de "
                     + "administrador. Cierra el launcher y ábrelo como administrador para "
                     + "usar addons aquí.",
        },
        ["AddonRunCancelled"] = new()
        {
            [LangEn] = "You declined the permission prompt, so nothing was changed.",
            [LangEs] = "Rechazaste el permiso, así que no se cambió nada.",
        },
        ["AddonCancelled"] = new()
        {
            [LangEn] = "Cancelled — nothing was changed.",
            [LangEs] = "Cancelado: no se cambió nada.",
        },
        ["AddonUnpacking"] = new()
        {
            [LangEn] = "Unpacking {0}...",
            [LangEs] = "Desempaquetando {0}...",
        },
        ["AddonRunInstallerTitle"] = new()
        {
            [LangEn] = "Unpack this addon's installer?",
            [LangEs] = "¿Desempaquetar el instalador de este addon?",
        },
        ["AddonRunInstallerBody"] = new()
        {
            [LangEn] = "{0} is distributed as an installer by its author, so the launcher has "
                     + "to run it to get at the files.\n\nIt runs into a temporary folder, "
                     + "never into your game. The launcher then applies the files the normal "
                     + "way, so they are backed up and can be undone.\n\nWindows will ask you "
                     + "for permission first, because that installer requests it.\n\nContinue?",
            [LangEs] = "{0} lo distribuye su autor como instalador, así que el launcher tiene "
                     + "que ejecutarlo para obtener los archivos.\n\nSe ejecuta hacia una "
                     + "carpeta temporal, nunca sobre tu juego. Después el launcher aplica los "
                     + "archivos de la forma normal, con respaldo y posibilidad de deshacer."
                     + "\n\nWindows te va a pedir permiso primero, porque ese instalador lo "
                     + "exige.\n\n¿Continuar?",
        },
        ["AddonUnpackFailed"] = new()
        {
            [LangEn] = "The installer could not be unpacked automatically. Open its page and "
                     + "install it by hand instead.",
            [LangEs] = "No se pudo desempaquetar el instalador automáticamente. Abre su página "
                     + "e instálalo a mano.",
        },
        ["AddonInstallerMissing"] = new()
        {
            [LangEn] = "The download did not contain an installer.",
            [LangEs] = "La descarga no contenía ningún instalador.",
        },
        ["AddonMultiplayerConfirmTitle"] = new()
        {
            [LangEn] = "This addon can affect multiplayer",
            [LangEs] = "Este addon puede afectar el multijugador",
        },
        ["AddonVersionMatchConfirmBody"] = new()
        {
            [LangEn] = "This addon replaces {0} precompiled game files (.xmb). Age of "
                     + "Empires III checks those when matching versions between players, "
                     + "so you may not be able to play with people who don't have the "
                     + "addon. The launcher cannot detect this, which is why you are being "
                     + "asked now.\n\nEnable it anyway?",
            [LangEs] = "Este addon reemplaza {0} archivos precompilados del juego (.xmb). "
                     + "Age of Empires III los usa para comparar versiones entre jugadores, "
                     + "así que puede que no puedas jugar con quien no tenga el addon. El "
                     + "launcher no puede detectarlo, por eso te preguntamos ahora."
                     + "\n\n¿Lo activas de todos modos?",
        },
        ["AddonSimulationConfirmTitle"] = new()
        {
            [LangEn] = "This addon can break matches",
            [LangEs] = "Este addon puede romper partidas",
        },
        ["AddonSimulationConfirmBody"] = new()
        {
            [LangEn] = "This addon changes game data, not just visuals. You will still be "
                     + "able to join rooms — the launcher can't detect this kind of change "
                     + "— but a match with players who don't have the addon may desync.\n\n"
                     + "Enable it anyway?",
            [LangEs] = "Este addon modifica datos del juego, no solo lo visual. Vas a poder "
                     + "entrar a las salas igual — el launcher no puede detectar este tipo de "
                     + "cambio — pero una partida con jugadores que no tengan el addon puede "
                     + "desincronizarse.\n\n¿Lo activas de todos modos?",
        },
        ["ModPropName"] = new()
        {
            [LangEn] = "Name",
            [LangEs] = "Nombre",
        },
        ["ModPropAuthor"] = new()
        {
            [LangEn] = "Author",
            [LangEs] = "Autor",
        },
        ["ModPropVersion"] = new()
        {
            [LangEn] = "Installed version",
            [LangEs] = "Versión instalada",
        },
        ["ModPropWebsite"] = new()
        {
            [LangEn] = "Website",
            [LangEs] = "Sitio web",
        },
        ["ModPropOpenFolder"] = new()
        {
            [LangEn] = "Open folder",
            [LangEs] = "Abrir carpeta",
        },
        ["ModPropNotInstalled"] = new()
        {
            [LangEn] = "(not installed)",
            [LangEs] = "(no instalado)",
        },
        ["ModPropVersionUnknown"] = new()
        {
            [LangEn] = "installed — version not verified",
            [LangEs] = "instalado — versión sin verificar",
        },
        ["ModPropStockVersion"] = new()
        {
            [LangEn] = "detected — ready to play",
            [LangEs] = "detectado — listo para jugar",
        },
        ["ModPropNoTranslations"] = new()
        {
            [LangEn] = "This mod ships no translation packs.",
            [LangEs] = "Este mod no incluye paquetes de traducción.",
        },
        ["ModPropLanguageCurrent"] = new()
        {
            [LangEn] = "Current language",
            [LangEs] = "Idioma actual",
        },
        // Properties dialog expansion — folds the old SETTINGS
        // popup into the dialog so the gear button has a single
        // destination for all per-mod actions.
        ["ModPropTabUserData"] = new()
        {
            [LangEn] = "USER DATA",
            [LangEs] = "DATOS",
        },
        ["ModPropCheckUpdates"] = new()
        {
            [LangEn] = "Check for updates",
            [LangEs] = "Buscar actualizaciones",
        },
        ["ModPropStayOnVersion"] = new()
        {
            [LangEn] = "Stay on this version (v{0}) — pause update prompts",
            [LangEs] = "Quedarme en esta versión (v{0}) — pausar avisos de actualización",
        },
        ["ModPropStayOnVersionHint"] = new()
        {
            [LangEn] = "The launcher stops offering you updates for this mod.",
            [LangEs] = "El launcher deja de ofrecerte actualizaciones de este mod.",
        },
        ["ModPropVersionSection"] = new()
        {
            // UPPERCASE: it is a SetGroupLabel, and every other one in this window
            // (ACTUALIZACIONES, RUTAS, MANTENIMIENTO) carries its own case.
            [LangEn] = "VERSION",
            [LangEs] = "VERSIÓN",
        },
        ["ModPropVersionHint"] = new()
        {
            [LangEn] = "Install or roll back to a specific published version. Older versions may lack fixes and can break multiplayer compatibility with players on the recommended version.",
            [LangEs] = "Instala o vuelve a una versión publicada específica. Las versiones anteriores pueden no tener correcciones y romper la compatibilidad multijugador con quienes usan la recomendada.",
        },
        ["ModPropVersionInstallBtn"] = new()
        {
            [LangEn] = "Install this version",
            [LangEs] = "Instalar esta versión",
        },
        ["ModPropVersionsLoading"] = new()
        {
            [LangEn] = "Loading versions…",
            [LangEs] = "Cargando versiones…",
        },
        ["ModPropVersionsFailed"] = new()
        {
            [LangEn] = "Couldn't load the version list. Check your connection and try again.",
            [LangEs] = "No se pudo cargar la lista de versiones. Revisa tu conexión e inténtalo de nuevo.",
        },
        ["ModPropVersionsNone"] = new()
        {
            [LangEn] = "No published versions found.",
            [LangEs] = "No se encontraron versiones publicadas.",
        },
        ["ModPropVersionInstalled"] = new()
        {
            [LangEn] = "installed",
            [LangEs] = "instalada",
        },
        ["ModPropVersionRecommended"] = new()
        {
            [LangEn] = "recommended",
            [LangEs] = "recomendada",
        },
        ["ModPropVersionPrerelease"] = new()
        {
            [LangEn] = "pre-release",
            [LangEs] = "preliminar",
        },
        ["ModPropVersionAlready"] = new()
        {
            [LangEn] = "That version is already installed.",
            [LangEs] = "Esa versión ya está instalada.",
        },
        ["ModPropChecking"] = new()
        {
            [LangEn] = "Checking for updates…",
            [LangEs] = "Buscando actualizaciones…",
        },
        ["ModPropUpToDate"] = new()
        {
            [LangEn] = "You're up to date.",
            [LangEs] = "Estás al día.",
        },
        ["ModPropUpdateAvailable"] = new()
        {
            [LangEn] = "An update is available — open the launcher to install it.",
            [LangEs] = "Hay una actualización disponible — vuelve al launcher para instalarla.",
        },
        ["ModPropCheckNotInstalled"] = new()
        {
            [LangEn] = "This mod isn't installed yet.",
            [LangEs] = "Este mod aún no está instalado.",
        },
        ["ModPropCheckFailed"] = new()
        {
            [LangEn] = "Couldn't check for updates. See the logs for details.",
            [LangEs] = "No se pudo buscar actualizaciones. Revisa los registros para más detalles.",
        },
        // --- Update-state panel (GENERAL). TITLE and BODY are two different sentences.
        // They used to be the same one: the title was assigned once, unconditionally, to
        // ModPropUpToDate, so the panel read "You're up to date." twice and stayed green
        // over a mod with newer releases published. Every state below owns both halves.
        ["ModPropUpToDateBody"] = new()
        {
            [LangEn] = "You have {0}, the latest published version.",
            [LangEs] = "Tienes la {0}, la última versión publicada.",
        },
        ["ModPropUpdateAvailableTitle"] = new()
        {
            [LangEn] = "Update available",
            [LangEs] = "Actualización disponible",
        },
        ["ModPropUpdateAvailableBody"] = new()
        {
            [LangEn] = "{0} has been published. You have {1}.",
            [LangEs] = "Se ha publicado la {0}. Tú tienes la {1}.",
        },
        ["ModPropUpdateUnknownTitle"] = new()
        {
            [LangEn] = "Version not verified",
            [LangEs] = "Versión sin verificar",
        },
        ["ModPropUpdateUnknownBody"] = new()
        {
            [LangEn] = "The mod is installed, but the launcher never recorded which version. Installing {0} will set it.",
            [LangEs] = "El mod está instalado, pero el launcher nunca anotó qué versión es. Instalar la {0} lo dejará anotado.",
        },
        ["ModPropUpdatePausedTitle"] = new()
        {
            [LangEn] = "Updates paused",
            [LangEs] = "Actualizaciones en pausa",
        },
        ["ModPropUpdatePausedBody"] = new()
        {
            [LangEn] = "{0} is published, but you chose to stay on {1}. Turn the switch below off to be offered it.",
            [LangEs] = "Está publicada la {0}, pero elegiste quedarte en la {1}. Apaga el interruptor de abajo para que se te ofrezca.",
        },
        ["ModPropNotInstalledTitle"] = new()
        {
            [LangEn] = "Not installed",
            [LangEs] = "Sin instalar",
        },
        ["ModPropCheckFailedTitle"] = new()
        {
            [LangEn] = "Couldn't check",
            [LangEs] = "No se pudo comprobar",
        },
        ["ModPropCheckingTitle"] = new()
        {
            [LangEn] = "Checking…",
            [LangEs] = "Comprobando…",
        },
        ["ModPropOpenAoE3Folder"] = new()
        {
            [LangEn] = "Open AoE3 folder",
            [LangEs] = "Abrir carpeta AoE3",
        },
        ["ModPropChangeModFolder"] = new()
        {
            [LangEn] = "Change mod folder",
            [LangEs] = "Cambiar carpeta del mod",
        },
        ["ModPropChangeAoE3Folder"] = new()
        {
            [LangEn] = "Change AoE3 folder",
            [LangEs] = "Cambiar carpeta AoE3",
        },
        ["ModPropViewLogs"] = new()
        {
            [LangEn] = "View logs",
            [LangEs] = "Ver registros",
        },
        ["ModPropShareDiagnostics"] = new()
        {
            [LangEn] = "📤 Share diagnostics",
            [LangEs] = "📤 Compartir diagnóstico",
        },
        ["ModPropShareDiagnosticsSaveTitle"] = new()
        {
            [LangEn] = "Save diagnostic file",
            [LangEs] = "Guardar archivo de diagnóstico",
        },
        ["ModPropShareDiagnosticsFailed"] = new()
        {
            [LangEn] = "Could not create the diagnostics file: {0}",
            [LangEs] = "No se pudo crear el archivo de diagnóstico: {0}",
        },
        ["ModPropDangerZone"] = new()
        {
            [LangEn] = "DANGER ZONE",
            [LangEs] = "ZONA PELIGROSA",
        },
        ["ModPropUninstall"] = new()
        {
            [LangEn] = "Uninstall mod…",
            [LangEs] = "Desinstalar mod…",
        },
        ["ModPropOpenUserDataFolder"] = new()
        {
            [LangEn] = "Open user data folder",
            [LangEs] = "Abrir carpeta de datos",
        },
        ["ModPropCreateBackup"] = new()
        {
            [LangEn] = "Create backup",
            [LangEs] = "Crear respaldo",
        },
        ["ModPropRestoreBackup"] = new()
        {
            [LangEn] = "Restore from backup",
            [LangEs] = "Restaurar desde respaldo",
        },
        ["ModPropDiagnostics"] = new()
        {
            [LangEn] = "DIAGNOSTICS",
            [LangEs] = "DIAGNÓSTICO",
        },
        ["ModPropPathsSection"] = new()
        {
            [LangEn] = "PATHS",
            [LangEs] = "RUTAS",
        },
        ["ModPropMaintenanceSection"] = new()
        {
            [LangEn] = "MAINTENANCE",
            [LangEs] = "MANTENIMIENTO",
        },
        // ----------------------------------------------------------------
        // Strings introduced for the Properties dialog redesign (cards +
        // descriptions). All copy is mod-agnostic — no Wars of Liberty
        // references — so the same dialog hosts any mod's settings.
        // (ModPropSubtitle removed when the header was compacted to a
        //  single row; the sidebar tabs already convey the dialog's
        //  purpose, so the subtitle was pure vertical filler.)
        // ----------------------------------------------------------------
        // --- MOD SETTINGS: GENERAL (5a).
        // --- MOD SETTINGS: FILES (5b). The verb moved onto the button and the subject
        // into the row title, so the buttons are one word and sit in a fixed column.
        ["ModPropSettingsImportDesc"] = new()
        {
            [LangEn] = "Copies graphics, volumes and hotkeys once.",
            [LangEs] = "Copia gráficos, volúmenes y atajos una sola vez.",
        },
        ["ModPropBackupsSection"] = new()
        {
            [LangEn] = "BACKUPS",
            [LangEs] = "COPIAS DE SEGURIDAD",
        },
        ["ModPropBackupsNote"] = new()
        {
            [LangEn] = "They keep saves, home cities and your profile. They do not include the mod's files.",
            [LangEs] = "Guardan partidas, ciudades de origen y perfil. No incluyen los archivos del mod.",
        },
        ["ModPropGameSettingsSection"] = new()
        {
            [LangEn] = "GAME SETTINGS",
            [LangEs] = "AJUSTES DE JUEGO",
        },
        ["ModPropImportTitle"] = new()
        {
            [LangEn] = "Import from another mod",
            [LangEs] = "Importar desde otro mod",
        },
        ["ModPropInstallsSection"] = new()
        {
            [LangEn] = "INSTALLATIONS",
            [LangEs] = "INSTALACIONES",
        },
        ["ModPropInstallsCountOne"] = new()
        {
            [LangEn] = "1 registered",
            [LangEs] = "1 registrada",
        },
        ["ModPropInstallsCountMany"] = new()
        {
            [LangEn] = "{0} registered",
            [LangEs] = "{0} registradas",
        },
        ["ModPropModFolderTitle"] = new()
        {
            [LangEn] = "Mod folder",
            [LangEs] = "Carpeta del mod",
        },
        ["ModPropAoe3FolderTitle"] = new()
        {
            [LangEn] = "AoE3 folder",
            [LangEs] = "Carpeta de AoE3",
        },
        ["ModPropFindInstallTitle"] = new()
        {
            [LangEn] = "Can't find your installation?",
            [LangEs] = "¿No encuentra tu instalación?",
        },
        ["ModPropFindInstallDesc"] = new()
        {
            [LangEn] = "Looks for copies of the mod on every drive.",
            [LangEs] = "Busca copias del mod en todos los discos.",
        },
        ["ModPropTempTitle"] = new()
        {
            [LangEn] = "Temporary files",
            [LangEs] = "Archivos temporales",
        },
        ["ModPropTempShortDesc"] = new()
        {
            [LangEn] = "Download cache. Safe to delete; it comes back if you repair.",
            [LangEs] = "Caché de descarga. Se puede borrar; se vuelve a bajar si reparas.",
        },
        ["ModPropVerifyTitle"] = new()
        {
            [LangEn] = "Verify the files",
            [LangEs] = "Verificar los archivos",
        },
        ["ModPropVerifyDesc"] = new()
        {
            [LangEn] = "Checks the fingerprint without downloading anything.",
            [LangEs] = "Comprueba la huella sin descargar nada.",
        },
        ["ModPropRepairTitle"] = new()
        {
            [LangEn] = "Repair the installation",
            [LangEs] = "Reparar la instalación",
        },
        ["ModPropRepairDesc"] = new()
        {
            [LangEn] = "Re-downloads whatever does not match. Your saves and profiles are not touched.",
            [LangEs] = "Vuelve a descargar lo que no coincida. Tus partidas y perfiles no se tocan.",
        },
        ["ModPropUninstallTitle"] = new()
        {
            [LangEn] = "Uninstall the mod",
            [LangEs] = "Desinstalar el mod",
        },
        ["BtnVerify"] = new()
        {
            [LangEn] = "Verify",
            [LangEs] = "Verificar",
        },
        ["BtnRepair"] = new()
        {
            [LangEn] = "Repair",
            [LangEs] = "Reparar",
        },
        ["BtnFreeSpace"] = new()
        {
            [LangEn] = "Free space",
            [LangEs] = "Liberar espacio",
        },
        ["ModPropUpdatesSection"] = new()
        {
            [LangEn] = "UPDATES",
            [LangEs] = "ACTUALIZACIONES",
        },
        ["ModPropInstalledLabel"] = new()
        {
            [LangEn] = "INSTALLED",
            [LangEs] = "INSTALADA",
        },
        // The version is already in the row above it, so the switch does not repeat it.
        ["ModPropStayOnVersionShort"] = new()
        {
            [LangEn] = "Stay on this version",
            [LangEs] = "Quedarme en esta versión",
        },
        ["ModPropStayOnVersionWarn"] = new()
        {
            [LangEn] = "Some updates fix multiplayer compatibility. If you stay behind, you may not be able to play with people who updated.",
            [LangEs] = "Algunas actualizaciones arreglan la compatibilidad multijugador. Si te quedas atrás, puede que no puedas jugar con quien ya actualizó.",
        },
        ["ModPropAboutSection"] = new()
        {
            [LangEn] = "ABOUT",
            [LangEs] = "ACERCA DE",
        },
        ["ModPropInstallSection"] = new()
        {
            [LangEn] = "INSTALL LOCATION",
            [LangEs] = "UBICACIÓN DE INSTALACIÓN",
        },
        ["ModPropDangerZoneDesc"] = new()
        {
            [LangEn] = "Removing the mod is permanent. Create a backup first if you might want to come back.",
            [LangEs] = "Eliminar el mod es permanente. Crea una copia antes si vas a querer volver.",
        },
        ["ModPropOpenUserDataDesc"] = new()
        {
            [LangEn] = "Browse where this mod stores saves and player profiles.",
            [LangEs] = "Abre la carpeta donde el mod guarda partidas y perfiles.",
        },
        ["ModPropCreateBackupDesc"] = new()
        {
            [LangEn] = "Snapshot the user data so you can revert later.",
            [LangEs] = "Crea una copia de seguridad de los datos del usuario.",
        },
        ["ModPropRestoreBackupDesc"] = new()
        {
            [LangEn] = "Replace current user data with a previous backup.",
            [LangEs] = "Restaura los datos del usuario desde una copia anterior.",
        },
        ["ModPropUserDataLocation"] = new()
        {
            [LangEn] = "LOCATION",
            [LangEs] = "UBICACIÓN",
        },
        ["ModPropUserDataPathDiverges"] = new()
        {
            [LangEn] = "⚠ Data was also found at: {0} — your Documents folder was likely moved or redirected (e.g. OneDrive). Backups from both locations appear in the restore list.",
            [LangEs] = "⚠ También se encontraron datos en: {0} — tu carpeta Documentos probablemente fue movida o redirigida (p. ej. OneDrive). Los backups de ambas ubicaciones aparecen en la lista de restauración.",
        },
        ["ModPropRestoreCount"] = new()
        {
            [LangEn] = "Replace current user data with a previous backup. {0} available · latest: {1}.",
            [LangEs] = "Restaura los datos del usuario desde una copia anterior. {0} disponibles · última: {1}.",
        },
        ["ModPropRestoreNone"] = new()
        {
            [LangEn] = "No backups yet — create one above and it will show up here.",
            [LangEs] = "Aún no hay backups — crea uno arriba y aparecerá aquí.",
        },
        ["ModPropBackupDone"] = new()
        {
            [LangEn] = "✔ Backup created: {0}",
            [LangEs] = "✔ Backup creado: {0}",
        },
        ["ModPropRestoreDone"] = new()
        {
            [LangEn] = "✔ Restored: {0}",
            [LangEs] = "✔ Restaurado: {0}",
        },
        ["ModPropLanguageSectionTitle"] = new()
        {
            [LangEn] = "INTERFACE LANGUAGE",
            [LangEs] = "IDIOMA DE LA INTERFAZ",
        },
        ["ModPropLanguageDesc"] = new()
        {
            [LangEn] = "Choose the language used for this mod's in-game text.",
            [LangEs] = "Elige el idioma de los textos del mod.",
        },
        ["ModPropOpenBtn"] = new()
        {
            [LangEn] = "Open",
            [LangEs] = "Abrir",
        },
        ["ModPropBackupBtn"] = new()
        {
            [LangEn] = "Backup",
            [LangEs] = "Copiar",
        },
        ["ModPropRestoreBtn"] = new()
        {
            [LangEn] = "Restore",
            [LangEs] = "Restaurar",
        },
        // Sub-view crumbs — shown as "AJUSTES › RUTAS" etc when the
        // user drills into Paths / User data / Game language from
        // the top-level settings popup. Lowercase "›" separator is
        // added by the popup builder, not stored in the strings.
        // Top-level "Administrar" wrapper — see SETTINGS popup
        // redesign. Opens a sub-popup with all the maintenance
        // and configuration actions that used to live at the top
        // level. Keeps the gear popup itself minimal (2 entries
        // only: Administrar + Propiedades).
        // Back-arrow row at the top of each sub-view.
        // Per-row subtitles — small cool-grey line below the main
        // label so each settings entry feels self-explanatory instead
        // of "what does this even open?" These get rendered by the
        // popup row builder when the caller passes a non-empty value.
        // Paths sub-popup subtitles.
        // "Open mod folder" — new entry under Paths that opens
        // the active mod's install path in Explorer. Distinct from
        // "Open AoE3 folder" (which opens the base game's folder).
        ["MenuOpenModFolder"] = new()
        {
            [LangEn] = "Open mod folder",
            [LangEs] = "Abrir carpeta del mod",
        },
        // User-data sub-popup subtitles.
        // ====================================================================
        // Catalog redesign (post-v0.9): two-column layout strings.
        // ====================================================================
        ["ModsBrowserHeaderTitle"] = new()
        {
            [LangEn] = "Workshop",
            [LangEs] = "Workshop",
        },
        // Names the LIBRARY tab, and says the two steps out loud. It used to send the
        // user to "the Dashboard" — a word that appears nowhere in the UI, since the tab
        // is called LIBRARY / BIBLIOTECA. A user who could not find where to install
        // reported exactly this gap.
        ["ModsBrowserHeaderSubtitle"] = new()
        {
            [LangEn] = "Discover mods and add them to your launcher. Install and play them from the Library.",
            [LangEs] = "Descubre mods y agrégalos a tu launcher. Instálalos y juégalos desde la Biblioteca.",
        },
        // Workshop redesign: per-row action button is now Add / Remove
        // from the user's personal collection. Install / Update /
        // Repair / Uninstall happen on the Dashboard (via PLAY +
        // gear menu) instead of in the Workshop itself.
        ["ModsBrowserBtnAdd"] = new()
        {
            [LangEn] = "Add to my mods",
            [LangEs] = "Agregar a mis mods",
        },
        // Forward action once a mod is in the collection, replacing a DISABLED "In my
        // mods" pill that left the panel with nothing to click but Remove. Same text
        // whether or not the mod is installed: it says where the button goes, never what
        // happens there, so it can't be misread as an install button the way the old
        // Workshop buttons were.
        ["ModsBrowserBtnOpenInLibrary"] = new()
        {
            [LangEn] = "See in Library",
            [LangEs] = "Ver en la Biblioteca",
        },
        // The badge reports DISK state; the button beside it reports collection
        // membership. Naming the axis is what keeps them from reading as one control.
        ["ModsBrowserBadgeTooltip"] = new()
        {
            [LangEn] = "Installation state on your PC. Adding a mod to your mods does not install it — open it in the Library to install.",
            [LangEs] = "Estado de instalación en tu PC. Agregar un mod a tus mods no lo instala — ábrelo en la Biblioteca para instalarlo.",
        },
        ["ModsBrowserBtnRemove"] = new()
        {
            [LangEn] = "Remove from my mods",
            [LangEs] = "Quitar de mis mods",
        },
        // Built-in profiles (WoL) are always in the user's collection
        // and can't be removed — the per-row button is a small
        // disabled "Built-in" pill instead of Add/Remove.
        ["ModsBrowserBtnBuiltin"] = new()
        {
            [LangEn] = "Built-in",
            [LangEs] = "Integrado",
        },
        // Detail-panel PRIMARY button once the mod is in the collection: a
        // disabled status pill, not an action. Removing moved to a secondary
        // ghost button so the destructive option no longer sits in the most
        // prominent slot.
        ["ModsBrowserInCollection"] = new()
        {
            [LangEn] = "In my mods",
            [LangEs] = "En mis mods",
        },

        // -- "Remove from my mods" confirmation -------------------------------
        // Removing only drops the id from userModIds; no file is ever deleted
        // and the per-mod state (install path, version, translation) survives,
        // so re-adding restores everything. The dialog exists to say that,
        // because an installed mod disappearing from the MODS popup otherwise
        // reads as an uninstall. Shown ONLY for an installed mod — removing one
        // that isn't installed risks nothing, and confirming harmless actions
        // is what trains users to click through the prompt that matters.
        ["DlgRemoveModTitle"] = new()
        {
            [LangEn] = "Remove from my mods",
            [LangEs] = "Quitar de mis mods",
        },
        ["DlgRemoveModBodyInstalled"] = new()
        {
            [LangEn] = "This mod will stop appearing in your MODS list, but it "
                     + "stays installed: no file is deleted and nothing is "
                     + "downloaded again if you add it back from the Workshop. "
                     + "To free up disk space you have to uninstall it instead, "
                     + "from the gear menu.",
            [LangEs] = "Este mod va a dejar de aparecer en tu lista de MODS, pero "
                     + "sigue instalado: no se borra ningún archivo y no se "
                     + "descarga nada de nuevo si lo vuelves a agregar desde el "
                     + "Workshop. Para liberar espacio en disco tienes que "
                     + "desinstalarlo, desde el menú de configuración.",
        },
        ["DlgRemoveModPathLabel"] = new()
        {
            [LangEn] = "Its files stay here:",
            [LangEs] = "Sus archivos quedan aquí:",
        },
        ["DlgRemoveModConfirm"] = new()
        {
            [LangEn] = "Remove",
            [LangEs] = "Quitar",
        },
        ["DlgRemoveModCancel"] = new()
        {
            [LangEn] = "Cancel",
            [LangEs] = "Cancelar",
        },
        ["ModsBrowserEmpty"] = new()
        {
            [LangEn] = "No mods match your filters.",
            [LangEs] = "Ningún mod coincide con tus filtros.",
        },
        ["ModsBrowserDetailEmpty"] = new()
        {
            [LangEn] = "Select a mod from the list to see its details.",
            [LangEs] = "Selecciona un mod de la lista para ver sus detalles.",
        },
        ["ModsBrowserSearchPlaceholder"] = new()
        {
            [LangEn] = "Search mods…",
            [LangEs] = "Buscar mods...",
        },
        ["ModsBrowserListSummary"] = new()
        {
            [LangEn] = "Available mods ({0})",
            [LangEs] = "Mods disponibles ({0})",
        },
        ["ModsBrowserRefreshCatalog"] = new()
        {
            [LangEn] = "↻ Refresh catalog",
            [LangEs] = "↻ Actualizar catálogo",
        },
        // ("ModsBrowserAddLocal" / "TipWsAddLocal" removed with the Workshop header button.
        //  Trying a mod.json now lives in Settings → DEVELOPER; the old copy also described
        //  a different feature — registering an installed folder — that never existed.)
        ["ModsBrowserSubTabMyMods"] = new()
        {
            [LangEn] = "My mods",
            [LangEs] = "Mis mods",
        },
        ["ModsBrowserSubTabCatalog"] = new()
        {
            [LangEn] = "Catalog",
            [LangEs] = "Catálogo",
        },
        ["ModsBrowserFiltersLabel"] = new()
        {
            [LangEn] = "Filters:",
            [LangEs] = "Filtros:",
        },
        ["ModsBrowserFilterAll"] = new()
        {
            [LangEn] = "All",
            [LangEs] = "Todos",
        },
        ["ModsBrowserFilterInstalled"] = new()
        {
            [LangEn] = "Installed",
            [LangEs] = "Instalados",
        },
        ["ModsBrowserFilterNotInstalled"] = new()
        {
            [LangEn] = "Not installed",
            [LangEs] = "No instalados",
        },
        ["ModsBrowserFilterUpdates"] = new()
        {
            [LangEn] = "Updates",
            [LangEs] = "Actualizaciones",
        },
        ["ModsBrowserFilterCompatible"] = new()
        {
            [LangEn] = "Compatible",
            [LangEs] = "Compatibles",
        },
        ["ModsBrowserSortLabel"] = new()
        {
            [LangEn] = "Sort by:",
            [LangEs] = "Ordenar por:",
        },
        ["ModsBrowserSortRecent"] = new()
        {
            [LangEn] = "Most recent",
            [LangEs] = "Más recientes",
        },
        ["ModsBrowserSortName"] = new()
        {
            [LangEn] = "Name",
            [LangEs] = "Nombre",
        },
        ["ModsBrowserSortStatus"] = new()
        {
            [LangEn] = "Status",
            [LangEs] = "Estado",
        },
        ["ModsBrowserBadgeNotInstalled"] = new()
        {
            [LangEn] = "Not installed",
            [LangEs] = "No instalado",
        },
        ["ModsBrowserBadgeInstalled"] = new()
        {
            [LangEn] = "Installed",
            [LangEs] = "Instalado",
        },
        ["ModsBrowserBadgeUpdate"] = new()
        {
            [LangEn] = "Update available",
            [LangEs] = "Actualización disponible",
        },
        ["ModsBrowserBadgeIncompatible"] = new()
        {
            [LangEn] = "Incompatible",
            [LangEs] = "Incompatible",
        },
        ["ModsBrowserDetailMetaTitle"] = new()
        {
            [LangEn] = "DETAILS",
            [LangEs] = "DETALLES",
        },
        // The install/update rows say what the choice MEANS for the player's machine.
        // They used to be hard-coded English jargon ("Isolated folder", "WoL patcher
        // (UpdateInfo.xml)"): untranslated, and about the launcher's internals rather than
        // about anything a player can act on.
        ["ModsBrowserInstallTypeIsolated"] = new()
        {
            [LangEn] = "In its own folder. Doesn't touch your AoE3.",
            [LangEs] = "En su propia carpeta. No modifica tu AoE3.",
        },
        ["ModsBrowserInstallTypeOverlay"] = new()
        {
            [LangEn] = "Over your AoE3 install.",
            [LangEs] = "Sobre tu instalación de AoE3.",
        },
        ["ModsBrowserUpdateMechAutomatic"] = new()
        {
            [LangEn] = "Automatic, from the launcher.",
            [LangEs] = "Automáticas, desde el launcher.",
        },
        ["ModsBrowserUpdateMechExternal"] = new()
        {
            [LangEn] = "Handled by the mod's own updater.",
            [LangEs] = "Las gestiona el actualizador del propio mod.",
        },
        ["ModsBrowserUpdateMechManual"] = new()
        {
            [LangEn] = "Manual.",
            [LangEs] = "Manuales.",
        },
        ["ModsBrowserBadgeBase"] = new()
        {
            [LangEn] = "Base",
            [LangEs] = "Base",
        },
        ["ModsBrowserBadgeError"] = new()
        {
            [LangEn] = "Error",
            [LangEs] = "Error",
        },
        ["ModsBrowserDetailDeveloper"] = new()
        {
            [LangEn] = "Developer",
            [LangEs] = "Desarrollador",
        },
        ["ModsBrowserDetailVersion"] = new()
        {
            [LangEn] = "Version",
            [LangEs] = "Versión",
        },
        ["ModsBrowserDetailAvailable"] = new()
        {
            [LangEn] = "Available",
            [LangEs] = "Disponible",
        },
        ["ModsBrowserDetailInstallType"] = new()
        {
            [LangEn] = "Install type",
            [LangEs] = "Tipo de instalación",
        },
        ["ModsBrowserDetailUpdates"] = new()
        {
            [LangEn] = "Updates",
            [LangEs] = "Actualizaciones",
        },
        ["ModsBrowserDetailWebsite"] = new()
        {
            [LangEn] = "Website",
            [LangEs] = "Sitio web",
        },
        ["ModsBrowserDetailLanguages"] = new()
        {
            [LangEn] = "Languages",
            [LangEs] = "Idiomas",
        },
        ["WorkshopGalleryTitle"] = new()
        {
            [LangEn] = "Screenshots",
            [LangEs] = "Capturas",
        },
        // Community links section of the Workshop detail panel. The per-type
        // captions are only used when a mod's manifest entry ships no label.
        ["ModsBrowserDetailLinks"] = new()
        {
            [LangEn] = "Community links",
            [LangEs] = "Enlaces de la comunidad",
        },
        ["ModLinkTypeWebsite"] = new()
        {
            [LangEn] = "Website",
            [LangEs] = "Sitio web",
        },
        ["ModLinkTypeDiscord"] = new()
        {
            [LangEn] = "Discord",
            [LangEs] = "Discord",
        },
        ["ModLinkTypeModDb"] = new()
        {
            [LangEn] = "ModDB",
            [LangEs] = "ModDB",
        },
        ["ModLinkTypeForum"] = new()
        {
            [LangEn] = "Forum",
            [LangEs] = "Foro",
        },
        ["ModLinkTypeWiki"] = new()
        {
            [LangEn] = "Wiki",
            [LangEs] = "Wiki",
        },
        ["ModLinkTypeVideo"] = new()
        {
            [LangEn] = "Videos",
            [LangEs] = "Videos",
        },
        ["ModLinkTypeOther"] = new()
        {
            [LangEn] = "Link",
            [LangEs] = "Enlace",
        },
        ["ModsBrowserActionInstall"] = new()
        {
            [LangEn] = "Install mod",
            [LangEs] = "Instalar mod",
        },
        ["ModsBrowserActionUpdate"] = new()
        {
            [LangEn] = "Update",
            [LangEs] = "Actualizar",
        },
        ["ModsBrowserActionPlay"] = new()
        {
            [LangEn] = "Play",
            [LangEs] = "Jugar",
        },
        ["ModsBrowserActionRepair"] = new()
        {
            [LangEn] = "Repair",
            [LangEs] = "Reparar",
        },
        ["ModsBrowserActionIncompatible"] = new()
        {
            [LangEn] = "Incompatible",
            [LangEs] = "Incompatible",
        },
        ["ModsBrowserActionViewWebsite"] = new()
        {
            [LangEn] = "View mod page",
            [LangEs] = "Ver página del mod",
        },
        ["ModsBrowserActionSwitchActive"] = new()
        {
            [LangEn] = "Set as active mod",
            [LangEs] = "Establecer como mod activo",
        },
        ["ModsBrowserActionUninstall"] = new()
        {
            [LangEn] = "Uninstall",
            [LangEs] = "Desinstalar",
        },
        ["ModsBrowserMenuPublish"] = new()
        {
            [LangEn] = "Publish my mod",
            [LangEs] = "Publicar mi mod",
        },
        // --- Add local mod: try a mod.json from disk before publishing it ---
        // --- Developer mode: unlocks the block holding the author tools ---
        // Its old title and hint lived on a loose row at the bottom of GENERAL and are gone
        // with it. The TIP stays: the switch moved into the block it governs rather than
        // disappearing, and it is still what closes it.
        ["DlgSettingsDeveloperModeTip"] = new()
        {
            [LangEn] = "Turning it off only hides the tools — the mods you added from a local file stay in your catalog.",
            [LangEs] = "Apagarlo solo esconde las herramientas — los mods que agregaste desde un archivo local siguen en tu catálogo.",
        },
        ["DlgLauncherSettingsSectionDeveloper"] = new()
        {
            [LangEn] = "DEVELOPER",
            [LangEs] = "DESARROLLADOR",
        },
        // --- Local mod.json section inside the developer tab ---
        ["DlgSettingsLocalModsHeader"] = new()
        {
            [LangEn] = "Test a mod.json",
            [LangEs] = "Probar un mod.json",
        },
        ["DlgSettingsLocalModsDescription"] = new()
        {
            [LangEn] = "Loads a mod.json from your PC using the same format as the catalog, so you can see how your mod will look before publishing it. Edit the file and press \"Refresh catalog\" in the Workshop to see your changes. Nothing is uploaded.",
            [LangEs] = "Carga un mod.json de tu PC con el mismo formato del catálogo, para ver cómo se verá tu mod antes de publicarlo. Edita el archivo y presiona \"Actualizar catálogo\" en el Workshop para ver los cambios. No se sube nada.",
        },
        ["DlgSettingsLocalModsAdd"] = new()
        {
            [LangEn] = "Choose a mod.json...",
            [LangEs] = "Elegir un mod.json...",
        },
        ["DlgSettingsLocalModsRemove"] = new()
        {
            [LangEn] = "Remove",
            [LangEs] = "Quitar",
        },
        ["DlgSettingsLocalModsEmpty"] = new()
        {
            [LangEn] = "No local manifests yet.",
            [LangEs] = "Todavía no hay manifiestos locales.",
        },
        ["ModsBrowserAddLocalPickTitle"] = new()
        {
            [LangEn] = "Choose a mod.json",
            [LangEs] = "Elige un mod.json",
        },
        ["ModsBrowserAddLocalFilter"] = new()
        {
            [LangEn] = "Mod manifest (mod.json)|*.json|All files|*.*",
            [LangEs] = "Manifiesto de mod (mod.json)|*.json|Todos los archivos|*.*",
        },
        ["ModsBrowserAddLocalAddedTitle"] = new()
        {
            [LangEn] = "Local mod added",
            [LangEs] = "Mod local agregado",
        },
        // {0} = mod display name. Says where it came from AND how to iterate, because the
        // re-read-on-refresh loop is the whole reason to use this instead of a PR.
        ["ModsBrowserAddLocalAddedBody"] = new()
        {
            [LangEn] = "{0} was added from your manifest and now appears in the catalog. Edit the file and press \"Refresh catalog\" to see your changes — nothing is uploaded.",
            [LangEs] = "{0} se agregó desde tu manifiesto y ya aparece en el catálogo. Edita el archivo y presiona \"Actualizar catálogo\" para ver los cambios — no se sube nada.",
        },
        ["ModsBrowserAddLocalInvalidTitle"] = new()
        {
            [LangEn] = "That manifest can't be read",
            [LangEs] = "No se puede leer ese manifiesto",
        },
        // {0} = the actual reason (JSON error with line, missing id, ...). Naming the
        // cause is the point: the catalog CI only tells you after you open the PR.
        ["ModsBrowserAddLocalInvalidBody"] = new()
        {
            [LangEn] = "The mod was not added:\n\n{0}",
            [LangEs] = "El mod no se agregó:\n\n{0}",
        },
        ["ModsBrowserLocalBadge"] = new()
        {
            [LangEn] = "Local",
            [LangEs] = "Local",
        },
        ["ModsBrowserRemoveLocal"] = new()
        {
            [LangEn] = "Stop using this file",
            [LangEs] = "Dejar de usar este archivo",
        },
        ["PublishWizardTitle"] = new()
        {
            [LangEn] = "Publish my mod",
            [LangEs] = "Publicar mi mod",
        },
        ["PublishWizardCancel"] = new()
        {
            [LangEn] = "Cancel",
            [LangEs] = "Cancelar",
        },
        ["PublishWizardBack"] = new()
        {
            [LangEn] = "Back",
            [LangEs] = "Atrás",
        },
        ["PublishWizardNext"] = new()
        {
            [LangEn] = "Next",
            [LangEs] = "Siguiente",
        },
        ["PublishWizardFinish"] = new()
        {
            [LangEn] = "Finish",
            [LangEs] = "Finalizar",
        },
        ["PublishWizardStepFormat"] = new()
        {
            [LangEn] = "Step {0} of {1}",
            [LangEs] = "Paso {0} de {1}",
        },
        ["PublishWizardStep1Title"] = new()
        {
            [LangEn] = "Identity",
            [LangEs] = "Identidad",
        },
        ["PublishWizardStep1Hint"] = new()
        {
            [LangEn] = "Pick a stable id and a display name. These two fields anchor the catalog entry — the id can't be changed later (it's the folder name), but the display name can.",
            [LangEs] = "Elige un id estable y un nombre visible. Estos dos campos anclan la entrada del catálogo: el id no se puede cambiar después (es el nombre de carpeta), pero el nombre visible sí.",
        },
        ["PublishWizardStep2Title"] = new()
        {
            [LangEn] = "Look & feel",
            [LangEs] = "Apariencia",
        },
        ["PublishWizardStep2Hint"] = new()
        {
            [LangEn] = "Accent colour, icon and banner. Optional but recommended.",
            [LangEs] = "Color de acento, icono y banner. Opcional pero recomendado.",
        },
        ["PublishWizardStep3Title"] = new()
        {
            [LangEn] = "Install",
            [LangEs] = "Instalación",
        },
        ["PublishWizardStep3Hint"] = new()
        {
            [LangEn] = "How the mod's files live on disk and which executable launches it.",
            [LangEs] = "Cómo se almacenan los archivos del mod y qué ejecutable lo lanza.",
        },
        ["PublishWizardStep4Title"] = new()
        {
            [LangEn] = "Updates",
            [LangEs] = "Actualizaciones",
        },
        ["PublishWizardStep4Hint"] = new()
        {
            [LangEn] = "How the launcher pulls new versions. For a brand-new mod, GitHubReleases is the easiest: point it at your repo and tag a release. Extra fields appear below once you pick a mechanism.",
            [LangEs] = "Cómo el launcher obtiene nuevas versiones. Para un mod nuevo, GitHubReleases es lo más fácil: apúntalo a tu repo y etiqueta un release. Aparecerán campos extra abajo al elegir un mecanismo.",
        },
        ["PublishWizardStep5Title"] = new()
        {
            [LangEn] = "Description & links",
            [LangEs] = "Descripción y enlaces",
        },
        ["PublishWizardStep5Hint"] = new()
        {
            [LangEn] = "Per-language description, the mod's homepage URL and your community links.",
            [LangEs] = "Descripción por idioma, la URL del sitio del mod y tus enlaces de comunidad.",
        },
        ["PublishWizardStep6Title"] = new()
        {
            [LangEn] = "Review & publish",
            [LangEs] = "Revisar y publicar",
        },
        ["PublishWizardStep6Hint"] = new()
        {
            [LangEn] = "Inspect the generated mod.json, copy it to the clipboard, and open the catalog PR template on GitHub.",
            [LangEs] = "Revisa el mod.json generado, cópialo al portapapeles y abre la plantilla de PR del catálogo en GitHub.",
        },
        ["PublishFieldId"] = new() { [LangEn] = "Id", [LangEs] = "Id" },
        ["PublishFieldIdHint"] = new()
        {
            [LangEn] = "Lowercase letters, digits, dashes. Used as the folder name under /mods/. Example: napoleonic-era",
            [LangEs] = "Minúsculas, dígitos y guiones. Se usa como nombre de carpeta dentro de /mods/. Ejemplo: napoleonic-era",
        },
        ["PublishFieldDisplayName"] = new() { [LangEn] = "Display name", [LangEs] = "Nombre visible" },
        ["PublishFieldDisplayNameHint"] = new()
        {
            [LangEn] = "The name shown in the catalog. Example: Napoleonic Era",
            [LangEs] = "El nombre que se muestra en el catálogo. Ejemplo: Napoleonic Era",
        },
        ["PublishFieldAuthor"] = new() { [LangEn] = "Author (optional)", [LangEs] = "Autor (opcional)" },
        ["PublishFieldAuthorHint"] = new()
        {
            [LangEn] = "Your name or your team's. Example: Napoleonic Team",
            [LangEs] = "Tu nombre o el de tu equipo. Ejemplo: Napoleonic Team",
        },
        ["PublishFieldSubtitle"] = new() { [LangEn] = "Subtitle (optional)", [LangEs] = "Subtítulo (opcional)" },
        ["PublishFieldSubtitleHint"] = new()
        {
            [LangEn] = "Short tagline under the title. Example: Napoleonic Wars, 1789–1815",
            [LangEs] = "Frase corta bajo el título. Ejemplo: Guerras napoleónicas, 1789–1815",
        },
        ["PublishFieldAccent"] = new() { [LangEn] = "Accent colour (optional)", [LangEs] = "Color de acento (opcional)" },
        ["PublishFieldAccentHint"] = new()
        {
            [LangEn] = "Hex format, e.g. #c8102e. It's the mod's brand colour in the launcher.",
            [LangEs] = "Formato hex, ej. #c8102e. Es el color de marca del mod en el launcher.",
        },
        ["PublishFieldIcon"] = new() { [LangEn] = "Icon filename (optional)", [LangEs] = "Nombre del icono (opcional)" },
        ["PublishFieldIconHint"] = new()
        {
            [LangEn] = "icon.png — 256x256, PNG with alpha, ≤100 KB.",
            [LangEs] = "icon.png — 256x256, PNG con alfa, ≤100 KB.",
        },
        ["PublishFieldBanner"] = new() { [LangEn] = "Banner filename (optional)", [LangEs] = "Nombre del banner (opcional)" },
        ["PublishFieldBannerHint"] = new()
        {
            [LangEn] = "banner.png/.jpg — 1200x300, ≤500 KB.",
            [LangEs] = "banner.png/.jpg — 1200x300, ≤500 KB.",
        },
        ["PublishFieldInstallType"] = new() { [LangEn] = "How your mod installs and runs", [LangEs] = "Cómo se instala y corre tu mod" },
        ["PublishFieldInstallTypeHint"] = new()
        {
            [LangEn] = "Pick how your mod behaves — this decides whether it opens correctly. Not sure? Test UHC: copy your game folder somewhere else and run the .exe; if it opens, it has UHC (first option). See MODDING.md §4.",
            [LangEs] = "Elige cómo se comporta tu mod — esto decide si abre bien. ¿No sabes? Prueba UHC: copia la carpeta del juego a otro lado y ejecuta el .exe; si abre, tiene UHC (primera opción). Mira MODDING.md §4.",
        },
        ["PublishInstallOptUhc"] = new()
        {
            [LangEn] = "Total conversion with its own patched exe (UHC) — runs from any folder",
            [LangEs] = "Conversión total con su propio exe parcheado (UHC) — corre desde cualquier carpeta",
        },
        ["PublishInstallOptAdditive"] = new()
        {
            [LangEn] = "Adds new files to AoE3 (own suffixed exe/.bar, doesn't replace base files)",
            [LangEs] = "Agrega archivos nuevos al AoE3 (exe/.bar propio con sufijo, no pisa el base)",
        },
        ["PublishInstallOptReplace"] = new()
        {
            [LangEn] = "Replaces AoE3 using the stock age3y.exe (gets its own registry entry)",
            [LangEs] = "Reemplaza el AoE3 usando el age3y.exe stock (recibe su propia clave de registro)",
        },
        ["PublishFieldDefaultFolder"] = new() { [LangEn] = "Default install folder", [LangEs] = "Carpeta de instalación por defecto" },
        ["PublishFieldDefaultFolderHint"] = new()
        {
            [LangEn] = "Folder name suggested when installing. Example: Napoleonic Era",
            [LangEs] = "Nombre de carpeta sugerido al instalar. Ejemplo: Napoleonic Era",
        },
        ["PublishFieldProbeFile"] = new() { [LangEn] = "Probe file", [LangEs] = "Archivo de detección" },
        ["PublishFieldProbeFileHint"] = new()
        {
            [LangEn] = "A file that confirms the mod is installed. Example: data\\napoleonic.xml",
            [LangEs] = "Un archivo que confirma que el mod está instalado. Ejemplo: data\\napoleonic.xml",
        },
        ["PublishFieldExecutable"] = new() { [LangEn] = "Executable", [LangEs] = "Ejecutable" },
        ["PublishFieldExecutableHint"] = new()
        {
            [LangEn] = "The .exe that launches the game. Example: age3y.exe",
            [LangEs] = "El .exe que lanza el juego. Ejemplo: age3y.exe",
        },
        ["PublishFieldArguments"] = new() { [LangEn] = "Arguments (optional)", [LangEs] = "Argumentos (opcional)" },
        ["PublishFieldArgumentsHint"] = new()
        {
            [LangEn] = "Command-line flags on launch. Example: +nointromovie",
            [LangEs] = "Parámetros de línea de comandos al lanzar. Ejemplo: +nointromovie",
        },
        ["PublishFieldMechanism"] = new() { [LangEn] = "Update mechanism", [LangEs] = "Mecanismo de actualización" },
        ["PublishFieldMechanismHint"] = new()
        {
            [LangEn] = "GitHubReleases is recommended for new mods. WolPatcher is the legacy UpdateInfo.xml flow; Manual = no auto-updates.",
            [LangEs] = "GitHubReleases es lo recomendado para mods nuevos. WolPatcher es el flujo antiguo de UpdateInfo.xml; Manual = sin auto-actualización.",
        },
        ["PublishFieldWolUpdateInfoUrl"] = new() { [LangEn] = "UpdateInfo.xml URL", [LangEs] = "URL de UpdateInfo.xml" },
        ["PublishFieldWolUpdateInfoUrlHint"] = new()
        {
            [LangEn] = "URL to a WoL-style UpdateInfo.xml. Example: https://yoursite.com/UpdateInfo.xml",
            [LangEs] = "URL a un UpdateInfo.xml estilo WoL. Ejemplo: https://tusitio.com/UpdateInfo.xml",
        },
        ["PublishFieldSourceRepo"] = new() { [LangEn] = "Source repo (owner/repo)", [LangEs] = "Repo fuente (owner/repo)" },
        ["PublishFieldSourceRepoHint"] = new()
        {
            [LangEn] = "Your mod's GitHub repository, e.g. yourname/your-mod.",
            [LangEs] = "El repositorio de GitHub de tu mod, ej. tunombre/tu-mod.",
        },
        ["PublishFieldApprovedTag"] = new() { [LangEn] = "Approved release tag", [LangEs] = "Tag de release aprobado" },
        ["PublishFieldApprovedTagHint"] = new()
        {
            [LangEn] = "The release tag the launcher downloads. Example: v1.0.0",
            [LangEs] = "El tag de release que el launcher descarga. Ejemplo: v1.0.0",
        },
        ["PublishFieldDescriptionEn"] = new() { [LangEn] = "Description (English)", [LangEs] = "Descripción (Inglés)" },
        ["PublishFieldDescriptionHint"] = new()
        {
            [LangEn] = "1–2 sentences on what your mod does. Example: A total conversion set during the Napoleonic Wars.",
            [LangEs] = "1–2 frases sobre qué hace tu mod. Ejemplo: Una conversión total ambientada en las guerras napoleónicas.",
        },
        ["PublishFieldDescriptionEs"] = new() { [LangEn] = "Description (Spanish)", [LangEs] = "Descripción (Español)" },
        ["PublishFieldWebsite"] = new() { [LangEn] = "Official website (optional)", [LangEs] = "Sitio web oficial (opcional)" },
        ["PublishFieldWebsiteHint"] = new()
        {
            [LangEn] = "Your mod's page, Discord or ModDB. Example: https://discord.gg/your-mod",
            [LangEs] = "La página de tu mod, Discord o ModDB. Ejemplo: https://discord.gg/tu-mod",
        },
        ["PublishFieldLinks"] = new()
        {
            [LangEn] = "Community links (optional)",
            [LangEs] = "Enlaces de la comunidad (opcional)",
        },
        ["PublishFieldLinksHint"] = new()
        {
            [LangEn] = "One per line, as type|url. Up to 4, HTTPS only. Types: website, discord, moddb, forum, wiki, video, other. Example: discord|https://discord.gg/your-mod",
            [LangEs] = "Uno por línea, con el formato tipo|url. Hasta 4, solo HTTPS. Tipos: website, discord, moddb, forum, wiki, video, other. Ejemplo: discord|https://discord.gg/tu-mod",
        },
        ["PublishCopyJson"] = new() { [LangEn] = "Copy JSON", [LangEs] = "Copiar JSON" },
        ["PublishOpenPr"] = new() { [LangEn] = "Open PR on GitHub", [LangEs] = "Abrir PR en GitHub" },
        ["PublishWizardIntro"] = new()
        {
            [LangEn] = "This wizard builds a mod.json for the public catalog. Fill in each step, then on the last step copy the file or open a ready-made GitHub pull request. You don't install anything here — your mod is added by a PR to the catalog repo (Gorgorito12/aoe3-mods-catalog), which the launcher reads to list every mod.",
            [LangEs] = "Este asistente crea un mod.json para el catálogo público. Completa cada paso y, en el último, copia el archivo o abre una pull request de GitHub ya preparada. Aquí no instalas nada: tu mod se agrega con una PR al repositorio del catálogo (Gorgorito12/aoe3-mods-catalog), que el launcher lee para listar todos los mods.",
        },
        ["PublishImagesUploadNote"] = new()
        {
            [LangEn] = "These are just filenames. After you open the pull request, drop the real image files (icon.png, banner.png) into the same mods/<id>/ folder of the PR — otherwise the catalog has nothing to show.",
            [LangEs] = "Esto son solo nombres de archivo. Tras abrir la pull request, coloca los archivos de imagen reales (icon.png, banner.png) en la misma carpeta mods/<id>/ de la PR; de lo contrario el catálogo no tiene nada que mostrar.",
        },
        ["PublishNextStepsTitle"] = new()
        {
            [LangEn] = "What happens after you publish",
            [LangEs] = "Qué pasa después de publicar",
        },
        ["PublishNextStepsBody"] = new()
        {
            [LangEn] = "1. Click \"Open PR on GitHub\" — it opens the catalog's new-file editor with this mod.json pre-filled at mods/<id>/mod.json.\n2. Commit it and create the pull request (GitHub forks the repo for you).\n3. Add your icon.png / banner.png to the same folder in the PR.\n4. Automated checks validate the schema and images. Cosmetic edits and version bumps merge automatically; first-time mods and changes to install/update fields get a manual review.\n5. Once merged, your mod appears in the Catalog after pressing \"Refresh catalog\".\n\nPublishing an UPDATE (GitHubReleases): upload your new build as a .zip on a new GitHub release, then open a tiny PR that only bumps \"approvedReleaseTag\" — that auto-merges. The launcher then shows an Update button; updating adds/overwrites your files and deletes the ones you dropped (see the deletion note on the Updates step). Full guide: https://github.com/Gorgorito12/AoE3-Mod-Launcher/blob/main/docs/MODDING.md",
            [LangEs] = "1. Haz clic en \"Abrir PR en GitHub\": abre el editor de archivo nuevo del catálogo con este mod.json ya completado en mods/<id>/mod.json.\n2. Confírmalo y crea la pull request (GitHub hace un fork del repo por ti).\n3. Agrega tu icon.png / banner.png a la misma carpeta de la PR.\n4. Verificaciones automáticas validan el esquema y las imágenes. Los cambios cosméticos y de versión se fusionan solos; los mods nuevos y los cambios en campos de instalación/actualización pasan por revisión manual.\n5. Una vez fusionado, tu mod aparece en el Catálogo tras hacer clic en \"Actualizar catálogo\".\n\nPublicar una ACTUALIZACIÓN (GitHubReleases): sube tu nueva versión como .zip en un release nuevo de GitHub y abre una PR pequeña que solo cambie \"approvedReleaseTag\": se fusiona sola. El launcher mostrará un botón Actualizar; al actualizar añade/sobrescribe tus archivos y borra los que quitaste (ver la nota de borrado en el paso Actualizaciones). Guía completa: https://github.com/Gorgorito12/AoE3-Mod-Launcher/blob/main/docs/MODDING.md",
        },
        // --- Schema-completeness pass: fields the wizard previously omitted. ---
        ["PublishFieldMarker"] = new() { [LangEn] = "Content marker (optional)", [LangEs] = "Marcador de contenido (opcional)" },
        ["PublishFieldMarkerHint"] = new()
        {
            [LangEn] = "A file or folder unique to your mod and absent from vanilla AoE3. Lets the launcher recognise your mod in a folder of ANY name. Only needed when your probe file also exists in the base game. Example: art\\my-mod-marker",
            [LangEs] = "Un archivo o carpeta exclusivo de tu mod y ausente en el AoE3 original. Permite al launcher reconocer tu mod en una carpeta con CUALQUIER nombre. Solo hace falta si tu probe file también existe en el juego base. Ejemplo: art\\mi-marcador",
        },
        ["PublishFieldUserDataFolder"] = new() { [LangEn] = "User-data folder (optional)", [LangEs] = "Carpeta de datos de usuario (opcional)" },
        ["PublishFieldUserDataFolderHint"] = new()
        {
            [LangEn] = "Folder name under Documents\\My Games\\ where your mod stores saves/replays. When set, the launcher offers backup/restore. Leave blank if your mod shares vanilla AoE3's folder. Example: My Mod",
            [LangEs] = "Nombre de carpeta dentro de Documents\\My Games\\ donde tu mod guarda partidas/repeticiones. Si la pones, el launcher ofrece copia/restauración. Déjala vacía si tu mod comparte la carpeta del AoE3 original. Ejemplo: Mi Mod",
        },
        ["PublishAdvancedHeader"] = new() { [LangEn] = "Advanced (optional)", [LangEs] = "Avanzado (opcional)" },
        ["PublishFieldProductGuid"] = new() { [LangEn] = "Uninstall registry key (optional)", [LangEs] = "Clave de registro de desinstalación (opcional)" },
        ["PublishFieldProductGuidHint"] = new()
        {
            [LangEn] = "Stable Add/Remove Programs subkey. Leave blank and the launcher derives <id>_launcher automatically. Only set it if you need a fixed value across releases.",
            [LangEs] = "Subclave estable de Agregar o quitar programas. Déjala vacía y el launcher deriva <id>_launcher automáticamente. Ponla solo si necesitas un valor fijo entre versiones.",
        },
        ["PublishFieldPayloadUrls"] = new() { [LangEn] = "Initial-install payload URLs (optional)", [LangEs] = "URLs del paquete de instalación inicial (opcional)" },
        ["PublishFieldPayloadUrlsHint"] = new()
        {
            [LangEn] = "One HTTPS URL per line. The archive(s) the launcher downloads for a fresh install when not using a GitHub release asset. For a multi-part archive (.zip.001/.002/…), list every part in order.",
            [LangEs] = "Una URL HTTPS por línea. El/los archivo(s) que el launcher descarga en una instalación nueva cuando no usas el asset de un release de GitHub. Para un archivo multi-parte (.zip.001/.002/…), lista cada parte en orden.",
        },
        ["PublishFieldPayloadSha256"] = new() { [LangEn] = "Payload SHA-256 (optional)", [LangEs] = "SHA-256 del paquete (opcional)" },
        ["PublishFieldPayloadSha256Hint"] = new()
        {
            [LangEn] = "One 64-hex SHA-256 per line, matching the URLs above in order. Strongly recommended — the launcher rejects a download whose hash doesn't match, blocking tampered payloads.",
            [LangEs] = "Un SHA-256 (64 hex) por línea, en el mismo orden que las URLs de arriba. Muy recomendado: el launcher rechaza una descarga cuyo hash no coincida, bloqueando paquetes manipulados.",
        },
        ["PublishFieldWolUrlAlt"] = new() { [LangEn] = "UpdateInfo.xml mirror URL (optional)", [LangEs] = "URL espejo de UpdateInfo.xml (opcional)" },
        ["PublishFieldWolUrlAltHint"] = new()
        {
            [LangEn] = "Fallback URL the launcher tries if the primary UpdateInfo.xml is unreachable.",
            [LangEs] = "URL de respaldo que el launcher prueba si el UpdateInfo.xml principal no responde.",
        },
        ["PublishFieldWolPayloadUrls"] = new() { [LangEn] = "Install payload ZIP URLs (optional)", [LangEs] = "URLs del ZIP de instalación (opcional)" },
        ["PublishFieldWolPayloadUrlsHint"] = new()
        {
            [LangEn] = "One HTTPS URL per line. The full install snapshot ZIP (multi-part allowed, in order). Used for a fresh install before the patch chain runs.",
            [LangEs] = "Una URL HTTPS por línea. El ZIP completo de instalación (multi-parte permitido, en orden). Se usa en una instalación nueva antes de aplicar la cadena de parches.",
        },
        ["PublishFieldWolPayloadSha256"] = new() { [LangEn] = "Install ZIP SHA-256 (optional)", [LangEs] = "SHA-256 del ZIP de instalación (opcional)" },
        ["PublishUpdateDeletionNote"] = new()
        {
            [LangEn] = "Updates & file deletion: when a player updates, the launcher extracts your new .zip on top — adding and overwriting files — and automatically deletes files YOU added that you've dropped from the new .zip. To delete files explicitly, ship a delete.lst (one path per line) at the root of your .zip. ⚠ delete.lst DELETES, it does not revert — never list a file your mod overwrote from the base game (re-pack the original bytes instead, or the game breaks). Deletions are backed up first. (Wars of Liberty uses its own update system.)",
            [LangEs] = "Actualizaciones y borrado de archivos: cuando un jugador actualiza, el launcher extrae tu nuevo .zip encima —añadiendo y sobrescribiendo archivos— y borra automáticamente los archivos que TÚ agregaste y quitaste del nuevo .zip. Para borrar archivos de forma explícita, incluye un delete.lst (una ruta por línea) en la raíz de tu .zip. ⚠ delete.lst BORRA, no revierte: nunca listes un archivo que tu mod sobrescribió del juego base (re-empaqueta los bytes originales en su lugar, o el juego se rompe). Los borrados se respaldan antes. (Wars of Liberty usa su propio sistema.)",
        },
        ["PublishGhAdvancedHeader"] = new() { [LangEn] = "Advanced: host the payload outside GitHub (optional)", [LangEs] = "Avanzado: alojar el paquete fuera de GitHub (opcional)" },
        ["PublishFieldGhExternalUrl"] = new() { [LangEn] = "External asset URL template", [LangEs] = "Plantilla de URL del asset externo" },
        ["PublishFieldGhExternalUrlHint"] = new()
        {
            [LangEn] = "HTTPS URL with the literal {tag}, replaced by the approved release tag at download time. Use it to host the .zip on your own CDN while keeping the tag as the version. Example: https://my-cdn.com/my-mod-{tag}.zip",
            [LangEs] = "URL HTTPS con el literal {tag}, que se reemplaza por el tag aprobado al descargar. Úsala para alojar el .zip en tu propia CDN manteniendo el tag como versión. Ejemplo: https://mi-cdn.com/mi-mod-{tag}.zip",
        },
        ["PublishFieldGhExternalSha"] = new() { [LangEn] = "External asset SHA-256", [LangEs] = "SHA-256 del asset externo" },
        ["PublishFieldGhExternalShaHint"] = new()
        {
            [LangEn] = "64-hex SHA-256 of the file the template serves. REQUIRED when you set a URL template — without GitHub's authenticity boundary the launcher won't install an external download unverified.",
            [LangEs] = "SHA-256 (64 hex) del archivo que sirve la plantilla. OBLIGATORIO si pones una plantilla de URL: sin la garantía de autenticidad de GitHub, el launcher no instala una descarga externa sin verificar.",
        },
        ["PublishTranslationsHeader"] = new() { [LangEn] = "Community translations (optional)", [LangEs] = "Traducciones de la comunidad (opcional)" },
        ["PublishFieldTranslationsRepo"] = new() { [LangEn] = "Translations repo (owner/repo)", [LangEs] = "Repo de traducciones (owner/repo)" },
        ["PublishFieldTranslationsRepoHint"] = new()
        {
            [LangEn] = "GitHub repo where community translation packs live (one release per language). Leave blank if your mod has no translation system.",
            [LangEs] = "Repo de GitHub donde viven los paquetes de traducción de la comunidad (un release por idioma). Déjalo vacío si tu mod no tiene sistema de traducciones.",
        },
        ["PublishFieldTranslationsCovered"] = new() { [LangEn] = "Translatable files (optional)", [LangEs] = "Archivos traducibles (opcional)" },
        ["PublishFieldTranslationsCoveredHint"] = new()
        {
            [LangEn] = "One relative path per line — the files a translation pack is allowed to replace. Example: data\\stringtable.xml",
            [LangEs] = "Una ruta relativa por línea: los archivos que un paquete de traducción puede reemplazar. Ejemplo: data\\stringtable.xml",
        },
        ["PublishErrorSourceRepo"] = new()
        {
            [LangEn] = "Source repo must be owner/repo (letters, digits, . _ -).",
            [LangEs] = "El repo debe ser owner/repo (letras, dígitos, . _ -).",
        },
        ["PublishErrorSha256"] = new()
        {
            [LangEn] = "Each SHA-256 must be 64 hexadecimal characters.",
            [LangEs] = "Cada SHA-256 debe tener 64 caracteres hexadecimales.",
        },
        ["PublishErrorGhShaRequired"] = new()
        {
            [LangEn] = "An external asset URL needs its SHA-256 (64 hex).",
            [LangEs] = "Una URL de asset externo necesita su SHA-256 (64 hex).",
        },
        ["PublishErrorId"] = new()
        {
            [LangEn] = "Invalid id. Use lowercase letters, digits and dashes (max 31 chars, starts with a letter).",
            [LangEs] = "Id inválido. Usa minúsculas, dígitos y guiones (máx 31 chars, empieza por letra).",
        },
        ["PublishErrorDisplayName"] = new()
        {
            [LangEn] = "Display name is required (1–50 characters).",
            [LangEs] = "El nombre visible es obligatorio (1–50 caracteres).",
        },
        ["PublishErrorAccent"] = new()
        {
            [LangEn] = "Accent colour must be a six-digit hex string like #c8102e.",
            [LangEs] = "El color de acento debe ser un hex de seis dígitos, ej. #c8102e.",
        },
        ["PublishErrorIcon"] = new()
        {
            [LangEn] = "Icon filename must end with .png and contain only letters, digits, dashes or underscores.",
            [LangEs] = "El nombre del icono debe acabar en .png y contener solo letras, dígitos, guiones o guiones bajos.",
        },
        ["PublishErrorBanner"] = new()
        {
            [LangEn] = "Banner filename must end with .png/.jpg/.jpeg and contain only safe characters.",
            [LangEs] = "El nombre del banner debe acabar en .png/.jpg/.jpeg y contener solo caracteres seguros.",
        },
        ["PublishErrorExecutable"] = new()
        {
            [LangEn] = "Executable must be a filename ending in .exe (e.g. age3y.exe).",
            [LangEs] = "El ejecutable debe ser un archivo terminado en .exe (ej. age3y.exe).",
        },
        ["PublishErrorWebsite"] = new()
        {
            [LangEn] = "Website must start with http:// or https://.",
            [LangEs] = "El sitio web debe empezar con http:// o https://.",
        },
        // -------- Multiplayer (v1.0) --------
        ["MpSubtabRooms"] = new() { [LangEn] = "Rooms", [LangEs] = "Salas" },
        // The Radmin assistant's door in the Rooms toolbar. It names the PROBLEM it
        // solves, not the tool: someone who needs this guide is precisely someone who
        // does not yet know what Radmin is. It also has to stay true once you ARE
        // connected — the window's own title, "Connect to the AoE3 network", would be
        // a lie then, since in that state it is a status panel rather than a guide.
        ["MpRoomsRadminHelp"] = new()
        {
            [LangEn] = "Help connecting",
            [LangEs] = "Ayuda para conectar",
        },
        ["MpRoomsRadminHelpTooltip"] = new()
        {
            [LangEn] = "How to connect with other players through Radmin VPN",
            [LangEs] = "Cómo conectarte con otros jugadores mediante Radmin VPN",
        },
        // Also the ProfileWindow's title and taskbar caption, since the subtab it named is
        // gone and the window is what replaced it.
        ["MpSubtabProfile"] = new() { [LangEn] = "Profile", [LangEs] = "Perfil" },
        ["MpSubtabHistory"] = new() { [LangEn] = "History", [LangEs] = "Historial" },

        // --- History tab (match log: players + duration + date) ---
        ["MpHistoryLoading"] = new() { [LangEn] = "Loading…", [LangEs] = "Cargando…" },
        ["MpHistoryEmpty"] = new()
        {
            [LangEn] = "No matches yet — your first game will appear here.",
            [LangEs] = "Todavía no hay partidas — tu primera partida aparecerá aquí.",
        },
        // Heads a side in a team match. 1-based because "Equipo 0" reads as a bug; the stored
        // number is 0-based and normalised by MatchTeamMap.
        ["MpHistoryTeam"] = new()
        {
            [LangEn] = "Team {0}",
            [LangEs] = "Equipo {0}",
        },
        ["MpHistoryPlayers"] = new() { [LangEn] = "{0} players", [LangEs] = "{0} jugadores" },
        ["MpHistoryReplay"] = new() { [LangEn] = "Replay", [LangEs] = "Repetición" },
        // Shown only for a match whose result was actually read. There is deliberately no
        // "Draw" label: 0.5 means "not known", and calling that a draw would invent one.
        ["MpHistoryWin"] = new() { [LangEn] = "Win", [LangEs] = "Victoria" },
        ["MpHistoryLoss"] = new() { [LangEn] = "Loss", [LangEs] = "Derrota" },
        // Per-player, in the roster under a match. Same rule as the badges above: a player
        // whose score is 0.5 gets NEITHER of these, because nobody could read who won.
        ["MpHistoryPlayerWon"] = new() { [LangEn] = "Won", [LangEs] = "Ganó" },
        ["MpHistoryPlayerLost"] = new() { [LangEn] = "Lost", [LangEs] = "Perdió" },

        // (The Profile tab's four one-line strings are gone with the four TextBlocks that
        //  showed them — MpProfileRating / MpProfileGames / MpProfileWinrate /
        //  MpProfileProvisional. The rules they carried did NOT go with them: the rate still
        //  divides by decided games, and it is now withheld entirely below the ladder's entry
        //  bar rather than being printed as "0 % wins" over one match. See the Perfil block
        //  above and ProfileSummaryView.)
        ["MpChatMatchRecorded"] = new()
        {
            [LangEn] = "Match recorded in your history ({0} players).",
            [LangEs] = "Partida registrada en tu historial ({0} jugadores).",
        },
        ["MpChatMatchNotRecorded"] = new()
        {
            [LangEn] = "Couldn't record the match (HTTP {0} · {1}).",
            [LangEs] = "No se pudo registrar la partida (HTTP {0} · {1}).",
        },
        // Only the host sees these — they are the visible confirmation that the result
        // was read from the recording instead of being logged as a draw.
        ["MpChatMatchResultWin"] = new()
        {
            [LangEn] = "Recorded as a win.",
            [LangEs] = "Registrada como victoria.",
        },
        ["MpChatMatchResultLoss"] = new()
        {
            [LangEn] = "Recorded as a loss.",
            [LangEs] = "Registrada como derrota.",
        },

        // --- Radmin VPN banner (reactive, three states) ---
        ["MpRadminNotInstalledTitle"] = new()
        {
            [LangEn] = "Multiplayer needs Radmin VPN",
            [LangEs] = "El multijugador necesita Radmin VPN",
        },
        ["MpRadminNotInstalledBody"] = new()
        {
            [LangEn] = "AoE3 LAN games discover each other through Radmin's virtual network. Lobby chat works without it; the actual game does not.",
            [LangEs] = "Las partidas LAN de AoE3 se descubren a través de la red virtual de Radmin. El chat del lobby funciona sin Radmin; la partida en sí no.",
        },
        ["MpRadminInstallButton"] = new() { [LangEn] = "Install Radmin VPN", [LangEs] = "Instalar Radmin VPN" },
        ["MpRadminInstalling"] = new()
        {
            [LangEn] = "Downloading and installing Radmin VPN… ({0}%)",
            [LangEs] = "Descargando e instalando Radmin VPN… ({0}%)",
        },
        ["MpRadminInstallFailed"] = new()
        {
            [LangEn] = "Auto-install failed. Opening Radmin's download page in your browser.",
            [LangEs] = "La auto-instalación falló. Abriendo la página de descarga de Radmin en tu navegador.",
        },

        ["MpRadminNotConnectedTitle"] = new()
        {
            [LangEn] = "Open Radmin and join the AoE3 network",
            [LangEs] = "Abre Radmin y únete a la red de AoE3",
        },
        ["MpRadminNotConnectedBody"] = new()
        {
            [LangEn] = "In Radmin, click \"Join network\" → \"Gaming\", then paste \"Age of Empires III: The Asian Dynasties\" (we'll copy it for you) and click Join.",
            [LangEs] = "En Radmin, clic en \"Unirse a la red\" → \"Gaming\", pega \"Age of Empires III: The Asian Dynasties\" (te lo copiamos al portapapeles) y clic Unirse.",
        },
        ["MpRadminNotConnectedBodyIp"] = new()
        {
            [LangEn] = "Your Radmin IP is already {0}. In Radmin, click \"Join network\" → \"Gaming\", paste \"Age of Empires III: The Asian Dynasties\" (we'll copy it for you) and click Join.",
            [LangEs] = "Tu IP de Radmin ya es {0}. En Radmin, clic en \"Unirse a la red\" → \"Gaming\", pega \"Age of Empires III: The Asian Dynasties\" (te lo copiamos al portapapeles) y clic Unirse.",
        },
        ["MpRadminOpenButton"] = new() { [LangEn] = "Open Radmin VPN", [LangEs] = "Abrir Radmin VPN" },
        // Title-bar connection chip. Replaces the permanent green Radmin banner:
        // when everything is fine this is ALL the user sees, so it has to say both
        // that the VPN is up and which address peers will reach them on.
        ["MpChipConnected"] = new() { [LangEn] = "Connected", [LangEs] = "Conectado" },
        ["MpChipVpnDetail"] = new() { [LangEn] = "VPN · {0}", [LangEs] = "VPN · {0}" },
        // Just the unit, for the places that render the number and the word at different
        // sizes. The same in both languages: it is the name of the rating scale.
        ["MpEloUnit"] = new() { [LangEn] = "ELO", [LangEs] = "ELO" },
        // Shown instead of the rating for somebody who has never played a rated match. Says
        // WHY there is no number rather than only that one is missing — and it is not a load
        // failure, which paints nothing at all.
        ["MpEloUnrated"] = new() { [LangEn] = "Unrated", [LangEs] = "Sin clasificar" },
        ["MpChipElo"] = new() { [LangEn] = "{0} ELO", [LangEs] = "{0} ELO" },
        ["MpChipReconnecting"] = new() { [LangEn] = "Reconnecting…", [LangEs] = "Reconectando…" },
        // Title-bar account menu. Sign out lives ONLY here now — the account row that
        // used to carry it was removed with the bar-2 redesign.
        // The account button's tooltip. It names the MENU, not one of its items: the click
        // opens a menu, so "Profile" here would be a promise the click does not keep.
        ["MpAccountMenuTooltip"] = new() { [LangEn] = "Your account", [LangEs] = "Tu cuenta" },
        ["MpAccountMenuProfile"] = new() { [LangEn] = "Profile", [LangEs] = "Perfil" },
        ["MpAccountMenuSignOut"] = new() { [LangEn] = "Sign out", [LangEs] = "Cerrar sesión" },
        // Centred day divider in the global chat. "Today" instead of a date, because a
        // date on the line you are reading right now is noise.
        ["MpChatToday"] = new() { [LangEn] = "TODAY", [LangEs] = "HOY" },
        // Join by code. The field sits in the rooms TOOLBAR, beside the search box — it was
        // a panel of its own under the list, whose height the rooms needed more. The title
        // and the hint no longer have a line to sit on: they are the field's tooltip, which
        // is why two full sentences are still worth their length.
        ["MpJoinByCodeTitle"] = new()
        {
            [LangEn] = "Were you given a room code?",
            [LangEs] = "¿Te pasaron un código de sala?",
        },
        ["MpJoinByCodeHint"] = new()
        {
            [LangEn] = "Private rooms don't show up in the list.",
            [LangEs] = "Las salas privadas no aparecen en la lista.",
        },
        ["MpJoinByCodePlaceholder"] = new() { [LangEn] = "room code", [LangEs] = "código" },
        ["MpJoinByCodeButton"] = new() { [LangEn] = "Enter", [LangEs] = "Entrar" },
        // Community-activity strip under the rooms list.
        ["MpActivityStripTitle"] = new()
        {
            [LangEn] = "Community activity",
            [LangEs] = "Actividad de la comunidad",
        },
        ["MpActivityRecentTitle"] = new() { [LangEn] = "RECENT MATCHES", [LangEs] = "ÚLTIMAS PARTIDAS" },
        // Two headings for one card, because the card has two sources. The community
        // list is what the strip promises; the personal one is the fallback for a
        // backend that cannot answer it, and calling THAT "community matches" would lie.
        ["MpActivityRecentCommunityTitle"] = new()
        {
            [LangEn] = "COMMUNITY MATCHES",
            [LangEs] = "PARTIDAS DE LA COMUNIDAD",
        },
        // Only ever written for a two-player match whose winner was actually read.
        ["MpActivityWon"] = new() { [LangEn] = "{0} beat {1}", [LangEs] = "{0} le ganó a {1}" },
        ["MpActivityAgo"] = new() { [LangEn] = "{0} ago", [LangEs] = "hace {0}" },

        // --- the community numbers, the middle third ---
        // ONE line, and it lives in the strip's HEADER row now — which was empty, so it costs
        // no height at all. As a footer under the recent matches it made that card the tallest
        // of the three, and they share a grid row, so that height was the whole strip's.
        // Each window travels with its own figure because they differ (matches over 30 days,
        // players over 7): the single "last {0} days" label this replaced could only ever have
        // restated one of them, and next to "(30 d)" it read as the same fact said twice.
        ["MpActivityTotalsCounts"] = new()
        {
            [LangEn] = "{0} matches ({1} d) · {2} players ({3} d)",
            [LangEs] = "{0} partidas ({1} d) · {2} jugadores ({3} d)",
        },
        // The map is LABELLED. It shipped bare for one round — "· ESOC Fertile Crescent" after
        // two labelled figures — and a proper noun does not announce itself as a map; reported
        // the same day. Appended to MpActivityTotalsCounts, and last, so a narrow window drops
        // the label together with the name it labels.
        ["MpActivityTotalsTopMap"] = new()
        {
            [LangEn] = "Most played: {0}",
            [LangEs] = "Mapa más jugado: {0}",
        },
        // Shown in place of the table while nobody qualifies, which after a ratings
        // reset is everybody for weeks. It names the requirement instead of leaving a
        // third of the strip blank.
        // No number any more: the table lists whoever has a RATED match, so there is no
        // threshold to quote. It used to promise entry at three decided matches while a
        // deviation filter nobody was told about was refusing everybody.
        // The number is BACK in this string. It lost its {0} when the entry bar was one rated
        // match — nothing worth quoting — and there is a bar again, so the empty state has to
        // name it or it describes a rule that is not the one being enforced. The figure comes
        // from the server (min_decided), never a literal here: those two have disagreed before,
        // and this text is where the player would have read the wrong one.
        ["MpActivityRankingEmpty"] = new()
        {
            [LangEn] = "Nobody is on the table yet — it takes {0} rated matches to enter, "
                     + "and a match only counts with a recording that says who won.",
            [LangEs] = "Todavía no hay nadie en la tabla: hacen falta {0} partidas puntuadas "
                     + "para entrar, y una partida sólo cuenta con una grabación que diga "
                     + "quién ganó.",
        },
        // A match whose result couldn't be read (no recording, or a team game). Most
        // stored matches are these, so the row says so instead of looking like a win.
        // The ladder card. "Decided" is the column that matters and the one that needs a
        // header of its own: most stored matches have no winner, so a table headed
        // "matches" would invite the reader to divide by the wrong number.
        // The Ranking subtab: the whole ladder, where the community strip shows only its
        // top three. Two ladders, one table, one payload.
        // Title case, like its four neighbours in the subtab bar. It was the only one
        // shouting, and the same key also heads the page. (MpActivityRankingTitle, the
        // community strip's card, stays uppercase — every card there is.)
        // ---------------------------------------------------------------
        // Tournaments and teams
        // ---------------------------------------------------------------
        ["DlgSettingsDemoStats"] = new()
        {
            [LangEn] = "Statistics preview",
            [LangEs] = "Vista previa de Estadísticas",
        },
        ["DlgSettingsDemoStatsHint"] = new()
        {
            [LangEn] = "Fills the Statistics page with sample community figures. The real "
                       + "civilization table needs hundreds of rated matches carrying a "
                       + "civilization, so it cannot be looked at yet by playing.",
            [LangEs] = "Llena la página de Estadísticas con cifras de ejemplo. La tabla real "
                       + "de civilizaciones necesita cientos de partidas puntuadas con "
                       + "civilización, así que todavía no se puede ver jugando.",
        },
        ["SettingsDemoStats"] = new()
        {
            [LangEn] = "Show it",
            [LangEs] = "Verla",
        },
        ["DlgSettingsDemoTournaments"] = new()
        {
            [LangEn] = "Tournament bracket preview",
            [LangEs] = "Vista previa del cuadro de torneo",
        },
        ["DlgSettingsDemoTournamentsHint"] = new()
        {
            [LangEn] = "Fills the Tournaments tab with four sample tournaments so their layout can "
                       + "be checked without running one. The buttons there do nothing.",
            [LangEs] = "Llena la pestaña Torneos con cuatro torneos de ejemplo para revisar cómo "
                       + "quedan sin tener que organizar uno. Sus botones no hacen nada.",
        },
        // --- Tournament demo mode (developer aid) ---------------------------
        // Sample TOURNAMENT names are localised; sample PLAYER names are not, because a
        // player name is a proper noun and nothing else here translates one.
        ["MpTournamentDemoBanner"] = new()
        {
            [LangEn] = "Sample data — nothing here came from a server, and the buttons do nothing.",
            [LangEs] = "Datos de ejemplo — nada de esto viene de un servidor, y los botones no hacen nada.",
        },
        ["MpTournamentDemoInertTitle"] = new()
        {
            [LangEn] = "This is a preview",
            [LangEs] = "Esto es una vista previa",
        },
        ["MpTournamentDemoInert"] = new()
        {
            [LangEn] = "The tournament on screen is made up, so this button has nothing to act on. "
                       + "Restart the launcher without --demo-tournaments to use the real thing.",
            [LangEs] = "El torneo que ves es inventado, así que este botón no tiene sobre qué actuar. "
                       + "Reinicia el launcher sin --demo-tournaments para usar el de verdad.",
        },
        ["MpTournamentDemoRunningName"] = new()
        {
            [LangEn] = "Sample Cup — sixteen players",
            [LangEs] = "Copa de ejemplo — dieciséis jugadores",
        },
        ["MpTournamentDemoTeamsName"] = new()
        {
            [LangEn] = "Sample Cup — teams of three",
            [LangEs] = "Copa de ejemplo — equipos de tres",
        },
        ["MpTournamentDemoRegistrationName"] = new()
        {
            [LangEn] = "Sample Cup — signing up",
            [LangEs] = "Copa de ejemplo — inscribiéndose",
        },
        ["MpTournamentDemoFinishedName"] = new()
        {
            [LangEn] = "Sample Cup — finished",
            [LangEs] = "Copa de ejemplo — terminada",
        },
        // Two more samples, and they exist for a structural reason rather than for variety:
        // a person is one entrant in a tournament and an entrant has one live match, so a
        // single viewer can never see more than one of Playable / JoinRoom / ReturnToRoom /
        // WaitingOpponent in the same bracket. Four states, four tournaments.
        // The seventh, and the first written from OUTSIDE the bracket: I run it and I play
        // in none of it. Every other sample answers "what about my match"; this one is the
        // only way to see the organiser's screen, where the answer is "none of them".
        ["MpTournamentDemoOrganiserName"] = new()
        {
            [LangEn] = "Sample Cup \u2014 I run it",
            [LangEs] = "Copa de ejemplo \u2014 la organizo yo",
        },
        // The watched room. Written as a dispute rather than small talk: what is being judged
        // here is whether looking into a room is worth building, and a room where nothing is
        // happening cannot answer that.
        // Does not name the round: the line above it already does, and a heading that says
        // "semifinal" twice in two type sizes reads as a rendering fault.
        ["MpTournamentDemoWatchRoomTitle"] = new()
        {
            [LangEn] = "Sample Cup \u00b7 room 2",
            [LangEs] = "Copa de ejemplo \u00b7 sala 2",
        },
        ["MpTournamentDemoWatchMod"] = new()
        {
            [LangEn] = "Wars of Liberty",
            [LangEs] = "Wars of Liberty",
        },
        ["MpTournamentDemoWatchChat1"] = new()
        {
            [LangEn] = "we lost the host for a moment, are we replaying it?",
            [LangEs] = "se nos cay\u00f3 el anfitri\u00f3n un momento, \u00bfrepetimos?",
        },
        ["MpTournamentDemoWatchChat2"] = new()
        {
            [LangEn] = "I was ahead, I would rather carry on",
            [LangEs] = "yo iba ganando, prefiero seguir",
        },
        ["MpTournamentDemoWatchChat3"] = new()
        {
            [LangEn] = "then let the organiser say",
            [LangEs] = "pues que lo diga la organizaci\u00f3n",
        },
        ["MpTournamentDemoWatchChat4"] = new()
        {
            [LangEn] = "agreed",
            [LangEs] = "de acuerdo",
        },
        ["MpTournamentDemoMyRoomName"] = new()
        {
            [LangEn] = "Sample Cup — my room is open",
            [LangEs] = "Copa de ejemplo — mi sala abierta",
        },
        ["MpTournamentDemoWaitingName"] = new()
        {
            [LangEn] = "Sample Cup — waiting for an opponent",
            [LangEs] = "Copa de ejemplo — esperando rival",
        },
        ["SettingsDemoTournaments"] = new()
        {
            [LangEn] = "Preview a tournament bracket",
            [LangEs] = "Ver un cuadro de torneo",
        },
        ["MpTournamentDialogTitle"] = new()
        {
            [LangEn] = "New tournament",
            [LangEs] = "Nuevo torneo",
        },
        ["MpTournamentDialogName"] = new()
        {
            [LangEn] = "Name",
            [LangEs] = "Nombre",
        },
        ["MpTournamentDialogFormat"] = new()
        {
            [LangEn] = "Format",
            [LangEs] = "Formato",
        },
        ["MpTournamentDialogTeamSource"] = new()
        {
            [LangEn] = "How teams are formed",
            [LangEs] = "Cómo se forman los equipos",
        },
        ["MpTournamentSourceRegistered"] = new()
        {
            [LangEn] = "A captain enters a saved team",
            [LangEs] = "Un capitán inscribe un equipo guardado",
        },
        ["MpTournamentSourceAdhoc"] = new()
        {
            [LangEn] = "A captain picks a line-up when entering",
            [LangEs] = "El capitán arma la alineación al inscribirse",
        },
        ["MpTournamentSourceDraft"] = new()
        {
            [LangEn] = "Everyone enters alone and I make the teams",
            [LangEs] = "Cada uno se apunta solo y yo hago los equipos",
        },
        ["MpTournamentDialogEntryMode"] = new()
        {
            [LangEn] = "Who gets in",
            [LangEs] = "Quién entra",
        },
        ["MpTournamentEntryOpen"] = new()
        {
            [LangEn] = "First come, first served",
            [LangEs] = "Por orden de llegada",
        },
        ["MpTournamentEntryApproval"] = new()
        {
            [LangEn] = "I accept each one",
            [LangEs] = "Yo acepto uno por uno",
        },
        ["MpTournamentDialogCapacity"] = new()
        {
            [LangEn] = "Places",
            [LangEs] = "Plazas",
        },
        ["MpTournamentDialogCapacityHint"] = new()
        {
            [LangEn] = "Sixteen is the ceiling: a first round that size already needs "
                       + "half the rooms the server allows at once.",
            [LangEs] = "Dieciséis es el tope: una primera ronda así ya necesita la "
                       + "mitad de las salas que el servidor permite a la vez.",
        },
        ["MpSubtabTournaments"] = new()
        {
            [LangEn] = "Tournaments",
            [LangEs] = "Torneos",
        },
        // The dialog's primary. NOT MpTournamentCreate, which is the list's "+ New
        // tournament" and is the caption of the very window this button sits in - a button
        // named after its own window says what the window is, not what pressing it does.
        ["MpTournamentCreateAction"] = new()
        {
            [LangEn] = "Create tournament",
            [LangEs] = "Crear torneo",
        },
        // ONE WORD, and the reason is measurable. It reads under a "Tournaments" heading in a
        // 300px column, so the heading supplies the noun and the button only has to supply the
        // verb - which is also what the handoff's own "+ Nuevo" did. "New tournament" at 125%
        // text in English needs 169px beside a 130px heading in 279px of usable column, and
        // what gave way was the TITLE, clipped mid-word with no ellipsis. Pinned by
        // TheTournamentsCreateButtonFitsItsColumnInBothLanguages.
        ["MpTournamentCreate"] = new()
        {
            [LangEn] = "New",
            [LangEs] = "Nuevo",
        },
        ["MpTournamentsEmpty"] = new()
        {
            [LangEn] = "No tournaments yet. Create the first one.",
            [LangEs] = "Todavía no hay torneos. Crea el primero.",
        },
        ["MpTournamentsPickOne"] = new()
        {
            [LangEn] = "Pick a tournament to see its bracket.",
            [LangEs] = "Elige un torneo para ver su cuadro.",
        },
        // A backend that predates tournaments answers 404. That is a state to render, not
        // a failure to report.
        ["MpTournamentsUnavailable"] = new()
        {
            [LangEn] = "Tournaments are not available on this server yet.",
            [LangEs] = "Este servidor todavía no tiene torneos.",
        },

        ["MpTournamentStatusDraft"] = new()
        {
            [LangEn] = "Not published",
            [LangEs] = "Sin publicar",
        },
        ["MpTournamentStatusRegistration"] = new()
        {
            [LangEn] = "Registration open",
            [LangEs] = "Inscripción abierta",
        },
        ["MpTournamentStatusReady"] = new()
        {
            [LangEn] = "Registration closed",
            [LangEs] = "Inscripción cerrada",
        },
        ["MpTournamentStatusRunning"] = new()
        {
            [LangEn] = "In progress",
            [LangEs] = "En curso",
        },
        ["MpTournamentStatusFinished"] = new()
        {
            [LangEn] = "Finished",
            [LangEs] = "Terminado",
        },
        ["MpTournamentStatusCancelled"] = new()
        {
            [LangEn] = "Cancelled",
            [LangEs] = "Cancelado",
        },
        // Archived for inactivity. It crowns nobody, which is why it is not "Finished".
        ["MpTournamentStatusAbandoned"] = new()
        {
            [LangEn] = "Abandoned",
            [LangEs] = "Abandonado",
        },

        ["MpTournamentPlaces"] = new()
        {
            [LangEn] = "{0} of {1} places",
            [LangEs] = "{0} de {1} plazas",
        },
        ["MpTournamentEntrants"] = new()
        {
            [LangEn] = "ENTRANTS",
            [LangEs] = "PARTICIPANTES",
        },
        ["MpTournamentChampion"] = new()
        {
            [LangEn] = "Champion: {0}",
            [LangEs] = "Campeón: {0}",
        },
        ["MpTournamentTbd"] = new()
        {
            [LangEn] = "TBD",
            [LangEs] = "Por definir",
        },

        ["MpTournamentEntrantConfirmed"] = new()
        {
            [LangEn] = "In",
            [LangEs] = "Dentro",
        },
        ["MpTournamentEntrantWaitlist"] = new()
        {
            [LangEn] = "Waiting",
            [LangEs] = "En espera",
        },
        ["MpTournamentEntrantPending"] = new()
        {
            [LangEn] = "Applied",
            [LangEs] = "Solicitado",
        },
        ["MpTournamentEntrantWithdrawn"] = new()
        {
            [LangEn] = "Withdrew",
            [LangEs] = "Se retiró",
        },
        ["MpTournamentEntrantRejected"] = new()
        {
            [LangEn] = "Rejected",
            [LangEs] = "Rechazado",
        },
        ["MpTournamentEntrantDisqualified"] = new()
        {
            [LangEn] = "Disqualified",
            [LangEs] = "Descalificado",
        },

        ["MpTournamentEnter"] = new()
        {
            [LangEn] = "Enter",
            [LangEs] = "Inscribirme",
        },
        ["MpTournamentWithdraw"] = new()
        {
            [LangEn] = "Withdraw",
            [LangEs] = "Retirarme",
        },
        ["MpTournamentOpenRegistration"] = new()
        {
            [LangEn] = "Open registration",
            [LangEs] = "Abrir inscripción",
        },
        ["MpTournamentCloseRegistration"] = new()
        {
            [LangEn] = "Close registration",
            [LangEs] = "Cerrar inscripción",
        },
        ["MpTournamentSeed"] = new()
        {
            [LangEn] = "Seed",
            [LangEs] = "Sembrar",
        },
        ["MpTournamentStart"] = new()
        {
            [LangEn] = "Draw the bracket",
            [LangEs] = "Generar el cuadro",
        },
        ["MpTournamentCancel"] = new()
        {
            [LangEn] = "Cancel tournament",
            [LangEs] = "Cancelar torneo",
        },
        ["MpTournamentAccept"] = new()
        {
            [LangEn] = "Accept",
            [LangEs] = "Aceptar",
        },
        ["MpTournamentReject"] = new()
        {
            [LangEn] = "Reject",
            [LangEs] = "Rechazar",
        },

        ["MpTournamentRoundFinal"] = new()
        {
            [LangEn] = "FINAL",
            [LangEs] = "FINAL",
        },
        ["MpTournamentRoundSemi"] = new()
        {
            [LangEn] = "SEMI-FINALS",
            [LangEs] = "SEMIFINALES",
        },
        ["MpTournamentRoundQuarter"] = new()
        {
            [LangEn] = "QUARTER-FINALS",
            [LangEs] = "CUARTOS",
        },
        ["MpTournamentRoundN"] = new()
        {
            [LangEn] = "ROUND {0}",
            [LangEs] = "RONDA {0}",
        },

        ["MpTournamentPlayMyMatch"] = new()
        {
            [LangEn] = "Open my room",
            [LangEs] = "Abrir mi sala",
        },
        ["MpTournamentJoinRoom"] = new()
        {
            [LangEn] = "Enter the room",
            [LangEs] = "Entrar en la sala",
        },
        ["MpTournamentReturnToRoom"] = new()
        {
            [LangEn] = "Back to my room",
            [LangEs] = "Volver a mi sala",
        },
        ["MpTournamentWaitingOpponent"] = new()
        {
            [LangEn] = "Waiting for an opponent",
            [LangEs] = "Esperando rival",
        },
        // Teams are picked inside AoE3, not here. Getting them wrong means the match does
        // not rate AND the bracket does not move, so the card says it before you go in.
        ["MpTournamentSidesWarning"] = new()
        {
            [LangEn] = "Pick these same sides inside the game, or the match will not count.",
            [LangEs] = "Elige estos mismos bandos dentro del juego, o la partida no contará.",
        },

        // --- Co-organisers: people the OWNER lets help run one tournament ---
        // Never "admin" or "moderator" in the copy: there is no such thing on this server,
        // the grant is one tournament wide, and calling it a role would promise otherwise.
        ["MpTournamentMakeManager"] = new()
        {
            [LangEn] = "Make co-organiser",
            [LangEs] = "Hacer co-organizador",
        },
        ["MpTournamentManagers"] = new()
        {
            [LangEn] = "CO-ORGANISERS",
            [LangEs] = "CO-ORGANIZADORES",
        },
        ["MpTournamentRemoveManager"] = new()
        {
            [LangEn] = "Remove as co-organiser",
            [LangEs] = "Quitar como co-organizador",
        },
        // The fallback for a server that sends ids and no names. A row of identifiers would
        // not be honest; a count is.
        ["MpTournamentManagerCount"] = new()
        {
            [LangEn] = "{0} co-organisers",
            [LangEs] = "{0} co-organizadores",
        },
        // --- The owner settling a match by hand, and throwing somebody out ---
        // Both confirmations say the same load-bearing thing: from the launcher this does not
        // come back. Undoing a bracket result is `tournament:void`, which is the maintainer's
        // CLI on purpose, so the question is the only place the owner can be told.
        ["MpTournamentAward"] = new()
        {
            [LangEn] = "Decide this match",
            [LangEs] = "Decidir esta partida",
        },
        ["MpTournamentAwardTo"] = new()
        {
            [LangEn] = "{0} wins",
            [LangEs] = "Gana {0}",
        },
        ["MpTournamentAwardConfirmTitle"] = new()
        {
            [LangEn] = "Decide this match?",
            [LangEs] = "¿Decidir esta partida?",
        },
        ["MpTournamentAwardConfirmBody"] = new()
        {
            [LangEn] = "{0} goes through and the bracket advances. It counts as a walkover, "
                     + "not as a game that was played, so no result is recorded for either "
                     + "side.\n\nThis cannot be undone from the launcher.",
            [LangEs] = "{0} pasa de ronda y el cuadro avanza. Cuenta como incomparecencia, no "
                     + "como una partida jugada, así que no se registra resultado para "
                     + "ninguno de los dos.\n\nEsto no se puede deshacer desde el launcher.",
        },
        ["MpTournamentAwardConfirmYes"] = new()
        {
            [LangEn] = "Yes, decide it",
            [LangEs] = "Sí, decidirla",
        },
        ["MpTournamentDisqualify"] = new()
        {
            [LangEn] = "Disqualify",
            [LangEs] = "Descalificar",
        },
        ["MpTournamentDisqualifyConfirmTitle"] = new()
        {
            [LangEn] = "Disqualify?",
            [LangEs] = "¿Descalificar?",
        },
        ["MpTournamentDisqualifyConfirmBody"] = new()
        {
            [LangEn] = "{0} is out of the tournament. Every pending match of theirs whose "
                     + "opponent is already known is awarded to that opponent, so this can "
                     + "settle several matches at once.\n\nThis cannot be undone from the "
                     + "launcher.",
            [LangEs] = "{0} queda fuera del torneo. Cada partida suya pendiente cuyo rival ya "
                     + "se conoce se le da a ese rival, así que esto puede resolver varias "
                     + "partidas de golpe.\n\nEsto no se puede deshacer desde el launcher.",
        },
        ["MpTournamentDisqualifyConfirmYes"] = new()
        {
            [LangEn] = "Yes, disqualify",
            [LangEs] = "Sí, descalificar",
        },
        ["MpTournamentOutcomeWalkover"] = new()
        {
            [LangEn] = "W.O.",
            [LangEs] = "W.O.",
        },
        ["MpTournamentOutcomeDq"] = new()
        {
            [LangEn] = "Disq.",
            [LangEs] = "Desc.",
        },
        ["MpTournamentOutcomeBye"] = new()
        {
            [LangEn] = "BYE",
            [LangEs] = "PASA",
        },

        ["MpTournamentActionFailed"] = new()
        {
            [LangEn] = "That didn't work",
            [LangEs] = "No se pudo",
        },
        ["MpTournamentErrClosed"] = new()
        {
            [LangEn] = "Registration for this tournament is not open.",
            [LangEs] = "La inscripción de este torneo no está abierta.",
        },
        ["MpTournamentErrFull"] = new()
        {
            [LangEn] = "This tournament has no places left.",
            [LangEs] = "Este torneo no tiene plazas libres.",
        },
        ["MpTournamentErrLimit"] = new()
        {
            [LangEn] = "You already have as many tournaments running as you may. Finish or cancel one first.",
            [LangEs] = "Ya tienes tantos torneos en marcha como puedes. Termina o cancela uno antes.",
        },
        ["MpTournamentErrNotReady"] = new()
        {
            [LangEn] = "This match is not ready to be played yet.",
            [LangEs] = "Esta partida todavía no se puede jugar.",
        },
        ["MpTournamentErrNotParticipant"] = new()
        {
            [LangEn] = "That room belongs to a tournament match between two other players.",
            [LangEs] = "Esa sala es de una partida de torneo entre otros dos jugadores.",
        },
        ["MpTournamentErrAlreadyEntered"] = new()
        {
            [LangEn] = "You are already entered in this tournament.",
            [LangEs] = "Ya estás inscrito en este torneo.",
        },
        ["MpTournamentErrRoster"] = new()
        {
            [LangEn] = "That line-up cannot enter: check the size, and that nobody is already in.",
            [LangEs] = "Esa alineación no puede entrar: revisa el tamaño y que nadie esté ya dentro.",
        },
        ["MpTournamentErrForbidden"] = new()
        {
            [LangEn] = "Only the person who created this tournament can do that.",
            [LangEs] = "Eso solo lo puede hacer quien creó el torneo.",
        },
        ["MpTournamentWrongModTitle"] = new()
        {
            [LangEn] = "Wrong mod",
            [LangEs] = "Mod incorrecto",
        },
        ["MpTournamentWrongModBody"] = new()
        {
            [LangEn] = "This tournament is played on {0}. Switch to it from the Play tab first.",
            [LangEs] = "Este torneo se juega en {0}. Cambia a ese mod desde la pestaña Jugar.",
        },

        ["MpTournamentToastReady"] = new()
        {
            [LangEn] = "Your tournament match is ready",
            [LangEs] = "Tu partida de torneo está lista",
        },
        ["MpTournamentToastRoomOpened"] = new()
        {
            [LangEn] = "Your opponent opened the room",
            [LangEs] = "Tu rival abrió la sala",
        },
        ["MpTournamentToastWon"] = new()
        {
            [LangEn] = "You won your tournament match",
            [LangEs] = "Ganaste tu partida de torneo",
        },
        ["MpTournamentToastLost"] = new()
        {
            [LangEn] = "Your tournament match is decided",
            [LangEs] = "Tu partida de torneo está decidida",
        },
        ["MpTournamentToastAccepted"] = new()
        {
            [LangEn] = "You are in",
            [LangEs] = "Estás dentro",
        },
        ["MpTournamentToastPromoted"] = new()
        {
            [LangEn] = "A place freed up and it is yours",
            [LangEs] = "Se liberó una plaza y es tuya",
        },

        ["MpTeamErrFull"] = new()
        {
            [LangEn] = "That team has no room for another player.",
            [LangEs] = "Ese equipo no tiene sitio para otro jugador.",
        },
        ["MpTeamErrNotCaptain"] = new()
        {
            [LangEn] = "Only the captain of that team can do that.",
            [LangEs] = "Eso solo lo puede hacer el capitán del equipo.",
        },

        // ---------------------------------------------------------------
        // Tournaments and Statistics, design handoff 8a-8c / 9a-9b.
        //
        // The bracket card, the entrant table, the four-step progress and the two halves
        // of the Statistics page. Nothing here is a label for its own sake: every one of
        // them exists because the screen was making a claim it could not support, or
        // making none where one was needed.
        // ---------------------------------------------------------------

        // A slot whose occupant is not decided yet. NOT "TBD": this says where the player
        // will come from, which is the one useful thing a bracket can say about an empty
        // slot, and it is what turns a wall of "to be decided" into a readable tree.
        // The action bar over the bracket. It names the tie the way the bracket names it, so
        // the bar reads as belonging to the card that was clicked.
        ["MpTournamentVersus"] = new()
        {
            [LangEn] = "{0} vs {1}",
            [LangEs] = "{0} contra {1}",
        },
        ["MpTournamentBarPlaying"] = new()
        {
            [LangEn] = "{0} \u00b7 being played now",
            [LangEs] = "{0} \u00b7 jug\u00e1ndose ahora",
        },
        // An unresolved slot when the long form does not fit. The long form is NOT gone - it
        // is the tooltip - because a column of "to be decided" is what made the top half of a
        // bracket unreadable, which is why FeederLabel exists at all. What changed is that a
        // truncated "Ganador de Rioplatense \u00b7 TercioVi\u2026" told you neither thing.
        ["MpTournamentSlotUndecided"] = new()
        {
            [LangEn] = "to be decided",
            [LangEs] = "por definir",
        },
        // Ordering an undecided tie replayed. Never "anular": nothing is undone, because
        // nothing was decided - the slot was already open and this says so out loud.
        ["MpTournamentReplay"] = new()
        {
            [LangEn] = "Have them play it again",
            [LangEs] = "Que la repitan",
        },
        ["MpTournamentReplayConfirmTitle"] = new()
        {
            [LangEn] = "Play the match again?",
            [LangEs] = "\u00bfQue repitan la partida?",
        },
        ["MpTournamentReplayConfirmBody"] = new()
        {
            [LangEn] = "{0} and {1} will be told to play their match again, and any room still "
                     + "open for it is closed. Nothing that has already been decided changes.",
            [LangEs] = "Se avisa a {0} y {1} de que vuelvan a jugar su partida, y se cierra la "
                     + "sala que siga abierta. No cambia nada de lo ya decidido.",
        },
        ["MpTournamentReplayConfirmYes"] = new()
        {
            [LangEn] = "Have them play again",
            [LangEs] = "Que la repitan",
        },
        ["MpTournamentWinnerOf"] = new()
        {
            [LangEn] = "Winner of {0} · {1}",
            [LangEs] = "Ganador de {0} · {1}",
        },
        // On the row of the card that is mine, and on the list entry I own. Short because
        // they sit inside a chip beside a name that must stay readable.
        ["MpTournamentYouTag"] = new()
        {
            [LangEn] = "YOU",
            [LangEs] = "TÚ",
        },
        ["MpTournamentYourTeamTag"] = new()
        {
            [LangEn] = "YOUR TEAM",
            [LangEs] = "TU EQUIPO",
        },
        ["MpTournamentMineTag"] = new()
        {
            [LangEn] = "MINE",
            [LangEs] = "MÍO",
        },
        // The capsule at the top right of an open tournament. It is the first thing
        // somebody looks for, and before this the only way to find it was to read the
        // whole bracket.
        ["MpTournamentYourTurnIn"] = new()
        {
            [LangEn] = "Your match is in the {0}",
            [LangEs] = "Te toca jugar en {0}",
        },
        ["MpTournamentYourTurn"] = new()
        {
            [LangEn] = "Your turn to play",
            [LangEs] = "Te toca jugar",
        },
        // The organiser's door into a match being played. WATCH, never join: a seat is a
        // different request with a different answer, and this one is for the person who may
        // have to settle the match afterwards.
        ["MpTournamentWatchRoom"] = new()
        {
            [LangEn] = "Watch the room",
            [LangEs] = "Ver la sala",
        },
        ["MpWatchWindowTitle"] = new()
        {
            [LangEn] = "Watching a match",
            [LangEs] = "Viendo una partida",
        },
        ["MpWatchRoster"] = new()
        {
            [LangEn] = "In the room",
            [LangEs] = "En la sala",
        },
        ["MpWatchChat"] = new()
        {
            [LangEn] = "Room chat",
            [LangEs] = "Chat de la sala",
        },
        ["MpWatchSend"] = new()
        {
            [LangEn] = "Send",
            [LangEs] = "Enviar",
        },
        ["MpWatchInGameFor"] = new()
        {
            [LangEn] = "in game for {0} min",
            [LangEs] = "en partida desde hace {0} min",
        },
        // Says what this window IS, inside the window. A preview that only admits to being
        // one in a commit message is a preview somebody screenshots as a promise.
        ["MpWatchPreviewNote"] = new()
        {
            [LangEn] = "Preview. The server does not allow this yet: a room that has started "
                     + "refuses every join, and a tournament room admits only the two players "
                     + "of that match.",
            [LangEs] = "Previsualizaci\u00f3n. El servidor todav\u00eda no permite esto: una sala que ya "
                     + "empez\u00f3 rechaza cualquier entrada, y una sala de torneo solo admite a los "
                     + "dos jugadores de ese cruce.",
        },
        ["MpTournamentInProgress"] = new()
        {
            [LangEn] = "Being played now",
            [LangEs] = "Jugándose ahora",
        },
        ["MpTournamentWaitingRoom"] = new()
        {
            [LangEn] = "Waiting for the room",
            [LangEs] = "Esperando que abran sala",
        },
        ["MpTournamentCreatedByYou"] = new()
        {
            [LangEn] = "created by you",
            [LangEs] = "creado por ti",
        },
        ["MpTournamentRoundOfTotal"] = new()
        {
            [LangEn] = "round {0} of {1}",
            [LangEs] = "ronda {0} de {1}",
        },
        ["MpTournamentPlacesShort"] = new()
        {
            [LangEn] = "{0} of {1} places",
            [LangEs] = "{0} de {1} plazas",
        },
        ["MpTournamentPlayerCount"] = new()
        {
            [LangEn] = "{0} players",
            [LangEs] = "{0} jugadores",
        },
        ["MpTournamentRequests"] = new()
        {
            [LangEn] = "{0} requests",
            [LangEs] = "{0} solicitudes",
        },
        ["MpTournamentRequestsOne"] = new()
        {
            [LangEn] = "1 request",
            [LangEs] = "1 solicitud",
        },
        ["MpTournamentSeeEntrants"] = new()
        {
            [LangEn] = "Entrants",
            [LangEs] = "Participantes",
        },
        ["MpTournamentMoreActions"] = new()
        {
            [LangEn] = "More actions",
            [LangEs] = "Más acciones",
        },
        // The sides warning, promoted from grey body text to an amber box that names the
        // team. It is the rule that decides whether the match counts at all: the sides are
        // chosen inside AoE3, and if they come out wrong everybody is reported on team 0,
        // the match is refused as not-a-1v1, and the bracket does not move.
        ["MpTournamentSidesWarningTeam"] = new()
        {
            [LangEn] = "Inside the game, the {0} players must all be on ONE side. If the "
                       + "sides do not match, the match does not count and the bracket "
                       + "does not move.",
            [LangEs] = "Dentro del juego, los de {0} tienen que ir juntos en un bando. Si "
                       + "los bandos no coinciden, la partida no cuenta y el cuadro no "
                       + "avanza.",
        },

        // The four steps. The third is why this bar exists: seeding is what blocks the
        // start, and nothing on the screen used to say so.
        ["MpTournamentStepCreated"] = new()
        {
            [LangEn] = "Created",
            [LangEs] = "Creado",
        },
        ["MpTournamentStepRegistration"] = new()
        {
            [LangEn] = "Registration",
            [LangEs] = "Inscripción",
        },
        ["MpTournamentStepSeeds"] = new()
        {
            [LangEn] = "Seeds",
            [LangEs] = "Semillas",
        },
        ["MpTournamentStepRunning"] = new()
        {
            [LangEn] = "Under way",
            [LangEs] = "En curso",
        },

        // The line under the primary action: what the next step needs, or what is missing.
        ["MpTournamentNextOpen"] = new()
        {
            [LangEn] = "Opening registration makes the tournament visible to everybody.",
            [LangEs] = "Al abrir la inscripción el torneo se hace visible para todos.",
        },
        ["MpTournamentNextClose"] = new()
        {
            [LangEn] = "Closing registration lets you assign the seeds. Until every "
                       + "confirmed entrant has one, the tournament cannot start.",
            [LangEs] = "Al cerrar la inscripción podrás asignar las semillas. Hasta que "
                       + "todos los confirmados tengan una, el torneo no puede empezar.",
        },
        ["MpTournamentNextSeed"] = new()
        {
            [LangEn] = "Hand out the seeds and the bracket can be drawn.",
            [LangEs] = "Reparte las semillas y ya se puede generar el cuadro.",
        },
        ["MpTournamentNextStart"] = new()
        {
            [LangEn] = "Drawing the bracket opens the first round's matches.",
            [LangEs] = "Al generar el cuadro empiezan las partidas de la primera ronda.",
        },
        ["MpTournamentBlockedSeeds"] = new()
        {
            [LangEn] = "{0} confirmed entrants have no seed yet, so the bracket cannot "
                       + "be drawn.",
            [LangEs] = "{0} confirmados no tienen semilla todavía, así que el cuadro no "
                       + "se puede generar.",
        },
        ["MpTournamentBlockedTooFew"] = new()
        {
            [LangEn] = "At least two confirmed entrants are needed.",
            [LangEs] = "Hacen falta al menos dos confirmados.",
        },

        // The three groups. They are groups and not one list because the entrant statuses
        // are not points on a single axis: one waits on the owner, one is in the bracket,
        // and one is not playing.
        ["MpTournamentGroupRequests"] = new()
        {
            [LangEn] = "APPLICATIONS",
            [LangEs] = "SOLICITUDES",
        },
        ["MpTournamentGroupIn"] = new()
        {
            [LangEn] = "IN",
            [LangEs] = "DENTRO",
        },
        ["MpTournamentGroupOut"] = new()
        {
            [LangEn] = "OUT OF THE BRACKET",
            [LangEs] = "FUERA DEL CUADRO",
        },
        ["MpTournamentColEntrant"] = new()
        {
            [LangEn] = "ENTRANT",
            [LangEs] = "PARTICIPANTE",
        },
        ["MpTournamentColStatus"] = new()
        {
            [LangEn] = "STATUS",
            [LangEs] = "ESTADO",
        },
        // The row that stops the tournament starting. Amber, in its own column, and said
        // in words: a blank seed cell explains nothing.
        ["MpTournamentNoSeed"] = new()
        {
            [LangEn] = "No seed",
            [LangEs] = "Sin semilla",
        },
        ["MpTournamentAskedToEnter"] = new()
        {
            [LangEn] = "asked to enter",
            [LangEs] = "pidió entrar",
        },
        ["MpTournamentGivePlace"] = new()
        {
            [LangEn] = "Give a place",
            [LangEs] = "Dar plaza",
        },

        // Cancelling, moved away from the other actions. It used to sit beside "Enter"
        // in the same blue at the same weight.
        ["MpTournamentDangerZone"] = new()
        {
            [LangEn] = "DANGER ZONE",
            [LangEs] = "ZONA DE PELIGRO",
        },
        ["MpTournamentCancelTitle"] = new()
        {
            [LangEn] = "Cancel the tournament",
            [LangEs] = "Cancelar el torneo",
        },
        ["MpTournamentCancelBody"] = new()
        {
            [LangEn] = "The {0} entrants are told, and it cannot be undone. Matches "
                       + "already played still count towards the ladder.",
            [LangEs] = "Se avisa a los {0} inscritos y no se puede deshacer. Las partidas "
                       + "ya jugadas siguen contando para el ELO.",
        },

        // ---------------------------------------------------------------
        // The new-tournament dialog: help text that follows the selection.
        //
        // What it replaces was a four-line amber paragraph whose example was a 3v3 while
        // 1v1 was selected - static copy that contradicted the thing it was explaining.
        // ---------------------------------------------------------------
        // The proposed name, mirroring "Sala de {0}" in the room dialog. Truncated to
        // MaxNameLength by the dialog: a long mod name must not open the field in a state
        // it would itself refuse.
        ["MpTournamentDialogDefaultName"] = new()
        {
            [LangEn] = "{0} tournament",
            [LangEs] = "Torneo de {0}",
        },
        ["MpTournamentDialogNameShort"] = new()
        {
            [LangEn] = "{0} more character(s): a name needs at least {1}.",
            [LangEs] = "Faltan {0}: el nombre necesita al menos {1} caracteres.",
        },
        ["MpTournamentDialogNameCount"] = new()
        {
            [LangEn] = "{0}/{1}",
            [LangEs] = "{0}/{1}",
        },
        ["MpTournamentWhyFormatSolo"] = new()
        {
            [LangEn] = "In a 1v1 everybody enters alone. Teams appear when you pick 2v2 "
                       + "or 3v3.",
            [LangEs] = "En 1v1 cada uno se apunta solo. Los equipos aparecen al elegir "
                       + "2v2 o 3v3.",
        },
        ["MpTournamentWhyFormatTeam"] = new()
        {
            [LangEn] = "A bracket slot holds a whole team of {0}, and its line-up is "
                       + "frozen when it enters.",
            [LangEs] = "Un hueco del cuadro contiene un equipo entero de {0}, y su "
                       + "alineación se congela al inscribirse.",
        },
        ["MpTournamentWhyEntryOpen"] = new()
        {
            [LangEn] = "Anybody past the last place goes on the waiting list.",
            [LangEs] = "Los que lleguen de más quedan en espera.",
        },
        ["MpTournamentWhyEntryApproval"] = new()
        {
            [LangEn] = "Nobody takes a place until you accept them.",
            [LangEs] = "Nadie ocupa plaza hasta que tú lo aceptes.",
        },
        // The multiplication, resolved. "Places" are counted in ENTRANTS, so eight places
        // of 3v3 is twenty-four people and four simultaneous first-round rooms - which is
        // the part worth knowing before choosing.
        ["MpTournamentWhyCapacity"] = new()
        {
            [LangEn] = "{0} × {1} = {2} players in total · first round of {3} rooms at "
                       + "once · {4} rounds to the final",
            [LangEs] = "{0} × {1} = {2} jugadores en total · primera ronda de {3} salas a "
                       + "la vez · {4} rondas hasta la final",
        },

        // ---------------------------------------------------------------
        // Statistics, design handoff 9a / 9b.
        // ---------------------------------------------------------------

        // The head counts. TWO numbers where there was one: the server's total carries no
        // `rated` predicate, and the page printed it under the words "rated matches" for a
        // build. The gap between them is the interesting part.
        ["MpStatsHeadRated"] = new()
        {
            [LangEn] = "{0} rated of {1} matches",
            [LangEs] = "{0} puntuadas de {1} partidas",
        },
        // The fallback for a backend that sends no rated count. Saying how many matches there
        // were is still true; calling them rated would not be.
        ["MpStatsHeadMatches"] = new()
        {
            [LangEn] = "{0} matches",
            [LangEs] = "{0} partidas",
        },
        ["MpStatsHeadMaps"] = new()
        {
            [LangEn] = "{0} maps",
            [LangEs] = "{0} mapas",
        },

        // How many matches actually moved a rating, and why the rest did not. Both figures
        // come from columns that existed since the rating rules were written and that no
        // endpoint read until now.
        ["MpStatsHealthTitle"] = new()
        {
            [LangEn] = "MATCHES THAT COUNTED",
            [LangEs] = "PARTIDAS QUE CONTARON",
        },
        ["MpStatsHealthCounted"] = new()
        {
            [LangEn] = "counted",
            [LangEs] = "contaron",
        },
        ["MpStatsHealthNotCounted"] = new()
        {
            [LangEn] = "did not",
            [LangEs] = "no contaron",
        },
        // The reason is the server's own identifier and is NOT translated: it is the word
        // somebody greps the logs for, and a translated one would not be findable.
        ["MpStatsHealthReason"] = new()
        {
            [LangEn] = "{0} of them: {1}",
            [LangEs] = "{0} de ellas: {1}",
        },

        ["MpStatsActivityTitle"] = new()
        {
            [LangEn] = "WHEN ROOMS OPEN",
            [LangEs] = "CUÁNDO SE ABREN SALAS",
        },
        ["MpStatsActivePlayers"] = new()
        {
            [LangEn] = "players in {0} days",
            [LangEs] = "jugadores en {0} días",
        },
        // Says which clock, and says what is being counted. The server measures rooms being
        // OPENED, not games being played - rooms are stamped server-side and never deleted,
        // while a match only exists if somebody's game got reported at all. Drawing one and
        // calling it the other is the mislabel this page just finished removing.
        ["MpStatsActivityPeak"] = new()
        {
            [LangEn] = "Busiest around {0}, your time. It counts rooms being opened, not "
                       + "games being played.",
            [LangEs] = "La hora más movida es sobre las {0}, en tu horario. Cuenta cuándo se "
                       + "abren salas, no cuándo se juega.",
        },

        // The deck table is opt-in on this side and the server cannot tell. An empty table
        // under a live heading reads as broken; this says whose data it would be and how to
        // be one of them.
        // Said under the table when the card names could not be read. The identifiers are
        // still shown - hiding the table to avoid admitting a limit would cost a whole
        // feature - and this is the sentence that stops them reading as a bug.
        ["MpStatsDecksNotResolved"] = new()
        {
            [LangEn] = "The card names come from the mod's own files, so they only appear "
                       + "once that mod is installed. Until then these are the internal "
                       + "identifiers.",
            [LangEs] = "Los nombres de las cartas salen de los archivos del propio mod, así "
                       + "que solo aparecen con ese mod instalado. Mientras tanto, estos son "
                       + "los identificadores internos.",
        },
        // The MIXED state: this mod's files were read and answered for some of these cards but
        // not the others, which is what happens when the table still holds rows another mod
        // contributed. Says which mod was consulted, because "not found" is only informative
        // next to "where".
        ["MpStatsDecksPartlyResolved"] = new()
        {
            [LangEn] = "Some of these cards are not in this mod's files, so they are shown by "
                     + "their internal identifier.",
            [LangEs] = "Algunas de estas cartas no están en los archivos de este mod, así "
                     + "que se muestran con su identificador interno.",
        },
        ["MpStatsDecksEmptyTitle"] = new()
        {
            [LangEn] = "Nobody has shared a deck yet",
            [LangEs] = "Todavía nadie ha compartido un mazo",
        },
        ["MpStatsDecksEmptyBody"] = new()
        {
            [LangEn] = "This table is built from the decks players share, and nobody has "
                       + "shared one for this mod yet. It says which cards people BRING - no "
                       + "recording carries the card that was actually played.",
            [LangEs] = "Esta tabla se construye con los mazos que comparten los jugadores, y "
                       + "todavía nadie ha compartido uno de este mod. Dice qué cartas TRAE "
                       + "la gente: ninguna grabación lleva la carta que se jugó.",
        },
        ["MpStatsDecksEmptyAction"] = new()
        {
            // PRIVACY, not Multiplayer. The switch hangs off PrivacyHeader in
            // LauncherSettingsDialog.xaml; this line had been sending people to the wrong
            // section since it was written.
            [LangEn] = "You share yours by default; it is in Settings, under Privacy.",
            [LangEs] = "Tú compartes los tuyos por defecto; está en Ajustes, en Privacidad.",
        },
        ["MpStatsWindowDays"] = new()
        {
            [LangEn] = "last {0} days",
            [LangEs] = "últimos {0} días",
        },
        ["MpStatsMapsOver"] = new()
        {
            [LangEn] = "over {0} matches",
            [LangEs] = "sobre {0} partidas",
        },
        ["MpStatsTailMaps"] = new()
        {
            [LangEn] = "{0} more maps, one match each",
            [LangEs] = "{0} mapas más, una partida cada uno",
        },
        ["MpStatsTailMapsWhy"] = new()
        {
            [LangEn] = "A map played once says nothing about anybody's preferences, so "
                       + "the tail is grouped instead of taking a row each.",
            [LangEs] = "Un mapa con una sola partida no dice nada de las preferencias de "
                       + "nadie, así que la cola se agrupa en vez de ocupar una fila cada uno.",
        },
        ["MpStatsSeeAll"] = new()
        {
            [LangEn] = "See all",
            [LangEs] = "Ver todos",
        },
        ["MpStatsCivsNoData"] = new()
        {
            [LangEn] = "No data yet",
            [LangEs] = "Aún sin datos",
        },
        ["MpStatsCivsCount"] = new()
        {
            [LangEn] = "{0} of {1} matches carry a civilization",
            [LangEs] = "{0} de {1} partidas traen civilización",
        },
        ["MpStatsCivsWhy"] = new()
        {
            [LangEn] = "The launcher only started recording each player's civilization "
                       + "recently. This table fills up with the matches played from then "
                       + "on: in the older ones there is no way to work it out.",
            [LangEs] = "El launcher empezó a registrar la civilización de cada jugador "
                       + "hace poco. Esta tabla se llena con las partidas que se jueguen "
                       + "desde entonces: en las viejas no hay forma de deducirla.",
        },
        ["MpStatsCivsMany"] = new()
        {
            [LangEn] = "This mod ships hundreds of civilizations, so most of them will sit "
                       + "without a percentage for a long time. That is the normal case "
                       + "here, not the exception.",
            [LangEs] = "Este mod trae cientos de civilizaciones, así que la mayoría "
                       + "estará mucho tiempo sin porcentaje. Ese es el caso normal aquí, "
                       + "no la excepción.",
        },
        ["MpStatsOrderNote"] = new()
        {
            [LangEn] = "ordered by matches played",
            [LangEs] = "orden por partidas jugadas",
        },
        // The two rules, said under the table. Somebody who sees a 58 % below a 56 %
        // needs to know why, or the table looks broken rather than honest.
        ["MpStatsCivsRules"] = new()
        {
            [LangEn] = "The percentage is over DECIDED matches, and is only published from "
                       + "{0} of them. The order is by matches played: ordering by "
                       + "percentage would put whoever won their only match on top.",
            [LangEs] = "El porcentaje se calcula sobre partidas DECIDIDAS y solo se "
                       + "publica a partir de {0}. Se ordena por partidas jugadas: "
                       + "ordenar por porcentaje pondría arriba a quien ganó su única "
                       + "partida.",
        },
        ["MpStatsTailCivs"] = new()
        {
            [LangEn] = "{0} more civilizations, with fewer than {1} matches",
            [LangEs] = "{0} civilizaciones más, con menos de {1} partidas",
        },
        ["MpStatsTailCivsWhy"] = new()
        {
            [LangEn] = "no percentage until there is a sample",
            [LangEs] = "sin porcentaje hasta que haya muestra",
        },
        ["MpStatsColWins"] = new()
        {
            [LangEn] = "WINS",
            [LangEs] = "VICTORIAS",
        },
        ["MpStatsHowMeasured"] = new()
        {
            [LangEn] = "HOW IT IS MEASURED",
            [LangEs] = "CÓMO SE MIDE",
        },
        ["MpStatsHowRated"] = new()
        {
            [LangEn] = "Rated matches only: the ones the server accepted with a recording.",
            [LangEs] = "Solo partidas puntuadas: las que el servidor aceptó con grabación.",
        },
        ["MpStatsHowDecided"] = new()
        {
            [LangEn] = "The percentage is over the DECIDED ones, not the played ones.",
            [LangEs] = "El porcentaje se calcula sobre las DECIDIDAS, no sobre las jugadas.",
        },
        ["MpStatsHowNoSample"] = new()
        {
            [LangEn] = "A civilization without a sample shows a dash, never an invented "
                       + "percentage.",
            [LangEs] = "Una civilización sin muestra muestra un guión, nunca un "
                       + "porcentaje inventado.",
        },
        ["MpStatsHowOld"] = new()
        {
            [LangEn] = "Matches older than the civilization recording carry none, and stay "
                       + "out of this table.",
            [LangEs] = "Las partidas anteriores al registro de civilización no la traen y "
                       + "quedan fuera de esta tabla.",
        },
        ["MpSubtabRanking"] = new()
        {
            [LangEn] = "Ranking",
            [LangEs] = "Clasificación",
        },
        ["MpRankingModeSolo"] = new()
        {
            [LangEn] = "1v1",
            [LangEs] = "1v1",
        },
        ["MpRankingModeTeam"] = new()
        {
            [LangEn] = "TEAMS",
            [LangEs] = "EQUIPOS",
        },
        // Shown when the server answered but has no table to give — as opposed to having
        // one that nobody qualifies for yet, which says so with MpActivityRankingEmpty.
        ["MpRankingUnavailable"] = new()
        {
            [LangEn] = "There is no ranking to show yet.",
            [LangEs] = "Todavía no hay clasificación que mostrar.",
        },
        // --- Perfil (design handoff 3c) ---
        // Shown in place of the page when nobody is signed in. The tab has a sign-in gate of
        // its own on the Rooms subtab; this is the one line Profile needs so it is not blank.
        ["MpSignInPrompt"] = new()
        {
            [LangEn] = "Sign in with Discord to see your profile.",
            [LangEs] = "Inicia sesión con Discord para ver tu perfil.",
        },
        ["MpProfileRatingLabel"] = new()
        {
            [LangEn] = "RATING 1v1",
            [LangEs] = "RATING 1v1",
        },
        ["MpProfileJoined"] = new()
        {
            [LangEn] = "joined {0}",
            [LangEs] = "se unió en {0}",
        },
        ["MpProfileRank"] = new()
        {
            [LangEn] = "rank {0} of {1}",
            [LangEs] = "puesto {0} de {1}",
        },
        // "Not on the ladder yet", said in one word. NOT the Glicko sense of provisional —
        // see ProfileSummaryView.IsProvisional for why that one is true of practically anyone.
        ["MpProfileProvisionalTag"] = new()
        {
            [LangEn] = "PROVISIONAL",
            [LangEs] = "PROVISIONAL",
        },
        ["MpProfileCurveTitle"] = new()
        {
            [LangEn] = "RATING OVER TIME",
            [LangEs] = "EVOLUCIÓN DEL RATING",
        },
        // With one point there is no line to draw, and a flat stroke would be a claim about a
        // rating that has held steady — which is false for somebody who has played once.
        ["MpProfileCurveTooFew"] = new()
        {
            [LangEn] = "Not enough rated matches yet to draw the curve.",
            [LangEs] = "Todavía no hay suficientes partidas puntuadas para dibujar la curva.",
        },
        ["MpProfileCurveFrom"] = new()
        {
            [LangEn] = "{0} start",
            [LangEs] = "{0} inicial",
        },
        ["MpProfileCurveTo"] = new()
        {
            [LangEn] = "{0} now",
            [LangEs] = "{0} actual",
        },
        ["MpProfileRecordTitle"] = new()
        {
            [LangEn] = "RECORD",
            [LangEs] = "RÉCORD",
        },
        // Below the ladder's entry bar the record is stated WITHOUT a percentage: a rate over
        // one match is not a rate, and the one it produced ("0 % wins") was the most
        // discouraging number the launcher could have led with.
        ["MpProfileRecordDecided"] = new()
        {
            [LangEn] = "in {0} decided",
            [LangEs] = "en {0} decididas",
        },
        ["MpProfileRecordPercent"] = new()
        {
            [LangEn] = "{0}% of {1} decided",
            [LangEs] = "{0}% de {1} decididas",
        },
        // The distance to the ladder, in matches rather than in units of rating deviation.
        // The number is the server's min_decided, never a literal.
        ["MpProfileToLadder"] = new()
        {
            [LangEn] = "{0} more rated matches and your rating stops being provisional.",
            [LangEs] = "Faltan {0} partidas puntuadas para que el rating deje de ser provisional.",
        },
        ["MpProfileOnLadder"] = new()
        {
            [LangEn] = "Your rating is settled — you are on the table.",
            [LangEs] = "Tu rating ya está asentado: apareces en la tabla.",
        },
        ["MpProfileTotalMatches"] = new()
        {
            [LangEn] = "MATCHES PLAYED",
            [LangEs] = "PARTIDAS TOTALES",
        },
        // All three numbers, because the first alone misleads: most matches are not decided,
        // so "3 matches" beside a record of 0-1 reads as a contradiction until the rest is
        // said.
        ["MpProfileTotalBreakdown"] = new()
        {
            [LangEn] = "{0} decided · {1} didn't count",
            [LangEs] = "{0} decididas · {1} sin contar",
        },
        // --- Profile: which civilizations you play -------------------------------
        // --- Clasificación: the community's civilization balance ------------------
        ["MpRankingModeCivs"] = new() { [LangEn] = "CIVS", [LangEs] = "CIVS" },
        ["MpSubtabStats"] = new() { [LangEn] = "Statistics", [LangEs] = "Estadísticas" },
        // Fixed: this shipped as "Mapas mas jugados", without the accent.
        // ⚠ COMMUNITY, and the name says so. These used to be MpStatsDecksTitle/Hint, which
        // the profile's own deck section already owned — a dictionary initializer is indexer
        // assignment, so the later declaration won and this table silently rendered "Your
        // decks" over a community aggregate. Caught by NoKeyIsDeclaredTwice.
        // --- The community-cards table, folded by civilization ---
        // The census beside the section label. It states what the table HAS, and nothing
        // beyond it: a claim about matches or players would be a different number that this
        // route cannot support.
        ["MpStatsDecksCardCount"] = new()
        {
            [LangEn] = "{0} distinct cards",
            [LangEs] = "{0} cartas distintas",
        },
        ["MpStatsDecksCivCards"] = new()
        {
            [LangEn] = "{0} cards",
            [LangEs] = "{0} cartas",
        },
        // Count and share in one cell. The share only appears when there is sample behind it;
        // below the minimum the count goes in alone and nothing takes the percentage's place.
        ["MpStatsDecksCountAndShare"] = new()
        {
            [LangEn] = "{0} \u00b7 {1} %",
            [LangEs] = "{0} \u00b7 {1} %",
        },
        ["MpStatsTailDecks"] = new()
        {
            [LangEn] = "{0} more cards, seen once",
            [LangEs] = "{0} cartas m\u00e1s, vistas una vez",
        },
        ["MpStatsTailDecksWhy"] = new()
        {
            [LangEn] = "A card somebody brought once says nothing about what the community "
                     + "prefers, so those are summed up in one line instead of taking a row each.",
            [LangEs] = "Una carta que alguien llev\u00f3 una sola vez no dice nada de lo que "
                     + "prefiere la comunidad, as\u00ed que esas se resumen en una l\u00ednea en vez de "
                     + "ocupar una fila cada una.",
        },
        ["MpStatsDecksMoreCivs"] = new()
        {
            [LangEn] = "{0} more civilizations",
            [LangEs] = "{0} civilizaciones m\u00e1s",
        },
        ["MpStatsCommunityDecksTitle"] = new()
        {
            [LangEn] = "Cards the community brings",
            [LangEs] = "Cartas que lleva la comunidad",
        },
        // Every clause here is load-bearing. "Bring" and not "play", because no recording
        // carries the card that was played. The contributor count, because this is opt-in and
        // a table built from three people must say so rather than pass for the community.
        ["MpStatsCommunityDecksHint"] = new()
        {
            [LangEn] = "From {0} players who chose to share their decks. These are the cards "
                     + "they TAKE into a match, not the ones they sent — the game does not "
                     + "record that anywhere the launcher can read.",
            [LangEs] = "De {0} jugadores que eligieron compartir sus mazos. Son las cartas que "
                     + "LLEVAN a la partida, no las que enviaron — eso el juego no lo guarda "
                     + "en ningún lado que el launcher pueda leer.",
        },
        // Settings -> Privacy, beside the telemetry switch.
        ["DlgSettingsShareDecks"] = new()
        {
            [LangEn] = "Share my decks with the community table",
            [LangEs] = "Compartir mis mazos con la tabla de la comunidad",
        },
        ["DlgSettingsShareDecksHint"] = new()
        {
            [LangEn] = "On by default. Sends the card names in your decks, per civilization, "
                     + "so Multiplayer → Statistics can show which cards people bring. No deck "
                     + "names, no matches, no dates. Turning it off stops it; what you already "
                     + "sent is replaced the next time you share and stays otherwise.",
            [LangEs] = "Encendido por defecto. Envía los nombres de las cartas de tus mazos, por "
                     + "civilización, para que Multijugador → Estadísticas muestre qué cartas "
                     + "lleva la gente. Ni nombres de mazos, ni partidas, ni fechas. Apagarlo "
                     + "lo detiene; lo que ya enviaste se reemplaza la próxima vez que "
                     + "compartas y por lo demás queda ahí.",
        },
        // The two segments of the ladder switch. Kept as short as the ranking's own, which
        // they sit beside on the same page: a segment whose label wraps stops reading as a
        // switch.
        ["MpStatsModeSolo"] = new()
        {
            [LangEn] = "1v1",
            [LangEs] = "1v1",
        },
        ["MpStatsModeTeam"] = new()
        {
            [LangEn] = "Teams",
            [LangEs] = "Equipos",
        },
        // In a team game "matchup" is ambiguous - every pair of civilizations in a 3v3 met,
        // and half of them were on the same side - so the two tables say which is which in
        // their titles rather than leaving it to the footnote.
        ["MpStatsRivalsTitle"] = new()
        {
            [LangEn] = "Civilizations against each other",
            [LangEs] = "Civilizaciones enfrentadas",
        },
        ["MpStatsRivalsHint"] = new()
        {
            [LangEn] = "Rated team games only, counting pairs on opposite sides. The record is "
                     + "the first civilization's. A percentage appears once a pairing has enough "
                     + "decided games behind it.",
            [LangEs] = "Sólo partidas por equipos puntuadas, contando parejas de bandos "
                     + "contrarios. El récord es el de la primera civilización. El porcentaje "
                     + "aparece cuando el enfrentamiento tiene suficientes partidas decididas "
                     + "detrás.",
        },
        // Not "matchup": these two civilizations were on the same side, and the column heading
        // saying otherwise contradicted the footnote directly under the same table.
        ["MpStatsAlliesColPair"] = new()
        {
            [LangEn] = "Pairing",
            [LangEs] = "Pareja",
        },
        ["MpStatsAlliesTitle"] = new()
        {
            [LangEn] = "Civilizations played together",
            [LangEs] = "Civilizaciones que juegan juntas",
        },
        // Says what the record means here, because the column headings are the same as the
        // table above and would otherwise read as one civilization beating its own ally.
        ["MpStatsAlliesHint"] = new()
        {
            [LangEn] = "Rated team games only, counting pairs on the SAME side. The record is "
                     + "how that pairing did together, so both civilizations won or lost the "
                     + "same games.",
            [LangEs] = "Sólo partidas por equipos puntuadas, contando parejas del MISMO bando. "
                     + "El récord es el de la pareja jugando junta, así que las dos "
                     + "civilizaciones ganaron o perdieron las mismas partidas.",
        },
        ["MpStatsFormatsTitle"] = new()
        {
            [LangEn] = "Team formats",
            [LangEs] = "Formatos por equipos",
        },
        // The fallback for a participant count that does not halve. It should never happen -
        // the server only rates 2v2 and 3v3 as team games - and if it does, saying the raw
        // count beats inventing a format nobody played.
        ["MpStatsFormatPlayers"] = new()
        {
            [LangEn] = "{0} players",
            [LangEs] = "{0} jugadores",
        },
        ["MpStatsMatchupsTitle"] = new()
        {
            [LangEn] = "Civilization matchups",
            [LangEs] = "Enfrentamientos entre civilizaciones",
        },
        // Says the three things that make the numbers readable, because each of them makes a
        // reader distrust the table if they find it out on their own: only 1v1, only rated, and
        // the record belongs to the FIRST civilization of the pair.
        ["MpStatsMatchupsHint"] = new()
        {
            [LangEn] = "Rated 1v1 only. The record is the first civilization's. A percentage "
                     + "appears once a pairing has enough decided games behind it.",
            [LangEs] = "Sólo 1v1 puntuado. El récord es el de la primera civilización. El "
                     + "porcentaje aparece cuando el enfrentamiento tiene suficientes partidas "
                     + "decididas detrás.",
        },
        ["MpMatchupColPair"] = new()
        {
            [LangEn] = "MATCHUP",
            [LangEs] = "ENFRENTAMIENTO",
        },
        // The pair as ONE string. Still used, as the row's tooltip: the cell itself is built
        // from parts now so a flag can sit beside each civilization, and an ellipsised pair
        // needs somewhere to say the whole thing.
        ["MpMatchupPair"] = new()
        {
            [LangEn] = "{0} vs {1}",
            [LangEs] = "{0} vs {1}",
        },
        // The separator alone, for the built cell. Kept as a string rather than a literal
        // because it is a word, and a word is the kind of thing that gets translated.
        ["MpMatchupVs"] = new()
        {
            [LangEn] = "vs",
            [LangEs] = "vs",
        },
        ["MpStatsMapsTitle"] = new()
        {
            [LangEn] = "Most-played maps",
            [LangEs] = "Mapas más jugados",
        },
        // The two halves of the profile page. Short on purpose: they sit in a row that also
        // carries the page, and a long caption there costs width the ladder needs.
        ["MpProfileSectionOverview"] = new()
        {
            [LangEn] = "Profile",
            [LangEs] = "Perfil",
        },
        ["MpProfileSectionDecks"] = new()
        {
            [LangEn] = "Decks",
            [LangEs] = "Mazos",
        },
        ["MpStatsDecksLoading"] = new()
        {
            [LangEn] = "Reading your decks from the game...",
            [LangEs] = "Leyendo tus mazos del juego...",
        },
        ["MpStatsDecksTitle"] = new()
        {
            [LangEn] = "Your decks",
            [LangEs] = "Tus mazos",
        },
        // Says outright that this is what the player BRINGS. A deck holds 25 cards and a match
        // may use five, so letting anyone read it as "cards played" would overstate it by a
        // factor nothing on screen could reveal.
        ["MpStatsDecksHint"] = new()
        {
            [LangEn] = "Read from the game, one per civilization you have played. These are the "
                     + "cards you TAKE into a match, not the ones you ended up sending.",
            [LangEs] = "Leídos del juego, uno por civilización que hayas jugado. Son las cartas "
                     + "que LLEVAS a la partida, no las que terminaste enviando.",
        },
        ["MpStatsDecksEmpty"] = new()
        {
            [LangEn] = "No decks yet. Build one in the game's home city and it will show up here.",
            [LangEs] = "Todavía no hay mazos. Arma uno en la ciudad natal del juego y aparecerá acá.",
        },
        ["MpCivsTitle"] = new()
        {
            [LangEn] = "Civilization balance",
            [LangEs] = "Balance de civilizaciones",
        },
        // How much is behind the table comes first, because with a handful of matches it is the
        // single most important thing on the page.
        ["MpCivsSubtitle"] = new()
        {
            [LangEn] = "{0} civilizations, over {1} rated matches",
            [LangEs] = "{0} civilizaciones, sobre {1} partidas puntuadas",
        },
        ["MpCivsFootnote"] = new()
        {
            [LangEn] = "Rated 1v1 only, and separated by mod version — a figure that mixed two "
                     + "builds would stop meaning anything the moment one of them changed. A win "
                     + "rate appears only once a civilization has enough decided matches.",
            [LangEs] = "Sólo 1v1 puntuados, y separados por versión del mod: un número que "
                     + "mezclara dos compilaciones dejaría de significar algo apenas cambiara "
                     + "una. El porcentaje aparece sólo cuando una civilización tiene "
                     + "suficientes partidas decididas.",
        },
        ["MpCivsLoading"] = new()
        {
            [LangEn] = "Loading...",
            [LangEs] = "Cargando...",
        },
        // The honest empty state. It WILL be what everybody sees for a while, so it says why.
        ["MpCivsEmpty"] = new()
        {
            [LangEn] = "No matches with a civilization yet. The launcher started recording which "
                     + "one each player used, and this fills in from the matches played with it "
                     + "— there is no way to work it out for older games.",
            [LangEs] = "Todavía no hay partidas con civilización. El launcher empezó a registrar "
                     + "cuál usó cada jugador, y esto se llena con las partidas que se jueguen "
                     + "desde entonces: no hay forma de deducirla en las partidas viejas.",
        },
        ["MpCivColCiv"] = new() { [LangEn] = "CIVILIZATION", [LangEs] = "CIVILIZACIÓN" },
        ["MpCivColPlayed"] = new() { [LangEn] = "PLAYED", [LangEs] = "JUGADAS" },
        ["MpCivColLength"] = new() { [LangEn] = "LENGTH", [LangEs] = "DURACIÓN" },
        ["MpProfileCivsTitle"] = new()
        {
            [LangEn] = "YOUR CIVILIZATIONS",
            [LangEs] = "TUS CIVILIZACIONES",
        },
        // Says both things a player needs: nothing is here yet, and why.
        ["MpProfileCivsEmpty"] = new()
        {
            [LangEn] = "Nothing here yet. The launcher started recording which civilization each "
                     + "player used, so this fills in from your next matches.",
            [LangEs] = "Todavía nada acá. El launcher empezó a registrar qué civilización usó cada "
                     + "jugador, así que esto se va llenando con tus próximas partidas.",
        },
        // Matches, then the record. Both are facts however few they are, unlike a percentage.
        ["MpProfileCivsRecord"] = new()
        {
            [LangEn] = "{0}  ·  {1}-{2}",
            [LangEs] = "{0}  ·  {1}-{2}",
        },
        // The window is named because this is computed from the history page, not from every
        // match ever played — see BuildProfileCivs.
        ["MpProfileCivsWindow"] = new()
        {
            [LangEn] = "Over your last {0} matches. A win rate is only shown once a civilization "
                     + "has enough decided games behind it.",
            [LangEs] = "Sobre tus últimas {0} partidas. El porcentaje sólo aparece cuando una "
                     + "civilización tiene suficientes partidas decididas detrás.",
        },
        ["MpProfileTopMap"] = new()
        {
            [LangEn] = "MOST PLAYED MAP",
            [LangEs] = "MAPA MÁS JUGADO",
        },
        ["MpProfileTopMapCount"] = new()
        {
            [LangEn] = "{0} of {1} matches",
            [LangEs] = "{0} de {1} partidas",
        },
        ["MpProfileRival"] = new()
        {
            [LangEn] = "USUAL OPPONENT",
            [LangEs] = "RIVAL HABITUAL",
        },
        ["MpProfileRivalRecord"] = new()
        {
            [LangEn] = "{0}-{1} against them",
            [LangEs] = "{0}-{1} contra él",
        },
        // (The "Latest matches" block is gone, and with it its three strings. It was a
        //  three-row excerpt of the history list under a link back to the History tab — and
        //  the full list is right there on this page now, so the excerpt was showing the same
        //  matches twice and the link led to where you already were.)

        // --- Historial (design handoff 3b) ---
        ["MpHistoryFilterAll"] = new() { [LangEn] = "All", [LangEs] = "Todas" },
        ["MpHistoryFilterRated"] = new() { [LangEn] = "Rated", [LangEs] = "Puntuadas" },
        ["MpHistoryFilterUnrated"] = new() { [LangEn] = "Didn't count", [LangEs] = "Sin contar" },
        // Shown when the FILTER emptied the list, not when there is no history at all —
        // those are different situations, and MpHistoryEmpty would send somebody looking for
        // matches that are one click away.
        ["MpHistoryFilterEmpty"] = new()
        {
            [LangEn] = "No matches match this filter.",
            [LangEs] = "No hay partidas que cumplan este filtro.",
        },
        // (The four summary cells are gone with the History tab. Every one of them is on the
        //  Profile already: the rating in its header, the decided record in RECORD, the
        //  "didn't count" tally in MATCHES PLAYED and the map in MOST PLAYED MAP. Keeping
        //  them would have been the same four numbers twice on one page.)
        // First line of a card. It replaces the two pills the card used to carry, which said
        // the same thing twice: a "Loss" badge beside a "-117" badge.
        ["MpHistoryAgainst"] = new()
        {
            [LangEn] = "against {0}",
            [LangEs] = "contra {0}",
        },
        ["MpHistoryRatingMove"] = new()
        {
            [LangEn] = "{0} → {1}",
            [LangEs] = "{0} → {1}",
        },
        // The tag on a match the ladder ignored. NOT "draw" — 0.5 is "the outcome could
        // not be read", and most stored matches are that.
        ["MpHistoryNotCounted"] = new()
        {
            [LangEn] = "DIDN'T COUNT",
            [LangEs] = "NO CONTÓ",
        },
        ["MpHistorySeeHow"] = new() { [LangEn] = "See how", [LangEs] = "Ver cómo" },
        // The heading over a day's matches when the timestamp could not be read. Such a match
        // is still listed — dropping somebody's match over a malformed date would be
        // worse — and this says so instead of printing a year 1 date as a day they played.
        ["MpHistoryDayUnknown"] = new()
        {
            [LangEn] = "DATE UNKNOWN",
            [LangEs] = "FECHA DESCONOCIDA",
        },
        // An em dash, for a value that is not merely zero but unknown. One key so every
        // surface writes the same character.
        ["MpDash"] = new() { [LangEn] = "—", [LangEs] = "—" },
        // --- Clasificacion (design handoff 3a) ---
        // How many players are on the table, beside the title. It says "with decided matches"
        // because that is literally the entry rule, and a bare count would read as "this is
        // how many people play", which is a different and much larger number.
        ["MpRankSubtitle"] = new()
        {
            [LangEn] = "{0} players with decided matches",
            [LangEs] = "{0} jugadores con partidas decididas",
        },
        // The two scope chips. They STATE the scope, they do not change it —
        // /stats/community takes neither a mod nor a window — so the window's number comes
        // from the server rather than being written here, where it would go stale the day the
        // server's did.
        ["MpRankScopeWindow"] = new()
        {
            [LangEn] = "{0} days",
            [LangEs] = "{0} días",
        },
        // Column headings. RATING replaces ELO: the launcher says "rating" everywhere else,
        // and DECID. is abbreviated because the column is 74px and Spanish is the wide
        // language (DECIDIDAS does not fit beside a RECORD column that did not use to exist).
        ["MpRankColRating"] = new()
        {
            [LangEn] = "RATING",
            [LangEs] = "RATING",
        },
        ["MpRankColDecided"] = new()
        {
            [LangEn] = "DECID.",
            [LangEs] = "DECID.",
        },
        // Wins-losses. The column DECIDED alone says how many matches were settled and nothing
        // about how they went, so the count invited a comparison it could not support.
        ["MpRankColRecord"] = new()
        {
            [LangEn] = "W-L",
            [LangEs] = "V-D",
        },
        ["MpRankRecordValue"] = new()
        {
            [LangEn] = "{0}-{1}",
            [LangEs] = "{0}-{1}",
        },
        ["MpRankPercentValue"] = new()
        {
            [LangEn] = "{0}%",
            [LangEs] = "{0}%",
        },
        // The footnote under the table. It names the entry rule with the SERVER's number,
        // never a literal: the two have disagreed before, and this is exactly where a player
        // would read the wrong one.
        ["MpRankFootnote"] = new()
        {
            [LangEn] = "It takes {0} rated matches to enter the table, and a match only counts "
                     + "with a recording that says who won. Your own row stays pinned to the "
                     + "bottom while it is out of sight.",
            [LangEs] = "Hacen falta {0} partidas puntuadas para entrar en la tabla, y una "
                     + "partida sólo cuenta con una grabación que diga quién ganó. Tu "
                     + "propia fila queda fija al pie mientras no esté a la vista.",
        },
        ["MpRankEloHelp"] = new()
        {
            [LangEn] = "How the rating works",
            [LangEs] = "Cómo funciona el ELO",
        },
        ["MpActivityRankingTitle"] = new() { [LangEn] = "RANKING", [LangEs] = "CLASIFICACI\u00D3N" },
        ["MpActivityRankColHash"] = new() { [LangEn] = "#", [LangEs] = "#" },
        ["MpActivityRankColPlayer"] = new() { [LangEn] = "PLAYER", [LangEs] = "JUGADOR" },
        // (RankColElo and RankColDecided are gone: the table says RATING, like the rest of the
        //  launcher, and abbreviates DECID. because the column is 74px and had to make room for
        //  a RECORD column. Their replacements are MpRankColRating / MpRankColDecided in the
        //  Clasificación block above. Hash, Player and Pct survive unchanged.)
        ["MpActivityRankColPct"] = new() { [LangEn] = "%", [LangEs] = "%" },
        // Peak hours. The source is rooms OPENED, not matches played, and the wording says
        // so on purpose: the two are not the same number and we are not going to imply
        // they are. Hours are the viewer's own local time.
        ["MpActivityPeakTitle"] = new() { [LangEn] = "PEAK HOURS", [LangEs] = "HORAS PUNTA" },
        ["MpActivityPeakSubtitle"] = new()
        {
            [LangEn] = "when rooms get opened \u2014 {0} in the last {1} days, your local time",
            [LangEs] = "cuando se abren salas \u2014 {0} en los \u00FAltimos {1} d\u00EDas, en tu hora local",
        },
        // The handoff's sentence, with the window's two hours emphasised inside it rather
        // than shouted above it as a separate headline. The {0}/{1} are wrapped in a bold Run
        // by FillPeakHours, so the placeholders must stay whole words in every language.
        // One hour, on its own, for the two emphasised values inside MpActivityPeakLine.
        ["MpActivityPeakHour"] = new() { [LangEn] = "{0}:00", [LangEs] = "{0}:00" },
        // Shown in place of the bars when the payload could not be read. Deliberately NOT
        // shown when the community is simply quiet: fewer than twenty rooms in a month is an
        // answer, and dressing it as a failure would be the same conflation in reverse.
        ["MpActivityPeakUnavailable"] = new()
        {
            [LangEn] = "Couldn\u2019t load the busiest hours. It will try again shortly.",
            [LangEs] = "No se pudieron cargar las horas punta. Lo vuelve a intentar en un momento.",
        },
        // 429. Its own sentence because "try again" is the wrong advice: the quota is per
        // address and per day, so two launchers on one connection can spend it by lunchtime
        // and nothing the player does brings it back sooner.
        ["MpActivityPeakRateLimited"] = new()
        {
            [LangEn] = "Too many requests from this connection today. The busiest hours will "
                     + "be back tomorrow.",
            [LangEs] = "Demasiadas peticiones desde esta conexi\u00f3n hoy. Las horas punta "
                     + "vuelven ma\u00f1ana.",
        },
        ["MpActivityPeakLine"] = new()
        {
            [LangEn] = "More people around between {0} and {1}",
            [LangEs] = "Hay más gente entre las {0} y {1}",
        },
        // Kept although the handoff has no such line: the sample and the window have to travel
        // WITH the claim, or the card starts lying the day that constant moves.
        ["MpActivityPeakSample"] = new()
        {
            [LangEn] = "{0} rooms opened in the last {1} days, your local time",
            [LangEs] = "{0} salas abiertas en los últimos {1} días, en tu hora local",
        },
        ["MpActivityRankingSeeAll"] = new() { [LangEn] = "See all", [LangEs] = "Ver todo" },
        // The viewer's own row in the strip's ranking, per the handoff.
        ["MpActivityYou"] = new() { [LangEn] = "you", [LangEs] = "tú" },
        // ONE BAR, ONE HOUR. The histogram briefly ran on three-hour buckets and this key
        // sat unused beside a MpActivityPeakBucketTip that named a stretch; the bars are back
        // to one per hour, so this is live again and that one is gone. The bars carry no axis
        // of any kind, so this tooltip is the ONLY thing that says which hour a bar is.
        ["MpActivityPeakBarTip"] = new()
        {
            [LangEn] = "{0}:00 \u2014 {1} rooms",
            [LangEs] = "{0}:00 \u2014 {1} salas",
        },
        ["MpActivityNotCounted"] = new() { [LangEn] = "didn't count", [LangEs] = "no contó" },
        // Quick replies above the chat composer. These are TYPED into the box, so they
        // are the player's own words — keep them short and natural in both languages
        // rather than literal translations of each other.
        ["MpQuickReplyAnyone"] = new() { [LangEn] = "Anyone playing?", [LangEs] = "¿Alguien juega?" },
        ["MpQuickReplyGg"] = new() { [LangEn] = "gg", [LangEs] = "gg" },
        ["MpQuickReplyMinute"] = new() { [LangEn] = "1 min", [LangEs] = "1 min" },
        // Room-opened card in the chat flow. {0} is the host's login.
        ["MpChatRoomOpened"] = new()
        {
            [LangEn] = "{0} opened a room",
            [LangEs] = "{0} abrió una sala",
        },
        ["MpRoomsSearchPlaceholder"] = new()
        {
            [LangEn] = "Search room, mod or player",
            [LangEs] = "Buscar sala, mod o jugador",
        },
        // Shown INSTEAD of the list when a search matches nothing. Without it an empty
        // panel reads as "there are no rooms" when they are only filtered out.
        ["MpRoomsNoMatches"] = new()
        {
            [LangEn] = "No rooms match your search.",
            [LangEs] = "Ninguna sala coincide con tu búsqueda.",
        },
        ["MpRadminLaunchFailed"] = new()
        {
            [LangEn] = "Could not launch Radmin VPN.",
            [LangEs] = "No se pudo iniciar Radmin VPN.",
        },

        // Honest wording: we can verify the Radmin service is running
        // with a 26.x.x.x identity, but Radmin's per-network membership
        // lives inside its process and is not visible to the OS. So
        // the launcher confirms "Radmin is on" and asks the user to
        // verify the specific network in Radmin's own window — with
        // the network name spelled out + a copy button + numbered
        // steps so the manual flow is as low-friction as possible.
        // {0} = own IP
        // Compact one-line variant used by MultiplayerTab once Radmin is
        // running: the network-name copier and numbered steps are hidden
        // (the RadminAssistantWindow already covers that flow), so the
        // banner shrinks to a single status line. {0} = own IP.

        // Button next to the network-name TextBox. Briefly flashes to
        // "Copied!" after the click so the user sees the action worked.
        ["MpRadminCopiedToast"] = new()
        {
            [LangEn] = "✓ Copied!",
            [LangEs] = "✓ ¡Copiado!",
        },

        // Numbered steps shown under the network-name copier when in
        // the Connected state. Kept short — the user reads them while
        // alt-tabbing to Radmin, not as a tutorial.
        ["MpSignInTitle"] = new()
        {
            [LangEn] = "Sign in to play online",
            [LangEs] = "Inicia sesión para jugar online",
        },
        ["MpSignInBody"] = new()
        {
            [LangEn] = "Multiplayer uses Discord to sign you in — no new account needed. Your username and avatar are read; nothing else is requested.",
            [LangEs] = "El multijugador usa Discord para iniciar sesión: no necesitas crear una cuenta nueva. Solo se leen tu usuario y avatar; nada más.",
        },
        ["MpSignInButton"] = new() { [LangEn] = "Sign in with Discord", [LangEs] = "Iniciar sesión con Discord" },
        ["MpSignOutButton"] = new() { [LangEn] = "Sign out", [LangEs] = "Cerrar sesión" },
        ["MpSignInDialogTitle"] = new() { [LangEn] = "Discord sign-in", [LangEs] = "Inicio de sesión Discord" },
        ["MpSignInStep1"] = new()
        {
            [LangEn] = "Sign in with Discord: open this link in your browser (or copy it) and click Authorize.",
            [LangEs] = "Inicia sesión con Discord: abre este enlace en tu navegador (o cópialo) y haz clic en Autorizar.",
        },
        ["MpSignInStep2"] = new()
        {
            // Only shown for legacy GitHub-style flows where the user has to
            // type a code into the browser. Discord skips this step entirely;
            // the dialog hides this text when the server returns an empty
            // user_code.
            [LangEn] = "Type or paste this code into the browser, then approve:",
            [LangEs] = "Escribe o pega este código en el navegador y aprueba:",
        },
        ["MpSignInWaiting"] = new()
        {
            // Two plain sentences rather than one with an ellipsis in the middle: it has a
            // whole row to itself now instead of being squeezed against Cancel, and
            // "continues automatically" was vaguer than what actually happens, which is
            // that the window closes.
            [LangEn] = "Waiting for you to authorize in the browser. This window closes "
                     + "itself when you are done.",
            [LangEs] = "Esperando tu autorizaci\u00f3n en el navegador. Esta ventana se cierra "
                     + "sola al terminar.",
        },
        ["MpSignInOpenBrowser"] = new() { [LangEn] = "Open browser", [LangEs] = "Abrir navegador" },
        ["MpSignInCopy"] = new() { [LangEn] = "Copy code", [LangEs] = "Copiar código" },
        ["MpSignInCopyLink"] = new() { [LangEn] = "Copy link", [LangEs] = "Copiar enlace" },
        ["MpSignInCopyLinkDone"] = new() { [LangEn] = "Copied ✓", [LangEs] = "¡Copiado! ✓" },
        // Says what to CHECK, not merely what to feel. "A browser you trust" asks the
        // player for a judgement they have no way to make; "the link starts with
        // discord.com" is a thing they can look at - which is also the reason the link
        // above this stopped being ellipsised.
        ["MpSignInBrowserHint"] = new()
        {
            [LangEn] = "Check the link starts with discord.com and that it opens in a "
                     + "browser you recognize. If not, copy it and paste it into Chrome, "
                     + "Edge or Firefox.",
            [LangEs] = "Comprueba que el enlace empieza por discord.com y que se abre en un "
                     + "navegador que reconoces. Si no, c\u00f3pialo y p\u00e9galo en Chrome, Edge "
                     + "o Firefox.",
        },
        // The card's own caption, so the URL is labelled rather than floating loose.
        ["MpSignInUriLabel"] = new()
        {
            [LangEn] = "AUTHORIZATION LINK",
            [LangEs] = "ENLACE DE AUTORIZACI\u00d3N",
        },
        ["MpSignInCancel"] = new() { [LangEn] = "Cancel", [LangEs] = "Cancelar" },
        ["MpSignInPrivacyPrefix"] = new()
        {
            [LangEn] = "By signing in you agree to our ",
            [LangEs] = "Al iniciar sesión aceptas nuestra ",
        },
        ["MpSignInPrivacyLink"] = new()
        {
            [LangEn] = "privacy policy",
            [LangEs] = "política de privacidad",
        },

        // -------------------------------------------------------------
        // Radmin assistant overlay (always-on-top guided checklist).
        // Lives in RadminAssistantWindow.xaml. All copy is honest about
        // what the launcher CAN observe (process running, 26.x IP up,
        // future: seed-peer ping answer) and what the user has to
        // verify themselves in Radmin (network membership until the
        // ping ships). Never tells the user we did something we
        // didn't — that's how confidence in the assistant survives.
        // -------------------------------------------------------------
        ["RadAsstWindowTitle"] = new()
        {
            [LangEn] = "Radmin Assistant",
            [LangEs] = "Asistente Radmin",
        },
        ["RadAsstHeaderTitle"] = new()
        {
            [LangEn] = "Connect to the AoE3 network",
            [LangEs] = "Conéctate a la red AoE3",
        },
        ["RadAsstHeaderSubtitle"] = new()
        {
            [LangEn] = "We'll walk you through it. Most steps auto-advance.",
            [LangEs] = "Te guiamos. La mayoría de los pasos avanzan solos.",
        },
        // Step 1 — open Radmin
        ["RadAsstStep1Title"] = new()
        {
            [LangEn] = "Open Radmin VPN",
            [LangEs] = "Abrir Radmin VPN",
        },
        ["RadAsstStep1BodyNotInstalled"] = new()
        {
            [LangEn] = "Radmin VPN isn't installed. Download it from Famatech to continue.",
            [LangEs] = "Radmin VPN no está instalado. Descárgalo de Famatech para continuar.",
        },
        ["RadAsstStep1BodyDone"] = new()
        {
            [LangEn] = "Opened for you. If the window didn't come to front, click reopen.",
            [LangEs] = "Lo abrimos por ti. Si la ventana no se ve, haz clic en reabrir.",
        },
        ["RadAsstStep1BtnInstall"] = new()
        {
            [LangEn] = "Download Radmin VPN",
            [LangEs] = "Descargar Radmin VPN",
        },
        ["RadAsstStep1BtnReopen"] = new()
        {
            [LangEn] = "Open Radmin",
            [LangEs] = "Abrir Radmin",
        },
        // Step 2 — sign in
        ["RadAsstStep2Title"] = new()
        {
            [LangEn] = "Sign in to Radmin",
            [LangEs] = "Inicia sesión en Radmin",
        },
        ["RadAsstStep2BodyWaiting"] = new()
        {
            [LangEn] = "Create a free Radmin account if you don't have one — we're waiting for your 26.x.x.x address to appear…",
            [LangEs] = "Crea una cuenta gratis de Radmin si no tienes — esperando tu dirección 26.x.x.x…",
        },
        ["RadAsstStep2BodyDone"] = new()
        {
            // {0} = the user's 26.x.x.x address.
            [LangEn] = "Signed in. Your Radmin IP: {0}",
            [LangEs] = "Sesión iniciada. Tu IP de Radmin: {0}",
        },
        // Step 3 — paste network name + Join
        ["RadAsstStep3Title"] = new()
        {
            [LangEn] = "Join the network",
            [LangEs] = "Únete a la red",
        },
        ["RadAsstStep3BodyPending"] = new()
        {
            [LangEn] = "First sign in above. Then we'll prepare the network name for you.",
            [LangEs] = "Primero inicia sesión arriba. Luego preparamos el nombre de la red.",
        },
        ["RadAsstStep3BodyActive"] = new()
        {
            [LangEn] = "In Radmin: Join network → Gaming tab → paste (Ctrl+V) → Join.",
            [LangEs] = "En Radmin: Unirse a la red → pestaña Gaming → pega (Ctrl+V) → Unirse.",
        },
        ["RadAsstStep3BodyDone"] = new()
        {
            [LangEn] = "Joined!",
            [LangEs] = "¡Unido!",
        },
        ["RadAsstStep3Hint"] = new()
        {
            [LangEn] = "The network name is already in your clipboard.",
            [LangEs] = "El nombre de la red ya está en tu portapapeles.",
        },
        ["RadAsstCopyNetwork"] = new()
        {
            [LangEn] = "Copy network name",
            [LangEs] = "Copiar nombre de la red",
        },
        // Step 4 — confirmation (until seed-peer ping ships, this stays
        // as "verify manually" — overpromising kills the assistant's
        // credibility on first failure).
        ["RadAsstStep4Title"] = new()
        {
            [LangEn] = "Connection confirmed",
            [LangEs] = "Conexión confirmada",
        },
        ["RadAsstStep4BodyPending"] = new()
        {
            [LangEn] = "Will mark itself once you finish the steps above.",
            [LangEs] = "Se marcará solo cuando termines los pasos de arriba.",
        },
        ["RadAsstStep4BodyManual"] = new()
        {
            [LangEn] = "Verify in Radmin that you appear in the 'Age of Empires III…' network. (Auto-detection is on its way.)",
            [LangEs] = "Verifica en Radmin que apareces unido a la red 'Age of Empires III…'. (La detección automática está por llegar.)",
        },
        ["RadAsstStep4BodyDone"] = new()
        {
            [LangEn] = "You're in the AoE3 network. You can close this assistant.",
            [LangEs] = "Estás en la red AoE3. Puedes cerrar este asistente.",
        },
        // Footer
        // The folded rows. A finished step is a RESULT, not an instruction, so these are
        // shorter than the bodies they replace: "Radmin abierto", not "Lo abrimos por ti.
        // Si la ventana no se ve, haz clic en reabrir." The long form comes back the moment
        // the step opens again.
        // The title bar's only line, since the subtitle went. Deliberately the PRODUCT
        // rather than an instruction: "Conectate a la red AoE3" was a heading over a
        // checklist, and it kept saying it after you were connected.
        ["RadAsstTitleBar"] = new()
        {
            [LangEn] = "Radmin VPN",
            [LangEs] = "Radmin VPN",
        },
        ["RadAsstStep1Done"] = new()
        {
            [LangEn] = "Radmin open",
            [LangEs] = "Radmin abierto",
        },
        ["RadAsstStep2Done"] = new()
        {
            [LangEn] = "Signed in \u00b7 IP {0}",
            [LangEs] = "Sesi\u00f3n iniciada \u00b7 IP {0}",
        },
        ["RadAsstStep3Done"] = new()
        {
            [LangEn] = "Joined the network",
            [LangEs] = "Unido a la red",
        },
        ["RadAsstStep4Done"] = new()
        {
            [LangEn] = "Connection confirmed",
            [LangEs] = "Conexi\u00f3n confirmada",
        },
        ["RadAsstProgress"] = new()
        {
            [LangEn] = "Step {0} of {1}",
            [LangEs] = "Paso {0} de {1}",
        },
        ["RadAsstAllDone"] = new()
        {
            [LangEn] = "All four steps are done.",
            [LangEs] = "Los cuatro pasos est\u00e1n completos.",
        },
        ["RadAsstHideSteps"] = new()
        {
            [LangEn] = "Hide steps",
            [LangEs] = "Ocultar pasos",
        },
        // The connected state. Says what IS, not what to do - by this point there is
        // nothing left to do, and the window's only remaining job is handing over the
        // network name.
        ["RadAsstConnectedTitle"] = new()
        {
            [LangEn] = "You\u2019re in the AoE3 network",
            [LangEs] = "Est\u00e1s en la red AoE3",
        },
        ["RadAsstConnectedIp"] = new()
        {
            [LangEn] = "your Radmin IP {0}",
            [LangEs] = "tu IP de Radmin {0}",
        },
        ["RadAsstNetworkLabel"] = new()
        {
            [LangEn] = "NETWORK NAME",
            [LangEs] = "NOMBRE DE LA RED",
        },
        ["RadAsstNetworkLabelJoined"] = new()
        {
            [LangEn] = "NETWORK YOU\u2019RE JOINED TO",
            [LangEs] = "RED A LA QUE EST\u00c1S UNIDO",
        },
        ["RadAsstCopy"] = new()
        {
            [LangEn] = "Copy",
            [LangEs] = "Copiar",
        },
        ["RadAsstCopyDone"] = new()
        {
            [LangEn] = "It\u2019s already in your clipboard.",
            [LangEs] = "Ya est\u00e1 en tu portapapeles.",
        },
        // What the launcher is watching for while the user is in Radmin's window. Honest
        // about who does what: they join, we notice.
        ["RadAsstWaitingJoin"] = new()
        {
            [LangEn] = "Waiting for you to join. This ticks itself.",
            [LangEs] = "Esperando a que te unas. Esto se marca solo.",
        },
        ["RadAsstDontShowAgain"] = new()
        {
            [LangEn] = "Don't show this again",
            [LangEs] = "No mostrar de nuevo",
        },
        ["RadAsstClose"] = new()
        {
            [LangEn] = "Close",
            [LangEs] = "Cerrar",
        },
        // Compact banner — single-line replacement in MultiplayerTab.
        // Mirrors the assistant's stage labels so the user sees the
        // same vocabulary in the small banner and the full overlay.
        ["RadAsstBannerShowSteps"] = new()
        {
            [LangEn] = "Show steps",
            [LangEs] = "Ver pasos",
        },
        // Settings dialog combo for the assistant mode.
        ["SettingsRadAsstLabel"] = new()
        {
            [LangEn] = "Radmin VPN assistant",
            [LangEs] = "Asistente de Radmin VPN",
        },
        ["SettingsRadAsstHint"] = new()
        {
            [LangEn] = "When to show the connection guide in Multiplayer.",
            [LangEs] = "Cuándo mostrar la guía de conexión en Multijugador.",
        },
        ["SettingsRadAsstAuto"] = new()
        {
            [LangEn] = "Automatic (recommended)",
            [LangEs] = "Automático (recomendado)",
        },
        ["SettingsRadAsstOnRequest"] = new()
        {
            [LangEn] = "Only when I ask",
            [LangEs] = "Solo cuando lo pida",
        },
        ["SettingsRadAsstNever"] = new()
        {
            [LangEn] = "Never",
            [LangEs] = "Nunca",
        },

        ["DlgClose"] = new() { [LangEn] = "Close", [LangEs] = "Cerrar" },
        ["MpRoomsCreate"] = new() { [LangEn] = "Create room", [LangEs] = "Crear sala" },
        ["MpRoomsRefresh"] = new() { [LangEn] = "Refresh", [LangEs] = "Actualizar" },
        // Kept for the skeleton rows' accessibility text; the visible loading state is
        // three placeholder rows, not a line of prose.
        ["MpRoomsLoading"] = new() { [LangEn] = "Loading rooms…", [LangEs] = "Cargando salas…" },
        ["MpRoomsErrorRetry"] = new() { [LangEn] = "Retry", [LangEs] = "Reintentar" },
        ["MpRoomsSectionTitle"] = new() { [LangEn] = "Active rooms", [LangEs] = "Salas activas" },
        ["MpGlobalChatTitle"] = new() { [LangEn] = "Global chat", [LangEs] = "Chat global" },
        ["MpGlobalChatPresence"] = new() { [LangEn] = "{0} connected", [LangEs] = "{0} conectados" },
        ["MpGlobalChatPlaceholder"] = new() { [LangEn] = "Write a message…", [LangEs] = "Escribe un mensaje…" },
        ["MpGlobalChatSend"] = new() { [LangEn] = "Send", [LangEs] = "Enviar" },
        ["MpGlobalChatEmpty"] = new() { [LangEn] = "No messages yet. Say hi! 👋", [LangEs] = "Todavía no hay mensajes. ¡Saluda! 👋" },
        ["MpGlobalChatConnecting"] = new() { [LangEn] = "Connecting…", [LangEs] = "Conectando…" },
        ["MpGlobalChatSlowMode"] = new() { [LangEn] = "You're sending too fast — wait a moment.", [LangEs] = "Estás enviando muy rápido, espera un momento." },
        ["MpGlobalChatRateLimited"] = new() { [LangEn] = "Too many messages — slow down.", [LangEs] = "Demasiados mensajes, espera un momento." },
        ["MpGlobalChatMuted"] = new() { [LangEn] = "You're muted for a moment.", [LangEs] = "Estás silenciado por un momento." },
        ["MpGlobalChatTimedOut"] = new() { [LangEn] = "Muted for spamming — try again shortly.", [LangEs] = "Silenciado por spam. Prueba de nuevo en un rato." },
        ["MpGlobalChatTooLong"] = new() { [LangEn] = "Message too long (max 500).", [LangEs] = "Mensaje demasiado largo (máx 500)." },
        ["MpRoomJoin"] = new() { [LangEn] = "Join", [LangEs] = "Unirse" },
        // A private room's action says so, because the click opens a password prompt.
        ["MpRoomJoinPrivate"] = new()
        {
            [LangEn] = "Join with password",
            [LangEs] = "Unirse con contraseña",
        },
        // Room sub-line context, the middle segment of "{mod} · {context} · {age}".
        ["MpRoomCtxYouHost"] = new() { [LangEn] = "you're the host", [LangEs] = "tú eres el anfitrión" },
        ["MpRoomCtxNeedsPassword"] = new() { [LangEn] = "asks for a password", [LangEs] = "pide contraseña" },
        ["MpRoomReenter"] = new() { [LangEn] = "Re-enter", [LangEs] = "Reingresar" },
        ["MpRoomYours"] = new() { [LangEn] = "Your room", [LangEs] = "Tu sala" },
        ["MpRoomFull"] = new() { [LangEn] = "Full", [LangEs] = "Llena" },
        // The room is full of PLAYERS but still has a seat beside the game. It replaces
        // "Unirse" rather than joining it, because there is only one seat left to take
        // and offering both would let somebody ask for the one that is gone.
        ["MpRoomWatch"] = new() { [LangEn] = "Watch", [LangEs] = "Ver" },
        ["MpRoomStatusWaiting"] = new() { [LangEn] = "Waiting", [LangEs] = "Esperando" },
        ["MpRoomStatusLocked"] = new() { [LangEn] = "Private", [LangEs] = "Privada" },
        // How long a room has been open — live "count-up" in the rooms list
        // sub-line and the lobby header meta. {0} = a compact duration ("5 min",
        // "1 h 20 min"), formatted by RoomAgeFormat.Compact.
        ["MpRoomOpenedAgo"] = new() { [LangEn] = "open for {0}", [LangEs] = "abierta hace {0}" },
        // Room cards (BuildRoomCard) + empty state + "last updated" header.
        ["MpRoomModNotInstalled"] = new() { [LangEn] = "Mod not installed", [LangEs] = "Mod no instalado" },
        ["MpRoomsEmptyTitle"] = new() { [LangEn] = "No rooms available right now", [LangEs] = "No hay salas disponibles ahora" },
        ["MpRoomsEmptyBody"] = new() { [LangEn] = "Be the first to create one and start a game!", [LangEs] = "¡Sé el primero en crear una y empezar a jugar!" },
        // Appended to the "updated N ago" line when a column sort is active. {0} is the
        // lowercased column name.
        ["MpRoomsSortedBy"] = new() { [LangEn] = "sorted by {0}", [LangEs] = "orden por {0}" },
        ["MpRoomsUpdatedNow"] = new() { [LangEn] = "Updated just now", [LangEs] = "Actualizado ahora" },
        ["MpRoomsUpdatedSecs"] = new() { [LangEn] = "Updated {0}s ago", [LangEs] = "Actualizado hace {0} s" },
        ["MpRoomsUpdatedMins"] = new() { [LangEn] = "Updated {0}m ago", [LangEs] = "Actualizado hace {0} min" },
        ["MpRoomPrivate"] = new() { [LangEn] = "Private", [LangEs] = "Privado" },
        // Room-list column headers (the card list mimics a table now).
        ["MpColRoom"] = new() { [LangEn] = "ROOM", [LangEs] = "SALA" },
        ["MpColMod"] = new() { [LangEn] = "MOD", [LangEs] = "MOD" },
        ["MpColHost"] = new() { [LangEn] = "HOST", [LangEs] = "ANFITRIÓN" },
        ["MpColPlayers"] = new() { [LangEn] = "PLAYERS", [LangEs] = "JUGADORES" },
        ["MpColPing"] = new() { [LangEn] = "PING", [LangEs] = "PING" },
        ["MpColStatus"] = new() { [LangEn] = "STATUS", [LangEs] = "ESTADO" },
        ["MpColAction"] = new() { [LangEn] = "ACTION", [LangEs] = "ACCIÓN" },
        ["MpRoomsShowingCount"] = new() { [LangEn] = "Showing {0} rooms", [LangEs] = "Mostrando {0} salas" },
        ["MpRoomLeave"] = new() { [LangEn] = "Leave room", [LangEs] = "Salir de la sala" },
        ["MpRoomReady"] = new() { [LangEn] = "Ready", [LangEs] = "Listo" },
        ["MpRoomPeekTooltip"] = new() { [LangEn] = "See who's in this room", [LangEs] = "Ver quién está en esta sala" },
        ["MpRoomPeekTitle"] = new() { [LangEn] = "Players in room", [LangEs] = "Jugadores en la sala" },
        ["MpRoomPeekLoading"] = new() { [LangEn] = "Loading players…", [LangEs] = "Cargando jugadores…" },
        ["MpRoomPeekEmpty"] = new() { [LangEn] = "No players in this room.", [LangEs] = "No hay jugadores en esta sala." },
        ["MpRoomPeekError"] = new() { [LangEn] = "Couldn't load the players. Try again.", [LangEs] = "No se pudieron cargar los jugadores. Prueba de nuevo." },
        ["MpOnlinePlayersTitle"] = new() { [LangEn] = "Online players · {0}", [LangEs] = "Jugadores conectados · {0}" },
        ["MpOnlinePlayersEmpty"] = new() { [LangEn] = "No one online.", [LangEs] = "Nadie conectado." },
        ["MpOnlinePlayersTooltip"] = new() { [LangEn] = "See who's online", [LangEs] = "Ver quién está conectado" },
        ["MpOnlinePlayersYou"] = new() { [LangEn] = "you", [LangEs] = "tú" },
        ["MpPlayersPanelTitle"] = new() { [LangEn] = "Players · {0}", [LangEs] = "Jugadores · {0}" },
        ["MpPlayersInGame"] = new() { [LangEn] = "In game · {0}", [LangEs] = "En partida · {0}" },
        ["MpPlayersInRoom"] = new() { [LangEn] = "In a room · {0}", [LangEs] = "En una sala · {0}" },
        ["MpPlayersInLauncher"] = new() { [LangEn] = "In launcher · {0}", [LangEs] = "En el launcher · {0}" },
        ["MpRoomStart"] = new() { [LangEn] = "Start game", [LangEs] = "Empezar partida" },
        ["MpRoomChatPlaceholder"] = new()
        {
            [LangEn] = "Type a message…",
            [LangEs] = "Escribe un mensaje…",
        },
        ["MpRoomChatHeader"] = new() { [LangEn] = "CHAT & ACTIVITY", [LangEs] = "CHAT Y ACTIVIDAD" },
        ["MpRoomChatClear"] = new() { [LangEn] = "Clear chat", [LangEs] = "Limpiar chat" },
        ["MpRoomChatSend"] = new() { [LangEn] = "Send", [LangEs] = "Enviar" },
        ["MpRoomChatEmpty"] = new()
        {
            [LangEn] = "No messages yet — say hi!",
            [LangEs] = "Aún no hay mensajes — ¡saluda!",
        },
        ["MpRoomPlayersHeader"] = new() { [LangEn] = "PLAYERS", [LangEs] = "JUGADORES" },
        ["MpRoomIdHeader"] = new() { [LangEn] = "ROOM ID", [LangEs] = "ID DE SALA" },
        ["MpRoomCopyCode"] = new() { [LangEn] = "Copy code", [LangEs] = "Copiar código" },
        // A WORD, not just the pencil. The glyph already had a tooltip and the button still
        // was not understood — which is the point: nobody hovers a thing they do not know
        // exists. Same lesson as the "?" on the connection-help button.
        // The private-room password prompt. It was hardcoded English at the call site and the
        // dialog localised nothing at all — an English window in the middle of a Spanish
        // multiplayer surface, on the path everyone joining a private room takes.
        ["MpJoinPasswordTitle"] = new()
        {
            [LangEn] = "Private room",
            [LangEs] = "Sala privada",
        },
        ["MpJoinPasswordPrompt"] = new()
        {
            [LangEn] = "This room is password-protected. Enter the password to join.",
            [LangEs] = "Esta sala tiene contraseña. Escríbela para entrar.",
        },
        ["MpJoinPasswordEnter"] = new()
        {
            [LangEn] = "Enter",
            [LangEs] = "Entrar",
        },
        ["MpRoomRenameButton"] = new()
        {
            [LangEn] = "✎ Rename",
            [LangEs] = "✎ Cambiar nombre",
        },
        ["MpRoomRenameTooltip"] = new()
        {
            [LangEn] = "Change the room name",
            [LangEs] = "Cambiar el nombre de la sala",
        },
        ["MpRenameDialogTitle"] = new()
        {
            [LangEn] = "Change the name",
            [LangEs] = "Cambiar el nombre",
        },
        ["MpRenameDialogPrompt"] = new()
        {
            [LangEn] = "Type the new room name (3 to 80 characters). Everyone in the room — and the Discord announcement — will see it.",
            [LangEs] = "Escribe el nuevo nombre de la sala (de 3 a 80 caracteres). Lo van a ver todos los que estén en la sala y también el anuncio de Discord.",
        },
        ["MpChatRoomRenamed"] = new()
        {
            [LangEn] = "The host changed the room name to “{0}”.",
            [LangEs] = "El anfitrión cambió el nombre de la sala a “{0}”.",
        },
        ["MpRenameFailed"] = new()
        {
            [LangEn] = "The room name couldn't be changed. Try again in a moment.",
            [LangEs] = "No se pudo cambiar el nombre de la sala. Vuelve a intentarlo en un momento.",
        },
        ["MpRoomInfoHeader"] = new() { [LangEn] = "ROOM INFO", [LangEs] = "INFO DE LA SALA" },
        ["MpRoomFieldMod"] = new() { [LangEn] = "Mod", [LangEs] = "Mod" },
        ["MpRoomFieldPassword"] = new() { [LangEn] = "Password", [LangEs] = "Contraseña" },
        ["MpRoomFieldCopy"] = new() { [LangEn] = "Copy", [LangEs] = "Copia" },
        ["MpRoomPasswordYes"] = new() { [LangEn] = "Required", [LangEs] = "Requerida" },
        ["MpRoomPasswordNo"] = new() { [LangEn] = "None", [LangEs] = "Ninguna" },
        ["MpRoomReadyMark"] = new() { [LangEn] = "Mark as ready", [LangEs] = "Marcar como listo" },
        ["MpRoomStatusInLobby"] = new() { [LangEn] = "In lobby", [LangEs] = "En la sala" },
        ["MpRoomStatusJoining"] = new() { [LangEn] = "Joining…", [LangEs] = "Entrando…" },
        ["MpRoomStatusLeaving"] = new() { [LangEn] = "Leaving…", [LangEs] = "Saliendo…" },
        ["MpRoomStatusInGame"] = new() { [LangEn] = "In game", [LangEs] = "En partida" },
        ["MpRoomP2pReady"] = new() { [LangEn] = "P2P LAN ready", [LangEs] = "P2P LAN listo" },
        ["MpRoomP2pStarting"] = new() { [LangEn] = "P2P starting…", [LangEs] = "Iniciando P2P…" },
        ["MpRoomTitleFallback"] = new() { [LangEn] = "{0}'s room", [LangEs] = "Sala de {0}" },
        ["MpRoomTitleGeneric"] = new() { [LangEn] = "Multiplayer room", [LangEs] = "Sala multijugador" },
        ["MpRoomBadgeHost"] = new() { [LangEn] = "Host", [LangEs] = "Anfitrión" },
        ["MpRoomSlotOpen"] = new()
        {
            [LangEn] = "Waiting for player…",
            [LangEs] = "Esperando jugador…",
        },
        ["MpRoomPingTooltip"] = new()
        {
            [LangEn] = "Your internet latency — the same for every room. A per-host ping isn't available.",
            [LangEs] = "Tu latencia de internet — igual para todas las salas. El ping por host no está disponible.",
        },

        // -------- Lobby match-phase overlays (LobbyWindow.xaml) --------
        // CountdownOverlay + InGameOverlay covers shown during the
        // Starting / InGame phases. Static labels are pushed by
        // ApplyLobbyStaticLabels(); the state-driven ones (countdown
        // "Go", in-game mode badge, cancel/leave button) are set from
        // their own code-behind paths and re-applied on language switch.
        ["MpCountdownLabel"] = new() { [LangEn] = "Starting in", [LangEs] = "Comienza en" },
        ["MpCountdownGo"] = new() { [LangEn] = "Go", [LangEs] = "¡Ya!" },
        ["MpCountdownCancel"] = new() { [LangEn] = "Cancel", [LangEs] = "Cancelar" },
        ["MpInGameTitle"] = new() { [LangEn] = "GAME IN PROGRESS", [LangEs] = "PARTIDA EN CURSO" },
        ["MpInGameMatchTimeHeader"] = new() { [LangEn] = "MATCH TIME", [LangEs] = "TIEMPO DE PARTIDA" },
        ["MpInGameTrafficHeader"] = new() { [LangEn] = "TRAFFIC", [LangEs] = "TRÁFICO" },
        ["MpInGameRoomHeader"] = new() { [LangEn] = "ROOM", [LangEs] = "SALA" },
        ["MpInGameConnectionHeader"] = new() { [LangEn] = "CONNECTION", [LangEs] = "CONEXIÓN" },
        // InGameModeText — the leading " — " separator is kept inside the
        // value so the badge reads "GAME IN PROGRESS — <mode>" without
        // any code-side concatenation. Connected is the XAML/static
        // default; the other two are set live by RefreshInGamePanel.
        ["MpInGameModeConnected"] = new()
        {
            [LangEn] = " — Connected via P2P LAN",
            [LangEs] = " — Conectado vía LAN P2P",
        },
        ["MpInGameModeInLobby"] = new()
        {
            [LangEn] = " — In lobby (Radmin VPN expected)",
            [LangEs] = " — En la sala (se espera Radmin VPN)",
        },
        ["MpInGameModeWaitingLobby"] = new()
        {
            [LangEn] = " — Waiting for lobby…",
            [LangEs] = " — Esperando la sala…",
        },
        ["MpInGameWaitingPeers"] = new()
        {
            [LangEn] = "Waiting for peers — you're the only player in the room right now.\n" +
                       "P2P stack ready; another launcher needs to Join this room for game traffic to flow.",
            [LangEs] = "Esperando jugadores — por ahora eres el único en la sala.\n" +
                       "La pila P2P está lista; otro launcher tiene que unirse a esta sala para que fluya el tráfico de la partida.",
        },
        // Cancel / Leave button in the in-game overlay — caption differs
        // for host vs joiner (set in ApplyMatchPhaseUi).
        ["MpInGameLeave"] = new()
        {
            [LangEn] = "↩  Leave game",
            [LangEs] = "↩  Salir de la partida",
        },
        // Abort-for-everyone button (shown instead of Leave during the grace window).
        ["MpInGameAbort"] = new()
        {
            [LangEn] = "✕  Abort match",
            [LangEs] = "✕  Abortar partida",
        },
        // Kick a player from the room (host-only).
        ["MpConfirmKickTitle"] = new()
        {
            [LangEn] = "Kick player?",
            [LangEs] = "¿Expulsar jugador?",
        },
        ["MpConfirmKickBody"] = new()
        {
            [LangEn] = "Kick {0} from the room?",
            [LangEs] = "¿Expulsar a {0} de la sala?",
        },
        ["MpConfirmKickYes"] = new()
        {
            [LangEn] = "Kick",
            [LangEs] = "Expulsar",
        },
        // Shown to the player who was kicked.
        ["MpKickedTitle"] = new()
        {
            [LangEn] = "You were kicked",
            [LangEs] = "Te expulsaron",
        },
        ["MpKickedBody"] = new()
        {
            [LangEn] = "The host kicked you from the room.",
            [LangEs] = "El anfitrión te expulsó de la sala.",
        },
        // Host migration (GameRanger-style): the host left and the lobby passed on.
        ["MpChatHostChanged"] = new()
        {
            [LangEn] = "{0} is now the host.",
            [LangEs] = "Ahora {0} es el anfitrión.",
        },
        // Global-chat timestamp: the "yesterday" prefix shown on a message
        // written the day before (older messages show the date instead).
        ["MpChatYesterday"] = new()
        {
            [LangEn] = "Yesterday",
            [LangEs] = "Ayer",
        },
        // Shown when a member tries to abort after the grace window has passed.
        ["MpChatAbortWindowClosed"] = new()
        {
            [LangEn] = "The abort window has passed — leaving only removes you, the others keep playing.",
            [LangEs] = "Ya pasó la ventana para abortar; al salir solo te retiras tú y los demás siguen jugando.",
        },
        // Match aborted by some member within the grace window (was host-only before).
        ["MpChatGameAborted"] = new()
        {
            [LangEn] = "The match was aborted. Returning to lobby.",
            [LangEs] = "La partida fue abortada. Volviendo a la sala.",
        },
        ["MpChatHostEndedMatch"] = new()
        {
            [LangEn] = "The host ended the match. Back to the lobby.",
            [LangEs] = "El anfitrión terminó la partida. De vuelta a la sala.",
        },
        // Abort confirmation card (within the grace window).
        ["MpConfirmAbortTitle"] = new()
        {
            [LangEn] = "Abort the match?",
            [LangEs] = "¿Abortar la partida?",
        },
        ["MpConfirmAbortBody"] = new()
        {
            [LangEn] = "This ends the match for EVERYONE — only possible in the first moments after launch (e.g. a bad/desynced start).",
            [LangEs] = "Esto termina la partida para TODOS — solo es posible en los primeros instantes tras lanzar (p. ej. un arranque mal/desincronizado).",
        },
        ["MpConfirmAbortYes"] = new()
        {
            [LangEn] = "Abort for everyone",
            [LangEs] = "Abortar para todos",
        },

        // ---- Themed in-lobby alert cards (MpAlertOverlay) — replace the
        //      old OS MessageBox prompts on the multiplayer surfaces. ----
        ["MpAlertOk"] = new() { [LangEn] = "OK", [LangEs] = "Entendido" },
        ["MpAlertCancel"] = new() { [LangEn] = "No", [LangEs] = "No" },
        // Cancel-the-game confirm (host) — the one from the screenshot.
        // Leave-the-game confirm (joiner) — only this player drops out.
        ["MpConfirmLeaveTitle"] = new()
        {
            [LangEn] = "Leave the game?",
            [LangEs] = "¿Salir de la partida?",
        },
        ["MpConfirmLeaveBody"] = new()
        {
            [LangEn] = "AoE3 will close. The room keeps playing for the other players.",
            [LangEs] = "AoE3 se cerrará. La sala sigue jugando para los demás jugadores.",
        },
        ["MpConfirmLeaveYes"] = new()
        {
            [LangEn] = "Yes, leave",
            [LangEs] = "Sí, salir",
        },
        // Join / create / mod error notices (single-button).
        ["MpNoticeModNotInstalledTitle"] = new()
        {
            [LangEn] = "Mod not installed",
            [LangEs] = "Mod no instalado",
        },
        ["MpNoticeModNotInstalledBody"] = new()
        {
            [LangEn] = "You don't have any of the mods you can host installed yet. Install one from the Workshop tab and try again.",
            [LangEs] = "Todavía no tienes instalado ninguno de los mods que puedes hospedar. Instala uno desde la pestaña Workshop e inténtalo de nuevo.",
        },
        ["MpNoticeRoomModMissingTitle"] = new()
        {
            [LangEn] = "Mod not installed",
            [LangEs] = "Mod no instalado",
        },
        // {0} = mod display name.
        ["MpNoticeRoomModMissingBody"] = new()
        {
            [LangEn] = "This room is for {0}, but you don't have that mod installed yet. Install it from the Workshop tab and try again.",
            [LangEs] = "Esta sala es para {0}, pero todavía no tienes ese mod instalado. Instálalo desde la pestaña Workshop e inténtalo de nuevo.",
        },
        ["MpNoticeUnknownModTitle"] = new()
        {
            [LangEn] = "Unknown mod",
            [LangEs] = "Mod desconocido",
        },
        // {0} = mod id.
        ["MpNoticeUnknownModBody"] = new()
        {
            [LangEn] = "This room uses an unknown mod ('{0}'). The launcher can't switch to it.",
            [LangEs] = "Esta sala usa un mod desconocido ('{0}'). El launcher no puede cambiar a él.",
        },
        ["MpNoticeSwitchFailedTitle"] = new()
        {
            [LangEn] = "Mod switch failed",
            [LangEs] = "No se pudo cambiar de mod",
        },
        // {0} = mod display name.
        ["MpNoticeSwitchFailedBody"] = new()
        {
            [LangEn] = "Couldn't switch to {0}. Make sure no install / update is in progress, then try again.",
            [LangEs] = "No se pudo cambiar a {0}. Asegúrate de que no haya una instalación o actualización en curso e inténtalo de nuevo.",
        },
        ["MpNoticeFingerprintTitle"] = new()
        {
            [LangEn] = "Couldn't read mod files",
            [LangEs] = "No se pudieron leer los archivos del mod",
        },
        // The server refused this build. The version comes from the server's own answer — the
        // launcher cannot know what a backend it predates requires.
        ["MpNoticeLauncherTooOldTitle"] = new()
        {
            [LangEn] = "This launcher is out of date",
            [LangEs] = "Este launcher está desactualizado",
        },
        ["MpNoticeLauncherTooOldBodyVersion"] = new()
        {
            [LangEn] = "Multiplayer needs {0} or newer, and this build is older. Everything else "
                     + "keeps working — your mods, single player, and your match history. Update "
                     + "the launcher and you can play again.",
            [LangEs] = "El multijugador necesita la {0} o más nueva, y esta versión es anterior. "
                     + "Todo lo demás sigue funcionando — tus mods, un jugador y tu historial de "
                     + "partidas. Actualiza el launcher y ya puedes volver a jugar.",
        },
        ["MpNoticeLauncherTooOldBody"] = new()
        {
            [LangEn] = "Multiplayer needs a newer launcher than this one. Everything else keeps "
                     + "working — your mods, single player, and your match history. Update the "
                     + "launcher and you can play again.",
            [LangEs] = "El multijugador necesita un launcher más nuevo que este. Todo lo demás "
                     + "sigue funcionando — tus mods, un jugador y tu historial de partidas. "
                     + "Actualiza el launcher y ya puedes volver a jugar.",
        },
        ["MpNoticeMismatchTitle"] = new()
        {
            [LangEn] = "Mod version mismatch",
            [LangEs] = "Versión del mod no coincide",
        },
        ["MpNoticeMismatchBody"] = new()
        {
            [LangEn] = "Your local mod files don't match the host. Verify or update the mod before trying again.",
            [LangEs] = "Tus archivos del mod no coinciden con los del anfitrión. Verifica o actualiza el mod antes de volver a intentarlo.",
        },
        ["MpNoticeJoinFailedTitle"] = new()
        {
            [LangEn] = "Couldn't join the room",
            [LangEs] = "No se pudo unir a la sala",
        },
        // Discord "Join" deep-link (wol-launcher://join/<id>) auto-join notices.
        ["MpDeepLinkSignInTitle"] = new()
        {
            [LangEn] = "Sign in to join",
            [LangEs] = "Inicia sesión para unirte",
        },
        ["MpDeepLinkSignInBody"] = new()
        {
            [LangEn] = "You need to sign in with Discord before joining a room from a link.",
            [LangEs] = "Necesitas iniciar sesión con Discord antes de unirte a una sala desde un enlace.",
        },
        ["MpDeepLinkNotFoundTitle"] = new()
        {
            [LangEn] = "Room not available",
            [LangEs] = "Sala no disponible",
        },
        ["MpDeepLinkNotFoundBody"] = new()
        {
            [LangEn] = "That room is no longer open — it may have closed or already started.",
            [LangEs] = "Esa sala ya no está abierta — puede haberse cerrado o ya empezó.",
        },
        ["MpDeepLinkFailedTitle"] = new()
        {
            [LangEn] = "Couldn't open the room",
            [LangEs] = "No se pudo abrir la sala",
        },
        ["MpNoticeCreateFailedTitle"] = new()
        {
            [LangEn] = "Couldn't enter the lobby",
            [LangEs] = "No se pudo entrar al lobby",
        },
        ["MpNoticeRadminLaunchTitle"] = new()
        {
            [LangEn] = "Radmin VPN",
            [LangEs] = "Radmin VPN",
        },

        // -------- Lobby chat-system lines (AppendChatSystem) --------
        // Status/activity messages injected into the lobby chat log.
        // The {0}/{1} placeholders are filled via Strings.Format.
        ["MpChatGameStartingIn"] = new()
        {
            [LangEn] = "Game starting in {0} seconds…",
            [LangEs] = "La partida empieza en {0} segundos…",
        },
        ["MpChatGameStarted"] = new()
        {
            [LangEn] = "The game has started.",
            [LangEs] = "La partida empezó.",
        },
        ["MpChatGameCancelledReason"] = new()
        {
            [LangEn] = "Game cancelled: {0}.",
            [LangEs] = "Partida cancelada: {0}.",
        },
        ["MpChatMemberJoined"] = new()
        {
            [LangEn] = "{0} joined.",
            [LangEs] = "{0} entró.",
        },
        ["MpChatMemberLeft"] = new()
        {
            [LangEn] = "{0} left.",
            [LangEs] = "{0} salió.",
        },
        ["MpChatPeerLost"] = new()
        {
            [LangEn] = "{0} lost connection (not responding).",
            [LangEs] = "{0} perdió la conexión (no responde).",
        },
        ["MpChatPeerReconnected"] = new()
        {
            [LangEn] = "{0} reconnected.",
            [LangEs] = "{0} reconectó.",
        },
        ["MpPeerYou"] = new()
        {
            [LangEn] = "you",
            [LangEs] = "tú",
        },
        ["MpPeerWaitingVpn"] = new()
        {
            [LangEn] = "Waiting for VPN",
            [LangEs] = "Esperando VPN",
        },
        ["MpPeerLost"] = new()
        {
            [LangEn] = "No connection",
            [LangEs] = "Sin conexión",
        },
        ["MpChatReadySavedLocally"] = new()
        {
            [LangEn] = "Ready saved locally — will sync when the room reconnects.",
            [LangEs] = "Estado «listo» guardado localmente — se sincronizará cuando la sala se reconecte.",
        },
        ["MpChatStartingGame"] = new()
        {
            [LangEn] = "Starting game…",
            [LangEs] = "Iniciando partida…",
        },
        ["MpChatAutoStartAllReady"] = new()
        {
            [LangEn] = "Everyone's ready — starting the game…",
            [LangEs] = "Todos están listos — empezando la partida…",
        },
        ["MpChatCannotLaunchNoProfile"] = new()
        {
            [LangEn] = "Cannot launch — no active mod profile.",
            [LangEs] = "No se puede iniciar — no hay un mod activo.",
        },
        ["MpChatCouldNotSpawn"] = new()
        {
            [LangEn] = "Could not spawn the game process.",
            [LangEs] = "No se pudo iniciar el proceso del juego.",
        },
        ["MpChatGameLaunched"] = new()
        {
            [LangEn] = "Game launched. In AoE3: Multiplayer → LAN.",
            [LangEs] = "Partida lanzada. En AoE3: Multijugador → LAN.",
        },
        ["MpChatRadminNoAdapter"] = new()
        {
            [LangEn] = "ℹ No Radmin VPN adapter detected, so we couldn't set your network IP. Install/enable Radmin VPN to play with others.",
            [LangEs] = "ℹ No se detectó el adaptador de Radmin VPN, así que no pudimos fijar tu IP de red. Instala/activa Radmin VPN para jugar con otros.",
        },
        ["MpChatRadminNotReady"] = new()
        {
            [LangEn] = "ℹ Set your Radmin IP for the game. To play, open Radmin and connect to the AoE3 network.",
            [LangEs] = "ℹ Fijamos tu IP de Radmin para el juego. Para jugar, abre Radmin y conéctate a la red de AoE3.",
        },
        ["MpChatLaunchFailed"] = new()
        {
            [LangEn] = "Launch failed: {0}",
            [LangEs] = "Falló el inicio: {0}",
        },
        ["MpChatGameClosed"] = new()
        {
            [LangEn] = "Game closed.",
            [LangEs] = "La partida se cerró.",
        },
        // No "upload it from History": uploading a replay is not implemented anywhere in the
        // launcher, and telling the player to go and do it sent them looking for a button that
        // has never existed. The format arity is unchanged so the call site stays as it was.
        ["MpChatReplaySaved"] = new()
        {
            [LangEn] = "Replay saved: {0} ({1} KB). AoE3 renames it after your next match — "
                     + "open it from the result card.",
            [LangEs] = "Repetición guardada: {0} ({1} KB). AoE3 la renombra en tu siguiente "
                     + "partida — ábrela desde la tarjeta de resultado.",
        },
        // The same line when the recording gave up its map, which is the ONE thing that tells
        // one of these apart from another: the file name does not, because AoE3 calls them all
        // "Record Game N" and renumbers so the newest is always 1.
        ["MpChatReplaySavedMap"] = new()
        {
            [LangEn] = "Replay saved: {0} · {2} ({1} KB). AoE3 renames it after your next "
                     + "match — open it from the result card.",
            [LangEs] = "Repetición guardada: {0} · {2} ({1} KB). AoE3 la renombra en tu "
                     + "siguiente partida — ábrela desde la tarjeta de resultado.",
        },
        ["MpChatYouCancelled"] = new()
        {
            [LangEn] = "You cancelled the game. Room returned to lobby.",
            [LangEs] = "Cancelaste la partida. La sala volvió a la espera.",
        },
        ["MpChatYouLeftGame"] = new()
        {
            [LangEn] = "You left the game. Other players continue.",
            [LangEs] = "Saliste de la partida. Los demás jugadores siguen.",
        },
        // End-of-match card. The pending line goes up the instant the game closes: the
        // numbers do not exist until the report comes back, and the recording search can
        // take the best part of ten seconds.
        // Lobby window (design handoff 1e).
        ["MpLobbyWindowTitle"] = new() { [LangEn] = "Room \u00B7 {0}", [LangEs] = "Sala \u00B7 {0}" },
        ["MpRoomCodeHeader"] = new() { [LangEn] = "CODE", [LangEs] = "C\u00D3DIGO" },
        ["MpRoomInvite"] = new() { [LangEn] = "Invite", [LangEs] = "Invitar" },
        ["MpRoomInviteTitle"] = new() { [LangEn] = "Invite a player", [LangEs] = "Invitar a un jugador" },
        ["MpRoomInviteBody"] = new()
        {
            [LangEn] = "Share the room code, or right-click a player in the Players panel of the "
                     + "Multiplayer tab and choose \u201CInvite to my room\u201D.",
            [LangEs] = "Comparte el c\u00F3digo de la sala, o haz clic derecho sobre un jugador en el "
                     + "panel Jugadores de la pesta\u00F1a Multijugador y elige \u201CInvitar a mi sala\u201D.",
        },
        // The roster's second line. The ELO segment is omitted entirely when the rating is
        // not known — never a placeholder number.
        ["MpRoomMemberElo"] = new() { [LangEn] = "{0} ELO", [LangEs] = "{0} ELO" },
        ["MpRoomMemberReady"] = new() { [LangEn] = "ready", [LangEs] = "listo" },
        ["MpRoomMemberWaiting"] = new() { [LangEn] = "waiting", [LangEs] = "esperando" },
        ["MpRoomSlotOpenShare"] = new()
        {
            [LangEn] = "Open slot \u00B7 share the code",
            [LangEs] = "Hueco libre \u00B7 comparte el c\u00F3digo",
        },
        // The two-item checklist that replaced the amber reminder band.
        ["MpPreflightHeader"] = new() { [LangEn] = "BEFORE YOU START", [LangEs] = "ANTES DE EMPEZAR" },
        ["MpPreflightModsMatch"] = new()
        {
            [LangEn] = "Identical mods across all {0} players",
            [LangEs] = "Mods id\u00E9nticos en los {0} jugadores",
        },
        ["MpPreflightRecordGame"] = new()
        {
            [LangEn] = "Tick {0} in AoE3 so the match counts towards ELO",
            [LangEs] = "Marcar {0} en AoE3 para que cuente el ELO",
        },
        ["MpPreflightSeeHow"] = new() { [LangEn] = "See how", [LangEs] = "Ver c\u00F3mo" },
        ["MpPreflightHelpTitle"] = new()
        {
            [LangEn] = "Where the Record Game box is",
            [LangEs] = "D\u00F3nde est\u00E1 la casilla Record Game",
        },
        ["MpPreflightHelpBody"] = new()
        {
            [LangEn] = "On the AoE3 multiplayer setup screen, before the match starts, there is a "
                     + "\u201CRecord Game\u201D checkbox. It comes up unticked EVERY match and the "
                     + "launcher cannot tick it for you \u2014 both ways of doing that were tried and "
                     + "neither works. Without a recording the match has no readable winner and counts "
                     + "for nobody.",
            [LangEs] = "En la pantalla de configuraci\u00F3n de la partida multijugador de AoE3, antes de "
                     + "empezar, hay una casilla \u201CRecord Game\u201D. Aparece desmarcada en CADA "
                     + "partida y el launcher no puede marcarla por ti \u2014 se probaron las dos formas "
                     + "de hacerlo y ninguna funciona. Sin grabaci\u00F3n la partida no tiene un ganador "
                     + "legible y no cuenta para nadie.",
        },
        // The third item, competitive rooms only. It is a RULE, not a task, and it exists
        // because the abandonment penalty was written in exactly one place — the
        // create-room dialog, which only the HOST sees. The wording deliberately mirrors
        // MpCreateDialogCompetitiveHint word for word: the guest has to be able to read the
        // same rule the host agreed to, and the two drifting apart is how somebody loses
        // rating to a sentence they were never shown.
        //
        // "Five minutes" is spelled out in both, and the number the SERVER actually uses is
        // COMPETITIVE_ABANDON_SECONDS (300). Change one and change all three.
        ["MpPreflightAbandon"] = new()
        {
            [LangEn] = "Walking out after the first five minutes counts as a loss",
            [LangEs] = "Abandonar después de los primeros cinco minutos cuenta como derrota",
        },
        ["MpRoomStateInLobby"] = new() { [LangEn] = "In the lobby", [LangEs] = "En el lobby" },
        ["MpRoomReadyShort"] = new() { [LangEn] = "Mark me ready", [LangEs] = "Marcarme listo" },
        ["MpRoomLeaveShort"] = new() { [LangEn] = "Leave the room", [LangEs] = "Salir de la sala" },
        // In-match panel (design handoff 1f). The recording cell states what the LAUNCHER
        // asked for; what the GAME will do is decided by a per-match checkbox nothing here
        // can read, so none of these three words claims it is recording.
        ["MpInGameRecordingHeader"] = new() { [LangEn] = "RECORDING", [LangEs] = "GRABACI\u00D3N" },
        ["MpInGameRecordingOn"] = new() { [LangEn] = "requested", [LangEs] = "solicitada" },
        ["MpInGameRecordingOff"] = new() { [LangEn] = "turned off", [LangEs] = "desactivada" },
        ["MpInGameRecordingUnknown"] = new() { [LangEn] = "not checked", [LangEs] = "sin comprobar" },
        ["MpInGameRecordingTooltip"] = new()
        {
            [LangEn] = "This is the launcher's own setting. Whether the match is actually recorded "
                     + "depends on the \u201CRecord Game\u201D box on AoE3's setup screen, which "
                     + "comes up unticked every match and cannot be read from here.",
            [LangEs] = "Es el ajuste del propio launcher. Que la partida se grabe de verdad depende "
                     + "de la casilla \u201CRecord Game\u201D de la pantalla de configuraci\u00F3n de "
                     + "AoE3, que aparece desmarcada en cada partida y no se puede leer desde aqu\u00ED.",
        },
        ["MpInGameSoloTitle"] = new()
        {
            [LangEn] = "You are the only player in the room",
            [LangEs] = "Eres el \u00FAnico jugador en la sala",
        },
        ["MpInGameSoloBody"] = new()
        {
            [LangEn] = "The P2P network is ready, but another launcher has to join this room "
                     + "before any game traffic can flow.",
            [LangEs] = "La red P2P est\u00E1 lista, pero hace falta que otro launcher entre en esta "
                     + "sala para que circule tr\u00E1fico de juego.",
        },
        ["MpInGameSoloCopy"] = new() { [LangEn] = "Copy code", [LangEs] = "Copiar c\u00F3digo" },
        ["MpRoomCopied"] = new() { [LangEn] = "Copied \u2713", [LangEs] = "Copiado \u2713" },
        ["MpInGameSoloAnnounce"] = new() { [LangEn] = "Say it in chat", [LangEs] = "Avisar en el chat" },
        ["MpInGameSoloAnnounced"] = new() { [LangEn] = "Sent \u2713", [LangEs] = "Enviado \u2713" },
        ["MpAnnounceRoomInGlobal"] = new()
        {
            [LangEn] = "Room open in {0} \u2014 code {1}. Looking for someone to play.",
            [LangEs] = "Sala abierta en {0} \u2014 c\u00F3digo {1}. Busco con qui\u00E9n jugar.",
        },
        // End-of-match card (design handoff 1f).
        // The in-game RECORDING cell's fourth state. The label says RECORDING and this
        // is not about recording, but the cell answers "is this match going to count",
        // and an unreadable profile answers it just as loudly as recording being off.
        ["MpInGameRecordingNoProfile"] = new()
        {
            [LangEn] = "\u26A0 profile unreadable",
            [LangEs] = "\u26A0 perfil ilegible",
        },
        ["MpInGameRecordingNoProfileTooltip"] = new()
        {
            [LangEn] = "Your AoE3 profile name could not be read, so this match cannot be "
                     + "matched to its recording and will not count towards anyone's rating. "
                     + "Open AoE3 once and make sure your profile has a name.",
            [LangEs] = "No se pudo leer el nombre de tu perfil de AoE3, as\u00ED que esta partida no "
                     + "se podr\u00E1 asociar con su grabaci\u00F3n y no contar\u00E1 para el ELO de nadie. "
                     + "Abre AoE3 una vez y aseg\u00FArate de que tu perfil tiene nombre.",
        },
        ["MpResultTitle"] = new() { [LangEn] = "Match result", [LangEs] = "Resultado de la partida" },
        ["MpResultWin"] = new() { [LangEn] = "Victory", [LangEs] = "Victoria" },
        ["MpResultLoss"] = new() { [LangEn] = "Defeat", [LangEs] = "Derrota" },
        // NOT "draw": 0.5 is what the backend stores when the outcome could not be read.
        ["MpResultNone"] = new() { [LangEn] = "No result", [LangEs] = "Sin resultado" },
        ["MpResultNoneBody"] = new()
        {
            [LangEn] = "The match was not recorded, so nobody can tell who won \u2014 it counted "
                     + "towards no one's rating. Tick \u201CRecord Game\u201D on the AoE3 setup "
                     + "screen before the next one.",
            [LangEs] = "La partida no se grab\u00F3, as\u00ED que no hay forma de saber qui\u00E9n gan\u00F3 "
                     + "\u2014 no cont\u00F3 para el ELO de nadie. Marca \u201CRecord Game\u201D en la "
                     + "pantalla de configuraci\u00F3n de AoE3 antes de la siguiente.",
        },
        // The rest of the "it didn't count" family. The server says WHY, and the advice
        // has to match the cause: telling someone to tick Record Game after a team game
        // sends them to fix something that was never the problem. MpResultNoneBody above
        // stays the message for a missing recording, and the fallback for a reason this
        // build has never heard of.
        ["MpResultUnratedTeam"] = new()
        {
            [LangEn] = "Only one-on-one matches count towards the rating — a recording names "
                     + "one loser, which says nothing about who won a team game. This one is in "
                     + "your history all the same.",
            [LangEs] = "Solo las partidas uno contra uno cuentan para el ELO — una grabación "
                     + "nombra a un perdedor, y eso no dice quién ganó una partida por equipos. "
                     + "Igualmente queda en tu historial.",
        },
        ["MpResultUnratedMod"] = new()
        {
            [LangEn] = "This mod has no ladder yet, so the match counted towards no one's rating. "
                     + "It is in your history all the same.",
            [LangEs] = "Este mod todavía no tiene clasificación, así que la partida no contó "
                     + "para el ELO de nadie. Igualmente queda en tu historial.",
        },
        ["MpResultUnratedNotCompetitive"] = new()
        {
            [LangEn] = "This room wasn't a competitive one, so the match counted towards nobody's "
                     + "rating. It is in your history all the same.",
            [LangEs] = "Esta sala no era competitiva, así que la partida no contó para el ELO de "
                     + "nadie. Igualmente queda en tu historial.",
        },
        ["MpResultUnratedDuplicate"] = new()
        {
            [LangEn] = "This recording had already been reported, so it did not count a second "
                     + "time. If the match was real, it is already in your history.",
            [LangEs] = "Esta grabación ya se había reportado, así que no contó una segunda vez. "
                     + "Si la partida fue real, ya está en tu historial.",
        },
        ["MpResultUnratedRoster"] = new()
        {
            [LangEn] = "Someone in this report was not in the room when the game started, so it "
                     + "counted towards no one's rating.",
            [LangEs] = "Alguien de este reporte no estaba en la sala cuando empezó la partida, "
                     + "así que no contó para el ELO de nadie.",
        },
        ["MpResultUnratedTiming"] = new()
        {
            [LangEn] = "The times reported for this match don't add up, so it counted towards no "
                     + "one's rating. A game has to run at least a few minutes.",
            [LangEs] = "Los tiempos de esta partida no cuadran, así que no contó para el ELO de "
                     + "nadie. Una partida tiene que durar al menos unos minutos.",
        },
        // The LOCAL half: why the launcher could not read a result. These only appear
        // when the server had nothing more specific to say, and they exist because all
        // five causes used to produce MpResultNoneBody — "tick Record Game" — which is
        // right for a missing recording and useless advice for the rest.
        ["MpResultUnratedNoProfile"] = new()
        {
            [LangEn] = "Your AoE3 profile name could not be read, so there was no way to find "
                     + "you among the players in the recording. The match is in your history "
                     + "all the same.",
            [LangEs] = "No se pudo leer el nombre de tu perfil de AoE3, as\u00ED que no hubo forma "
                     + "de encontrarte entre los jugadores de la grabaci\u00F3n. Igualmente la "
                     + "partida queda en tu historial.",
        },
        ["MpResultUnratedNoRoster"] = new()
        {
            [LangEn] = "The room was already gone when the match ended, so there was nobody to "
                     + "check the recording against.",
            [LangEs] = "La sala ya no exist\u00EDa cuando termin\u00F3 la partida, as\u00ED que no hab\u00EDa "
                     + "contra qui\u00E9n contrastar la grabaci\u00F3n.",
        },
        ["MpResultUnratedUnreadable"] = new()
        {
            [LangEn] = "The recording of this match could not be read \u2014 it may have been cut "
                     + "short when the game closed.",
            [LangEs] = "La grabaci\u00F3n de esta partida no se pudo leer \u2014 puede que se cortara "
                     + "al cerrarse el juego.",
        },
        ["MpResultUnratedAmbiguous"] = new()
        {
            [LangEn] = "The recording does not say who won, so the match counted towards no "
                     + "one's rating.",
            [LangEs] = "La grabaci\u00F3n no dice qui\u00E9n gan\u00F3, as\u00ED que la partida no cont\u00F3 para "
                     + "el ELO de nadie.",
        },
        // Recordings were found and read perfectly, and none of them is this match. This used
        // to fall on MpResultNoneBody — "it was not recorded" — which is the one piece of
        // advice that is certainly wrong here, since the recordings are fine. The likeliest
        // cause is named because it fails every single match until it is fixed; the concrete
        // names are appended by the card from LocalFailureDetail.
        ["MpResultUnratedNotOurs"] = new()
        {
            [LangEn] = "Recordings were found, but none of them has you among its players, so "
                     + "there was no way to tell which one was this match. Check that your AoE3 "
                     + "profile name is the one you actually play under.",
            [LangEs] = "Se encontraron grabaciones, pero en ninguna apareces entre los jugadores, "
                     + "así que no hubo forma de saber cuál era esta partida. Verifica que el "
                     + "nombre de tu perfil de AoE3 sea el mismo con el que juegas.",
        },
        // Appended to the message above by the card. {0} is the profile name we read, {1} the
        // names the recordings carried — the two side by side are what make the mismatch
        // obvious. Names are data: they are never translated, only framed.
        // The bell for a match decided after its room had already closed. The room is long
        // gone by then, so this is the only place the correction can surface.
        ["NotifMatchRatedTitle"] = new()
        {
            [LangEn] = "A match of yours was rated",
            [LangEs] = "Se puntuó una partida tuya",
        },
        ["NotifMatchRatedBodyDelta"] = new()
        {
            [LangEn] = "The recording was read after the match ended: {0} ({1}).",
            [LangEs] = "La grabación se leyó después de terminar la partida: {0} ({1}).",
        },
        ["NotifMatchRatedBody"] = new()
        {
            [LangEn] = "The recording was read after the match ended: {0}.",
            [LangEs] = "La grabación se leyó después de terminar la partida: {0}.",
        },
        // The result itself could not be named — the match still counts, so say so rather
        // than inventing a verdict for it.
        ["NotifMatchRatedBodyPlain"] = new()
        {
            [LangEn] = "It counted towards the rating after all. It's in your History.",
            [LangEs] = "Al final sí contó para el ELO. Está en tu historial.",
        },
        ["MpResultNotOursDetail"] = new()
        {
            [LangEn] = "(yours: “{0}” · in the recordings: {1})",
            [LangEs] = "(el tuyo: “{0}” · en las grabaciones: {1})",
        },
        // The recording exists and is this match; the game just never finished writing its
        // ending. Measured on a real 18-minute 1v1: 5 of the outcome block's 12 bytes.
        ["MpResultUnratedNoOutcome"] = new()
        {
            [LangEn] = "This match WAS recorded, but the game closed before it finished writing "
                     + "the ending, so the recording does not say who won. Leave the match to the "
                     + "main menu before closing AoE3 and the next one will count.",
            [LangEs] = "Esta partida SÍ se grabó, pero el juego se cerró antes de terminar de "
                     + "escribir el final, así que la grabación no dice quién ganó. Sal de la "
                     + "partida hasta el menú principal antes de cerrar AoE3 y la próxima sí "
                     + "contará.",
        },
        // Not a failure — a wait. Nothing is known about this player's recording yet because
        // their AoE3 is still open, and claiming anything is what produced the wrong message.
        ["MpResultUnratedReadPending"] = new()
        {
            [LangEn] = "The match ended while your AoE3 was still open, so your recording has "
                     + "not been read yet. Close the game and the launcher will read it — if it "
                     + "names a winner, the match can still count.",
            [LangEs] = "La partida terminó con tu AoE3 todavía abierto, así que tu grabación "
                     + "aún no se leyó. Cierra el juego y el launcher la va a leer — si nombra "
                     + "un ganador, la partida todavía puede contar.",
        },
        ["MpResultUnratedNoLobby"] = new()
        {
            [LangEn] = "This match was reported without a room, so there was no way to check who "
                     + "played. It counted towards no one's rating.",
            [LangEs] = "Esta partida se reportó sin sala, así que no había forma de comprobar "
                     + "quiénes jugaron. No contó para el ELO de nadie.",
        },
        ["MpResultRatingBefore"] = new() { [LangEn] = "was {0}", [LangEs] = "antes {0}" },
        ["MpResultMinutes"] = new() { [LangEn] = "{0} min", [LangEs] = "{0} min" },
        ["MpResultPlayers"] = new() { [LangEn] = "{0} players", [LangEs] = "{0} jugadores" },
        // Your civilization against theirs. Both names come from the mod's own string table, so
        // they are already in the language that mod was installed in.
        ["MpResultCivMatchup"] = new() { [LangEn] = "{0} vs {1}", [LangEs] = "{0} vs {1}" },
        ["MpResultDecidedHeader"] = new() { [LangEn] = "DECIDED", [LangEs] = "DECIDIDAS" },
        ["MpResultReplayHeader"] = new() { [LangEn] = "REPLAY", [LangEs] = "REPETICI\u00D3N" },
        // "not uploaded" until the cell started naming the FILE. Upload is still scaffolded
        // with no caller, but the cell no longer talks about it, so its empty state is now the
        // honest one: the game wrote nothing.
        ["MpResultReplayNone"] = new() { [LangEn] = "no recording", [LangEs] = "sin grabación" },
        ["MpResultReplayReveal"] = new()
        {
            [LangEn] = "Show it in Explorer.",
            [LangEs] = "Mostrarla en el Explorador.",
        },
        ["MpResultRivalHeader"] = new() { [LangEn] = "OPPONENT", [LangEs] = "RIVAL" },
        ["MpResultUnknownValue"] = new() { [LangEn] = "\u2014", [LangEs] = "\u2014" },
        ["MpResultProvisional"] = new()
        {
            [LangEn] = "Your rating is still provisional \u2014 it settles after a few more decided matches.",
            [LangEs] = "Tu rating sigue siendo provisional \u2014 se estabiliza tras unas cuantas partidas decididas m\u00E1s.",
        },
        ["MpResultRematch"] = new() { [LangEn] = "Rematch", [LangEs] = "Revancha" },
        ["MpResultBackToRooms"] = new() { [LangEn] = "Back to rooms", [LangEs] = "Volver a salas" },
        ["MpResultPendingTimeout"] = new()
        {
            [LangEn] = "The result has not come through yet \u2014 it will show up under History.",
            [LangEs] = "El resultado todav\u00EDa no ha llegado \u2014 aparecer\u00E1 en Historial.",
        },
        ["MpResultPending"] = new()
        {
            [LangEn] = "Working out the result…",
            [LangEs] = "Calculando el resultado…",
        },
        // The guest's half of the wait, and the state that used to be blank. Everything this
        // launcher could do is done; what is left is the other player's machine.
        ["MpResultWaitingHost"] = new()
        {
            [LangEn] = "Waiting for the host to send the result…",
            [LangEs] = "Esperando a que el anfitrión envíe el resultado…",
        },
        ["MpCreateDialogTitle"] = new() { [LangEn] = "Create a room", [LangEs] = "Crear una sala" },
        ["MpCreateDialogTitleLabel"] = new() { [LangEn] = "Room title", [LangEs] = "Título de la sala" },
        ["MpCreateDialogMaxPlayers"] = new() { [LangEn] = "Max players", [LangEs] = "Jugadores máx." },
        ["MpCreateDialogObservers"] = new() { [LangEn] = "Observers", [LangEs] = "Observadores" },
        ["MpCreateDialogObserversHint"] = new()
        {
            // The whole point of the row, said before it is used rather than discovered after.
            // AoE3 has no spectator mode: an observer is a real player in a real slot, and it
            // is the MAP that leaves them with nothing. So it only works on a map built for it
            // — which is why the sentence names them instead of saying "supported maps".
            [LangEn] = "An observer takes a seat and plays nothing — no town centre, no "
                     + "settlers. Only on an observer map (OBS_ESOC…); on any other they "
                     + "start as an ordinary player.",
            [LangEs] = "Un observador ocupa una plaza y no juega: sin centro urbano ni "
                     + "aldeanos. Solo en un mapa de observador (OBS_ESOC…); en cualquier "
                     + "otro empieza como un jugador normal.",
        },
        ["MpCreateDialogObserversCost"] = new()
        {
            // {0} observers, {1} people actually playing. The second number is the one a host
            // is about to get wrong, so it is stated rather than left to be subtracted.
            [LangEn] = "{0} watching, {1} playing.",
            [LangEs] = "{0} mirando, {1} jugando.",
        },
        ["MpCreateDialogPassword"] = new()
        {
            [LangEn] = "Password (optional)",
            [LangEs] = "Contraseña (opcional)",
        },
        // No "(optional)". Everything on this form is optional; saying it on one checkbox
        // implies the others are not.
        ["MpCreateDialogPrivate"] = new()
        {
            [LangEn] = "Private room",
            [LangEs] = "Sala privada",
        },
        // Heads-up shown next to the "Private room" toggle so the host chooses
        // knowingly: a private room is deliberately NOT announced (backend skips
        // it for Discord AND the in-app popup), it's only reachable by browsing
        // + entering the password.
        ["MpCreateDialogPrivateHint"] = new()
        {
            [LangEn] = "A private room isn't announced (on Discord or in-app) and needs the password to join.",
            [LangEs] = "Una sala privada no se anuncia (ni en Discord ni como aviso) y se une con la contraseña.",
        },
        // Competitive. The hint is long on purpose: everything it lists is a restriction the
        // host is agreeing to, and the one that surprises people is not being able to walk out
        // freely. Better read once here than discovered at the worst moment.
        // The parenthetical went into the line under it, where the rest of the explanation
        // already was - a title that carries half a sentence reads as two labels.
        ["MpCreateDialogCompetitive"] = new()
        {
            [LangEn] = "Competitive room",
            [LangEs] = "Sala competitiva",
        },
        // THE FORFEIT CLAUSE LEFT THIS STRING, and it has to stay out. It used to end with
        // "walking out after the first five minutes counts as a loss", which is a 1v1 rule:
        // decideByAbandon refuses anything but two participants, so in a team room the launcher
        // was threatening a penalty the server does not apply. It now lives in
        // MpCreateDialogCompetitiveForfeit, shown only for 1v1. The two clauses that remain are
        // true for every format — ConfirmRecordGameAsync and RoomMatchState.HoldLeave both key
        // off the competitive flag alone.
        ["MpCreateDialogCompetitiveHint"] = new()
        {
            // FOUR LINES BECAME ONE, and the rest did not vanish - it became the amber Record
            // Game box, which now appears exactly when this is ticked. The two said the same
            // thing in different words, fifteen pixels apart, and the box says it at the
            // moment it becomes true. A checkbox caption can carry what the room IS; how the
            // result gets decided needs its own space.
            [LangEn] = "Matches in this room count towards the rating.",
            [LangEs] = "Las partidas de esta sala cuentan para el ELO.",
        },
        // 1v1 only. See the note above the hint.
        ["MpCreateDialogCompetitiveForfeit"] = new()
        {
            [LangEn] = "Walking out after the first five minutes counts as a loss.",
            [LangEs] = "Si abandonas después de los primeros cinco minutos, cuenta como derrota.",
        },
        // The three competitive formats. Short on purpose: they are segment captions in a
        // row three wide, and every language writes them the same way.
        ["MpFormat1v1"] = new() { [LangEn] = "1v1", [LangEs] = "1v1" },
        ["MpFormat2v2"] = new() { [LangEn] = "2v2", [LangEs] = "2v2" },
        ["MpFormat3v3"] = new() { [LangEn] = "3v3", [LangEs] = "3v3" },
        // Sentence case, like every other section label in this dialog. It was the one
        // heading in caps, which made it read as a different KIND of question from the
        // identical ones outside the card.
        ["MpCreateDialogFormat"] = new()
        {
            [LangEn] = "Format",
            [LangEs] = "Formato",
        },
        // Shown for a TEAM format only. It informs, it does not forbid — and it has to be said
        // here, because the hint above promises a rating and for these two that is not true yet.
        // Shown on the end-of-match card for a team game whose result is in but whose
        // corroboration is not. See teamEvidenceMet on the backend.
        ["MpResultUnratedAwaitingTeam"] = new()
        {
            [LangEn] = "The result is in, but the team ladder also needs a reading from the "
                     + "other side. It counts as soon as one of them closes the game with the "
                     + "launcher still open.",
            [LangEs] = "El resultado ya está, pero la clasificación de equipos necesita además "
                     + "una lectura del otro bando. Cuenta en cuanto uno de ellos cierre el "
                     + "juego con el launcher abierto.",
        },
        ["MpCreateDialogCompetitiveTeamNote"] = new()
        {
            // It used to say a team match moved nobody's rating. It does now — on its own
            // ladder — so what this has to explain instead is the condition, because that
            // is the part a player can do something about: somebody on the OTHER side has
            // to still have the launcher open when the game closes.
            [LangEn] = "A {0} scores on its own ladder — the team one, separate from 1v1. It "
                     + "counts once a player from EACH side has read the recording: two "
                     + "readings that agree, one per team, so no side decides its own result.",
            [LangEs] = "Un {0} puntúa en una clasificación aparte, la de equipos, separada de "
                     + "la de 1v1. Cuenta cuando un jugador de CADA bando lee la grabación: "
                     + "dos lecturas que coincidan, una por equipo, para que ningún bando "
                     + "decida su propio resultado.",
        },
        // The server refused the competitive room and made a casual one. Saying nothing would
        // leave the host playing as if their rating were on the line when it is not.
        ["MpCreateDialogCompetitiveDowngraded"] = new()
        {
            [LangEn] = "This mod has no ladder yet, so the room was created as a normal one — "
                     + "the match won't count towards anyone's rating.",
            [LangEs] = "Este mod todavía no tiene clasificación, así que la sala se creó como "
                     + "normal: la partida no va a contar para el ELO.",
        },
        ["MpCreateDialogModLabel"] = new()
        {
            [LangEn] = "Mod",
            [LangEs] = "Mod",
        },
        // Label + read-only hint for the create-room copy picker (shown only
        // when the selected mod has 2+ installed copies).
        ["MpCreateDialogCopyLabel"] = new()
        {
            [LangEn] = "Copy to use",
            [LangEs] = "Copia a usar",
        },
        ["MpCreateDialogCopyHintReadonly"] = new()
        {
            [LangEn] = "This is the mod's active copy. To host with another copy, switch the active copy from the Library first.",
            [LangEs] = "Es la copia activa del mod. Para hostear con otra copia, cambia la copia activa desde la Biblioteca primero.",
        },
        ["MpCreateDialogHashLabel"] = new()
        {
            [LangEn] = "Mod fingerprint",
            [LangEs] = "Huella del mod",
        },
        // Fingerprint state, shown UNDER the mod name in the create-room dialog. It was
        // hidden behind an "Advanced details" toggle; a mismatched fingerprint is the
        // usual reason another player cannot join, so it belongs on screen.
        ["MpCreateDialogFingerprintOk"] = new()
        {
            [LangEn] = "Fingerprint verified · {0}",
            [LangEs] = "Huella verificada · {0}",
        },
        ["MpCreateDialogFingerprintLoading"] = new()
        {
            [LangEn] = "Computing fingerprint…",
            [LangEs] = "Calculando huella…",
        },
        ["MpCreateDialogFingerprintFailed"] = new()
        {
            [LangEn] = "Couldn't read the fingerprint — verify the install",
            [LangEs] = "No se pudo leer la huella — verifica la instalación",
        },
        // Title suggestions. They are appended to the room title, so they read as
        // things a host announces about the match, not as titles by themselves.
        ["MpCreateDialogSuggest1"] = new() { [LangEn] = "Quick 1v1", [LangEs] = "1v1 rápido" },
        ["MpCreateDialogSuggest2"] = new() { [LangEn] = "No rush 10 min", [LangEs] = "Sin rush 10 min" },
        ["MpCreateDialogPrivateBody"] = new()
        {
            [LangEn] = "Not announced. Share the code or the password.",
            [LangEs] = "No se anuncia. Comparte el código o la contraseña.",
        },
        ["MpCreateDialogShowPassword"] = new() { [LangEn] = "Show", [LangEs] = "Mostrar" },
        ["MpCreateDialogHidePassword"] = new() { [LangEn] = "Hide", [LangEs] = "Ocultar" },
        // The recording warning belongs here, BEFORE the match: by the time the game is
        // over there is nothing left to fix. {0} is the in-game checkbox's own name,
        // which stays in English because that is what AoE3 shows.
        ["MpCreateDialogRecordWarn"] = new()
        {
            [LangEn] = "For the match to count towards ELO, tick {0} on the AoE3 setup screen. "
                     + "Without a recording nobody knows who won.",
            [LangEs] = "Para que la partida cuente en el ELO, marca {0} en la pantalla de configuración de AoE3. "
                     + "Sin grabación nadie sabe quién ganó.",
        },
        ["MpCreateDialogRecordWarnName"] = new() { [LangEn] = "Record Game", [LangEs] = "Record Game" },
        ["MpCreateDialogAnnounceNote"] = new()
        {
            [LangEn] = "It will be announced in the global chat and on Discord.",
            [LangEs] = "Se anunciará en el chat global y en Discord.",
        },
        ["MpCreateDialogAnnounceNotePrivate"] = new()
        {
            [LangEn] = "A private room is not announced anywhere.",
            [LangEs] = "Una sala privada no se anuncia en ningún sitio.",
        },
        // The counts box, assembled from these with " \u00b7 " between them. Lower case and
        // no full stops: they are fragments of one line, not sentences.
        ["MpCreateDialogSummarySeats"] = new()
        {
            [LangEn] = "{0} seats",
            [LangEs] = "{0} plazas",
        },
        ["MpCreateDialogSummarySeatsOf"] = new()
        {
            // The format fixes how many PLAY; the second number is the room cap, which is
            // what says the rest of the seats are not missing, they are unused.
            [LangEn] = "{0} of {1} seats",
            [LangEs] = "{0} de {1} plazas",
        },
        ["MpCreateDialogSummaryObservers"] = new()
        {
            [LangEn] = "{0} watching",
            [LangEs] = "{0} mirando",
        },
        ["MpCreateDialogSummaryPublic"] = new()
        {
            [LangEn] = "public room",
            [LangEs] = "sala p\u00fablica",
        },
        ["MpCreateDialogSummaryPrivate"] = new()
        {
            [LangEn] = "private room",
            [LangEs] = "sala privada",
        },
        ["MpCreateDialogSummaryElo"] = new()
        {
            [LangEn] = "counts for ELO",
            [LangEs] = "punt\u00faa para el ELO",
        },
        ["MpCreateDialogSummaryNoElo"] = new()
        {
            // Said of a team room as well, and that is the point: it is competitive, it is
            // playable, and it still moves nobody's rating.
            [LangEn] = "no ELO",
            [LangEs] = "sin ELO",
        },
        ["MpCreateDialogSummaryAnnounced"] = new()
        {
            [LangEn] = "announced in chat and on Discord",
            [LangEs] = "se anuncia en el chat y en Discord",
        },
        ["MpCreateDialogSummaryQuiet"] = new()
        {
            // The promise a private room is ticked for.
            [LangEn] = "not announced",
            [LangEs] = "no se anuncia",
        },
        ["MpCreateDialogTitleTooShort"] = new()
        {
            [LangEn] = "The title needs at least 3 characters.",
            [LangEs] = "El título necesita al menos 3 caracteres.",
        },
        ["MpCreateDialogNoFingerprint"] = new()
        {
            [LangEn] = "Pick a mod — the fingerprint is still being computed.",
            [LangEs] = "Elige un mod — la huella todavía se está calculando.",
        },
        // KEPT FOR RECOGNITION ONLY - do not delete as unused. The proposed room title is a
        // network value now and no longer localized (see RoomTitleProposal), but titles this
        // key produced are still sitting in hosts' boxes and on the lobby server, and
        // RoomTitleProposal.LegacyProposals has to be able to reproduce them or those boxes
        // freeze. Same for MpRoomCompetitiveBadge, which is also still live in three badges.
        ["MpCreateDialogDefaultTitle"] = new()
        {
            [LangEn] = "{0} room",
            [LangEs] = "Sala de {0}",
        },
        // Asked before EVERY competitive start, not once per room: AoE3's own Record Game box
        // comes up unticked for each match and the launcher cannot tick it. This is the last
        // moment the player is looking at us rather than at the game.
        ["MpStartConfirmRecordTitle"] = new()
        {
            [LangEn] = "Before you start",
            [LangEs] = "Antes de empezar",
        },
        ["MpStartConfirmRecordBody"] = new()
        {
            [LangEn] = "When the game opens, tick Record Game on the match setup screen. It is a "
                     + "box inside Age of Empires III that comes up unticked every single match, "
                     + "and the launcher can't tick it for you. Without a recording there is no "
                     + "way to tell who won, and the match won't score.",
            [LangEs] = "Cuando abra el juego, marca «Record Game» en la pantalla de la partida. "
                     + "Es una casilla del propio Age of Empires III que vuelve desmarcada en "
                     + "cada partida, y el launcher no puede activarla por ti. Sin grabación no "
                     + "hay forma de saber quién ganó, y la partida no puntúa.",
        },
        // The same prompt once the launcher has EVIDENCE. It leads with the fact rather than the
        // instruction, because a reminder that reads identically every time stops being read —
        // and this one is not a reminder, it is something that already happened.
        ["MpStartConfirmRecordTitleAgain"] = new()
        {
            [LangEn] = "Your last match wasn't recorded",
            [LangEs] = "Tu partida anterior no se grabó",
        },
        ["MpStartConfirmRecordBodyAgain"] = new()
        {
            [LangEn] = "The previous competitive match left no recording, so it counted for "
                     + "nobody. The box is inside Age of Empires III and the launcher cannot tick "
                     + "it for you: when the game opens, tick Record Game on the match setup "
                     + "screen — it comes up unticked every single match.",
            [LangEs] = "La partida competitiva anterior no dejó grabación, así que no contó para "
                     + "nadie. La casilla está dentro de Age of Empires III y el launcher no "
                     + "puede marcarla por ti: cuando abra el juego, marca «Record Game» en la "
                     + "pantalla de la partida — vuelve desmarcada en cada partida.",
        },
        ["MpStartConfirmRecordYes"] = new()
        {
            [LangEn] = "Got it — start",
            [LangEs] = "Entendido, empezar",
        },
        ["MpStartConfirmRecordNo"] = new() { [LangEn] = "Cancel", [LangEs] = "Cancelar" },
        // Held, not asked: leaving here hands the host role to the opponent and the server then
        // refuses the report outright, so there is no upside to weigh against.
        ["MpLeaveBlockedTitle"] = new()
        {
            [LangEn] = "One moment",
            [LangEs] = "Espera un momento",
        },
        ["MpLeaveBlockedReading"] = new()
        {
            [LangEn] = "The recording is still being read to work out who won. Leaving now would "
                     + "cost both players the result.",
            [LangEs] = "Estamos leyendo la grabación para saber quién ganó. Si sales ahora, la "
                     + "partida no va a contar para ninguno de los dos.",
        },
        // Held for a different reason than the other two: nothing of ours is outstanding, so
        // leaving costs nobody the report — it costs only this player the sight of their own
        // result. Say that, rather than borrowing the host's "it won't count" wording, which
        // would be false here.
        ["MpLeaveBlockedWaitingHost"] = new()
        {
            [LangEn] = "The host is still closing their game. The result arrives in a moment — "
                     + "leave now and you will not see it.",
            [LangEs] = "El anfitrión todavía está cerrando su juego. El resultado llega en un "
                     + "momento; si sales ahora no lo vas a ver.",
        },
        ["MpLeaveBlockedReporting"] = new()
        {
            [LangEn] = "The result is on its way to the server. Leaving now makes it refuse the "
                     + "report, and the match counts for nobody.",
            [LangEs] = "Estamos enviando el resultado. Si sales ahora el servidor lo rechaza y "
                     + "la partida no cuenta para nadie.",
        },
        // Closing the launcher AFTER the game has closed but before the result is settled. The
        // ordinary mid-match warning would be false here — nothing is running to be cut short.
        ["MpCloseDuringResultBody"] = new()
        {
            [LangEn] = "The launcher is still reading the recording to work out who won. If you "
                     + "quit now that match ends up with no result, and nothing will remember it "
                     + "was owed one. Quit anyway?",
            [LangEs] = "El launcher todavía está leyendo la grabación para saber quién ganó. Si "
                     + "cierras ahora, esa partida se queda sin resultado y ya no hay forma de "
                     + "recuperarlo. ¿Cerrar de todos modos?",
        },
        ["MpRoomCompetitiveBadge"] = new()
        {
            [LangEn] = "COMPETITIVE",
            [LangEs] = "COMPETITIVA",
        },
        ["MpRoomCompetitiveTooltip"] = new()
        {
            [LangEn] = "Competitive room — this match counts towards the rating.",
            [LangEs] = "Sala competitiva: esta partida cuenta para el ELO.",
        },
        // What pressing it DOES, which is the tournament dialog's rule for a primary - and
        // still not the window's own title, which is "Create a room" / "Crear una sala".
        ["MpCreateDialogCreate"] = new() { [LangEn] = "Create room", [LangEs] = "Crear sala" },
        // Shown ON the Create button while the request is in flight. Without it the only
        // thing that happens on click is that both buttons go inert, which reads as "nothing
        // happened" at the exact moment the user needs to know it did.
        ["MpCreateDialogCreating"] = new() { [LangEn] = "Creating…", [LangEs] = "Creando…" },
        ["MpCreateDialogCancel"] = new() { [LangEn] = "Cancel", [LangEs] = "Cancelar" },
        ["MpCreateDialogRadminWarning"] = new()
        {
            [LangEn] = "ℹ Radmin VPN isn't active. You can still create the room and players can join it — "
                     + "to actually play, install/enable Radmin and join the AoE3 network (every player needs it).",
            [LangEs] = "ℹ Radmin VPN no está activo. Puedes crear la sala igual y los jugadores pueden unirse — "
                     + "para jugar, instala/activa Radmin y únete a la red de AoE3 (todos los jugadores lo necesitan).",
        },
        ["MpCreateDialogRadminInfo"] = new()
        {
            [LangEn] = "ℹ Radmin VPN isn't active, but the room is created normally and your Radmin IP ({0}) "
                     + "is injected automatically. To actually play, open Radmin and connect to the AoE3 network "
                     + "(every player needs it).",
            [LangEs] = "ℹ Radmin VPN no está activo, pero la sala se crea igual y tu IP de Radmin ({0}) se "
                     + "inyecta automáticamente. Para jugar, abre Radmin y conéctate a la red de AoE3 (todos "
                     + "los jugadores lo necesitan).",
        },
        ["MpModNotInstalled"] = new()
        {
            [LangEn] = "Install the mod first to host or join a room for it.",
            [LangEs] = "Instala primero el mod para crear o unirte a una sala suya.",
        },
        ["MpQuotaBar"] = new()
        {
            [LangEn] = "{0}/{1} players online · {2}/{3} active rooms",
            [LangEs] = "{0}/{1} jugadores online · {2}/{3} salas activas",
        },
        ["SettingsTabTeaser"] = new()
        {
            [LangEn] = "Launcher preferences (language, theme, autostart, mods catalog, …)",
            [LangEs] = "Preferencias del launcher (idioma, tema, autoarranque, catálogo de mods, …)",
        },
        ["SettingsTabOpen"] = new()
        {
            [LangEn] = "Open settings",
            [LangEs] = "Abrir configuración",
        },
        // The label/hint name the thing this toggle actually DOES: start with
        // Windows. The old copy ("stay shown as connected even without the window
        // open") described something the user already had — presence rides an
        // always-on socket while signed in, and CloseToTray defaults on — so it read
        // as "isn't this already happening?" and made the toggle look broken.
        ["DlgLauncherSettingsStartWithWindows"] = new()
        {
            [LangEn] = "Start with Windows in the background",
            [LangEs] = "Iniciar con Windows en segundo plano",
        },
        ["DlgLauncherSettingsStartWithWindowsHint"] = new()
        {
            [LangEn] = "It waits in the system tray, so you show up as online without opening it.",
            [LangEs] = "Se queda en la bandeja del sistema y apareces como conectado sin abrirlo.",
        },
        ["DlgLauncherSettingsStartupFailed"] = new()
        {
            [LangEn] = "Windows would not let the launcher register itself to start automatically. "
                     + "This can be a PC policy or your antivirus blocking it.",
            [LangEs] = "Windows no dejó que el launcher se registrara para iniciarse automáticamente. "
                     + "Puede ser una política de la PC o tu antivirus bloqueándolo.",
        },
        // Opt-in prompt shown when enabling "start with Windows" from a portable exe
        // that hasn't been installed to a stable location yet — auto-start needs a
        // durable path or it silently breaks when the .exe is moved/deleted.
        // --- The auto-start question, asked once, before anything is written ---
        // It names the registry key and where to undo it, which the balloon it replaced did
        // not. The Yes button is the recommendation and nothing more: the X, Escape and a
        // closed window all count as no.
        ["DlgBackgroundConsentTitle"] = new()
        {
            [LangEn] = "Start with Windows?",
            [LangEs] = "¿Iniciar con Windows?",
        },
        ["DlgBackgroundConsentBody"] = new()
        {
            [LangEn] = "The launcher can start with Windows and wait in the system tray, so "
                     + "your friends see you as connected and you get a notification when "
                     + "somebody opens a room — without having to open it yourself.",
            [LangEs] = "El launcher puede iniciarse con Windows y esperar en la bandeja del "
                     + "sistema, para que tus amigos te vean conectado y te avise cuando "
                     + "alguien abra una sala — sin que tengas que abrirlo tú.",
        },
        ["DlgBackgroundConsentDetail"] = new()
        {
            [LangEn] = "Saying yes adds one Windows startup entry for your user account only. "
                     + "No service, no scheduled task, no administrator rights. You can change "
                     + "your mind any time in Settings → General.",
            [LangEs] = "Si aceptas se añade una entrada de inicio de Windows, sólo para tu "
                     + "cuenta de usuario. Sin servicios, sin tareas programadas y sin permisos "
                     + "de administrador. Puedes cambiar de idea cuando quieras en "
                     + "Configuración → General.",
        },
        ["DlgBackgroundConsentYes"] = new()
        {
            [LangEn] = "Yes, start with Windows",
            [LangEs] = "Sí, iniciar con Windows",
        },
        ["DlgBackgroundConsentNo"] = new()
        {
            [LangEn] = "No, thanks",
            [LangEs] = "No, gracias",
        },
        ["DlgSettingsBgInstallPromptTitle"] = new()
        {
            [LangEn] = "Install a stable copy?",
            [LangEs] = "¿Instalar una copia estable?",
        },
        ["DlgSettingsBgInstallPromptBody"] = new()
        {
            [LangEn] = "For \"start with Windows\" to keep working, the launcher should run from a "
                     + "fixed location. Right now it runs from this .exe, and auto-start will break if "
                     + "you move or delete it.\n\n"
                     + "Install a stable copy on this PC now? (You can keep using this .exe; nothing "
                     + "else changes.)",
            [LangEs] = "Para que \"iniciar con Windows\" siga funcionando, el launcher debería ejecutarse "
                     + "desde una ubicación fija. Ahora se ejecuta desde este .exe, y el inicio automático "
                     + "se romperá si lo mueves o lo borras.\n\n"
                     + "¿Instalar ahora una copia estable en esta PC? (Puedes seguir usando este .exe; no "
                     + "cambia nada más.)",
        },
        ["DlgSettingsBgInstallFailed"] = new()
        {
            [LangEn] = "Couldn't install the stable copy. Auto-start was set up to use this .exe instead — "
                     + "it may stop working if you move or delete it.",
            [LangEs] = "No se pudo instalar la copia estable. El inicio automático quedó apuntando a este "
                     + ".exe — puede dejar de funcionar si lo mueves o lo borras.",
        },
        // Buttons for the themed first-launch install dialog (SelfInstallPromptDialog).
        ["DlgSettingsBgInstallPromptYes"] = new()
        {
            [LangEn] = "Install a copy",
            [LangEs] = "Instalar una copia",
        },
        ["DlgSettingsBgInstallPromptNo"] = new()
        {
            [LangEn] = "Not now",
            [LangEs] = "Ahora no",
        },
        // CompatibilityLayerDialog — shown when a launch needed elevation because Windows
        // pinned a compatibility layer on the game .exe. The copy has to make two things
        // clear, because both were reported as "the launcher broke my game": WINDOWS did
        // this, not us; and it is reversible.
        ["DlgCompatLayerTitle"] = new()
        {
            [LangEn] = "Windows is forcing administrator on your game",
            [LangEs] = "Windows está forzando el modo administrador en tu juego",
        },
        ["DlgCompatLayerBody"] = new()
        {
            [LangEn] = "Windows set a compatibility mode on your game by itself, and that is what makes it "
                     + "ask for administrator permission every time you open it. It also stops the launcher "
                     + "from keeping the game running when the launcher is force-closed.\n\n"
                     + "The launcher can remove it. This only affects this one file, only for your user "
                     + "account, and you can turn it back on anytime from the file's Properties → "
                     + "Compatibility if the game starts misbehaving.",
            [LangEs] = "Windows le puso un modo de compatibilidad a tu juego por su cuenta, y eso es lo que "
                     + "hace que pida permisos de administrador cada vez que lo abres. Además impide que el "
                     + "launcher mantenga el juego abierto si cierras el launcher a la fuerza.\n\n"
                     + "El launcher lo puede quitar. Solo afecta a este archivo, solo para tu usuario, y lo "
                     + "puedes volver a activar cuando quieras desde Propiedades → Compatibilidad del archivo "
                     + "si el juego te empieza a fallar.",
        },
        // Variant for a layer the USER set on purpose (no "~" marker, or machine-wide):
        // it is their choice, so we explain and point at Properties instead of offering
        // to undo it.
        ["DlgCompatLayerBodyManual"] = new()
        {
            [LangEn] = "Your game has a compatibility mode set, and that is what makes it ask for "
                     + "administrator permission every time you open it. It also stops the launcher from "
                     + "keeping the game running when the launcher is force-closed.\n\n"
                     + "This one wasn't set by Windows, so the launcher won't touch it. You can review it "
                     + "in the file's Properties → Compatibility.",
            [LangEs] = "Tu juego tiene puesto un modo de compatibilidad, y eso es lo que hace que pida "
                     + "permisos de administrador cada vez que lo abres. Además impide que el launcher "
                     + "mantenga el juego abierto si cierras el launcher a la fuerza.\n\n"
                     + "Este no lo puso Windows, así que el launcher no lo va a tocar. Lo puedes revisar en "
                     + "Propiedades → Compatibilidad del archivo.",
        },
        ["DlgCompatLayerFileLabel"] = new()
        {
            [LangEn] = "Affected file",
            [LangEs] = "Archivo afectado",
        },
        ["DlgCompatLayerRemove"] = new()
        {
            [LangEn] = "Remove it",
            [LangEs] = "Quitarlo",
        },
        ["DlgCompatLayerProperties"] = new()
        {
            [LangEn] = "Open file properties",
            [LangEs] = "Abrir propiedades del archivo",
        },
        ["DlgCompatLayerLater"] = new()
        {
            [LangEn] = "Not now",
            [LangEs] = "Ahora no",
        },
        ["DlgCompatLayerDontAsk"] = new()
        {
            [LangEn] = "Don't show this again",
            [LangEs] = "No volver a mostrar esto",
        },
        ["StatusCompatLayerRemoved"] = new()
        {
            [LangEn] = "Compatibility mode removed. The next launch shouldn't ask for administrator.",
            [LangEs] = "Modo de compatibilidad quitado. El próximo lanzamiento no debería pedir administrador.",
        },
        ["StatusCompatLayerRemoveFailed"] = new()
        {
            [LangEn] = "Couldn't remove the compatibility mode. You can do it from the file's Properties → Compatibility.",
            [LangEs] = "No se pudo quitar el modo de compatibilidad. Lo puedes hacer desde Propiedades → Compatibilidad del archivo.",
        },
        // Declining the UAC prompt is a decision, not a failure — it gets a neutral status
        // line instead of the red framework error dialog it used to produce.
        ["StatusGameLaunchCancelled"] = new()
        {
            [LangEn] = "Launch cancelled — you declined the Windows permission prompt.",
            [LangEs] = "Lanzamiento cancelado: rechazaste el permiso de Windows.",
        },
        // A game that dies seconds after launching failed to start; it used to be
        // indistinguishable from a normal session, so a broken install just looked like
        // "I press play and nothing happens".
        ["StatusGameClosedImmediately"] = new()
        {
            [LangEn] = "The game closed by itself",
            [LangEs] = "El juego se cerró solo",
        },
        // Refusing a destination that is the user's actual AoE3 folder. Every install
        // stamps clonedAoe3:true, which is what Uninstall reads to decide between removing
        // the mod's files and deleting the whole folder — so this would arm an uninstall
        // that wipes their game.
        ["DlgInstallInsideAoe3Title"] = new()
        {
            [LangEn] = "Choose a different folder",
            [LangEs] = "Elige otra carpeta",
        },
        ["DlgInstallInsideAoe3Body"] = new()
        {
            [LangEn] = "That folder is your Age of Empires III installation:\n\n{0}\n\n"
                     + "A mod can't be installed on top of the game itself — uninstalling it later "
                     + "would delete your game with it. Pick a separate folder, for example one "
                     + "next to it named after the mod.",
            [LangEs] = "Esa carpeta es tu instalación de Age of Empires III:\n\n{0}\n\n"
                     + "Un mod no se puede instalar encima del juego mismo: al desinstalarlo después "
                     + "se llevaría tu juego por delante. Elige una carpeta aparte, por ejemplo una "
                     + "al lado con el nombre del mod.",
        },
        ["ToastGameClosedImmediatelyBody"] = new()
        {
            [LangEn] = "It opened and shut down after a few seconds, so it didn't start properly. "
                     + "That usually means files are missing: try \"Verify files\" or \"Repair\" "
                     + "from the gear menu.",
            [LangEs] = "Se abrió y se cerró a los pocos segundos, así que no llegó a arrancar bien. "
                     + "Suele ser por archivos que faltan: prueba \"Verificar archivos\" o \"Reparar\" "
                     + "desde el menú del engranaje.",
        },
        // Careful: "closing the window keeps it running" belongs to the SEPARATE
        // close-to-tray checkbox (LauncherConfig.CloseToTray), not to this toggle.
        // This one is auto-start only; don't merge the two descriptions again.
        ["DlgLauncherSettingsStartWithWindowsTip"] = new()
        {
            [LangEn] = "Windows opens the launcher at login, straight to the system tray — no window pops up, "
                     + "and other players see you online without you doing anything. Right-click the tray icon "
                     + "→ Exit to fully quit. You can turn this off anytime.",
            [LangEs] = "Windows abre el launcher al iniciar sesión, directo a la bandeja del sistema: no aparece "
                     + "ninguna ventana, y los demás jugadores te ven conectado sin que hagas nada. Clic derecho "
                     + "en el ícono de la bandeja → Salir para cerrarlo del todo. Lo puedes apagar cuando quieras.",
        },
        ["DlgLauncherSettingsJoinLinks"] = new()
        {
            [LangEn] = "Discord \"Join\" links",
            [LangEs] = "Enlaces «Unirse» de Discord",
        },
        ["DlgLauncherSettingsJoinLinksHint"] = new()
        {
            [LangEn] = "Click one on Discord and the launcher opens that room directly.",
            [LangEs] = "Al hacer clic en Discord, el launcher abre esa sala directamente.",
        },
        ["DlgLauncherSettingsJoinLinksTip"] = new()
        {
            [LangEn] = "Registers a wol-launcher:// handler for your user only (no admin needed). "
                     + "Turn off if you'd rather leave no entry in the Windows registry.",
            [LangEs] = "Registra un handler wol-launcher:// solo para tu usuario (sin admin). "
                     + "Desactívalo si prefieres no dejar ninguna entrada en el registro de Windows.",
        },
        ["DlgLauncherSettingsCloseOnGame"] = new()
        {
            [LangEn] = "Close the launcher when the game starts",
            [LangEs] = "Cerrar el launcher al empezar la partida",
        },
        ["DlgLauncherSettingsCloseOnGameHint"] = new()
        {
            [LangEn] = "Frees up resources while you play.",
            [LangEs] = "Libera recursos mientras juegas.",
        },
        ["DlgLauncherSettingsCloseOnGameTip"] = new()
        {
            [LangEn] = "The launcher closes itself once the game opens. You reopen it by hand after "
                     + "the game closes. Leave off if you want the launcher (and multiplayer chat) to stay open.",
            [LangEs] = "El launcher se cierra solo cuando abre el juego. Lo vuelves a abrir a mano cuando el "
                     + "juego cierra. Déjalo apagado si quieres que el launcher (y el chat multijugador) siga abierto.",
        },
        ["DlgLauncherSettingsMinimizeToTray"] = new()
        {
            [LangEn] = "Minimize to system tray on close",
            [LangEs] = "Minimizar a la bandeja al cerrar",
        },
        ["DlgLauncherSettingsMinimizeToTrayHint"] = new()
        {
            [LangEn] = "Right-click the tray icon → Exit to close it completely.",
            [LangEs] = "Clic derecho en el icono de la bandeja → Salir para cerrarlo del todo.",
        },
        ["DlgLauncherSettingsMinimizeToTrayTip"] = new()
        {
            [LangEn] = "On by default: the X sends the launcher to the system tray so it keeps running "
                     + "(and you stay shown as connected). Double-click the tray icon to reopen it, or "
                     + "right-click → Exit to fully quit. The minimize button is unaffected. Turn this off "
                     + "to make the X quit the launcher like before.",
            [LangEs] = "Activado por defecto: la X manda el launcher a la bandeja del sistema para que siga "
                     + "corriendo (y aparezcas conectado). Doble clic en el ícono para volver a abrirlo, o "
                     + "clic derecho → Salir para cerrarlo del todo. El botón minimizar no cambia. Desactívalo "
                     + "para que la X cierre el launcher como antes.",
        },
        ["DlgLauncherSettingsShowToasts"] = new()
        {
            [LangEn] = "Tell me when an update finishes",
            [LangEs] = "Avisarme cuando termine una actualización",
        },
        ["DlgLauncherSettingsShowToastsTip"] = new()
        {
            [LangEn] = "A small system-tray notification appears when an update finishes while the "
                     + "launcher window is hidden or minimised — so you can step away and come back when it's ready.",
            [LangEs] = "Aparece una notificación chica en la bandeja cuando termina una actualización y la "
                     + "ventana está oculta o minimizada — así puedes hacer otra cosa y volver cuando esté lista.",
        },
        ["DlgSettingsNotifyRooms"] = new()
        {
            [LangEn] = "Tell me when someone opens a room",
            [LangEs] = "Avisarme cuando alguien abre una sala",
        },
        ["DlgSettingsNotifyRoomsHint"] = new()
        {
            [LangEn] = "Only for mods you have installed.",
            [LangEs] = "Solo para mods que tengas instalados.",
        },
        ["DlgSettingsNotifyRoomsTip"] = new()
        {
            [LangEn] = "A Windows notification when any player creates a multiplayer room for a mod you "
                     + "have installed. Only shows while the launcher window isn't in focus.",
            [LangEs] = "Una notificación de Windows cuando cualquier jugador crea una sala multijugador de un "
                     + "mod que tienes instalado. Solo aparece cuando la ventana del launcher no está en foco.",
        },
        ["DlgSettingsSounds"] = new()
        {
            [LangEn] = "Sounds",
            [LangEs] = "Sonidos",
        },
        ["DlgSettingsSoundsHint"] = new()
        {
            [LangEn] = "Chat, notifications and connections.",
            [LangEs] = "Chat, notificaciones y conexiones.",
        },
        ["DlgSettingsSoundsTip"] = new()
        {
            [LangEn] = "Discord-style audio cues: a blip for chat, a ding for notifications, and a pop when a "
                     + "player joins your room or a new room appears. Never plays for your own message. Turn "
                     + "this off for a fully silent launcher.",
            [LangEs] = "Señales de audio estilo Discord: un blip para el chat, un ding para las notificaciones, y "
                     + "un pop cuando un jugador entra a tu sala o aparece una sala nueva. Nunca suena por tu propio "
                     + "mensaje. Desactívalo para un launcher totalmente silencioso.",
        },
        ["DlgSettingsReceiveInvites"] = new()
        {
            [LangEn] = "Let players invite me to their rooms",
            [LangEs] = "Dejar que me inviten a sus salas",
        },
        ["DlgSettingsReceiveInvitesHint"] = new()
        {
            [LangEn] = "Repeated invites throttle themselves.",
            [LangEs] = "Las invitaciones repetidas se limitan solas.",
        },
        ["DlgSettingsReceiveInvitesTip"] = new()
        {
            [LangEn] = "An in-app toast (with a Join button) when another player invites you to their "
                     + "multiplayer room. Repeated invites from the same player are silenced for ~60s, and "
                     + "each toast has a Mute button to stop that player for the session. Turn this off to "
                     + "refuse all invites.",
            [LangEs] = "Un aviso in-app (con botón Unirse) cuando otro jugador te invita a su sala "
                     + "multijugador. Las invitaciones repetidas del mismo jugador se silencian ~60s, y cada "
                     + "aviso tiene un botón Silenciar para frenar a ese jugador durante la sesión. Desactívalo "
                     + "para rechazar todas las invitaciones.",
        },
        ["ModPropSettingsTitle"] = new()
        {
            [LangEn] = "Game settings",
            [LangEs] = "Ajustes del juego",
        },
        ["ModPropSettingsDesc"] = new()
        {
            [LangEn] = "Copy the graphics, sound volumes and hotkeys from another mod into this one. "
                     + "Your saved games, home cities and profile are not touched.",
            [LangEs] = "Copia los gráficos, los volúmenes y los atajos de otro mod a este. Tus partidas "
                     + "guardadas, tus metrópolis y tu perfil no se tocan.",
        },
        ["ModPropSettingsNoSources"] = new()
        {
            [LangEn] = "There is no other mod installed to copy settings from yet.",
            [LangEs] = "Todavía no hay otro mod instalado del que copiar los ajustes.",
        },
        ["ModPropSettingsImportBtn"] = new()
        {
            [LangEn] = "Import",
            [LangEs] = "Importar",
        },
        ["ModPropSettingsImported"] = new()
        {
            [LangEn] = "✔ Graphics, sound and hotkeys copied from {0}.",
            [LangEs] = "✔ Gráficos, sonido y atajos copiados de {0}.",
        },
        // The two causes that used to hide behind "couldn't be read". The first is not an
        // error at all: the game writes its profile on the first run, so a mod nobody has
        // opened yet has nothing to copy INTO — and telling that player something failed sent
        // them looking for a problem that does not exist.
        ["ModPropSettingsNeverOpened"] = new()
        {
            [LangEn] = "This mod has never been opened, so it has no settings file yet. "
                     + "Open it once and try again.",
            [LangEs] = "Este mod nunca se ha abierto, así que todavía no tiene archivo de "
                     + "ajustes. Ábrelo una vez y vuelve a intentarlo.",
        },
        ["ModPropSettingsNoSourceSettings"] = new()
        {
            [LangEn] = "{0} has no graphics, sound or hotkeys to copy. This mod is unchanged.",
            [LangEs] = "{0} no tiene gráficos, sonido ni atajos que copiar. Este mod queda igual.",
        },
        ["ModPropSettingsImportFailed"] = new()
        {
            [LangEn] = "Nothing was copied — the settings couldn't be read. This mod is unchanged.",
            [LangEs] = "No se copió nada — no se pudieron leer los ajustes. Este mod queda igual.",
        },
        ["ModPropSettingsShare"] = new()
        {
            [LangEn] = "Keep the same settings in every mod",
            [LangEs] = "Mantener los mismos ajustes en todos los mods",
        },
        ["ModPropSettingsShareHint"] = new()
        {
            [LangEn] = "Every mod with this ticked keeps the same graphics, sound and hotkeys: change "
                     + "them while playing any of them and the rest pick them up. A mod without it is "
                     + "left alone. The first time this writes, the mod's original settings are saved "
                     + "beside their file.",
            [LangEs] = "Todos los mods con esto marcado mantienen los mismos gráficos, sonido y atajos: "
                     + "los cambias jugando a cualquiera de ellos y el resto los adopta. Un mod sin "
                     + "marcar no se toca. La primera vez que se escribe, los ajustes originales del mod "
                     + "se guardan junto a su archivo.",
        },
        ["DlgLauncherSettingsSectionPrivacy"] = new()
        {
            [LangEn] = "PRIVACY",
            [LangEs] = "PRIVACIDAD",
        },
        ["DlgLauncherSettingsPrivacyHeader"] = new()
        {
            [LangEn] = "Privacy & data",
            [LangEs] = "Privacidad y datos",
        },
        ["DlgLauncherSettingsPrivacyDescription"] = new()
        {
            [LangEn] = "The launcher collects no analytics by default. Multiplayer (Discord sign-in, lobbies, chat) sends only what those features need to the lobby server. See the privacy policy for the full detail.",
            [LangEs] = "El launcher no recopila analíticas por defecto. El multijugador (inicio de sesión con Discord, salas, chat) solo envía al servidor lo que esas funciones necesitan. Consulta la política de privacidad para el detalle completo.",
        },
        ["DlgLauncherSettingsTelemetry"] = new()
        {
            [LangEn] = "Enable local telemetry log (off by default)",
            [LangEs] = "Habilitar registro de telemetría local (desactivado por defecto)",
        },
        ["DlgLauncherSettingsTelemetryHint"] = new()
        {
            [LangEn] = "A file on your PC that helps diagnose multiplayer problems. It is not sent anywhere.",
            [LangEs] = "Un archivo en tu PC que ayuda a diagnosticar problemas de multijugador. No se envía a ningún sitio.",
        },
        ["DlgLauncherSettingsTelemetryTip"] = new()
        {
            [LangEn] = "Writes a local multiplayer-events.log with plain counters (sign-ins, lobby joins, error "
                     + "codes). No network, no third parties — it never leaves your PC. Only useful if you attach "
                     + "it to a bug report.",
            [LangEs] = "Escribe un multiplayer-events.log local con contadores simples (inicios de sesión, entradas "
                     + "a salas, códigos de error). Sin red ni terceros: nunca sale de tu PC. Solo sirve si lo "
                     + "adjuntas a un reporte de error.",
        },
        ["DlgLauncherSettingsViewPrivacy"] = new()
        {
            [LangEn] = "View privacy policy",
            [LangEs] = "Ver política de privacidad",
        },
        ["DlgLauncherSettingsPrivacyHint"] = new()
        {
            [LangEn] = "See exactly what data the launcher uses.",
            [LangEs] = "Mira exactamente qué datos usa el launcher.",
        },
        ["DlgLauncherSettingsPrivacyTip"] = new()
        {
            [LangEn] = "Opens the privacy policy (PRIVACY.md on GitHub) in your browser.",
            [LangEs] = "Abre la política de privacidad (PRIVACY.md en GitHub) en tu navegador.",
        },
        ["TrayTooltip"] = new()
        {
            [LangEn] = "AoE3 Mod Launcher",
            [LangEs] = "AoE3 Mod Launcher",
        },
        ["TrayMenuShow"] = new()
        {
            [LangEn] = "Show launcher",
            [LangEs] = "Mostrar launcher",
        },
        ["TrayMenuExit"] = new()
        {
            [LangEn] = "Exit",
            [LangEs] = "Salir",
        },
        ["TrayClosedHintTitle"] = new()
        {
            [LangEn] = "Still running in the tray",
            [LangEs] = "Sigue activo en la bandeja",
        },
        ["TrayClosedHintBody"] = new()
        {
            [LangEn] = "The launcher keeps running here so you stay online. Right-click this icon → Exit to fully quit, or turn this off in Settings.",
            [LangEs] = "El launcher sigue corriendo aquí para que sigas en línea. Clic derecho en este ícono → Salir para cerrarlo del todo, o desactívalo en Configuración.",
        },
        // Shown once, when the ON-by-default "run in background" preference is first
        // applied. This balloon is the whole reason a default-on auto-start is
        // defensible: it is announced and it says where to undo it. Don't drop it.
        // The one-time tray balloon that used to announce the auto-start registration lived
        // here. It is gone with the write it announced: the launcher now ASKS before touching
        // the Run key (DlgBackgroundConsent*), and a question beforehand is strictly stronger
        // than a notice afterwards. Don't reinstate the balloon without reinstating the
        // silent write it was apologising for.

        // --- Game recording (how a match result is known at all) ---
        ["DlgSettingsGameRecording"] = new()
        {
            [LangEn] = "Let the launcher switch recording on",
            [LangEs] = "Dejar que el launcher active la grabación",
        },
        ["DlgSettingsGameRecordingHint"] = new()
        {
            [LangEn] = "It switches it on once per mod; from then on the setting is yours.",
            [LangEs] = "La activa una vez por mod; a partir de ahí el ajuste es tuyo.",
        },
        ["DlgSettingsGameRecordingTip"] = new()
        {
            [LangEn] = "Writes optionrecordgame in Documents\\My Games\\<mod>\\Users3\\<profile>.xml, keeping a one-time backup. Changes apply the next time you launch each mod. Old automatic recordings (\"Record Game 7\") are cleaned up, keeping the 10 most recent — anything you renamed is kept forever. Off = the launcher writes false once per mod and then leaves your profile alone.",
            [LangEs] = "Escribe optionrecordgame en Documentos\\My Games\\<mod>\\Users3\\<perfil>.xml, con una copia de seguridad única. Los cambios se aplican la próxima vez que inicies cada mod. Las grabaciones automáticas viejas (\"Record Game 7\") se limpian y se conservan las 10 más recientes; lo que hayas renombrado se conserva siempre. Apagado = el launcher escribe false una vez por mod y después no vuelve a tocar tu perfil.",
        },
        ["TrayGameRecordingTitle"] = new()
        {
            [LangEn] = "Game recording is on",
            [LangEs] = "La grabación de partidas está activada",
        },
        ["TrayGameRecordingBody"] = new()
        {
            [LangEn] = "Age of Empires III doesn't record by default, so the launcher turned it on — that's the only way it can tell who won a match. You can turn it off in Settings → General.",
            [LangEs] = "Age of Empires III no graba por defecto, así que el launcher lo activó: es la única forma de saber quién ganó una partida. Lo puedes apagar en Configuración → General.",
        },
        ["MpChatRecordReminder"] = new()
        {
            // Conditional on purpose. It fires on EVERY host launch — the launcher cannot read
            // AoE3's per-match box — so the old flat "this match won't count" appeared over
            // matches that recorded and rated perfectly well, and a warning that is wrong half
            // the time teaches players to ignore the half that isn't.
            [LangEn] = "⚠ If you haven't ticked \"Record Game\" on AoE3's setup screen, this match won't be able to count.",
            [LangEs] = "⚠ Si no marcaste \"Record Game\" en la pantalla de configuración de AoE3, esta partida no va a poder contar.",
        },
        ["MpRecordBandTitle"] = new()
        {
            [LangEn] = "So the match counts",
            [LangEs] = "Para que la partida cuente",
        },
        ["MpRecordBandHost"] = new()
        {
            [LangEn] = "Tick \"Record Game\" on Age of Empires III's own setup screen before you start. Without the recording there is no way to tell who won, and the match counts for nobody's rating.",
            [LangEs] = "Marca \"Record Game\" en la pantalla de configuración de Age of Empires III antes de empezar. Sin la grabación no hay forma de saber quién ganó, y la partida no cuenta para la puntuación de nadie.",
        },
        ["MpRecordBandGuest"] = new()
        {
            [LangEn] = "Remind the host to tick \"Record Game\" in Age of Empires III. Only their recording is read, so without it this match counts for nobody's rating — yours included.",
            [LangEs] = "Recuérdale al anfitrión que marque \"Record Game\" en Age of Empires III. Solo se lee su grabación, así que sin ella la partida no cuenta para la puntuación de nadie, incluida la tuya.",
        },
        ["MpRecordBandDismiss"] = new()
        {
            [LangEn] = "Don't show this again",
            [LangEs] = "No mostrar más",
        },
        ["DlgSettingsRecordReminder"] = new()
        {
            [LangEn] = "Remind me to tick \"Record Game\" before each match",
            [LangEs] = "Recordarme marcar «Record Game» antes de cada partida",
        },
        ["DlgSettingsRecordReminderHint"] = new()
        {
            [LangEn] = "AoE3's own checkbox is separate and has to be ticked by hand every time. One line in the chat when you host.",
            [LangEs] = "La casilla de la pantalla de configuración de AoE3 es independiente y hay que marcarla a mano cada vez. Una línea en el chat cuando eres anfitrión.",
        },
        ["DlgSettingsRecordReminderTip"] = new()
        {
            [LangEn] = "Only the host is reminded, because only the host's recording is read to work out who won. Turn this off once you always remember — the launcher will never decide on its own that you no longer need it, since a match that recorded doesn't prove the next one will.",
            [LangEs] = "Solo se avisa al anfitrión, porque solo se lee su grabación para saber quién ganó. Apágalo cuando ya te acuerdes siempre: el launcher nunca decide por su cuenta que ya no lo necesitas, porque una partida que se grabó no prueba que la siguiente lo haga.",
        },
        ["MpNoRecordingTitle"] = new()
        {
            [LangEn] = "The match was saved without a result",
            [LangEs] = "La partida se guardó sin resultado",
        },
        ["MpNoRecordingCheckbox"] = new()
        {
            [LangEn] = "No recording of this match was found. Check the \"Record Game\" box on AoE3's setup screen before starting the next one.",
            [LangEs] = "No se encontró la grabación de esta partida. Marca la casilla \"Record Game\" en la pantalla de configuración de AoE3 antes de empezar la próxima.",
        },
        ["MpNoRecordingProfileOff"] = new()
        {
            [LangEn] = "No recording of this match was found: Age of Empires III turned recording off again. Turn it back on in the game's options.",
            [LangEs] = "No se encontró la grabación de esta partida: Age of Empires III volvió a desactivar la grabación. Actívala de nuevo en las opciones del juego.",
        },
        ["MpNoRecordingUnknown"] = new()
        {
            [LangEn] = "No recording of this match was found, so it was saved without a result.",
            [LangEs] = "No se encontró la grabación de esta partida, así que se guardó sin resultado.",
        },

        // --- Match report: titles for the toast that replaces the chat line when a successful
        // report has already closed the room and taken the lobby window with it. ---
        ["MpMatchReportedTitle"] = new()
        {
            [LangEn] = "Match saved",
            [LangEs] = "Partida guardada",
        },
        ["MpMatchNotReportedTitle"] = new()
        {
            [LangEn] = "The match could not be saved",
            [LangEs] = "No se pudo guardar la partida",
        },

        // --- Reopening the game while the room is still playing ---
        ["MpRoomRejoinGame"] = new()
        {
            [LangEn] = "Open the game",
            [LangEs] = "Abrir el juego",
        },
        ["MpRoomRejoinTooltip"] = new()
        {
            [LangEn] = "Opens Age of Empires III again without leaving the room. The others keep playing — nothing is interrupted.",
            [LangEs] = "Vuelve a abrir Age of Empires III sin salir de la sala. Los demás siguen jugando: no se interrumpe nada.",
        },
        ["MpRoomReopenGame"] = new()
        {
            [LangEn] = "Reopen the game",
            [LangEs] = "Volver a abrir el juego",
        },
        ["MpChatRejoiningGame"] = new()
        {
            [LangEn] = "Opening the game again…",
            [LangEs] = "Abriendo el juego de nuevo…",
        },
        ["MpChatRoomStillPlaying"] = new()
        {
            [LangEn] = "The room is still in a match. Press \"Open the game\" to get back in — if you leave the room you won't be able to return until the match is over.",
            [LangEs] = "La sala sigue en partida. Pulsa \"Abrir el juego\" para volver a entrar: si sales de la sala no vas a poder regresar hasta que la partida termine.",
        },

        // --- Leaving a room while a match is running ---
        ["MpLeaveDuringMatchTitle"] = new()
        {
            [LangEn] = "There's a match in progress",
            [LangEs] = "Hay una partida en curso",
        },
        ["MpLeaveDuringMatchHost"] = new()
        {
            [LangEn] = "You are the host. Leaving now closes Age of Empires III for every player and the match goes down with no winner. Leave anyway?",
            [LangEs] = "Eres el anfitrión. Si sales ahora se cierra el Age of Empires III de todos los jugadores y la partida queda registrada sin ganador. ¿Salir de todos modos?",
        },
        ["MpLeaveDuringMatchGuest"] = new()
        {
            [LangEn] = "Your Age of Empires III will be closed and you will leave the room. The others keep playing. Leave anyway?",
            [LangEs] = "Se va a cerrar tu Age of Empires III y vas a salir de la sala. Los demás siguen jugando. ¿Salir de todos modos?",
        },
        ["MpLeaveDuringMatchCannotRejoin"] = new()
        {
            [LangEn] = "The room is still in a match, so you will not be able to come back until it ends. If you closed the game by mistake, use \"Open the game\" instead. Leave anyway?",
            [LangEs] = "La sala sigue en partida, así que no vas a poder volver hasta que termine. Si cerraste el juego sin querer, usa \"Abrir el juego\" en vez de salir. ¿Salir de todos modos?",
        },
        ["MpLeaveDuringMatchYes"] = new()
        {
            [LangEn] = "Leave",
            [LangEs] = "Salir",
        },
        ["MpLeaveDuringMatchNo"] = new()
        {
            [LangEn] = "Stay",
            [LangEs] = "Quedarme",
        },

        // --- Notification bell (Steam-style) ---
        ["NotifBellTooltip"] = new()
        {
            [LangEn] = "Notifications",
            [LangEs] = "Notificaciones",
        },
        ["NotifPanelTitle"] = new()
        {
            [LangEn] = "Notifications",
            [LangEs] = "Notificaciones",
        },
        ["NotifMarkAllRead"] = new()
        {
            [LangEn] = "Mark all read",
            [LangEs] = "Marcar todo leído",
        },
        ["NotifClearAll"] = new()
        {
            [LangEn] = "Clear",
            [LangEs] = "Borrar",
        },
        ["NotifEmpty"] = new()
        {
            [LangEn] = "No notifications",
            [LangEs] = "Sin notificaciones",
        },
        ["NotifUpdateAvailableTitle"] = new()
        {
            [LangEn] = "Update available",
            [LangEs] = "Actualización disponible",
        },
        ["NotifUpdateAvailableBody"] = new()
        {
            [LangEn] = "{0} {1} is available to download.",
            [LangEs] = "{0} {1} está disponible para descargar.",
        },
        ["NotifUpdateFinishedTitle"] = new()
        {
            [LangEn] = "Update complete",
            [LangEs] = "Actualización completada",
        },
        ["NotifUpdateFinishedBody"] = new()
        {
            [LangEn] = "{0} was updated to {1}.",
            [LangEs] = "{0} se actualizó a {1}.",
        },
        // Version picker. Deliberately NOT worded as an update: picking a version can be
        // a downgrade, and "updated to 19.07" after going back from 24.07 would be wrong.
        // {0} = mod name, {1} = version.
        ["NotifVersionInstalledTitle"] = new()
        {
            [LangEn] = "Version installed",
            [LangEs] = "Versión instalada",
        },
        ["NotifVersionInstalledBody"] = new()
        {
            [LangEn] = "{0} is now on {1}.",
            [LangEs] = "{0} quedó en la versión {1}.",
        },
        // Repair. Only raised when files were actually re-laid — a repair that found
        // nothing damaged says so in the status line and leaves no bell entry.
        // {0} = mod name.
        ["NotifRepairFinishedTitle"] = new()
        {
            [LangEn] = "Repair complete",
            [LangEs] = "Reparación completada",
        },
        ["NotifRepairFinishedBody"] = new()
        {
            [LangEn] = "{0}'s files were restored.",
            [LangEs] = "Se restauraron los archivos de {0}.",
        },
        ["MpNotifRoomCreatedTitle"] = new()
        {
            [LangEn] = "New room",
            [LangEs] = "Nueva sala",
        },
        ["MpNotifRoomCreatedBody"] = new()
        {
            [LangEn] = "'{0}' · {1}",
            [LangEs] = "'{0}' · {1}",
        },
        // ---- In-app toasts: room invites + new-room push (/global/ws) ----
        ["MpInviteMenuItem"] = new()
        {
            [LangEn] = "Invite to my room",
            [LangEs] = "Invitar a mi sala",
        },
        ["MpInviteTooltip"] = new()
        {
            [LangEn] = "Invite to your room",
            [LangEs] = "Invitar a tu sala",
        },
        ["MpInviteTooltipDisabled"] = new()
        {
            [LangEn] = "Join or create a room to invite someone",
            [LangEs] = "Entra o crea una sala para invitar",
        },
        ["MpInviteToastTitle"] = new()
        {
            [LangEn] = "{0} invited you to their room",
            [LangEs] = "{0} te invitó a su sala",
        },
        ["MpInviteSent"] = new()
        {
            [LangEn] = "Invite sent to {0}",
            [LangEs] = "Invitación enviada a {0}",
        },
        ["MpInviteErrOffline"] = new()
        {
            [LangEn] = "That player is offline",
            [LangEs] = "Ese jugador está desconectado",
        },
        ["MpInviteErrRate"] = new()
        {
            [LangEn] = "Too many invites — slow down",
            [LangEs] = "Demasiadas invitaciones — espera un momento",
        },
        ["MpInviteErrNotInRoom"] = new()
        {
            [LangEn] = "You're not in a room",
            [LangEs] = "No estás en una sala",
        },
        ["MpInviteErrGeneric"] = new()
        {
            [LangEn] = "Couldn't send the invite",
            [LangEs] = "No se pudo enviar la invitación",
        },
        ["MpToastNewRoomTitle"] = new()
        {
            [LangEn] = "New room",
            [LangEs] = "Nueva sala",
        },
        ["MpToastNewRoomBody"] = new()
        {
            [LangEn] = "{0} · {2} · by {1}",
            [LangEs] = "{0} · {2} · por {1}",
        },
        ["MpToastJoin"] = new()
        {
            [LangEn] = "Join",
            [LangEs] = "Unirse",
        },
        ["MpToastIgnore"] = new()
        {
            [LangEn] = "Ignore",
            [LangEs] = "Ignorar",
        },
        ["MpToastMute"] = new()
        {
            [LangEn] = "Mute",
            [LangEs] = "Silenciar",
        },
        ["MpInviteMutedConfirm"] = new()
        {
            [LangEn] = "You won't get invites from {0} this session",
            [LangEs] = "No recibirás más invitaciones de {0} esta sesión",
        },
        ["NotifInstalledTitle"] = new()
        {
            [LangEn] = "Installation complete",
            [LangEs] = "Instalación completada",
        },
        ["NotifInstalledBody"] = new()
        {
            [LangEn] = "{0} was installed ({1}).",
            [LangEs] = "{0} se instaló ({1}).",
        },
        ["NotifCopyInstalledTitle"] = new()
        {
            [LangEn] = "Copy installed",
            [LangEs] = "Copia instalada",
        },
        ["NotifCopyInstalledBody"] = new()
        {
            [LangEn] = "A new copy of {0} was installed ({1}).",
            [LangEs] = "Se instaló una nueva copia de {0} ({1}).",
        },
        // ---- Bell: launcher self-update ----
        ["NotifLauncherUpdateTitle"] = new()
        {
            [LangEn] = "Launcher update available",
            [LangEs] = "Actualización del launcher",
        },
        ["NotifLauncherUpdateBody"] = new()
        {
            [LangEn] = "Version {0} of the launcher is available. Click to update.",
            [LangEs] = "La versión {0} del launcher está disponible. Haz clic para actualizar.",
        },
        // ---- Bell: connectivity ----
        ["NotifOfflineTitle"] = new()
        {
            [LangEn] = "You're offline",
            [LangEs] = "Sin conexión",
        },
        ["NotifOfflineBody"] = new()
        {
            [LangEn] = "Installed mods stay playable; online features are paused until you reconnect.",
            [LangEs] = "Los mods instalados siguen jugables; las funciones en línea se pausan hasta reconectar.",
        },
        ["NotifOnlineTitle"] = new()
        {
            [LangEn] = "Back online",
            [LangEs] = "Conexión restaurada",
        },
        ["NotifOnlineBody"] = new()
        {
            [LangEn] = "Online features are available again.",
            [LangEs] = "Las funciones en línea vuelven a estar disponibles.",
        },
        // ---- Bell: new mod in the catalog ----
        // A patch for a mod the player does NOT have installed. Worded so it cannot be
        // mistaken for the update item: nothing here is waiting to be applied.
        ["NotifModPatchTitle"] = new()
        {
            [LangEn] = "New patch published",
            [LangEs] = "Nuevo parche publicado",
        },
        ["NotifModPatchBody"] = new()
        {
            [LangEn] = "{0} released {1}. You don't have this mod installed.",
            [LangEs] = "{0} ha publicado la {1}. No tienes este mod instalado.",
        },
        ["NotifNewModTitle"] = new()
        {
            [LangEn] = "New mod available",
            [LangEs] = "Nuevo mod disponible",
        },
        ["NotifNewModBody"] = new()
        {
            [LangEn] = "{0} was just added to the Workshop.",
            [LangEs] = "{0} se acaba de añadir al Workshop.",
        },
        ["NotifNewTranslationTitle"] = new()
        {
            [LangEn] = "New translation",
            [LangEs] = "Nueva traducción",
        },
        ["NotifNewTranslationBody"] = new()
        {
            [LangEn] = "A new translation is available for {0}: {1}.",
            [LangEs] = "Hay una nueva traducción para {0}: {1}.",
        },
        ["DlgLauncherSettingsAutoCheck"] = new()
        {
            [LangEn] = "Check for updates on startup",
            [LangEs] = "Buscar actualizaciones al iniciar",
        },
        ["DlgLauncherSettingsAutoCheckHint"] = new()
        {
            [LangEn] = "Keeps your mods current so you can play online with everyone.",
            [LangEs] = "Mantiene tus mods al día para poder jugar online con todos.",
        },
        ["DlgLauncherSettingsAutoCheckTip"] = new()
        {
            [LangEn] = "Runs quietly in the background at launch and only shows a notice when there's an "
                     + "update to install. Turn off on a metered connection to keep the launcher silent.",
            [LangEs] = "Corre en silencio al arrancar y solo te avisa cuando hay una actualización para "
                     + "instalar. Apágalo en una conexión con límite de datos para que el launcher no use red.",
        },
        ["DlgLauncherSettingsOpenPostUpdate"] = new()
        {
            [LangEn] = "Open changelog page after updating",
            [LangEs] = "Abrir la página de novedades tras actualizar",
        },
        ["DlgLauncherSettingsOpenPostUpdateHint"] = new()
        {
            [LangEn] = "See what changed after a patch is applied.",
            [LangEs] = "Mira qué cambió después de aplicar un parche.",
        },
        ["DlgLauncherSettingsOpenPostUpdateTip"] = new()
        {
            [LangEn] = "Some mods link to a changelog / news page in your browser right after a patch is applied.",
            [LangEs] = "Algunos mods enlazan a una página de cambios / novedades en tu navegador justo después de aplicar un parche.",
        },
        ["DlgLauncherSettingsCatalogDefault"] = new()
        {
            [LangEn] = "Default catalog",
            [LangEs] = "Catálogo por defecto",
        },
        ["DlgLauncherSettingsCatalogCustom"] = new()
        {
            [LangEn] = "Custom repository:",
            [LangEs] = "Repositorio personalizado:",
        },
        ["DlgLauncherSettingsCatalogDisabled"] = new()
        {
            [LangEn] = "Disabled (built-in mods only)",
            [LangEs] = "Desactivado (solo mods integrados)",
        },
        ["DlgLauncherSettingsClearCache"] = new()
        {
            [LangEn] = "Clear catalog cache",
            [LangEs] = "Limpiar caché del catálogo",
        },
        ["DlgLauncherSettingsClearCacheHint"] = new()
        {
            [LangEn] = "Only if the mod list looks out of date.",
            [LangEs] = "Solo si la lista de mods se ve desactualizada.",
        },
        ["DlgLauncherSettingsClearCacheTip"] = new()
        {
            [LangEn] = "Forces the launcher to download a fresh mod catalog next time it starts.",
            [LangEs] = "Fuerza al launcher a descargar un catálogo de mods nuevo la próxima vez que arranca.",
        },
        ["DlgLauncherSettingsCacheCleared"] = new()
        {
            [LangEn] = "Cache cleared.",
            [LangEs] = "Caché eliminada.",
        },
        ["DlgLauncherSettingsTxDefaultLabel"] = new()
        {
            [LangEn] = "Default repository (always active): {0}",
            [LangEs] = "Repositorio por defecto (siempre activo): {0}",
        },
        ["DlgLauncherSettingsTxAddHeader"] = new()
        {
            [LangEn] = "Additional repositories (all are merged):",
            [LangEs] = "Repositorios adicionales (todos se combinan):",
        },
        ["DlgLauncherSettingsTxAddButton"] = new()
        {
            [LangEn] = "Add",
            [LangEs] = "Agregar",
        },
        ["DlgLauncherSettingsTxRemoveTooltip"] = new()
        {
            [LangEn] = "Remove this repository",
            [LangEs] = "Quitar este repositorio",
        },
        ["DlgLauncherSettingsTxNoneYet"] = new()
        {
            [LangEn] = "No extra repositories added yet.",
            [LangEs] = "Aún no agregaste repositorios adicionales.",
        },
        ["DlgLauncherSettingsTxDuplicate"] = new()
        {
            [LangEn] = "That repository is already in the list.",
            [LangEs] = "Ese repositorio ya está en la lista.",
        },
        ["DlgLauncherSettingsTxDisableToggle"] = new()
        {
            [LangEn] = "Disable all community translations",
            [LangEs] = "Desactivar todas las traducciones de la comunidad",
        },
        ["DlgLauncherSettingsClearTxCache"] = new()
        {
            [LangEn] = "Clear translations cache",
            [LangEs] = "Limpiar caché de traducciones",
        },
        ["DlgLauncherSettingsClearTxCacheHint"] = new()
        {
            [LangEn] = "Reloads the translation list from the repository now.",
            [LangEs] = "Recarga la lista de traducciones desde el repositorio ahora.",
        },
        // Named for the tool it holds (the translation PACKAGER), not
        // "Translations" — the generic word made users think this is where the
        // launcher's display language is changed (that's GENERAL → "Launcher
        // language"). The content header still reads "Translator tools".
        ["DlgLauncherSettingsSectionTranslations"] = new()
        {
            [LangEn] = "PACKAGER",
            [LangEs] = "EMPAQUETADOR",
        },
        ["DlgLauncherSettingsTranslationsHeader"] = new()
        {
            [LangEn] = "Translator tools",
            [LangEs] = "Herramientas para traductores",
        },
        ["DlgLauncherSettingsTranslationsDescription"] = new()
        {
            [LangEn] = "Build a ready-to-publish translation pack from a folder of translated " +
                       "XML files. Works for any installed mod — pick the target mod in the " +
                       "packaging dialog. The launcher computes file hashes, writes the manifest " +
                       "and zips everything for upload to a GitHub release.",
            [LangEs] = "Crea un paquete de traducción listo para publicar a partir de una carpeta " +
                       "con archivos XML traducidos. Funciona para cualquier mod instalado — " +
                       "elige el mod de destino dentro del diálogo. El launcher calcula los " +
                       "hashes, escribe el manifiesto y empaqueta todo en un .zip listo para " +
                       "subir a una release de GitHub.",
        },
        ["DlgLauncherSettingsOpenPackager"] = new()
        {
            [LangEn] = "Open translation packager",
            [LangEs] = "Abrir empaquetador de traducciones",
        },
        ["DlgLauncherSettingsTranslationsHint"] = new()
        {
            [LangEn] = "Casual users can ignore this tab — it's only useful when authoring a new translation.",
            [LangEs] = "Los usuarios normales pueden ignorar esta sección — solo es útil al crear una traducción nueva.",
        },

        // --- Patch generator (Settings → Packager section + its dialog) ---
        ["DlgPatchGenSectionHeader"] = new()
        {
            [LangEn] = "Incremental patch generator",
            [LangEs] = "Generador de parches incrementales",
        },
        ["DlgPatchGenSectionDescription"] = new()
        {
            [LangEn] = "For mod authors on GitHub Releases that enabled delta patches: build a small \"only the changed files\" patch from your previous and new overlay zips, to upload alongside the full release.",
            [LangEs] = "Para autores de mods en GitHub Releases con parches delta activados: crea un parche pequeño de \"solo los archivos cambiados\" a partir de tu overlay anterior y el nuevo, para subirlo junto al release completo.",
        },
        ["DlgPatchGenSectionHint"] = new()
        {
            [LangEn] = "Casual users can ignore this — it's only for publishing mod updates.",
            [LangEs] = "Los usuarios normales pueden ignorar esto — es solo para publicar actualizaciones de mods.",
        },
        ["DlgPatchGenOpen"] = new()
        {
            [LangEn] = "Open patch generator",
            [LangEs] = "Abrir generador de parches",
        },
        // Sits under the patch generator in Settings -> Developer. Both keys shipped
        // referenced but never defined, so the button rendered its own key as its label
        // (Strings.Get falls back to the key) - a live cosmetic bug, not a new feature.
        ["DlgSettingsPreviewToasts"] = new()
        {
            [LangEn] = "Preview notification popups",
            [LangEs] = "Ver un ejemplo de los avisos",
        },
        ["DlgSettingsPreviewToastsHint"] = new()
        {
            [LangEn] = "Shows a sample room invitation and a sample new-room card, in the same place and with the same look as the real ones, so you can check them without waiting for another player.",
            [LangEs] = "Muestra una invitación de ejemplo y un aviso de sala nueva, en el mismo sitio y con el mismo aspecto que los de verdad, para que los veas sin esperar a otro jugador.",
        },
        ["DlgPatchGenTitle"] = new()
        {
            [LangEn] = "Generate patch",
            [LangEs] = "Generar parche",
        },
        ["DlgPatchGenHeader"] = new()
        {
            [LangEn] = "Incremental delta patch",
            [LangEs] = "Parche delta incremental",
        },
        ["DlgPatchGenDescription"] = new()
        {
            [LangEn] = "Pick your previous release's overlay .zip and your new overlay .zip. The tool diffs them and writes a small patch-<from>-to-<to>.zip + .json to upload to your new GitHub release. Users on the previous version then download only the changed files.",
            [LangEs] = "Elige el overlay .zip de tu release anterior y tu overlay .zip nuevo. La herramienta los compara y escribe un pequeño patch-<from>-to-<to>.zip + .json para subir a tu nuevo release de GitHub. Los usuarios en la versión anterior descargarán solo los archivos cambiados.",
        },
        ["DlgPatchGenSectionSources"] = new()
        {
            [LangEn] = "SOURCE OVERLAYS",
            [LangEs] = "OVERLAYS DE ORIGEN",
        },
        ["DlgPatchGenOldZip"] = new()
        {
            [LangEn] = "Previous overlay .zip (the OLD version)",
            [LangEs] = "Overlay .zip anterior (la versión VIEJA)",
        },
        ["DlgPatchGenOldZipHint"] = new()
        {
            [LangEn] = "The full overlay zip you shipped on the previous release.",
            [LangEs] = "El overlay completo que subiste en el release anterior.",
        },
        ["DlgPatchGenNewZip"] = new()
        {
            [LangEn] = "New overlay .zip (the NEW version)",
            [LangEs] = "Overlay .zip nuevo (la versión NUEVA)",
        },
        ["DlgPatchGenNewZipHint"] = new()
        {
            [LangEn] = "The full overlay zip you're about to release — you still upload this one too.",
            [LangEs] = "El overlay completo que vas a publicar — este también lo subes igual.",
        },
        ["DlgPatchGenSectionVersions"] = new()
        {
            [LangEn] = "VERSION TAGS",
            [LangEs] = "TAGS DE VERSIÓN",
        },
        ["DlgPatchGenFromTag"] = new()
        {
            [LangEn] = "From tag (previous release)",
            [LangEs] = "Tag origen (release anterior)",
        },
        ["DlgPatchGenToTag"] = new()
        {
            [LangEn] = "To tag (new release)",
            [LangEs] = "Tag destino (release nuevo)",
        },
        ["DlgPatchGenVersionsHint"] = new()
        {
            [LangEn] = "Must match your real GitHub release tags exactly (e.g. v1.0 and v1.1).",
            [LangEs] = "Deben coincidir exactamente con tus tags reales de GitHub (p. ej. v1.0 y v1.1).",
        },
        ["DlgPatchGenSectionOutput"] = new()
        {
            [LangEn] = "OUTPUT",
            [LangEs] = "SALIDA",
        },
        ["DlgPatchGenOutputFolder"] = new()
        {
            [LangEn] = "Output folder",
            [LangEs] = "Carpeta de salida",
        },
        ["DlgPatchGenBrowse"] = new()
        {
            [LangEn] = "Browse…",
            [LangEs] = "Examinar…",
        },
        ["DlgPatchGenGenerate"] = new()
        {
            [LangEn] = "Generate patch",
            [LangEs] = "Generar parche",
        },
        ["DlgPatchGenWorking"] = new()
        {
            [LangEn] = "Generating…",
            [LangEs] = "Generando…",
        },
        ["DlgPatchGenClose"] = new()
        {
            [LangEn] = "Close",
            [LangEs] = "Cerrar",
        },
        ["DlgPatchGenNeedInputs"] = new()
        {
            [LangEn] = "Pick both overlay zips, fill in both tags, and choose an output folder.",
            [LangEs] = "Elige ambos overlays .zip, completa los dos tags y elige una carpeta de salida.",
        },
        ["DlgPatchGenResult"] = new()
        {
            [LangEn] = "✓ Patch generated: {0} changed/added, {1} deleted, {2}.",
            [LangEs] = "✓ Parche generado: {0} cambiados/añadidos, {1} eliminados, {2}.",
        },
        ["DlgPatchGenReminder"] = new()
        {
            [LangEn] = "Upload BOTH the patch .zip and .json to your new release — and don't forget the full overlay .zip too (needed for fresh installs and version skips).",
            [LangEs] = "Sube TANTO el .zip como el .json del parche a tu nuevo release — y no olvides subir también el overlay .zip completo (necesario para instalaciones nuevas y saltos de versión).",
        },
        ["DlgPatchGenErrorPrefix"] = new()
        {
            [LangEn] = "Couldn't generate the patch:",
            [LangEs] = "No se pudo generar el parche:",
        },
        ["DlgLauncherSettingsSectionMaintenance"] = new()
        {
            [LangEn] = "MAINTENANCE",
            [LangEs] = "MANTENIMIENTO",
        },
        ["DlgLauncherSettingsClearAssets"] = new()
        {
            [LangEn] = "Clear mod icons cache",
            [LangEs] = "Limpiar caché de íconos de mods",
        },
        ["DlgLauncherSettingsClearAssetsHint"] = new()
        {
            [LangEn] = "Only if you see broken or outdated mod images.",
            [LangEs] = "Solo si ves imágenes de mods rotas o antiguas.",
        },
        ["DlgLauncherSettingsClearAssetsTip"] = new()
        {
            [LangEn] = "Deletes the downloaded icon/banner images. They download again by themselves the next "
                     + "time you open the launcher. Safe — it doesn't touch your installed mods.",
            [LangEs] = "Borra las imágenes (íconos/portadas) descargadas. Se vuelven a bajar solas la próxima "
                     + "vez que abres el launcher. Es seguro — no toca tus mods instalados.",
        },
        ["DlgLauncherSettingsClearTemp"] = new()
        {
            [LangEn] = "Clear temporary files",
            [LangEs] = "Limpiar archivos temporales",
        },
        ["DlgLauncherSettingsClearTempHint"] = new()
        {
            [LangEn] = "Interrupted downloads.",
            [LangEs] = "Descargas interrumpidas.",
        },
        ["DlgLauncherSettingsClearTempTip"] = new()
        {
            [LangEn] = "Removes leftover download/extract files from updates that were cancelled or crashed. "
                     + "Safe — it doesn't touch your installed mods.",
            [LangEs] = "Elimina archivos sobrantes de descargas/extracciones que se cancelaron o fallaron. "
                     + "Es seguro — no toca tus mods instalados.",
        },
        // Maintenance: ask GitHub for a newer launcher on demand. The startup check and the
        // offline chip were the only ways in, and the chip is only on screen while offline.
        ["DlgLauncherSettingsCheckUpdate"] = new()
        {
            [LangEn] = "Check for updates",
            [LangEs] = "Buscar actualizaciones",
        },
        ["DlgLauncherSettingsCheckUpdateHint"] = new()
        {
            [LangEn] = "Asks GitHub whether there is a newer one.",
            [LangEs] = "Le pregunta a GitHub si hay una más nueva.",
        },
        ["DlgLauncherSettingsCheckUpdateTip"] = new()
        {
            [LangEn] = "The launcher already checks on startup. Use this when you want to ask "
                     + "again — after downloading a version by hand, for example.",
            [LangEs] = "El launcher ya lo comprueba al arrancar. Usa esto cuando quieras "
                     + "preguntar otra vez, por ejemplo tras descargar una versión a mano.",
        },
        ["DlgLauncherSettingsCheckUpdateBusy"] = new()
        {
            [LangEn] = "Checking…",
            [LangEs] = "Consultando…",
        },
        ["DlgLauncherSettingsCheckUpdateFound"] = new()
        {
            [LangEn] = "There is a new version — the update window just opened.",
            [LangEs] = "Hay una versión nueva: se acaba de abrir la ventana de actualización.",
        },
        ["DlgLauncherSettingsCheckUpdateNone"] = new()
        {
            [LangEn] = "You are on the latest version.",
            [LangEs] = "Estás en la última versión.",
        },
        // Deliberately NOT the same as "up to date": not reaching the server tells you nothing
        // about which version is out there, and saying otherwise is how a broken check looks fine.
        ["DlgLauncherSettingsCheckUpdateFailed"] = new()
        {
            [LangEn] = "Could not reach GitHub. Check your connection and try again.",
            [LangEs] = "No se pudo consultar GitHub. Revisa tu conexión e inténtalo de nuevo.",
        },
        ["DlgLauncherSettingsOpenDataFolder"] = new()
        {
            [LangEn] = "Open data folder",
            [LangEs] = "Abrir carpeta de datos",
        },
        ["DlgLauncherSettingsOpenDataFolderHint"] = new()
        {
            [LangEn] = "Where the launcher keeps its settings and logs.",
            [LangEs] = "Donde el launcher guarda su configuración y registros.",
        },
        ["DlgLauncherSettingsOpenDataFolderTip"] = new()
        {
            [LangEn] = "Opens the launcher's data folder (settings, logs, caches). Handy when sharing logs for a bug report.",
            [LangEs] = "Abre la carpeta de datos del launcher (configuración, registros, cachés). Útil para compartir registros en un reporte de error.",
        },
        ["DlgLauncherSettingsInstall"] = new()
        {
            [LangEn] = "Install on my PC (recommended)",
            [LangEs] = "Instalar en mi PC (recomendado)",
        },
        ["DlgLauncherSettingsInstallHint"] = new()
        {
            [LangEn] = "Leaves shortcuts on the Desktop and the Start menu, and keeps updating itself.",
            [LangEs] = "Deja accesos en el Escritorio y el menú Inicio, y se actualiza solo.",
        },
        ["DlgLauncherSettingsInstallTip"] = new()
        {
            [LangEn] = "Copies the launcher to a fixed folder so you never lose it if you move or delete this "
                     + "file — and so 'Run in background' keeps working. You don't need this to get updates "
                     + "(it already updates itself), but it's the tidy way to keep it.",
            [LangEs] = "Copia el launcher a una carpeta fija para no perderlo si mueves o borras este archivo — y "
                     + "para que 'Ejecutar en segundo plano' siga funcionando. No hace falta para actualizar "
                     + "(ya se actualiza solo), pero es la forma prolija de tenerlo.",
        },
        ["DlgLauncherSettingsInstallDone"] = new()
        {
            [LangEn] = "Installed to {0}",
            [LangEs] = "Instalado en {0}",
        },
        ["DlgLauncherSettingsInstallFailed"] = new()
        {
            [LangEn] = "Install failed: {0}",
            [LangEs] = "Falló la instalación: {0}",
        },
        ["DlgLauncherSettingsInstallRelaunchTitle"] = new()
        {
            [LangEn] = "Restart from the installed copy?",
            [LangEs] = "¿Reiniciar desde la copia instalada?",
        },
        ["DlgLauncherSettingsInstallRelaunchBody"] = new()
        {
            [LangEn] = "The launcher was installed. Restart it now from the installed location? This closes the current window and reopens it from the new path.",
            [LangEs] = "El launcher se instaló. ¿Reiniciarlo ahora desde la ubicación instalada? Esto cierra la ventana actual y la reabre desde la nueva ruta.",
        },
        ["DlgLauncherSettingsUninstall"] = new()
        {
            [LangEn] = "Uninstall from my PC",
            [LangEs] = "Desinstalar de mi PC",
        },
        ["DlgLauncherSettingsUninstallHint"] = new()
        {
            [LangEn] = "Removes the shortcuts, the auto-start entry and the installed copy. Your mods are not touched.",
            [LangEs] = "Quita los accesos directos, el arranque automático y la copia instalada. No toca tus mods.",
        },
        ["DlgLauncherSettingsUninstallTip"] = new()
        {
            [LangEn] = "Deletes the installed launcher copy, its shortcuts and its start-with-Windows entry. "
                     + "It does NOT remove your installed mods (Wars of Liberty, Asian Dynasties) — those are "
                     + "uninstalled separately from each mod's menu. You choose whether to also delete your "
                     + "settings.",
            [LangEs] = "Borra la copia instalada del launcher, sus accesos directos y el inicio con Windows. NO "
                     + "quita tus mods instalados (Wars of Liberty, Asian Dynasties) — esos se desinstalan aparte "
                     + "desde el menú de cada mod. Tú eliges si borrar también tu configuración.",
        },
        ["DlgLauncherSettingsUninstallConfirmTitle"] = new()
        {
            [LangEn] = "Uninstall the launcher",
            [LangEs] = "Desinstalar el launcher",
        },
        ["DlgLauncherSettingsUninstallConfirmBody"] = new()
        {
            [LangEn] = "The launcher will be removed from this PC: the app, its shortcuts and start-with-Windows. "
                     + "Your installed mods (Wars of Liberty, Asian Dynasties) are NOT touched.\n\n"
                     + "Also delete your settings and data (preferences, logs, icon cache)?\n\n"
                     + "• Yes — uninstall and delete everything\n"
                     + "• No — uninstall but keep my settings\n"
                     + "• Cancel — do nothing",
            [LangEs] = "Se quitará el launcher de esta PC: la app, sus accesos directos y el inicio con Windows. "
                     + "Tus mods instalados (Wars of Liberty, Asian Dynasties) NO se tocan.\n\n"
                     + "¿Borrar también tu configuración y datos (preferencias, registros, caché de íconos)?\n\n"
                     + "• Sí — desinstalar y borrar todo\n"
                     + "• No — desinstalar pero conservar mi configuración\n"
                     + "• Cancelar — no hacer nada",
        },
        ["DlgLauncherSettingsUninstallFailed"] = new()
        {
            [LangEn] = "Couldn't uninstall. Try again, or delete the launcher folder manually.",
            [LangEs] = "No se pudo desinstalar. Vuelve a intentarlo o borra la carpeta del launcher a mano.",
        },
        ["DlgLauncherSettingsAssetsCleared"] = new()
        {
            [LangEn] = "Asset cache cleared ({0} files).",
            [LangEs] = "Caché de imágenes eliminada ({0} archivos).",
        },
        ["DlgLauncherSettingsTempCleared"] = new()
        {
            [LangEn] = "Temp files cleared.",
            [LangEs] = "Archivos temporales eliminados.",
        },
        ["DlgLauncherSettingsNothingToClean"] = new()
        {
            [LangEn] = "Nothing to clean.",
            [LangEs] = "Nada que limpiar.",
        },
        ["DlgLauncherSettingsInvalidRepo"] = new()
        {
            [LangEn] = "Invalid repository format. Use owner/repo (e.g. Gorgorito12/aoe3-mods-catalog).",
            [LangEs] = "Formato inválido. Usa owner/repo (ej: Gorgorito12/aoe3-mods-catalog).",
        },
        ["BtnSave"] = new()
        {
            [LangEn] = "Save changes",
            [LangEs] = "Guardar cambios",
        },
        ["BtnCancel"] = new()
        {
            [LangEn] = "Cancel",
            [LangEs] = "Cancelar",
        },
        ["BtnOpenFolder"] = new()
        {
            [LangEn] = "Open folder",
            [LangEs] = "Abrir carpeta",
        },
        // -------- New gear-menu items (Maintenance + Advanced) --------
        ["MenuRepairInstall"] = new()
        {
            [LangEn] = "Repair install",
            [LangEs] = "Reparar instalación",
        },
        ["MenuVerifyFiles"] = new()
        {
            [LangEn] = "Verify files",
            [LangEs] = "Verificar archivos",
        },
        ["MenuViewLogs"] = new()
        {
            [LangEn] = "View logs",
            [LangEs] = "Ver logs",
        },
        ["TooltipMenuRepairInstall"] = new()
        {
            [LangEn] = "Re-downloads the mod payload and overlays it on top " +
                       "of the existing install — replaces missing or corrupt " +
                       "files without losing user data.",
            [LangEs] = "Re-descarga el contenido del mod y lo aplica sobre " +
                       "la instalación actual — reemplaza archivos faltantes " +
                       "o corruptos sin perder los datos del usuario.",
        },
        ["TooltipMenuViewLogs"] = new()
        {
            [LangEn] = "Opens the launcher diagnostic log in your default " +
                       "text editor.",
            [LangEs] = "Abre el log de diagnóstico del launcher en tu editor " +
                       "de texto predeterminado.",
        },
        ["TooltipMenuInstallAnotherCopy"] = new()
        {
            [LangEn] = "Install a second, separate copy of this mod in another folder — " +
                       "handy for keeping different versions side by side.",
            [LangEs] = "Instala una segunda copia separada de este mod en otra carpeta — " +
                       "útil para tener distintas versiones al mismo tiempo.",
        },
        ["BtnPlay"] = new()
        {
            [LangEn] = "PLAY",
            [LangEs] = "JUGAR",
        },
        ["BtnPlaying"] = new()
        {
            [LangEn] = "PLAYING...",
            [LangEs] = "JUGANDO...",
        },
        ["BtnStop"] = new()
        {
            [LangEn] = "STOP",
            [LangEs] = "DETENER",
        },
        ["BtnPause"] = new()
        {
            [LangEn] = "PAUSE",
            [LangEs] = "PAUSAR",
        },
        ["BtnResume"] = new()
        {
            [LangEn] = "RESUME",
            [LangEs] = "REANUDAR",
        },
        ["StatusPaused"] = new()
        {
            [LangEn] = "Download paused. Click RESUME to continue from where you left off.",
            [LangEs] = "Descarga pausada. Haz clic en REANUDAR para continuar desde donde quedaste.",
        },

        // -------- Status: idle / ready --------
        ["StatusUpToDate"] = new()
        {
            [LangEn] = "Up to date. Version {0}. Ready to play!",
            [LangEs] = "Todo al día. Versión {0}. ¡Listo para jugar!",
        },
        ["StatusInstalledVersionUnknown"] = new()
        {
            [LangEn] = "Installed and ready to play — couldn't verify your version or updates right now.",
            [LangEs] = "Instalado y listo para jugar — no se pudo verificar tu versión ni actualizaciones ahora.",
        },
        // ---- Offline mode (observed connectivity) ----
        ["OfflineChip"] = new()
        {
            [LangEn] = "Offline",
            [LangEs] = "Sin conexión",
        },
        ["OfflineChipTooltip"] = new()
        {
            [LangEn] = "No internet connection. Installed mods are playable; online " +
                       "features (updates, multiplayer, workshop) are unavailable until " +
                       "you reconnect.",
            [LangEs] = "Sin conexión a internet. Los mods instalados se pueden jugar; las " +
                       "funciones en línea (actualizaciones, multijugador, catálogo) no " +
                       "están disponibles hasta reconectar.",
        },
        ["OfflineNeedsInternet"] = new()
        {
            [LangEn] = "Requires an internet connection",
            [LangEs] = "Necesita conexión a internet",
        },
        ["MpOfflineNotice"] = new()
        {
            [LangEn] = "You're offline. Multiplayer needs an internet connection.",
            [LangEs] = "Sin conexión. El multijugador necesita conexión a internet.",
        },
        // {0} = mod display name, {1} = current ver, {2} = latest ver,
        // {3} = official website URL (from the mod's catalog manifest).
        ["StatusVersionTooOld"] = new()
        {
            [LangEn] = "Your version of {0} ({1}) is too old to update via patches. " +
                       "Latest available: {2}. Please reinstall {0} from {3}.",
            [LangEs] = "Tu versión de {0} ({1}) es demasiado antigua para actualizar por parches. " +
                       "Última disponible: {2}. Necesitas reinstalar {0} desde {3}.",
        },
        ["StatusUpdatesAvailable"] = new()
        {
            [LangEn] = "{0} update(s) available ({1} total).",
            [LangEs] = "{0} actualización(es) disponible(s) ({1} total).",
        },
        ["StatusContinuingUpdate"] = new()
        {
            [LangEn] = "Repair done — continuing with the pending update…",
            [LangEs] = "Reparación lista — continuando con la actualización pendiente…",
        },
        ["VerifyEngineSuffix"] = new()
        {
            [LangEn] = " (base game engine file — reinstall AoE3)",
            [LangEs] = " (archivo del motor del juego base — reinstala AoE3)",
        },
        ["StatusRevalidating"] = new()
        {
            [LangEn] = "Re-verifying files ({0}/{1})…",
            [LangEs] = "Re-verificando archivos ({0}/{1})…",
        },
        // {0} = mod display name.

        // -------- Status: in progress --------
        // {0} = active mod's display name (e.g. "Wars of Liberty",
        // "Improvement Mod"). Used to be hard-coded to WoL.
        ["StatusDetectingInstall"] = new()
        {
            [LangEn] = "Detecting {0} installation...",
            [LangEs] = "Detectando instalación de {0}...",
        },
        ["StatusFetchingManifest"] = new()
        {
            [LangEn] = "Downloading update information...",
            [LangEs] = "Descargando información de actualizaciones...",
        },
        ["StatusIdentifyingVersion"] = new()
        {
            [LangEn] = "Identifying installed version...",
            [LangEs] = "Identificando versión instalada...",
        },
        ["StatusVerifyingExisting"] = new()
        {
            [LangEn] = "Verifying existing file for update #{0}...",
            [LangEs] = "Verificando archivo existente para actualización #{0}...",
        },
        ["StatusDownloading"] = new()
        {
            [LangEn] = "Downloading update #{0} (version {1})...",
            [LangEs] = "Descargando actualización #{0} (versión {1})...",
        },
        ["StatusVerifyingDownload"] = new()
        {
            [LangEn] = "Verifying integrity of update #{0}...",
            [LangEs] = "Verificando integridad de actualización #{0}...",
        },
        ["StatusApplying"] = new()
        {
            [LangEn] = "Applying update #{0}...",
            [LangEs] = "Aplicando actualización #{0}...",
        },
        ["StatusExtracting"] = new()
        {
            [LangEn] = "Extracting: {0}",
            [LangEs] = "Extrayendo: {0}",
        },
        ["StatusExtractFailedRestoring"] = new()
        {
            [LangEn] = "Extraction failed. Restoring backup files...",
            [LangEs] = "Error durante la extracción. Restaurando archivos...",
        },
        ["StatusCleanup"] = new()
        {
            [LangEn] = "Running post-update cleanup #{0}...",
            [LangEs] = "Aplicando limpieza post-actualización #{0}...",
        },
        ["StatusAllDone"] = new()
        {
            [LangEn] = "All updates applied successfully.",
            [LangEs] = "Todas las actualizaciones aplicadas correctamente.",
        },
        ["StatusCancelledCheck"] = new()
        {
            [LangEn] = "Check cancelled.",
            [LangEs] = "Verificación cancelada.",
        },
        ["StatusCancelledUpdate"] = new()
        {
            [LangEn] = "Update cancelled.",
            [LangEs] = "Actualización cancelada.",
        },

        // -------- Progress display --------
        ["ProgressUpdating"] = new()
        {
            [LangEn] = "Updating {0} → {1}",
            [LangEs] = "Actualizando {0} → {1}",
        },
        ["ProgressPatchOf"] = new()
        {
            [LangEn] = "Patch {0} of {1}: {2} → {3}",
            [LangEs] = "Parche {0} de {1}: {2} → {3}",
        },
        // Sub-phase-aware status lines shown just above the bars during update.
        // {0} = patch target version, {1} = current step, {2} = total steps.
        ["ProgressPatchStatusDownloading"] = new()
        {
            [LangEn] = "📥 Downloading {0} ({1}/{2})...",
            [LangEs] = "📥 Descargando {0} ({1}/{2})...",
        },
        ["ProgressPatchStatusVerifying"] = new()
        {
            [LangEn] = "✓ Verifying {0} ({1}/{2})...",
            [LangEs] = "✓ Verificando {0} ({1}/{2})...",
        },
        ["ProgressPatchStatusApplying"] = new()
        {
            [LangEn] = "🔧 Applying {0} ({1}/{2})...",
            [LangEs] = "🔧 Aplicando {0} ({1}/{2})...",
        },
        ["ProgressCurrentPatch"] = new()
        {
            [LangEn] = "Current patch",
            [LangEs] = "Parche actual",
        },
        ["ProgressOverall"] = new()
        {
            [LangEn] = "Overall",
            [LangEs] = "Total",
        },
        ["ProgressSpeed"] = new()
        {
            [LangEn] = "Speed: {0}/s",
            [LangEs] = "Velocidad: {0}/s",
        },
        // Phase-aware speed labels — picked dynamically so the user sees an
        // accurate description of what the bytes/sec figure represents.
        ["ProgressSpeedDownload"] = new()
        {
            [LangEn] = "📡 Download: {0}/s",
            [LangEs] = "📡 Descarga: {0}/s",
        },
        ["ProgressSpeedExtract"] = new()
        {
            [LangEn] = "📦 Extract: {0}/s",
            [LangEs] = "📦 Extracción: {0}/s",
        },
        ["ProgressSpeedCopy"] = new()
        {
            [LangEn] = "💾 Copy: {0}/s",
            [LangEs] = "💾 Copia: {0}/s",
        },
        ["ProgressSpeedVerify"] = new()
        {
            [LangEn] = "✓ Verifying: {0}/s",
            [LangEs] = "✓ Verificando: {0}/s",
        },
        ["StatusExtractingPayload"] = new()
        {
            [LangEn] = "📦 Extracting mod files ({0}/{1})...",
            [LangEs] = "📦 Extrayendo archivos del mod ({0}/{1})...",
        },
        ["StatusInstallingMod"] = new()
        {
            [LangEn] = "🔧 Applying mod overlay ({0}/{1})...",
            [LangEs] = "🔧 Aplicando mod ({0}/{1})...",
        },
        ["ProgressEta"] = new()
        {
            [LangEn] = "ETA: {0}",
            [LangEs] = "Tiempo restante: {0}",
        },
        ["ProgressEtaCalculating"] = new()
        {
            [LangEn] = "calculating...",
            [LangEs] = "calculando...",
        },

        // -------- Phase breadcrumb step labels --------

        // -------- Update breadcrumb step labels --------

        // Subtitle of the header during update — shown under "Updating X → Y"
        ["ProgressPatchSubtitle"] = new()
        {
            [LangEn] = "Patch {0}/{1}: {2} → {3}",
            [LangEs] = "Parche {0}/{1}: {2} → {3}",
        },

        // -------- Dialogs --------
        ["DlgInvalidFolderTitle"] = new()
        {
            [LangEn] = "Invalid folder",
            [LangEs] = "Carpeta no válida",
        },
        // {0} = mod display name, {1} = the content signals the launcher expects
        // inside the install folder, e.g. "data\\stringtabley.xml + art\\zulushield"
        // for WoL or "age3m.exe" for Improvement Mod.
        ["DlgInvalidFolderBody"] = new()
        {
            [LangEn] = "The selected folder doesn't appear to be a valid {0} installation.\n\n" +
                       "Expected to find '{1}' inside.",
            [LangEs] = "La carpeta seleccionada no parece ser una instalación válida de {0}.\n\n" +
                       "Esperaba encontrar '{1}' adentro.",
        },
        // {0} = mod display name, {1} = the content marker that's missing
        // (relative to the install folder), e.g. "art\\zulushield" for WoL. Shown
        // when the folder has the probe file but not the marker — i.e. it looks
        // like a base-game / incomplete install rather than a full mod install.
        ["DlgInvalidFolderMarkerBody"] = new()
        {
            [LangEn] = "The selected folder looks like a base Age of Empires III folder, not a complete {0} installation — it's missing '{1}'.\n\n" +
                       "If you uninstalled {0} or files are missing, reinstall it from the launcher (adding the folder can't recover deleted mod files).",
            [LangEs] = "La carpeta seleccionada parece un Age of Empires III base, no una instalación completa de {0} — le falta '{1}'.\n\n" +
                       "Si desinstalaste {0} o faltan archivos, reinstálalo desde el launcher (agregar la carpeta no puede recuperar archivos del mod borrados).",
        },
        // {0} = mod display name.
        ["DlgInvalidFolderEngineBody"] = new()
        {
            [LangEn] = "That folder only has {0}'s own files, not the Age of Empires III game underneath — so it isn't a complete installation the launcher can run.\n\n" +
                       "Install {0} from the launcher instead: it copies a full Age of Empires III and lays the mod on top.",
            [LangEs] = "Esa carpeta solo tiene los archivos de {0}, no el juego Age of Empires III debajo — así que no es una instalación completa que el launcher pueda ejecutar.\n\n" +
                       "Instala {0} desde el launcher: copia un Age of Empires III completo y aplica el mod encima.",
        },
        // {0} = mod display name.
        ["DlgInvalidFolderInProgressBody"] = new()
        {
            [LangEn] = "That folder holds an UNFINISHED install of {0}: a previous install was " +
                       "interrupted before it could complete, so most of the mod's files are missing.\n\n" +
                       "Install {0} again from the launcher (or delete that folder) instead of pointing it here.",
            [LangEs] = "Esa carpeta tiene una instalación de {0} SIN TERMINAR: una instalación anterior se " +
                       "interrumpió antes de completarse, así que faltan casi todos los archivos del mod.\n\n" +
                       "Instala {0} de nuevo desde el launcher (o borra esa carpeta) en vez de apuntar aquí.",
        },
        // {0} = mod display name.
        ["DlgFolderPickerTitle"] = new()
        {
            [LangEn] = "Select {0} folder",
            [LangEs] = "Seleccionar carpeta de {0}",
        },
        ["DlgGameRunningTitle"] = new()
        {
            [LangEn] = "Game is running",
            [LangEs] = "El juego está en ejecución",
        },
        ["DlgGameRunningBody"] = new()
        {
            [LangEn] = "Age of Empires III is currently running.\n\n" +
                       "• Yes — Close the game and continue\n" +
                       "• No — Continue without closing (not recommended)\n" +
                       "• Cancel — Go back",
            [LangEs] = "Age of Empires III está actualmente en ejecución.\n\n" +
                       "• Sí — Cerrar el juego y continuar\n" +
                       "• No — Continuar sin cerrar (no recomendado)\n" +
                       "• Cancelar — Volver",
        },
        ["DlgGameLaunchErrorTitle"] = new()
        {
            [LangEn] = "Could not start the game",
            [LangEs] = "Error al iniciar el juego",
        },

        // -------- Errors (also surface in dialogs) --------
        ["ErrManifestUnreachable"] = new()
        {
            [LangEn] = "Could not fetch UpdateInfo.xml from any server.\n" +
                       "Primary ({0}): {1}\nAlternate ({2}): {3}",
            [LangEs] = "No se pudo obtener UpdateInfo.xml de ningún servidor.\n" +
                       "Primario ({0}): {1}\nAlternativo ({2}): {3}",
        },
        ["ErrManifestEmpty"] = new()
        {
            [LangEn] = "UpdateInfo.xml is empty or malformed.",
            [LangEs] = "UpdateInfo.xml está vacío o malformado.",
        },
        ["ErrCorruptDownload"] = new()
        {
            [LangEn] = "Update #{0} arrived corrupted. Expected CRC32: {1}, actual: {2}.",
            [LangEs] = "La actualización #{0} llegó corrupta. CRC32 esperado: {1}, real: {2}.",
        },
        // {0} = mod display name (e.g. "Wars of Liberty", "Improvement Mod").
        // The 'age3y.exe' string stays literal — that's specifically the AoE3
        // base game's executable, which is the same file regardless of which
        // mod is on top.
        ["ErrGameExeNotFound"] = new()
        {
            [LangEn] = "'age3y.exe' (Age of Empires III: The Asian Dynasties) not found.\n\n" +
                       "{0} needs Age of Empires III installed to work.\n" +
                       "Use the \"Change...\" button to point to the correct folder, " +
                       "or set 'gameExecutable' manually in launcher-config.json.",
            [LangEs] = "No se encontró 'age3y.exe' (Age of Empires III: The Asian Dynasties).\n\n" +
                       "{0} necesita Age of Empires III instalado para funcionar.\n" +
                       "Usa el botón \"Cambiar...\" para indicar la carpeta correcta, " +
                       "o configura 'gameExecutable' manualmente en launcher-config.json.",
        },
        ["ErrInstallPathMissing"] = new()
        {
            [LangEn] = "Install path not detected. Call CheckAsync first.",
            [LangEs] = "Ruta de instalación no detectada. Llama a CheckAsync primero.",
        },

        // -------- Installer flow (used when WoL isn't installed yet) --------
        ["BtnInstall"] = new()
        {
            [LangEn] = "INSTALL MOD",
            [LangEs] = "INSTALAR MOD",
        },
        ["StatusNotInstalled"] = new()
        {
            [LangEn] = "Wars of Liberty is not installed. Choose a folder and click INSTALL MOD.",
            [LangEs] = "Wars of Liberty no está instalado. Elige una carpeta y haz clic en INSTALAR MOD.",
        },

        // -------- Integrated install panel --------
        ["InstallGameNotLaunchedWarning"] = new()
        {
            [LangEn] = "⚠ We couldn't find Age of Empires III user data. " +
                       "Open Age of Empires III: The Asian Dynasties at least once " +
                       "before installing, so it generates its configuration files.",
            [LangEs] = "⚠ No encontramos los datos de usuario de Age of Empires III. " +
                       "Abre Age of Empires III: The Asian Dynasties al menos una vez " +
                       "antes de instalar, para que genere sus archivos de configuración.",
        },
        ["InstallAoe3NotDetected"] = new()
        {
            [LangEn] = "⚠ Age of Empires III was not detected automatically.\n" +
                       "It's required — the mod is installed on top of a copy of AoE3, so " +
                       "without the base game there's nothing to install. Use the button " +
                       "above to select your Age of Empires III folder to continue.",
            [LangEs] = "⚠ No se detectó Age of Empires III automáticamente.\n" +
                       "Es obligatorio: el mod se instala sobre una copia de AoE3, así que " +
                       "sin el juego base no hay nada que instalar. Usa el botón de arriba " +
                       "para seleccionar tu carpeta de Age of Empires III y poder continuar.",
        },
        // Blocking warning shown above the buttons while the install is
        // gated on a missing AoE3 source. Kept short — the orange status
        // line under the AoE3 field carries the full explanation.
        ["DlgInstallAoe3Required"] = new()
        {
            [LangEn] = "Select your Age of Empires III folder above to enable installation.",
            [LangEs] = "Selecciona tu carpeta de Age of Empires III arriba para poder instalar.",
        },
        // Manual "search my Asian Dynasties" button + its transient states.
        // Shown only while no AoE3 source is set; runs an exhaustive content
        // scan that finds AoE3 even in non-standard folders (e.g. Microsoft Studios).
        ["DlgSearchAoe3Button"] = new()
        {
            [LangEn] = "Search for my Asian Dynasties…",
            [LangEs] = "Buscar mi Asian Dynasties…",
        },
        ["DlgSearchAoe3Searching"] = new()
        {
            [LangEn] = "Searching for Age of Empires III on your PC…",
            [LangEs] = "Buscando Age of Empires III en tu PC…",
        },
        ["DlgSearchAoe3NotFound"] = new()
        {
            [LangEn] = "⚠ No clean Age of Empires III install was found. Use the button " +
                       "above to select the folder manually.",
            [LangEs] = "⚠ No se encontró una instalación limpia de Age of Empires III. Usa el " +
                       "botón de arriba para seleccionar la carpeta manualmente.",
        },
        ["InstallDiskSpace"] = new()
        {
            [LangEn] = "Available disk space: {0} on {1}",
            [LangEs] = "Espacio en disco disponible: {0} en {1}",
        },
        ["DiskSpaceCalculating"] = new()
        {
            [LangEn] = "Calculating required space…",
            [LangEs] = "Calculando el espacio necesario…",
        },
        // {0} = required, {1} = free, {2} = drive
        ["DiskSpaceWarningLine"] = new()
        {
            [LangEn] = "Low space: about {0} needed, only {1} free on {2}.",
            [LangEs] = "Poco espacio: se necesitan ~{0}, solo hay {1} en {2}.",
        },
        // {0} = required, {1} = free, {2} = drive. Generic body for any download that can
        // fill a disk; DiskSpaceConfirmInstallBody takes no arguments and so cannot name
        // the drive, which matters when the short one is not the one the user was looking at.
        ["DiskSpaceConfirmDownloadBody"] = new()
        {
            [LangEn] = "This download needs about {0} of free space, but only {1} is free on {2}. "
                     + "It can fail part-way if the drive fills up. Continue anyway?",
            [LangEs] = "Esta descarga necesita unos {0} libres, pero solo hay {1} en {2}. Puede fallar "
                     + "a la mitad si el disco se llena. ¿Continuar igual?",
        },
        // The install dialog's optional row. Shown ONLY when another installed mod could
        // supply them, so a first-ever install never sees it.
        ["DlgInstallCopySettings"] = new()
        {
            [LangEn] = "Copy graphics, sound and hotkeys from:",
            [LangEs] = "Copiar gráficos, sonido y atajos de:",
        },
        // Says WHEN, because it is usually not now. The game writes its settings file on its
        // first run, so a brand-new mod has nowhere to put them until then — and a promise
        // that quietly does nothing for one launch is worse than no promise.
        ["DlgInstallCopySettingsHint"] = new()
        {
            [LangEn] = "Your saved games, home cities and profile are not touched. If this mod is "
                     + "new, the copy is applied once you have opened it — not on the first launch.",
            [LangEs] = "Tus partidas guardadas, tus metrópolis y tu perfil no se tocan. Si el mod "
                     + "es nuevo, la copia se aplica cuando ya lo hayas abierto una vez, no en el "
                     + "primer arranque.",
        },
        ["DiskSpaceConfirmTitle"] = new()
        {
            [LangEn] = "Low disk space",
            [LangEs] = "Poco espacio en disco",
        },
        ["DiskSpaceConfirmInstallBody"] = new()
        {
            [LangEn] = "There may not be enough free disk space to install. The install can fail part-way if the drive fills up. Continue anyway?",
            [LangEs] = "Puede que no haya suficiente espacio en disco para instalar. La instalación puede fallar a la mitad si el disco se llena. ¿Continuar igual?",
        },
        // {0} = required, {1} = free, {2} = drive
        ["DiskSpaceConfirmRepairBody"] = new()
        {
            [LangEn] = "Repair needs about {0} of free space, but only {1} is free on {2}. It can fail part-way if the drive fills up. Continue anyway?",
            [LangEs] = "La reparación necesita unos {0} libres, pero solo hay {1} en {2}. Puede fallar a la mitad si el disco se llena. ¿Continuar igual?",
        },
        // {0} = mod display name. Size deliberately omitted — the progress
        // bar underneath already shows real bytes for whichever mod is
        // being downloaded.
        ["StatusDownloadingInstaller"] = new()
        {
            [LangEn] = "📥 Downloading {0} installer...",
            [LangEs] = "📥 Descargando instalador de {0}...",
        },
        // {0} = mod display name (e.g. "Wars of Liberty", "Improvement Mod").
        ["DlgPickInstallFolderTitle"] = new()
        {
            [LangEn] = "Choose where to install {0}",
            [LangEs] = "Elige dónde instalar {0}",
        },
        ["DlgPickInstallFolderHeader"] = new()
        {
            [LangEn] = "Install location",
            [LangEs] = "Ubicación de instalación",
        },
        // {0} = mod display name. Appears twice — first as the subject, then
        // as the folder name (e.g. "Improvement Mod will be installed in its
        // own 'Improvement Mod' folder").
        ["DlgPickInstallFolderDescription"] = new()
        {
            [LangEn] = "{0} will be installed in its own \"{0}\" folder " +
                       "(separate from the original Age of Empires III install). The launcher copies " +
                       "AoE3 there as a base and applies the mod on top, so a working Age of Empires III " +
                       "install is required. About 12 GB of free space recommended.",
            [LangEs] = "{0} se instalará en su propia carpeta \"{0}\" " +
                       "(separada de la instalación original de Age of Empires III). El launcher copia " +
                       "AoE3 ahí como base y aplica el mod encima, por lo que es necesario tener Age of " +
                       "Empires III instalado. Se recomiendan unos 12 GB libres.",
        },
        ["DlgAoe3DetectedTitle"] = new()
        {
            [LangEn] = "AGE OF EMPIRES III DETECTED",
            [LangEs] = "AGE OF EMPIRES III DETECTADO",
        },
        ["DlgAoe3DetectedTitleWithSource"] = new()
        {
            [LangEn] = "AGE OF EMPIRES III DETECTED ({0})",
            [LangEs] = "AGE OF EMPIRES III DETECTADO ({0})",
        },
        ["DlgPickInstallFolderLabel"] = new()
        {
            [LangEn] = "INSTALL FOLDER",
            [LangEs] = "CARPETA DE INSTALACIÓN",
        },
        ["WarnPathEmpty"] = new()
        {
            [LangEn] = "Please enter a folder path.",
            [LangEs] = "Por favor ingresa la ruta de una carpeta.",
        },
        ["WarnPathInvalid"] = new()
        {
            [LangEn] = "This doesn't look like a valid Windows path.",
            [LangEs] = "Esto no parece una ruta válida de Windows.",
        },
        ["WarnPathSystem"] = new()
        {
            [LangEn] = "This folder is reserved by Windows. Please choose a different location.",
            [LangEs] = "Esta carpeta está reservada por Windows. Por favor elige otra ubicación.",
        },

        // -------- AoE3 detection warnings --------
        ["DlgBrokenInstallTitle"] = new()
        {
            // {0} = mod display name.
            [LangEn] = "{0} may not be working correctly",
            [LangEs] = "{0} podría no estar funcionando correctamente",
        },
        // {0} = the detected install path on disk, {1} = mod display name.
        ["DlgBrokenInstallBody"] = new()
        {
            [LangEn] = "{1} was found at:\n\n{0}\n\n" +
                       "But this folder doesn't appear to be inside Age of Empires III. " +
                       "The mod files are on disk, but the AoE3 engine won't load them from " +
                       "this location.\n\n" +
                       "To fix this, reinstall {1} into the same folder as " +
                       "Age of Empires III (typically your Steam library).",
            [LangEs] = "Se encontró {1} en:\n\n{0}\n\n" +
                       "Pero esta carpeta no parece estar dentro de Age of Empires III. " +
                       "Los archivos del mod están en disco, pero el motor de AoE3 no los va a " +
                       "cargar desde esta ubicación.\n\n" +
                       "Para arreglarlo, reinstala {1} en la misma carpeta donde " +
                       "tienes Age of Empires III (típicamente tu biblioteca de Steam).",
        },

        // -------- Install: additional copy --------
        ["DlgInstallCopyExistsTitle"] = new()
        {
            [LangEn] = "Copy already there",
            [LangEs] = "Ya hay una copia ahí",
        },
        ["DlgInstallCopyExistsBody"] = new()
        {
            [LangEn] = "There's already a registered copy of this mod at:\n{0}\n\n" +
                       "Pick a different folder for the new copy, or switch to the existing " +
                       "one from Mod Properties.",
            [LangEs] = "Ya hay una copia registrada de este mod en:\n{0}\n\n" +
                       "Elige otra carpeta para la copia nueva, o cambia a la existente " +
                       "desde Propiedades del mod.",
        },
        ["MenuInstallAnotherCopy"] = new()
        {
            [LangEn] = "Install another copy…",
            [LangEs] = "Instalar otra copia…",
        },
        ["InstallCopiesHeader"] = new()
        {
            [LangEn] = "Installed copies",
            [LangEs] = "Copias instaladas",
        },
        ["RemoveInstallCopy"] = new()
        {
            [LangEn] = "Remove from list (doesn't delete files)",
            [LangEs] = "Quitar de la lista (no borra archivos)",
        },
        ["RemoveInstallBtn"] = new()
        {
            [LangEn] = "Remove",
            [LangEs] = "Quitar",
        },
        ["ManageInstallsHeader"] = new()
        {
            [LangEn] = "Manage installs",
            [LangEs] = "Gestionar instalaciones",
        },
        ["ManageInstallsDesc"] = new()
        {
            [LangEn] = "Each registered copy of this mod (shown by its folder name). Switch the active " +
                       "one, remove it from the list (files are kept), add an existing folder, or install a new copy.",
            [LangEs] = "Cada copia registrada de este mod (por el nombre de su carpeta). Cambia la activa, " +
                       "quítala de la lista (los archivos se conservan), añade una carpeta existente o instala una copia nueva.",
        },
        ["AddExistingFolder"] = new()
        {
            [LangEn] = "Add existing folder…",
            [LangEs] = "Añadir carpeta existente…",
        },
        ["SearchInstallButton"] = new()
        {
            [LangEn] = "Search for my install…",
            [LangEs] = "Buscar mi instalación…",
        },
        ["SearchInstallButtonShort"] = new()
        {
            [LangEn] = "ALREADY INSTALLED?",
            [LangEs] = "¿YA LO TIENES?",
        },
        ["SearchInstallNotFound"] = new()
        {
            [LangEn] = "No existing {0} install was found on your drives. If you have it installed, use “Change mod folder” and point it at the folder.",
            [LangEs] = "No se encontró una instalación de {0} en tus discos. Si la tienes instalada, usa «Cambiar carpeta del mod» y apunta a la carpeta.",
        },
        ["SearchInstallFound"] = new()
        {
            [LangEn] = "Found your install:\n{0}",
            [LangEs] = "Se encontró tu instalación:\n{0}",
        },
        ["SearchInstallFoundMultiple"] = new()
        {
            [LangEn] = "Found your install:\n{0}\n\n({1} other install(s) were also found — pick a specific one with “Change mod folder”.)",
            [LangEs] = "Se encontró tu instalación:\n{0}\n\n(También se encontraron {1} instalación(es) más — elige una específica con «Cambiar carpeta del mod».)",
        },
        ["RenameInstallHint"] = new()
        {
            [LangEn] = "Rename this copy (leave empty to use the folder name)",
            [LangEs] = "Renombra esta copia (déjalo vacío para usar el nombre de la carpeta)",
        },
        ["SwitchToInstall"] = new()
        {
            [LangEn] = "Switch",
            [LangEs] = "Cambiar",
        },
        ["ActiveInstallBadge"] = new()
        {
            [LangEn] = "Active",
            [LangEs] = "Activa",
        },

        // -------- Elevation (UAC) --------
        ["DlgElevationRequiredTitle"] = new()
        {
            [LangEn] = "Administrator permission required",
            [LangEs] = "Se requieren permisos de administrador",
        },
        ["DlgElevationRequiredBody"] = new()
        {
            [LangEn] = "Wars of Liberty is installed in a protected folder:\n{0}\n\n" +
                       "To apply updates there, the launcher needs to be run as administrator. " +
                       "Click OK to restart the launcher with elevated privileges. " +
                       "Windows will ask for confirmation.",
            [LangEs] = "Wars of Liberty está instalado en una carpeta protegida:\n{0}\n\n" +
                       "Para aplicar actualizaciones ahí, el launcher necesita ejecutarse como " +
                       "administrador. Haz clic en Aceptar para reiniciar el launcher con " +
                       "permisos elevados. Windows pedirá confirmación.",
        },
        ["StatusElevationDenied"] = new()
        {
            [LangEn] = "Update cancelled — administrator permission was denied.",
            [LangEs] = "Actualización cancelada — se rechazó el permiso de administrador.",
        },
        ["StatusRunningAsAdmin"] = new()
        {
            [LangEn] = "(running as administrator)",
            [LangEs] = "(ejecutando como administrador)",
        },

        // -------- AoE3 detection / clone flow --------

        // -------- AoE3 not found --------

        // -------- Disk space confirmation --------

        // -------- Clone progress --------
        ["StatusInstallIncomplete"] = new()
        {
            [LangEn] = "Installation finished but {0} item(s) may be missing. Check the log for details.",
            [LangEs] = "La instalación finalizó pero {0} elemento(s) podrían faltar. Revisa el log para más detalles.",
        },
        // Shown when the AoE3 base clone copied 0 files (integrity gate in
        // NativeInstallService.InstallAsync) — the mod would be unplayable, so
        // the install is aborted before overlay/registry/shortcuts.
        ["StatusInstallBaseMissing"] = new()
        {
            [LangEn] = "The Age of Empires III base game wasn't copied, so the mod can't run. Your AoE3 install may be missing, in an unexpected location, or excluded by another mod's path. The mod was NOT installed — check your AoE3 install and try again.",
            [LangEs] = "No se copió el juego base de Age of Empires III, así que el mod no puede ejecutarse. Tu instalación de AoE3 podría faltar, estar en una ubicación inesperada o quedar excluida por la ruta de otro mod. El mod NO se instaló — revisa tu instalación de AoE3 e inténtalo de nuevo.",
        },
        ["StatusGameStillRunning"] = new()
        {
            [LangEn] = "The game couldn't be closed, so nothing was changed — it may be running with administrator rights. Close it yourself and try again.",
            [LangEs] = "No se pudo cerrar el juego, así que no se cambió nada — puede estar ejecutándose como administrador. Ciérralo tú e inténtalo de nuevo.",
        },
        ["StatusInstallSetupPathBadName"] = new()
        {
            [LangEn] = "This mod's name can't be used for the registry entry it needs: it has to be plain ASCII, without \"\\\", and at most {0} characters. Nothing was installed — the mod's author has to shorten it in the catalogue.",
            [LangEs] = "El nombre de este mod no sirve para la entrada del registro que necesita: tiene que ser ASCII simple, sin «\\», y de {0} caracteres como máximo. No se instaló nada — quien publica el mod tiene que acortarlo en el catálogo.",
        },
        ["StatusPrivateKeyNeedsAdmin"] = new()
        {
            [LangEn] = "This mod needs a registry entry of its own so it loads its files instead of the base game's, and creating it needs administrator rights. Close the launcher and run it as administrator, then try again.",
            [LangEs] = "Este mod necesita una entrada propia en el registro para cargar sus archivos y no los del juego base, y crearla requiere permisos de administrador. Cierra el launcher, ábrelo como administrador e inténtalo de nuevo.",
        },
        ["StatusInstallSetupPathFailed"] = new()
        {
            [LangEn] = "This mod needs its own copy of {0} adjusted so it loads its files instead of the base game's, but that executable isn't one the launcher recognises. The mod was NOT installed — it would have run showing plain Age of Empires III content.",
            [LangEs] = "Este mod necesita que se ajuste su propia copia de {0} para que cargue sus archivos y no los del juego base, pero ese ejecutable no es uno que el launcher reconozca. El mod NO se instaló — habría arrancado mostrando el contenido normal de Age of Empires III.",
        },
        // Short on purpose: this lands in the status bar and the progress error
        // line. The paths, the how-to and the copy button live in
        // AntivirusExclusionDialog, which this catch opens — a paragraph with two
        // absolute paths is unreadable in a one-line status strip.
        ["InstallDefenderBlocked"] = new()
        {
            [LangEn] = "Antivirus removed a mod file during install: {0}. The mod was not installed.",
            [LangEs] = "El antivirus eliminó un archivo del mod durante la instalación: {0}. El mod no se instaló.",
        },

        // -------- Antivirus exclusion notice (AntivirusExclusionDialog) --------
        // Two modes, one dialog: shown BEFORE installing a mod that declares a
        // known false positive (ModProfile.AntivirusFalsePositiveFile), and AFTER an
        // antivirus actually removed a payload file. The launcher never edits
        // antivirus settings — it only names the folders and copies them.
        ["DlgAntivirusTitleNotice"] = new()
        {
            [LangEn] = "Antivirus exclusion recommended",
            [LangEs] = "Se recomienda una exclusión de antivirus",
        },
        ["DlgAntivirusTitleBlocked"] = new()
        {
            [LangEn] = "Your antivirus removed a mod file",
            [LangEs] = "Tu antivirus eliminó un archivo del mod",
        },
        // {0} = mod display name, {1} = the file antivirus flags.
        ["DlgAntivirusNoticeBody"] = new()
        {
            [LangEn] = "Windows Defender is known to flag one of {0}'s files, {1}, as a threat and delete it while the mod installs. It is a false positive: the file is part of the mod, the detection is older than this launcher, and it has already been reported.\n\nIf it happens, the install stops after the whole download has finished — so it is worth adding the exclusions below before you start.",
            [LangEs] = "Windows Defender suele marcar como amenaza uno de los archivos de {0}, {1}, y lo elimina mientras el mod se instala. Es un falso positivo: el archivo es parte del mod, la detección es anterior a este launcher y ya fue reportada.\n\nSi ocurre, la instalación se detiene después de que terminó toda la descarga — así que conviene agregar las exclusiones de abajo antes de empezar.",
        },
        // {0} = the file that was removed.
        ["DlgAntivirusBlockedBody"] = new()
        {
            [LangEn] = "Your antivirus removed {0} while the mod was installing, so the launcher stopped instead of leaving you with a broken copy of the mod.\n\nThis is a known false positive — the file is safe. Add the exclusions below, then install again.",
            [LangEs] = "Tu antivirus eliminó {0} mientras el mod se instalaba, así que el launcher se detuvo en lugar de dejarte una copia dañada del mod.\n\nEs un falso positivo conocido — el archivo es seguro. Agrega las exclusiones de abajo y vuelve a instalar.",
        },
        ["DlgAntivirusPathsLabel"] = new()
        {
            [LangEn] = "Add these folders to your antivirus exclusions:",
            [LangEs] = "Agrega estas carpetas a las exclusiones de tu antivirus:",
        },
        ["DlgAntivirusHowTo"] = new()
        {
            [LangEn] = "In Windows Security: Virus & threat protection → Manage settings → Add or remove exclusions → Add an exclusion → Folder.",
            [LangEs] = "En Seguridad de Windows: Protección antivirus y contra amenazas → Administrar la configuración → Agregar o quitar exclusiones → Agregar una exclusión → Carpeta.",
        },
        ["DlgAntivirusCopyPaths"] = new()
        {
            [LangEn] = "Copy paths",
            [LangEs] = "Copiar rutas",
        },
        ["DlgAntivirusCopied"] = new()
        {
            [LangEn] = "Copied",
            [LangEs] = "Copiado",
        },
        ["DlgAntivirusDontShowAgain"] = new()
        {
            [LangEn] = "Don't show this again",
            [LangEs] = "No volver a mostrar esto",
        },
        ["DlgAntivirusContinue"] = new()
        {
            [LangEn] = "Continue",
            [LangEs] = "Continuar",
        },
        ["DlgAntivirusCancel"] = new()
        {
            [LangEn] = "Cancel",
            [LangEs] = "Cancelar",
        },
        ["DlgAntivirusClose"] = new()
        {
            [LangEn] = "Close",
            [LangEs] = "Cerrar",
        },

        // -------- Running under a different Windows account (RunningAccount) --------
        // Shown once when the launcher runs as one account while another account's session is
        // open — the "run as administrator with someone else's credentials" case, which quietly
        // splits a player's recordings, saves, decks and launcher settings across two profiles.
        ["DlgCrossUserTitle"] = new()
        {
            [LangEn] = "The launcher is running under another Windows account",
            [LangEs] = "El launcher está corriendo con otra cuenta de Windows",
        },
        ["DlgCrossUserBody"] = new()
        {
            [LangEn] = "You are signed in to Windows as {1}, but the launcher is running as {0}.\n\nAge of Empires III writes into the Documents folder of the account that started it, and the launcher is what starts it. So your recorded games, saves, home city decks and launcher settings are going to {0}'s folders — and if you sometimes open the launcher normally, they end up split between the two accounts.",
            [LangEs] = "Iniciaste sesión en Windows como {1}, pero el launcher se está ejecutando como {0}.\n\nAge of Empires III escribe en la carpeta Documentos de la cuenta que lo inició, y quien lo inicia es el launcher. Así que tus partidas grabadas, tus guardados, tus mazos y los ajustes del launcher están yendo a las carpetas de {0} — y si algunas veces abres el launcher normal, terminan repartidos entre las dos cuentas.",
        },
        ["DlgCrossUserWhereNow"] = new()
        {
            [LangEn] = "Where it is being saved now, under {0}:",
            [LangEs] = "Donde se está guardando ahora, en {0}:",
        },
        ["DlgCrossUserWhereYours"] = new()
        {
            [LangEn] = "Your own account, {0}:",
            [LangEs] = "Tu propia cuenta, {0}:",
        },
        ["DlgCrossUserAutoStart"] = new()
        {
            [LangEn] = "Starting with Windows will not work this way either: the launcher registers it for the account it runs as, and Windows only reads your own when you sign in.",
            [LangEs] = "Iniciar con Windows tampoco va a funcionar así: el launcher lo registra para la cuenta con la que corre, y Windows solo lee la tuya cuando inicias sesión.",
        },
        ["DlgCrossUserHowTo"] = new()
        {
            [LangEn] = "To keep everything in one place, open the launcher normally — a plain double-click, not \"run as administrator\" with another account. It does not need administrator rights. Whatever is already saved under the other account stays there, and you can copy it across yourself.",
            [LangEs] = "Para tener todo en un mismo lugar, abre el launcher normal — con doble clic, no con «ejecutar como administrador» usando otra cuenta. No necesita permisos de administrador. Lo que ya se guardó en la otra cuenta se queda ahí, y puedes copiarlo tú mismo.",
        },
        ["DlgCrossUserCopyPaths"] = new()
        {
            [LangEn] = "Copy paths",
            [LangEs] = "Copiar rutas",
        },
        ["DlgCrossUserCopied"] = new()
        {
            [LangEn] = "Copied",
            [LangEs] = "Copiado",
        },
        ["DlgCrossUserClose"] = new()
        {
            [LangEn] = "Got it",
            [LangEs] = "Entendido",
        },
        ["DlgSettingsStartupWrongAccount"] = new()
        {
            [LangEn] = "Registered for {0}, but you sign in to Windows as {1} — so Windows will not start it for you. Open the launcher as {1} and turn this on again.",
            [LangEs] = "Registrado para {0}, pero inicias sesión en Windows como {1}, así que Windows no lo va a iniciar por ti. Abre el launcher como {1} y vuelve a activarlo.",
        },
        ["ModPropUserDataOtherAccount"] = new()
        {
            [LangEn] = "The launcher is running as {0}, so this is {0}'s folder, not {1}'s. Anything saved while the launcher was open as {1} is in a different one.",
            [LangEs] = "El launcher se está ejecutando como {0}, así que esta es la carpeta de {0}, no la de {1}. Lo que se guardó con el launcher abierto como {1} está en otra.",
        },

        // -------- Download corruption retry (NativeInstall) --------
        // Shown when ZIP extraction fails because the downloaded payload is
        // corrupted (usually a flipped byte during the multi-GB download).
        // {0} = attempt just failed (1-based); {1} = total attempts allowed.
        ["DlgInstallRetryCorruptTitle"] = new()
        {
            [LangEn] = "Download appears corrupted",
            [LangEs] = "La descarga parece estar corrupta",
        },
        ["DlgInstallRetryCorruptBody"] = new()
        {
            [LangEn] = "The downloaded mod files failed integrity check (attempt {0} of {1}). " +
                       "This usually means a few bytes got dropped during the download.\n\n" +
                       "Retry the download from scratch?",
            [LangEs] = "Los archivos descargados del mod fallaron la verificación de integridad (intento {0} de {1}). " +
                       "Esto suele significar que se perdieron algunos bytes durante la descarga.\n\n" +
                       "¿Reintentar la descarga desde cero?",
        },
        ["StatusInstallRetrying"] = new()
        {
            [LangEn] = "Retrying install (attempt {0} of {1})...",
            [LangEs] = "Reintentando instalación (intento {0} de {1})...",
        },
        ["StatusInstallCorruptedGaveUp"] = new()
        {
            [LangEn] = "Download kept arriving corrupted after {0} attempts. Try again later or from a different network.",
            [LangEs] = "La descarga siguió llegando corrupta después de {0} intentos. Prueba de nuevo más tarde o desde otra red.",
        },

        // -------- Verification --------
        ["StatusVerifying"] = new()
        {
            [LangEn] = "Verifying installation...",
            [LangEs] = "Verificando instalación...",
        },
        ["StatusVerifyOk"] = new()
        {
            [LangEn] = "✓ Installation verified — {0} items checked, all OK.",
            [LangEs] = "✓ Instalación verificada — {0} elementos revisados, todo bien.",
        },
        ["StatusVerifyMissing"] = new()
        {
            [LangEn] = "⚠ {0} problem(s) found: {1}",
            [LangEs] = "⚠ {0} problema(s) encontrado(s): {1}",
        },
        ["StatusRepairNothing"] = new()
        {
            [LangEn] = "✓ Installation intact — nothing to repair ({0} files verified).",
            [LangEs] = "✓ Instalación íntegra — nada que reparar ({0} archivos verificados).",
        },
        ["StatusDeltaChecking"] = new()
        {
            [LangEn] = "Checking for a small patch…",
            [LangEs] = "Buscando un parche pequeño…",
        },
        ["StatusDeltaApplying"] = new()
        {
            [LangEn] = "Applying incremental patch…",
            [LangEs] = "Aplicando parche incremental…",
        },
        ["StatusRepairingFiles"] = new()
        {
            [LangEn] = "Repairing {0} damaged file(s)…",
            [LangEs] = "Reparando {0} archivo(s) dañado(s)…",
        },
        ["StatusInstallSuccessVerified"] = new()
        {
            [LangEn] = "✓ Wars of Liberty installed and verified ({0} items checked).",
            [LangEs] = "✓ Wars of Liberty instalado y verificado ({0} elementos revisados).",
        },
        ["DlgVerifyRepairBody"] = new()
        {
            [LangEn] = "Found {0} problem(s) in the installation.\n\n" +
                       "Would you like to repair it? This will re-download the mod files " +
                       "and overwrite any damaged or missing files.\n\n" +
                       "Your AoE3 game files will NOT be affected.",
            [LangEs] = "Se encontraron {0} problema(s) en la instalación.\n\n" +
                       "¿Deseas repararla? Esto volverá a descargar los archivos del mod " +
                       "y sobrescribirá los archivos dañados o faltantes.\n\n" +
                       "Los archivos del juego AoE3 NO se verán afectados.",
        },
        ["StatusRepairSuccess"] = new()
        {
            [LangEn] = "✓ Repair complete — all files verified successfully.",
            [LangEs] = "✓ Reparación completa — todos los archivos verificados correctamente.",
        },
        ["StatusUpdateSuccess"] = new()
        {
            [LangEn] = "✓ Update complete — the mod is now up to date.",
            [LangEs] = "✓ Actualización completa — el mod está al día.",
        },
        ["TranslationRevertedTitle"] = new()
        {
            [LangEn] = "Translation reset to English",
            [LangEs] = "Traducción restablecida a inglés",
        },
        ["DlgLangRefreshButton"] = new()
        {
            [LangEn] = "🔄  Check for new translations",
            [LangEs] = "🔄  Buscar nuevas traducciones",
        },
        ["DlgLangRefreshing"] = new() { [LangEn] = "⏳  Checking…", [LangEs] = "⏳  Buscando…" },
        ["ModPropTempSection"] = new()
        {
            [LangEn] = "TEMPORARY FILES",
            [LangEs] = "ARCHIVOS TEMPORALES",
        },
        ["ModPropTempDesc"] = new()
        {
            [LangEn] = "Free up the install download left in the temp folder (can be several GB). Safe to delete — it's only a download cache and gets re-downloaded if you ever repair.",
            [LangEs] = "Libera la descarga de instalación que queda en la carpeta temporal (pueden ser varios GB). Es seguro borrarla — solo es caché de descarga y se vuelve a descargar si reparas.",
        },
        ["ModPropClearTemp"] = new()
        {
            [LangEn] = "🗑  Clear temporary files",
            [LangEs] = "🗑  Limpiar archivos temporales",
        },
        ["DlgTempClearing"] = new() { [LangEn] = "⏳  Clearing…", [LangEs] = "⏳  Limpiando…" },
        ["DlgTempClearedTitle"] = new()
        {
            [LangEn] = "Temporary files",
            [LangEs] = "Archivos temporales",
        },
        ["DlgTempCleared"] = new()
        {
            [LangEn] = "Temporary install files were cleared successfully.",
            [LangEs] = "Los archivos temporales de instalación se borraron correctamente.",
        },
        ["DlgTempClearFailed"] = new()
        {
            [LangEn] = "Couldn't clear some files (they may be in use). Try again after closing the game.",
            [LangEs] = "No se pudieron borrar algunos archivos (pueden estar en uso). Inténtalo de nuevo tras cerrar el juego.",
        },
        ["TranslationWrongModTitle"] = new() { [LangEn] = "Translation is for another mod", [LangEs] = "La traducción es para otro mod" },
        ["TranslationWrongModBody"] = new()
        {
            [LangEn] = "The translation \"{0}\" was made for the mod \"{1}\", not {2}. Applying it could overwrite this mod's files with the wrong text, so it was blocked.",
            [LangEs] = "La traducción \"{0}\" se hizo para el mod \"{1}\", no para {2}. Aplicarla podría sobrescribir los archivos de este mod con el texto equivocado, así que se bloqueó.",
        },
        ["LangCardForMod"] = new() { [LangEn] = "For mod {0}", [LangEs] = "Para el mod {0}" },
        ["LangCardPackVer"] = new() { [LangEn] = "Pack v{0}", [LangEs] = "Pack v{0}" },
        ["LangCardActive"] = new() { [LangEn] = "In use ✓", [LangEs] = "En uso ✓" },
        ["LangCardUse"] = new() { [LangEn] = "Use", [LangEs] = "Usar" },
        ["LangCardUseAnyway"] = new() { [LangEn] = "Use anyway", [LangEs] = "Usar igual" },
        ["LangCardApplyVersion"] = new() { [LangEn] = "Apply this version", [LangEs] = "Aplicar esta versión" },
        ["LangCardVerNewest"] = new() { [LangEn] = "newest", [LangEs] = "más nueva" },
        ["LangCardVerActive"] = new() { [LangEn] = "in use", [LangEs] = "en uso" },
        ["LangCardUnavailableBusy"] = new() { [LangEn] = "Unavailable", [LangEs] = "No disponible" },
        ["LangCardCompatibleHint"] = new()
        {
            [LangEn] = "Compatible with your installed mod version.",
            [LangEs] = "Compatible con tu versión instalada del mod.",
        },
        ["LangCardBlockedHint"] = new()
        {
            [LangEn] = "⚠ Made for a different mod version. Some text may be wrong, missing, or in English — and it can cause multiplayer sync problems (version mismatch / out-of-sync) with other players. Use it anyway at your own risk.",
            [LangEs] = "⚠ Hecha para otra versión del mod. Algunos textos pueden quedar incorrectos, faltantes o en inglés, y puede causar problemas de sincronización en multijugador (versión distinta / desincronización) con otros jugadores. Úsala igual bajo tu propia responsabilidad.",
        },
        ["LanguageBusyHint"] = new()
        {
            [LangEn] = "⏳ You can't change the language while the mod is installing or updating. Wait for it to finish.",
            [LangEs] = "⏳ No puedes cambiar el idioma mientras el mod se está instalando o actualizando. Espera a que termine.",
        },
        ["TranslationRevertedBody"] = new()
        {
            [LangEn] = "Your \"{0}\" translation was made for version {1}, but the mod is now {2}. It was switched back to English to avoid mixing old translated text with new content — pick it again once an updated pack is released.",
            [LangEs] = "Tu traducción \"{0}\" era para la versión {1}, pero el mod ahora está en {2}. Se cambió a inglés para no mezclar texto traducido viejo con contenido nuevo — vuelve a elegirla cuando salga un pack actualizado.",
        },
        ["StatusUpdateAvailableGh"] = new()
        {
            [LangEn] = "Update available: {0}. Click Update to install it.",
            [LangEs] = "Actualización disponible: {0}. Haz clic en Actualizar para instalarla.",
        },
        ["StatusGhVersionUnknownCanUpdate"] = new()
        {
            [LangEn] = "Installed — version not verified. You can update to {0}.",
            [LangEs] = "Instalado — versión sin verificar. Puedes actualizar a {0}.",
        },
        ["StatusUpdatePausedPinned"] = new()
        {
            [LangEn] = "On v{0} — updates paused. Resume them in Mod Properties.",
            [LangEs] = "En la v{0} — actualizaciones en pausa. Reanúdalas en Propiedades del mod.",
        },
        ["StatusRepairPartial"] = new()
        {
            [LangEn] = "⚠ Repair finished but {0} problem(s) remain. Some AoE3 base files may need manual reinstall.",
            [LangEs] = "⚠ Reparación terminada pero {0} problema(s) persisten. Algunos archivos base de AoE3 pueden necesitar reinstalación manual.",
        },

        // -------- Game state --------
        ["StatusPlaying"] = new()
        {
            [LangEn] = "🎮 Game is running — Wars of Liberty is active.",
            [LangEs] = "🎮 El juego está en ejecución — Wars of Liberty está activo.",
        },
        ["StatusGameClosed"] = new()
        {
            [LangEn] = "Game closed.",
            [LangEs] = "Juego cerrado.",
        },
        ["DlgInstallNoUrlBody"] = new()
        {
            [LangEn] = "No installer URL is configured.\n\n" +
                       "Click OK to open the official Wars of Liberty website where you can " +
                       "download the installer manually.",
            [LangEs] = "No hay una URL de instalador configurada.\n\n" +
                       "Haz clic en Aceptar para abrir el sitio oficial de Wars of Liberty " +
                       "y descargar el instalador manualmente.",
        },

        // -------- Launcher self-update --------
        ["DlgLauncherUpdateTitle"] = new()
        {
            [LangEn] = "Launcher update available",
            [LangEs] = "Actualización del launcher disponible",
        },
        // Persistent title-bar pill that reminds the user a launcher update is
        // available on every launch (non-invasive replacement for the old
        // auto-modal). {0} = the available version/tag.
        ["LauncherUpdatePill"] = new()
        {
            [LangEn] = "Update {0}",
            [LangEs] = "Actualizar {0}",
        },
        ["LauncherUpdatePillTooltip"] = new()
        {
            [LangEn] = "A new launcher version is available — click to update.",
            [LangEs] = "Hay una nueva versión del launcher — haz clic para actualizar.",
        },
        ["DlgLauncherUpdateVersionInfo"] = new()
        {
            [LangEn] = "A new version of the launcher is available — " +
                       "current: {0}, new: {1} ({2}).",
            [LangEs] = "Hay una nueva versión del launcher disponible — " +
                       "actual: {0}, nueva: {1} ({2}).",
        },
        ["DlgLauncherUpdateConfirmPrompt"] = new()
        {
            [LangEn] = "Click DOWNLOAD to fetch the new version. " +
                       "You'll be asked to restart the launcher when it finishes.",
            [LangEs] = "Haz clic en DESCARGAR para obtener la nueva versión. " +
                       "Al terminar te pedirá reiniciar el launcher.",
        },
        ["DlgLauncherUpdateReadyToDownload"] = new()
        {
            [LangEn] = "Ready to download",
            [LangEs] = "Listo para descargar",
        },
        ["DlgLauncherUpdateDownloading"] = new()
        {
            [LangEn] = "Downloading...",
            [LangEs] = "Descargando...",
        },
        ["DlgLauncherUpdateDownloadComplete"] = new()
        {
            [LangEn] = "Download complete",
            [LangEs] = "Descarga completa",
        },
        ["DlgLauncherUpdateRestartPrompt"] = new()
        {
            [LangEn] = "The new version was downloaded. The launcher needs to restart to apply it.\n" +
                       "Click RESTART NOW to apply the update, or LATER to keep using this version " +
                       "(the update will apply next time you open the launcher).",
            [LangEs] = "Se descargó la nueva versión. El launcher necesita reiniciarse para aplicarla.\n" +
                       "Haz clic en REINICIAR AHORA para aplicarla, o MÁS TARDE para seguir usando esta " +
                       "versión (la actualización se aplicará la próxima vez que abras el launcher).",
        },
        ["DlgLauncherUpdateWhatsNew"] = new()
        {
            [LangEn] = "WHAT'S NEW",
            [LangEs] = "NOVEDADES",
        },
        ["DlgLauncherUpdateVerifyFailed"] = new()
        {
            [LangEn] = "Verification failed",
            [LangEs] = "Verificación fallida",
        },
        ["DlgLauncherUpdateVerifyFailedBody"] = new()
        {
            [LangEn] = "The downloaded update could not be verified (its checksum or " +
                       "signature didn't match what the release published). The file was " +
                       "discarded and your current launcher was left untouched. Please try " +
                       "again later or download the update manually from GitHub.",
            [LangEs] = "No se pudo verificar la actualización descargada (su checksum o firma " +
                       "no coincide con lo publicado en la release). El archivo se descartó y tu " +
                       "launcher actual quedó intacto. Inténtalo de nuevo más tarde o descarga la " +
                       "actualización manualmente desde GitHub.",
        },
        ["DlgLauncherUpdateOpenPage"] = new()
        {
            [LangEn] = "Download manually",
            [LangEs] = "Descargar manualmente",
        },
        ["DlgLauncherUpdateBtnDownload"] = new()
        {
            [LangEn] = "DOWNLOAD",
            [LangEs] = "DESCARGAR",
        },
        ["DlgLauncherUpdateBtnRestart"] = new()
        {
            [LangEn] = "RESTART NOW",
            [LangEs] = "REINICIAR AHORA",
        },
        ["DlgLauncherUpdateBtnRestartLater"] = new()
        {
            [LangEn] = "LATER",
            [LangEs] = "MÁS TARDE",
        },

        // -------- User-data backup (Documents\<mod>\) --------
        // These once belonged to a modal that interrupted every fresh install to offer
        // a backup; it was removed and backups are now ON DEMAND only (gear menu →
        // Create backup now, Properties → USER DATA). The keys the alert alone used are
        // gone with it — what remains is what those on-demand surfaces still show. The
        // `DlgUserDataAlert*` prefix is kept so git blame stays readable.
        ["DlgUserDataAlertBtnOpen"] = new()
        {
            [LangEn] = "Open folder",
            [LangEs] = "Abrir carpeta",
        },
        ["DlgUserDataAlertBackupFailedTitle"] = new()
        {
            [LangEn] = "Backup failed",
            [LangEs] = "Respaldo fallido",
        },
        ["DlgUserDataAlertBackupFailedBody"] = new()
        {
            [LangEn] = "Could not rename the user data folder. Make sure no other " +
                       "program (Explorer, the game, etc.) has it open and try again.",
            [LangEs] = "No se pudo renombrar la carpeta de datos. Verifica que " +
                       "ningún programa (Explorador, el juego, etc.) la tenga " +
                       "abierta e intenta de nuevo.",
        },
        ["StatusUserDataBackedUp"] = new()
        {
            [LangEn] = "User data backed up to: {0}",
            [LangEs] = "Datos respaldados en: {0}",
        },

        // -------- User data submenu --------
        ["MenuUserData"] = new()
        {
            [LangEn] = "User data",
            [LangEs] = "Datos de usuario",
        },
        ["MenuOpenUserDataFolder"] = new()
        {
            [LangEn] = "Open data folder",
            [LangEs] = "Abrir carpeta de datos",
        },
        ["MenuCreateBackupNow"] = new()
        {
            [LangEn] = "Create backup now",
            [LangEs] = "Crear respaldo ahora",
        },
        ["MenuRestoreUserData"] = new()
        {
            [LangEn] = "Restore backup...",
            [LangEs] = "Restaurar respaldo...",
        },

        // -------- Manual backup confirmation (gear menu → Create backup now) --------
        ["DlgBackupConfirmTitle"] = new()
        {
            [LangEn] = "Create backup?",
            [LangEs] = "¿Crear respaldo?",
        },
        ["DlgBackupConfirmBody"] = new()
        {
            [LangEn] = "Move the current Wars of Liberty user data to a backup " +
                       "folder named with today's timestamp?\n\n" +
                       "The game will create a fresh empty data folder the next " +
                       "time it runs.",
            [LangEs] = "¿Mover los datos actuales de Wars of Liberty a una " +
                       "carpeta de respaldo con la fecha de hoy?\n\n" +
                       "El juego creará una carpeta nueva vacía la próxima vez " +
                       "que se ejecute.",
        },
        ["DlgBackupNothingTitle"] = new()
        {
            [LangEn] = "Nothing to back up",
            [LangEs] = "Nada que respaldar",
        },
        ["DlgBackupNothingBody"] = new()
        {
            [LangEn] = "There is no Wars of Liberty user data to back up.",
            [LangEs] = "No hay datos de Wars of Liberty para respaldar.",
        },

        // -------- Restore dialog (styled list of backups) --------
        ["DlgRestoreDialogTitle"] = new()
        {
            [LangEn] = "Restore user data backup",
            [LangEs] = "Restaurar respaldo de datos",
        },
        ["DlgRestoreDialogHeader"] = new()
        {
            [LangEn] = "Pick a backup to restore",
            [LangEs] = "Elige un respaldo para restaurar",
        },
        ["DlgRestoreDialogDescriptionSingle"] = new()
        {
            [LangEn] = "Restoring will rename this backup back into place. Your " +
                       "current data will be saved as a new backup first, so you " +
                       "can swap back any time.",
            [LangEs] = "Al restaurar, este respaldo vuelve a ser la carpeta " +
                       "activa. Tus datos actuales se guardarán como un respaldo " +
                       "nuevo primero, así puedes volver cuando quieras.",
        },
        ["DlgRestoreDialogDescriptionMultiple"] = new()
        {
            [LangEn] = "We found {0} backups. Pick one to restore — your current " +
                       "data will be saved as a new backup first.",
            [LangEs] = "Encontramos {0} respaldos. Elige uno para restaurar — " +
                       "tus datos actuales se guardarán como un respaldo nuevo " +
                       "primero.",
        },
        ["DlgRestoreDialogListLabel"] = new()
        {
            [LangEn] = "AVAILABLE BACKUPS",
            [LangEs] = "RESPALDOS DISPONIBLES",
        },
        ["DlgRestoreDialogReassurance"] = new()
        {
            [LangEn] = "ⓘ Nothing is deleted. Your current data and any unselected " +
                       "backups stay on disk — you can manage them later via Explorer.",
            [LangEs] = "ⓘ No se elimina nada. Tus datos actuales y los respaldos " +
                       "no seleccionados quedan en disco — puedes gestionarlos " +
                       "después desde el Explorador.",
        },
        ["DlgRestoreDialogBtnRestore"] = new()
        {
            [LangEn] = "Restore selected",
            [LangEs] = "Restaurar seleccionado",
        },
        ["DlgRestoreDialogRowDetail"] = new()
        {
            [LangEn] = "{0} files",
            [LangEs] = "{0} archivos",
        },
        ["DlgRestoreDialogRowDetailWithSaves"] = new()
        {
            [LangEn] = "{0} files  ·  {1} savegames in Savegame\\",
            [LangEs] = "{0} archivos  ·  {1} partidas en Savegame\\",
        },

        ["DlgRestoreNoBackupsTitle"] = new()
        {
            [LangEn] = "No backups found",
            [LangEs] = "Sin respaldos",
        },
        ["DlgRestoreNoBackupsBody"] = new()
        {
            [LangEn] = "There are no user data backups to restore. Backups are " +
                       "created when you choose 'Back up and continue' in the " +
                       "previous-data alert after a fresh install.",
            [LangEs] = "No hay respaldos de datos para restaurar. Los respaldos " +
                       "se crean cuando eliges 'Respaldar y continuar' en la " +
                       "alerta de datos previos después de una instalación nueva.",
        },
        ["DlgRestoreFailedTitle"] = new()
        {
            [LangEn] = "Restore failed",
            [LangEs] = "Restauración fallida",
        },
        ["DlgRestoreFailedBody"] = new()
        {
            [LangEn] = "Could not restore the backup:\n\n{0}\n\n" +
                       "Make sure no program (Explorer, the game, etc.) has " +
                       "the folder open, and try again.",
            [LangEs] = "No se pudo restaurar el respaldo:\n\n{0}\n\n" +
                       "Verifica que ningún programa (Explorador, el juego, " +
                       "etc.) tenga la carpeta abierta e intenta de nuevo.",
        },
        ["StatusRestoreSuccess"] = new()
        {
            [LangEn] = "Restored backup '{0}'.",
            [LangEs] = "Respaldo '{0}' restaurado.",
        },
        ["StatusRestoreSuccessWithSnapshot"] = new()
        {
            [LangEn] = "Restored '{0}'. Your previous data was saved as '{1}'.",
            [LangEs] = "Restaurado '{0}'. Tus datos previos fueron guardados como '{1}'.",
        },

        // -------- Settings menu --------
        ["TooltipSettings"] = new()
        {
            [LangEn] = "Settings",
            [LangEs] = "Configuración",
        },
        ["MenuFolders"] = new()
        {
            [LangEn] = "Folders",
            [LangEs] = "Carpetas",
        },
        // Replaces "Folders" — the submenu is now for path settings, not
        // path opening (the sidebar's "Open folder" button covers the
        // common case for the active mod).
        ["MenuManagePaths"] = new()
        {
            [LangEn] = "Manage paths",
            [LangEs] = "Administrar rutas",
        },
        ["MenuOpenAoE3Folder"] = new()
        {
            [LangEn] = "Open Age of Empires III folder",
            [LangEs] = "Abrir carpeta de Age of Empires III",
        },
        // {0} = mod display name.
        ["MenuSelectModFolder"] = new()
        {
            [LangEn] = "Select {0} folder...",
            [LangEs] = "Seleccionar carpeta de {0}...",
        },
        ["MenuSelectAoE3Folder"] = new()
        {
            [LangEn] = "Select Age of Empires III folder...",
            [LangEs] = "Seleccionar carpeta de Age of Empires III...",
        },
        ["MenuCheckForUpdates"] = new()
        {
            [LangEn] = "Check for updates",
            [LangEs] = "Buscar actualizaciones",
        },
        ["MenuGameLanguage"] = new()
        {
            [LangEn] = "Game language",
            [LangEs] = "Idioma del juego",
        },
        ["MenuLangEnglish"] = new()
        {
            [LangEn] = "English (default)",
            [LangEs] = "Inglés (predeterminado)",
        },
        ["MenuLangRefresh"] = new()
        {
            [LangEn] = "Refresh list",
            [LangEs] = "Actualizar lista",
        },
        ["MenuLangRefreshing"] = new()
        {
            [LangEn] = "Refreshing...",
            [LangEs] = "Actualizando...",
        },
        ["MenuLangNoneAvailable"] = new()
        {
            [LangEn] = "No translations available yet",
            [LangEs] = "Aún no hay traducciones disponibles",
        },
        // Shown next to each translation in the menu. {0} is the comma-
        // separated list of mod versions from compatibleWith — that's what
        // users care about ("does this work with my mod version?").
        ["MenuLangModVersionLabel"] = new()
        {
            [LangEn] = "(mod {0})",
            [LangEs] = "(mod {0})",
        },
        // -------- Translator packaging dialog --------
        // (The "Package my translation..." Game-language menu entry was
        // retired — the packager lives in Launcher Settings → Translations
        // now, where it's globalised across mods. Strings.MenuLangPackager
        // was deleted with it.)
        ["DlgPackagerTitle"] = new()
        {
            [LangEn] = "Package translation",
            [LangEs] = "Empaquetar traducción",
        },
        ["DlgPackagerSectionMod"] = new()
        {
            [LangEn] = "TARGET MOD",
            [LangEs] = "MOD DE DESTINO",
        },
        ["DlgPackagerSectionIdentity"] = new()
        {
            [LangEn] = "PACK IDENTITY",
            [LangEs] = "IDENTIDAD DEL PAQUETE",
        },
        ["DlgPackagerSectionSource"] = new()
        {
            [LangEn] = "SOURCE FILES",
            [LangEs] = "ARCHIVOS DE ORIGEN",
        },
        ["DlgPackagerSectionCompat"] = new()
        {
            [LangEn] = "COMPATIBILITY",
            [LangEs] = "COMPATIBILIDAD",
        },
        ["DlgPackagerSectionOutput"] = new()
        {
            [LangEn] = "OUTPUT",
            [LangEs] = "SALIDA",
        },
        ["DlgPackagerFieldMod"] = new()
        {
            [LangEn] = "MOD TO TRANSLATE",
            [LangEs] = "MOD A TRADUCIR",
        },
        ["DlgPackagerHintMod"] = new()
        {
            [LangEn] = "Drives the originals snapshot, compatibility version and default output filename.",
            [LangEs] = "Define el snapshot de originales, la versión compatible y el nombre por defecto del archivo de salida.",
        },
        ["DlgPackagerModNotInstalled"] = new()
        {
            [LangEn] = "not installed",
            [LangEs] = "sin instalar",
        },
        ["DlgPackagerErrorNoMod"] = new()
        {
            [LangEn] = "Pick a mod from the list before packaging.",
            [LangEs] = "Elige un mod en la lista antes de empaquetar.",
        },
        ["DlgPackagerHeader"] = new()
        {
            [LangEn] = "Build a translation pack",
            [LangEs] = "Crear un paquete de traducción",
        },
        ["DlgPackagerDescription"] = new()
        {
            [LangEn] = "This generates a ready-to-publish .zip from a folder of " +
                       "translated XML files. The launcher computes the hashes " +
                       "and manifest automatically.",
            [LangEs] = "Genera un .zip listo para publicar a partir de una carpeta " +
                       "con archivos XML traducidos. El launcher calcula los hashes " +
                       "y el manifest automáticamente.",
        },
        ["DlgPackagerFieldId"] = new()
        {
            [LangEn] = "LANGUAGE ID",
            [LangEs] = "ID DEL IDIOMA",
        },
        ["DlgPackagerHintId"] = new()
        {
            [LangEn] = "Short identifier — e.g. \"es\", \"fr\", \"pt-br\"",
            [LangEs] = "Identificador corto — ej. \"es\", \"fr\", \"pt-br\"",
        },
        ["DlgPackagerFieldName"] = new()
        {
            [LangEn] = "DISPLAY NAME",
            [LangEs] = "NOMBRE VISIBLE",
        },
        ["DlgPackagerFieldAuthor"] = new()
        {
            [LangEn] = "AUTHOR / HANDLE",
            [LangEs] = "AUTOR / NOMBRE DE USUARIO",
        },
        ["DlgPackagerFieldVersion"] = new()
        {
            [LangEn] = "TRANSLATION VERSION  (e.g. 1.0)",
            [LangEs] = "VERSIÓN DE LA TRADUCCIÓN  (ej: 1.0)",
        },
        ["DlgPackagerHintVersion"] = new()
        {
            [LangEn] = "Version of YOUR translation pack — bump this when you " +
                       "publish changes (1.0 → 1.1 → 1.2...). NOT the mod version " +
                       "— that goes in the 'Compatibility' field below.",
            [LangEs] = "Versión de TU paquete de traducción — súbela al publicar " +
                       "cambios (1.0 → 1.1 → 1.2...). NO es la versión del mod " +
                       "— eso va en el campo 'Compatibilidad' abajo.",
        },
        ["DlgPackagerVersionLooksLikeMod"] = new()
        {
            [LangEn] = "⚠ \"{0}\" looks like a mod version. The translation version is " +
                       "yours — start with 1.0 and bump it on each release.",
            [LangEs] = "⚠ \"{0}\" parece una versión del mod. La versión de la " +
                       "traducción es tuya — empieza con 1.0 y súbela en cada release.",
        },
        ["DlgPackagerFieldFolder"] = new()
        {
            [LangEn] = "TRANSLATED XML FILES",
            [LangEs] = "ARCHIVOS XML TRADUCIDOS",
        },
        ["DlgPackagerHintFolder"] = new()
        {
            [LangEn] = "Pick your translated stringtabley.xml and/or unithelpstringsy.xml. The file name can differ (e.g. stringtabley_translated.xml) — it's matched automatically.",
            [LangEs] = "Selecciona tu stringtabley.xml y/o unithelpstringsy.xml traducidos. El nombre del archivo puede ser distinto (ej. stringtabley_translated.xml) — se detecta automáticamente.",
        },
        ["DlgPackagerFieldOriginals"] = new()
        {
            [LangEn] = "ORIGINAL ENGLISH XML FILES",
            [LangEs] = "ARCHIVOS XML ORIGINALES EN INGLÉS",
        },
        ["DlgPackagerHintOriginals"] = new()
        {
            [LangEn] = "The same files as above but the ENGLISH versions. The file name can " +
                       "differ — it's matched automatically. Auto-filled from the launcher's snapshot when available.",
            [LangEs] = "Los mismos archivos de arriba pero en INGLÉS. El nombre puede ser distinto " +
                       "— se detecta automáticamente. Se auto-completa con el respaldo del launcher si está disponible.",
        },
        ["DlgPackagerFieldCompat"] = new()
        {
            [LangEn] = "MOD COMPATIBILITY",
            [LangEs] = "COMPATIBILIDAD CON EL MOD",
        },
        ["DlgPackagerCompatCurrent"] = new()
        {
            [LangEn] = "Compatible with current mod version ({0})",
            [LangEs] = "Compatible con la versión actual del mod ({0})",
        },
        ["DlgPackagerHintCompatExtra"] = new()
        {
            [LangEn] = "Extra versions, comma-separated (optional)",
            [LangEs] = "Otras versiones, separadas por coma (opcional)",
        },
        ["DlgPackagerFieldOutput"] = new()
        {
            [LangEn] = "OUTPUT .ZIP FILE",
            [LangEs] = "ARCHIVO .ZIP DE SALIDA",
        },
        ["DlgPackagerBtnGenerate"] = new()
        {
            [LangEn] = "Generate package",
            [LangEs] = "Generar paquete",
        },
        // Errors
        ["DlgPackagerErrorIdMissing"] = new()
        {
            [LangEn] = "Language ID is required (e.g. \"es\").",
            [LangEs] = "El ID del idioma es obligatorio (ej. \"es\").",
        },
        ["DlgPackagerErrorNameMissing"] = new()
        {
            [LangEn] = "Display name is required.",
            [LangEs] = "El nombre visible es obligatorio.",
        },
        ["DlgPackagerErrorVersionMissing"] = new()
        {
            [LangEn] = "Pack version is required.",
            [LangEs] = "La versión del paquete es obligatoria.",
        },
        ["DlgPackagerErrorFolderMissing"] = new()
        {
            [LangEn] = "The translated-files folder doesn't exist.",
            [LangEs] = "La carpeta con los archivos traducidos no existe.",
        },
        ["DlgPackagerErrorOutputMissing"] = new()
        {
            [LangEn] = "Output .zip path is required.",
            [LangEs] = "Falta la ruta del .zip de salida.",
        },
        ["DlgPackagerErrorNoCompat"] = new()
        {
            [LangEn] = "Specify at least one compatible mod version.",
            [LangEs] = "Indica al menos una versión del mod compatible.",
        },
        // Result
        ["DlgPackagerResultHeader"] = new()
        {
            [LangEn] = "Package created",
            [LangEs] = "Paquete creado",
        },
        ["DlgPackagerResultFolderPath"] = new()
        {
            [LangEn] = "📁 Ready to commit: {0}",
            [LangEs] = "📁 Listo para commitear: {0}",
        },
        ["DlgPackagerResultPath"] = new()
        {
            [LangEn] = "📦 {0} ({1})",
            [LangEs] = "📦 {0} ({1})",
        },
        ["DlgPackagerResultJsonPath"] = new()
        {
            [LangEn] = "📄 {0}",
            [LangEs] = "📄 {0}",
        },
        ["DlgPackagerResultInstructions"] = new()
        {
            [LangEn] = "ⓘ How to publish (pick one):\n" +
                       "• Folder (recommended): commit the translations/ folder shown above " +
                       "to github.com/{0} (push to main or open a PR). It's the ready " +
                       "translations/<id>/<version>/ layout, so each export adds a new " +
                       "version to the history — old ones stay.\n" +
                       "• Release (legacy): create a release and upload the .zip + " +
                       "translation.json as assets.\n" +
                       "Either way, players see it the next time the launcher refreshes its list.",
            [LangEs] = "ⓘ Cómo publicar (elige una):\n" +
                       "• Carpeta (recomendado): commitea la carpeta translations/ de arriba " +
                       "a github.com/{0} (push a main o abre un PR). Ya viene con el layout " +
                       "translations/<id>/<version>/, así cada export agrega una versión " +
                       "nueva al historial — las viejas quedan.\n" +
                       "• Release (legacy): crea una release y sube el .zip + " +
                       "translation.json como assets.\n" +
                       "En cualquier caso, los jugadores la verán la próxima vez que el " +
                       "launcher refresque la lista.",
        },
        ["DlgPackagerFieldDescription"] = new()
        {
            [LangEn] = "Description / changelog (optional)",
            [LangEs] = "Descripción / cambios (opcional)",
        },
        ["DlgPackagerHintDescription"] = new()
        {
            [LangEn] = "Shown when players pick this pack. e.g. what's translated, or what changed in this version.",
            [LangEs] = "Se muestra cuando los jugadores eligen este pack. Ej.: qué está traducido o qué cambió en esta versión.",
        },
        ["DlgPackagerResultPreviewLabel"] = new()
        {
            [LangEn] = "MENU PREVIEW (how players will see it):",
            [LangEs] = "VISTA PREVIA EN EL MENÚ (así lo verán los jugadores):",
        },
        ["DlgPackagerBtnOpenFolder"] = new()
        {
            [LangEn] = "Open folder",
            [LangEs] = "Abrir carpeta",
        },
        ["DlgPackagerBtnDone"] = new()
        {
            [LangEn] = "Done",
            [LangEs] = "Listo",
        },

        // Game-language status / dialogs
        ["StatusLangIndexLoaded"] = new()
        {
            [LangEn] = "Translation list loaded ({0} available).",
            [LangEs] = "Lista de traducciones cargada ({0} disponibles).",
        },
        ["StatusLangIndexUnavailable"] = new()
        {
            [LangEn] = "Translation list is currently unavailable.",
            [LangEs] = "La lista de traducciones no está disponible.",
        },
        ["StatusLangApplied"] = new()
        {
            [LangEn] = "✓ {0} translation applied.",
            [LangEs] = "✓ Traducción {0} aplicada.",
        },
        ["StatusLangRevertedToEnglish"] = new()
        {
            [LangEn] = "✓ Reverted game language to English.",
            [LangEs] = "✓ Idioma del juego restablecido a inglés.",
        },
        ["DlgLangApplyTitle"] = new()
        {
            [LangEn] = "Apply translation",
            [LangEs] = "Aplicar traducción",
        },
        ["DlgLangApplyByAuthor"] = new()
        {
            [LangEn] = "by {0}",
            [LangEs] = "por {0}",
        },
        ["DlgLangApplyModVersionsLabel"] = new()
        {
            [LangEn] = "MOD VERSIONS",
            [LangEs] = "VERSIONES DEL MOD",
        },
        ["DlgLangApplySizeLabel"] = new()
        {
            [LangEn] = "DOWNLOAD SIZE",
            [LangEs] = "TAMAÑO",
        },
        // Heading over the translator's free-form description in the apply dialog.
        ["DlgLangApplyDescriptionLabel"] = new()
        {
            [LangEn] = "📝 Translator's note",
            [LangEs] = "📝 Nota del traductor",
        },
        ["DlgLangApplyCompatOk"] = new()
        {
            [LangEn] = "Compatible with your installed mod (v{0})",
            [LangEs] = "Compatible con tu instalación del mod (v{0})",
        },
        ["DlgLangApplyCompatOkNoVer"] = new()
        {
            [LangEn] = "Compatible with your mod",
            [LangEs] = "Compatible con tu mod",
        },
        ["DlgLangApplyCompatWarn"] = new()
        {
            [LangEn] = "Made for mod {1}, but you have {0}. Some text may stay in English, and it can cause multiplayer sync problems (out-of-sync) with other players.",
            [LangEs] = "Hecha para mod {1}, pero tienes {0}. Algunos textos pueden quedar en inglés y puede causar problemas de sincronización en multijugador (desincronización) con otros jugadores.",
        },
        ["DlgLangApplyDownloading"] = new()
        {
            [LangEn] = "Downloading translation pack...",
            [LangEs] = "Descargando paquete de traducción...",
        },
        ["DlgLangApplyInstalling"] = new()
        {
            [LangEn] = "Extracting pack...",
            [LangEs] = "Extrayendo paquete...",
        },
        ["DlgLangApplyApplying"] = new()
        {
            [LangEn] = "Applying translation files...",
            [LangEs] = "Aplicando archivos de traducción...",
        },
        ["DlgLangApplyBtnApply"] = new()
        {
            [LangEn] = "Apply",
            [LangEs] = "Aplicar",
        },
        ["DlgLangApplyBtnForce"] = new()
        {
            [LangEn] = "Apply anyway",
            [LangEs] = "Aplicar igual",
        },
        ["DlgLangApplyFailedBodyDetail"] = new()
        {
            [LangEn] = "Could not apply the translation:\n\n{0}",
            [LangEs] = "No se pudo aplicar la traducción:\n\n{0}",
        },
        ["DlgLangIncompatibleBody"] = new()
        {
            [LangEn] = "This translation was made for a different version of the mod. " +
                       "Some text may be wrong, missing, or stay in English — and it can cause " +
                       "MULTIPLAYER SYNC PROBLEMS (version mismatch / out-of-sync) with other players. " +
                       "You can use it anyway at your own risk.\n\nApply anyway?",
            [LangEs] = "Esta traducción se hizo para una versión diferente del mod. " +
                       "Algunos textos pueden quedar incorrectos, faltantes o en inglés, y puede causar " +
                       "PROBLEMAS DE SINCRONIZACIÓN EN MULTIJUGADOR (versión distinta / desincronización) con otros jugadores. " +
                       "Puedes usarla igual bajo tu propia responsabilidad.\n\n¿Aplicar igual?",
        },
        ["DlgLangApplyFailedTitle"] = new()
        {
            [LangEn] = "Could not apply translation",
            [LangEs] = "No se pudo aplicar la traducción",
        },
        ["DlgLangApplyFailedBody"] = new()
        {
            [LangEn] = "The translation could not be applied:\n\n{0}",
            [LangEs] = "No se pudo aplicar la traducción:\n\n{0}",
        },
        ["DlgLangNoDownloadUrlBody"] = new()
        {
            [LangEn] = "This translation entry has no download URL configured.",
            [LangEs] = "Esta entrada de traducción no tiene URL de descarga configurada.",
        },
        ["DlgLangRevertFailedBody"] = new()
        {
            [LangEn] = "Could not revert to English — the original snapshot is missing. " +
                       "Run Verify files to repair the install.",
            [LangEs] = "No se pudo volver al inglés — el respaldo del original no existe. " +
                       "Ejecuta Verificar archivos para reparar la instalación.",
        },

        // -------- Settings menu tooltips --------
        // Hover help so the user knows what each option does without clicking.
        ["TooltipSettingsBody"] = new()
        {
            [LangEn] = "Manage folders, user data, and run health checks",
            [LangEs] = "Gestionar carpetas, datos de usuario y revisar el estado del mod",
        },
        ["TooltipMenuOpenAoE3Folder"] = new()
        {
            [LangEn] = "Open the Age of Empires III install folder in Windows Explorer",
            [LangEs] = "Abrir la carpeta del juego base en el Explorador de Windows",
        },
        // {0} = mod display name.
        ["TooltipMenuSelectModFolder"] = new()
        {
            [LangEn] = "Manually point the launcher at an existing {0} install if auto-detection failed",
            [LangEs] = "Indicar manualmente dónde está {0} si la detección automática falló",
        },
        ["TooltipMenuSelectAoE3Folder"] = new()
        {
            [LangEn] = "Manually point the launcher at Age of Empires III if it wasn't detected",
            [LangEs] = "Indicar manualmente dónde está Age of Empires III si no se detectó",
        },
        ["TooltipMenuOpenUserDataFolder"] = new()
        {
            [LangEn] = "View your savegames, custom home cities and game settings",
            [LangEs] = "Ver tus partidas guardadas, metrópolis y configuración del juego",
        },
        ["TooltipMenuCreateBackupNow"] = new()
        {
            [LangEn] = "Move your current data to a timestamped backup. The game will start fresh next time",
            [LangEs] = "Mover tus datos actuales a un respaldo con la fecha. El juego empezará limpio",
        },
        ["TooltipMenuRestoreUserData"] = new()
        {
            [LangEn] = "Restore an earlier backup. Your current data is automatically backed up first",
            [LangEs] = "Volver a una versión anterior de tus partidas. Los datos actuales se respaldan primero",
        },
        ["TooltipMenuCheckForUpdates"] = new()
        {
            [LangEn] = "Ask the server whether new patches are available",
            [LangEs] = "Consultar al servidor si hay parches nuevos disponibles",
        },
        ["TooltipMenuVerifyFiles"] = new()
        {
            [LangEn] = "Check the integrity of the mod's files and repair anything missing or corrupt",
            [LangEs] = "Revisar la integridad de los archivos del mod y reparar archivos dañados o faltantes",
        },
        ["TooltipMenuUninstall"] = new()
        {
            [LangEn] = "Remove Wars of Liberty from this computer. Age of Empires III is not affected",
            [LangEs] = "Eliminar Wars of Liberty de tu PC. No afecta a Age of Empires III",
        },
        ["DlgOpenFolderNotFoundTitle"] = new()
        {
            [LangEn] = "Folder not found",
            [LangEs] = "Carpeta no encontrada",
        },
        // {0} = mod display name (appears twice).
        ["DlgOpenAoE3NotFoundBody"] = new()
        {
            [LangEn] = "Age of Empires III is not detected. " +
                       "Use 'Select Age of Empires III folder' to point the launcher at it.",
            [LangEs] = "No se detectó Age of Empires III. " +
                       "Usa 'Seleccionar carpeta de Age of Empires III' para indicar dónde está.",
        },

        // -------- AoE3 folder browse --------
        ["BrowseAoE3Button"] = new()
        {
            [LangEn] = "Select AoE3 folder...",
            [LangEs] = "Seleccionar carpeta de AoE3...",
        },
        ["LblGamePath"] = new()
        {
            [LangEn] = "AGE OF EMPIRES III",
            [LangEs] = "AGE OF EMPIRES III",
        },
        ["DlgAoE3FolderPickerTitle"] = new()
        {
            [LangEn] = "Select Age of Empires III folder",
            [LangEs] = "Seleccionar carpeta de Age of Empires III",
        },
        ["DlgInvalidAoE3FolderTitle"] = new()
        {
            [LangEn] = "Invalid folder",
            [LangEs] = "Carpeta no válida",
        },
        ["DlgInvalidAoE3FolderBody"] = new()
        {
            [LangEn] = "Could not find 'age3y.exe' in the selected folder.\n\n" +
                       "Please select the Age of Empires III installation folder " +
                       "(the one that contains age3y.exe or has a 'bin' subfolder with it).",
            [LangEs] = "No se encontró 'age3y.exe' en la carpeta seleccionada.\n\n" +
                       "Selecciona la carpeta de instalación de Age of Empires III " +
                       "(la que contiene age3y.exe o tiene una subcarpeta 'bin' con él).",
        },
        ["StatusAoE3Configured"] = new()
        {
            [LangEn] = "Age of Empires III configured successfully.",
            [LangEs] = "Age of Empires III configurado correctamente.",
        },
    };
}
