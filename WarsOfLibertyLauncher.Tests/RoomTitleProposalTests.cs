using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using WarsOfLibertyLauncher;
using WarsOfLibertyLauncher.Localization;
using WarsOfLibertyLauncher.Models;
using WarsOfLibertyLauncher.Services;
using WarsOfLibertyLauncher.Services.Multiplayer;
using Xunit;

namespace WarsOfLibertyLauncher.Tests;

/// <summary>
/// A competitive room announces itself in its own title, in words everyone can read.
///
/// <para>The proposed title was <c>"Sala de {mod}"</c> whatever the room was, so a host who
/// ticked "Sala competitiva" got a name that said nothing about the one fact worth knowing
/// before joining — that the match is rated. The badge on the browser row does say it, but it
/// is a small chip on the second line, and it is the SERVER's claim; the title is the host's.
/// Both, deliberately.</para>
///
/// <para>The second half is why the wording is a constant now. A room title is a NETWORK
/// VALUE: it is persisted on the lobby server and rendered verbatim to every viewer, so the
/// badge beside it can be localized per viewer and the title never can. Composing it from the
/// string table meant a Spanish host published <c>Sala de WoL · COMPETITIVA 2v2</c> and an
/// English player read exactly that.</para>
/// </summary>
[Collection("wpf-and-language")]
public class RoomTitleProposalTests
{
    private const int Cap = 64;   // RoomTitleBox.MaxLength

    /// <summary>
    /// THE ONE THAT MATTERS. The marker carries the format the host actually chose, and says
    /// the same thing to everyone.
    ///
    /// <para>A Theory over all three, because pinning only 1v1 would let through exactly the
    /// thing that was asked for by name: a 2v2 or a 3v3 room whose title claims a 1v1, or
    /// says nothing at all. The format label is asked of <see cref="RoomFormats.LabelKey"/>,
    /// the same source the browser row's chip uses, so a room and its own row can never
    /// disagree about what to call it.</para>
    /// </summary>
    [Theory]
    [InlineData(RoomFormat.OneVOne, "1v1")]
    [InlineData(RoomFormat.TwoVTwo, "2v2")]
    [InlineData(RoomFormat.ThreeVThree, "3v3")]
    public void THE_ONE_THAT_MATTERS_ACompetitiveRoomSaysSoInItsOwnTitle(
        RoomFormat format, string label)
    {
        foreach (var lang in new[] { "es", "en" })
        {
            WithLanguage(lang, () =>
            {
                var title = RoomTitleProposal.Propose("WoL", competitive: true, format, Cap);
                Assert.Equal("WoL · Ranked " + label, title);
            });
        }
    }

    /// <summary>
    /// THE OTHER ONE THAT MATTERS. The title does not change with the host's language.
    ///
    /// <para>This is the whole defect: the value goes onto the lobby server and comes back
    /// out in front of players who did not write it. Asserting that the two languages produce
    /// the same string is the only way this stays true, because nothing on the host's own
    /// screen would ever look wrong.</para>
    /// </summary>
    [Theory]
    [InlineData(true, RoomFormat.OneVOne)]
    [InlineData(true, RoomFormat.TwoVTwo)]
    [InlineData(true, RoomFormat.ThreeVThree)]
    [InlineData(true, RoomFormat.Unknown)]
    [InlineData(false, RoomFormat.Casual)]
    public void TheTitleIsTheSameInEveryLanguage(bool competitive, RoomFormat format)
    {
        string? spanish = null;
        string? english = null;

        WithLanguage("es", () => spanish = RoomTitleProposal.Propose("WoL", competitive, format, Cap));
        WithLanguage("en", () => english = RoomTitleProposal.Propose("WoL", competitive, format, Cap));

        Assert.Equal(spanish, english);
        // And it is not accidentally equal because both came back empty.
        Assert.False(string.IsNullOrWhiteSpace(spanish));
    }

    /// <summary>
    /// A competitive room whose size names no format says the marker and stops there.
    ///
    /// <para><see cref="RoomFormat.Unknown"/> is a real state, not a defensive branch — a
    /// competitive room made before formats existed, or by a client that skipped the dialog.
    /// Naming a format there would be inventing one.</para>
    /// </summary>
    [Fact]
    public void AnUndeclaredFormatIsNotInvented()
        => WithLanguage("es", () =>
            Assert.Equal("WoL · Ranked",
                RoomTitleProposal.Propose("WoL", competitive: true, RoomFormat.Unknown, Cap)));

    /// <summary>Unticking takes it back off, and says what the room is instead of going
    /// quiet.</summary>
    [Fact]
    public void UntickingTakesItBackOff()
        => WithLanguage("es", () =>
            Assert.Equal("WoL · Casual",
                RoomTitleProposal.Propose("WoL", competitive: false, RoomFormat.Casual, Cap)));

    /// <summary>A blank mod name does not open the title with a dangling separator.</summary>
    [Fact]
    public void ABlankModNameLeavesTheMarkerAlone()
        => WithLanguage("es", () =>
            Assert.Equal("Ranked 1v1",
                RoomTitleProposal.Propose("", competitive: true, RoomFormat.OneVOne, Cap)));

    /// <summary>
    /// THE SILENT ONE. Every title this class can write, it can also recognise.
    ///
    /// <para>The dialog only replaces a title it believes is its own. So the failure mode is
    /// not a wrong title — it is a title that stops moving: propose the competitive variant
    /// once, fail to recognise it, and from then on every change looks like a hand-typed name
    /// and is left alone. Nothing on screen says anything is wrong. Hence the round trip, over
    /// the generated list rather than a restated one, so a new variant is covered the day it
    /// is added instead of the day somebody remembers this test.</para>
    /// </summary>
    [Fact]
    public void EveryTitleWeWriteIsOneWeRecogniseAgain()
    {
        WithLanguage("es", () =>
        {
            var mods = new[] { "WoL", "Struggle of Indonesia" };
            var all = RoomTitleProposal.AllProposals(mods, Cap).ToList();

            // Casual plus four competitive states, per mod. Stated so that deleting a variant
            // fails here rather than quietly shrinking what the round trip covers.
            Assert.Equal(10, all.Count);

            foreach (var title in all)
                Assert.True(RoomTitleProposal.IsOurs(title, mods, Cap),
                    $"the dialog wrote \"{title}\" and would then mistake it for a title the "
                    + "host typed, so it would never update that box again.");
        });
    }

    /// <summary>
    /// THE UPGRADE ONE. A title an EARLIER build wrote is still recognised, in either
    /// language.
    ///
    /// <para>The same silent failure as above, reached a different way: a host whose box still
    /// holds <c>Sala de WoL · COMPETITIVA 1v1</c> from the previous version would find the
    /// field frozen forever with nothing to tell them why. Checked while the launcher runs in
    /// BOTH languages, because switching language does not rewrite a title already written.</para>
    /// </summary>
    [Theory]
    [InlineData("Sala de WoL")]
    [InlineData("Sala de WoL · COMPETITIVA")]
    [InlineData("Sala de WoL · COMPETITIVA 1v1")]
    [InlineData("Sala de WoL · COMPETITIVA 2v2")]
    [InlineData("Sala de WoL · COMPETITIVA 3v3")]
    [InlineData("WoL room")]
    [InlineData("WoL room · COMPETITIVE")]
    [InlineData("WoL room · COMPETITIVE 1v1")]
    [InlineData("WoL room · COMPETITIVE 2v2")]
    [InlineData("WoL room · COMPETITIVE 3v3")]
    public void TitlesFromTheOldWordingAreStillOurs(string legacy)
    {
        foreach (var running in new[] { "es", "en" })
            WithLanguage(running, () =>
                Assert.True(RoomTitleProposal.IsOurs(legacy, new[] { "WoL" }, Cap),
                    $"a box holding \"{legacy}\" would never update again."));
    }

    /// <summary>A name somebody typed is theirs, in either state of the tick box.</summary>
    [Fact]
    public void ATypedTitleIsNotOurs()
    {
        WithLanguage("es", () =>
        {
            var mods = new[] { "WoL" };
            Assert.False(RoomTitleProposal.IsOurs("Vengan noobs", mods, Cap));
            // Close, but not ours: the host edited what we proposed.
            Assert.False(RoomTitleProposal.IsOurs("WoL · Ranked 1v1 sin rush", mods, Cap));
            // Nor the old wording once edited.
            Assert.False(RoomTitleProposal.IsOurs("Sala de WoL · COMPETITIVA 1v1 sin rush", mods, Cap));
            // An empty box belongs to nobody, so it is free to fill.
            Assert.True(RoomTitleProposal.IsOurs("", mods, Cap));
        });
    }

    /// <summary>
    /// The cap trims the room's name, never the marker.
    ///
    /// <para>Sixty-four characters is the field's own limit. A title cut off mid-"Ranke…"
    /// announces nothing, so what gives way is the part that still reads when it is
    /// shorter.</para>
    /// </summary>
    [Fact]
    public void TheMarkerSurvivesTheLengthCap()
    {
        WithLanguage("es", () =>
        {
            var huge = new string('M', 200);
            var title = RoomTitleProposal.Propose(huge, competitive: true, RoomFormat.ThreeVThree, Cap);

            Assert.True(title.Length <= Cap, $"{title.Length} characters in a {Cap} field.");
            Assert.EndsWith("· Ranked 3v3", title);
        });
    }

    /// <summary>
    /// END TO END, through the real dialog: ticking a format rewrites the box.
    ///
    /// <para>The core above can be perfectly right and the dialog never call it. 2v2 rather
    /// than 1v1 on purpose — it is the format the dialog does NOT fall back to, so a handler
    /// that fires but reads the wrong state fails here too.</para>
    /// </summary>
    [Fact]
    public void TheDialogPutsItInTheBoxWhenAFormatIsPicked()
    {
        var error = DialogXamlTests.RunOnStaThread(() =>
        {
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var dlg = NewDialog(out _);

                Assert.Equal("WoL · Casual", dlg.RoomTitleBox.Text);

                var twoVTwo = dlg.FormatRow.Children.OfType<Button>().ElementAt(1);
                twoVTwo.RaiseEvent(new System.Windows.RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                Assert.Equal("WoL · Ranked 2v2", dlg.RoomTitleBox.Text);

                // And back off again, which is the half a one-way handler would fail.
                dlg.CompetitiveCheck.IsChecked = false;
                Assert.Equal("WoL · Casual", dlg.RoomTitleBox.Text);
            }
            finally { Strings.SetLanguage(previous); }
        });
        Assert.Null(error);
    }

    /// <summary>
    /// And it leaves a host's own title alone — in both directions, which is the half that
    /// gets forgotten: unticking must not "restore" a name the host never asked for.
    /// </summary>
    [Fact]
    public void TheDialogNeverOverwritesAName()
    {
        var error = DialogXamlTests.RunOnStaThread(() =>
        {
            var previous = Strings.Language;
            try
            {
                Strings.SetLanguage("es");
                var dlg = NewDialog(out _);
                dlg.RoomTitleBox.Text = "Vengan noobs";

                dlg.FormatRow.Children.OfType<Button>().ElementAt(1).RaiseEvent(
                    new System.Windows.RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal("Vengan noobs", dlg.RoomTitleBox.Text);

                dlg.CompetitiveCheck.IsChecked = false;
                Assert.Equal("Vengan noobs", dlg.RoomTitleBox.Text);
            }
            finally { Strings.SetLanguage(previous); }
        });
        Assert.Null(error);
    }

    private static CreateLobbyDialog NewDialog(out ModProfile profile)
    {
        profile = new ModProfile { Id = "wol", DisplayName = "WoL" };
        var session = new MultiplayerSession(new LauncherConfig());
        return new CreateLobbyDialog(
            session,
            new List<ModProfile> { profile },
            profile,
            _ => Task.FromResult("0123456789abcdef"),
            _ => new ModCopyInfo(false, false, Array.Empty<ModCopyChoice>()),
            _ => Task.CompletedTask);
    }

    private static void WithLanguage(string lang, Action body)
    {
        var previous = Strings.Language;
        try { Strings.SetLanguage(lang); body(); }
        finally { Strings.SetLanguage(previous); }
    }
}
