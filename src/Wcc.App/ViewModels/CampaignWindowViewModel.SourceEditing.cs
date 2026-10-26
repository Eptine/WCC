using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public sealed record CampaignDifficultyVm(string Label, string Define, bool IsDefault,
    CampaignFieldSourceLocation? SourceLocation, CampaignSourceEntry? RawEntry, bool CanEditRaw)
{
    public string Description { get; init; } = "";
    public string Image { get; init; } = "";
    public string DisplayName => Label + (string.IsNullOrEmpty(Define) ? "" : " (" + Define + ")") +
        (IsDefault ? " — default" : "");
    public bool HasSource => SourceLocation is not null;
}

/// <summary>One manual source callback bound to the exact current authored main file.</summary>
public sealed class CampaignMainRawEditDraft
{
    internal CampaignMainRawEditDraft(WccProject project, CampaignDocument document, string sourcePath,
        string sourceText, long generation, int start, int length)
    { Project = project; Document = document; SourcePath = sourcePath; SourceText = sourceText;
        Generation = generation; Start = start; Length = length; }
    internal WccProject Project { get; }
    internal CampaignDocument Document { get; }
    internal string SourceText { get; }
    internal long Generation { get; }
    internal int Start { get; }
    internal int Length { get; }
    public string SourcePath { get; }
    public string Text => SourceText.Substring(Start, Length);
    internal string Candidate(string text) => SourceText[..Start] + text + SourceText[(Start + Length)..];
}

public sealed class CampaignSourceExportSnapshot
{
    internal CampaignSourceExportSnapshot(WccProject project, CampaignDocument document, long generation, string sourceText)
    { Project = project; Document = document; Generation = generation; SourceText = sourceText; }
    internal WccProject Project { get; }
    internal CampaignDocument Document { get; }
    internal long Generation { get; }
    public string SourceText { get; }
}

public partial class CampaignWindowViewModel
{
    private string? _campaignInventorySource;
    private IReadOnlyList<CampaignSourceEntry> _campaignSourceInventory = [];
    private IReadOnlyList<CampaignSourceEntry> _campaignNavigationInventory = [];
    private CampaignSourceEntry? _selectedCampaignCode;
    public bool HasCampaignMainSource => GetCurrentMainSourceRawWml() is not null;
    public bool CanEditCampaignMainRaw => _project?.Campaign is not null && !RequiresSelectedCampaignAdapter && !IsReadOnly &&
        _campaignFieldConfiguration?.Session.CanWrite == true &&
        _campaignFieldConfiguration.Session.Scope == global::Wcc.App.ViewModels.Wcc.WccEditorSessionScope.Project &&
        !HasCampaignSourceDrafts && !IsCampaignFieldSaveBusy && _campaignFieldSession.Recovery is null &&
        !string.IsNullOrWhiteSpace(_project?.SourcePath) && IsPathInsideCampaignRoot(_project.SourcePath);
    public string CampaignSourceGuidance => HasCampaignSourceDrafts || IsCampaignFieldSaveBusy
        ? "Save or discard campaign field drafts before changing source code. Source remains available for inspection."
        : IsReadOnly ? "This main file is open read-only. Source code remains available for inspection."
        : "RAW changes stay in the working source until Save. Definitions, includes and conditional code keep their exact file positions.";
    public CampaignSourceEntry? SelectedCampaignAdditionalCode
    {
        get => _selectedCampaignCode;
        set => SetField(ref _selectedCampaignCode, value);
    }
    private IReadOnlyList<CampaignSourceEntry> CampaignSourceEntries
    {
        get
        {
            var source = GetCurrentMainSourceRawWml();
            if (!string.Equals(source, _campaignInventorySource, StringComparison.Ordinal))
            {
                _campaignInventorySource = source;
                _campaignSourceInventory = source is null ? [] : CampaignSourceInventory.Build(source);
                _campaignNavigationInventory = source is null ? [] : CampaignSourceInventory.BuildForNavigation(source);
                _selectedCampaignCode = null;
            }
            return _campaignNavigationInventory;
        }
    }
    public IReadOnlyList<CampaignSourceEntry> CampaignDifficultyEntries => CampaignSourceEntries
        .Where(entry => entry.Kind == CampaignSourceKind.CampaignChildTag && entry.Name is "difficulty" or "+difficulty").ToArray();
    public IReadOnlyList<CampaignSourceEntry> CampaignAdditionalCodeEntries
    {
        get
        {
            _ = CampaignSourceEntries;
            return _campaignSourceInventory.Where(entry => !(entry.Kind == CampaignSourceKind.CampaignChildTag &&
                entry.Name is "difficulty" or "+difficulty")).ToArray();
        }
    }

    public IReadOnlyList<CampaignDifficultyVm> CampaignDifficulties
    {
        get
        {
            lock (_campaignFieldGate)
            {
                if (_campaignMenuOwner is not { } campaign || _campaignProjection?.Source is not { } source ||
                    !IsCurrentCampaignMenuSource(source)) return [];
                var owners = campaign.DirectChildren.Where(owner => owner.TagName == "difficulty").ToArray();
                if (owners.Length == 0 && EffectiveCampaignPlan is { } plan &&
                    (_campaignProjection.EvaluationEntrySource ?? source) is { } root && plan.CampaignMainSourcePath == root.Path && plan.CampaignMainTextHash == root.ContentHash)
                {
                    var location = GetCampaignFieldSourceLocation(campaign, "difficulties");
                    var entry = CampaignSourceEntries.FirstOrDefault(entry => entry.Kind == CampaignSourceKind.RootTag &&
                        entry.Name == "campaign" && entry.StartOffset == _project?.Campaign?.CampaignBlockSpan.StartIndex);
                    var hasDefault = plan.Difficulties.Any(difficulty => difficulty.IsDefault);
                    var contextIsProved = _selectedCampaignEntry?.IsContextComplete == true ||
                        CampaignInitialFieldProjector.CheckContextPlan(_campaignProjection, campaign, plan) is null;
                    return plan.Difficulties.Select((difficulty, index) => new CampaignDifficultyVm(difficulty.Define,
                        difficulty.Define, contextIsProved && (difficulty.IsDefault || !hasDefault && index == 0),
                        location, entry, CanEditCampaignMainRaw)).ToArray();
                }
                var canDetermineDefault = _campaignProjection.IsComplete &&
                    owners.All(owner => owner.ResolveField("default").State != WmlStaticScalarResolutionState.Unresolved);
                var defaultIndex = canDetermineDefault ? Array.FindLastIndex(owners,
                    owner => Literal(owner.GetKnownValue("default")) is "yes" or "true") : -1;
                if (canDetermineDefault && defaultIndex < 0 && owners.Length != 0) defaultIndex = 0;
                return owners.Select((owner, index) =>
                {
                    var define = Literal(owner.GetKnownValue("define")) ?? "Unresolved define";
                    var label = Literal(owner.GetKnownValue("label")) ?? Literal(owner.GetKnownValue("description")) ?? define;
                    var range = owner.OpeningExpansionStack.Reverse().SelectMany(frame => frame.InvocationOrigins)
                        .FirstOrDefault(origin => origin.Source.Path == source.Path && origin.Source.ContentHash == source.ContentHash)
                        ?? owner.OpeningOrigin;
                    var entry = range is null ? null : CampaignSourceEntries.FirstOrDefault(entry =>
                        entry.StartOffset <= range.StartOffset && entry.EndOffsetExclusive >= range.StartOffset + range.Length &&
                        entry.Kind is CampaignSourceKind.CampaignChildTag or CampaignSourceKind.MacroInvocation);
                    var location = range is null ? null : new CampaignFieldSourceLocation(range.Source.Path, range.Source.Text,
                        range.StartOffset, range.Length, "Captured difficulty source. Difficulty topology is edited in RAW in this slice.");
                    return new CampaignDifficultyVm(label, define, index == defaultIndex, location, entry, CanEditCampaignMainRaw)
                    {
                        Description = Literal(owner.GetKnownValue("description")) ?? "",
                        // The shared inventory observes bounded literal resource
                        // concatenation without changing strict scalar knownness.
                        Image = CampaignStructureInventory?.Snapshot.DifficultyRows is { } rows &&
                            index < rows.Count && rows[index].Define == define
                                ? rows[index].Image : Literal(owner.GetKnownValue("image")) ?? ""
                    };
                }).ToArray();

                static string? Literal(string? value) => value is null ? null : WccScenarioFieldValueCodec.DecodeLiteral(value);
            }
        }
    }

    // Reading source never hydrates or writes a model, starts a save or cancels previews.
    public string? GetCurrentMainSourceRawWml() => _project?.Campaign?.OriginalText ?? _project?.CampaignRawTextSnapshot;

    public CampaignMainRawEditDraft? BeginCampaignMainRawEdit()
    {
        lock (_campaignFieldGate)
        {
            if (!CanEditCampaignMainRaw || _project?.Campaign is not { } document || _project.SourcePath is not { } path) return null;
            return new(_project, document, path, document.OriginalText, _campaignFieldGeneration, 0, document.OriginalText.Length);
        }
    }
    public CampaignMainRawEditDraft? BeginCampaignSourceEntryRawEdit(CampaignSourceEntry entry)
    {
        lock (_campaignFieldGate)
        {
            var navigation = CampaignSourceEntries; // Refresh both exact inventories before checking identity.
            if (!(navigation.Any(current => ReferenceEquals(entry, current)) ||
                  _campaignSourceInventory.Any(current => ReferenceEquals(entry, current))) || BeginCampaignMainRawEdit() is not { } main ||
                entry.StartOffset < 0 || entry.EndOffsetExclusive > main.SourceText.Length || entry.EndOffsetExclusive < entry.StartOffset ||
                main.SourceText[entry.StartOffset..entry.EndOffsetExclusive] != entry.Text) return null;
            return new(main.Project, main.Document, main.SourcePath, main.SourceText, main.Generation, entry.StartOffset,
                entry.EndOffsetExclusive - entry.StartOffset);
        }
    }
    public string? ValidateCampaignMainRawEdit(CampaignMainRawEditDraft draft, string text)
    {
        lock (_campaignFieldGate)
        {
            if (!IsCurrentCampaignMainRawDraft(draft)) return "The source or editing context changed. Reopen Main file RAW.";
            var candidate = draft.Candidate(text);
            var validation = WmlSingleBlockValidator.ValidateSourceStructure(candidate);
            if (!validation.IsValid) return validation.Problem;
            try
            {
                var before = WmlBlockScanner.FindTopLevelBlockSpans(draft.SourceText).Count(span => span.TagName.Equals("campaign", StringComparison.OrdinalIgnoreCase));
                var after = WmlBlockScanner.FindTopLevelBlockSpans(candidate).Count(span => span.TagName.Equals("campaign", StringComparison.OrdinalIgnoreCase));
                if (before != after) return "The RAW change must preserve the main file's campaign blocks.";
                CampaignImporter.ImportFromText(candidate, draft.SourcePath);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            { return "The edited main file could not be imported: " + exception.Message; }
            return null;
        }
    }
    public bool ApplyCampaignMainRawEdit(CampaignMainRawEditDraft draft, string text)
    {
        lock (_campaignFieldGate)
        {
            if (ValidateCampaignMainRawEdit(draft, text) is { } problem) { Status = problem; return false; }
            if (text == draft.Text) return true;
            var document = CampaignImporter.ImportFromText(draft.Candidate(text), draft.SourcePath);
            SourceChanging?.Invoke();
            if (!IsCurrentCampaignMainRawDraft(draft)) { Status = "The source or editing context changed before applying RAW code."; return false; }
            RevokeCampaignFieldAuthority();
            _project!.Campaign = document;
            _campaignHasRawSourceChanges = true;
            _campaignProjection = null;
            _campaignMenuOwner = null;
            _campaignProjectionDocument = null;
            _campaignRoutes.Clear();
            _campaignFields.Clear();
            RunUiSync(RefreshLegacyCampaignFields);
            NotifyDocumentChanged();
            _campaignFieldStatus = "Save source changes to resolve and edit campaign fields.";
            Status = "Main file source changed in the working draft. Save writes the file.";
            RaiseCampaignFieldsChanged();
            return true;
        }
    }
    private bool IsCurrentCampaignMainRawDraft(CampaignMainRawEditDraft draft) => CanEditCampaignMainRaw &&
        draft.Generation == _campaignFieldGeneration && ReferenceEquals(draft.Project, _project) &&
        ReferenceEquals(draft.Document, _project?.Campaign) && string.Equals(draft.SourcePath, _project?.SourcePath, StringComparison.Ordinal) &&
        draft.SourceText == GetCurrentMainSourceRawWml();

    private bool BeforeChangingCampaignSource(Action continuation)
    {
        if (CampaignSourcePublicationGuard?.Invoke() == false) return false;
        if (!HasCampaignSourceDrafts && !IsCampaignFieldSaveBusy && !_campaignHasRawSourceChanges) return true;
        if (BeforeCampaignSourceNavigation is { } before) return before(continuation);
        Status = "Save or discard pending campaign changes before opening another source.";
        return false;
    }
    public bool CanExportCurrentCampaignSource()
    {
        if (HasCampaignSourceDrafts || IsCampaignFieldSaveBusy)
        { Status = "Save or discard campaign field drafts before Save As or export."; return false; }
        return _project?.Campaign is not null;
    }
    public CampaignSourceExportSnapshot? CaptureCampaignSourceExport()
    {
        lock (_campaignFieldGate)
        {
            if (!CanExportCurrentCampaignSource() || _project?.Campaign is not { } document || RenderPatchedMainFile() is not { } text) return null;
            return new(_project, document, _campaignFieldGeneration, text);
        }
    }
    public bool IsCurrentCampaignSourceExport(CampaignSourceExportSnapshot snapshot)
    {
        lock (_campaignFieldGate)
        {
            if (snapshot.Generation == _campaignFieldGeneration && ReferenceEquals(snapshot.Project, _project) &&
                ReferenceEquals(snapshot.Document, _project?.Campaign) && CanExportCurrentCampaignSource() &&
                RenderPatchedMainFile() == snapshot.SourceText) return true;
            Status = "The campaign source or context changed. Reopen Save As or export.";
            return false;
        }
    }
}
