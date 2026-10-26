using System;
using System.Collections.Generic;
using System.Linq;

namespace Wcc.Core.Wml;

/// <summary>
/// Finds direct child blocks of a given tag inside matching top-level container blocks.
/// Example: find [movetype] or [trait] directly inside top-level [units].
/// </summary>
public static class WmlContainerChildBlockScanner
{
    public static IReadOnlyList<WmlBlock> FindDirectChildBlocks(
        string text,
        string containerTagName,
        string childTagName)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (string.IsNullOrWhiteSpace(containerTagName)) throw new ArgumentException("Container tag must be set.", nameof(containerTagName));
        if (string.IsNullOrWhiteSpace(childTagName)) throw new ArgumentException("Child tag must be set.", nameof(childTagName));

        var result = new List<WmlBlock>();

        foreach (var container in WmlTopLevelFileBlockScanner.FindBlocks(text, containerTagName))
        {
            var editable = WmlEditableBlockParser.Parse(
                container,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            var spans = WmlDirectChildBlockScanner.FindSpans(editable, childTagName);

            foreach (var span in spans)
            {
                var raw = string.Join(
                    editable.NewLine,
                    editable.Lines.Skip(span.StartLineIndex).Take(span.EndLineIndexExclusive - span.StartLineIndex));

                result.Add(new WmlBlock(childTagName, raw));
            }
        }

        return result;
    }
}