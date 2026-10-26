using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Wcc.App.Preview;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

internal sealed record WccLeaderUnitPreviewRequest(string UnitTypeId, WccAssetReferenceKind Kind,
    WccLibrarySnapshot Snapshot, WccPackageRevisionId RootRevisionId, WmlCampaignEvaluationPlan Plan,
    WmlEvaluationContext Context, ScenarioFileAsset Asset, ScenarioDocument Document, string SourcePath,
    string SourceText, string ProfileIdentity, string? InstallPath, string? AddonsPath);

/// <summary>Read-only visual observation from one exact active unit declaration.</summary>
internal sealed record WccLeaderUnitPreviewResult(WccUnitPreviewResolution Resolution, string? Value = null,
    string? Diagnostic = null);

public partial class ScenarioWindowViewModel
{
    internal Func<WccLeaderUnitPreviewRequest, CancellationToken, Task<WccLeaderUnitPreviewResult>>?
        LeaderUnitPreviewResolver { get; set; }
    private readonly Dictionary<(WmlStaticScalarOwner Owner, string Key), WccFieldImagePreviewVm> _wccImagePreviews = new();
    private bool _wccSideFlagPreviewNotificationsInitialized;
    private Lazy<WccCapturedImageResourceContext>? _wccCapturedImageResources;
    private string? _wccCapturedImageResourcesUnavailableReason;
    private static readonly WccImageExpressionRenderer FieldImageExpressionRenderer =
        new(WccAppPaths.GetImageRenderCacheRoot());
    // Uses the same renderer/cache and strict runtime path as the existing unit editor.
    // The private delegate permits deterministic cancellation tests without an engine process.
    private Func<string?, string?, string?, CancellationToken, Task<WccImageExpressionRenderResult>>
        _wccFieldImageRenderer = FieldImageExpressionRenderer.RenderAsync;

    public WccFieldImagePreviewVm? WccSelectedLeaderImagePreview => SelectedWccInitialLeader is { } leader
        ? GetWccFieldImagePreview(leader.Owner, "unit_sprite") : null;
    public WccFieldImagePreviewVm? WccSelectedLeaderPortraitPreview => SelectedWccInitialLeader is { } leader
        ? GetWccFieldImagePreview(leader.Owner, "profile") : null;

    /// <summary>
    /// Display only; supported visual edits continue through the existing scalar draft routes.
    /// unit_sprite is a display key, distinct from the read-only recorded image attribute.
    /// </summary>
    public WccFieldImagePreviewVm GetWccFieldImagePreview(WmlStaticScalarOwner owner, string key)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!_wccSideFlagPreviewNotificationsInitialized)
            {
                _wccSideFlagPreviewNotificationsInitialized = true;
                WccSideFlagDraftsChanged += (_, _) => RefreshWccFieldImagePreviews();
            }
            if (!_wccImagePreviews.TryGetValue((owner, key), out var preview))
                _wccImagePreviews[(owner, key)] = preview = new();
            var request = CaptureWccImagePreviewRequest(owner, key);
            if (request is null)
            {
                if (preview.RequestIdentity is not null) preview.Dispose();
                return preview;
            }
            if (!Equals(preview.RequestIdentity, request))
            {
                var work = preview.Begin(request);
                _ = LoadWccImagePreviewAsync(preview, request, work.Version, work.Token, work.Completion);
            }
            return preview;
        }
    }

    private void RefreshWccFieldImagePreviews()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            var wanted = WccScenarioFieldEditors.Concat(WccSideFieldEditors).Concat(WccLeaderFieldEditors)
                .Where(row => row.Definition.Kind == ScenarioInitialFieldKind.Image)
                .Select(row => (row.Owner, row.Key)).ToHashSet();
            foreach (var leader in WccInitialLeaders)
            {
                wanted.Add((leader.Owner, "image"));
                wanted.Add((leader.Owner, "unit_sprite"));
                wanted.Add((leader.Owner, "profile"));
            }
            foreach (var key in _wccImagePreviews.Keys.Where(key => !wanted.Contains(key)).ToArray())
            {
                var old = _wccImagePreviews[key];
                _wccImagePreviews.Remove(key);
                old.Dispose();
            }
            foreach (var (owner, key) in wanted) GetWccFieldImagePreview(owner, key);
            foreach (var leader in WccInitialLeaders)
                leader.RefreshPreview(WccLeaderPreviewName(leader.Owner), GetWccFieldImagePreview(leader.Owner, "unit_sprite"));
            OnPropertyChanged(nameof(WccSelectedLeaderImagePreview));
            OnPropertyChanged(nameof(WccSelectedLeaderPortraitPreview));
        }
    }

    private void CancelWccFieldImagePreviews()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            var old = _wccImagePreviews.Values.ToArray();
            _wccImagePreviews.Clear();
            foreach (var preview in old) preview.Dispose();
        }
    }

    public async Task WaitForWccFieldImagePreviewsAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_wccInitialFieldRefreshGate) tasks = _wccImagePreviews.Values.Select(model => model.LoadingTask).ToArray();
            await Task.WhenAll(tasks);
            lock (_wccInitialFieldRefreshGate)
                if (_wccImagePreviews.Values.All(model => model.LoadingTask.IsCompleted)) return;
        }
    }

    private string WccLeaderPreviewName(WmlStaticScalarOwner owner)
    {
        foreach (var key in new[] { "name", "id", "type" })
        {
            var value = CaptureWccImageValue(owner, key);
            if (value.State == WmlStaticScalarResolutionState.Known && !string.IsNullOrWhiteSpace(value.Text))
                return value.Text;
        }
        return "Leader";
    }

    private WccImagePreviewRequest? CaptureWccImagePreviewRequest(WmlStaticScalarOwner owner, string key)
    {
        if (!IsCurrentWccInitialFieldOwner(owner) || _pristineLoadedSourceText is null ||
            _hasAuthoredSourceChanges || _externalSourceChangesPending || owner.OpeningOrigin is null) return null;
        var isLeader = owner.TagName == "leader" || WccInitialLeaders.Any(row => ReferenceEquals(row.Owner, owner));
        // For this display-only key, ProvenAbsent selects unit_type lookup; it
        // is never published as evidence that an authored scalar is absent.
        var image = key == "unit_sprite" ? new WccImageValue(WmlStaticScalarResolutionState.ProvenAbsent, null) :
            CaptureWccImageValue(owner, key);
        // The engine's sprite comes from unit_type. Recorded [unit]image is
        // serialized output, including when profile explicitly uses unit_image.
        var unitImage = key == "profile" ? new WccImageValue(WmlStaticScalarResolutionState.ProvenAbsent, null) : image;
        if (WccScenarioFieldContentScope.CheckOwner(owner) is { } scopeProblem)
        {
            if (image.State == WmlStaticScalarResolutionState.ProvenAbsent)
                image = new(WmlStaticScalarResolutionState.Unresolved, null, scopeProblem);
            if (unitImage.State == WmlStaticScalarResolutionState.ProvenAbsent)
                unitImage = new(WmlStaticScalarResolutionState.Unresolved, null, scopeProblem);
        }
        var type = isLeader ? CaptureWccImageValue(owner, "type") :
            new WccImageValue(WmlStaticScalarResolutionState.ProvenAbsent, null);
        WccEntityRecord? entity = null;
        var ambiguous = false;
        if (isLeader && type.State == WmlStaticScalarResolutionState.Known && !string.IsNullOrWhiteSpace(type.Text) &&
            _macroLibrarySnapshot is { } snapshot)
        {
            var visible = _macroVisibleRevisionIds ?? (_staticRootRevisionSelection is { } root &&
                ReferenceEquals(root.Snapshot, snapshot)
                ? WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, root.RootRevisionId) : null);
            var matches = snapshot.FindEntities(WccEntityTypes.UnitType).Where(candidate =>
                string.Equals(candidate.ExportSymbol, type.Text, StringComparison.Ordinal) &&
                snapshot.FindPackageForEntity(candidate.Id)?.IsAvailable == true &&
                visible is not null && WccLibraryRevisionVisibility.ContainsEntity(snapshot, visible, candidate))
                .Take(2).ToArray();
            if (matches.Length == 1) entity = matches[0];
            ambiguous = matches.Length > 1;
        }
        var contextualUnit = ambiguous && type.Text is { } unitType && _macroLibrarySnapshot is { } unitSnapshot &&
            _staticRootRevisionSelection is { } unitRoot && ReferenceEquals(unitRoot.Snapshot, unitSnapshot) &&
            _sourceEvaluationPlan is { } unitPlan && _currentScenarioAsset is { } unitAsset &&
            SelectedScenario?.Document is { } unitDocument && (_currentSourcePath ?? LoadedPath) is { } unitPath
            ? new WccLeaderUnitPreviewRequest(unitType, key == "profile" ? WccAssetReferenceKind.Portrait : WccAssetReferenceKind.Image,
                unitSnapshot, unitRoot.RootRevisionId, unitPlan, _evaluationContext, unitAsset, unitDocument, unitPath,
                _pristineLoadedSourceText, _wesnothSourceProfileIdentity, _wesnothSourceInstallPath, _wesnothSourceAddonsPath) : null;
        return new(owner, key, image, unitImage, type, isLeader, entity, ambiguous,
            _macroLibrarySnapshot, _macroVisibleRevisionIds, _staticRootRevisionSelection,
            _sourceHostSession, _sourceEvaluationPlan, _currentScenarioAsset, _pristineLoadedSourceText,
            _currentSourcePath ?? LoadedPath, _evaluationContext.SemanticKey, _wesnothSourceInstallPath,
            _wesnothSourceAddonsPath, _wesnothSourceProfileIdentity, _wesnothInstallPath,
            _sourceEvaluationPlan is not null, _wccCapturedImageResources, _wccCapturedImageResourcesUnavailableReason,
            contextualUnit, LeaderUnitPreviewResolver);
    }

    internal bool IsCurrentLeaderUnitPreviewRequest(WccLeaderUnitPreviewRequest request)
    {
        lock (_wccInitialFieldRefreshGate)
            return ReferenceEquals(request.Snapshot, _macroLibrarySnapshot) &&
                request.RootRevisionId == _staticRootRevisionSelection?.RootRevisionId &&
                ReferenceEquals(request.Plan, _sourceEvaluationPlan) && request.Context.SemanticKey == _evaluationContext.SemanticKey &&
                ReferenceEquals(request.Asset, _currentScenarioAsset) && ReferenceEquals(request.Document, SelectedScenario?.Document) &&
                PathsEqual(request.SourcePath, _currentSourcePath ?? LoadedPath) && request.SourceText == _pristineLoadedSourceText &&
                request.SourceText == request.Asset.RawTextSnapshot && request.Document.OriginalText == request.SourceText &&
                request.ProfileIdentity == _wesnothSourceProfileIdentity &&
                SameOptionalWccInlinePath(request.InstallPath, _wesnothSourceInstallPath) &&
                SameOptionalWccInlinePath(request.AddonsPath, _wesnothSourceAddonsPath) &&
                !_hasAuthoredSourceChanges && !_externalSourceChangesPending && HasCurrentStaticScalarProjection;
    }

    private WccImageValue CaptureWccImageValue(WmlStaticScalarOwner owner, string key)
    {
        if (FindWccInlineDraft(GetWccInitialFieldRoute(owner, key)) is { } draft)
        {
            if (draft.Operation == WmlFieldOperation.Remove)
                return new(WmlStaticScalarResolutionState.Unresolved, null, "The removed override resolves after Save.");
            if (draft.ValidationError is not null || draft.ValueInputKind != WccScenarioFieldValueInputKind.Literal)
                return new(WmlStaticScalarResolutionState.Unresolved, draft.Text, "This draft expression has not been resolved.");
            return new(WmlStaticScalarResolutionState.Known, draft.Text);
        }
        if (TryGetWccSideFlagDraftValue(owner, key, out var flagValue))
            return new(WmlStaticScalarResolutionState.Known, flagValue);
        var value = owner.ResolveField(key);
        if (value.State == WmlStaticScalarResolutionState.Known && value.ValueText is { } raw &&
            !IsTranslatedWccPreviewValue(raw) &&
            WccScenarioFieldValueCodec.InferInputKind(raw) == WccScenarioFieldValueInputKind.Literal)
            return new(value.State, WccScenarioFieldValueCodec.DecodeLiteral(raw));
        // Translated names are useful in leader cards; translated image paths are not guessed.
        if (key == "name" && value.State == WmlStaticScalarResolutionState.Known)
            return new(value.State, WccScenarioFieldValueCodec.DecodeLiteral(value.ValueText));
        return new(value.State == WmlStaticScalarResolutionState.ProvenAbsent ? value.State :
            WmlStaticScalarResolutionState.Unresolved, value.ValueText,
            value.State == WmlStaticScalarResolutionState.ProvenAbsent ? null : "The source value is unresolved.");
    }

    private static bool IsTranslatedWccPreviewValue(string raw)
    {
        var value = raw.AsSpan().TrimStart();
        return value.Length > 1 && value[0] == '_' && value[1..].TrimStart().StartsWith("\"");
    }

    private async Task LoadWccImagePreviewAsync(WccFieldImagePreviewVm model, WccImagePreviewRequest request,
        long version, CancellationToken cancellationToken, TaskCompletionSource completion)
    {
        Bitmap? bitmap = null;
        try
        {
            // Registration completes before any property callback or synchronous resolver can reenter.
            await Task.Yield();
            var result = await Task.Run(() => ResolveWccImagePreviewAsync(request, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            bitmap = await Task.Run(() => BitmapFileLoader.TryLoad(result.Resolution.ResolvedAbsolutePath), cancellationToken);
            var resolution = result.Resolution;
            var status = result.Status ?? (resolution.IsSuppressed ? "The portrait is disabled." :
                resolution.IsDynamicReference ? "This image expression needs source resolution." :
                !resolution.HasReference ? "No image is available." :
                !resolution.IsResolved ? "The image was not found in the current source resources." :
                bitmap is null ? "The image path resolved, but WCC could not display it." :
                result.Inherited ? "Preview from the exact visible unit type." : "Local image preview.");
            var expression = WccImageExpressionParser.TryParse(resolution.RawExpression);
            var compositeIsUnproved = request.RequiresCapturedResources && expression?.MayContainNestedImageReferences == true;
            if (resolution.IsResolved && compositeIsUnproved)
                status = "Base image preview only; composite modifier resources are not proven in this captured campaign.";
            if (resolution.IsResolved && expression?.HasModifiers == true && !compositeIsUnproved)
            {
                var rendered = await _wccFieldImageRenderer(request.RuntimeInstallPath, resolution.RawExpression,
                    resolution.ResolvedAbsolutePath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (rendered.IsSuccess && rendered.RenderedPath is { } renderedPath)
                {
                    var renderedBitmap = await Task.Run(() => BitmapFileLoader.TryLoad(renderedPath), cancellationToken);
                    if (renderedBitmap is not null)
                    {
                        bitmap?.Dispose();
                        bitmap = renderedBitmap;
                        status = result.Inherited ? "Unit type image expression preview." : "Local image expression preview.";
                    }
                    else status = "Showing the base image; the rendered PNG could not be displayed.";
                }
                else status = "Showing the base image; " + (rendered.Diagnostic ?? "the image expression could not be rendered.");
            }
            if (request.Key == "image" && request.Value.State == WmlStaticScalarResolutionState.Known &&
                !string.IsNullOrWhiteSpace(request.Value.Text))
                status += " Recorded source image; this attribute does not override the unit type sprite.";
            lock (_wccInitialFieldRefreshGate)
            {
                if (!cancellationToken.IsCancellationRequested &&
                    _wccImagePreviews.TryGetValue((request.Owner, request.Key), out var current) && ReferenceEquals(current, model) &&
                    Equals(request, CaptureWccImagePreviewRequest(request.Owner, request.Key)))
                {
                    model.Publish(version, bitmap, resolution.ResolvedAbsolutePath, resolution.RawExpression, status,
                        result.Inherited, result.InheritedValue);
                    bitmap = null;
                    foreach (var row in WccScenarioFieldEditors.Concat(WccSideFieldEditors).Concat(WccLeaderFieldEditors)
                                 .Where(row => ReferenceEquals(row.Owner, request.Owner) && row.Key == request.Key)) row.Refresh();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            lock (_wccInitialFieldRefreshGate)
                if (_wccImagePreviews.TryGetValue((request.Owner, request.Key), out var current) && ReferenceEquals(current, model) &&
                    Equals(request, CaptureWccImagePreviewRequest(request.Owner, request.Key)))
                    model.Publish(version, null, null, request.Value.Text, "Image preview unavailable: " + exception.Message, false);
        }
        finally
        {
            bitmap?.Dispose();
            completion.TrySetResult();
        }
    }

    private static async Task<WccImagePreviewResult> ResolveWccImagePreviewAsync(WccImagePreviewRequest request,
        CancellationToken cancellationToken)
    {
        var value = request.Value;
        var kind = request.Key is "profile" or "small_profile" ? WccAssetReferenceKind.Portrait : WccAssetReferenceKind.Image;
        if (value.State == WmlStaticScalarResolutionState.Unresolved)
            return new(new() { RawExpression = value.Text }, false, value.Problem);
        if (request.Key == "profile" && value.Text == "none")
            return new(new() { IsSuppressed = true, HasReference = true, RawExpression = "none" }, false);
        if (request.Key == "profile" && value.Text == "unit_image")
        {
            value = request.UnitImage;
            kind = WccAssetReferenceKind.Image;
            if (value.State == WmlStaticScalarResolutionState.Unresolved)
                return new(new() { RawExpression = value.Text }, false, value.Problem);
        }
        if (value.State == WmlStaticScalarResolutionState.Known && !string.IsNullOrWhiteSpace(value.Text))
        {
            var local = request.RequiresCapturedResources
                ? ApplyCapturedWccImageResources(request, new() { HasReference = true,
                    RawExpression = value.Text, SourceLabel = "Captured campaign image" })
                : WccUnitPreviewPathResolver.ResolveRawImageExpression(request.SourcePath,
                    WccSourceOrigin.ImportedExternal, value.Text, kind, request.SourceInstallPath);
            return new(local, false, local.UnavailableReason);
        }
        if (!request.IsLeader || request.Key is not ("image" or "profile" or "unit_sprite")) return new(new(), false);
        if (request.AmbiguousUnit && request.ContextualUnit is { } unitRequest && request.ContextualUnitResolver is { } resolveUnit)
        {
            var captured = await resolveUnit(unitRequest with { Kind = kind }, cancellationToken);
            var resolved = ApplyCapturedWccImageResources(request, captured.Resolution);
            return new(resolved, resolved.IsResolved || resolved.IsSuppressed, captured.Diagnostic ?? resolved.UnavailableReason,
                resolved.IsResolved || resolved.IsSuppressed ? captured.Value : null);
        }
        if (request.UnitEntity is not { } entity || request.Snapshot is not { } snapshot)
            return new(new(), false, request.AmbiguousUnit ? "More than one visible unit type matches this leader." :
                "No exact visible unit type image is available.");
        var loaded = await WccLibraryUnitEntityLoader.LoadAsync(snapshot, entity.Id, cancellationToken);
        if (!loaded.MatchesIndexedContentHash)
            return new(new(), false, "The unit source changed since it was indexed; reload the library preview.");
        var documents = loaded.SourceFile.Documents.Where(document =>
            entity.ContentFingerprint is { } fingerprint && string.Equals(fingerprint,
                WccContentHash.ComputeText(document.BlockSpan.RawText), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (documents.Length != 1) return new(new(), false, "The exact indexed unit definition could not be located.");
        var document = documents[0];
        var resolution = ApplyCapturedWccImageResources(request,
            WccUnitPreviewPathResolver.ResolveKind(loaded.SourceFile, document, kind, request.SourceInstallPath));
        var inherited = resolution.IsResolved || resolution.IsSuppressed;
        var authoredValue = kind == WccAssetReferenceKind.Image ? document.Unit.Image : document.Unit.Profile;
        return new(resolution, inherited, resolution.UnavailableReason, InheritedValue: inherited && authoredValue is not null
            ? WccScenarioFieldValueCodec.DecodeLiteral(authoredValue) : null);
    }

    private static WccUnitPreviewResolution ApplyCapturedWccImageResources(WccImagePreviewRequest request,
        WccUnitPreviewResolution resolution)
    {
        if (!request.RequiresCapturedResources || resolution.IsSuppressed || !resolution.HasReference) return resolution;
        if (request.ImageResources is { } resources)
            return resources.Value.Resolve(resolution.RawExpression, resolution.SourceLabel);
        // A configured campaign may have unknown earlier binary paths. Never
        // fall back to a coincidental owner/Core asset when its capture is partial.
        return new() { HasReference = true, RawExpression = resolution.RawExpression,
            SourceLabel = resolution.SourceLabel, IsDynamicReference = resolution.IsDynamicReference,
            UnavailableReason = request.ResourcesUnavailableReason ?? "The captured campaign image context is unavailable." };
    }

    private sealed record WccImageValue(WmlStaticScalarResolutionState State, string? Text, string? Problem = null);
    private sealed record WccImagePreviewResult(WccUnitPreviewResolution Resolution, bool Inherited,
        string? Status = null, string? InheritedValue = null);
    private sealed record WccImagePreviewRequest(WmlStaticScalarOwner Owner, string Key, WccImageValue Value,
        WccImageValue UnitImage, WccImageValue Type, bool IsLeader, WccEntityRecord? UnitEntity, bool AmbiguousUnit,
        WccLibrarySnapshot? Snapshot, IReadOnlySet<WccPackageRevisionId>? VisibleRevisions, object? Root,
        object Host, object? Plan, ScenarioFileAsset? Asset, string SourceText, string? SourcePath, string ContextKey,
        string? SourceInstallPath, string? SourceAddonsPath, string SourceProfileIdentity, string? RuntimeInstallPath,
        bool RequiresCapturedResources, Lazy<WccCapturedImageResourceContext>? ImageResources, string? ResourcesUnavailableReason,
        WccLeaderUnitPreviewRequest? ContextualUnit,
        Func<WccLeaderUnitPreviewRequest, CancellationToken, Task<WccLeaderUnitPreviewResult>>? ContextualUnitResolver);
}
