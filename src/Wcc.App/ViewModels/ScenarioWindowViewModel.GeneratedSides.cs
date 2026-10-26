using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>A location in captured source. It is navigation evidence, never write authority.</summary>
public sealed record ScenarioSideSourceLocation(
    string SourcePath,
    string SourceText,
    int SelectionStart,
    int SelectionLength,
    string Description);

public partial class ScenarioWindowViewModel
{
    private string? _pendingGeneratedSideSelection;
    private string? _pendingGeneratedSideSourceSelection;

    public bool CanAddSide => CanEditDocument && SelectedScenario is not null && SelectedSide?.IsGenerated != true;
    public bool HasSelectedSideSource => GetSelectedSideSourceLocation() is not null;

    private int GetAuthoredSideIndex(ScenarioSideVm? row)
    {
        if (row?.AuthoredDocument is not { } document || SelectedScenario is null)
            return -1;
        return SelectedScenario.Document.SideDocuments.FindIndex(candidate => ReferenceEquals(candidate, document));
    }

    private void RevokeGeneratedSideRows()
    {
        if (SelectedSide is { IsGenerated: true } selected)
        {
            _pendingGeneratedSideSelection = selected.EffectiveIdentity;
            _pendingGeneratedSideSourceSelection = selected.EffectiveOwner is { } owner
                ? BuildGeneratedSideSourceIdentity(owner)
                : null;
        }

        RunUiSync(() =>
        {
            foreach (var row in Sides.ToArray())
            {
                row.EffectiveOwner = null;
                if (row.IsGenerated)
                    Sides.Remove(row);
            }

            if (SelectedSide?.IsGenerated == true)
                SelectedSide = Sides.FirstOrDefault();
        });
        OnPropertyChanged(nameof(CanAddSide));
        OnPropertyChanged(nameof(HasSelectedSideSource));
    }

    private void ReconcileStaticSideRows(WmlStaticSource source, ScenarioDocument document,
        WmlStaticScalarOwner scenario)
    {
        var generation = _staticInitialValuesGeneration;
        var authoredRows = document.SideDocuments.Select(side =>
                Sides.FirstOrDefault(row => ReferenceEquals(row.AuthoredDocument, side)) ?? new ScenarioSideVm(side))
            .ToArray();
        foreach (var row in authoredRows)
        {
            row.EffectiveOwner = null;
            if (generation != _staticInitialValuesGeneration)
                return;
        }

        var previousRows = Sides.Where(row => row.IsGenerated).ToArray();
        var rows = new System.Collections.Generic.List<ScenarioSideVm>();
        foreach (var effective in scenario.DirectChildren.Where(child => child.TagName == "side"))
        {
            var authored = authoredRows.Where(row =>
                    FindAuthoredSideOpeningOffset(source.Text, document, row.Document) is { } opening &&
                    IsLiteralStaticOpening(effective) && effective.OpeningOrigin is { } origin &&
                    origin.StartOffset == opening && PathsEqual(origin.Source.Path, source.Path) &&
                    string.Equals(origin.Source.ContentHash, source.ContentHash, StringComparison.Ordinal)).Take(2)
                .ToArray();
            if (authored.Length > 1)
                continue;
            ScenarioSideVm? row = authored.Length == 1 ? authored[0] : null;
            if (row is null)
            {
                // A field value or side number never supplies collection identity.
                // Require the exact direct parent and a mapped opening occurrence.
                if (scenario.OpeningOutputOffset is null || effective.OpeningOutputOffset is not { } output ||
                    effective.OpeningOrigin is not { Source.IsAuthored: true } ||
                    !ReferenceEquals(effective.Parent, scenario))
                    continue;
                var identity = source.ContentHash + ":" + document.BlockSpan.StartIndex + ":" + output + ":" +
                               BuildGeneratedSideSourceIdentity(effective);
                row = previousRows.FirstOrDefault(candidate => candidate.EffectiveIdentity == identity) ??
                      new ScenarioSideVm(effective, identity);
            }

            row.EffectiveOwner = effective;
            if (generation != _staticInitialValuesGeneration)
                return;
            rows.Add(row);
            if (row.AuthoredDocument is { } authoredDocument)
                _staticSideOwners[authoredDocument] = effective;
        }

        // Inactive or uncertain authored blocks stay available as source rows.
        // They receive no effective owner and never borrow another side's values.
        foreach (var row in authoredRows.Where(row => !rows.Contains(row)))
            rows.Add(row);

        foreach (var previous in previousRows.Where(row => !rows.Contains(row)))
        {
            previous.EffectiveOwner = null;
            if (generation != _staticInitialValuesGeneration)
                return;
        }

        var selection = SelectedSide;
        if (!rows.Contains(selection!))
        {
            selection = rows.FirstOrDefault(row => row.IsGenerated &&
                                                   row.EffectiveIdentity == _pendingGeneratedSideSelection);
            if (selection is null && _pendingGeneratedSideSourceSelection is { } sourceIdentity)
            {
                var matches = rows.Where(row => row.IsGenerated && row.EffectiveOwner is { } owner &&
                                                BuildGeneratedSideSourceIdentity(owner) == sourceIdentity).Take(2)
                    .ToArray();
                if (matches.Length == 1)
                    selection = matches[0];
            }

            selection ??= rows.FirstOrDefault();
        }
        else if (_pendingGeneratedSideSelection is not null)
        {
            var matches = rows.Where(row => row.IsGenerated &&
                                            (row.EffectiveIdentity == _pendingGeneratedSideSelection ||
                                             row.EffectiveOwner is { } owner &&
                                             BuildGeneratedSideSourceIdentity(owner) ==
                                             _pendingGeneratedSideSourceSelection)).Take(2).ToArray();
            if (matches.Length == 1)
                selection = matches[0];
        }

        RunUiSync(() =>
        {
            for (var index = 0; index < rows.Count; index++)
            {
                var existing = Sides.IndexOf(rows[index]);
                if (existing < 0)
                    Sides.Insert(index, rows[index]);
                else if (existing != index)
                    Sides.Move(existing, index);
                if (generation != _staticInitialValuesGeneration)
                    return;
            }

            while (Sides.Count > rows.Count)
            {
                Sides.RemoveAt(Sides.Count - 1);
                if (generation != _staticInitialValuesGeneration)
                    return;
            }

            SelectedSide = selection;
        });
        if (generation != _staticInitialValuesGeneration)
            return;
        _pendingGeneratedSideSelection = null;
        _pendingGeneratedSideSourceSelection = null;
        OnPropertyChanged(nameof(CanAddSide));
        OnPropertyChanged(nameof(HasSelectedSideSource));
    }

    private static string BuildGeneratedSideSourceIdentity(WmlStaticScalarOwner owner)
    {
        static string Range(WmlStaticSourceRange range) => range.Source.Path + "|" +
                                                           range.Source.ContentHash + "|" + range.StartOffset + "|" +
                                                           range.Length;

        return (owner.OpeningOrigin is { } opening ? Range(opening) : "") + ":" +
               string.Join("/", owner.OpeningExpansionStack.Select(frame => frame.MacroName + ":" +
                                                                            Range(frame.Definition) + ":" +
                                                                            string.Join(",",
                                                                                frame.InvocationOrigins
                                                                                    .Select(Range))));
    }

    public ScenarioSideSourceLocation? GetSelectedSideSourceLocation()
    {
        if (!HasCurrentStaticScalarProjection || _hasAuthoredSourceChanges || _externalSourceChangesPending ||
            SelectedStaticSideOwner is not { } owner || !ReferenceEquals(owner.Parent, _staticScenarioOwner) ||
            owner.OpeningOutputOffset is null)
            return null;
        var origins = owner.OpeningExpansionStack.Reverse()
            .Where(frame => frame.InvocationOrigins.Count == 1)
            .Select(frame => frame.InvocationOrigins[0]).Where(IsNavigableSourceRange).ToArray();
        // Prefer the instance invocation in this physical scenario over a shared
        // wrapper's body invocation. Both ranges refer to exact captured bytes.
        var target = SelectedSide?.IsGenerated == false
            ? owner.OpeningOrigin is { } authoredOpening && IsNavigableSourceRange(authoredOpening)
                ? authoredOpening
                : null
            : origins.FirstOrDefault(range => PathsEqual(range.Source.Path, _currentSourcePath ?? LoadedPath) &&
                                              range.StartOffset >= SelectedScenario!.Document.BlockSpan.StartIndex &&
                                              range.StartOffset + range.Length <= SelectedScenario.Document.BlockSpan
                                                  .EndIndexExclusive) ??
              origins.FirstOrDefault() ?? (owner.OpeningOrigin is { } opening && IsNavigableSourceRange(opening)
                  ? opening
                  : null);
        return target is null
            ? null
            : new(target.Source.Path, target.Source.Text, target.StartOffset, target.Length,
                SelectedSide?.IsGenerated == true
                    ? "Captured source invocation for the generated side. Read-only navigation; edits to shared macro definitions can affect other scenarios."
                    : "Captured authored opening for this side. Read-only source navigation.");
    }

    private static bool IsNavigableSourceRange(WmlStaticSourceRange range) => range.Source.IsAuthored &&
                                                                              range.StartOffset >= 0 &&
                                                                              range.Length > 0 &&
                                                                              range.StartOffset <=
                                                                              range.Source.Text.Length - range.Length;

    private void RefreshSelectedStaticSideSummary()
    {
        StaticSelectedSideValuesSummary = SelectedStaticSideOwner is { } owner
            ? BuildStaticSummary(owner, [
                ("side", "Side"), ("gold", "Gold"), ("income", "Income"),
                ("controller", "Controller"), ("type", "Leader type"), ("id", "Leader ID")
            ])
            : null;
        RaiseStaticInitialValuesChanged();
        OnPropertyChanged(nameof(CanAddSide));
        OnPropertyChanged(nameof(HasSelectedSideSource));
    }

    private static string RenderResolvedSideAttributes(WmlStaticScalarOwner owner)
    {
        var text = new StringBuilder();

        void Append(WmlStaticScalarOwner node, int depth)
        {
            var indent = new string(' ', depth * 4);
            text.Append(indent).Append('[').Append(node.TagName).AppendLine("]");
            foreach (var field in node.Fields.Values)
                if (field.ValueText is not null)
                    text.Append(indent).Append("    ").Append(field.Key).Append('=').AppendLine(field.ValueText);
            foreach (var child in node.DirectChildren)
                Append(child, depth + 1);
            text.Append(indent).Append("[/").Append(node.TagName).AppendLine("]");
        }

        Append(owner, 0);
        return text.ToString();
    }
}