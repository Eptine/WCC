using System;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>A manual authored-source draft, separate from resolved/generated data.</summary>
public sealed class WccScenarioRawEditDraft
{
    internal WccScenarioRawEditDraft(ScenarioDocument document, string sourcePath, string sourceText,
        long hostGeneration, long staticGeneration, ScenarioSideSourceLocation? location)
    {
        Document = document;
        SourcePath = sourcePath;
        SourceText = sourceText;
        HostGeneration = hostGeneration;
        StaticGeneration = staticGeneration;
        SourceLocation = location;
        Text = sourceText[document.BlockSpan.StartIndex..document.BlockSpan.EndIndexExclusive];
    }
    internal ScenarioDocument Document { get; }
    internal string SourcePath { get; }
    internal string SourceText { get; }
    internal long HostGeneration { get; }
    internal long StaticGeneration { get; }
    public string Text { get; }
    public ScenarioSideSourceLocation? SourceLocation { get; }
}

public sealed class WccScenarioFileRawEditDraft
{
    internal WccScenarioFileRawEditDraft(WccScenarioRawEditDraft scenario) => Scenario = scenario;
    internal WccScenarioRawEditDraft Scenario { get; }
    public string Text => Scenario.SourceText;
}

public partial class ScenarioWindowViewModel
{
    public WccScenarioFileRawEditDraft? BeginWccScenarioFileRawEdit() =>
        BeginWccScenarioSourceRawEdit() is { } scenario ? new(scenario) : null;

    public string? ValidateWccScenarioFileRawEdit(WccScenarioFileRawEditDraft draft, string raw)
    {
        if (!IsCurrentWccRawDraft(draft.Scenario)) return "The source or editing context changed. Reopen its RAW editor.";
        var validation = WmlSingleBlockValidator.ValidateSourceStructure(raw);
        if (!validation.IsValid) return validation.Problem;
        try
        {
            if (ScenarioImporter.ImportAllFromText(raw).Count != Scenarios.Count)
                return "The RAW change must preserve the file's scenario blocks.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return "The scenario source could not be imported: " + exception.Message;
        }
        return null;
    }

    public bool ApplyWccScenarioFileRawEdit(WccScenarioFileRawEditDraft draft, string raw)
    {
        if (ValidateWccScenarioFileRawEdit(draft, raw) is { } problem)
        {
            Status = problem;
            return false;
        }
        if (string.Equals(raw, draft.Text, StringComparison.Ordinal)) return true;
        return ApplyWccAuthoredSourceDraft(raw);
    }

    // Manual source editing also works when expansion is incomplete. The
    // selected document must still be an exact authored asset document.
    public WccScenarioRawEditDraft? BeginWccScenarioSourceRawEdit()
    {
        var path = _currentSourcePath ?? LoadedPath;
        if (!CanUseAuthoredWccFieldRawEditor || _currentScenarioAsset is null ||
            _hasAuthoredSourceChanges || _externalSourceChangesPending ||
            _pristineLoadedSourceText is not { } text || string.IsNullOrWhiteSpace(path) ||
            SelectedScenario is not { } selected ||
            !_currentScenarioAsset.Documents.Any(document => ReferenceEquals(document, selected.Document)) ||
            !string.Equals(selected.Document.OriginalText, text, StringComparison.Ordinal) ||
            !string.Equals(_currentScenarioAsset.RawTextSnapshot, text, StringComparison.Ordinal)) return null;
        var span = selected.Document.BlockSpan;
        if (span.StartIndex < 0 || span.EndIndexExclusive > text.Length || span.EndIndexExclusive <= span.StartIndex)
            return null;
        return new(selected.Document, path, text, _sourceHostGeneration, _staticInitialValuesGeneration, null);
    }

    private bool CanUseAuthoredWccFieldRawEditor => CanEditDocument && _sourceHostSession.CanWrite &&
        _sourceHostSession.Scope == WccEditorSessionScope.Project &&
        !WccFieldEditSession.IsBusy && IsPathInsideRoot(_currentSourcePath ?? LoadedPath, _sourceHostSession.RootPath) &&
        !IsProtectedWesnothInstallPath(_currentSourcePath ?? LoadedPath);

    public WccScenarioRawEditDraft? BeginWccInitialFieldRawEdit(string key)
    {
        var path = _currentSourcePath ?? LoadedPath;
        if (!CanUseAuthoredWccFieldRawEditor || _currentScenarioAsset is null ||
            _hasAuthoredSourceChanges || _externalSourceChangesPending || _pristineLoadedSourceText is not { } text ||
            string.IsNullOrWhiteSpace(path) || !IsPathInsideRoot(path, _sourceHostSession.RootPath) ||
            IsProtectedWesnothInstallPath(path) || SelectedScenario is not { } selected ||
            !HasCurrentStaticScalarProjection || _staticScenarioOwner is not { OpeningOutputOffset: not null } owner ||
            !IsLiteralStaticOpening(owner) || owner.OpeningOrigin is not { } opening ||
            !PathsEqual(opening.Source.Path, path) || !string.Equals(opening.Source.Text, text, StringComparison.Ordinal) ||
            opening.StartOffset != selected.Document.BlockSpan.StartIndex ||
            !string.Equals(_currentScenarioAsset.RawTextSnapshot, text, StringComparison.Ordinal))
            return null;
        var span = selected.Document.BlockSpan;
        if (span.StartIndex < 0 || span.EndIndexExclusive > text.Length || span.EndIndexExclusive <= span.StartIndex)
            return null;
        return new(selected.Document, path, text, _sourceHostGeneration, _staticInitialValuesGeneration,
            GetWccInitialFieldSourceLocation(key));
    }

    public string? ValidateWccInitialFieldRawEdit(WccScenarioRawEditDraft draft, string raw)
    {
        if (!IsCurrentWccRawDraft(draft)) return "The source or editing context changed. Reopen its RAW editor.";
        var validation = WmlSingleBlockValidator.Validate(raw, "scenario");
        if (!validation.IsValid) return validation.Problem;
        try
        {
            var candidate = ReplaceWccRawDraft(draft, raw);
            var documents = ScenarioImporter.ImportAllFromText(candidate);
            if (documents.Count != Scenarios.Count)
                return "The RAW change must preserve the containing scenario file's scenario blocks.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return "The scenario source could not be imported: " + exception.Message;
        }
        return null;
    }

    public bool ApplyWccInitialFieldRawEdit(WccScenarioRawEditDraft draft, string raw)
    {
        if (ValidateWccInitialFieldRawEdit(draft, raw) is { } problem)
        {
            Status = problem;
            return false;
        }
        if (string.Equals(raw, draft.Text, StringComparison.Ordinal)) return true;
        return ApplyWccAuthoredSourceDraft(ReplaceWccRawDraft(draft, raw));
    }

    private bool ApplyWccAuthoredSourceDraft(string candidateSource)
    {
        var documents = ScenarioImporter.ImportAllFromText(candidateSource);
        var scenarioIndex = Scenarios.IndexOf(SelectedScenario!);
        var sideIndex = GetAuthoredSideIndex(SelectedSide);
        var asset = _currentScenarioAsset!;
        InvalidateSourceEditingSession();
        _hasAuthoredSourceChanges = true;
        RunUiSync(() =>
        {
            SelectedScenario = null;
            asset.Documents.Clear();
            asset.Documents.AddRange(documents);
            Scenarios.Clear();
            foreach (var document in documents) Scenarios.Add(new ScenarioDocVm(document));
            SelectedScenario = Scenarios.ElementAtOrDefault(scenarioIndex) ?? Scenarios.FirstOrDefault();
            SelectedSide = Sides.FirstOrDefault(side => GetAuthoredSideIndex(side) == sideIndex) ?? Sides.FirstOrDefault();
        });
        NotifyDocumentChanged();
        Status = "Scenario source changed in the working draft. Save the scenario file to write it.";
        return true;
    }

    private bool IsCurrentWccRawDraft(WccScenarioRawEditDraft draft) =>
        !IsReadOnly && _sourceHostSession.CanWrite && _sourceHostSession.Scope == WccEditorSessionScope.Project &&
        !HasWccInlineDrafts && !HasWccSideFlagDrafts && !HasWccEventDrafts && !HasWccSideTopologyDrafts && !HasWccLeaderAdditionDrafts && !HasWccInitialUnitDrafts && !HasWccStoryDraft && !IsWccInlineSaveBusy &&
        (!HasWccMusicScheduleDrafts || IsUsingWccMusicScheduleSourceDraft) &&
        !WccFieldEditSession.IsBusy && !_hasAuthoredSourceChanges &&
        !_externalSourceChangesPending && _currentScenarioAsset is not null &&
        draft.HostGeneration == _sourceHostGeneration && draft.StaticGeneration == _staticInitialValuesGeneration &&
        ReferenceEquals(draft.Document, SelectedScenario?.Document) &&
        PathsEqual(draft.SourcePath, _currentSourcePath ?? LoadedPath) &&
        IsPathInsideRoot(draft.SourcePath, _sourceHostSession.RootPath) && !IsProtectedWesnothInstallPath(draft.SourcePath) &&
        string.Equals(draft.SourceText, _pristineLoadedSourceText, StringComparison.Ordinal) &&
        string.Equals(draft.SourceText, _currentScenarioAsset.RawTextSnapshot, StringComparison.Ordinal);

    private static string ReplaceWccRawDraft(WccScenarioRawEditDraft draft, string raw) =>
        draft.SourceText[..draft.Document.BlockSpan.StartIndex] + raw +
        draft.SourceText[draft.Document.BlockSpan.EndIndexExclusive..];
}
