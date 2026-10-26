using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

internal sealed record WccSideFlagResourceBinding(CatalogPickerSelection Selection, string IconPath,
    string Origin, IReadOnlyList<(string Path, string Hash)> Files);

public sealed class WccSideFlagPresetDraft
{
    internal WccSideFlagPresetDraft(string identity, string label, WccScenarioSideFlagPresetEdit recipe,
        WccSideFlagPresetSourcePlan plan, IReadOnlyList<WmlMacroExpansionDefinition> definitions,
        WccSideFlagResourceBinding resources)
    { Identity = identity; Label = label; Recipe = recipe; Plan = plan; Definitions = definitions; Resources = resources; }
    public string Identity { get; }
    public string Label { get; }
    public string BeforeInvocation => Plan.BaselineInvocation?.Text ?? "Absent flag pair";
    public string InvocationText => Recipe.InvocationText;
    public string Summary => BeforeInvocation + " → " + InvocationText;
    internal WccScenarioSideFlagPresetEdit Recipe { get; }
    internal WccSideFlagPresetSourcePlan Plan { get; }
    internal IReadOnlyList<WmlMacroExpansionDefinition> Definitions { get; }
    internal WccSideFlagResourceBinding Resources { get; }
}

public sealed class WccSideFlagPresetSelectionContext
{
    internal WccSideFlagPresetSelectionContext(object editor, object binding, ScenarioSideVm side,
        WmlStaticScalarOwner owner, long version, IReadOnlyList<WccSideFlagResourceBinding> resources)
    { Editor = editor; Binding = binding; Side = side; Owner = owner; Version = version; Resources = resources; }
    internal object Editor { get; }
    internal object Binding { get; }
    internal ScenarioSideVm Side { get; }
    internal WmlStaticScalarOwner Owner { get; }
    internal long Version { get; }
    internal IReadOnlyList<WccSideFlagResourceBinding> Resources { get; }
}

public sealed class WccSideFlagDraftContribution
{
    internal WccSideFlagDraftContribution(object editor, object binding, long version,
        WccScenarioInitialSettingsEdit settings)
    { Editor = editor; Binding = binding; Version = version; InitialSettings = settings; }
    internal object Editor { get; }
    internal object Binding { get; }
    internal long Version { get; }
    public WccScenarioInitialSettingsEdit InitialSettings { get; }
    public WmlStaticSource Source => InitialSettings.ScenarioRange.Source;
    public bool AuthorizesPersistence => false;
}

public partial class ScenarioWindowViewModel
{
    private sealed record SideFlagSourceBinding(ScenarioFileAsset Asset, ScenarioDocument Document,
        WmlStaticSourceRange Range, WccEditorSessionContext Host, string ProjectIdentity,
        WccLibrarySnapshot Snapshot, WccPackageRevisionId RootRevision, WmlCampaignEvaluationPlan Plan,
        string VisibleRevisions, string MacroVisibleRevisions, string? InstallPath, string? AddonsPath,
        string ProfileIdentity, string? TextDomain);
    private SideFlagSourceBinding? _sideFlagSourceBinding;
    private IReadOnlyList<WccSideFlagPresetDraft> _sideFlagDrafts = Array.Empty<WccSideFlagPresetDraft>();
    private long _sideFlagDraftVersion;
    public IReadOnlyList<WccSideFlagPresetDraft> WccSideFlagDrafts => _sideFlagDrafts;
    public bool HasWccSideFlagDrafts => _sideFlagDrafts.Count != 0;
    public event EventHandler? WccSideFlagDraftsChanged;

    public WccSideFlagPresetSelectionContext? BeginWccSideFlagPresetSelection()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!CanStageWccSideFlagPreset || SelectedSide is not { } side || SelectedStaticSideOwner is not { } owner)
                return null;
            var resources = GetWccSideFlagPresetOptions().Select(option =>
                WccSideFlagPresetSourcePlanner.TryReadInvocation(option.ValueId, out var name, out _)
                    ? CaptureSideFlagResources(option, name) : null).OfType<WccSideFlagResourceBinding>().ToArray();
            return new(this, _sideFlagSourceBinding ?? CaptureSideFlagSourceBinding(), side, owner,
                _sideFlagDraftVersion, Array.AsReadOnly(resources));
        }
    }

    public string? ValidateWccSideFlagPresetSelection(WccSideFlagPresetSelectionContext selection)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!ReferenceEquals(selection.Editor, this) || selection.Binding is not SideFlagSourceBinding binding ||
                !ReferenceEquals(selection.Side, SelectedSide) || !ReferenceEquals(selection.Owner, SelectedStaticSideOwner) ||
                selection.Version != _sideFlagDraftVersion || !CanStageWccSideFlagPreset)
                return "The selected side or preset draft changed. Reopen the flag picker.";
            return GetSideFlagBindingProblem(binding);
        }
    }

    public bool StageWccSideFlagPreset(WccSideFlagPresetSelectionContext selection, CatalogPickerSelection option)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (ValidateWccSideFlagPresetSelection(selection) is { } problem)
            { Status = problem; return false; }
            if (!IsCurrentFlagPresetOption(option) ||
                !WccSideFlagPresetSourcePlanner.TryReadInvocation(option.EmittedValue, out var name, out _))
            { Status = "The flag preset choice changed. Reopen the picker."; return false; }
            var resources = selection.Resources.SingleOrDefault(resource => resource.Selection == option);
            if (resources is null || !IsCurrentSideFlagResource(resources, GetWccSideFlagPresetOptions()))
            { Status = "The selected flag image family changed while the picker was open. Reopen it; existing drafts are retained."; return false; }
            if (_wccInlineDrafts.Any(draft => draft.Route.Key is "flag" or "flag_icon" && SameSideFlagOwner(draft.Route.Owner, selection.Owner)))
            { Status = "A scalar flag or flag_icon draft targets this side. Revert that draft or the flag preset before choosing another preset."; return false; }
            var binding = (SideFlagSourceBinding)selection.Binding;
            var definitions = GetWccMusicScheduleDefinitions(FlagPresetMacroNames, fresh: true).ToArray();
            var chosen = definitions.Where(definition => definition.Name == name).ToArray();
            if (chosen.Length != 1 || !WccSideFlagPresetSourcePlanner.IsSupportedDefinition(chosen[0]))
            { Status = "The exact flag definition is unavailable or changed. Refresh its source."; return false; }
            var recipe = new WccScenarioSideFlagPresetEdit(selection.Owner, option.EmittedValue, chosen[0]);
            var plan = WccSideFlagPresetSourcePlanner.Plan(binding.Range.Source, recipe);
            if (!plan.IsSuccess)
            { Status = plan.Diagnostic; return false; }
            var identity = SideFlagOwnerIdentity(selection.Owner);
            var remaining = _sideFlagDrafts.Where(draft => draft.Identity != identity).ToList();
            if (!plan.IsNoOp)
            {
                var label = "Side " + (selection.Side.DisplayNumber ?? "?") + " flag preset";
                remaining.Add(new(identity, label, recipe, plan, Array.AsReadOnly(definitions), resources));
            }
            _sideFlagSourceBinding = remaining.Count == 0 ? null : binding;
            _sideFlagDrafts = remaining.AsReadOnly();
            _sideFlagDraftVersion++;
            Status = remaining.Count == 0 ? "Flag preset matches the saved source."
                : "Flag preset staged. Save checks it together with the other campaign and scenario drafts.";
            RaiseSideFlagDraftsChanged();
            return true;
        }
    }

    private bool CanStageWccSideFlagPreset => CanCaptureWccMusicScheduleSource() &&
        _sourceEvaluationPlan is not null && _macroLibrarySnapshot is not null &&
        _staticRootRevisionSelection is { } root && ReferenceEquals(root.Snapshot, _macroLibrarySnapshot) &&
        TryGetFlagPresetSide(out var owner, out _, out _, out _) && !IsSharedFlagPreset(owner) &&
        (WccSideFlagPresetSourcePlanner.FindWinningInvocation(owner, new((_currentSourcePath ?? LoadedPath)!, _pristineLoadedSourceText!)) is not null ||
            owner.ResolveField("flag").State == WmlStaticScalarResolutionState.ProvenAbsent &&
            owner.ResolveField("flag_icon").State == WmlStaticScalarResolutionState.ProvenAbsent) &&
        (_sideFlagSourceBinding is null || GetSideFlagBindingProblem(_sideFlagSourceBinding) is null);

    private SideFlagSourceBinding CaptureSideFlagSourceBinding()
    {
        var source = new WmlStaticSource((_currentSourcePath ?? LoadedPath)!, _pristineLoadedSourceText!);
        var document = SelectedScenario!.Document;
        return new(_currentScenarioAsset!, document,
            new(source, document.BlockSpan.StartIndex, document.BlockSpan.EndIndexExclusive - document.BlockSpan.StartIndex),
            _sourceHostSession, CurrentWccFieldProjectIdentity, _macroLibrarySnapshot!, _staticRootRevisionSelection!.RootRevisionId,
            _sourceEvaluationPlan!, MusicScheduleRevisionSignature(ScenarioContentVisibleRevisions),
            MusicScheduleRevisionSignature(_macroVisibleRevisionIds), _wesnothSourceInstallPath, _wesnothSourceAddonsPath,
            _wesnothSourceProfileIdentity, GetWccScenarioTextDomain());
    }

    private string? GetSideFlagBindingProblem(SideFlagSourceBinding binding)
    {
        if (!ReferenceEquals(binding.Asset, _currentScenarioAsset) || !ReferenceEquals(binding.Document, SelectedScenario?.Document) ||
            !binding.Asset.Documents.Any(document => ReferenceEquals(document, binding.Document)) ||
            !PathsEqual(binding.Range.Source.Path, _currentSourcePath ?? LoadedPath) ||
            binding.Range.Source.Text != _pristineLoadedSourceText || binding.Range.Source.Text != binding.Asset.RawTextSnapshot ||
            binding.Document.OriginalText != binding.Range.Source.Text ||
            binding.Document.BlockSpan.StartIndex != binding.Range.StartOffset ||
            binding.Document.BlockSpan.EndIndexExclusive != binding.Range.StartOffset + binding.Range.Length ||
            _hasAuthoredSourceChanges || _externalSourceChangesPending)
            return "The scenario source changed. Flag preset drafts are retained.";
        if (IsReadOnly || !_sourceHostSession.CanWrite || _sourceHostSession.Scope != WccEditorSessionScope.Project ||
            !ReferenceEquals(binding.Host, _sourceHostSession) || binding.ProjectIdentity != CurrentWccFieldProjectIdentity ||
            !IsPathInsideRoot(binding.Range.Source.Path, _sourceHostSession.RootPath) || IsProtectedWesnothInstallPath(binding.Range.Source.Path) ||
            !ReferenceEquals(binding.Snapshot, _macroLibrarySnapshot) || binding.RootRevision != _staticRootRevisionSelection?.RootRevisionId ||
            !ReferenceEquals(_staticRootRevisionSelection?.Snapshot, _macroLibrarySnapshot) || !ReferenceEquals(binding.Plan, _sourceEvaluationPlan) ||
            binding.VisibleRevisions != MusicScheduleRevisionSignature(ScenarioContentVisibleRevisions) ||
            binding.MacroVisibleRevisions != MusicScheduleRevisionSignature(_macroVisibleRevisionIds) ||
            !SameOptionalWccInlinePath(binding.InstallPath, _wesnothSourceInstallPath) ||
            !SameOptionalWccInlinePath(binding.AddonsPath, _wesnothSourceAddonsPath) ||
            binding.ProfileIdentity != _wesnothSourceProfileIdentity || binding.TextDomain != GetWccScenarioTextDomain())
            return "The campaign, indexed source or profile binding changed. Flag preset drafts are retained.";
        if (WccFieldEditSession.Recovery is not null || _collectedSaveRecoveryPending)
            return "A previous save requires source recovery. Flag preset drafts are retained.";
        return GetWccInlineBindingProblem();
    }

    internal bool TryCaptureWccSideFlagContribution(out WccSideFlagDraftContribution? contribution, out string? problem)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            contribution = null;
            if (!HasWccSideFlagDrafts || _sideFlagSourceBinding is not { } binding)
            { problem = "There are no pending flag preset changes."; return false; }
            problem = GetSideFlagBindingProblem(binding) ?? CheckSideFlagDefinitions() ?? CheckSideFlagResources();
            if (problem is not null) return false;
            var definitions = _sideFlagDrafts.SelectMany(draft => draft.Definitions).Distinct().ToArray();
            contribution = new(this, binding, _sideFlagDraftVersion, new(binding.Range, definitions, binding.TextDomain,
                sideFlags: _sideFlagDrafts.Select(draft => draft.Recipe)));
            return true;
        }
    }

    internal bool IsCurrentWccSideFlagContribution(WccSideFlagDraftContribution contribution)
    {
        lock (_wccInitialFieldRefreshGate)
            return ReferenceEquals(contribution.Editor, this) && HasWccSideFlagDrafts &&
                ReferenceEquals(contribution.Binding, _sideFlagSourceBinding) && contribution.Version == _sideFlagDraftVersion &&
                _sideFlagSourceBinding is { } binding && GetSideFlagBindingProblem(binding) is null &&
                CheckSideFlagDefinitions() is null && CheckSideFlagResources() is null;
    }

    internal bool CompleteWccSideFlagContribution(WccSideFlagDraftContribution contribution)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!ReferenceEquals(contribution.Editor, this) || !HasWccSideFlagDrafts ||
                !ReferenceEquals(contribution.Binding, _sideFlagSourceBinding) || contribution.Version != _sideFlagDraftVersion) return false;
            _sideFlagSourceBinding = null;
            _sideFlagDrafts = Array.Empty<WccSideFlagPresetDraft>();
            _sideFlagDraftVersion++;
            RaiseSideFlagDraftsChanged();
            return true;
        }
    }

    public void RevertWccSideFlagDraft(string identity)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (IsWccInlineSaveBusy || !_sideFlagDrafts.Any(draft => draft.Identity == identity)) return;
            _sideFlagDrafts = Array.AsReadOnly(_sideFlagDrafts.Where(draft => draft.Identity != identity).ToArray());
            if (!HasWccSideFlagDrafts) _sideFlagSourceBinding = null;
            _sideFlagDraftVersion++;
            RaiseSideFlagDraftsChanged();
        }
    }

    public void DiscardWccSideFlagDrafts()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (IsWccInlineSaveBusy) return;
            _sideFlagDrafts = Array.Empty<WccSideFlagPresetDraft>();
            _sideFlagSourceBinding = null;
            _sideFlagDraftVersion++;
            RaiseSideFlagDraftsChanged();
        }
    }

    internal string GetPendingWccSideFlagChangeStamp() => _sideFlagDraftVersion + "\u001f" +
        string.Join("\u001f", _sideFlagDrafts.Select(draft => draft.Identity + "=" + draft.InvocationText));

    private string? CheckSideFlagDefinitions()
    {
        var fresh = GetWccMusicScheduleDefinitions(FlagPresetMacroNames, fresh: true);
        foreach (var definition in _sideFlagDrafts.SelectMany(draft => draft.Definitions).Distinct())
            if (!fresh.Any(current => current.Name == definition.Name && PathsEqual(current.SourcePath, definition.SourcePath) &&
                current.SourceStartLine == definition.SourceStartLine && current.RawText == definition.RawText && current.TextDomain == definition.TextDomain))
                return "A captured flag definition changed. Flag preset drafts are retained; refresh the source before Save.";
        return null;
    }

    private WccSideFlagResourceBinding? CaptureSideFlagResources(CatalogPickerOptionVm option, string macroName)
    {
        if (option.PreviewPath is not { } icon || !icon.EndsWith("-flag-icon.png", StringComparison.OrdinalIgnoreCase)) return null;
        var prefix = icon[..^"-flag-icon.png".Length];
        var paths = Enumerable.Range(1, macroName == "FLAG_VARIANT6" ? 6 : 4)
            .Select(index => prefix + "-flag-" + index + ".png").Prepend(icon);
        try
        {
            return new(option.ToSelection(), icon, option.ResourceSourceLabel ?? "",
                Array.AsReadOnly(paths.Select(path => (path, SideFlagResourceHash(path))).ToArray()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private string? CheckSideFlagResources()
    {
        var options = GetWccSideFlagPresetOptions();
        foreach (var draft in _sideFlagDrafts)
        {
            var resource = draft.Resources;
            if (!IsCurrentSideFlagResource(resource, options))
                return "The selected flag image family changed, disappeared or its resource origin changed. Flag preset drafts are retained; reopen the picker.";
        }
        return null;
    }

    private static bool IsCurrentSideFlagResource(WccSideFlagResourceBinding resource, IReadOnlyList<CatalogPickerOptionVm> options)
    {
        if (!options.Any(option => option.ToSelection() == resource.Selection &&
                PathsEqual(option.PreviewPath, resource.IconPath) && (option.ResourceSourceLabel ?? "") == resource.Origin)) return false;
        try { return resource.Files.All(file => SideFlagResourceHash(file.Path) == file.Hash); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string SideFlagResourceHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private WccSideFlagPresetDraft? GetEffectiveSideFlagDraft(WmlStaticScalarOwner owner)
    {
        if (_sideFlagSourceBinding is not { } binding || GetSideFlagBindingProblem(binding) is not null) return null;
        var draft = _sideFlagDrafts.FirstOrDefault(draft => SameSideFlagOwner(draft.Recipe.SideOwner, owner));
        if (draft is null) return null;
        if (draft.Plan.BaselineInvocation is { } old)
        {
            var current = WccSideFlagPresetSourcePlanner.FindWinningInvocation(owner, binding.Range.Source);
            return current is not null && current.StartOffset == old.StartOffset && current.Length == old.Length ? draft : null;
        }
        return owner.ResolveField("flag").State == WmlStaticScalarResolutionState.ProvenAbsent &&
            owner.ResolveField("flag_icon").State == WmlStaticScalarResolutionState.ProvenAbsent ? draft : null;
    }

    internal bool TryGetWccSideFlagDraftValue(WmlStaticScalarOwner owner, string key, out string value)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            value = string.Empty;
            if (key is not ("flag" or "flag_icon") || !IsCurrentWccInitialFieldOwner(owner) ||
                GetEffectiveSideFlagDraft(owner) is not { } draft ||
                !WccSideFlagPresetSourcePlanner.TryGetValues(draft.InvocationText, out var flag, out var icon)) return false;
            value = key == "flag" ? flag : icon;
            return true;
        }
    }

    private static bool SameSideFlagOwner(WmlStaticScalarOwner left, WmlStaticScalarOwner right) => left.TagName == "side" &&
        right.TagName == "side" && left.OpeningOrigin is { } a && right.OpeningOrigin is { } b &&
        SameAuthoredFlagSource(a.Source, b.Source) && a.StartOffset == b.StartOffset && a.Length == b.Length;
    private static bool SameAuthoredFlagSource(WmlStaticSource left, WmlStaticSource right) => left.IsAuthored && right.IsAuthored &&
        string.Equals(left.Path, right.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        left.ContentHash == right.ContentHash;
    private static string SideFlagOwnerIdentity(WmlStaticScalarOwner owner) => owner.OpeningOrigin!.Source.Path + "\u001f" +
        owner.OpeningOrigin.Source.ContentHash + "\u001f" + owner.OpeningOrigin.StartOffset;

    private void RaiseSideFlagDraftsChanged()
    {
        CollectedSaveAuthorityChanging?.Invoke();
        OnPropertyChanged(nameof(HasWccSideFlagDrafts));
        OnPropertyChanged(nameof(WccSideFlagDrafts));
        OnPropertyChanged(nameof(WccSideFlagPresetInvocation));
        OnPropertyChanged(nameof(WccSideFlagPresetGuidance));
        OnPropertyChanged(nameof(CanPickWccSideFlagPreset));
        OnPropertyChanged(nameof(CanEditDocument));
        WccSideFlagDraftsChanged?.Invoke(this, EventArgs.Empty);
    }
}
