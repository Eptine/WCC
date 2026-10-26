using System;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>Frozen initial-settings intent for a shared save, never write authority.</summary>
public sealed class WccMusicScheduleDraftContribution
{
    internal WccMusicScheduleDraftContribution(object editor, object binding, long version,
        WccScenarioInitialSettingsEdit initialSettings)
    { Editor = editor; Binding = binding; Version = version; InitialSettings = initialSettings; }
    internal object Editor { get; }
    internal object Binding { get; }
    internal long Version { get; }
    public WccScenarioInitialSettingsEdit InitialSettings { get; }
    public WmlStaticSource Source => InitialSettings.ScenarioRange.Source;
    public bool AuthorizesPersistence => false;
}

public partial class ScenarioWindowViewModel
{
    private WccMusicScheduleSourceBinding? _musicScheduleSourceBinding;
    private long _musicScheduleDraftVersion;

    // Selected difficulty and preview generations are view state. Source, closure, plan,
    // host and profile are captured independently and still fence every contribution.
    private sealed record WccMusicScheduleSourceBinding(
        ScenarioFileAsset Asset, ScenarioDocument Document, WmlStaticSourceRange Range,
        WccEditorSessionContext Host, string ProjectIdentity, WccLibrarySnapshot? Snapshot,
        WccPackageRevisionId? RootRevision, WmlCampaignEvaluationPlan? Plan,
        string VisibleRevisions, string MacroVisibleRevisions,
        string? InstallPath, string? AddonsPath, string ProfileIdentity, string? TextDomain)
    {
        // Original planning evidence survives the temporary preview revocation
        // during native Save/publication. It cannot authorize a source write.
        internal WmlStaticScalarProjection? DefinitionProjection { get; init; }
        internal RawEditorReferenceSnapshot? DefinitionReferences { get; init; }
    }

    private bool CanCaptureWccMusicScheduleSource() => CanInspectWccScenarioSource() &&
        !IsReadOnly && _sourceHostSession.CanWrite &&
        !IsProtectedWesnothInstallPath(_currentSourcePath ?? LoadedPath);

    // Reading captured source does not require ownership of a write boundary.
    // Story previews use this gate; music, events and Save retain the wrapper above.
    private bool CanInspectWccScenarioSource() =>
        _sourceHostSession.Scope == WccEditorSessionScope.Project && !IsWccInlineSaveBusy &&
        !WccFieldEditSession.IsBusy &&
        WccFieldEditSession.Recovery is null &&
        GetWccInlineBindingProblem() is null && !_hasAuthoredSourceChanges && !_externalSourceChangesPending &&
        _currentScenarioAsset is not null && SelectedScenario is { } selected &&
        _currentScenarioAsset.Documents.Any(document => ReferenceEquals(document, selected.Document)) &&
        _pristineLoadedSourceText is { } text && selected.Document.OriginalText == text &&
        _currentScenarioAsset.RawTextSnapshot == text &&
        !string.IsNullOrWhiteSpace(_currentSourcePath ?? LoadedPath) &&
        IsPathInsideRoot(_currentSourcePath ?? LoadedPath, _sourceHostSession.RootPath) &&
        selected.Document.BlockSpan.StartIndex >= 0 && selected.Document.BlockSpan.EndIndexExclusive <= text.Length &&
        selected.Document.BlockSpan.EndIndexExclusive > selected.Document.BlockSpan.StartIndex;

    private WccMusicScheduleSourceBinding CaptureWccMusicScheduleBinding(WccScenarioRawEditDraft draft) => new(
        _currentScenarioAsset!, draft.Document,
        new(new WmlStaticSource(draft.SourcePath, draft.SourceText), draft.Document.BlockSpan.StartIndex,
            draft.Document.BlockSpan.EndIndexExclusive - draft.Document.BlockSpan.StartIndex),
        _sourceHostSession, CurrentWccFieldProjectIdentity, _macroLibrarySnapshot,
        _staticRootRevisionSelection?.RootRevisionId, _sourceEvaluationPlan,
        MusicScheduleRevisionSignature(ScenarioContentVisibleRevisions), MusicScheduleRevisionSignature(_macroVisibleRevisionIds),
        _wesnothSourceInstallPath, _wesnothSourceAddonsPath, _wesnothSourceProfileIdentity, GetWccScenarioTextDomain())
    {
        DefinitionProjection = CurrentMusicScheduleAuthoringProjection,
        DefinitionReferences = CurrentMusicScheduleAuthoringProjection is not null &&
            _rawEditorReferenceSnapshot is { } references &&
            references.Generation == _staticInitialValuesGeneration && references.ContextKey == _evaluationContext.SemanticKey &&
            PathsEqual(references.Source.Path, draft.SourcePath) && references.Source.Text == draft.SourceText
                ? references : null
    };

    private string? GetWccMusicScheduleBindingProblem(WccMusicScheduleSourceBinding binding)
        => GetWccScenarioSourceBindingProblem(binding) ??
            (IsReadOnly || !_sourceHostSession.CanWrite || IsProtectedWesnothInstallPath(binding.Range.Source.Path)
                ? "The campaign, indexed source or profile binding changed. Music and schedule drafts are retained." : null);

    private string? GetWccScenarioSourceBindingProblem(WccMusicScheduleSourceBinding binding)
    {
        if (!ReferenceEquals(binding.Asset, _currentScenarioAsset) ||
            !ReferenceEquals(binding.Document, SelectedScenario?.Document) ||
            !binding.Asset.Documents.Any(document => ReferenceEquals(document, binding.Document)) ||
            !PathsEqual(binding.Range.Source.Path, _currentSourcePath ?? LoadedPath) ||
            binding.Range.Source.Text != _pristineLoadedSourceText ||
            binding.Range.Source.Text != binding.Asset.RawTextSnapshot ||
            binding.Document.OriginalText != binding.Range.Source.Text ||
            binding.Document.BlockSpan.StartIndex != binding.Range.StartOffset ||
            binding.Document.BlockSpan.EndIndexExclusive != binding.Range.StartOffset + binding.Range.Length ||
            _hasAuthoredSourceChanges || _externalSourceChangesPending)
            return "The scenario source changed. Music and schedule drafts are retained.";
        if (_sourceHostSession.Scope != WccEditorSessionScope.Project ||
            !ReferenceEquals(binding.Host, _sourceHostSession) ||
            binding.ProjectIdentity != CurrentWccFieldProjectIdentity ||
            !IsPathInsideRoot(binding.Range.Source.Path, _sourceHostSession.RootPath) ||
            !ReferenceEquals(binding.Snapshot, _macroLibrarySnapshot) ||
            binding.RootRevision != _staticRootRevisionSelection?.RootRevisionId ||
            (_staticRootRevisionSelection is { } root && !ReferenceEquals(root.Snapshot, _macroLibrarySnapshot)) ||
            !ReferenceEquals(binding.Plan, _sourceEvaluationPlan) ||
            binding.VisibleRevisions != MusicScheduleRevisionSignature(ScenarioContentVisibleRevisions) ||
            binding.MacroVisibleRevisions != MusicScheduleRevisionSignature(_macroVisibleRevisionIds) ||
            !SameOptionalWccInlinePath(binding.InstallPath, _wesnothSourceInstallPath) ||
            !SameOptionalWccInlinePath(binding.AddonsPath, _wesnothSourceAddonsPath) ||
            binding.ProfileIdentity != _wesnothSourceProfileIdentity || binding.TextDomain != GetWccScenarioTextDomain())
            return "The campaign, indexed source or profile binding changed. Music and schedule drafts are retained.";
        if (WccFieldEditSession.Recovery is not null)
            return "A previous save requires source recovery. Music and schedule drafts are retained.";
        return GetWccInlineBindingProblem();
    }

    private static string MusicScheduleRevisionSignature(System.Collections.Generic.IEnumerable<WccPackageRevisionId>? revisions) =>
        revisions is null ? "<unbound>" : string.Join("\u001f", revisions.Select(revision => revision.ToString()).Order(StringComparer.Ordinal));

    public bool TryCaptureWccMusicScheduleContribution(out WccMusicScheduleDraftContribution? contribution, out string? problem)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            contribution = null;
            if (!HasWccMusicScheduleDrafts || _musicScheduleSourceBinding is not { } binding)
            { problem = "There are no pending music or schedule changes."; return false; }
            problem = GetWccMusicScheduleBindingProblem(binding);
            if (problem is not null || !TryBuildWccMusicScheduleCandidate(fresh: true, out _, out problem)) return false;
            var settings = CreateWccMusicScheduleSettings(binding.Range);
            contribution = new(this, binding, _musicScheduleDraftVersion, settings);
            return true;
        }
    }

    public bool IsCurrentWccMusicScheduleContribution(WccMusicScheduleDraftContribution contribution)
    {
        lock (_wccInitialFieldRefreshGate)
            return ReferenceEquals(contribution.Editor, this) && HasWccMusicScheduleDrafts &&
                ReferenceEquals(contribution.Binding, _musicScheduleSourceBinding) &&
                contribution.Version == _musicScheduleDraftVersion && _musicScheduleSourceBinding is { } binding &&
                GetWccMusicScheduleBindingProblem(binding) is null &&
                TryBuildWccMusicScheduleCandidate(fresh: true, out _, out _);
    }

    // The shared coordinator has already committed. The token only identifies which
    // proposed version to clear; a later draft can never be consumed by this callback.
    public bool CompleteWccMusicScheduleContribution(WccMusicScheduleDraftContribution contribution)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!ReferenceEquals(contribution.Editor, this) || !HasWccMusicScheduleDrafts ||
                !ReferenceEquals(contribution.Binding, _musicScheduleSourceBinding) ||
                contribution.Version != _musicScheduleDraftVersion) return false;
            ClearWccMusicScheduleDraft();
            RaiseWccMusicScheduleChanged();
            return true;
        }
    }
}
