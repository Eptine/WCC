using Wcc.Core.Library;
using Wcc.Core.Wml;
using Wcc.Core.Wml.Checking;

namespace Wcc.Core.References;

/// <summary>
/// Display-only image search in one actual evaluated campaign. Available captured
/// packages are not active binary paths. No mutation, registration or IPF authority.
/// </summary>
public sealed class WccCapturedImageResourceContext
{
    private readonly WccStaticSourceCaptureResult _capture;
    private readonly IWmlStaticResourceResolver? _resolver;
    private readonly IReadOnlyList<string> _mounts;
    private WccCapturedImageResourceContext(WccStaticSourceCaptureResult capture,
        IReadOnlyList<string> mounts, string? unavailableReason)
    {
        _capture = capture;
        _resolver = capture.IncludeResolver as IWmlStaticResourceResolver;
        _mounts = mounts;
        UnavailableReason = unavailableReason;
    }

    public IReadOnlyList<string> ActiveBinaryMounts => _mounts;
    public string? UnavailableReason { get; }
    public bool HasAuthoritativeSearchOrder => UnavailableReason is null;
    public bool AuthorizesPersistence => false;

    /// <summary>Uses the full evaluation retained by an exact captured menu or
    /// scenario projection. It does not evaluate a new context or grant edits.</summary>
    public static WccCapturedImageResourceContext Create(WccStaticSourceCaptureResult capture,
        WmlStaticScalarProjection projection)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.SourceEvaluation is not { } evaluation || capture.EntrySource is null ||
            !evaluation.Sources.Contains(capture.EntrySource) ||
            evaluation.Sources.Any(source => !capture.Sources.Contains(source) &&
                !capture.PreludeSources.Contains(source) &&
                !(capture.HasCompleteCoreUnitPrelude && capture.CoreUnitPreludeSources.Contains(source)) &&
                !IsKnownSingleplayerSource(source, capture.CoreVersion)))
            return new(capture, [], "Image search needs the full projection of this exact captured campaign.");
        return Create(capture, evaluation, projection);
    }

    public static WccCapturedImageResourceContext Create(WccStaticSourceCaptureResult capture,
        WmlStaticEvaluationResult evaluation, WmlStaticScalarProjection projection)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(projection);
        if (!capture.IsCaptureComplete || !evaluation.IsComplete || capture.IncludeResolver is not IWmlStaticResourceResolver)
            return new(capture, [], "Image search needs a complete captured campaign evaluation.");
        var resources = WmlCheckResourceProjection.Create(evaluation, projection, tag => tag == "binary_path");
        if (resources.HasUncertainBinaryPathInventory)
            return new(capture, [], "The active campaign binary paths are not fully known.");
        var mounts = new List<string> { "core" };
        foreach (var resource in resources.Owners.Where(resource => resource.OriginalOwner.Parent is null))
        {
            var field = resource.ValueOwner.ResolveField("path");
            if (!resource.HasProvenTopLevelExtent || field.State != WmlStaticScalarResolutionState.Known ||
                !WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var literal, out _) ||
                !WmlResourceLookupPaths.TryMapDataPath(literal, out var mount))
                return new(capture, [], "An active campaign binary path has no bounded captured route.");
            mounts.Add(mount);
        }
        // Wesnoth searches the last declared binary path first. Keep Core as the
        // final implicit path; availability of another package never activates it.
        return new(capture, Array.AsReadOnly(mounts.AsEnumerable().Reverse().Distinct(StringComparer.Ordinal).ToArray()), null);
    }

    /// <summary>Uses an explicitly complete scenario inspection from this exact capture.
    /// Its omitted unit interiors do not become full-output or persistence evidence.</summary>
    public static WccCapturedImageResourceContext Create(WccStaticSourceCaptureResult capture,
        WmlScenarioInspectionResult inspection)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(inspection);
        if (!capture.IsCaptureComplete || !inspection.IsComplete || inspection.Evaluation.IsComplete ||
            capture.IncludeResolver is not IWmlStaticResourceResolver ||
            !ReferenceEquals(inspection.Projection.SourceEvaluation, inspection.Evaluation) ||
            !ReferenceEquals(inspection.EntrySource, capture.EntrySource) ||
            !ReferenceEquals(inspection.IncludeResolver, capture.IncludeResolver) ||
            inspection.Evaluation.Diagnostics.Any(item => item.IsBlocking) || inspection.Projection.Diagnostics.Count != 0 ||
            inspection.Evaluation.Sources.Any(source => !capture.Sources.Contains(source) &&
                !capture.PreludeSources.Contains(source) &&
                !(capture.HasCompleteCoreUnitPrelude && capture.CoreUnitPreludeSources.Contains(source)) &&
                !IsKnownSingleplayerSource(source, capture.CoreVersion)))
            return new(capture, [], "Image search needs a complete scenario inspection from this exact captured campaign.");

        var mounts = new List<string> { "core" };
        var evaluation = inspection.Evaluation;
        foreach (var owner in inspection.Projection.Owners.Where(owner => owner.TagName == "binary_path" && owner.Parent is null))
        {
            var field = owner.ResolveField("path");
            if (owner.IsAmended || owner.HasUnresolvedContributions ||
                owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing ||
                opening.Text != "[binary_path]" || closing.Text != "[/binary_path]" ||
                owner.OpeningOutputOffset is not { } start || owner.ClosingOutputOffset is not { } end || start < 0 || end <= start ||
                start > evaluation.Text.Length - opening.Length || end > evaluation.Text.Length - closing.Length ||
                !evaluation.Text.AsSpan(start, opening.Length).SequenceEqual(opening.Text) ||
                !evaluation.Text.AsSpan(end, closing.Length).SequenceEqual(closing.Text) ||
                field.State != WmlStaticScalarResolutionState.Known ||
                !owner.Fields.TryGetValue("path", out var originalField) ||
                originalField.Contributions.LastOrDefault() is not { ValueMappingIsExact: true } contribution ||
                contribution.CompositeAssignmentKeys.Count != 0 ||
                !WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var literal, out _) ||
                !WmlResourceLookupPaths.TryMapDataPath(literal, out var mount))
                return new(capture, [], "The inspected active campaign binary paths are not fully proven.");
            mounts.Add(mount);
        }
        return new(capture, Array.AsReadOnly(mounts.AsEnumerable().Reverse().Distinct(StringComparer.Ordinal).ToArray()), null);
    }

    private static bool IsKnownSingleplayerSource(WmlStaticSource source, string? version)
    {
        if (source.IsAuthored) return false;
        // These explicit host definitions are the sole legitimate inputs outside
        // the captured authored inventory. An arbitrary synthetic source cannot
        // supply a parent campaign's binary-path authority.
        foreach (var platform in Enum.GetValues<WmlStaticSingleplayerPlatform>())
            if (WmlStaticSingleplayerEnvironment.TryCreate(version, out var environment, platform) &&
                source.Path == environment!.DefinitionsSource.Path && source.ContentHash == environment.DefinitionsSource.ContentHash &&
                source.Text == environment.DefinitionsSource.Text) return true;
        return false;
    }

    /// <summary>Consumes an image expression, already separated from WML scalar quoting.</summary>
    public WccUnitPreviewResolution Resolve(string? imageExpression, string? sourceLabel = null)
    {
        var expression = WccImageExpressionParser.TryParse(imageExpression);
        var label = sourceLabel ?? "captured campaign image";
        if (expression is null) return new() { SourceLabel = label };
        WccUnitPreviewResolution Unresolved(string reason) => new()
        { HasReference = true, RawExpression = expression.NormalizedExpression, SourceLabel = label, UnavailableReason = reason };
        if (UnavailableReason is { } unavailable) return Unresolved(unavailable);
        var syntax = WmlResourceReferenceSyntax.ParseBinaryFilename(expression.BaseReference);
        if (!syntax.IsComplete || syntax.Filenames.Count != 1)
            return Unresolved("The image filename is dynamic or outside the captured resource syntax.");
        var plan = WmlResourceLookupPaths.Create(syntax.Filenames[0], "images", _mounts,
            useImageFallback: true, installationResourceFolders: _resolver!.InstallationResourceFolders);
        foreach (var route in plan.Candidates)
        {
            var presence = _resolver.ResolveResource(route, _capture.EntrySource!);
            if (presence.Exists == WmlStaticTruth.Unknown)
                return Unresolved(presence.Diagnostic ?? "An earlier image search route is not uniquely captured.");
            if (presence.Exists != WmlStaticTruth.True) continue;
            var member = WccStaticSourceCapture.FindCapturedResourceByVirtualPath(_capture, route);
            return member is null ? Unresolved("The image has no exact captured physical member.") : new()
            { HasReference = true, RawExpression = expression.NormalizedExpression, SourceLabel = label, ResolvedAbsolutePath = member.PhysicalPath };
        }
        return Unresolved("No image exists in the active captured campaign binary paths.");
    }
}
