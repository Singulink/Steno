using System.Globalization;
using System.Net;
using System.Text;

namespace Singulink.Steno.Benchmarks;

/// <summary>
/// A horizontal grouped bar chart as a self-contained SVG file: one group of bars per category, one bar per series. It has no background, draws text and
/// lines in currentColor (dark grey on a light page, light grey on a dark one when it is shown as an image), and colours that read on both.
/// </summary>
internal sealed class SvgChart
{
    private const int Width = 760;
    private const int LabelWidth = 170;
    private const int RightMargin = 56;
    private const double BarHeight = 7;
    private const double GroupGap = 9;

    private static readonly Dictionary<string, string> Colors = new() {
        ["Steno"] = "#f58518",
        ["Steno (.NET)"] = "#f58518",
        ["UTF-8"] = "#4c78a8",
        ["UTF-16"] = "#9ecae9",
        ["SCSU"] = "#54a24b",
        ["BOCU-1"] = "#b279a2",
        ["Deflate fast"] = "#c7c7c7",
        ["Deflate max"] = "#8c8c8c",
        ["Brotli 1"] = "#ff9d98",
        ["Brotli 11"] = "#e45756",
        ["LZ4 fast"] = "#9ad0c9",
        ["LZ4 max"] = "#439894",
        ["Zstd 1"] = "#f2cf5b",
        ["Zstd 19"] = "#b8860b",
    };

    public required string Title { get; init; }

    /// <summary>
    /// Gets the line under the title: what the values are, their unit, and which direction is better.
    /// </summary>
    public required string Subtitle { get; init; }

    public required IReadOnlyList<string> Categories { get; init; }

    public required IReadOnlyList<string> Series { get; init; }

    /// <summary>
    /// Gets the values by category, then by series; NaN for a value that is missing.
    /// </summary>
    public required double[][] Values { get; init; }

    public bool LogScale { get; init; }

    public required Func<double, string> Format { get; init; }

    /// <summary>
    /// Gets a description for screen readers, which should say where the same numbers are as a table.
    /// </summary>
    public required string Description { get; init; }

    public string? Note { get; init; }

    public string Render()
    {
        var all = Values.SelectMany(v => v).Where(double.IsFinite).ToList();
        double max = all.Count is 0 ? 1 : all.Max();
        double min = all.Count is 0 ? 1 : all.Where(v => v > 0).DefaultIfEmpty(1).Min();

        (double lower, double upper, double[] ticks) = LogScale ? LogAxis(min, max) : LinearAxis(max);

        int plotWidth = Width - LabelWidth - RightMargin;
        double Scale(double v) => LogScale
            ? plotWidth * (Math.Log10(Math.Max(v, lower)) - Math.Log10(lower)) / (Math.Log10(upper) - Math.Log10(lower))
            : plotWidth * v / upper;

        // Legend items in rows across the width.
        var legend = new List<(string Name, double X, double Y)>();
        double lx = 8, ly = 0;

        foreach (string name in Series)
        {
            double itemWidth = 22 + (name.Length * 6.6);

            if (lx + itemWidth > Width - 8 && lx > 8)
            {
                lx = 8;
                ly += 18;
            }

            legend.Add((name, lx, ly));
            lx += itemWidth + 12;
        }

        double top = 52 + ly + 30;
        double groupHeight = (Series.Count * BarHeight) + GroupGap;
        double plotHeight = Categories.Count * groupHeight;
        double height = top + plotHeight + 40 + (Note is null ? 0 : 18);

        // Ids of its own, so that several charts can be inlined in one page.
        string id = "chart-" + string.Concat(Title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        var svg = new StringBuilder();
        svg.Append(Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {Width} {height:F0}\" width=\"{Width}\" height=\"{height:F0}\" "));
        svg.AppendLine($"role=\"img\" aria-labelledby=\"{id}-title {id}-desc\" font-family=\"system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif\">");
        svg.AppendLine($"<title id=\"{id}-title\">{Escape(Title)}</title>");
        svg.AppendLine($"<desc id=\"{id}-desc\">{Escape(Description)}</desc>");

        // Shown as an image, the chart has no page colour to take: dark grey text, or light grey where the viewer is in dark mode. Inline in a page, the
        // rule does not apply (the svg is not the root) and the text takes the colour of the page.
        svg.AppendLine("<style>svg:root{color:#1f2328}@media (prefers-color-scheme: dark){svg:root{color:#e6edf3}}</style>");
        svg.AppendLine(Invariant($"<text x=\"8\" y=\"20\" font-size=\"15\" font-weight=\"600\" fill=\"currentColor\">{Escape(Title)}</text>"));
        svg.AppendLine(Invariant($"<text x=\"8\" y=\"38\" font-size=\"12\" fill=\"currentColor\" fill-opacity=\"0.8\">{Escape(Subtitle)}</text>"));

        foreach ((string name, double x, double y) in legend)
        {
            svg.AppendLine(Invariant($"<rect x=\"{x:F1}\" y=\"{52 + y:F1}\" width=\"14\" height=\"10\" rx=\"2\" fill=\"{Color(name)}\"/>"));
            svg.AppendLine(Invariant($"<text x=\"{x + 19:F1}\" y=\"{61 + y:F1}\" font-size=\"11\" fill=\"currentColor\">{Escape(name)}</text>"));
        }

        // Grid lines and tick labels.
        foreach (double tick in ticks)
        {
            double x = LabelWidth + Scale(tick);
            svg.AppendLine(Line(x, top - 6, x, top + plotHeight, 0.15));
            svg.AppendLine(Text(x, top + plotHeight + 14, 10, Format(tick), "text-anchor=\"middle\" fill-opacity=\"0.8\""));
        }

        for (int c = 0; c < Categories.Count; c++)
        {
            double groupTop = top + (c * groupHeight);
            double labelY = groupTop + (Series.Count * BarHeight / 2) + 4;
            svg.AppendLine(Text(LabelWidth - 8, labelY, 11, Categories[c], "text-anchor=\"end\""));

            for (int s = 0; s < Series.Count; s++)
            {
                double value = Values[c][s];

                if (!double.IsFinite(value))
                    continue;

                double y = groupTop + (s * BarHeight);
                double w = Math.Max(0.5, Scale(value));
                svg.Append(Invariant($"<rect x=\"{LabelWidth}\" y=\"{y:F1}\" width=\"{w:F1}\" height=\"{BarHeight - 1:F1}\" fill=\"{Color(Series[s])}\">"));
                svg.AppendLine($"<title>{Escape($"{Categories[c]}, {Series[s]}: {Format(value)}")}</title></rect>");
                svg.AppendLine(Text(LabelWidth + w + 3, y + BarHeight - 1.2, 7.5, Format(value), "fill-opacity=\"0.85\""));
            }
        }

        svg.AppendLine(Line(LabelWidth, top - 6, LabelWidth, top + plotHeight, 0.5));

        if (Note is not null)
            svg.AppendLine(Text(8, top + plotHeight + 32, 10.5, Note, "fill-opacity=\"0.8\""));

        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    public static string Color(string series) => Colors.TryGetValue(series, out string? color) ? color : "#888888";

    private static (double Lower, double Upper, double[] Ticks) LinearAxis(double max)
    {
        double raw = max / 5;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw);
        double upper = Math.Ceiling(max * 1.08 / step) * step;

        return (0, upper, [.. Enumerable.Range(0, (int)Math.Round(upper / step) + 1).Select(i => i * step)]);
    }

    private static (double Lower, double Upper, double[] Ticks) LogAxis(double min, double max)
    {
        double lower = Math.Pow(10, Math.Floor(Math.Log10(min)));
        double upper = Math.Pow(10, Math.Ceiling(Math.Log10(max * 1.5)));
        var ticks = new List<double>();

        for (double t = lower; t <= upper * 1.0001; t *= 10)
            ticks.Add(t);

        return (lower, upper, [.. ticks]);
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    private static string Text(double x, double y, double size, string text, string attributes = "") =>
        Invariant($"<text x=\"{x:F1}\" y=\"{y:F1}\" font-size=\"{size}\" fill=\"currentColor\" {attributes}>{Escape(text)}</text>");

    private static string Line(double x1, double y1, double x2, double y2, double opacity) =>
        Invariant($"<line x1=\"{x1:F1}\" y1=\"{y1:F1}\" x2=\"{x2:F1}\" y2=\"{y2:F1}\" stroke=\"currentColor\" stroke-opacity=\"{opacity}\"/>");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
