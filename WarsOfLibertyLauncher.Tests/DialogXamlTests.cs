using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WarsOfLibertyLauncher;
using WarsOfLibertyLauncher.Models;
using WarsOfLibertyLauncher.Models.Multiplayer;
using WarsOfLibertyLauncher.Controls;
using WarsOfLibertyLauncher.Localization;
using WarsOfLibertyLauncher.Services;
using WarsOfLibertyLauncher.Services.Multiplayer;
using Xunit;

namespace WarsOfLibertyLauncher.Tests;

/// <summary>
/// Constructs the multiplayer windows that the startup smoke test never reaches.
///
/// <para>A green build does not prove a window loads: a <c>{StaticResource}</c> that
/// fails to resolve throws when the XAML is PARSED, and these windows are only parsed
/// once someone signs in and opens a room. The launcher's smoke test opens MainWindow
/// and nothing else, so a broken resource key here would ship unseen — which is exactly
/// the class of bug that has bitten this repo before (RadiusMd).</para>
///
/// <para>The resource dictionaries are merged by EXPLICIT pack:// URIs: App.xaml's own
/// Source values are relative and resolve against the entry assembly, which under a test
/// host is the test runner, not the launcher.</para>
/// </summary>
/// <summary>
/// The classes that build WPF trees and switch the launcher's language, run one at a time.
///
/// <para><b>Two process-wide things, not one.</b> <see cref="TestApplication"/> already holds
/// the single <c>Application</c> and says why in its own header - but its merged dictionaries
/// are filled once and read by everybody, and <c>Strings.Language</c> is a static that a test
/// sets, reads and restores. xUnit runs test classes in parallel, so a class asserting on a
/// Spanish caption can be reading it in the microsecond another class spends in English.</para>
///
/// <para><b>It fails in the wrong place, which is why this is a collection and not a lock.</b>
/// The failure lands in whichever unrelated test was mid-assertion - "the status cell is gone"
/// from the entrant-row width test, or a NullReferenceException walking a template - and moves
/// between runs. Serialising the three of them is the only fix that makes the symptom
/// impossible rather than rare.</para>
/// </summary>
[CollectionDefinition("wpf-and-language", DisableParallelization = true)]
public class WpfAndLanguageCollection { }

[Collection("wpf-and-language")]
public class DialogXamlTests
{
    [Fact]
    public void CreateLobbyDialog_LoadsItsXaml()
    {
        var error = RunOnStaThread(() =>
        {
            var session = new MultiplayerSession(new LauncherConfig());
            var dlg = new CreateLobbyDialog(
                session,
                new List<ModProfile>(),
                null,
                _ => Task.FromResult("0123456789abcdef"),
                _ => new ModCopyInfo(false, false, Array.Empty<ModCopyChoice>()),
                _ => Task.CompletedTask);
            // Touching a named element proves the tree was really built, not just that
            // the constructor returned.
            Assert.NotNull(dlg.CreateButton);

            // The format row is ALWAYS on screen now — it used to be revealed by the tick, which
            // made one decision take two steps and jumped the dialog's height.
            Assert.NotNull(dlg.CompetitiveFormatRow);
            Assert.Equal(Visibility.Visible, dlg.CompetitiveFormatRow.Visibility);
            Assert.Equal(3, dlg.FormatRow.Children.Count);   // 1v1 / 2v2 / 3v3

            // AND NOTHING IS LIT, which is the invariant that being visible put at risk. A
            // casual room has declared no format, and showing 1v1 highlighted would both
            // contradict the "Max players: 8" just above it and assert the one thing this model
            // refuses to assert — that a two-seat casual room IS a 1v1 (see RoomFormats).
            Assert.All(dlg.FormatRow.Children.OfType<Button>(),
                b => Assert.Null(b.Tag as string));
            Assert.NotNull(dlg.MaxPlayersRow);
            Assert.True(dlg.MaxPlayersRow.Children.Count > 0);

            // And the note under it says nothing until a format is chosen — it carries either
            // the 1v1 forfeit clause or the team "does not rate yet" line, and neither applies
            // to a casual room.
            Assert.Equal(Visibility.Collapsed, dlg.CompetitiveSizeNote.Visibility);

            // And a casual room still opens at the full eight seats. The format row picks a
            // format at construction — so that ticking the box lands on something rather than on
            // nothing — and that pick moves the size, so without the competitive guard on it
            // every room would have quietly opened as a two-player one. Making the row visible
            // did not touch that guard, and this is what proves it.
            var active = dlg.MaxPlayersRow.Children.OfType<Button>()
                .Where(b => (b.Tag as string) == "active")
                .Select(b => b.Content as string)
                .ToList();
            Assert.Equal(new[] { "8" }, active);
            Assert.All(dlg.MaxPlayersRow.Children.OfType<Button>(), b => Assert.True(b.IsEnabled));

            dlg.Close();
        });

        Assert.Null(error);
    }

    /// <summary>
    /// PICKING A FORMAT DECLARES THE ROOM COMPETITIVE — the whole point of the row being on
    /// screen for a casual room, and the half that compiles perfectly while doing nothing.
    ///
    /// <para>The format only means something for a competitive room, so a click that lit a
    /// segment and left the box unticked would be a control that looks broken: you would pick
    /// 2v2, watch nothing else move, and still have to go find the checkbox. One click has to
    /// carry the whole decision — the box, the format, and the four seats that format IS.</para>
    ///
    /// <para>Asserted through the real Click event rather than by calling the handler, because
    /// what is being pinned is the wiring: the handler could be perfect and simply not attached.
    /// </para>
    /// </summary>
    [Fact]
    public void PickingAFormatMakesTheRoomCompetitive()
    {
        var error = RunOnStaThread(() =>
        {
            var session = new MultiplayerSession(new LauncherConfig());
            var dlg = new CreateLobbyDialog(
                session,
                new List<ModProfile>(),
                null,
                _ => Task.FromResult("0123456789abcdef"),
                _ => new ModCopyInfo(false, false, Array.Empty<ModCopyChoice>()),
                _ => Task.CompletedTask);

            // Starts casual and eight-seat, as the test above pins in detail.
            Assert.NotEqual(true, dlg.CompetitiveCheck.IsChecked);

            // 1v1 / 2v2 / 3v3, in that order — so this is 2v2, chosen because its four seats
            // differ from BOTH the eight the room opens on and the two a 1v1 would give: a
            // handler that did nothing, and one that fell back to the default format, both fail.
            var twoVTwo = dlg.FormatRow.Children.OfType<Button>().ElementAt(1);
            twoVTwo.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.True(dlg.CompetitiveCheck.IsChecked);
            Assert.Equal("active", twoVTwo.Tag as string);

            var active = dlg.MaxPlayersRow.Children.OfType<Button>()
                .Where(b => (b.Tag as string) == "active")
                .Select(b => b.Content as string)
                .ToList();
            Assert.Equal(new[] { "4" }, active);

            // The seat row belongs to the format now, and the note says what a team match needs.
            Assert.All(dlg.MaxPlayersRow.Children.OfType<Button>(), b => Assert.False(b.IsEnabled));
            Assert.Equal(Visibility.Visible, dlg.CompetitiveSizeNote.Visibility);

            // Unticking gives the seats back and leaves NO format showing — the room stopped
            // being one that has a format, so the row must stop claiming it has one.
            dlg.CompetitiveCheck.IsChecked = false;
            Assert.All(dlg.FormatRow.Children.OfType<Button>(), b => Assert.Null(b.Tag as string));
            Assert.All(dlg.MaxPlayersRow.Children.OfType<Button>(), b => Assert.True(b.IsEnabled));
            Assert.Equal(Visibility.Collapsed, dlg.CompetitiveSizeNote.Visibility);

            dlg.Close();
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The Create button must not inherit the ghost button's hover.
    ///
    /// <para>It did, and it took three reports to find. `MpFooterPrimary` is BasedOn
    /// `MpFooterGhost`, so it inherited the ghost's ControlTemplate — whose IsMouseOver
    /// trigger painted the template's Border BY NAME with `MpRowHighlight`. A trigger that
    /// targets a template element by name cannot be overridden by a derived style, so
    /// pointing at the filled blue Create button replaced its fill with #16263E: the
    /// primary action went dark at the exact moment the user was about to click it. It
    /// reads as "the interface eats the buttons", which is what it was reported as.</para>
    ///
    /// <para>The FIRST assertion is the general lesson — no state colour hardcoded by
    /// TargetName in a template other styles build on. The second pins this outcome.</para>
    /// </summary>
    [Fact]
    public void CreateButton_DoesNotInheritTheGhostButtonsHover()
    {
        var error = RunOnStaThread(() =>
        {
            var session = new MultiplayerSession(new LauncherConfig());
            var dlg = new CreateLobbyDialog(
                session,
                new List<ModProfile>(),
                null,
                _ => Task.FromResult("0123456789abcdef"),
                _ => new ModCopyInfo(false, false, Array.Empty<ModCopyChoice>()),
                _ => Task.CompletedTask);

            var button = dlg.CreateButton;
            Assert.NotNull(button.Template);

            // (1) Nothing in the shared template may paint a state by TargetName: that is
            //     the setter shape a derived style is powerless against.
            foreach (var trigger in button.Template.Triggers.OfType<Trigger>())
                foreach (var setter in trigger.Setters.OfType<Setter>())
                    Assert.True(string.IsNullOrEmpty(setter.TargetName),
                        $"Template trigger on {trigger.Property?.Name} sets {setter.Property?.Name} " +
                        $"through TargetName='{setter.TargetName}' — a derived style cannot override it.");

            // (2) The hover that actually applies is the primary's blue. Triggers merge
            //     base-first down the BasedOn chain and the last setter for a property wins,
            //     so walking the chain in that order leaves the effective one.
            var chain = new List<Style>();
            for (var style = button.Style; style != null; style = style.BasedOn)
                chain.Insert(0, style);

            object? hoverBackground = null;
            foreach (var style in chain)
                foreach (var trigger in style.Triggers.OfType<Trigger>())
                    if (trigger.Property == UIElement.IsMouseOverProperty)
                        foreach (var setter in trigger.Setters.OfType<Setter>())
                            if (setter.Property == Control.BackgroundProperty)
                                hoverBackground = setter.Value;

            var key = (hoverBackground as DynamicResourceExtension)?.ResourceKey as string;
            Assert.Equal("MpBlueHover", key);

            dlg.Close();
        });

        Assert.Null(error);
    }

    [Fact]
    public void LobbyWindow_LoadsItsXaml()
    {
        // The window with the most to lose: it is only ever parsed once someone signs in
        // AND enters a room, so nothing in the automated verification touches it. Both
        // remaining handoff screens (the lobby itself and the in-match panel) rewrite it.
        var error = RunOnStaThread(() =>
        {
            var window = new LobbyWindow(new MultiplayerSession(new LauncherConfig()));
            Assert.NotNull(window.StartButton);
            Assert.NotNull(window.MatchResultOverlay);

            // ApplyMatchPhaseUi COLLAPSES this container for the InGame and Result phases
            // so the lobby underneath cannot leak around the opaque overlays. Rename it and
            // that hiding stops happening SILENTLY — the overlays still show, the lobby
            // still peeks out at the edges, and nothing fails. This is the tripwire.
            Assert.NotNull(window.LobbyLeftColumn);
            Assert.NotNull(window.InGameOverlay);

            // The "before you start" checklist. The third item states the abandonment
            // penalty, which is the ONLY place a guest can read it — the create-room
            // dialog that spells it out is seen by the host alone. It starts Collapsed
            // and RefreshPreflightChecklist shows it for a competitive room, so a rename
            // here would leave every guest reading nothing, silently.
            Assert.NotNull(window.PreflightAbandonRow);
            Assert.NotNull(window.PreflightAbandonText);
            Assert.Equal(Visibility.Collapsed, window.PreflightAbandonRow.Visibility);

            window.Close();
        });

        Assert.Null(error);
    }

    [Fact]
    public void ProfileWindow_LoadsItsXaml()
    {
        // The profile left the multiplayer subtab bar for a window of its own, so like the
        // lobby it is now parsed only when somebody signs in and clicks their own name —
        // nothing in the automated verification opens it, and a {StaticResource} that fails
        // to resolve throws at runtime rather than at compile time.
        var error = RunOnStaThread(() =>
        {
            var window = new ProfileWindow();

            // The page is built into this StackPanel and into nothing else: MultiplayerTab's
            // RenderProfileTab writes here. Rename it and the whole profile silently stops
            // being drawn — the build stays green, because that lookup is by field.
            Assert.NotNull(window.ProfileBody);
            Assert.NotNull(window.TitleBarControl);

            // No MaxWidth. The 900 px bound the handoff asks for was rejected three times, and
            // reintroducing one here would re-cap the deck grid without anything failing.
            // ⚠ An unset MaxWidth is +infinity; NaN is what Width defaults to. Confusing the
            // two fires this assertion on a perfectly good page.
            Assert.True(double.IsPositiveInfinity(window.ProfileBody.MaxWidth));

            window.Close();
        });

        Assert.Null(error);
    }

    [Fact]
    public void TheAccountMenuStillOffersProfileAndSignOut()
    {
        // Sign-out is the ONLY way out of an account in the whole launcher, and it has now
        // moved twice — out of the Multiplayer tab's account row into a menu, into the
        // profile window's title bar, and back into the menu. Losing it breaks nothing
        // visible: the launcher builds, runs, and simply strands a signed-in player.
        //
        // The guard is at SOURCE level because the menu is built in MainWindow's
        // code-behind and DialogXamlTests never constructs MainWindow — the same idiom
        // StringTableSourceTests uses when the runtime evidence is out of reach.
        var main = ReadRepoFile("WarsOfLibertyLauncher", "MainWindow.xaml.cs");

        Assert.Contains("MpAccountMenuProfile", main);
        Assert.Contains("MpAccountMenuSignOut", main);

        // The CALL, not just the caption: a menu row labelled "Sign out" that invokes
        // nothing would satisfy a caption-only check and strand the player anyway.
        Assert.Contains("MultiplayerView.SignOut()", main);

        // And it MOVED rather than being duplicated — two sign-outs is two places for the
        // rating cache to be cleared, or not.
        var window = ReadRepoFile("WarsOfLibertyLauncher", "ProfileWindow.xaml.cs");
        Assert.DoesNotContain("MpAccountMenuSignOut", window);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = System.IO.Path.Combine(
                new[] { dir.FullName }.Concat(parts).ToArray());
            if (System.IO.File.Exists(candidate)) return System.IO.File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new System.IO.FileNotFoundException(
            $"Could not find {string.Join('/', parts)} above {AppContext.BaseDirectory}");
    }

    [Fact]
    public void EveryDemoScenarioRendersAndResolvesEveryTokenItUses()
    {
        // The bracket, the tournament cards and the entrant list are ASSEMBLED IN CODE and so
        // checked by nothing at compile time — the same hole MatchResultCard was added here for,
        // where two resource keys that had never been defined shipped in a card only a finished
        // match could build. A bracket only appears once somebody runs a tournament, so nothing
        // else would find a missing token before a player did.
        //
        // Driven from the DEMO fixture rather than a hand-made one, which buys two things at
        // once: the four samples cover every card state between them (TournamentDemoDataTests
        // pins that), and the fixture the maintainer looks at is the fixture this renders — so
        // it cannot rot into something that no longer paints.
        var error = RunOnStaThread(() =>
        {
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var tab = new MultiplayerTab();

                foreach (var t in TournamentDemoData.All())
                {
                    Assert.NotNull(tab.BuildTournamentCard(t, isDraft: false));
                    Assert.NotNull(tab.BuildEntrantsList(t, TournamentDemoData.MeUserId));
                    if (t.Matches is { Count: > 0 })
                        Assert.NotNull(tab.BuildBracketPanel(t, TournamentDemoData.MeUserId));
                }

                // And in English too: a key defined in only one language renders as the key,
                // which is visible but easy to miss in a language you do not read.
                Strings.SetLanguage("en");
                foreach (var t in TournamentDemoData.All())
                {
                    if (t.Matches is { Count: > 0 })
                        Assert.NotNull(tab.BuildBracketPanel(t, TournamentDemoData.MeUserId));
                }
            }
            finally { Strings.SetLanguage(previous); }
        });

        Assert.Null(error);
    }

    [Fact]
    public void TheTournamentHeaderPutsTheTurnCapsuleAndTheEntrantToggleOnTheRight()
    {
        // The capsule is the answer to the only question somebody opens this tab with, and
        // the toggle is the only way back to the entrant table once a bracket hides it. Both
        // are built in code, both are conditional, and neither is checked by anything else -
        // so if a layout edit drops one, the loss is invisible until somebody goes looking
        // for a match they cannot find.
        //
        // Asserted by WALKING THE TREE and measuring, not by trusting the builder returned
        // something: an element added to a Grid column that ends up zero-wide is present,
        // non-null, and completely absent from the screen.
        var error = RunOnStaThread(() =>
        {
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var tab = new MultiplayerTab();
                var t = TournamentDemoData.Running();
                var header = (FrameworkElement)tab.BuildTournamentHeader(
                    t, TournamentDemoData.MeUserId);

                header.Measure(new Size(1200, double.PositiveInfinity));
                header.Arrange(new Rect(new Point(0, 0), header.DesiredSize));

                var texts = Descendants(header).OfType<TextBlock>().ToList();

                // The fixture has a playable match, so the capsule must name its round.
                Assert.Contains(texts, tb => tb.Text.Contains(
                    Strings.Get("MpTournamentRoundQuarter").ToLowerInvariant(),
                    StringComparison.OrdinalIgnoreCase));

                var toggle = Descendants(header).OfType<Button>().ToList();
                Assert.Contains(toggle, b =>
                    (b.Content as string) == Strings.Get("MpTournamentSeeEntrants"));

                // And they must have actually been given room. Zero width is the failure
                // this test exists for.
                foreach (var b in toggle) Assert.True(b.ActualWidth > 1, "the toggle is zero-wide");
            }
            finally { Strings.SetLanguage(previous); }
        });

        Assert.Null(error);
    }

    /// <summary>Every element under one, so a code-built panel can be inspected.</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        if (n == 0)
        {
            // Not arranged yet, or a content host: fall back to the logical tree, which is
            // populated as soon as the children are added.
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var deeper in Descendants(child)) yield return deeper;
            }
            yield break;
        }
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    [Fact]
    public void TheStatisticsPageRendersBothItsStatesAndResolvesEveryTokenItUses()
    {
        // The whole page is assembled in code - the tables, the bars, the cards - so nothing
        // checks at compile time that a brush or a size it asks for by name exists. And the
        // FILLED state is unreachable by playing: it needs hundreds of rated matches carrying
        // a civilization, which this community has none of. Without this test that half of the
        // page has no automated check at all.
        //
        // Both scopes and both scenarios, in both languages, because each combination builds a
        // different set of cards: the civ table only appears with a sample, the map table only
        // takes the wide column without one, and a key defined in one language renders as the
        // key in the other.
        var error = RunOnStaThread(() =>
        {
            var previous = Strings.Language;
            try
            {
                foreach (var lang in new[] { "es", "en" })
                {
                    Strings.SetLanguage(lang);
                    foreach (var scenario in new[] { "full", "empty" })
                    {
                        var tab = new MultiplayerTab();
                        tab.ShowDemoStats(scenario);
                        Assert.NotEmpty(tab.StatsLeftColumn.Children);
                        Assert.NotEmpty(tab.StatsRightColumn.Children);

                        // And the MOD picker, which is what replaced the viewer scope. On a
                        // machine with one installed mod it draws as a label rather than a row
                        // of buttons — but never as nothing, because the figures on this page
                        // only mean something once it says which mod they are about.
                        Assert.NotEmpty(tab.StatsModPicker.Children);
                    }
                }
            }
            finally { Strings.SetLanguage(previous); }
        });

        Assert.Null(error);
    }

    [Fact]
    public void CreateTournamentDialog_SaysWhatItIsWaitingForAndThenStopsSayingIt()
    {
        // The defect: a primary button greyed out with nothing anywhere explaining that it
        // wanted three characters. Now the field says what is missing and stops the moment
        // it is not. Both halves matter - a validation line that never clears is the same
        // bug from the other side.
        var error = RunOnStaThread(() =>
        {
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var dlg = new CreateTournamentDialog();

                Assert.False(dlg.OkButton.IsEnabled);
                Assert.Equal(Visibility.Visible, dlg.NameProblem.Visibility);
                Assert.False(string.IsNullOrWhiteSpace(dlg.NameProblem.Text));

                dlg.NameEntry.Text = "Co";
                Assert.False(dlg.OkButton.IsEnabled);
                Assert.Equal(Visibility.Visible, dlg.NameProblem.Visibility);

                dlg.NameEntry.Text = "Copa de septiembre";
                Assert.True(dlg.OkButton.IsEnabled);
                Assert.Equal(Visibility.Collapsed, dlg.NameProblem.Visibility);
                // The counter is the other half of the same job: it lets somebody watch the
                // requirement being satisfied instead of guessing at it.
                Assert.Contains("18", dlg.NameCount.Text);
            }
            finally { Strings.SetLanguage(previous); }
        });

        Assert.Null(error);
    }

    [Fact]
    public void CreateTournamentDialog_TheHelpTextFollowsTheSelection()
    {
        // THE defect this dialog was rebuilt for. The paragraph it replaces worked its
        // example in 3v3 while 1v1 was selected, so the one line explaining what "places"
        // means was wrong for the format actually chosen.
        var error = RunOnStaThread(() =>
        {
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var dlg = new CreateTournamentDialog();

                // 1v1: eight places is eight players, and there are no teams to form.
                Assert.Equal(Visibility.Collapsed, dlg.TeamSourceBlock.Visibility);
                Assert.Contains("8", dlg.CapacityMath.Text);
                string solo = dlg.CapacityMath.Text;

                dlg.Format3v3.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                // 3v3: the same eight places are twenty-four people, and the question of how
                // a team is formed now exists.
                Assert.Equal(Visibility.Visible, dlg.TeamSourceBlock.Visibility);
                Assert.NotEqual(solo, dlg.CapacityMath.Text);
                Assert.Contains("24", dlg.CapacityMath.Text);
            }
            finally { Strings.SetLanguage(previous); }
        });

        Assert.Null(error);
    }

    [Fact]
    public void CreateTournamentDialog_ThePrimaryIsNotNamedAfterItsOwnWindow()
    {
        // "New tournament" on the button and "New tournament" in the caption above it: the
        // button then says what the window IS rather than what pressing it does. Checked in
        // both languages because the two strings are separate keys in each.
        var error = RunOnStaThread(() =>
        {
            var previous = Strings.Language;
            try
            {
                foreach (var lang in new[] { "es", "en" })
                {
                    Strings.SetLanguage(lang);
                    var dlg = new CreateTournamentDialog();
                    Assert.NotEqual(dlg.Title, dlg.OkButton.Content as string);
                }
            }
            finally { Strings.SetLanguage(previous); }
        });

        Assert.Null(error);
    }

    [Fact]
    public void CreateTournamentDialog_LoadsItsXaml()
    {
        // It is only ever parsed once somebody clicks "New tournament", so a broken
        // StaticResource in it would ship unseen — which is what this whole file is for.
        var error = RunOnStaThread(() =>
        {
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                Assert.NotNull(new CreateTournamentDialog());
            }
            finally { Strings.SetLanguage(previous); }
        });

        Assert.Null(error);
    }

    [Fact]
    public void TheMultiplayerSubtabStripHasExactlyFourNamedTabs()
    {
        // Two tabs left: PERFIL to its own window, AMIGOS deleted outright (its view was one
        // hardcoded, unlocalized "Friends — coming soon" with no endpoint behind it). The
        // count blocks re-adding a stub; the caption half re-pins a bug that already shipped,
        // where SubtabStats was declared in XAML and never assigned in ApplyStrings and so
        // rendered as a clickable, anonymous gap for as long as the subtab existed.
        //
        // TORNEOS made it four, and it is the counter-example rather than an exception: it
        // has an endpoint, a DTO, a websocket frame and a bracket behind it, which is the
        // bar AMIGOS failed. Raising this number is meant to be an argument, not a habit.
        var error = RunOnStaThread(() =>
        {
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var tab = new MultiplayerTab();

                var strip = (Panel)LogicalTreeHelper.GetParent(tab.SubtabRanking);
                var buttons = strip.Children.OfType<FrameworkElement>()
                    .SelectMany(c => c is Panel p ? p.Children.OfType<FrameworkElement>() : new[] { c })
                    .OfType<Button>()
                    .ToList();

                Assert.Equal(4, buttons.Count);
                Assert.All(buttons, b => Assert.False(
                    string.IsNullOrWhiteSpace(b.Content as string),
                    "a subtab pill has no caption: it is clickable and anonymous."));
            }
            finally { Strings.SetLanguage(previous); }
        });

        Assert.Null(error);
    }

    [Fact]
    public void RenameRoomDialog_LoadsItsXaml()
    {
        // Added when this dialog moved off the launcher's gold styles onto the multiplayer
        // blue ones. It now resolves MpDialogField, MpSecondaryButton and MpPrimaryButton —
        // keys it had never referenced — and a StaticResource that fails to resolve throws at
        // RUNTIME, not compile. Nothing else opens this window: it needs a signed-in host
        // inside a room to press the button.
        var error = RunOnStaThread(() =>
        {
            var dlg = new RenameRoomDialog("Sala de prueba");
            Assert.NotNull(dlg.NameEntry);
            dlg.Close();
        });

        Assert.Null(error);
    }

    [Fact]
    public void PasswordPromptDialog_LoadsItsXaml()
    {
        // Same move, and the better-travelled of the two — it opens on every join of a private
        // room. Its PasswordBox is the riskier half: WPF ships no implicit style for that
        // control, so it depends entirely on MpDialogPasswordField resolving.
        var error = RunOnStaThread(() =>
        {
            var dlg = new PasswordPromptDialog();
            Assert.NotNull(dlg.PasswordEntry);
            dlg.Close();
        });

        Assert.Null(error);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.0)]
    [InlineData(0.5)]
    public void MatchResultCard_BuildsForEveryVerdict(double result)
    {
        // The card is built in code, so nothing about it is checked at compile time: a
        // resource key that does not resolve throws at BUILD time, and it is only built
        // once a real match has finished. All three verdicts take different branches —
        // the no-result one in particular reaches the footer's explanation, which no other
        // path does.
        var error = RunOnStaThread(() =>
        {
            var model = new MatchOutcomeView(
                MatchOutcomeView.Classify(result),
                "wol", "Texas", 1440, 2,
                RatingBefore: 1524, RatingAfter: 1542,
                RivalLogin: "someone", RivalRating: 1592,
                Wins: 4, Losses: 1, Rd: 60);
            var card = MatchResultCard.Build(
                model, new MatchResultCard.Actions(OnRematch: null, OnDismiss: () => { }));
            Assert.NotNull(card);
        });

        Assert.Null(error);
    }

    [Fact]
    public void MatchResultCard_BuildsWithNothingKnown()
    {
        // The degraded shape an older backend produces: no ratings, no map, no player
        // count, nothing decided. Every one of those is a branch that returns null or an
        // em dash instead of a value, and together they are the case most likely to throw.
        var error = RunOnStaThread(() =>
        {
            var model = new MatchOutcomeView(
                MatchVerdict.NoResult, null, null, 0, 0,
                RatingBefore: null, RatingAfter: null,
                RivalLogin: null, RivalRating: null,
                Wins: 0, Losses: 0, Rd: null);
            Assert.NotNull(MatchResultCard.Build(
                model, new MatchResultCard.Actions(null, null)));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The REPLAY cell, both ways.
    ///
    /// <para>It used to be a fixed label and is now the one cell that can become a BUTTON —
    /// a different branch of <c>Cell</c>, resolving a style by name (<c>MpLinkButton</c>) that
    /// nothing else in this card touches. A key that does not resolve throws only when a real
    /// match ends, which is the worst possible place to find out.</para>
    ///
    /// <para><b>The no-recording case is the one that matters.</b> Most matches have none, so
    /// that branch has to render exactly as it always did; if it ever starts throwing, every
    /// unrecorded match loses its whole card rather than losing a button.</para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(@"C:\Users\someone\Documents\My Games\Wars of Liberty\Savegame\Record Game 1.age3Yrec")]
    public void MatchResultCard_BuildsWithAndWithoutARecording(string? recordingPath)
    {
        var error = RunOnStaThread(() =>
        {
            var model = new MatchOutcomeView(
                MatchVerdict.Win, "wol", "ESOC_Tibet", 916, 2,
                RatingBefore: 1500, RatingAfter: 1617,
                RivalLogin: "alucard", RivalRating: 1383,
                Wins: 1, Losses: 0, Rd: 110,
                RecordingPath: recordingPath);

            Assert.NotNull(MatchResultCard.Build(
                model, new MatchResultCard.Actions(null, () => { })));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The subtitle's civilization segment, in all three states it can be in.
    ///
    /// <para><b>The both-null case is the one that matters</b> — it is every match stored before
    /// civilizations were reported and every match of a mod that ships no loose civ list, so it
    /// has to render byte for byte what it always did. And the mine-only case exists because
    /// <c>Strings.Format</c> would happily print a matchup with an empty second half; the card
    /// is supposed to fall back to the bare name instead.</para>
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("Chinese", null)]
    [InlineData("Chinese", "Colombians")]
    public void MatchResultCard_BuildsWithAndWithoutCivilizations(string? mine, string? theirs)
    {
        var error = RunOnStaThread(() =>
        {
            var model = new MatchOutcomeView(
                MatchVerdict.Win, "wol", "ESOC_Tibet", 916, 2,
                RatingBefore: 1500, RatingAfter: 1617,
                RivalLogin: "alucard", RivalRating: 1383,
                Wins: 1, Losses: 0, Rd: 110,
                MyCiv: mine, RivalCiv: theirs);

            Assert.NotNull(MatchResultCard.Build(
                model, new MatchResultCard.Actions(null, () => { })));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The four empty states of the tournaments panel are drawn by one helper, and it set
    /// NEITHER a colour NOR a size.
    ///
    /// <para>The colour was the visible half: nothing on that path sets
    /// <c>TextElement.Foreground</c> on an ancestor and the implicit TextBlock style has no
    /// setters, so the text fell through to WPF's default BLACK and was drawn italic on navy.
    /// It shipped, and it took a screenshot to find, because a missing Foreground is not an
    /// error anywhere — it is a valid colour that merely happens to be invisible here.</para>
    ///
    /// <para>The size was the quieter half: with no FontSize the block sits at WPF's 12, which
    /// is not a token <c>TextScale</c> multiplies, so this was the one piece of the tab that
    /// ignored the text-size setting. What is asserted is that the size is a REFERENCE and not
    /// a number — a baked FontSize would satisfy a value check and still never follow the
    /// setting.</para>
    /// </summary>
    [Fact]
    public void TheTournamentEmptyStatesAreNotBlackAndDoScale()
    {
        var error = RunOnStaThread(() =>
        {
            var hint = MultiplayerTab.Hint("Pick a tournament to see its bracket.");

            Assert.Equal(Application.Current.FindResource("MpTextMuted"), hint.Foreground);
            Assert.NotEqual(Brushes.Black, hint.Foreground);

            Assert.Equal((double)Application.Current.FindResource("MpBodySize"), hint.FontSize);
            var local = hint.ReadLocalValue(TextBlock.FontSizeProperty);
            Assert.NotEqual(DependencyProperty.UnsetValue, local);
            Assert.IsNotType<double>(local);
        });

        Assert.Null(error);
    }

    /// <summary>
    /// A row of the CIVS table on the Clasificación page, and the same percentage rule the
    /// Profile card follows — the two surfaces must never disagree about when there is enough
    /// behind a civilization to state a rate.
    ///
    /// <para>It also pins that a row's columns match the table definition. Header and rows are
    /// built by one method so drift is structurally impossible today; this is what notices if
    /// somebody inlines one of them.</para>
    /// </summary>
    [Theory]
    [InlineData(2, 1, 1, false)]    // two decided games: no rate
    [InlineData(9, 6, 3, true)]     // past the bar
    public void CivRow_ShowsAPercentageOnlyWhenThereIsEnoughBehindIt(
        int played, int wins, int losses, bool expectPercent)
    {
        var error = RunOnStaThread(() =>
        {
            var row = (Border)MultiplayerTab.BuildCivRow("Chinese", played, wins, losses, 900);

            var grid = (Grid)row.Child;
            Assert.Equal(CivTableLayout.All.Count, grid.ColumnDefinitions.Count);

            var cells = grid.Children.OfType<TextBlock>().ToList();

            // Column 0 is a PANEL, not a TextBlock: the civilization's flag sits beside its
            // name inside that one cell, so the table's shared column contract stays untouched.
            // Read through it rather than pinning the shape, which is not what this test is for.
            var nameCell = grid.Children.OfType<FrameworkElement>().Single(e => Grid.GetColumn(e) == 0);
            Assert.Equal("Chinese", RevealText.PlainTextOf(
                VisualsUnder(nameCell).OfType<TextBlock>().First()));

            Assert.Equal($"{wins}-{losses}",
                RevealText.PlainTextOf(cells.Single(t => Grid.GetColumn(t) == 2)));

            // Read by COLUMN ROLE and not by index: a column added to the middle of the table
            // (the win bar was) must not silently repoint this at the wrong cell.
            int percentColumn = CivTableLayout.All
                .Select((spec, i) => (spec, i))
                .Single(x => x.spec.Column == CivColumn.Percent).i;
            var percent = RevealText.PlainTextOf(
                cells.Single(t => Grid.GetColumn(t) == percentColumn));
            Assert.Equal(expectPercent, percent.Contains('%'));

            // And the bar itself: drawn as a filled proportion only when the percentage is,
            // an empty channel otherwise. A bar at 0 % would assert what the blank percentage
            // beside it is refusing to assert.
            int barColumn = CivTableLayout.All
                .Select((spec, i) => (spec, i))
                .Single(x => x.spec.Column == CivColumn.WinBar).i;
            var bar = grid.Children.OfType<Border>().Single(b => Grid.GetColumn(b) == barColumn);
            Assert.Equal(expectPercent, bar.Child is Grid);
        });

        Assert.Null(error);
    }

    /// <summary>
    /// A row of the Profile's YOUR CIVILIZATIONS card, above and below the percentage bar.
    ///
    /// <para><b>The thin row is the one that matters</b> — for months almost every civilization
    /// will have two or three matches, so that is the shape most players see, and it must draw
    /// NOTHING where the percentage would go rather than an em dash or a 0.</para>
    /// </summary>
    [Theory]
    [InlineData(1, 0, false)]    // one decided game: no rate
    [InlineData(6, 2, true)]     // past the bar
    public void ProfileCivRow_ShowsAPercentageOnlyWhenThereIsEnoughBehindIt(
        int wins, int losses, bool expectPercent)
    {
        var error = RunOnStaThread(() =>
        {
            var row = MultiplayerTab.BuildProfileCivRow(
                new CivStatRow("Chinese", wins + losses, wins, losses));

            Assert.NotNull(row);

            var cells = ((Grid)row).Children.OfType<TextBlock>().ToList();
            Assert.Equal("Chinese", RevealText.PlainTextOf(cells[0]));

            var percent = cells.Single(t => Grid.GetColumn(t) == 2);
            Assert.Equal(expectPercent, RevealText.PlainTextOf(percent).Contains('%'));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// A card on the STATISTICS tab, in the three shapes the store really holds.
    ///
    /// <para><b>The zeroed case is the one that matters</b> — every game but the newest in a
    /// personality file has its totals wiped by AoE3, so most imported games arrive with real
    /// unit counts and no resources at all. That card must build, and it must not print
    /// "0 shipments", which would be a statement rather than an absence.</para>
    ///
    /// <para>Nothing else in the launcher builds this and the smoke test never opens the tab, so
    /// a resource looked up by name would fail for the first time in front of a player.</para>
    /// </summary>
    [Theory]
    [InlineData(true, 664331, 42)]     // the newest game: everything recorded
    [InlineData(false, 0, 0)]          // an older game: units only
    [InlineData(null, 0, 0)]           // a block with no result at all
    public void ModPropertiesAiGameCard_BuildsForEveryShape(bool? won, int score, int shipments)
    {
        var error = RunOnStaThread(() =>
        {
            var game = new AiGameRecord
            {
                Personality = "wolMenelik",
                ModId = "wol",
                PlayerName = "Gorgorito",
                DurationMs = 1067806,
                Won = won,
                Score = score,
                Shipments = shipments,
                Gold = score > 0 ? 300820 : 0,
                Units = new Dictionary<string, int> { ["gwtank"] = 56, ["hussar"] = 31 },
            };

            var card = ModPropertiesDialog.BuildAiGameCard(
                game, new Dictionary<string, string> { ["gwtank"] = "Tank" });

            Assert.NotNull(card);

            // The unresolved proto falls back to its internal name — unlike a civilization,
            // which must go blank rather than print a number nobody can read.
            var text = string.Join(" ", ((StackPanel)card.Child).Children
                .OfType<TextBlock>()
                .Select(RevealText.PlainTextOf));
            Assert.Contains("Tank", text, StringComparison.Ordinal);
            Assert.Contains("hussar", text, StringComparison.Ordinal);
            Assert.Equal(shipments > 0, text.Contains("42", StringComparison.Ordinal));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The ranking strip is ONE card, and it is not drawn empty.
    ///
    /// <para>It used to be two side by side — civilizations and maps — and the rule worth
    /// pinning then was that hiding one gave back its star column AND the 11px gap, or half
    /// the strip stayed reserved and blank with a stray inset beside it. The maps card has
    /// gone to Estadisticas, where it is a full table with proportional bars and a mod it can
    /// name, rather than five names and five numbers duplicated across two pages. The column
    /// arithmetic went with it.</para>
    ///
    /// <para>What survives is the simpler rule underneath, and it is the one that actually
    /// mattered: a card with nothing in it is not drawn at all.</para>
    /// </summary>
    [Fact]
    public void TheRankingStripIsOneCardAndIsNotDrawnEmpty()
    {
        var error = RunOnStaThread(() =>
        {
            var tab = new MultiplayerTab();

            // No civilization data is the ordinary state of this community for months yet, so
            // it is the state that has to look deliberate.
            tab.RenderRankingSummaryCardsForTest();
            Assert.Equal(Visibility.Collapsed, tab.RankingCivsCard.Visibility);

            // And the maps card is GONE — not hidden, not zero-width. This is where the
            // duplication would come back if somebody reinstated it.
            Assert.Null(tab.FindName("RankingMapsCard"));
            Assert.Null(tab.FindName("RankingStripGap"));
        });

        Assert.Null(error);
    }

    [Fact]
    public void StatsCountRow_TrimsTheNameAndNeverTheNumber()
    {
        var error = RunOnStaThread(() =>
        {
            var row = MultiplayerTab.BuildCountRow(
                "ESOC Fertile Crescent, a deliberately very long map name", 1234);

            Assert.NotNull(row);
            var blocks = row.Children.OfType<TextBlock>().ToList();
            Assert.Equal(2, blocks.Count);

            Assert.Equal(TextTrimming.CharacterEllipsis, blocks[0].TextTrimming);
            Assert.Equal(TextTrimming.None, blocks[1].TextTrimming);

            // ...and the number is thousands-separated, because these run to four figures.
            Assert.Contains("1", RevealText.PlainTextOf(blocks[1]), StringComparison.Ordinal);
            Assert.Equal(0, Grid.GetColumn(blocks[0]));
            Assert.Equal(1, Grid.GetColumn(blocks[1]));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// A deck as tiles on the DECKS tab, built from the game's own home city file.
    ///
    /// <para><b>The order is the assertion that matters.</b> A deck is a sequence the player
    /// arranged, and it is the one thing this file carries that nothing else does — a sort would
    /// still show every card correctly and destroy it silently.</para>
    ///
    /// <para><b>And it is read off each tile's <c>Tag</c>, which is the whole reason that Tag
    /// exists.</b> This used to scan the rendered text, because a deck was a line of names. Now a
    /// card is a picture: its name lives in a tooltip and nowhere in the tree as text, so the old
    /// assertion would have gone on passing while checking nothing at all.</para>
    ///
    /// <para>Nothing else in the launcher builds this and the smoke test never opens the tab, so
    /// a resource looked up by name would fail for the first time in front of a player.</para>
    /// </summary>
    [Fact]
    public void ModPropertiesDeckTiles_BuildAndKeepTheDeckOrder()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();

            var deck = new HomeCityDeckEntry
            {
                Name = "Static Deck",
                Cards =
                {
                    new HomeCityCard { Slot = 0, Dbid = 4128, InternalName = "YPHCExpandedTradingPost" },
                    new HomeCityCard { Slot = 1, Dbid = 2212, InternalName = "HCShipWoodCrates3" },
                    new HomeCityCard { Slot = 2, Dbid = 52905, InternalName = "WOLHCShipTigermen2" },
                },
            };

            var details = new Dictionary<string, WarsOfLibertyLauncher.Services.CardDetail>
            {
                ["HCShipWoodCrates3"] =
                    new("3 Wood Crates", "Wood source.", @"ui\techs\hc_wood_crate\hc_wood_crate_128"),
            };

            var tiles = WarsOfLibertyLauncher.Controls.DeckTiles.Build(
                deck, details, new Dictionary<string, System.Windows.Media.ImageSource>());

            Assert.Equal(3, tiles.Count);

            // In the deck's order, which is not alphabetical and not by id.
            Assert.Equal("YPHCExpandedTradingPost", tiles[0].Tag);
            Assert.Equal("HCShipWoodCrates3", tiles[1].Tag);
            Assert.Equal("WOLHCShipTigermen2", tiles[2].Tag);

            // A chromeless BUTTON around each picture, not a bare Border: selecting is now the
            // only way to open a card, and MouseLeftButtonUp on a Border can be swallowed by the
            // surrounding ScrollViewer — the trap the language cards already document.
            var faces = tiles.Select(t => (Border)t.Content).ToList();
            Assert.All(tiles, t => Assert.NotNull(t.Template));

            // The multiplayer profile calls the same builder with its own size and rim, which is
            // the whole of the difference between the two surfaces — so it is covered here rather
            // than by a second copy of this test against a second copy of the builder.
            var smaller = WarsOfLibertyLauncher.Controls.DeckTiles.Build(
                deck, details, new Dictionary<string, System.Windows.Media.ImageSource>(),
                tileSize: 40, rimBrush: "MpRimFaint");

            Assert.Equal(
                tiles.Select(t => (string)t.Tag!),
                smaller.Select(t => (string)t.Tag!));
            Assert.Equal(40d, ((Border)smaller[0].Content).Width);

            // Every tile is the same square, so the grid cannot go ragged on a card whose name
            // happens to be long.
            Assert.All(faces, f => Assert.Equal(faces[0].Width, f.Width));

            // With no picture available the tile still says which card it is, rather than
            // sitting blank — and it says it under the RESOLVED name where there is one.
            Assert.Equal("3", ((TextBlock)faces[1].Child).Text);
            Assert.Equal("Y", ((TextBlock)faces[0].Child).Text);
        });

        Assert.Null(error);
    }

    /// <summary>
    /// A local match card on the STATISTICS tab, built from a recording on disk.
    ///
    /// <para><b>The verdict is marked per PLAYER, not for the viewer.</b> Most recordings a
    /// player keeps are somebody else's — a game they were sent — and the file still names who
    /// lost. Reporting that only when the viewer is in the match threw the fact away on exactly
    /// the cards that had nothing else to say.</para>
    ///
    /// <para>Who LOST is measured; who WON is derived and only in a clean two-human 1v1, so the
    /// caller passes -1 for the winner when it is not known and this must then mark nobody.</para>
    /// </summary>
    [Fact]
    public void ModPropertiesLocalMatchCard_MarksWhoLostAndWhoWon()
    {
        // The verdict words are asserted, so the language is decided rather than inherited —
        // and put back afterwards, or the next test inherits it instead.
        var previousLanguage = Strings.Language;
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            Strings.SetLanguage(Strings.LangEn);

            var players = new[]
            {
                new ReplayParserService.ReplayPlayer(
                    1, "CodeFender", 33, -1, ReplayParserService.SlotTypeHuman,
                    "Ahuitzotl", 13, "sp_Quebec_homecity.xml"),
                new ReplayParserService.ReplayPlayer(
                    2, "NathanR06", 7, -1, ReplayParserService.SlotTypeHuman,
                    "Da Zaohua", 10, "sp_Beijing_homecity.xml"),
            };

            var card = ModPropertiesDialog.BuildHumanGameCard(
                "Code vs Nathan 2",
                new DateTime(2026, 7, 27, 21, 30, 0),
                "ESOC Arizona",
                players,
                localSlot: -1,          // the viewer is not in this one
                result: null,
                loserSlot: 1,
                winnerSlot: 2,
                civs: new Dictionary<int, string> { [33] = "Canadians", [7] = "Chinese" });

            Assert.NotNull(card);

            var lines = ((StackPanel)card.Child).Children.OfType<TextBlock>()
                .Select(RevealText.PlainTextOf).ToList();
            var all = string.Join(" | ", lines);

            Assert.Contains("ESOC Arizona", all, StringComparison.Ordinal);

            // AoE3 names every recording "Record Game N" and renumbers them, so the file name is
            // the only way back to the one on disk.
            Assert.Contains("Code vs Nathan 2", all, StringComparison.Ordinal);

            var loser = lines.Single(l => l.Contains("CodeFender", StringComparison.Ordinal));
            var winner = lines.Single(l => l.Contains("NathanR06", StringComparison.Ordinal));
            Assert.StartsWith("Lost", loser, StringComparison.Ordinal);
            Assert.StartsWith("Won", winner, StringComparison.Ordinal);

            // What the recording carries about each player, resolved rather than raw: the civ is
            // an index in the file and the deck is a file name.
            Assert.Contains("Canadians", loser, StringComparison.Ordinal);
            Assert.Contains("Quebec", loser, StringComparison.Ordinal);
            Assert.Contains("Ahuitzotl", loser, StringComparison.Ordinal);
        });

        Strings.SetLanguage(previousLanguage);
        Assert.Null(error);
    }

    /// <summary>
    /// The saved deck folded under a past match: the button, and what unfolds from it.
    ///
    /// <para><b>Written because the first version of this threw.</b> It styled its Button with
    /// <c>SetActionQuiet</c>, which is a <c>TextBlock</c> style — applying it raises, and because
    /// the element is built inside the STATISTICS load that one line emptied BOTH groups of the
    /// page. The whole suite stayed green: nothing constructed it. It was found by opening the
    /// window and looking.</para>
    ///
    /// <para>The art arrives through a callback so this can hand it a completed task: awaiting
    /// one continues on the SAME thread, which is what lets an STA test with no message pump
    /// drive the expansion at all.</para>
    /// </summary>
    [Fact]
    public void ModPropertiesSavedDeck_UnfoldsIntoTilesInDeckOrder()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();

            var deck = new HomeCityDeckEntry
            {
                Name = "Static Deck",
                Cards =
                {
                    new HomeCityCard { Slot = 0, Dbid = 4128, InternalName = "YPHCExpandedTradingPost" },
                    new HomeCityCard { Slot = 1, Dbid = 2212, InternalName = "HCShipWoodCrates3" },
                },
            };
            var profile = new HomeCityProfile { Civ = "Chinese", CityName = "Beijing", Decks = { deck } };

            var section = (StackPanel)ModPropertiesDialog.BuildDeckSnapshotSection(
                new[] { profile },
                _ => Task.FromResult((
                    (IReadOnlyDictionary<string, WarsOfLibertyLauncher.Services.CardDetail>)
                        new Dictionary<string, WarsOfLibertyLauncher.Services.CardDetail>(),
                    (IReadOnlyDictionary<string, System.Windows.Media.ImageSource>)
                        new Dictionary<string, System.Windows.Media.ImageSource>())))!;

            // Folded: a match card must not grow 25 tiles by itself.
            var show = Assert.IsType<Button>(Assert.Single(section.Children.OfType<UIElement>()));
            Assert.Empty(section.Children.OfType<WrapPanel>());

            show.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            var tiles = section.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToList();
            Assert.Equal(
                new[] { "YPHCExpandedTradingPost", "HCShipWoodCrates3" },
                tiles.Select(t => (string)t.Tag!));

            // And it says what it is: the cards of THAT day, and every deck of the city rather
            // than one picked, because the game records neither.
            var said = string.Join(" ", section.Children.OfType<TextBlock>()
                .Select(RevealText.PlainTextOf));
            Assert.Contains("Static Deck", said, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(said));
        });

        Assert.Null(error);
    }

    /// <summary>A match played before snapshots existed offers nothing, which is most of them.</summary>
    [Fact]
    public void ModPropertiesSavedDeck_OffersNothingWithoutASnapshot()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();

            Assert.Null(ModPropertiesDialog.BuildDeckSnapshotSection(null, _ => throw new Exception()));
            Assert.Null(ModPropertiesDialog.BuildDeckSnapshotSection(
                Array.Empty<HomeCityProfile>(), _ => throw new Exception()));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The other half, and the one that keeps this honest: with no outcome block in the file —
    /// which is most of them — the card says nothing about who won. Never a draw: "not known"
    /// and "drawn" are different, and only one of them is ever true here.
    /// </summary>
    [Fact]
    public void ModPropertiesLocalMatchCard_SaysNothingWhenTheFileDoesNot()
    {
        var previousLanguage = Strings.Language;
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            Strings.SetLanguage(Strings.LangEn);

            var players = new[]
            {
                new ReplayParserService.ReplayPlayer(
                    1, "Geaf_Argento", 31, -1, ReplayParserService.SlotTypeHuman),
                new ReplayParserService.ReplayPlayer(
                    2, "Gorgorito", 7, -1, ReplayParserService.SlotTypeHuman),
            };

            var card = ModPropertiesDialog.BuildHumanGameCard(
                "Record Game 3", new DateTime(2026, 7, 28), "ESOC High Plains", players,
                localSlot: 2, result: null, loserSlot: -1, winnerSlot: -1,
                civs: new Dictionary<int, string>());

            var all = string.Join(" | ", ((StackPanel)card.Child).Children
                .OfType<TextBlock>().Select(RevealText.PlainTextOf));

            Assert.DoesNotContain("Won", all, StringComparison.Ordinal);
            Assert.DoesNotContain("Lost", all, StringComparison.Ordinal);
            Assert.Contains("Gorgorito", all, StringComparison.Ordinal);
        });

        Strings.SetLanguage(previousLanguage);
        Assert.Null(error);
    }

    /// <summary>
    /// A roster line under a history row, with and without a civilization.
    ///
    /// <para>The civ is appended as a <c>Run</c> of the name's own TextBlock rather than given a
    /// column, so this checks the two things that arrangement can break: that the row still
    /// builds, and that the name is still the FIRST thing in it — the ellipsis eats from the
    /// end, and whose name it is matters more than what they played.</para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Colombians")]
    public void HistoryPlayerRow_BuildsWithAndWithoutACivilization(string? civ)
    {
        var error = RunOnStaThread(() =>
        {
            var row = MultiplayerTab.BuildHistoryPlayerRow(new MatchParticipantLine(
                "u-1", "Gorgorito", null, IsMe: true, MatchVerdict.Win, RatingDelta: 117,
                Team: 0, Civ: civ));

            Assert.NotNull(row);

            // Column 1 is the name cell — the only one the civilization is allowed to join.
            var name = ((Grid)row).Children.OfType<TextBlock>()
                .Single(t => Grid.GetColumn(t) == 1);
            var text = RevealText.PlainTextOf(name);

            Assert.StartsWith("Gorgorito", text, StringComparison.Ordinal);
            Assert.Equal(civ != null, text.Contains("Colombians", StringComparison.Ordinal));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The home city joins the same cell, after the civilization — and the NAME is still what
    /// comes first, which is the whole rule of that TextBlock: the ellipsis eats from the end,
    /// so whose row it is has to survive everything appended to it.
    ///
    /// <para>Absent is the ordinary case and always will be for matches already stored, so the
    /// row is checked in both states.</para>
    /// </summary>
    [Theory]
    [InlineData("Beijing")]
    [InlineData(null)]
    public void HistoryPlayerRow_PutsTheHomeCityAfterTheNameOrNotAtAll(string? city)
    {
        var error = RunOnStaThread(() =>
        {
            var row = MultiplayerTab.BuildHistoryPlayerRow(new MatchParticipantLine(
                "u-1", "Gorgorito", null, IsMe: false, MatchVerdict.Loss, RatingDelta: null,
                Team: 0, Civ: "Chinese", HomeCity: city));

            var name = ((Grid)row).Children.OfType<TextBlock>()
                .Single(t => Grid.GetColumn(t) == 1);
            var text = RevealText.PlainTextOf(name);

            Assert.StartsWith("Gorgorito", text, StringComparison.Ordinal);
            Assert.Contains("Chinese", text, StringComparison.Ordinal);
            Assert.Equal(city != null, text.Contains("Beijing", StringComparison.Ordinal));

            if (city != null)
            {
                Assert.True(
                    text.IndexOf("Chinese", StringComparison.Ordinal)
                    < text.IndexOf("Beijing", StringComparison.Ordinal),
                    "the home city must follow the civilization, not precede it");
            }
        });

        Assert.Null(error);
    }

    /// <summary>
    /// No derived style may declare a state that its INHERITED template silently stomps.
    ///
    /// <para>The generalisation of the Create-button bug, and the test that would have
    /// caught it — twice over. A <c>ControlTemplate</c> trigger that paints a template
    /// element BY NAME is unreachable from a derived style: the derived style can only set
    /// the control's own property, which the TargetName setter then beats on its way down
    /// the TemplateBinding. So a derived style that declares, say, a hover Background while
    /// its inherited template also sets that Background by name is writing code that reads
    /// as intent and does nothing.</para>
    ///
    /// <para>Two real cases existed when this was written: <c>MpFooterPrimary</c> (the Create
    /// button went grey on hover instead of blue) and <c>PrimaryButton</c>/<c>DangerButton</c>,
    /// whose gold and red hovers were dead in ~15 dialogs — every confirm and every
    /// destructive button in the launcher hovered neutral grey.</para>
    ///
    /// <para>A style that supplies its own <c>Template</c> is exempt: it inherits no template
    /// to clash with. That is how several styles here legitimately keep a TargetName trigger.</para>
    ///
    /// <para>Scope: the app-wide <c>Styles/*.xaml</c> dictionaries. A style declared inside a
    /// single window (as <c>MpFooterPrimary</c> is) is not reachable from here — that one has
    /// its own test above.</para>
    /// </summary>
    [Fact]
    public void NoDerivedStyleDeclaresAStateItsInheritedTemplateStomps()
    {
        var error = RunOnStaThread(() =>
        {
            var app = Application.Current!;
            var offenders = new List<string>();
            var examined = 0;

            foreach (var dict in app.Resources.MergedDictionaries)
                foreach (var key in dict.Keys.Cast<object>().ToList())
                {
                    if (dict[key] is not Style style || style.BasedOn == null) continue;

                    // Owns its template → inherits nothing that could stomp it.
                    if (style.Setters.OfType<Setter>().Any(s => s.Property == Control.TemplateProperty))
                        continue;

                    var template = InheritedTemplate(style.BasedOn);
                    if (template == null) continue;

                    var declared = style.Triggers.OfType<Trigger>()
                        .SelectMany(t => t.Setters.OfType<Setter>())
                        .Select(s => s.Property)
                        .Where(p => p != null)
                        .ToHashSet();
                    if (declared.Count == 0) continue;
                    examined++;

                    foreach (var t in template.Triggers.OfType<Trigger>())
                        foreach (var s in t.Setters.OfType<Setter>())
                            if (!string.IsNullOrEmpty(s.TargetName) && declared.Contains(s.Property))
                                offenders.Add(
                                    $"'{key}' declares {s.Property?.Name} on a trigger, but its inherited " +
                                    $"template sets {s.Property?.Name} via TargetName='{s.TargetName}' — " +
                                    "the declaration is dead. Move the template's state off TargetName, " +
                                    "or give this style its own Template.");
                }

            // A rule nothing is subject to is a rule that passes for the wrong reason. A few
            // styles match this shape today; if the count ever reaches zero it means the walk
            // stopped finding styles, not that the codebase became clean.
            Assert.True(examined > 0, "the audit examined no derived styles at all");
            Assert.True(offenders.Count == 0, string.Join("\n", offenders));
        });

        Assert.Null(error);
    }

    /// <summary>The nearest Template setter walking up the BasedOn chain, or null.</summary>
    private static ControlTemplate? InheritedTemplate(Style? style)
    {
        for (; style != null; style = style.BasedOn)
        {
            var setter = style.Setters.OfType<Setter>()
                .FirstOrDefault(s => s.Property == Control.TemplateProperty);
            if (setter?.Value is ControlTemplate template) return template;
        }
        return null;
    }

    /// <summary>
    /// The support pill is assembled in code, so nothing checks it at compile time — the same
    /// reason MatchResultCard is built here. It resolves four resources by name
    /// (<c>ModLinkPill</c>, <c>AccentBrush</c>, <c>TextSecondary</c>, <c>FontSizeCaption</c>),
    /// and a rename of any of them would throw only when a player already had something go
    /// wrong, which is the worst possible moment to find out.
    /// </summary>
    [Fact]
    public void SupportLink_Builds()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var pill = SupportLink.Build();
            Assert.NotNull(pill.Style);
            // The full url in the tooltip is the anti-phishing measure, not decoration: a label
            // can claim anything, so the destination has to be visible.
            Assert.NotNull(pill.ToolTip);

            // The optional size exists so ONE host — the diagnostics row, whose neighbours run
            // on the smaller settings scale — can match its row without a second builder. It
            // must reach the button, or that host silently keeps the default and the Spanish
            // caption goes back over the edge of the card.
            var sized = SupportLink.Build(11.5);
            Assert.Equal(11.5, sized.FontSize);
            Assert.NotNull(sized.Style);
        });

        Assert.Null(error);
    }

    /// <summary>
    /// One step is open at a time, and which one follows the stage.
    ///
    /// <para>The defect: <c>ApplyStage</c> never touched <c>Visibility</c> on any step, and
    /// <c>InProgress</c> and <c>Pending</c> drew the SAME glyph — so four identical cards
    /// stood there in every state and the only thing that moved was a badge colour. An
    /// assistant where everything weighs the same does not guide.</para>
    /// </summary>
    [Theory]
    [InlineData(RadminStage.NotInstalled, 1)]
    [InlineData(RadminStage.InstalledNotRunning, 2)]
    [InlineData(RadminStage.LoggedIn, 3)]
    public void TheAssistantOpensExactlyOneStep(RadminStage stage, int expectedOpen)
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var window = new RadminAssistantWindow(new LauncherConfig());
            window.ApplyStage(stage, Probe(stage));

            Assert.Equal(Visibility.Collapsed, window.ConnectedBlock.Visibility);
            Assert.Equal(Visibility.Visible, window.StepsBlock.Visibility);
            Assert.Equal(4, window.StepsList.Children.Count);

            // The open one is the only child with a card's background; folded and pending
            // rows have none. Asserting the SHAPE rather than a colour, because the colour
            // is what the broken version already varied.
            var open = window.StepsList.Children.OfType<Border>()
                .Where(b => b.Background != null && b.BorderThickness.Left > 0)
                .ToList();
            Assert.Single(open);
            Assert.Same(window.StepsList.Children[expectedOpen - 1], open[0]);
        });
        Assert.Null(error);
    }

    /// <summary>
    /// Connected, the window is not a checklist — but it still offers BOTH things it is
    /// good for.
    ///
    /// <para><c>.claude/rules/multiplayer.md</c> is explicit, and corrected itself once to be:
    /// with everything green this window offers "the copy-network-name button and the 'Open
    /// Radmin' shortcut", and it added the second — <i>"it is short by one"</i>. Folding
    /// the four steps away is what threatens that, so the folded summary carries the shortcut
    /// itself instead of burying it behind the fold.</para>
    /// </summary>
    [Fact]
    public void ConnectedTheAssistantFoldsTheStepsButKeepsBothActions()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var window = new RadminAssistantWindow(new LauncherConfig());
                window.ApplyStage(RadminStage.InAoE3Network, Probe(RadminStage.InAoE3Network));

                Assert.Equal(Visibility.Visible, window.ConnectedBlock.Visibility);
                Assert.Equal(Visibility.Collapsed, window.StepsBlock.Visibility);

                // (1) the network name, in full and reachable to copy.
                Assert.NotNull(window.ConnectedNetworkHost.Content);
                var card = (Border)window.ConnectedNetworkHost.Content!;
                var texts = Descendants(card).OfType<TextBlock>().ToList();
                Assert.Contains(texts, t => t.Text == RadminVpnService.AoE3TadNetworkName);
                Assert.All(texts.Where(t => t.Text == RadminVpnService.AoE3TadNetworkName),
                    t => Assert.NotEqual(TextTrimming.CharacterEllipsis, t.TextTrimming));
                Assert.Contains(Descendants(card).OfType<Button>(), _ => true);

                // (2) the shortcut the rules file added by hand.
                Assert.Equal(Visibility.Visible, window.ReopenRadminLink.Visibility);
                Assert.False(string.IsNullOrWhiteSpace(window.ReopenRadminLink.Content as string));
            }
            finally
            {
                Strings.SetLanguage(previous);
            }
        });
        Assert.Null(error);
    }

    private static RadminStatus Probe(RadminStage stage) => new(
        stage == RadminStage.NotInstalled ? RadminInstallState.NotInstalled : RadminInstallState.Installed,
        ExePath: null,
        Version: null,
        IsServiceRunning: stage > RadminStage.InstalledNotRunning,
        AdapterIp: stage >= RadminStage.LoggedIn ? "26.162.244.170" : null);

    /// <summary>
    /// THE ONE THAT MATTERS for the bracket: <b>a cell carries no buttons</b>.
    ///
    /// <para>Not tidiness. <c>MeasureBracketRow</c> takes the tallest card in the WHOLE
    /// bracket, divided by its span, and makes that the height of every row of every round \u2014
    /// so one button on one card is vertical space on all sixty. Its own doc records what that
    /// cost when it last happened: a sixteen-entrant bracket over two thousand pixels tall,
    /// with the first round running off three screens. <c>BuildAwardStrip</c> wrote the same
    /// warning down, and a later pass added a second stacked button anyway.</para>
    ///
    /// <para>So the assertion is on the whole card surface rather than on named controls: the
    /// next button somebody adds inside a cell has to fail here.</para>
    /// </summary>
    [Fact]
    public void THE_ONE_THAT_MATTERS_ABracketCellCarriesNoButtons()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var tab = new WarsOfLibertyLauncher.Controls.MultiplayerTab();
            var t = TournamentDemoData.Organiser();
            var me = TournamentDemoData.MeUserId;

            // Selected, and by the viewer with the MOST to offer: an organiser looking at a
            // match being played is the case that used to stack two buttons and a status line
            // into one 220 px cell.
            tab.SelectBracketMatchForPreview(t.Id, t.Matches!.First(m => m.Lobby != null).Id);
            var panel = (FrameworkElement)tab.BuildBracketPanel(t, me);

            panel.Measure(new Size(1000, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, 1000, panel.DesiredSize.Height));
            panel.UpdateLayout();

            // The bracket lives under the ScrollViewer; the action bar is its sibling above.
            var scroller = VisualsUnder(panel).OfType<ScrollViewer>().First();
            var inCells = VisualsUnder(scroller).OfType<Button>().ToList();
            Assert.True(inCells.Count == 0,
                "a bracket cell has grown a button again: "
                + string.Join(", ", inCells.Select(b => b.Content as string ?? "?"))
                + ". MeasureBracketRow makes the tallest card the height of every row in the "
                + "bracket, so this is not one card getting taller - it is all of them.");

            // And the actions did not vanish, they moved: the bar has them.
            var captions = VisualsUnder(panel).OfType<Button>()
                .Select(b => b.Content as string)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .ToList();
            Assert.Contains(Strings.Get("MpTournamentWatchRoom"), captions);
        });
        Assert.Null(error);
    }

    /// <summary>
    /// The organiser's two powers live behind the \u22ef, and never as a second button.
    ///
    /// <para>Deciding a match by hand and ordering it replayed both act on other people's
    /// game. They take a deliberate extra click, and they are absent rather than disabled for
    /// anyone who cannot use them \u2014 the same shape the award flyout already had.</para>
    /// </summary>
    [Fact]
    public void TheOrganisersPowersAreBehindTheOverflow()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var tab = new WarsOfLibertyLauncher.Controls.MultiplayerTab();
                var t = TournamentDemoData.Organiser();
                var live = t.Matches!.First(m => m.Lobby != null);
                tab.SelectBracketMatchForPreview(t.Id, live.Id);

                var panel = (FrameworkElement)tab.BuildBracketPanel(t, TournamentDemoData.MeUserId);
                panel.Measure(new Size(1000, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, 1000, panel.DesiredSize.Height));
                panel.UpdateLayout();

                var overflow = VisualsUnder(panel).OfType<Button>()
                    .FirstOrDefault(b => b.ContextMenu != null);
                Assert.NotNull(overflow);

                var items = overflow!.ContextMenu!.Items.OfType<MenuItem>()
                    .Select(i => i.Header as string ?? "")
                    .ToList();

                // Both winners, and the replay - three ways to end or restart one tie, none
                // of them a button sitting next to "Ver la sala".
                Assert.Contains(items, i => i.Contains(EntrantNameFor(t, live.Entrant1Id)));
                Assert.Contains(items, i => i.Contains(EntrantNameFor(t, live.Entrant2Id)));
                Assert.Contains(Strings.Get("MpTournamentReplay"), items);
            }
            finally
            {
                Strings.SetLanguage(previous);
            }
        });
        Assert.Null(error);
    }

    private static string EntrantNameFor(TournamentDetail t, string? entrantId)
        => t.Entrants!.First(e => e.Id == entrantId).DisplayName ?? "";

    /// <summary>
    /// THE ONE THAT MATTERS for the watch window: it offers nothing a non-member could not do.
    ///
    /// <para>The pressure on this window is one button at a time. Somebody watching a match
    /// will eventually want to ready up, or start it, or kick a player, and each of those is a
    /// small reasonable-sounding addition — and the sum of them is <c>LobbyWindow</c>, which
    /// already exists and belongs to people who are IN the room. What this window is for is the
    /// four things a supervisor came for: which slot, who is in it, what is being said, and the
    /// result.</para>
    ///
    /// <para>So the assertion is on the WHOLE button surface rather than on named controls: a
    /// new member action added later has to fail here rather than be forgotten.</para>
    /// </summary>
    [Fact]
    public void THE_ONE_THAT_MATTERS_TheWatchWindowOffersNoMemberActions()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var t = TournamentDemoData.Organiser();
                var m = t.Matches!.First(x => x.Lobby != null);
                var w = new MatchWatchWindow(t, m, TournamentDemoData.WatchSample());

                var root = (FrameworkElement)w.Content;
                root.Measure(new Size(640, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, 640, root.DesiredSize.Height));
                root.UpdateLayout();

                var captions = VisualsUnder(root).OfType<Button>()
                    .Select(b => b.Content as string)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();

                // The two it does offer, and both are things somebody outside the room can
                // do: say something, and stop watching.
                Assert.Contains(Strings.Get("MpWatchSend"), captions);
                Assert.Contains(Strings.Get("DlgClose"), captions);

                // And not one action that belongs to being IN the room. By caption against
                // LobbyWindow's own keys rather than by counting buttons: the count also
                // catches the title bar's close glyph and the chat scroller's repeat
                // buttons, which have nothing to do with the rule.
                foreach (var key in new[]
                         {
                             "MpRoomStart", "MpRoomLeave", "MpRoomLeaveShort",
                             "MpRoomInvite", "MpRoomRenameButton", "MpRoomRejoinGame",
                         })
                {
                    var caption = Strings.Get(key);
                    Assert.DoesNotContain(captions,
                        c => c!.Contains(caption, StringComparison.Ordinal));
                }

                // And the roster it draws is the bracket's, not a second list that could
                // disagree with the slot the organiser clicked.
                var shown = VisualsUnder(root).OfType<TextBlock>()
                    .Select(x => x.Text).ToList();
                foreach (var id in new[] { m.Entrant1Id, m.Entrant2Id })
                {
                    var name = t.Entrants!.First(e => e.Id == id).DisplayName;
                    Assert.Contains(name, shown);
                }
            }
            finally
            {
                Strings.SetLanguage(previous);
            }
        });
        Assert.Null(error);
    }

    /// <summary>
    /// Every visual under <paramref name="root"/>, stopping AT a TextBlock rather than inside
    /// one.
    ///
    /// <para>The shared <c>Descendants</c> walks with <c>VisualTreeHelper</c>, and a TextBlock
    /// built from <c>Inlines</c> hands it a <c>Run</c>, which is not a Visual and throws. The
    /// watch window's chat is the first thing in this suite to use inlines - the speaker's
    /// name is weighted differently from what they said - so the walk stops where the text
    /// begins instead of the shared helper being widened for one caller.</para>
    /// </summary>
    private static IEnumerable<DependencyObject> VisualsUnder(DependencyObject root)
    {
        if (root is TextBlock) yield break;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in VisualsUnder(child)) yield return deeper;
        }
    }

    /// <summary>
    /// THE ONE THAT MATTERS for sign-in: the authorization link is READABLE, the advice
    /// about it arrives BEFORE the button, and the way out is not the loudest thing here.
    ///
    /// <para><b>The link.</b> This window tells the player to check where the link goes, and
    /// the control holding it had <c>TextTrimming="CharacterEllipsis"</c>, no wrap and no
    /// tooltip — so what it showed was
    /// <c>https://discord.com/oauth2/authorize?response_type=co…</c>. A dialog cannot ask
    /// somebody to verify a URL and then hide it: the OAuth link is the one thing here an
    /// attacker would want swapped, and reading it is the only defence a player has. It is a
    /// read-only <c>TextBox</c> so it can also be selected by hand — the Copy button is a
    /// convenience over that, not a replacement for it.</para>
    ///
    /// <para><b>The other two are one rule each from <c>CreateTournamentDialog</c>.</b> No
    /// line of help may arrive after the action it describes: "if a browser you don't
    /// recognize opens, copy the link" was printed UNDER "Open browser", i.e. after the click
    /// it exists to prevent. And ONE solid element, which is the one that does the thing:
    /// Cancel wore <c>SidebarPrimaryButton</c>, the gold gradient with the drop-shadow halo,
    /// the same style as the primary beside it.</para>
    ///
    /// <para><b>All three in one test on purpose.</b> <c>RunOnStaThread</c> starts a fresh
    /// STA thread per call, and <c>SidebarPrimaryButton</c>'s <c>LinearGradientBrush</c>
    /// cannot be frozen — so the SECOND test to parse this window throws a cross-thread
    /// error and the first passes, which reads as a flaky dialog rather than as the harness
    /// it is. One construction, three questions.</para>
    /// </summary>
    [Fact]
    public void THE_ONE_THAT_MATTERS_TheSignInDialogCanBeVerifiedByThePersonUsingIt()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var dlg = new GitHubLoginDialog(new MultiplayerSession(new LauncherConfig()));

                Assert.True(dlg.VerificationUriText.TextWrapping != TextWrapping.NoWrap,
                    "the authorization link does not wrap, so a long one is cut - and this "
                    + "window asks the player to check where it points.");
                Assert.True(dlg.VerificationUriText.IsReadOnly,
                    "the link box is editable, so a stray keystroke rewrites the URL on screen.");

                var root = (FrameworkElement)dlg.Content;
                root.Measure(new Size(520, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, 520, root.DesiredSize.Height));
                root.UpdateLayout();

                var advice = dlg.BrowserHintText.TranslatePoint(new Point(0, 0), root).Y;
                var button = dlg.OpenBrowserButton.TranslatePoint(new Point(0, 0), root).Y;
                Assert.True(advice < button,
                    $"the browser advice sits at y={advice:0}, below the button at y={button:0}"
                    + " - it is telling the player what to watch for after they have clicked.");

                // The same style on both is the shape of the defect: the way out drawn
                // exactly as loudly as the way forward.
                Assert.NotSame(dlg.OpenBrowserButton.Style, dlg.CancelButton.Style);
            }
            finally
            {
                Strings.SetLanguage(previous);
            }
        });
        Assert.Null(error);
    }

    /// <summary>
    /// Handed a mod, the tournament dialog PROPOSES a name instead of demanding one.
    ///
    /// <para>The sibling test above pins the opposite case and both are wanted: with no mod
    /// this dialog still opens empty, complaining, button dead — that path is what
    /// <c>ShowDemoCreateDialog</c> uses, and it is the behaviour the optional parameter was
    /// made optional to preserve. What changed is the path a player takes, where the launcher
    /// has known the mod all along and was making them type anyway.</para>
    /// </summary>
    [Fact]
    public void CreateTournamentDialog_ProposesANameWhenItKnowsTheMod()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var dlg = new CreateTournamentDialog("Struggle of Indonesia");

                // Opens usable: no complaint, and the button is alive.
                Assert.True(dlg.OkButton.IsEnabled);
                Assert.Equal(Visibility.Collapsed, dlg.NameProblem.Visibility);
                Assert.Contains("Struggle of Indonesia", dlg.NameEntry.Text);

                // And the complaint still exists — it is about a choice now, not a greeting.
                dlg.NameEntry.Text = "";
                Assert.False(dlg.OkButton.IsEnabled);
                Assert.Equal(Visibility.Visible, dlg.NameProblem.Visibility);
            }
            finally
            {
                Strings.SetLanguage(previous);
            }
        });
        Assert.Null(error);
    }

    /// <summary>
    /// A mod name long enough to overflow the field never opens the dialog in a state the
    /// field itself would refuse.
    ///
    /// <para><c>MaxNameLength</c> is 80 and the XAML repeats it as <c>MaxLength="80"</c>, so
    /// an untruncated proposal would be silently cut by the TextBox anyway — the truncation
    /// is explicit so the two numbers cannot disagree about where.</para>
    /// </summary>
    [Fact]
    public void CreateTournamentDialog_TruncatesAProposalThatWouldNotFit()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var dlg = new CreateTournamentDialog(new string('M', 200));
            Assert.True(dlg.NameEntry.Text.Length <= dlg.NameEntry.MaxLength,
                $"proposed {dlg.NameEntry.Text.Length} characters into a "
                + $"{dlg.NameEntry.MaxLength}-character field");
            Assert.True(dlg.OkButton.IsEnabled);
        });
        Assert.Null(error);
    }

    /// <summary>
    /// THE ONE THAT MATTERS for the Radmin assistant: its footer's checkbox has a width.
    ///
    /// <para>The window is a hard 430 px with <c>NoResize</c>, so the footer had
    /// <c>430 - 2 - 40 = 388</c> px to hold the support pill, its 12 px margin and
    /// <c>CloseBtn</c>'s <c>MinWidth="100"</c>. In Spanish the pill alone wants ~354, which
    /// is 466 in 388 — and a <c>Grid</c> does not clip the way a <c>StackPanel</c> does, it
    /// NEGOTIATES: it took the entire 78 px deficit out of the only column that could give,
    /// the star one, and that was the checkbox. So the symptom was not a clipped pill, it was
    /// a control that vanished — and it is the only writer that sets
    /// <c>RadminAssistantSkipped</c> to true, i.e. the only thing that stops this window
    /// opening by itself on every visit to Multiplayer.</para>
    ///
    /// <para><b>Spanish explicitly</b>, for the same reason as the diagnostics row below: in
    /// English the footer fits and this test would pass over the broken layout.</para>
    /// </summary>
    [Fact]
    public void THE_ONE_THAT_MATTERS_TheRadminFooterLeavesTheCheckboxAWidth()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var window = new RadminAssistantWindow(new LauncherConfig());

                // The window's own content, at the window's own fixed width. Measuring the
                // Window would measure chrome this test does not own.
                var content = (FrameworkElement)window.Content;
                const double width = 430;
                content.Measure(new Size(width, double.PositiveInfinity));
                content.Arrange(new Rect(0, 0, width, content.DesiredSize.Height));
                content.UpdateLayout();

                Assert.True(window.DontShowAgainCheck.ActualWidth > 1,
                    "the footer squeezed 'No mostrar de nuevo' to "
                    + $"{window.DontShowAgainCheck.ActualWidth:0} px. It is the star column, so it "
                    + "absorbs whatever the fixed columns take — and it is the only control that "
                    + "can set RadminAssistantSkipped, so losing it means the assistant reopens "
                    + "itself forever with no way to say no.");

                // And the button it shares the row with still ends inside the window.
                var closeOrigin = window.CloseBtn.TranslatePoint(new Point(0, 0), content);
                Assert.True(closeOrigin.X + window.CloseBtn.ActualWidth <= width + 0.5,
                    $"Cerrar ends at {closeOrigin.X + window.CloseBtn.ActualWidth:0} in a "
                    + $"{width:0} px window, so it is cut off by the window edge — the gold "
                    + "stripe in the report.");

                // The pill is out of the footer, which is what made room for both.
                Assert.False(
                    IsInside(window.SupportLinkHost, window.CloseBtn.Parent as DependencyObject),
                    "the support pill is back in the footer. It is built to sit alone on its "
                    + "line, three of its four hosts use it that way, and this row cannot hold "
                    + "it plus a checkbox plus a button at 430 px.");
            }
            finally
            {
                Strings.SetLanguage(previous);
            }
        });
        Assert.Null(error);
    }

    /// <summary>Whether <paramref name="child"/> sits anywhere under <paramref name="root"/>.</summary>
    private static bool IsInside(DependencyObject child, DependencyObject? root)
    {
        if (root == null) return false;
        for (var at = System.Windows.Media.VisualTreeHelper.GetParent(child); at != null;
             at = System.Windows.Media.VisualTreeHelper.GetParent(at))
        {
            if (ReferenceEquals(at, root)) return true;
        }
        return false;
    }

    /// <summary>
    /// THE ONE THAT MATTERS for the entrant table: <b>a row stays inside its card</b>.
    ///
    /// <para>It did not, and the direction is the surprise. The action column is a FIXED
    /// 190 px and the strip inside it is a horizontal <c>StackPanel</c>, which measures its
    /// children at infinite width; its own <c>DesiredSize</c> is clamped back to 190, so the
    /// Grid is told it fits. Measured, the strip is <b>396 px</b>. Because it is right-aligned
    /// it is then arranged from x=87 — <b>to the LEFT of the name, which starts at 47</b>.
    /// It does not spill past the card: it spills BACKWARDS across its own row and paints on
    /// top of the two columns beside it.</para>
    ///
    /// <para><b>Spanish explicitly</b>, like the diagnostics row below: "Descalificar" plus
    /// "Hacer co-organizador" wants ~260 px where the English pair wants ~40 less and fits, so
    /// in English this test would pass over the broken layout.</para>
    ///
    /// <para>And the WORST case, not the common one: a pending entrant, seen by the owner of a
    /// running tournament, whose captain is not yet a co-organiser, is offered four actions at
    /// once — Accept and Reject from one chain, and Disqualify and Make co-organiser from two
    /// independent <c>if</c>s that stack on top.</para>
    /// </summary>
    [Fact]
    public void THE_ONE_THAT_MATTERS_AnEntrantRowStaysInsideItsCard()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var tab = new WarsOfLibertyLauncher.Controls.MultiplayerTab();

                // Every action at once: pending (Accept + Reject), running and owned by me
                // (Disqualify), and a captain who is not a manager yet (Make co-organiser).
                var entrant = new TournamentEntrant
                {
                    Id = "e1",
                    Kind = "solo",
                    DisplayName = "Rioplatense",
                    Status = "pending",
                    Seed = 12,
                    CaptainUserId = "u-e1",
                    MemberIds = new List<string> { "u-e1" },
                };
                var t = new TournamentDetail
                {
                    Id = "c1",
                    Name = "Copa",
                    Status = "running",
                    OwnerUserId = "me",
                    Capacity = 8,
                    Entrants = new List<TournamentEntrant> { entrant },
                };

                var list = (FrameworkElement)tab.BuildEntrantsList(t, "me");

                // The narrowest the card can be: the window's own MinWidth, minus the nav
                // rail, the 300 px tournament list and the detail pane's margins. The card
                // caps at 760 but never gets it at this size.
                const double narrowest = 900 - 64 - 300 - 21;
                list.Measure(new Size(narrowest, double.PositiveInfinity));
                list.Arrange(new Rect(0, 0, narrowest, list.DesiredSize.Height));
                list.UpdateLayout();

                // Where the status column ends. Found by the row's own content rather than by
                // arithmetic over the column widths, so the test keeps meaning what it says if
                // those widths are ever retuned.
                var statusEnd = VisualsUnder(list).OfType<TextBlock>()
                    .Where(x => x.Text == Strings.Get("MpTournamentAskedToEnter"))
                    .Select(x => x.TranslatePoint(new Point(0, 0), list).X + x.ActualWidth)
                    .DefaultIfEmpty(0)
                    .Max();
                Assert.True(statusEnd > 0, "the status cell is gone; this guard has to move.");

                var buttons = VisualsUnder(list).OfType<Button>().ToList();
                Assert.NotEmpty(buttons);

                foreach (var button in buttons)
                {
                    var origin = button.TranslatePoint(new Point(0, 0), list);
                    var right = origin.X + button.ActualWidth;

                    // Backwards, over its neighbours - the reported symptom.
                    Assert.True(origin.X >= statusEnd,
                        $"'{button.Content as string ?? "?"}' starts at {origin.X:0}, before the "
                        + $"status column ends at {statusEnd:0} - so it is painted ON TOP of "
                        + "the row's own name and status. A horizontal StackPanel measures at "
                        + "infinity and is then arranged at its full width from a right-aligned "
                        + "edge, so a fixed column cannot hold it back.");

                    // And forwards, past the card, which is the same defect mirrored.
                    Assert.True(right <= narrowest + 0.5,
                        $"'{button.Content as string ?? "?"}' ends at {right:0} in a "
                        + $"{narrowest:0} px pane, so it is painted outside the card.");
                }

                // THE OTHER HALF, because the cheap way to pass the assertions above is to
                // delete the actions. The two long ones moved into a menu; they did not go.
                var overflow = buttons.FirstOrDefault(b => b.ContextMenu != null);
                Assert.NotNull(overflow);
                var items = overflow!.ContextMenu!.Items.OfType<MenuItem>()
                    .Select(i => i.Header as string ?? "")
                    .ToList();
                Assert.Contains(Strings.Get("MpTournamentDisqualify"), items);
                Assert.Contains(Strings.Get("MpTournamentMakeManager"), items);

                // And the row's own reason for existing stays a button, not a menu entry: a
                // request waiting on a yes or a no is the one thing here that is urgent.
                var captions = buttons.Select(b => b.Content as string ?? "").ToList();
                Assert.Contains(Strings.Get("MpTournamentAccept"), captions);
                Assert.Contains(Strings.Get("MpTournamentReject"), captions);
            }
            finally
            {
                Strings.SetLanguage(previous);
            }
        });
        Assert.Null(error);
    }

    /// <summary>
    /// THE PROMISE ONE. A card row offers to expand only when the mod actually says something
    /// about it.
    ///
    /// <para>Roughly half of a real table is unit shipments and crates. They carry no
    /// <c>RolloverTextID</c>, and the engine has no wording for an effect aimed at the player,
    /// so there is genuinely nothing to show — and a caret that opens onto nothing is a promise
    /// the data cannot keep. Those rows stay inert; the ones with something to say become
    /// buttons.</para>
    /// </summary>
    [Fact]
    public void THE_PROMISE_ONE_ADeckRowIsClickableOnlyWhenItHasSomethingToSay()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();

            var row = new DeckCardRow("HCXPRefrigeration", "Refrigeration", 3, null);

            // Nothing to say: no button, no caret.
            var silent = Laid(MultiplayerTab.BuildDeckCardRow(row, VocabularyWith()));
            Assert.IsNotType<Button>(silent);
            Assert.DoesNotContain(VisualsUnder(silent).OfType<TextBlock>(),
                t => (t.Text ?? "").Contains('\u25b8') || (t.Text ?? "").Contains('\u25be'));

            // Something to say: a button, and a closed caret.
            var speaking = Laid(MultiplayerTab.BuildDeckCardRow(
                row, VocabularyWith("Delivers 10 Cheriks"), open: false, onToggle: () => { }));
            Assert.IsType<Button>(speaking);
            Assert.Contains(VisualsUnder(speaking).OfType<TextBlock>(),
                t => (t.Text ?? "").Contains('\u25b8'));

            // Closed, the text is not on screen; opened, it is. That is the whole feature.
            Assert.DoesNotContain(VisualsUnder(speaking).OfType<TextBlock>(),
                t => (t.Text ?? "").Contains("Delivers 10 Cheriks"));

            var opened = Laid(MultiplayerTab.BuildDeckCardRow(
                row, VocabularyWith("Delivers 10 Cheriks"), open: true, onToggle: () => { }));
            Assert.Contains(VisualsUnder(opened).OfType<TextBlock>(),
                t => (t.Text ?? "").Contains("Delivers 10 Cheriks"));
            Assert.Contains(VisualsUnder(opened).OfType<TextBlock>(),
                t => (t.Text ?? "").Contains('\u25be'));
        });
        Assert.Null(error);
    }

    /// <summary>
    /// Lays an element out before its visual tree is walked. A freshly built control has no
    /// visual children until it is measured, so an assertion over them would pass for the
    /// wrong reason - by finding nothing at all.
    /// </summary>
    private static FrameworkElement Laid(FrameworkElement element)
    {
        element.Measure(new Size(760, double.PositiveInfinity));
        element.Arrange(new Rect(0, 0, 760, element.DesiredSize.Height));
        element.UpdateLayout();
        return element;
    }

    /// <summary>A vocabulary whose single card says exactly these lines.</summary>
    private static DeckCardNames.Vocabulary VocabularyWith(params string[] lines)
        => new(
            new Dictionary<string, CardDetail>(StringComparer.Ordinal)
            {
                ["HCXPRefrigeration"] = new CardDetail("Refrigeration", null, null),
            },
            new Dictionary<string, System.Windows.Media.ImageSource>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            lines.Length == 0
                ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["HCXPRefrigeration"] = lines,
                });

    /// <summary>
    /// THE SYMMETRY ONE. The status column starts at the same x on every row of a card, and
    /// on its header.
    ///
    /// <para>It did not. <c>BuildEntrantGrid</c> hands out a NEW Grid per row, so the Auto
    /// actions column was measured per row and took its width out of the star column beside
    /// it. A row with "Withdraw" therefore gave up star width that the row under it kept, and
    /// the status column - whose left edge is 46 plus whatever the star came to - landed
    /// somewhere different on each. "In" beside a button and "No seed" without one could never
    /// line up, and the STATUS heading, on a row with no actions at all, sat furthest right of
    /// the lot.</para>
    ///
    /// <para>Measured against the HEADING, deliberately: the heading is the column's promise,
    /// and a table whose rows agree with each other but not with their own header is still
    /// wrong.</para>
    /// </summary>
    [Fact]
    public void THE_SYMMETRY_ONE_EveryStatusStartsUnderTheStatusHeading()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var previous = Strings.Language;
            try
            {
                // Spanish: the longest captions, which is what made the columns diverge.
                Strings.SetLanguage("es");
                var tab = new WarsOfLibertyLauncher.Controls.MultiplayerTab();

                // The three shapes that used to disagree: a confirmed entrant with a Withdraw
                // button (it is me), one with only the overflow, and one with no seed at all.
                var t = new TournamentDetail
                {
                    Id = "c1",
                    Name = "Copa",
                    Status = "registration",
                    OwnerUserId = "someone-else",
                    Capacity = 8,
                    Entrants = new List<TournamentEntrant>
                    {
                        new()
                        {
                            Id = "e1", Kind = "solo", DisplayName = "Gorgo", Status = "confirmed",
                            Seed = 1, CaptainUserId = "me", MemberIds = new List<string> { "me" },
                        },
                        new()
                        {
                            Id = "e2", Kind = "solo", DisplayName = "Aluclown", Status = "confirmed",
                            Seed = 2, CaptainUserId = "u2", MemberIds = new List<string> { "u2" },
                        },
                        new()
                        {
                            Id = "e3", Kind = "solo", DisplayName = "Vandalia", Status = "confirmed",
                            Seed = null, CaptainUserId = "u3", MemberIds = new List<string> { "u3" },
                        },
                    },
                };

                var list = (FrameworkElement)tab.BuildEntrantsList(t, "me");

                const double narrowest = 900 - 64 - 300 - 21;
                list.Measure(new Size(narrowest, double.PositiveInfinity));
                list.Arrange(new Rect(0, 0, narrowest, list.DesiredSize.Height));
                list.UpdateLayout();

                // Every status label, plus the heading, by their own text rather than by
                // arithmetic over the column widths - so this keeps meaning what it says if
                // those widths are ever retuned.
                var wanted = new[]
                {
                    Strings.Get("MpTournamentColStatus"),
                    Strings.Get("MpTournamentNoSeed"),
                    EntrantStatusText("confirmed"),
                };

                var lefts = VisualsUnder(list).OfType<TextBlock>()
                    .Where(x => wanted.Contains(x.Text))
                    .Select(x => (x.Text, X: x.TranslatePoint(new Point(0, 0), list).X))
                    .ToList();

                // The three shapes really are all on screen, or this would pass over an empty
                // table.
                Assert.Contains(lefts, l => l.Text == Strings.Get("MpTournamentColStatus"));
                Assert.Contains(lefts, l => l.Text == Strings.Get("MpTournamentNoSeed"));
                Assert.True(lefts.Count >= 4, $"only {lefts.Count} status cells were drawn.");

                var heading = lefts.First(l => l.Text == Strings.Get("MpTournamentColStatus")).X;
                foreach (var (text, x) in lefts)
                {
                    // The status dot sits before the label in a row and not in the header, so
                    // the label itself is offset by the dot's width there. What must hold is
                    // that every ROW agrees, and that none of them wanders off the heading.
                    Assert.True(System.Math.Abs(x - heading) < 20,
                        $"\"{text}\" starts at {x:0} but the STATUS heading is at {heading:0}. "
                        + "The actions column is being measured per row again, so the star "
                        + "column - and with it the status column's left edge - is a different "
                        + "width on every row.");
                }

                // And the rows agree with each OTHER exactly, dot and all.
                var rowLefts = lefts
                    .Where(l => l.Text != Strings.Get("MpTournamentColStatus"))
                    .Select(l => l.X)
                    .ToList();
                Assert.True(rowLefts.Max() - rowLefts.Min() < 0.5,
                    $"the status labels start between {rowLefts.Min():0} and {rowLefts.Max():0}; "
                    + "they are meant to be one column.");
            }
            finally
            {
                Strings.SetLanguage(previous);
            }
        });
        Assert.Null(error);
    }

    /// <summary>The label a confirmed entrant's status cell carries.</summary>
    private static string EntrantStatusText(string status) =>
        Strings.Get("MpTournamentEntrantConfirmed");

    /// <summary>
    /// The DIAGNOSTICS row of the mod properties window has to fit its three actions at the
    /// window's narrowest, in the widest language.
    ///
    /// <para>It did not: "¿Necesitas ayuda? Pregunta en Discord" is 36 characters against the
    /// English 25, and a horizontal <c>StackPanel</c> measures its children with INFINITE
    /// width, so nothing negotiates — the pill asked for its full width, was arranged at it,
    /// and the card clipped it mid-word. Nothing in that row trims and a Button cannot
    /// ellipsise its own caption. Same failure, same shape, as the rooms toolbar and the
    /// Workshop's filter chips.</para>
    ///
    /// <para><b>Spanish explicitly</b>: in English the row fits with room to spare and this
    /// test passes over a broken layout.</para>
    /// </summary>
    [Fact]
    public void TheDiagnosticsRowFitsAtTheNarrowestWindow()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");

                // The structural half. Measuring alone cannot catch this class of defect —
                // DesiredSize is clamped to the constraint, so an overflow reports as a fit —
                // which is why the panel TYPE is what is asserted.
                var pill = SupportLink.Build(11.5);
                var row = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var caption in new[]
                         {
                             Strings.Get("ModPropViewLogs"),
                             Strings.Get("ModPropShareDiagnostics"),
                         })
                {
                    row.Children.Add(new Button
                    {
                        Content = caption,
                        Style = (Style)Application.Current!.Resources["SetActionButtonLg"],
                        Width = double.NaN,
                        MinWidth = 120,
                        Padding = new Thickness(14, 0, 14, 0),
                        Margin = new Thickness(0, 0, 10, 8),
                    });
                }
                row.Children.Add(pill);

                // The narrowest the row can be: the mod window's MinWidth, minus its rail,
                // the content padding, the card border and the row padding.
                const double narrowest = 780 - 206 - 40 - 2 - 28;
                row.Measure(new Size(narrowest, double.PositiveInfinity));
                row.Arrange(new Rect(0, 0, narrowest, row.DesiredSize.Height));
                row.UpdateLayout();

                // Every child inside the row's own bounds. A WrapPanel that has to wrap is a
                // pass; a child hanging past the right edge is the reported defect.
                foreach (FrameworkElement child in row.Children)
                {
                    var origin = child.TranslatePoint(new Point(0, 0), row);
                    // Name the offender in words: the pill's Content is a StackPanel of three
                    // TextBlocks, so printing it says nothing about which control ran over.
                    var who = (child as ContentControl)?.Content as string ?? "the support pill";
                    Assert.True(origin.X + child.ActualWidth <= narrowest + 0.5,
                        $"'{who}' ends at {origin.X + child.ActualWidth:0} in a {narrowest:0} px "
                        + "row, so it is clipped by the card. A horizontal StackPanel measures at "
                        + "infinity and nothing here can trim — the row has to wrap.");
                }
            }
            finally
            {
                Strings.SetLanguage(previous);
            }
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The two post-match panels the guest sees are assembled in code, exactly like the card and
    /// the support pill, so a green build is no evidence either one can be shown.
    ///
    /// <para>They resolve <c>MpTextFaint</c>, <c>FontSizeCaption</c> and <c>MpSecondaryButton</c>
    /// by name, and they appear at the one moment where a throw is most expensive: the player has
    /// just finished a rated match and is waiting to be told who won. Nothing else would exercise
    /// them — the smoke launch only opens MainWindow, and these live in the lobby window, which is
    /// not built until somebody signs in and enters a room.</para>
    /// </summary>
    [Fact]
    public void ThePostMatchWaitingPanelsResolveTheirResources()
    {
        var error = RunOnStaThread(() =>
        {
            // Mirrors ShowResultWaitingForHost / ShowResultUnavailable. Those are private methods
            // on MultiplayerTab and need a live lobby window, so what is pinned here is the part
            // that can actually fail on its own: every resource they look up by name exists.
            var faint = Application.Current.FindResource("MpTextFaint");
            var caption = Application.Current.FindResource("FontSizeCaption");
            var secondary = Application.Current.FindResource("MpSecondaryButton");

            Assert.IsAssignableFrom<System.Windows.Media.Brush>(faint);
            Assert.IsType<double>(caption);
            var style = Assert.IsType<Style>(secondary);

            var button = new Button { Content = "x", Style = style };
            var text = new TextBlock
            {
                Text = "x",
                Foreground = (System.Windows.Media.Brush)faint,
                FontSize = (double)caption,
            };
            var stack = new StackPanel();
            stack.Children.Add(text);
            stack.Children.Add(button);

            Assert.Equal(2, stack.Children.Count);
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The per-player line under a History row, built for real.
    ///
    /// <para>Assembled in code like <see cref="MatchResultCard"/>, so a resource key that
    /// does not resolve throws when it is BUILT — and the History subtab is not a surface
    /// the startup smoke test ever reaches. The three verdicts take different branches: only
    /// the decided ones paint a verdict at all, and each reaches a different brush.</para>
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.0)]
    [InlineData(0.5)]
    public void HistoryPlayerRow_BuildsForEveryVerdict(double result)
    {
        var error = RunOnStaThread(() =>
        {
            var line = MatchParticipantsView.Build(new List<MatchHistoryParticipant>
            {
                new()
                {
                    UserId = "me", DisplayName = "Gorgorito", DiscordUsername = "gorgorito",
                    Result = result, RatingBefore = 1617, RatingAfter = 1500,
                },
            }, "me").Single();

            Assert.NotNull(MultiplayerTab.BuildHistoryPlayerRow(line));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// One row of the Clasificación table, built for real, in both of its states.
    ///
    /// <para>Same reason as the player line below: it is assembled in code, so a resource key
    /// that does not resolve throws when it is BUILT and nothing at compile time can see it —
    /// and this table is only ever drawn after somebody signs in and opens a subtab the
    /// startup smoke test never reaches. The two branches paint different brushes (first place
    /// is gold, the viewer's own row is tinted and blue) and different bar lengths.</para>
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(7, true)]
    public void RankingRow_Builds(int rank, bool isMe)
    {
        var error = RunOnStaThread(() =>
        {
            var tab = new MultiplayerTab();
            var row = new LeaderboardRow
            {
                Rank = rank,
                UserId = "me",
                DisplayName = "Gorgorito12",
                DiscordUsername = "gorgorito_12",
                Rating = 1383,
                Rd = 286,
                GamesPlayed = 6,
                Wins = 2,
                Losses = 4,
            };

            Assert.NotNull(tab.BuildLeaderboardRow(row, 1383, 1604, isMe));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// A history card, in the two shapes that take different branches: one that counted, with
    /// a roster and a delta, and one that did not — which is the one that also builds the
    /// amber note and the neutral tag, and is the majority of stored matches.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HistoryCard_Builds(bool rated)
    {
        var error = RunOnStaThread(() =>
        {
            var tab = new MultiplayerTab();
            var row = new MatchHistoryRow
            {
                Id = "m1",
                ModId = "wol",
                MapName = "ESOC_Fertile_Crescent",
                StartedAt = "2026-08-29T17:50:00Z",
                EndedAt = "2026-08-29T19:09:00Z",
                Result = rated ? 0.0 : 0.5,
                Rated = rated,
                UnratedReason = rated ? null : "not_competitive",
                RatingBefore = rated ? 1500 : null,
                RatingAfter = rated ? 1383 : null,
                Participants = new List<MatchHistoryParticipant>
                {
                    new() { UserId = "me", DisplayName = "Gorgorito12", Result = rated ? 0.0 : 0.5 },
                    new() { UserId = "alu", DisplayName = "Aluclown", Result = rated ? 1.0 : 0.5 },
                },
            };

            Assert.NotNull(tab.BuildHistoryRow(row, "me"));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The profile header — the one card that carries a gradient, a rounded-square avatar and
    /// the 30-px rating, none of which appears anywhere else in the launcher.
    ///
    /// <para>Built with NO standing on purpose: that is the state a player sees while the
    /// fetch is in flight, and it is the branch that omits elements rather than painting
    /// them, which makes it the one most likely to be wrong and never noticed.</para>
    /// </summary>
    [Fact]
    public void ProfileHeader_BuildsWithNoStandingYet()
    {
        var error = RunOnStaThread(() =>
        {
            var tab = new MultiplayerTab();
            Assert.NotNull(tab.BuildProfileHeader(new LobbyUserSummary
            {
                Id = "me",
                DisplayName = "Gorgorito12",
                DiscordUsername = "gorgorito_12",
                CreatedAt = "2026-08-01T00:00:00Z",
            }));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The degraded shape: somebody else, no avatar, no rating either side, nothing decided.
    /// Every one of those is a branch that omits an element rather than painting one, which
    /// makes this the row most likely to be built wrong and never noticed.
    /// </summary>
    [Fact]
    public void HistoryPlayerRow_BuildsWithNothingKnown()
    {
        var error = RunOnStaThread(() =>
        {
            var line = new MatchParticipantLine(
                "someone", "?", null, IsMe: false, MatchVerdict.NoResult, RatingDelta: null);

            Assert.NotNull(MultiplayerTab.BuildHistoryPlayerRow(line));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The install dialog, with and without a mod to copy settings from.
    ///
    /// <para>The dialog gained an optional row, and <b>the case that matters is the empty
    /// one</b>: somebody installing their first mod has nothing to copy from, so the row must
    /// collapse rather than offer an empty combo. Getting that backwards would put a dead
    /// control in front of every new player, in the one dialog they cannot avoid.</para>
    ///
    /// <para>It is also the only automated cover this window has — the startup smoke test never
    /// opens it, so a resource key that does not resolve would first be seen by someone trying
    /// to install.</para>
    ///
    /// <para><b>NO AoE3 source is passed, and that is not incidental — do not "improve" it by
    /// adding one.</b> With a source the constructor kicks <c>MeasureCloneSizeAsync</c>, whose
    /// continuation comes back through the captured SynchronizationContext. This harness runs a
    /// bare STA thread with no Dispatcher, so there is none: the continuation resumes on the
    /// thread pool, touches a TextBox it does not own, and the unhandled exception takes down the
    /// whole test host — every other test in the run disappears with it, and the run still
    /// reports success with a smaller total. Passing no source keeps that method synchronous,
    /// which is exactly the shape an in-place overlay install uses anyway.</para>
    /// </summary>
    [Fact]
    public void InstallFolderDialog_HidesTheCopySettingsRowWithNoSources()
    {
        var error = RunOnStaThread(() =>
        {
            var none = new InstallFolderDialog(
                @"C:\Games\Wars of Liberty", null, null,
                "Wars of Liberty", requiresAoe3Source: false, settingsSources: null);
            Assert.Equal(Visibility.Collapsed, none.CopySettingsRow.Visibility);
            none.Close();

            var some = new InstallFolderDialog(
                @"C:\Games\Wars of Liberty", null, null,
                "Wars of Liberty", requiresAoe3Source: false,
                settingsSources: new List<ModProfile>
                {
                    new() { Id = "improvement-mod", DisplayName = "Improvement Mod" },
                });
            Assert.Equal(Visibility.Visible, some.CopySettingsRow.Visibility);
            Assert.Single(some.CopySettingsCombo.Items);

            // Unticked, and the combo inert with it: the default has to be "don't copy". A row
            // that arrives already agreeing to write into the player's profile is not a question.
            Assert.False(some.CopySettingsCheck.IsChecked);
            Assert.False(some.CopySettingsCombo.IsEnabled);
            some.Close();
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The whole Multiplayer tab, parsed.
    ///
    /// <para>This is the broadest guard in the file and the cheapest: constructing the
    /// control runs <c>InitializeComponent</c>, which parses every <c>{StaticResource}</c>
    /// in the largest XAML in the launcher, and then <c>ApplyStrings</c>, which touches
    /// dozens of named elements. A key that does not resolve throws HERE instead of on a
    /// player's screen.</para>
    ///
    /// <para>It is the only automated cover for resources reached by the XAML alone and by
    /// no code-built card — <c>MpActivityHeadlineSize</c> is one. The tab does live inside
    /// MainWindow, so the startup smoke test would also catch it, but that test cannot run
    /// while a launcher is already open: the single-instance guard makes the second process
    /// exit successfully without parsing anything.</para>
    ///
    /// <para>Safe to construct with no session: the constructor only lays itself out and
    /// wires handlers. Everything that needs a backend waits for <c>Attach</c>.</para>
    /// </summary>
    [Fact]
    public void MultiplayerTab_ParsesItsWholeXaml()
    {
        var error = RunOnStaThread(() =>
        {
            var tab = new MultiplayerTab();
            Assert.NotNull(tab.Content);

            // The strip's own pieces, by name: renaming one of these breaks
            // LayOutActivityColumns SILENTLY at runtime, since it finds them by field.
            Assert.NotNull(tab.ActivityStrip);
            Assert.NotNull(tab.ActivityColPeak);
            Assert.NotNull(tab.ActivityColRecent);
            Assert.NotNull(tab.ActivityColMiddle);
            // The gaps replaced the vertical rules when the strip went to the handoff's
            // three cards, and they collapse with their card for the same reason the
            // columns do — so losing one of these names breaks the layout just as quietly.
            Assert.NotNull(tab.ActivityGapLeft);
            Assert.NotNull(tab.ActivityGapRight);
            Assert.NotNull(tab.ActivityMiddleCard);
            Assert.NotNull(tab.ActivityStripTotals);
            Assert.NotNull(tab.ActivityRankingEmpty);
            Assert.NotNull(tab.ActivityRankingSeeAll);
            Assert.NotNull(tab.ActivityPeakLine);

            // NONE of the three cards may stretch. They share one grid row, where a Border
            // fills the row by default — so the shortest card was drawn as tall as the
            // tallest, which painted the ranking as a ~200-px empty box under two lines of
            // text. Measured on this very tree: stretched, all three came out at 297 px;
            // top-aligned they are 129 / 225 / 110. Losing this property costs no build
            // error and no test but the one, and looks like the panel grew back.
            foreach (var card in new[]
                     { tab.ActivityPeakCard, tab.ActivityRecentCard, tab.ActivityMiddleCard })
            {
                Assert.Equal(VerticalAlignment.Top, card.VerticalAlignment);
            }
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The rooms top bar's two groups — the subtabs and the tool cluster — must FIT side by side
    /// at the narrowest window the app allows.
    ///
    /// <para>They share one 48-px row as `*` + `Auto`, and NEITHER has TextTrimming. So the Auto
    /// cluster takes its full width first and the star strip is arranged at its desired size and
    /// then clipped at the column edge, with the cluster painting over the same pixels. The
    /// symptom is the CLASIFICACION tab reading "CLAS" with the room-code box sitting on top of
    /// it, which is what shipped: adding that field cost ~154 px this row did not have.</para>
    ///
    /// <para><b>Measured in Spanish, which is the wide language</b> (CLASIFICACION vs RANKING is
    /// 93 px against 58). And measured against a FIXED budget rather than the window, because
    /// UiScale lays this tab out at a scaled logical size: the transform pins the logical bar at
    /// ~1072 px for every window between the 900-px minimum and the 1100-px default, so that is
    /// simultaneously the worst case and the common one — making the window smaller does not
    /// make this worse, and the default size is already it.</para>
    ///
    /// <para>Nothing else can catch this. It is not an overflow (a star column that shrinks
    /// reports nothing, the same blindness the tab's own overflow diagnostic has), it throws
    /// nothing, and it looks fine on a wide monitor.</para>
    ///
    /// <para><b>AND IT WAS ITSELF BLIND FOR A WHILE, which is worth knowing before trusting a
    /// number this test reports.</b> The three text buttons in the cluster take their size from
    /// MpSecondaryButton / MpPrimaryButton, whose Setter said <c>{StaticResource FontSizeBody}</c>
    /// — and in this harness that reference did not resolve, so they measured at the WPF default
    /// of 12 while the shipped app painted them at 14. The bar was therefore ~22 px over budget
    /// in reality and passing here. Moving every font size to <c>{DynamicResource}</c> for the
    /// text-size setting (see <c>TextScaleTests</c>) fixed the harness, the overlap appeared, and
    /// the cluster's captions were taken down to the multiplayer scale they should always have
    /// been on. A green result here means what it says only because the harness now measures the
    /// same sizes the app paints.</para>
    /// </summary>
    /// <summary>
    /// The three rebuilt multiplayer pages FILL the window — and the ladder's flexible column
    /// is the one that can absorb the surplus.
    ///
    /// <para><b>Both halves belong in one test because either alone can be satisfied by
    /// breaking the other.</b> Filling the window is what was asked for, three rounds running;
    /// what makes it safe is that RATING grows and PLAYER is capped, so a wide window lengthens
    /// the comparative bar instead of stranding a name 1500 px from its own rating. Flip the
    /// flexible column back to PLAYER — the obvious reading of the handoff's fixed-width mockup
    /// — and the pages still "fill the window" while reproducing the exact defect the rebuild
    /// started from, with a green build and no error anywhere.</para>
    ///
    /// <para>The page assertions are one XAML attribute each, which is the other reason: a
    /// tidy-up that puts a MaxWidth back reads as harmless in a diff.</para>
    /// </summary>
    [Fact]
    public void TheMultiplayerPagesFillTheWindowAndTheLadderGrowsByItsBar()
    {
        var error = RunOnStaThread(() =>
        {
            var tab = new MultiplayerTab();
            // The profile page lives in ProfileWindow now — the rule it is checked against is
            // unchanged, only its address is.
            var profileWindow = new ProfileWindow();

            foreach (var (name, page) in new (string, FrameworkElement)[]
                     {
                         ("Ranking", tab.RankingPage),
                         ("Profile", profileWindow.ProfileBody),
                     })
            {
                Assert.True(double.IsPositiveInfinity(page.MaxWidth),
                    $"{name} is bounded to {page.MaxWidth}: these pages fill the window now, "
                    + "and the bounding is what left more than half of it empty.");

                Assert.True(page.HorizontalAlignment == HorizontalAlignment.Stretch,
                    $"{name} is {page.HorizontalAlignment}, not Stretch, so it cannot fill "
                    + "the width it is given.");
            }

            var flexible = RankingTableLayout.All.Where(c => c.FixedWidth == null).ToList();
            Assert.Equal(2, flexible.Count);

            var player = RankingTableLayout.All.Single(c => c.Column == RankingColumn.Player);
            var rating = RankingTableLayout.All.Single(c => c.Column == RankingColumn.Rating);

            Assert.True(player.MaxWidth is > 0,
                "PLAYER has no cap, so on a wide window the name takes the whole surplus and "
                + "its rating ends up an arm's length away — the defect this table was rebuilt "
                + "to fix.");
            Assert.True(rating.FixedWidth == null && rating.MaxWidth == null,
                "RATING is not the column that grows. Its cell holds the comparative bar, "
                + "which is the only thing here that gets MORE useful with more width.");

            profileWindow.Close();
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The Workshop's filter strip cannot be painted over by the sort cluster.
    ///
    /// <para><b>Why this exists.</b> That row is <c>* | Auto</c> — the sort cluster takes its
    /// width first and whatever is in the star column is arranged at its own desired size and
    /// clipped at the column edge, with the cluster drawing over the same pixels. Nothing in
    /// the strip trims and a Button cannot ellipsise its caption, so as a horizontal
    /// StackPanel the chips had no way to give ground. It was invisible because a UiScale
    /// LayoutTransform pinned the whole Workshop's logical width at ~1100 px for every window
    /// from 900 up; with that gone the row gets the real width — about 852 px at the minimum
    /// window — and any text size above 100 % pushes it over.</para>
    ///
    /// <para><b>The type assertion is the load-bearing half, and the numbers cannot replace
    /// it.</b> <c>Measure</c> clamps <c>DesiredSize</c> to the constraint it is given, so a
    /// StackPanel that overflows reports a width that fits — the overflow is simply not
    /// visible from here. What IS checkable is that the strip is a panel that WRAPS, so it
    /// cannot overflow by construction, and that the Auto cluster still leaves the first line
    /// room for the label and the widest chip.</para>
    /// </summary>
    [Fact]
    public void TheWorkshopFiltersRowFitsAtTheNarrowestWindow()
    {
        var error = RunOnStaThread(() =>
        {
            var previous = Strings.Language;
            try
            {
                // Spanish is the wide language here: "Actualizaciones", "No instalados".
                Strings.SetLanguage("es");
                var browser = new ModsBrowser();

                // The captions come from MainWindow, so the harness has to supply them or
                // every chip measures as an empty button.
                browser.FiltersLabelText = Strings.Get("ModsBrowserFiltersLabel");
                browser.SortLabelText = Strings.Get("ModsBrowserSortLabel");
                browser.SetFilterLabels(
                    Strings.Get("ModsBrowserFilterAll"),
                    Strings.Get("ModsBrowserFilterInstalled"),
                    Strings.Get("ModsBrowserFilterNotInstalled"),
                    Strings.Get("ModsBrowserFilterUpdates"),
                    Strings.Get("ModsBrowserFilterCompatible"));

                var strip = LogicalTreeHelper.GetParent(browser.FilterAll);
                Assert.True(strip is WrapPanel,
                    $"the filter strip is a {strip?.GetType().Name}, not a WrapPanel. In a "
                    + "`* | Auto` row nothing else can give ground: the chips do not trim and "
                    + "cannot ellipsise, so on a narrow window they go UNDER the sort box. "
                    + "Measuring will not catch this — DesiredSize is clamped to the "
                    + "constraint, so the overflow reports as a fit.");

                var sort = (FrameworkElement)LogicalTreeHelper.GetParent(browser.SortBox);
                sort.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                var label = browser.FiltersLabel;
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                var widestChip = 0.0;
                foreach (var chip in new[]
                         {
                             browser.FilterAll, browser.FilterInstalled,
                             browser.FilterNotInstalled, browser.FilterUpdates,
                             browser.FilterCompatible,
                         })
                {
                    chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    widestChip = Math.Max(widestChip, chip.DesiredSize.Width);
                }

                // The 900-px minimum window, less the header's own 24-px side padding. NOT
                // divided by any scale factor — that is exactly what changed when the
                // Workshop's LayoutTransform was removed.
                const double budget = 900 - 48;
                var firstLine = sort.DesiredSize.Width + label.DesiredSize.Width + widestChip;

                Assert.True(firstLine <= budget,
                    $"the sort cluster ({sort.DesiredSize.Width:F0}), the label "
                    + $"({label.DesiredSize.Width:F0}) and the widest chip ({widestChip:F0}) "
                    + $"need {firstLine:F0} px of the {budget:F0} the narrowest window gives "
                    + "this row — so not even one chip fits beside the sort box and wrapping "
                    + "cannot save it. Take the width out of the sort box or a chip caption.");
            }
            finally { Strings.SetLanguage(previous); }
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The tournaments panel's create button fits its column, in both languages and at the
    /// biggest text size the launcher offers.
    ///
    /// <para>It used to be a bare text link with zero padding, so it fitted anything. It is a
    /// 32px filled button now, with a plus and a two-word label, sharing a <b>300px</b> column
    /// with a 17px bold heading — the same shape as the rooms top bar, and the same way to get
    /// it wrong. English is the worse case ("+ New tournament" against "+ Nuevo torneo") and
    /// 125% is the biggest offered scale, so the two are tested together.</para>
    ///
    /// <para>What is asserted is that BOTH fit — not that one of them gives way. The first
    /// version of this change kept the long "New tournament" label and let the heading lose,
    /// and at English/125% the heading came out clipped mid-word with no ellipsis, which reads
    /// as a broken screen rather than as a truncation. Shortening the label to one word is
    /// what makes both fit, and this is the tripwire for lengthening it again.</para>
    /// </summary>
    [Fact]
    public void TheTournamentsCreateButtonFitsItsColumnInBothLanguages()
    {
        var error = RunOnStaThread(() =>
        {
            var previous = Strings.Language;
            var baseline = (double)Application.Current.Resources["MpLabelSize"];
            try
            {
                foreach (var lang in new[] { "es", "en" })
                {
                    Strings.SetLanguage(lang);
                    var tab = new MultiplayerTab();

                    // The largest step the Interface setting offers, scoped to THIS tab.
                    //
                    // It used to be written into Application.Current.Resources, which every
                    // test in the process shares and which no `finally` can un-bake once a
                    // style has resolved against it: the suite grew a flake in an unrelated
                    // style test that appeared once and would not reproduce. An element-level
                    // resource overrides the application one for this subtree only, measures
                    // exactly the same thing, and dies with the tab.
                    tab.Resources["MpLabelSize"] = baseline * 1.25;

                    var header = (FrameworkElement)LogicalTreeHelper.GetParent(
                        tab.TournamentCreateButton);
                    Assert.IsType<Grid>(header);

                    header.Measure(new Size(300, double.PositiveInfinity));
                    header.Arrange(new Rect(0, 0, 300, header.DesiredSize.Height));

                    var button = tab.TournamentCreateButton;
                    var title = tab.TournamentsTitleText;

                    Assert.True(button.DesiredSize.Width > 0, $"[{lang}] the button measured to nothing");
                    Assert.True(
                        Math.Abs(button.ActualWidth - button.DesiredSize.Width) < 0.5,
                        $"[{lang}] the create button was squeezed: it wants "
                        + $"{button.DesiredSize.Width:F0} px and got {button.ActualWidth:F0}.");

                    // THE HEADING IS NOT CLIPPED EITHER. It carries no TextTrimming, so a
                    // heading that does not fit is cut mid-word with nothing to say it was cut.
                    Assert.True(
                        title.ActualWidth + 0.5 >= title.DesiredSize.Width,
                        $"[{lang}] the heading needs {title.DesiredSize.Width:F0} px and got "
                        + $"{title.ActualWidth:F0}: it will be cut mid-word with no ellipsis. "
                        + "Shorten MpTournamentCreate rather than widening the column - the "
                        + "heading already supplies the noun, so the button only needs the verb.");
                }
            }
            finally
            {
                Strings.SetLanguage(previous);
            }
        });

        Assert.Null(error);
    }

    [Fact]
    public void TheRoomsTopBarFitsAtTheNarrowestWindow()
    {
        var error = RunOnStaThread(() =>
        {
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var tab = new MultiplayerTab();

                // SubtabRanking, because SubtabRooms's logical parent is the inner Grid that
                // carries the new-room dot — anchoring there would measure a wrapper holding
                // one button and pass with a third of the real width. The type assertion is
                // what stops the next re-anchor from doing exactly that.
                var tabs = (FrameworkElement)LogicalTreeHelper.GetParent(tab.SubtabRanking);
                Assert.IsType<StackPanel>(tabs);
                var cluster = (FrameworkElement)LogicalTreeHelper.GetParent(tab.CreateRoomButton);
                tabs.Measure(new Size(double.PositiveInfinity, 48));
                cluster.Measure(new Size(double.PositiveInfinity, 48));

                // The worst case, and it is NOT the smallest window. UiScale scales this tab by
                // min(w/1100, h/560) with a 0.82 floor, so the LOGICAL width is 1100 at the
                // 1100-px default and 900/0.82 = 1097.6 at the 900-px minimum — i.e. the bar is
                // ~1098 logical px wide across that whole range, and shrinking the window does not
                // shrink it further. Less the bar's own 10-px side padding.
                const double budget = 1097.6 - 20;
                var need = tabs.DesiredSize.Width + cluster.DesiredSize.Width;

                // Two subtabs left this strip (Perfil to its own window, Amigos deleted), so
                // there is real slack now — do NOT read a permanently green test as room for
                // another tab. It is still a live tripwire for the growing side, the tool
                // cluster on the right.
                Assert.True(need <= budget,
                    $"the top bar needs {need:F0} px and has {budget:F0}: the subtab strip will be "
                    + "painted over by the tool cluster. Take the width out of padding, a caption, "
                    + "or the search box — but NOT out of the Radmin help button's word, which is "
                    + "a documented refusal.");
            }
            finally { Strings.SetLanguage(previous); }
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The rooms list may NOT have a viewport of its own.
    ///
    /// <para>It had one, and on a short window that is what reduced it to a single 64-px row.
    /// The join-by-code box and the activity strip below it are Auto rows that take their
    /// height first, so the star row holding the list absorbed the whole shortfall while the
    /// strip kept every pixel: the list scrolled inside about one row, and the page did not
    /// scroll at all.</para>
    ///
    /// <para>Both halves are pinned because both fail silently. Re-adding a ScrollViewer
    /// around <c>RoomsListPanel</c> builds clean and looks right on a big monitor; so does
    /// removing the page one. And the header strip has to sit in the SAME viewport as the
    /// rows — that is what makes the old scrollbar-gutter compensation unnecessary, and
    /// re-adding that compensation now would push the header left of the rows it labels.</para>
    /// </summary>
    [Fact]
    public void TheRoomsListScrollsWithThePageAndNeverOnItsOwn()
    {
        var error = RunOnStaThread(() =>
        {
            var tab = new MultiplayerTab();

            static IEnumerable<DependencyObject> Ancestors(DependencyObject d)
            {
                for (var p = LogicalTreeHelper.GetParent(d); p != null;
                     p = LogicalTreeHelper.GetParent(p))
                    yield return p;
            }

            var overRows = Ancestors(tab.RoomsListPanel).OfType<ScrollViewer>().ToList();
            Assert.Single(overRows);
            Assert.Same(tab.RoomsPageScroll, overRows[0]);

            // ...and it is the same one for every part of the page: the column headers (or
            // the gutter compensation comes back), the footer and the strip. The join-by-code
            // field used to be here too and is deliberately NOT any more — it lives in the
            // toolbar now, outside the scroller, which is the point of having moved it.
            foreach (FrameworkElement part in new FrameworkElement[]
                     {
                         tab.RoomsHeaderStrip, tab.RoomsShowingCount, tab.ActivityStrip,
                     })
            {
                Assert.Same(tab.RoomsPageScroll,
                    Ancestors(part).OfType<ScrollViewer>().Single());
            }

            // The rows' left inset is the header's: 16 here plus 14 of row padding makes the
            // 30 the strip is inset by. It was the deleted scroller's Padding.
            Assert.Equal(16, tab.RoomsListPanel.Margin.Left);
            Assert.Equal(16, tab.RoomsListPanel.Margin.Right);
        });

        Assert.Null(error);
    }

    /// <summary>
    /// On a window too short for everything, the ROOMS keep the height and the join box and
    /// the strip go below the fold — not the other way round.
    ///
    /// <para>Measured, not eyeballed, and deliberately not a pixel count: the claim is that
    /// the block is as tall as the rows it holds, whatever that comes to. Ten rows against a
    /// 420-px window is the reported screenshot, where the block was handed about one row.
    /// It fails on the layout this replaced, and it fails again the moment anyone divides a
    /// fixed height between a star row and an Auto one here.</para>
    /// </summary>
    [Fact]
    public void AShortWindowShrinksThePageAndNotTheRoomsList()
    {
        var error = RunOnStaThread(() =>
        {
            var tab = new MultiplayerTab();
            const int rows = 10, rowHeight = 64;
            for (var i = 0; i < rows; i++)
                tab.RoomsListPanel.Children.Add(new Border { Height = rowHeight });
            // Collapsed until its data lands; visible is the case that hurt.
            tab.ActivityStrip.Visibility = Visibility.Visible;

            // Laid out DIRECTLY, not through the tab: nobody is signed in on a bare
            // MultiplayerTab, so the sign-in gate collapses everything under it and laying
            // out the tab measures nothing at all (every height comes back 0). 420 is the
            // viewport the reported short window gives this column.
            tab.RoomsPageScroll.Measure(new Size(1100, 420));
            tab.RoomsPageScroll.Arrange(new Rect(0, 0, 1100, 420));
            tab.RoomsPageScroll.UpdateLayout();

            Assert.True(
                tab.RoomsBlock.ActualHeight >= rows * rowHeight,
                $"the rooms block was squeezed to {tab.RoomsBlock.ActualHeight:0} px for "
                + $"{rows} rows: something below it is taking the height first");
            Assert.True(tab.RoomsPageScroll.ScrollableHeight > 0, "the page did not scroll");
            // And nothing re-adds the scrollbar gutter: the header is in the same viewport as
            // the rows, so it loses the same width and its inset stays a flat 30.
            Assert.Equal(30, tab.RoomsHeaderStrip.Margin.Right);
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The community-activity strip's two code-built rows.
    ///
    /// <para>Same reason as the card above: they resolve their brushes and sizes by name at
    /// BUILD time, and the strip lives at the bottom of the Rooms subtab, which the startup
    /// smoke test never opens. They also reach the three sizes this panel does not share
    /// with the rest of the tab — <c>MpActivityTitleSize</c>, <c>MpActivityBodySize</c>,
    /// <c>MpActivityHeadlineSize</c> — so a mistyped key would be invisible until a player
    /// with a signed-in session opened the tab.</para>
    ///
    /// <para>Both branches are exercised: a decided duel writes "X beat Y" in one brush, an
    /// unreadable match writes the mod and the map in another.</para>
    /// </summary>
    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(0.5, 0.5)]
    public void CommunityMatchRow_BuildsForDecidedAndUndecided(double a, double b)
    {
        var error = RunOnStaThread(() =>
        {
            var match = new CommunityMatch
            {
                Id = "m1",
                ModId = "wol",
                MapName = "ESOC_Fertile Crescent",
                ReportedAt = DateTime.UtcNow.AddHours(-2).ToString("s"),
            };
            match.Participants.Add(new MatchHistoryParticipant
            {
                UserId = "u1", DisplayName = "Alucard", Result = a,
            });
            match.Participants.Add(new MatchHistoryParticipant
            {
                UserId = "u2", DisplayName = "Gorgorito", Result = b,
            });

            Assert.NotNull(MultiplayerTab.BuildCommunityMatchRow(match));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// THE AGE LABEL REGISTERS ITSELF, and the label it registers is the one actually in the
    /// row. Both halves matter and both fail silently.
    ///
    /// <para>Those "31 min ago" labels were computed once, when the row was built, and the row
    /// is only rebuilt by a fetch that got past a 60-second gate — so a tab left open kept
    /// saying 31 min for as long as anybody looked at it. They are ticked in place now, from
    /// the rooms ping timer, which only works if the builder hands the TextBlock back.</para>
    ///
    /// <para>Registering a block that is NOT in the row would tick something nobody can see,
    /// and nothing would look wrong — the same shape as the roster health dots, which were
    /// found by structure and silently stopped updating when that structure moved. Hence the
    /// reference check against the row's own children.</para>
    /// </summary>
    [Fact]
    public void ACommunityMatchRowHandsBackItsAgeLabel()
    {
        var error = RunOnStaThread(() =>
        {
            var cells = new List<(TextBlock Text, DateTime ReportedUtc)>();
            var reported = DateTime.UtcNow.AddMinutes(-31);
            var match = new CommunityMatch
            {
                Id = "m1",
                ModId = "wol",
                MapName = "ESOC_Fertile Crescent",
                ReportedAt = reported.ToString("s"),
            };

            var row = MultiplayerTab.BuildCommunityMatchRow(match, cells);

            var cell = Assert.Single(cells);
            Assert.Contains("31", cell.Text.Text);
            // Within a second of what was asked: the row parses the stamp itself, and a cell
            // registered against a different instant would drift away from its own label.
            Assert.True((cell.ReportedUtc - reported).Duration() < TimeSpan.FromSeconds(1));

            var grid = Assert.IsType<Grid>(row);
            Assert.Contains(grid.Children.Cast<UIElement>(), c => ReferenceEquals(c, cell.Text));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// A row whose timestamp does not parse registers NOTHING. There is no label to tick, and a
    /// cell holding a fabricated instant would invent an age for a match that never reported one
    /// — the same refusal the row already makes by omitting the column entirely.
    /// </summary>
    [Fact]
    public void AnUnreadableTimestampRegistersNoAgeLabel()
    {
        var error = RunOnStaThread(() =>
        {
            var cells = new List<(TextBlock Text, DateTime ReportedUtc)>();
            MultiplayerTab.BuildCommunityMatchRow(new CommunityMatch { ReportedAt = "no" }, cells);
            Assert.Empty(cells);
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The degraded shape: no map, no participants, and a timestamp that does not parse —
    /// every segment of the meta line absent at once, which is the row most likely to be
    /// built wrong and never seen.
    /// </summary>
    [Fact]
    public void CommunityMatchRow_BuildsWithNothingKnown()
    {
        var error = RunOnStaThread(() =>
        {
            Assert.NotNull(MultiplayerTab.BuildCommunityMatchRow(new CommunityMatch()));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The two windows a player can only ever see ONCE, on their very first launch.
    ///
    /// <para>They are the worst place in the app for an unresolved <c>{StaticResource}</c>:
    /// the smoke launch never opens either, they show before anybody has learned what the
    /// launcher looks like, and a throw there happens while the launcher is asking for
    /// permission to touch the registry. <see cref="SelfInstallPromptDialog"/> had no cover
    /// at all; the consent dialog is new.</para>
    ///
    /// <para>The assertion past construction is the one that matters for the consent window:
    /// <b>a dismissal is not a yes.</b> Only the Yes button ever sets DialogResult, so a
    /// freshly built window reports null, and the caller's <c>== true</c> reads the X, Escape
    /// and a closed window all as no.</para>
    /// </summary>
    [Fact]
    public void TheFirstLaunchWindowsLoad_AndConsentIsNeverAssumed()
    {
        var error = RunOnStaThread(() =>
        {
            var consent = new BackgroundConsentDialog();
            // Never pre-answered, in either direction.
            Assert.Null(consent.DialogResult);
            // It has to say what it is going to do and where to undo it — the balloon it
            // replaced named neither, which is half of why it was not consent.
            Assert.NotEmpty(consent.BodyText.Text);
            Assert.NotEmpty(consent.DetailText.Text);
            // And both lines have a colour that RESOLVED. The detail line was first written
            // against "TextMuted", which is not a brush this app has - an unresolved
            // DynamicResource neither fails nor warns, it just leaves the property at its
            // default, and the default Foreground is BLACK on a navy dialog. It took a
            // screenshot to see, in the one window whose whole job is to be read before
            // somebody agrees to something.
            Assert.NotEqual(Brushes.Black, consent.BodyText.Foreground);
            Assert.NotEqual(Brushes.Black, consent.DetailText.Foreground);
            consent.Close();

            var install = new SelfInstallPromptDialog();
            Assert.Null(install.DialogResult);
            install.Close();
        });

        Assert.Null(error);
    }

    /// <summary>
    /// The "you are running as another Windows account" notice.
    ///
    /// <para>Worth a case of its own because of WHERE it opens: only on a machine whose accounts
    /// are already tangled. Without this, an unresolved <c>{StaticResource}</c> in it would first
    /// be seen by the one person least able to report what happened — and the launcher would have
    /// thrown while explaining why their recordings went missing.</para>
    ///
    /// <para>The second half is the half that matters. The other account's folder is resolved
    /// exactly or not at all (see <c>RunningAccount.ProfileFolderOf</c>), so "not at all" is a
    /// normal outcome, and the caption has to disappear WITH the box — a heading over an empty
    /// field reads as a bug in the dialog rather than as a path we could not confirm.</para>
    /// </summary>
    [Fact]
    public void CrossUserAccountDialog_CollapsesTheOtherFolderWhenThereIsNone()
    {
        var error = RunOnStaThread(() =>
        {
            var info = new WarsOfLibertyLauncher.Services.RunningAccount.AccountInfo(
                "a-admin", "Miro", Elevated: true, Mismatch: true);

            var both = new CrossUserAccountDialog(
                info,
                @"C:\Users\a-admin\Documents\My Games\Wars of Liberty",
                @"C:\Users\Miro\Documents\My Games");
            Assert.Equal(Visibility.Visible, both.OtherLabel.Visibility);
            Assert.Equal(Visibility.Visible, both.OtherPathText.Visibility);
            // Both accounts are named, or the reader cannot tell which folder is which.
            Assert.Contains("a-admin", both.BodyText.Text);
            Assert.Contains("Miro", both.BodyText.Text);
            both.Close();

            var unresolved = new CrossUserAccountDialog(
                info, @"C:\Users\a-admin\Documents", null);
            Assert.Equal(Visibility.Collapsed, unresolved.OtherLabel.Visibility);
            Assert.Equal(Visibility.Collapsed, unresolved.OtherPathText.Visibility);
            unresolved.Close();
        });

        Assert.Null(error);
    }

    /// <summary>
    /// THE REVEAL IS BUILT FOR REAL, because it is assembled entirely in code and nothing else
    /// in the app would ever notice if a piece of it stopped resolving.
    ///
    /// <para><c>RevealTextTests</c> pins the decisions; this pins the object. It measures a
    /// genuinely truncated line with <c>FormattedText</c> against a real arranged width, looks
    /// up <c>RevealTooltip</c> from <c>Styles/Text.xaml</c> by name, and computes the offset
    /// that puts the revealed first glyph on top of the original's. A renamed style resolves to
    /// nothing and the reveal silently reverts to the app's ordinary tooltip — a shadowed,
    /// differently-padded box in the wrong place — which is not a crash and not a build error.
    /// </para>
    ///
    /// <para>The negative half is in the same test on purpose: the same block, given room to
    /// fit, must build NOTHING. With the behaviour armed on every trimming TextBlock in the
    /// launcher, a measurement that answered "cut" too readily would put a box over text that
    /// was perfectly legible, everywhere, at once.</para>
    /// </summary>
    [Fact]
    public void TheRevealBuildsInPlaceAndOnlyWhenTheTextIsActuallyCut()
    {
        var error = RunOnStaThread(() =>
        {
            const string full = "Mapa mas jugado: ESOC Fertile Crescent";

            var text = new TextBlock
            {
                Text = full,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                FontSize = 12,
                Padding = new Thickness(3, 1, 0, 0),
            };
            // A card with a real fill, so the backdrop walk has something to find — the reveal
            // sits directly on top of the original text and must not be see-through.
            var card = new Border
            {
                Background = (System.Windows.Media.Brush)Application.Current.FindResource("MpPanel"),
                Child = text,
            };

            // The implicit style in Styles/Text.xaml armed it, without the call site asking.
            Assert.True(RevealText.GetEnabled(text),
                "the implicit TextBlock style did not arm the behaviour");

            // Narrow: the line cannot fit, which is the case the feature exists for.
            card.Measure(new Size(90, 40));
            card.Arrange(new Rect(0, 0, 90, 40));
            // Loaded is the hook that arms it in the real app (SizeChanged carries it from
            // there). Raised by hand because a detached tree is never loaded and never runs a
            // real layout pass — what is pinned is that the handler is wired to it and does
            // the right thing with a laid-out block, which is exactly what was wrong.
            text.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            // THE REGRESSION, and the whole reason this test exists in this shape: the tooltip
            // has to be IN PLACE once the block has been laid out, with no mouse involved. The
            // first version computed it on hover and therefore never showed anything at all —
            // WPF's tooltip service inspects an element when the mouse ENTERS it, from a class
            // handler that runs before any instance handler, so a tooltip assigned during hover
            // is always assigned a moment too late.
            var tip = Assert.IsType<ToolTip>(text.ToolTip);

            // Same words, in full, and wrapped rather than trimmed — a reveal that trimmed
            // again would show exactly what the user was already looking at.
            var revealed = Assert.IsType<TextBlock>(tip.Content);
            // Read through PlainTextOf, never revealed.Text: a TextBlock whose content is runs
            // reports "" from that property, which is the trap the helper exists for.
            Assert.Equal(full, RevealText.PlainTextOf(revealed));
            Assert.Equal(TextWrapping.Wrap, revealed.TextWrapping);
            Assert.Equal(text.FontSize, revealed.FontSize);

            // Its own chrome resolved. Without the style it would inherit the app-wide tooltip
            // template, shadow and all.
            Assert.NotNull(tip.Style);
            Assert.False(tip.HasDropShadow);

            // NO DELAY, and on the TEXTBLOCK rather than on the balloon — the service reads it
            // from the owner, so set on the ToolTip it would silently do nothing and the reveal
            // would go back to WPF's stock second of waiting.
            Assert.Equal(0, ToolTipService.GetInitialShowDelay(text));

            // Placed back by its own border and padding, less whatever inset the original gives
            // its text: the two first glyphs land on the same pixel. THAT is what makes it read
            // as the same sentence continuing.
            Assert.Equal(-(RevealText.PadX + 1 - text.Padding.Left), tip.HorizontalOffset, 3);
            Assert.Equal(-(RevealText.PadY + 1 - text.Padding.Top), tip.VerticalOffset, 3);
            Assert.Equal(text, tip.PlacementTarget);

            // And the same block with room to spare reveals nothing at all — and takes its own
            // tooltip back off, so it stops shadowing whatever an ancestor might have to say.
            card.Measure(new Size(600, 40));
            card.Arrange(new Rect(0, 0, 600, 40));
            text.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Null(text.ToolTip);

            // The delay comes off with it: it was ours to impose only while our own tooltip was
            // the one on this element.
            Assert.NotEqual(0, ToolTipService.GetInitialShowDelay(text));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// HOVERING AN UNCHANGED BLOCK MUST NOT REBUILD ITS TOOLTIP, and this is the other half of
    /// why the reveal took seconds to appear.
    ///
    /// <para>By the time the MouseEnter handler runs, WPF's tooltip service has already
    /// inspected the element from a class handler and scheduled the show. Clearing the ToolTip
    /// property cancels that, and re-assigning it does not reschedule — the timer only starts on
    /// entry — so the reveal needed a SECOND inspection to appear, one full delay later. The
    /// handler was rebuilding on every hover, unconditionally.</para>
    ///
    /// <para><b>The assertion has to be by REFERENCE.</b> A rebuilt tooltip holds the same words
    /// in the same font at the same offset: compared by value it passes, looks right in a
    /// screenshot, and is exactly the bug.</para>
    /// </summary>
    [Fact]
    public void HoveringAnUnchangedBlockLeavesItsRevealAlone()
    {
        var error = RunOnStaThread(() =>
        {
            var text = new TextBlock
            {
                Text = "Mapa mas jugado: ESOC Fertile Crescent",
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                FontSize = 12,
            };
            var card = new Border
            {
                Background = (System.Windows.Media.Brush)Application.Current.FindResource("MpPanel"),
                Child = text,
            };
            card.Measure(new Size(90, 40));
            card.Arrange(new Rect(0, 0, 90, 40));
            text.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            var armed = Assert.IsType<ToolTip>(text.ToolTip);

            // Two hovers, nothing changed in between.
            text.RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
            text.RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });

            Assert.Same(armed, text.ToolTip);

            // But a block whose TEXT changed without its width changing — a room's age ticking
            // in a fixed column — is still refreshed. That case is the whole reason the handler
            // exists, and a "never touch it" fix would have silently dropped it.
            text.Text = "Mapa mas jugado: ESOC Yucatan y algo mucho mas largo todavia";
            text.RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });

            var refreshed = Assert.IsType<ToolTip>(text.ToolTip);
            Assert.NotSame(armed, refreshed);
            Assert.Equal(text.Text, RevealText.PlainTextOf(Assert.IsType<TextBlock>(refreshed.Content)));
        });

        Assert.Null(error);
    }

    /// <summary>
    /// A BLOCK WHOSE LETTER CHANGED IS REFRESHED, and this is the defect that reached a
    /// screenshot.
    ///
    /// <para>The balloon is a clone drawn on top of the original, so the two have to be the
    /// same letter. A style trigger restyles the anchor long after the clone was built — a
    /// segmented button going active turns Medium into SemiBold and the foreground white — and
    /// neither automatic refresh sees it: <c>SizeChanged</c> does not fire for a block clamped
    /// at a MaxWidth, and the signature used to be width and text only. The result was a
    /// reveal in the wrong weight and colour sitting over the live text, with the glyphs
    /// drifting apart after the first word.</para>
    ///
    /// <para>This is the pair of <see cref="HoveringAnUnchangedBlockLeavesItsRevealAlone"/> and
    /// they have to both hold: refresh when the letter changed, and NEVER when nothing did.</para>
    /// </summary>
    [Fact]
    public void AWeightChangeRebuildsTheRevealSoItMatchesWhatIsUnderIt()
    {
        var error = RunOnStaThread(() =>
        {
            var text = new TextBlock
            {
                Text = "Age of Empires III: The Asian Dynasties",
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("MpTextBody"),
            };
            var card = new Border
            {
                Background = (System.Windows.Media.Brush)Application.Current.FindResource("MpPanel"),
                Child = text,
            };
            card.Measure(new Size(90, 40));
            card.Arrange(new Rect(0, 0, 90, 40));
            text.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            var armed = Assert.IsType<ToolTip>(text.ToolTip);
            Assert.Equal(FontWeights.Medium, Assert.IsType<TextBlock>(armed.Content).FontWeight);

            // What Tag="active" does to a chip: heavier and white, same words, same width — so
            // nothing else in the refresh path can notice.
            text.FontWeight = FontWeights.SemiBold;
            text.Foreground = System.Windows.Media.Brushes.White;
            text.RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });

            var refreshed = Assert.IsType<ToolTip>(text.ToolTip);
            Assert.NotSame(armed, refreshed);

            var clone = Assert.IsType<TextBlock>(refreshed.Content);
            Assert.Equal(FontWeights.SemiBold, clone.FontWeight);
            Assert.Equal(System.Windows.Media.Brushes.White, clone.Foreground);
        });

        Assert.Null(error);
    }

    /// <summary>
    /// THE MOD CHIP DOES NOT TRIM, and that is load-bearing rather than cosmetic.
    ///
    /// <para>Trimming is what arms <see cref="RevealText"/> — the implicit TextBlock style
    /// turns the hover reveal on for any block with an ellipsis, with no opt-in. On this chip
    /// the reveal painted its own bordered box over the blue fill and spilled the full name
    /// across the NEXT chip. A MaxWidth quietly reintroduced here brings all of that back and
    /// nothing else in the suite would notice, which is why the absence is asserted.</para>
    ///
    /// <para>It is safe because the catalogue schema caps <c>displayName</c> at 50 characters
    /// and the row is a WrapPanel.</para>
    /// </summary>
    [Fact]
    public void TheModChipShowsTheWholeNameAndThereforeArmsNoReveal()
    {
        var error = RunOnStaThread(() =>
        {
            var tab = new MultiplayerTab();
            var chip = Assert.IsType<StackPanel>(
                tab.BuildModChipContent(Services.ModRegistry.Default));
            var name = chip.Children.OfType<TextBlock>().Single();

            Assert.Equal(TextTrimming.None, name.TextTrimming);
            Assert.True(double.IsPositiveInfinity(name.MaxWidth), "the chip name is capped again");
            Assert.False(RevealText.GetEnabled(name), "the reveal is armed on the mod chip again");
        });

        Assert.Null(error);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on an STA thread with the launcher's resource
    /// dictionaries loaded, and returns the exception it threw (null when it didn't).
    /// </summary>
    /// <summary>
    /// Every shared settings control in <c>Styles/Controls.xaml</c> applies, and the
    /// brushes it names actually resolve.
    ///
    /// <para>These styles are the handoff's "make it ONCE" set — the switch, the card,
    /// the row, the badge, the notice box and the fixed-width action button — and they
    /// are consumed from three different windows. Nothing checks them at compile time:
    /// a style with an unresolvable <c>{StaticResource}</c> throws only when it is
    /// APPLIED, and one with a dead <c>{DynamicResource}</c> throws nothing at all and
    /// simply paints the WPF default, which on a dark surface is invisible rather than
    /// obviously wrong. The smoke launch cannot see any of it either: it opens
    /// MainWindow, and none of these styles is used there.</para>
    /// </summary>
    [Fact]
    public void EverySharedSettingsControlAppliesAndItsBrushesResolve()
    {
        var ex = RunOnStaThread(() =>
        {
            EnsureResources();
            var res = Application.Current!.Resources;

            // -- text roles ------------------------------------------------
            foreach (var key in new[]
                     {
                         "SetSectionTitle", "SetGroupLabel", "SetRowTitle",
                         "SetRowDesc", "SetMonoValue", "SetActionQuiet",
                     })
            {
                var style = res[key] as Style;
                Assert.True(style != null, $"{key} is missing from Styles/Controls.xaml");
                var tb = new TextBlock { Style = style, Text = "x" };
                MeasureSharedControl(tb);
                Assert.True(tb.Foreground != null, $"{key} resolved no Foreground");
                Assert.True(tb.FontSize > 0, $"{key} resolved no FontSize");
            }

            // -- card + rows -----------------------------------------------
            foreach (var key in new[]
                     {
                         "SetCard", "SetCardDim", "SetRow", "SetRowLast",
                         "SetActionRow", "SetActionRowLast",
                         "SetBadge", "SetNoticeAmber", "SetNoticeDanger",
                     })
            {
                var style = res[key] as Style;
                Assert.True(style != null, $"{key} is missing from Styles/Controls.xaml");
                var border = new Border { Style = style, Child = new TextBlock { Text = "x" } };
                MeasureSharedControl(border);
            }

            // The seam between rows is the ONLY thing separating them — the handoff
            // forbids separating them by margin — so a SetRow that resolved no
            // BorderBrush would silently merge every row in every card into one block.
            var row = new Border { Style = (Style)res["SetRow"] };
            MeasureSharedControl(row);
            Assert.True(row.BorderBrush != null, "SetRow resolved no seam brush");
            Assert.True(row.BorderThickness.Bottom > 0, "SetRow has no bottom seam");
            var last = new Border { Style = (Style)res["SetRowLast"] };
            MeasureSharedControl(last);
            Assert.Equal(0d, last.BorderThickness.Bottom);

            // -- badge variants --------------------------------------------
            // Tag drives the colour. A typo'd Tag falls through to neutral rather than
            // throwing, so assert that each variant actually CHANGES the fill.
            var neutral = BadgeFill(res, null);
            foreach (var tag in new[] { "ok", "warn", "info", "danger", "private" })
            {
                var fill = BadgeFill(res, tag);
                Assert.True(fill != null, $"SetBadge[{tag}] resolved no Background");
                Assert.True(fill!.ToString() != neutral!.ToString(),
                    $"SetBadge[{tag}] painted the neutral fill — the trigger did not fire");
            }

            // -- toggle switch ---------------------------------------------
            // The one control the whole redesign hangs on: it replaces every checkbox
            // in both settings windows. Off must read as "no colour" and on as blue,
            // or a column of eleven of them cannot be scanned at a glance, which is the
            // entire reason they stopped being checkboxes.
            var toggleStyle = res["SetToggle"] as Style;
            Assert.True(toggleStyle != null, "SetToggle is missing from Styles/Controls.xaml");

            var off = new System.Windows.Controls.Primitives.ToggleButton
            {
                Style = toggleStyle,
                IsChecked = false,
            };
            MeasureSharedControl(off);
            off.ApplyTemplate();
            Assert.Equal(34d, off.Width);
            Assert.Equal(20d, off.Height);
            var offFill = off.Background;
            Assert.True(offFill != null, "SetToggle resolved no off-track brush");

            var on = new System.Windows.Controls.Primitives.ToggleButton
            {
                Style = toggleStyle,
                IsChecked = true,
            };
            MeasureSharedControl(on);
            on.ApplyTemplate();
            Assert.True(on.Background != null, "SetToggle resolved no on-track brush");
            Assert.True(on.Background!.ToString() != offFill!.ToString(),
                "SetToggle looks identical on and off — the IsChecked trigger did not fire");

            // -- fixed-width action buttons --------------------------------
            // The width must NOT follow the label: a column whose width tracks its text
            // zigzags down the section, which is the defect the fixed widths exist to
            // stop. So the same style with a long and a short caption must measure the
            // same, and the three sizes must actually differ from each other.
            var widths = new Dictionary<string, double>();
            foreach (var (key, expected) in new[]
                     {
                         ("SetActionButtonSm", 88d),
                         ("SetActionButton", 112d),
                         ("SetActionButtonLg", 132d),
                         ("SetActionButtonPrimary", 112d),
                     })
            {
                var style = res[key] as Style;
                Assert.True(style != null, $"{key} is missing from Styles/Controls.xaml");

                var shortBtn = new Button { Style = style, Content = "Ok" };
                var longBtn = new Button { Style = style, Content = "Reparar la instalación completa" };
                MeasureSharedControl(shortBtn);
                MeasureSharedControl(longBtn);
                shortBtn.ApplyTemplate();

                Assert.Equal(expected, shortBtn.Width);
                Assert.Equal(shortBtn.DesiredSize.Width, longBtn.DesiredSize.Width);
                Assert.True(shortBtn.Foreground != null, $"{key} resolved no Foreground");
                widths[key] = expected;
            }
            Assert.Equal(3, widths.Values.Distinct().Count());
        });
        Assert.Null(ex);
    }

    /// <summary>
    /// The launcher settings window parses, its five sections resolve, and the footer
    /// counts what is pending.
    ///
    /// <para>The smoke launch opens MainWindow and nothing else, so this window's XAML —
    /// which is where the whole shared-control set is first consumed — would ship with a
    /// broken <c>{StaticResource}</c> unseen. Constructing it here is the only automated
    /// cover it has.</para>
    ///
    /// <para>The section mapping is the part worth pinning: the redesign merged seven
    /// rail entries into five, so ADVANCED shows three panels and MODS AND UPDATES shows
    /// two. Nothing about that is visible in a build — a section that forgot a panel
    /// simply renders a shorter page.</para>
    /// </summary>
    [Fact]
    public void TheSettingsWindowLoadsAndItsFiveSectionsMapToTheRightPanels()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var dlg = new LauncherSettingsDialog(new LauncherConfig());

                // Opens on GENERAL, and the title above the content is the same string as
                // the rail entry — they are one name, not a name and a header.
                Assert.Equal(Visibility.Visible, dlg.GeneralPanel.Visibility);
                Assert.Equal("active", dlg.TabGeneralBtn.Tag);
                Assert.Equal(Strings.Get("DlgLauncherSettingsSectionGeneral"), dlg.SectionTitleText.Text);

                // The two recording settings moved OUT of General into GAMES — they are
                // what decides whether a match can be rated, and they were the least
                // findable things on the page.
                Assert.Equal(Visibility.Collapsed, dlg.GamesPanel.Visibility);
                dlg.TabGamesBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(Visibility.Visible, dlg.GamesPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, dlg.GeneralPanel.Visibility);

                // MODS AND UPDATES = the old Updates + Catalog, both at once.
                dlg.TabModsBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(Visibility.Visible, dlg.UpdatesPanel.Visibility);
                Assert.Equal(Visibility.Visible, dlg.CatalogPanel.Visibility);

                // ADVANCED = the old Maintenance + Privacy + Developer.
                dlg.DeveloperModeCheck.IsChecked = false;
                dlg.TabAdvancedBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(Visibility.Visible, dlg.MaintenancePanel.Visibility);
                // Privacy and Developer live inside one wrapper so they sit side by side
                // instead of taking a column each. Asserting the wrapper is the whole
                // point: PrivacyPanel is permanently Visible, so checking IT would pass
                // even if ADVANCED stopped showing them at all.
                Assert.Equal(Visibility.Visible, dlg.AdvancedExtrasPanel.Visibility);
                Assert.Equal(Visibility.Visible, dlg.PrivacyPanel.Visibility);

                // THE DEVELOPER BLOCK IS GONE, not folded. It used to stay on screen with
                // its tools shut and a line reading "turn on developer mode", which told
                // every player the tools were there and how to get them — the thing this
                // change exists to stop. Asserting the WRAPPER and not DevTools is the
                // point: a regression that merely re-folded the tools would leave the
                // heading, the description and the invitation on screen and still pass a
                // DevTools check.
                Assert.Equal(Visibility.Collapsed, dlg.TranslationsPanel.Visibility);
                // And nothing on a cold open says how to get in, or even that anything is
                // hidden. The way back is armed only by turning it off in this window.
                Assert.Equal(Visibility.Collapsed, dlg.DevOffHint.Visibility);

                dlg.DeveloperModeCheck.IsChecked = true;
                Assert.Equal(Visibility.Visible, dlg.TranslationsPanel.Visibility);
                Assert.Equal(Visibility.Visible, dlg.DevTools.Visibility);
                Assert.Equal(Visibility.Collapsed, dlg.DevOffHint.Visibility);

                dlg.Close();
            }
            finally
            {
                Strings.SetLanguage(previous);
            }
        });
        Assert.Null(error);
    }

    /// <summary>
    /// Every settings section fills the window, and NOTHING on the way up to the
    /// ScrollViewer takes that width back.
    ///
    /// <para>This asserts an ABSENCE, which is why it is worth a test at all. The page has
    /// been through four shapes — capped-left, capped-centred, split into columns, and now
    /// one full-width column — and each of the first three was a single <c>MaxWidth</c> or
    /// alignment somewhere on this chain. Putting one back throws nothing, builds clean,
    /// and reads in a diff as an ordinary style choice; the only symptom is that the
    /// settings quietly go narrow again on a wide monitor.</para>
    /// </summary>
    [Fact]
    public void TheSettingsSectionsFillTheWindowAndNothingCapsThem()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var dlg = new LauncherSettingsDialog(new LauncherConfig());

            foreach (var panel in new[]
                     {
                         dlg.GeneralPanel, dlg.InterfacePanel, dlg.GamesPanel,
                         dlg.UpdatesPanel, dlg.CatalogPanel, dlg.MaintenancePanel,
                         dlg.AdvancedExtrasPanel,
                     })
            {
                AssertNothingCapsTheWidth(panel);
            }

            dlg.Close();
        });
        Assert.Null(error);
    }

    /// <summary>
    /// The one width limit the settings surface still has, and the one place it belongs.
    ///
    /// <para>The section spans the whole window, so the description — the only thing in a
    /// row that WRAPS, since the title and the group label trim instead — would otherwise
    /// run as a single line right across an ultrawide. Removing this setter would look like
    /// tidying up a redundant cap.</para>
    /// </summary>
    [Fact]
    public void TheRowDescriptionKeepsItsOwnWidthLimit()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();

            var style = (Style)Application.Current.FindResource("SetRowDesc");
            var cap = style.Setters.OfType<Setter>()
                           .FirstOrDefault(s => s.Property == FrameworkElement.MaxWidthProperty);

            Assert.True(cap != null,
                "SetRowDesc lost its MaxWidth. It wraps and the settings page is now "
                + "full-width, so without it one description becomes a single line across "
                + "the monitor.");

            // Above the ~824 px a description had under the old 900-px column, so the cap
            // cannot re-wrap anything that renders correctly today.
            Assert.True((double)cap!.Value >= 824,
                $"SetRowDesc is capped at {cap.Value}, below the width descriptions already "
                + "had — that re-wraps real strings instead of only guarding the extreme");
        });
        Assert.Null(error);
    }

    /// <summary>
    /// Searching in the mod window and then clearing the box leaves the deliberately hidden
    /// elements hidden.
    ///
    /// <para><b>This is the bug a straight copy of the launcher search would have shipped.</b>
    /// That version put every direct child of a panel back to Visible when the query cleared,
    /// and got away with it because the launcher runs <c>ShowSection</c> immediately afterwards
    /// and re-decides them. <c>SetActiveTab</c> re-decides nothing — so a search followed by a
    /// backspace would have revealed the "no backups yet" line beside an actual list of backups,
    /// the collapsed version block, and three more empty-state hints.</para>
    ///
    /// <para>It breaks nothing, throws nothing and builds clean: text simply appears that should
    /// not be there. Hence a test rather than care.</para>
    /// </summary>
    [Fact]
    public void SearchingTheModWindowAndClearingLeavesTheHiddenThingsHidden()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();

            var config = new LauncherConfig();
            var profile = Services.ModRegistry.Default;
            var dlg = new ModPropertiesDialog(
                profile,
                new Services.UpdateService(config, profile),
                config,
                translationIndex: null,
                applyTranslation: _ => { },
                revertToEnglish: () => { },
                openVerify: () => { },
                openRepair: () => { },
                checkForUpdates: () => Task.FromResult<Services.UpdateService.CheckResult?>(null),
                openAoE3Folder: () => { },
                changeModFolder: () => { },
                changeAoE3Folder: () => { },
                openUserDataFolder: () => { },
                createBackup: () => null,
                restoreBackup: () => null,
                viewLogs: () => { },
                shareDiagnostics: () => { },
                uninstall: () => { });

            // Everything the window hides on purpose, by name — the empty-state hints and the
            // two wrapper blocks that only appear once their data says so.
            var hidden = new (string Name, FrameworkElement El)[]
            {
                ("VersionSection", dlg.VersionSection),
                ("GameSettingsSection", dlg.GameSettingsSection),
                ("LanguageEmptyHint", dlg.LanguageEmptyHint),
                ("AddonsEmptyHint", dlg.AddonsEmptyHint),
                ("StatsEmptyHint", dlg.StatsEmptyHint),
                ("DecksEmptyHint", dlg.DecksEmptyHint),
            };

            var before = hidden.Select(h => (h.Name, h.El, Was: h.El.Visibility)).ToList();

            // Guard the guard: if the window ever starts showing all of these by default this
            // test would pass while checking nothing at all.
            Assert.Contains(before, b => b.Was != Visibility.Visible);

            dlg.ModSearchBox.Text = "carpeta";
            dlg.ModSearchBox.Text = "";

            foreach (var (name, el, was) in before)
            {
                Assert.True(el.Visibility == was,
                    $"{name} came back as {el.Visibility} after a search was cleared, but it "
                    + $"was {was} before it — clearing a search must restore, not reveal");
            }

            dlg.Close();
        });
        Assert.Null(error);
    }

    /// <summary>
    /// The mod window's search box exists and is wired. It is one line, but the whole feature is
    /// three named elements the code-behind finds by field: rename one and the window throws on
    /// open, which nothing else in the run would reach.
    /// </summary>
    [Fact]
    public void TheModWindowHasASearchBox()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();

            var config = new LauncherConfig();
            var profile = Services.ModRegistry.Default;
            var dlg = new ModPropertiesDialog(
                profile,
                new Services.UpdateService(config, profile),
                config,
                translationIndex: null,
                applyTranslation: _ => { },
                revertToEnglish: () => { },
                openVerify: () => { },
                openRepair: () => { },
                checkForUpdates: () => Task.FromResult<Services.UpdateService.CheckResult?>(null),
                openAoE3Folder: () => { },
                changeModFolder: () => { },
                changeAoE3Folder: () => { },
                openUserDataFolder: () => { },
                createBackup: () => null,
                restoreBackup: () => null,
                viewLogs: () => { },
                shareDiagnostics: () => { },
                uninstall: () => { });

            Assert.NotNull(dlg.ModSearchBox);
            Assert.NotNull(dlg.ModSearchNoResults);
            Assert.False(string.IsNullOrWhiteSpace(dlg.ModSearchPlaceholder.Text));
            Assert.Equal(Visibility.Collapsed, dlg.ModSearchNoResults.Visibility);

            // A query nothing can match has to SAY so, or the page just empties and a search
            // that found nothing looks exactly like one that broke.
            dlg.ModSearchBox.Text = "qqqzzzxxx";
            Assert.Equal(Visibility.Visible, dlg.ModSearchNoResults.Visibility);

            dlg.ModSearchBox.Text = "";
            Assert.Equal(Visibility.Collapsed, dlg.ModSearchNoResults.Visibility);

            dlg.Close();
        });
        Assert.Null(error);
    }

    /// <summary>
    /// A matchup row builds, and it withholds the percentage on a pairing nobody has played
    /// enough of.
    ///
    /// <para>The row is assembled in code, so nothing at compile time checks the half-dozen
    /// brushes and sizes it looks up by name. And the withholding is the part worth pinning: the
    /// whole table exists for a modder, and a "100%" derived from one game is worse than a blank
    /// — it is a claim, and they would act on it.</para>
    /// </summary>
    [Fact]
    public void MatchupRow_BuildsAndWithholdsAPercentageItCannotSupport()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();

            static IEnumerable<TextBlock> TextIn(DependencyObject root)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                {
                    if (child is TextBlock tb) yield return tb;
                    foreach (var d2 in TextIn(child)) yield return d2;
                }
            }

            // One game. Below CivStatsView.MinDecidedForPercent, so no rate may be printed.
            var thin = MultiplayerTab.BuildMatchupRow(new MatchupEntry
            {
                CivA = "Chinese", CivB = "Ottomans", Played = 1, WinsA = 1, LossesA = 0,
            });
            var thinText = TextIn(thin).Select(t => t.Text ?? "").ToList();

            // The pair is built from parts now - a flag can sit beside each civilization - so
            // the two names are separate runs. What still has to hold is the ORDER: the record
            // beside them belongs to the FIRST, and "B vs A" with A's record is the same
            // numbers meaning the opposite.
            var thinPair = string.Join(" ", thinText);
            Assert.Contains("Chinese", thinPair);
            Assert.Contains("Ottomans", thinPair);
            Assert.True(thinPair.IndexOf("Chinese", System.StringComparison.Ordinal)
                        < thinPair.IndexOf("Ottomans", System.StringComparison.Ordinal),
                $"the pair reads '{thinPair}' - the first civilization must come first.");
            Assert.Contains("1-0", thinText);
            Assert.DoesNotContain(thinText, t => t.Contains("%"));
            // Not an em dash and not a zero either — the cell is simply empty.
            Assert.DoesNotContain(thinText, t => t.Contains("—"));

            // Past the bar, the rate appears.
            var solid = MultiplayerTab.BuildMatchupRow(new MatchupEntry
            {
                CivA = "Chinese", CivB = "Ottomans", Played = 9, WinsA = 6, LossesA = 3,
            });
            var solidText = TextIn(solid).Select(t => t.Text ?? "").ToList();

            Assert.Contains("6-3", solidText);
            Assert.Contains(solidText, t => t.Contains("%"));
        });
        Assert.Null(error);
    }

    /// <summary>
    /// Walks from a section panel out to its ScrollViewer, asserting each step still hands
    /// the full width down. Shared by both settings windows so neither can drift.
    /// </summary>
    private static void AssertNothingCapsTheWidth(FrameworkElement panel)
    {
        FrameworkElement? el = panel;
        var hops = 0;

        while (el is not null and not ScrollViewer)
        {
            var who = string.IsNullOrEmpty(el.Name) ? el.GetType().Name : el.Name;

            // Unset MaxWidth is positive INFINITY, not NaN — Width is the one that
            // defaults to NaN, and mixing them up makes this assertion fire on a
            // perfectly good page.
            Assert.True(double.IsPositiveInfinity(el.MaxWidth),
                $"{who} sets MaxWidth={el.MaxWidth}, which narrows the settings back to a "
                + "column on a wide monitor");

            Assert.True(el.HorizontalAlignment == HorizontalAlignment.Stretch,
                $"{who} is aligned {el.HorizontalAlignment}, so the section stops filling "
                + "the window");

            el = LogicalTreeHelper.GetParent(el) as FrameworkElement;

            Assert.True(++hops < 20,
                $"{panel.Name} never reached a ScrollViewer — the content chain changed "
                + "shape and this test is no longer checking what it claims to");
        }

        Assert.True(el is ScrollViewer,
            $"{panel.Name} is not inside a ScrollViewer at all");
    }

    /// <summary>
    /// The mod window is the biggest XAML in the launcher and NOTHING constructed it: its
    /// six sections, their cards and every <c>{StaticResource}</c> in them were parsed for
    /// the first time when a user opened the gear. This builds it once and pins the two
    /// things that fail silently there.
    ///
    /// <para><b>The section heading.</b> <c>ModSectionTitle</c> was declared and never
    /// assigned, so GENERAL, LOCAL FILES and USER DATA showed no name at all and every
    /// section carried an empty line where the heading belonged. Nothing failed: the
    /// element existed, laid out, and painted nothing.</para>
    ///
    /// <para><b>The columns.</b> Same rule as the settings window, with ONE exemption —
    /// LANGUAGE is a single block because it is the last panel still on the old styles,
    /// with no cards and no group labels to split on. Splitting it as it stands would be
    /// worse than not splitting: a column holding the header and the refresh button beside
    /// one holding the entire pack list.</para>
    /// </summary>
    [Fact]
    public void TheModWindowLoadsAndItsSectionsAreColumnsWithARealHeading()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();

            var config = new LauncherConfig();
            var profile = Services.ModRegistry.Default;
            var dlg = new ModPropertiesDialog(
                profile,
                new Services.UpdateService(config, profile),
                config,
                translationIndex: null,
                applyTranslation: _ => { },
                revertToEnglish: () => { },
                openVerify: () => { },
                openRepair: () => { },
                checkForUpdates: () => Task.FromResult<Services.UpdateService.CheckResult?>(null),
                openAoE3Folder: () => { },
                changeModFolder: () => { },
                changeAoE3Folder: () => { },
                openUserDataFolder: () => { },
                createBackup: () => null,
                restoreBackup: () => null,
                viewLogs: () => { },
                shareDiagnostics: () => { },
                uninstall: () => { });

            var panels = new[]
            {
                dlg.GeneralPanel, dlg.LocalFilesPanel, dlg.UserDataPanel,
                dlg.LanguagePanel, dlg.AddonsPanel, dlg.DecksPanel, dlg.StatsPanel,
            };

            foreach (var panel in panels)
            {
                AssertNothingCapsTheWidth(panel);
            }

            // The rail entry exists and is localized, which is the half of the DECKS wiring a
            // click would otherwise be needed to reach.
            Assert.False(string.IsNullOrWhiteSpace(dlg.TabDecksLabel.Text));

            // Every rail entry names its section, and names it with the SAME words the item
            // you clicked carries — the heading is read off that label rather than from a
            // second table of keys, so the two cannot drift apart.
            var tabs = new[]
            {
                (dlg.TabGeneralBtn, dlg.TabGeneralLabel),
                (dlg.TabLocalFilesBtn, dlg.TabLocalFilesLabel),
                (dlg.TabUserDataBtn, dlg.TabUserDataLabel),
                (dlg.TabLanguageBtn, dlg.TabLanguageLabel),
                (dlg.TabAddonsBtn, dlg.TabAddonsLabel),
                // DECKS is deliberately absent: its click handler starts the real disk work —
                // home city files, 12 MB of tech files, five archive indexes — and this STA
                // thread pumps no messages, so the await would resume on the pool and touch
                // these controls from the wrong thread. Its label and panel are checked above.
                (dlg.TabStatsBtn, dlg.TabStatsLabel),
            };

            foreach (var (button, label) in tabs)
            {
                button.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.False(string.IsNullOrWhiteSpace(dlg.ModSectionTitle.Text),
                    $"{button.Name} left the section heading empty");
                Assert.Equal(label.Text, dlg.ModSectionTitle.Text);
            }

            dlg.Close();
        });
        Assert.Null(error);
    }

    /// <summary>
    /// An untouched settings window offers nothing to save, and one changed setting
    /// counts as one.
    ///
    /// <para>This is what lets the footer stop being a permanent Cancel/Save pair. It
    /// also pins the property that makes the count honest rather than merely
    /// impressive: a switch flipped twice is back where it started, so it counts as
    /// nothing — the footer compares against what the dialog OPENED with, not against
    /// how many times something moved.</para>
    ///
    /// <para>The handlers are attached to the content root as class handlers, so this
    /// also covers the wiring: a setting that stopped being counted would look exactly
    /// like a setting nobody changed.</para>
    /// </summary>
    [Fact]
    public void TheSettingsFooterAppliesInstantlyAndOnlyCountsWhatCanStillBeRefused()
    {
        var error = RunOnStaThread(() =>
        {
            EnsureResources();
            var config = new LauncherConfig();
            var dlg = new LauncherSettingsDialog(config);

            // Untouched: the line states the contract, without the amber dot, and there
            // is nothing to save.
            Assert.Equal(Visibility.Visible, dlg.UnsavedIndicator.Visibility);
            Assert.Equal(Visibility.Collapsed, dlg.UnsavedIndicatorDot.Visibility);
            Assert.Equal(Visibility.Collapsed, dlg.SaveButton.Visibility);
            Assert.Equal(Strings.Get("DlgSettingsAppliesInstantly"), dlg.UnsavedText.Text);

            // An INSTANT setting reaches the config the moment it is touched, and is
            // never pending — which is the whole claim the footer line makes.
            bool wasOn = dlg.SoundsCheck.IsChecked == true;
            dlg.SoundsCheck.IsChecked = !wasOn;
            Assert.Equal(!wasOn, config.EnableSounds);
            Assert.Equal(Visibility.Collapsed, dlg.UnsavedIndicatorDot.Visibility);
            Assert.Equal(Visibility.Collapsed, dlg.SaveButton.Visibility);
            Assert.Equal(Strings.Get("DlgSettingsAppliesInstantly"), dlg.UnsavedText.Text);

            // A DEFERRED setting is the opposite: it can be refused, so it waits for Save
            // and the footer counts it meanwhile.
            dlg.CatalogCustomRadio.IsChecked = true;
            Assert.Equal(Visibility.Visible, dlg.UnsavedIndicatorDot.Visibility);
            Assert.Equal(Visibility.Visible, dlg.SaveButton.Visibility);
            Assert.Equal(Strings.Get("DlgSettingsUnsavedOne"), dlg.UnsavedText.Text);
            // ...and it has NOT been written.
            Assert.Equal("", config.ModsCatalogRepo);

            // Two of them, counted as two.
            bool bgWas = dlg.StartWithWindowsCheck.IsChecked == true;
            dlg.StartWithWindowsCheck.IsChecked = !bgWas;
            Assert.Equal(Strings.Format("DlgSettingsUnsavedMany", 2), dlg.UnsavedText.Text);

            // Put both back and the footer forgets them.
            dlg.CatalogDefaultRadio.IsChecked = true;
            dlg.StartWithWindowsCheck.IsChecked = bgWas;
            Assert.Equal(Visibility.Collapsed, dlg.UnsavedIndicatorDot.Visibility);
            Assert.Equal(Visibility.Collapsed, dlg.SaveButton.Visibility);
            Assert.Equal(Strings.Get("DlgSettingsAppliesInstantly"), dlg.UnsavedText.Text);

            dlg.Close();
        });
        Assert.Null(error);
    }

    /// <summary>
    /// The redirect in <c>TestDataDirectory</c> is what stops the assertion above from
    /// overwriting the developer's real config: the dialog PERSISTS on every change now,
    /// so a test that flips a control writes a file. Pinned here because the failure mode
    /// is silent and expensive — it destroys installed-mod state on the machine running
    /// the suite, and nothing else in the run would report it.
    /// </summary>
    [Fact]
    public void TheTestRunNeverWritesToTheRealLauncherDataDirectory()
    {
        var real = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AoE3ModLauncher");
        Assert.NotEqual(
            System.IO.Path.GetFullPath(real).TrimEnd(System.IO.Path.DirectorySeparatorChar),
            System.IO.Path.GetFullPath(WarsOfLibertyLauncher.Services.AppPaths.DataDir)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar));
    }

    private static System.Windows.Media.Brush? BadgeFill(ResourceDictionary res, string? tag)
    {
        var border = new Border { Style = (Style)res["SetBadge"], Tag = tag };
        MeasureSharedControl(border);
        return border.Background;
    }

    private static void MeasureSharedControl(FrameworkElement el)
    {
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        el.Arrange(new Rect(el.DesiredSize));
        el.UpdateLayout();
    }

    /// <summary>Shared with the other suites that build these same windows: the STA thread
    /// AND the resource bootstrap are one step, because a dialog parsed without the merged
    /// dictionaries throws on its first StaticResource.</summary>
    internal static Exception? RunOnStaThread(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureResources();
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Generous: the first WPF touch in a process pays for the framework's own
        // initialisation. A hang is a failure too, so it is bounded.
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the STA thread did not finish");
        return captured;
    }

    private static void EnsureResources()
    {
        // Shared holder, not a local guard: Application.Current goes null when the STA
        // thread that created it exits while WPF's one-per-AppDomain guard does not
        // reset, so the second class to run would throw. See TestApplication.
        var app = TestApplication.Ensure();
        if (app.Resources.MergedDictionaries.Count > 0) return;
        // Text BEFORE Buttons, as App.xaml merges them: SidebarNavLabel is BasedOn the
        // implicit TextBlock style that lives in Text.xaml, and a StaticResource in a
        // merged dictionary can only see dictionaries merged before it.
        foreach (var name in new[] { "Tokens", "Colors", "Text", "Chrome", "Buttons", "Inputs", "Controls" })
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/Aoe3ModLauncher;component/Styles/{name}.xaml",
                    UriKind.Absolute),
            });
        }
        // App.xaml's inline resources (the FontSize scale and the font families) are not
        // in any dictionary file, so they are recreated here rather than parsed.
        app.Resources["FontSizeCaption"] = 13.0;
        app.Resources["FontSizeBody"] = 14.0;
        app.Resources["FontSizeBodyStrong"] = 15.0;
        app.Resources["FontSizeSubtitle"] = 16.0;
        app.Resources["FontSizeTitle"] = 18.0;
        app.Resources["FontSizeHeading"] = 24.0;
        app.Resources["FontSizeDisplay"] = 34.0;
        app.Resources["DisplayFont"] = new System.Windows.Media.FontFamily("Cambria, Georgia");
        app.Resources["BodyFont"] = new System.Windows.Media.FontFamily("Segoe UI, Tahoma");
        app.Resources["MonoFont"] = new System.Windows.Media.FontFamily("Consolas, Courier New");
    }
}
