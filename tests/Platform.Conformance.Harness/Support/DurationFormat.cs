using System.Globalization;

namespace Platform.Conformance.Harness.Support;

/// <summary>Formats durations for people: <c>850 ms</c>, <c>12.4 s</c>, <c>3 min 05 s</c>, <c>1 h 02 min</c>.</summary>
public static class DurationFormat
{
    /// <summary>Formats <paramref name="duration"/> with the largest sensible unit, using the invariant culture.</summary>
    /// <param name="duration">The duration; negative values are formatted as zero.</param>
    /// <returns>A short human-readable duration.</returns>
    public static string Human(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration < TimeSpan.FromSeconds(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMilliseconds} ms");
        }

        if (duration < TimeSpan.FromMinutes(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.0} s");
        }

        if (duration < TimeSpan.FromHours(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes} min {duration.Seconds:00} s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours} h {duration.Minutes:00} min");
    }
}
