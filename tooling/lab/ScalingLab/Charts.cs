using System.Globalization;
using System.Text;

namespace EventTicketing.Tooling.ScalingLab;

/// <summary>One line of a chart: a value per second, oldest first; null leaves a gap (nothing to measure).</summary>
/// <param name="Color">A CSS colour, normally a custom property such as <c>var(--ok)</c>.</param>
/// <param name="Dashed">For a reference line, such as what was offered.</param>
public sealed record ChartSeries(string Name, string Color, IReadOnlyList<double?> Values, bool Dashed = false);

/// <summary>The arithmetic of the lab's line charts: scales, paths and label placement, kept apart from the markup so it can be tested.</summary>
public static class Charts
{
    /// <summary>
    /// A round step for about <paramref name="ticks"/> gridlines up to <paramref name="max"/>: 1, 2, 2.5 or 5 times a power of ten,
    /// so tick labels stay short. Never below <paramref name="minimumTop"/> / ticks, so an idle chart has a sensible scale.
    /// </summary>
    public static (double Top, double Step) Scale(double max, double minimumTop, int ticks = 4)
    {
        var target = Math.Max(max, minimumTop) / ticks;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(factor => factor * magnitude).First(candidate => candidate >= target * (1 - 1e-9));
        return (step * Math.Ceiling(Math.Max(max, minimumTop) / step - 1e-9), step);
    }

    /// <summary>An SVG path through the points, one segment per run of known values.</summary>
    public static string Path(IReadOnlyList<double?> values, Func<int, double> x, Func<double, double> y)
    {
        var path = new StringBuilder();
        var drawing = false;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } value)
            {
                drawing = false;
                continue;
            }

            path.Append(drawing ? " L" : " M").Append(Number(x(i))).Append(' ').Append(Number(y(value)));
            drawing = true;
        }

        return path.ToString().TrimStart();
    }

    /// <summary>
    /// Moves labels apart so none is closer than <paramref name="gap"/> to the next, staying between
    /// <paramref name="top"/> and <paramref name="bottom"/>; the order of the labels is kept.
    /// </summary>
    public static IReadOnlyList<double> Spread(IReadOnlyList<double> positions, double gap, double top, double bottom)
    {
        var order = Enumerable.Range(0, positions.Count).OrderBy(i => positions[i]).ToList();
        var placed = new double[positions.Count];
        var previous = double.NegativeInfinity;
        foreach (var i in order)
        {
            placed[i] = Math.Max(Math.Max(positions[i], top), previous + gap);
            previous = placed[i];
        }

        // Too low at the bottom: push back up from the last label.
        var next = double.PositiveInfinity;
        foreach (var i in Enumerable.Reverse(order))
        {
            placed[i] = Math.Min(Math.Min(placed[i], bottom), next - gap);
            next = placed[i];
        }

        return placed;
    }

    /// <summary>A short label for a tick or a tile: 1,234 · 12.5 · 0.8.</summary>
    public static string Format(double value) => value switch
    {
        >= 100 => value.ToString("#,0", CultureInfo.InvariantCulture),
        >= 10 => value.ToString("0.#", CultureInfo.InvariantCulture),
        _ => value.ToString("0.##", CultureInfo.InvariantCulture),
    };

    public static string Number(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
