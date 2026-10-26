using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wcc.Core.Library;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private bool _hasStaticScalarProjection;
    private WmlStaticScalarOwner? _staticScenarioOwner;
    private WmlStaticScalarProjection? _staticScenarioTopologyProjection;
    private ScenarioDocument? _staticScalarScenario;
    private string? _staticScalarContextKey;
    private readonly Dictionary<ScenarioSideDocument, WmlStaticScalarOwner> _staticSideOwners = new();
    private PreprocessedScalarValues? _preprocessedScalarValues;
    private ExactInitialScalarValues? _exactInitialScalarValues;

    // Resolved values remain separate from authored models. Proven local routes
    // turn user input into inline proposals for one collected checked Save.
    public string? ScenarioTurnsDisplay
    {
        get => WccInlineDisplay("turns", ScenarioTurnsField.Value);
        set { if (!ScenarioTurnsDisplayIsReadOnly && !TrySetWccInlineValue("turns", value)) ScenarioTurns = value; }
    }
    public string? SideGoldDisplay
    {
        get => WccInlineDisplay("gold", SideGoldField.Value);
        set { if (!SideGoldDisplayIsReadOnly && !TrySetWccInlineValue("gold", value)) SideGold = value; }
    }
    public string? SideNumberDisplay
    {
        get => SideNumberField.Value;
        set { if (!SideNumberDisplayIsReadOnly) SideNumber = value; }
    }
    public string? SideIncomeDisplay
    {
        get => WccInlineDisplay("income", SideIncomeField.Value);
        set { if (!SideIncomeDisplayIsReadOnly && !TrySetWccInlineValue("income", value)) SideIncome = value; }
    }
    public bool ScenarioTurnsDisplayIsReadOnly => IsWccInlineSaveBusy ||
        !(CanReviewWccInitialField("turns") || !HasWccInlineDrafts && ScenarioTurnsField.CanEditAuthoredValue);
    public bool SideNumberDisplayIsReadOnly => !SideNumberField.CanEditAuthoredValue;
    public bool SideGoldDisplayIsReadOnly => IsWccInlineSaveBusy ||
        !(CanReviewWccInitialField("gold") || !HasWccInlineDrafts && SideGoldField.CanEditAuthoredValue);
    public bool SideIncomeDisplayIsReadOnly => IsWccInlineSaveBusy ||
        !(CanReviewWccInitialField("income") || !HasWccInlineDrafts && SideIncomeField.CanEditAuthoredValue);
    public string ScenarioTurnsDisplayStatus => WccInlineFieldStatus("turns", ScenarioTurnsField.Status);
    public string SideNumberDisplayStatus => SideNumberField.Status;
    public string SideGoldDisplayStatus => WccInlineFieldStatus("gold", SideGoldField.Status);
    public string SideIncomeDisplayStatus => WccInlineFieldStatus("income", SideIncomeField.Status);
    // Resolver evidence and the existing authored draft gate remain separate:
    // a dirty draft may be editable without a known effective initial value.
    public WmlFieldPresentation ScenarioTurnsPresentation => ScenarioTurnsField.Presentation;
    public WmlFieldPresentation SideNumberPresentation => SideNumberField.Presentation;
    public WmlFieldPresentation SideGoldPresentation => SideGoldField.Presentation;
    public WmlFieldPresentation SideIncomePresentation => SideIncomeField.Presentation;
    public bool ScenarioTurnsHasSource => SelectedScenario is not null;
    public bool SideNumberHasSource => SelectedSide is not null;
    public bool SideGoldHasSource => SelectedSide is not null;
    public bool SideIncomeHasSource => SelectedSide is not null;

    private InitialScalarField ScenarioTurnsField => BuildInitialScalarField(
        HasCurrentStaticScalarProjection ? _staticScenarioOwner : null,
        "turns", ScenarioScalarSourceField.Turns, ScenarioTurns,
        ScenarioTurnsIsEditable, SelectedScenario is not null);
    private InitialScalarField SideGoldField => BuildInitialScalarField(
        SelectedStaticSideOwner, "gold", ScenarioScalarSourceField.SideGold, SideGold,
        SideGoldIsEditable, SelectedSide is not null);
    private InitialScalarField SideNumberField => BuildInitialScalarField(
        SelectedStaticSideOwner, "side", null, SideNumber,
        SideNumberIsEditable, SelectedSide is not null);
    private InitialScalarField SideIncomeField => BuildInitialScalarField(
        SelectedStaticSideOwner, "income", ScenarioScalarSourceField.SideIncome, SideIncome,
        SideIncomeIsEditable, SelectedSide is not null);
    private bool HasCurrentStaticScalarProjection => _hasStaticScalarProjection &&
        ReferenceEquals(_staticScalarScenario, SelectedScenario?.Document) &&
        string.Equals(_staticScalarContextKey, _evaluationContext.SemanticKey, StringComparison.Ordinal);
    private WmlStaticScalarOwner? SelectedStaticSideOwner => HasCurrentStaticScalarProjection && SelectedSide is { } side
        ? side.AuthoredDocument is { } document ? _staticSideOwners.GetValueOrDefault(document) :
            Sides.Contains(side) && side.EffectiveOwner is { } owner &&
            ReferenceEquals(owner.Parent, _staticScenarioOwner) && owner.OpeningOutputOffset is not null
                ? owner : null : null;

    private InitialScalarField BuildInitialScalarField(WmlStaticScalarOwner? owner, string key,
        ScenarioScalarSourceField? field,
        string? authoredValue, bool canEditAuthored, bool hasSourceOwner)
    {
        var resolution = owner?.ResolveField(key);
        var knownInteger = resolution?.State == WmlStaticScalarResolutionState.Known
            ? NormalizeInitialInteger(resolution.ValueText) : null;
        var exactEngineValue = GetExactInitialScalarValue(key, field == ScenarioScalarSourceField.Turns
            ? null : SelectedSide?.AuthoredDocument);
        var engineConflict = exactEngineValue is not null &&
            (resolution?.State == WmlStaticScalarResolutionState.ProvenAbsent ||
             resolution?.State == WmlStaticScalarResolutionState.Known &&
             !string.Equals(knownInteger ?? (field == ScenarioScalarSourceField.Turns &&
                 NormalizeInitialScalarLiteral(resolution.ValueText) == "unlimited" ? "unlimited" : null),
                 exactEngineValue, StringComparison.Ordinal));
        var authority = WmlFieldValueAuthority.Unknown;
        string? value = null;
        var canEdit = false;
        string status;
        if (_hasAuthoredSourceChanges || _externalSourceChangesPending)
        {
            value = authoredValue;
            // Dirtying one field cannot turn another missing field into an
            // insertion route. Retain an existing plain source field even if
            // the user temporarily clears its draft while typing.
            var states = field == ScenarioScalarSourceField.Turns
                ? SelectedScenario?.Document.Scenario.FieldStates : SelectedSide?.AuthoredDocument?.Side.FieldStates;
            canEdit = canEditAuthored && !_externalSourceChangesPending &&
                states?.GetValueOrDefault(key)?.Kind == WmlFieldSafetyKind.PlainEditable;
            status = _externalSourceChangesPending
                ? "Authored working value; external source changes are pending. Save or reload to resolve the initial value."
                : "Authored working value. Save or reload source changes to resolve the initial value.";
        }
        else if (knownInteger is not null)
        {
            value = knownInteger;
            authority = WmlFieldValueAuthority.Static;
            // An independently resolved value is not permission to change an
            // argument, shared definition, or a different authored literal.
            canEdit = canEditAuthored && SameInteger(value, authoredValue);
            status = "Initial value resolved by WCC for the selected context.";
        }
        else if (resolution?.State == WmlStaticScalarResolutionState.Known &&
                 field == ScenarioScalarSourceField.Turns &&
                 string.Equals(NormalizeInitialScalarLiteral(resolution.ValueText), "unlimited", StringComparison.Ordinal))
        {
            // Scenario turns also supports this literal in Wesnoth's schema.
            // Keep the token visible without feeding it into the integer draft.
            value = "unlimited";
            authority = WmlFieldValueAuthority.Static;
            status = "Initial value resolved by WCC for the selected context.";
        }
        else if (resolution?.State == WmlStaticScalarResolutionState.Known)
        {
            // WCC has resolved the source value, but this numeric field cannot
            // represent it. An engine integer must not replace that evidence.
            status = "WCC resolved a source value that is not a supported integer. Use source editing to inspect it.";
        }
        else if (resolution?.State == WmlStaticScalarResolutionState.ProvenAbsent)
        {
            authority = WmlFieldValueAuthority.ProvenAbsent;
            status = "No explicit value in this owner for the selected context. Source editing is available; defaults are not shown.";
        }
        else if (exactEngineValue is not null)
        {
            value = exactEngineValue;
            authority = WmlFieldValueAuthority.PreprocessedInitial;
            status = "Initial value resolved by Wesnoth for this exact source owner and selected context. Source editing is available.";
        }
        else if (!HasCurrentStaticScalarProjection)
        {
            value = authoredValue;
            // Preserve existing direct authored edits while evaluation is
            // unavailable. A blank unresolved field cannot authorize insertion.
            canEdit = canEditAuthored && authoredValue is not null;
            status = value is null
                ? "Initial value unresolved. Use source editing; no absence has been established."
                : "Authored value; its initial value has not been resolved yet.";
        }
        else
            status = "Initial value unresolved. Use source editing; no absence has been established.";

        if (engineConflict)
        {
            canEdit = false;
            status += " Conflict: WCC and Wesnoth disagree on this initial value (Wesnoth: " +
                exactEngineValue + "). WCC's result is retained; use source editing to inspect the difference.";
        }

        if (authority == WmlFieldValueAuthority.Unknown &&
            resolution?.State != WmlStaticScalarResolutionState.Known && HasCurrentPreprocessedScalarValues)
            status += " Wesnoth's preview is available separately; its values cannot yet be bound to this exact source owner.";

        if (key != "turns" && SelectedSide?.IsGenerated == true)
        {
            canEdit = false;
            status += " This side comes from source expansion; displayed values are not authored save inputs. Use its field action for a supported local edit or source access.";
        }

        var setAuthority = canEdit && hasSourceOwner
            ? WmlFieldOperationAuthority.Authorized
            : WmlFieldOperationAuthority.Unsupported;
        var evidence = new WmlFieldEvidence
        {
            ValueAuthority = authority,
            AuthoredPresence = authoredValue is not null
                ? WmlFieldAuthoredPresence.Present : WmlFieldAuthoredPresence.Unknown,
            Provenance = authority == WmlFieldValueAuthority.Static && resolution!.Origins.Count > 0
                ? WmlFieldSourceProvenance.Contribution
                : hasSourceOwner ? WmlFieldSourceProvenance.Owner : WmlFieldSourceProvenance.None,
            HasSourceLocation = hasSourceOwner,
            CollectionCompleteness = WmlFieldCollectionCompleteness.NotApplicable,
            SessionAccess = WccFieldEditSession.Recovery is not null ? WmlFieldSessionAccess.RecoveryRequired :
                IsReadOnly || key != "turns" && SelectedSide?.IsGenerated == true
                    ? WmlFieldSessionAccess.ReadOnly : WmlFieldSessionAccess.Editable,
            Operations = new Dictionary<WmlFieldOperation, WmlFieldOperationAuthority>
            { [WmlFieldOperation.Set] = setAuthority }
        };
        return new(value, canEdit && hasSourceOwner, status,
            WmlFieldPresentationDeriver.Derive(evidence));
    }

    private static bool SameInteger(string? left, string? right) =>
        int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leftValue) &&
        int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rightValue) &&
        leftValue == rightValue;

    private static string? NormalizeInitialInteger(string? raw)
    {
        return int.TryParse(NormalizeInitialScalarLiteral(raw), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static string? NormalizeInitialScalarLiteral(string? raw)
    {
        // These scalar tokens are not translated display text. Decode WML
        // delimiters only; stripping '_' or '^' could manufacture a known value.
        var value = raw?.Trim();
        if (value is null)
            return null;
        if (value.Length >= 4 && value.StartsWith("<<", StringComparison.Ordinal) &&
            value.EndsWith(">>", StringComparison.Ordinal) &&
            value.IndexOf(">>", 2, StringComparison.Ordinal) == value.Length - 2)
            return value[2..^2].Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal).Trim();
        return value;
    }

    private bool HasCurrentPreprocessedScalarValues
    {
        get
        {
            var values = _preprocessedScalarValues;
            // The normalized preview and its navigation breadcrumbs are separate
            // from exact opening-bound display evidence.
            return values is not null && !_hasAuthoredSourceChanges && !_externalSourceChangesPending &&
                IsCurrentExactPreview(values.Generation, values.AuthoredScenario, _evaluationContext) &&
                string.Equals(values.ContextKey, _evaluationContext.SemanticKey, StringComparison.Ordinal);
        }
    }

    private void PublishPreprocessedScalarValues(ScenarioDocument authoredScenario,
        ScenarioDocument effectiveScenario, IReadOnlyList<ScenarioScalarSourceRoute> routes,
        WmlEvaluationContext context, int generation)
    {
        if (!IsCurrentExactPreview(generation, authoredScenario, context))
            return;
        _preprocessedScalarValues = new(authoredScenario, effectiveScenario, routes, context.SemanticKey, generation);
        RaiseInitialScalarFieldsChanged();
    }

    private void ClearInitialScalarFields()
    {
        _hasStaticScalarProjection = false;
        _staticScenarioOwner = null;
        _staticScenarioTopologyProjection = null;
        // These derived capabilities own the same projection and must retire with it.
        _initialUnitSnapshotCache = null;
        _leaderAdditionCapability = null;
        _staticScalarScenario = null;
        _staticScalarContextKey = null;
        _staticSideOwners.Clear();
        _preprocessedScalarValues = null;
        _exactInitialScalarValues = null;
        RaiseInitialScalarFieldsChanged();
    }

    private void RaiseInitialScalarFieldsChanged()
        => RequestFieldDisplayRefresh(full: true);

    private void RaiseInitialScalarFieldsChangedCore()
    {
        RaiseWccSideTopologyCapabilitiesChanged();
        RaiseWccStoryCapabilitiesChanged();
        RefreshWccInitialUnitRows();
        RefreshWccInitialLeaders();
        RefreshWccInitialFieldEditorsNow();
        RefreshInitialSideNumbers();
        foreach (var property in new[]
        {
            nameof(ScenarioTurnsDisplay), nameof(SideGoldDisplay), nameof(SideIncomeDisplay),
            nameof(ScenarioTurnsDisplayIsReadOnly), nameof(SideGoldDisplayIsReadOnly), nameof(SideIncomeDisplayIsReadOnly),
            nameof(ScenarioTurnsDisplayStatus), nameof(SideGoldDisplayStatus), nameof(SideIncomeDisplayStatus),
            nameof(ScenarioTurnsPresentation), nameof(SideGoldPresentation), nameof(SideIncomePresentation),
            nameof(ScenarioTurnsHasSource), nameof(SideGoldHasSource), nameof(SideIncomeHasSource),
            nameof(SideNumberDisplay), nameof(SideNumberDisplayIsReadOnly), nameof(SideNumberDisplayStatus),
            nameof(SideNumberPresentation), nameof(SideNumberHasSource)
        })
            OnPropertyChanged(property);
        RaiseWccInitialFieldPresentationChanged();
    }

    private sealed record InitialScalarField(string? Value, bool CanEditAuthoredValue,
        string Status, WmlFieldPresentation Presentation);
    private sealed record PreprocessedScalarValues(ScenarioDocument AuthoredScenario,
        ScenarioDocument EffectiveScenario, IReadOnlyList<ScenarioScalarSourceRoute> Routes,
        string ContextKey, int Generation);

    private ExactInitialScalarRequest? CaptureExactInitialScalarRequest(string sourceText,
        ScenarioDocument authoredScenario, WmlEvaluationContext context, int generation)
    {
        var sourcePath = _currentSourcePath ?? LoadedPath;
        if (_hasAuthoredSourceChanges || _externalSourceChangesPending ||
            !IsCurrentExactPreview(generation, authoredScenario, context) ||
            string.IsNullOrWhiteSpace(sourcePath) ||
            !string.Equals(sourceText, _pristineLoadedSourceText, StringComparison.Ordinal) ||
            _macroLibrarySnapshot is not { } snapshot ||
            _exactPreprocessVisibleRevisionIds is not { } revisions)
            return null;
        var sourceHash = WccContentHash.ComputeText(sourceText);
        var sourceRevisions = snapshot.SourceFilesById.Values.Where(file =>
                revisions.Contains(file.PackageRevisionId) && PathsEqual(file.StoragePath, sourcePath) &&
                string.Equals(file.ContentHash, sourceHash, StringComparison.OrdinalIgnoreCase))
            .Select(file => file.PackageRevisionId).Distinct().Take(2).ToArray();
        if (sourceRevisions.Length != 1 ||
            _staticRootRevisionSelection is { } root &&
            (!ReferenceEquals(root.Snapshot, snapshot) || root.RootRevisionId != sourceRevisions[0]))
            return null;
        if (_sourceEvaluationPlan is { } plan && !snapshot.SourceFilesById.Values.Any(file =>
                file.PackageRevisionId == sourceRevisions[0] &&
                PathsEqual(file.StoragePath, plan.CampaignMainSourcePath) &&
                string.Equals(file.ContentHash, plan.CampaignMainTextHash, StringComparison.OrdinalIgnoreCase)))
            return null;
        return new(sourcePath, sourceText, sourceHash, authoredScenario, context, generation,
            _sourceHostGeneration, snapshot, revisions, _staticRootRevisionSelection, sourceRevisions[0],
            _sourceEvaluationPlan, _wesnothInstallPath, _wesnothAddonsPath, _wesnothRuntimeProfileIdentity);
    }

    private bool IsCurrentExactInitialScalarRequest(ExactInitialScalarRequest request) =>
        !_hasAuthoredSourceChanges && !_externalSourceChangesPending &&
        IsCurrentExactPreview(request.Generation, request.AuthoredScenario, request.Context) &&
        request.HostGeneration == _sourceHostGeneration &&
        string.Equals(request.Context.SemanticKey, _evaluationContext.SemanticKey, StringComparison.Ordinal) &&
        ReferenceEquals(request.Snapshot, _macroLibrarySnapshot) &&
        ReferenceEquals(request.VisibleRevisions, _exactPreprocessVisibleRevisionIds) &&
        Equals(request.RootSelection, _staticRootRevisionSelection) &&
        ReferenceEquals(request.Plan, _sourceEvaluationPlan) &&
        string.Equals(request.InstallPath, _wesnothInstallPath, PathComparison) &&
        string.Equals(request.AddonsPath, _wesnothAddonsPath, PathComparison) &&
        string.Equals(request.ProfileIdentity, _wesnothRuntimeProfileIdentity, StringComparison.Ordinal) &&
        PathsEqual(request.SourcePath, _currentSourcePath ?? LoadedPath) &&
        string.Equals(request.SourceText, _pristineLoadedSourceText, StringComparison.Ordinal);

    private void PublishExactInitialScalarValues(ExactInitialScalarRequest? request,
        WesnothPreprocessResult result, WccPackageRevisionId? sourceRevisionId)
    {
        if (request is null || !IsCurrentExactInitialScalarRequest(request))
            return;
        _exactInitialScalarValues = null;
        if (sourceRevisionId == request.SourceRevisionId && result.IsSuccess &&
            !string.IsNullOrWhiteSpace(result.NormalizedWml) &&
            result.VerifiedDependencyFingerprint is { Length: 64 } fingerprint &&
            fingerprint.All(Uri.IsHexDigit) && result.ScalarOwners is { } owners)
        {
            var scenario = FindExactInitialScalarOwner(owners, "scenario", request,
                request.AuthoredScenario.BlockSpan.StartIndex);
            if (scenario is not null)
            {
                var sides = new Dictionary<ScenarioSideDocument, IReadOnlyDictionary<string, string>>();
                foreach (var side in Sides.Where(side => side.AuthoredDocument is not null))
                {
                    var offset = FindAuthoredSideOpeningOffset(request.SourceText, request.AuthoredScenario, side.Document);
                    var effective = offset is { } start
                        ? FindExactInitialScalarOwner(scenario.Children, "side", request, start) : null;
                    if (effective is not null)
                        sides[side.Document] = new Dictionary<string, string>(effective.Scalars, StringComparer.Ordinal);
                }
                _exactInitialScalarValues = new(request,
                    new Dictionary<string, string>(scenario.Scalars, StringComparer.Ordinal), sides);
            }
        }
        RaiseInitialScalarFieldsChanged();
    }

    private static WesnothPreprocessedScalarOwner? FindExactInitialScalarOwner(
        IReadOnlyList<WesnothPreprocessedScalarOwner> owners, string tagName,
        ExactInitialScalarRequest request, int offset)
    {
        if (offset < 0 || offset >= request.SourceText.Length || request.SourceText[offset] != '[')
            return null;
        var end = request.SourceText.IndexOf(']', offset);
        if (end < 0 || !request.SourceText.AsSpan(offset + 1, end - offset - 1).Trim()
                .Equals(tagName, StringComparison.Ordinal))
            return null;
        var matches = owners.Where(owner => owner.TagName == tagName && owner.Opening is { } opening &&
                opening.StartOffset == offset && opening.Length == end - offset + 1 &&
                PathsEqual(opening.SourcePath, request.SourcePath) &&
                string.Equals(opening.ContentHash, request.SourceHash, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private bool HasCurrentExactInitialScalarValues => _exactInitialScalarValues is { } values &&
        IsCurrentExactInitialScalarRequest(values.Request);

    private string? GetExactInitialScalarValue(string key, ScenarioSideDocument? side)
    {
        if (!HasCurrentExactInitialScalarValues)
            return null;
        var scalars = key == "turns" ? _exactInitialScalarValues!.ScenarioScalars :
            side is not null ? _exactInitialScalarValues!.SideScalars.GetValueOrDefault(side) : null;
        if (scalars is null || !scalars.TryGetValue(key, out var value))
            return null;
        // The engine projection already decoded WML strings. Do not decode a
        // second time: quoted numeric content is different from numeric content.
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
            ? integer.ToString(CultureInfo.InvariantCulture)
            : key == "turns" && value == "unlimited" ? value : null;
    }

    private void RefreshInitialSideNumbers()
    {
        foreach (var side in Sides)
        {
            if (_hasAuthoredSourceChanges || _externalSourceChangesPending ||
                !HasCurrentStaticScalarProjection && !HasCurrentExactInitialScalarValues)
            {
                side.ClearEffectiveNumber();
                continue;
            }
            var resolution = HasCurrentStaticScalarProjection
                ? (side.AuthoredDocument is { } document ? _staticSideOwners.GetValueOrDefault(document) : side.EffectiveOwner)?.ResolveField("side") : null;
            var number = resolution?.State == WmlStaticScalarResolutionState.Known
                ? NormalizeInitialInteger(resolution.ValueText)
                : resolution?.State == WmlStaticScalarResolutionState.ProvenAbsent
                    ? null : GetExactInitialScalarValue("side", side.AuthoredDocument);
            side.SetEffectiveNumber(number);
        }
    }

    private sealed record ExactInitialScalarRequest(string SourcePath, string SourceText, string SourceHash,
        ScenarioDocument AuthoredScenario, WmlEvaluationContext Context, int Generation, long HostGeneration,
        WccLibrarySnapshot Snapshot, IReadOnlySet<WccPackageRevisionId> VisibleRevisions,
        StaticRootRevisionSelection? RootSelection, WccPackageRevisionId SourceRevisionId,
        WmlCampaignEvaluationPlan? Plan, string? InstallPath, string? AddonsPath, string ProfileIdentity);

    private sealed record ExactInitialScalarValues(ExactInitialScalarRequest Request,
        IReadOnlyDictionary<string, string> ScenarioScalars,
        IReadOnlyDictionary<ScenarioSideDocument, IReadOnlyDictionary<string, string>> SideScalars);
}
