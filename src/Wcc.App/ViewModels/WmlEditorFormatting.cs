using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>UI-compatible presentation of one shared Core whitespace plan.</summary>
public sealed class WmlEditorFormattingPlan
{
    internal WmlEditorFormattingPlan(string sourceText, IEnumerable<WmlEditorReplacementChange> changes,
        int changedLineCount, int protectedLineCount)
    {
        SourceText = sourceText;
        Changes = Array.AsReadOnly(changes.ToArray());
        ChangedLineCount = changedLineCount;
        ProtectedLineCount = protectedLineCount;
    }

    public string SourceText { get; }
    public IReadOnlyList<WmlEditorReplacementChange> Changes { get; }
    public int ChangedLineCount { get; }
    public int ProtectedLineCount { get; }
}

/// <summary>The shared editor delegates formatting and Enter indentation to Core.</summary>
public static class WmlEditorFormatting
{
    public const int IndentSize = WmlFormatting.IndentSize;

    public static WmlEditorFormattingPlan Build(string source)
    {
        var plan = WmlFormatting.Build(source);
        return new(plan.SourceText, plan.Changes.Select(change => new WmlEditorReplacementChange(
            change.StartOffset, change.Length, change.ReplacementText)), plan.ChangedLineCount, plan.ProtectedLineCount);
    }

    public static string? GetLineIndent(string source, int lineOffset) =>
        WmlFormatting.GetLineIndent(source, lineOffset);
}
