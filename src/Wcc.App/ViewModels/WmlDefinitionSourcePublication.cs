using System;
using System.IO;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>
/// Prepared in-memory publication of a manually committed physical source.
/// The host owns persistence, access, disk-byte verification and the index refresh.
/// </summary>
internal sealed record WmlDefinitionSourcePublication(Func<bool> IsCurrent, Func<bool> Publish)
{
    internal object? AssetIdentity { get; init; }

    internal static bool RunGuarded(Func<bool> isCurrent, Action[] actions)
    {
        foreach (var action in actions)
        {
            if (!isCurrent()) return false;
            action();
            if (!isCurrent()) return false;
        }
        return true;
    }

    internal static bool SamePath(string? left, string? right)
    {
        if (left is null || right is null) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        { return false; }
    }

    // Selection restoration only. Mapping a source observation grants no write authority.
    internal static bool SameMappedOwner(WmlStaticScalarOwner before, WmlStaticScalarOwner after,
        string path, string expectedText, string savedText)
    {
        var start = 0;
        while (start < expectedText.Length && start < savedText.Length && expectedText[start] == savedText[start]) start++;
        var oldEnd = expectedText.Length;
        var newEnd = savedText.Length;
        while (oldEnd > start && newEnd > start && expectedText[oldEnd - 1] == savedText[newEnd - 1])
        { oldEnd--; newEnd--; }
        bool SameRange(WmlStaticSourceRange? old, WmlStaticSourceRange? fresh)
        {
            if (old is null || fresh is null || !SamePath(old.Source.Path, fresh.Source.Path)) return false;
            var offset = old.StartOffset;
            var length = old.Length;
            if (SamePath(old.Source.Path, path))
            {
                if (old.Source.Text != expectedText || fresh.Source.Text != savedText) return false;
                var end = offset + length;
                if (end <= start) { }
                else if (offset >= oldEnd) offset += newEnd - oldEnd;
                else if (offset <= start && end >= oldEnd) length += newEnd - oldEnd;
                else return false;
            }
            else if (old.Source.ContentHash != fresh.Source.ContentHash) return false;
            return offset == fresh.StartOffset && length == fresh.Length;
        }
        if (before.TagName != after.TagName || !SameRange(before.OpeningOrigin, after.OpeningOrigin) ||
            before.OpeningExpansionStack.Count != after.OpeningExpansionStack.Count) return false;
        for (var index = 0; index < before.OpeningExpansionStack.Count; index++)
        {
            var old = before.OpeningExpansionStack[index];
            var fresh = after.OpeningExpansionStack[index];
            if (old.MacroName != fresh.MacroName || !SameRange(old.Definition, fresh.Definition) ||
                old.InvocationOrigins.Count != fresh.InvocationOrigins.Count) return false;
            for (var origin = 0; origin < old.InvocationOrigins.Count; origin++)
                if (!SameRange(old.InvocationOrigins[origin], fresh.InvocationOrigins[origin])) return false;
        }
        return true;
    }
}
