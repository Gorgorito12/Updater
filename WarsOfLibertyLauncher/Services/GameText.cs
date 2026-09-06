using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace WarsOfLibertyLauncher.Services;

/// <summary>
/// Turns a raw string-table entry into something worth putting on screen.
///
/// <para>The game's own tables carry display markup: colour spans, inline icon references, and
/// line breaks written as the two characters <c>\</c> and <c>n</c> rather than as a newline.
/// Printed verbatim they are visible junk — measured on one real deck, 2 of its 23 card
/// descriptions carry a colour span.</para>
///
/// <para><b>Two ways out, and showing the markup is neither.</b> <see cref="Clean"/> removes
/// the tags and keeps the words; <see cref="Parse"/> honours a colour span as an actual
/// colour. Which one a caller wants depends on whether it can paint: a plain
/// <c>TextBlock.Text</c> or a tooltip takes <see cref="Clean"/>, a run-capable one takes
/// <see cref="Fill"/>. What no caller may do is print the raw string — the community-cards
/// table did, and rows read
/// <c>10 Dragoons &lt;color=1.0, 1.0, 0.0&gt;+ 3 Hussars&lt;/color&gt;</c>.</para>
///
/// <para><b>Deliberately conservative.</b> Stripping <c>&lt;[^&gt;]+&gt;</c> in general would eat
/// a legitimate <c>&lt;</c> and everything after it up to the next one, and these strings are
/// written by modders. Only the three forms the game actually uses are removed.</para>
/// </summary>
public static class GameText
{
    /// <summary>
    /// A colour span. The values are floats 0-1 separated by commas — <c>&lt;color=0.74, 0.25,
    /// 0.11&gt;</c> — not hex and not 0-255.
    /// </summary>
    private static readonly Regex ColourTag =
        new(@"</?color\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>An inline icon: <c>&lt;icon="(32)(ui/ingame/resource_population)"&gt;</c>.</summary>
    private static readonly Regex IconTag =
        new(@"<icon\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The three components of an opening colour tag.
    ///
    /// <para>Parsed, never matched literally. The tag can carry ANY triple — the two spans in
    /// one real deck are <c>0.74, 0.25, 0.11</c> and <c>0.19, 0.52, 0.76</c> — so substituting
    /// the one string somebody happened to see in a screenshot would leave every other span
    /// on screen as raw markup.
    /// </para>
    /// </summary>
    private static readonly Regex ColourValues = new(
        @"=\s*(?<r>[0-9]*\.?[0-9]+)\s*,\s*(?<g>[0-9]*\.?[0-9]+)\s*,\s*(?<b>[0-9]*\.?[0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>One stretch of text, and the colour the table asked for it, if any.</summary>
    /// <param name="Text">The words. Never contains markup.</param>
    /// <param name="Colour">Null means "no colour was asked for, or none could be resolved" —
    /// the caller leaves its own foreground alone rather than picking something.</param>
    public readonly record struct GameTextRun(string Text, Color? Colour);

    /// <summary>Markup out, real line breaks in. Null or blank comes back as an empty string.</summary>
    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var text = raw.Replace("\\n", "\n");
        text = ColourTag.Replace(text, "");
        text = IconTag.Replace(text, "");
        return text.Trim();
    }

    /// <summary>
    /// The same string, split into coloured and uncoloured stretches.
    ///
    /// <para>An unresolvable tag — a malformed triple, a value outside 0-1, an unclosed span —
    /// is dropped and its text kept. That is the documented second-best outcome, and it is
    /// still infinitely better than the tag reaching a player.</para>
    /// </summary>
    public static IReadOnlyList<GameTextRun> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<GameTextRun>();

        var text = IconTag.Replace(raw.Replace("\\n", "\n"), "");

        var runs = new List<GameTextRun>();
        Color? current = null;
        int cursor = 0;

        foreach (Match tag in ColourTag.Matches(text))
        {
            if (tag.Index > cursor)
                Append(runs, text[cursor..tag.Index], current);

            // A closing tag ends the span; an opening one starts whatever it can resolve.
            current = tag.Value.StartsWith("</", StringComparison.Ordinal)
                ? null
                : TryColour(tag.Value);

            cursor = tag.Index + tag.Length;
        }

        if (cursor < text.Length) Append(runs, text[cursor..], current);

        Trim(runs);
        return runs;
    }

    /// <summary>
    /// Renders <paramref name="raw"/> into a <see cref="TextBlock"/>, colouring only the
    /// stretches the table asked to colour. Everything else inherits the block's own
    /// foreground, so a row keeps its normal colour and its emphasis both.
    /// </summary>
    public static void Fill(TextBlock target, string? raw)
    {
        if (target == null) return;

        target.Inlines.Clear();
        foreach (var run in Parse(raw))
        {
            var inline = new Run(run.Text);
            if (run.Colour != null) inline.Foreground = new SolidColorBrush(run.Colour.Value);
            target.Inlines.Add(inline);
        }
    }

    private static void Append(List<GameTextRun> runs, string text, Color? colour)
    {
        if (text.Length == 0) return;

        // Two stretches the same colour are one stretch. Keeps a span that was opened and
        // immediately re-opened from becoming two Runs that only look like one.
        if (runs.Count > 0 && runs[^1].Colour.Equals(colour))
        {
            runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
            return;
        }

        runs.Add(new GameTextRun(text, colour));
    }

    /// <summary>Trims the outer edges only — <see cref="Clean"/> trims, so this has to as
    /// well, or the same string would be laid out differently by the two paths.</summary>
    private static void Trim(List<GameTextRun> runs)
    {
        while (runs.Count > 0)
        {
            var first = runs[0] with { Text = runs[0].Text.TrimStart() };
            if (first.Text.Length > 0) { runs[0] = first; break; }
            runs.RemoveAt(0);
        }

        while (runs.Count > 0)
        {
            var last = runs[^1] with { Text = runs[^1].Text.TrimEnd() };
            if (last.Text.Length > 0) { runs[^1] = last; break; }
            runs.RemoveAt(runs.Count - 1);
        }
    }

    /// <summary>The tag's colour, or null when it does not resolve.</summary>
    private static Color? TryColour(string tag)
    {
        var m = ColourValues.Match(tag);
        if (!m.Success) return null;

        // InvariantCulture, explicitly. The table writes "1.0"; on a Spanish Windows the
        // thread's own culture reads that as ten, and most of this player base runs one.
        if (!TryComponent(m.Groups["r"].Value, out var r)) return null;
        if (!TryComponent(m.Groups["g"].Value, out var g)) return null;
        if (!TryComponent(m.Groups["b"].Value, out var b)) return null;

        return Legible(r, g, b);
    }

    private static bool TryComponent(string raw, out double value) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && value >= 0 && value <= 1;

    /// <summary>
    /// The modder's colour, brought into a band that reads on the launcher's dark panels.
    ///
    /// <para>The tables are written for the game's own HUD, where a fully saturated primary
    /// sits on artwork. On <c>#12213a</c> the same value is either a shout — pure yellow — or
    /// invisible: <c>0, 0, 1</c> is near-black against a dark blue panel. Both are the modder's
    /// emphasis rendered as damage.</para>
    ///
    /// <para>So the HUE is kept, exactly as written, and only saturation and lightness are
    /// pulled into the range the rest of the launcher's accents occupy. Keeping the hue is the
    /// point: it is the part that carries the modder's meaning, and it is the part a
    /// per-colour lookup table would throw away.</para>
    ///
    /// <para><b>One known deviation from the design handoff.</b> It maps
    /// <c>1.0, 1.0, 0.0</c> to the launcher's gold <c>#E6C86A</c>; this maps it to
    /// <c>#E6E66A</c>. The difference is hue — that gold is 45°, pure yellow is 60° — and no
    /// clamp can move one to the other without rotating every other colour by the same amount,
    /// which would misreport the reds and blues the real tables actually use. Same lightness,
    /// same saturation, same weight on the page.</para>
    /// </summary>
    private static Color Legible(double r, double g, double b)
    {
        var (h, s, l) = ToHsl(r, g, b);

        // The band is TAKEN FROM THE HANDOFF'S OWN GOLD rather than guessed: #E6C86A is
        // saturation 0.7127 at lightness 0.6588, and those are the ceiling and the floor
        // here. So a fully saturated primary lands at exactly the weight the design asked
        // for, and a near-black one is lifted to it.
        s = Math.Min(s, MaxSaturation);
        l = Math.Clamp(l, MinLightness, MaxLightness);

        // A grey stays grey. Pushing lightness into a hueless colour would invent an accent
        // out of what the modder wrote as plain emphasis.
        if (s <= 0.001) l = Math.Clamp(l, MinLightness, 0.85);

        var (rr, gg, bb) = ToRgb(h, s, l);
        return Color.FromRgb(Round(rr), Round(gg), Round(bb));
    }

    /// <summary>Saturation of the launcher's accent gold <c>#E6C86A</c>.</summary>
    private const double MaxSaturation = 0.712643;

    /// <summary>Lightness of the same. A colour darker than the design's own accent would not
    /// read on the panel it sits on.</summary>
    private const double MinLightness = 0.658824;

    /// <summary>Above this a colour stops reading as an accent and starts reading as white.</summary>
    private const double MaxLightness = 0.78;

    private static byte Round(double channel) =>
        (byte)Math.Clamp(Math.Round(channel * 255.0, MidpointRounding.AwayFromZero), 0, 255);

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;
        double d = max - min;

        if (d <= 0.0) return (0, 0, l);

        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        double h;
        if (max == r) h = ((g - b) / d + (g < b ? 6.0 : 0.0)) * 60.0;
        else if (max == g) h = ((b - r) / d + 2.0) * 60.0;
        else h = ((r - g) / d + 4.0) * 60.0;

        return (h, s, l);
    }

    private static (double R, double G, double B) ToRgb(double h, double s, double l)
    {
        if (s <= 0.0) return (l, l, l);

        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = l - c / 2.0;

        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0);
        else if (h < 120) (r, g, b) = (x, c, 0);
        else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c);
        else if (h < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);

        return (r + m, g + m, b + m);
    }
}
