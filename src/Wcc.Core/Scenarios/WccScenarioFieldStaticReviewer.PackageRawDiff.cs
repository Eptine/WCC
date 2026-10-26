namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private readonly record struct PackageRawLine(int Start, int Length);
    private readonly record struct PackageRawHunk(int BeforeStart, int BeforeLength, int AfterStart, int AfterLength);

    private static IReadOnlyList<PackageRawHunk> PackageRawChangedLines(string before, string after)
    {
        var oldLines = Lines(before); var newLines = Lines(after);
        var first = 0;
        while (first < oldLines.Count && first < newLines.Count && Same(first, first)) first++;
        var oldEnd = oldLines.Count; var newEnd = newLines.Count;
        while (oldEnd > first && newEnd > first && Same(oldEnd - 1, newEnd - 1)) { oldEnd--; newEnd--; }
        var oldCount = oldEnd - first; var newCount = newEnd - first;
        var matches = new List<(int Before, int After)>();
        if (oldCount != 0 && newCount != 0)
        {
            // Myers' exact line matches avoid quadratic file-size storage. Bound both saved frontiers
            // and search work; a broad rewrite must fail honestly instead of widening its footprint.
            const int limit = 4 * 1024 * 1024;
            long work = 0;
            var trace = new List<int[]>();
            var found = false;
            for (var distance = 0; distance <= oldCount + newCount && !found; distance++)
            {
                work += 2L * distance + 1;
                if (work > limit) throw TooBroad();
                var frontier = new int[2 * distance + 1];
                var previous = distance == 0 ? null : trace[^1];
                for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
                {
                    var x = distance == 0 ? 0 : diagonal == -distance
                        || diagonal != distance && previous![diagonal + distance - 2] < previous![diagonal + distance]
                            ? previous![diagonal + distance] : previous![diagonal + distance - 2] + 1;
                    var y = x - diagonal;
                    while (x < oldCount && y < newCount)
                    {
                        if (++work > limit) throw TooBroad();
                        if (!Same(first + x, first + y)) break;
                        x++; y++;
                    }
                    frontier[diagonal + distance] = x;
                    if (x == oldCount && y == newCount) { found = true; break; }
                }
                trace.Add(frontier);
            }
            if (!found) throw TooBroad();
            var oldPosition = oldCount; var newPosition = newCount;
            for (var distance = trace.Count - 1; distance > 0; distance--)
            {
                var previous = trace[distance - 1]; var diagonal = oldPosition - newPosition;
                var previousDiagonal = diagonal == -distance
                    || diagonal != distance && previous[diagonal + distance - 2] < previous[diagonal + distance]
                        ? diagonal + 1 : diagonal - 1;
                var previousX = previous[previousDiagonal + distance - 1]; var previousY = previousX - previousDiagonal;
                while (oldPosition > previousX && newPosition > previousY)
                    matches.Add((first + --oldPosition, first + --newPosition));
                oldPosition = previousX; newPosition = previousY;
            }
            while (oldPosition > 0 && newPosition > 0)
                matches.Add((first + --oldPosition, first + --newPosition));
            matches.Reverse();
        }
        var hunks = new List<PackageRawHunk>(); var oldCursor = first; var newCursor = first;
        foreach (var match in matches)
        {
            AddHunk(match.Before, match.After);
            oldCursor = match.Before + 1; newCursor = match.After + 1;
        }
        AddHunk(oldEnd, newEnd);
        return hunks;

        bool Same(int oldIndex, int newIndex) => before.AsSpan(oldLines[oldIndex].Start, oldLines[oldIndex].Length)
            .SequenceEqual(after.AsSpan(newLines[newIndex].Start, newLines[newIndex].Length));
        void AddHunk(int oldBoundary, int newBoundary)
        {
            if (oldCursor == oldBoundary && newCursor == newBoundary) return;
            var oldStart = Boundary(oldLines, oldCursor, before.Length); var newStart = Boundary(newLines, newCursor, after.Length);
            hunks.Add(new(oldStart, Boundary(oldLines, oldBoundary, before.Length) - oldStart,
                newStart, Boundary(newLines, newBoundary, after.Length) - newStart));
        }
        static int Boundary(IReadOnlyList<PackageRawLine> lines, int index, int length) => index == lines.Count ? length : lines[index].Start;
        static List<PackageRawLine> Lines(string text)
        {
            var lines = new List<PackageRawLine>(); var start = 0;
            while (start < text.Length)
            {
                if (lines.Count >= 128 * 1024) throw TooBroad();
                var end = start;
                while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
                if (end < text.Length && text[end] == '\r') end++;
                if (end < text.Length && text[end] == '\n') end++;
                lines.Add(new(start, end - start)); start = end;
            }
            return lines;
        }
        static InvalidOperationException TooBroad() => new("The RAW proposal exceeds the bounded disjoint-line review budget; make a smaller source edit.");
    }
}
