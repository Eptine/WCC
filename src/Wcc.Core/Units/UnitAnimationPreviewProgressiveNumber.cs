using System.Globalization;

namespace Wcc.Core.Units;

/// <summary>
/// Read-only evaluator for AnimationWML progressive numeric values such as
/// <c>0~0.8:180,0.8~0:230</c>. It is used only by previews; source WML remains
/// authoritative.
/// </summary>
public sealed class UnitAnimationPreviewProgressiveNumber
{
    private readonly IReadOnlyList<Segment> _segments;

    private UnitAnimationPreviewProgressiveNumber(
        string expression,
        IReadOnlyList<Segment> segments)
    {
        Expression = expression;
        _segments = segments;
    }

    public string Expression { get; }
    public int? ExplicitDurationMs => _segments.All(x => x.DurationMs.HasValue)
        ? _segments.Sum(x => x.DurationMs!.Value)
        : null;

    public static bool TryParse(
        string? expression,
        out UnitAnimationPreviewProgressiveNumber? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        var normalized = expression.Trim();
        if (normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"')
            normalized = normalized[1..^1].Trim();
        if (normalized.Length == 0)
            return false;

        var segments = new List<Segment>();
        foreach (var rawPart in normalized.Split(',', StringSplitOptions.TrimEntries))
        {
            if (rawPart.Length == 0)
                return false;

            var valuePart = rawPart;
            int? durationMs = null;
            var colon = rawPart.LastIndexOf(':');
            if (colon >= 0)
            {
                valuePart = rawPart[..colon].Trim();
                if (!int.TryParse(
                        rawPart[(colon + 1)..].Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var parsedDuration) || parsedDuration < 0)
                {
                    return false;
                }

                durationMs = parsedDuration;
            }

            var rangeParts = valuePart.Split('~', StringSplitOptions.TrimEntries);
            if (rangeParts.Length is < 1 or > 2 ||
                !TryParseNumber(rangeParts[0], out var from) ||
                (rangeParts.Length == 2 && !TryParseNumber(rangeParts[1], out _)))
            {
                return false;
            }

            var to = rangeParts.Length == 2
                ? double.Parse(rangeParts[1], NumberStyles.Float, CultureInfo.InvariantCulture)
                : from;
            segments.Add(new Segment(from, to, durationMs));
        }

        if (segments.Count == 0)
            return false;

        value = new UnitAnimationPreviewProgressiveNumber(normalized, segments);
        return true;
    }

    public double Evaluate(double elapsedTimeMs, double availableDurationMs)
    {
        if (_segments.Count == 0)
            return 0;

        var durations = AllocateDurations(Math.Max(1, availableDurationMs));
        var remaining = Math.Max(0, elapsedTimeMs);
        for (var i = 0; i < _segments.Count; i++)
        {
            var duration = durations[i];
            if (remaining <= duration || i == _segments.Count - 1)
            {
                if (duration <= 0)
                    return _segments[i].To;
                var progress = Math.Clamp(remaining / duration, 0, 1);
                return _segments[i].From + ((_segments[i].To - _segments[i].From) * progress);
            }

            remaining -= duration;
        }

        return _segments[^1].To;
    }

    private double[] AllocateDurations(double availableDurationMs)
    {
        var result = new double[_segments.Count];
        var explicitDuration = _segments
            .Where(x => x.DurationMs.HasValue)
            .Sum(x => (double)x.DurationMs!.Value);
        var unspecifiedCount = _segments.Count(x => !x.DurationMs.HasValue);
        var unspecifiedDuration = unspecifiedCount == 0
            ? 0
            : Math.Max(0, availableDurationMs - explicitDuration) / unspecifiedCount;

        if (unspecifiedCount > 0 && unspecifiedDuration <= 0)
            unspecifiedDuration = availableDurationMs / _segments.Count;

        for (var i = 0; i < _segments.Count; i++)
            result[i] = _segments[i].DurationMs ?? unspecifiedDuration;

        return result;
    }

    private static bool TryParseNumber(string raw, out double value)
        => double.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);

    private sealed record Segment(double From, double To, int? DurationMs);
}
