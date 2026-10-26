using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wcc.Core.Library;
using Wcc.Core.References;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;
using Wcc.App.Preview;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>
/// Display-only schedule art. The existing evaluator supplies each image's origin;
/// the captured package resolver supplies its exact package and pinned Core roots.
/// Synthetic evaluation inputs below are never passed off as physical captures.
/// </summary>
internal sealed class WccScheduleImagePreviews(WccLibrarySnapshot? snapshot,
    WccPackageRevisionId? scenarioRevision, WmlStaticSource? scenarioSource,
    IReadOnlyList<WmlMacroExpansionDefinition> definitions)
{
    private readonly Dictionary<string, Lazy<IReadOnlyList<ImageOrigin?>>> _origins = new(StringComparer.Ordinal);
    private readonly Dictionary<WmlMacroExpansionDefinition, Lazy<ImageOrigin?>> _definitionOrigins = new();
    private readonly Dictionary<(WccPackageRevisionId, string), Lazy<WmlStaticSource?>> _sourceCaptures = new();
    private sealed record ImageOrigin(WccPackageRevisionId Revision, WmlStaticSource Source);

    public CatalogPickerOptionVm Macro(string name)
    {
        // CatalogPickerOptionVm executes this resolver on its bounded background
        // queue. Opening the picker does not evaluate every schedule on the UI thread.
        var projection = new Lazy<ScenarioInitialScheduleProjection>(() => ScenarioInitialScheduleEditor.Project(
            "[scenario]\n{" + name + "}\n[/scenario]\n", definitions));
        return new()
        {
            ValueId = name, DisplayName = name,
            PreviewImageExpressionResolver = () => projection.Value.Times.ElementAtOrDefault(StartingIndex(projection.Value))?.Image,
            PreviewHint = "Starting-slot image when resolved; first-slot image for random or unresolved starts. " +
                "Random starts remain random. Presets requiring arguments may have no preview. " +
                "Literal in-bounds CROP modifiers are previewed; other modifier chains show the base image.",
            PreviewPathResolver = () => Time(projection.Value, StartingIndex(projection.Value)).PreviewPathResolver!()
        };
    }

    public CatalogPickerOptionVm Time(ScenarioInitialScheduleProjection projection, int index, string? hint = null)
    {
        var time = projection.Times.ElementAtOrDefault(index);
        var image = time?.Image;
        Lazy<IReadOnlyList<ImageOrigin?>> origins;
        lock (_origins)
        {
            if (!_origins.TryGetValue(projection.SourceText, out origins!))
                _origins[projection.SourceText] = origins = new(() => CaptureOrigins(projection));
        }
        return new()
        {
            ValueId = time?.Id ?? "", DisplayName = time?.Name ?? time?.Id ?? "",
            PreviewImageExpression = image,
            PreviewHint = (hint ?? "Time slot image.") + (string.IsNullOrWhiteSpace(image)
                ? " No resolved image is available."
                : "\n" + image + BitmapFileLoader.ImageExpressionPreviewHint(image)),
            PreviewPathResolver = () =>
            {
                if (snapshot is null || string.IsNullOrWhiteSpace(image) ||
                    origins.Value.ElementAtOrDefault(index) is not { } origin) return null;
                return WccCapturedImagePreviewPathResolver.Resolve(snapshot, origin.Revision,
                    origin.Source, image, WccAssetReferenceKind.Image).ResolvedAbsolutePath;
            }
        };
    }

    public static int StartingIndex(ScenarioInitialScheduleProjection projection) =>
        projection is { StartIsKnown: true, IsRandomStart: false, StartIndex: { } index } &&
        index >= 0 && index < projection.Times.Count ? index : 0;

    public static string Describe(ScenarioInitialScheduleProjection projection)
    {
        if (projection.Times.Count == 0) return "No resolved time image; this preset may require arguments.";
        var time = projection.Times[StartingIndex(projection)];
        var name = time.Name ?? time.Id ?? "slot " + (time.Index + 1);
        return projection.IsRandomStart ? "First-slot preview: " + name + ". Starting time remains random."
            : !projection.StartIsKnown ? "First-slot preview: " + name + ". Starting time is unresolved."
            : "Starting time: " + name + ".";
    }

    private IReadOnlyList<ImageOrigin?> CaptureOrigins(ScenarioInitialScheduleProjection projection)
    {
        if (snapshot is null) return [];
        try
        {
            // The schedule editor proves initial fragments independently. Re-evaluating
            // the entire scenario here would let unrelated difficulty/side/Story
            // macros revoke otherwise known time-image mappings. Keep this display
            // projection on the same fragment boundary, with all source guards below.
            var fragments = ScenarioContentSourceInventory.Build(projection.SourceText)
                .Where(item => item.Kind == ScenarioContentSourceKind.ChildTag && item.Name == "time" ||
                    item.Kind == ScenarioContentSourceKind.MacroInvocation && ScenarioInitialScheduleEditor.Project(
                        "[scenario]\n" + item.Text + "\n[/scenario]", definitions).Times.Count > 0)
                .Select(item => item.Text);
            var entry = new WmlStaticSource("wcc:schedule-thumbnail-source",
                "[scenario]\n" + string.Join("\n", fragments) + "\n[/scenario]", false);
            var captures = new Dictionary<WmlStaticSource, ImageOrigin?>();
            var prelude = new List<WmlStaticSource>();
            foreach (var definition in definitions.GroupBy(item => item.Name, StringComparer.Ordinal)
                         .Where(group => group.Count() == 1).Select(group => group.Single()))
            {
                var seed = new WmlStaticSource("wcc:schedule-thumbnail-definition:" + prelude.Count,
                    definition.RawText + "\n", false);
                prelude.Add(seed);
                Lazy<ImageOrigin?> capture;
                lock (_definitionOrigins)
                {
                    if (!_definitionOrigins.TryGetValue(definition, out capture!))
                        _definitionOrigins[definition] = capture = new(() => CaptureDefinition(definition));
                }
                captures.Add(seed, capture.Value);
            }
            captures.Add(entry, scenarioRevision is { } revision && scenarioSource is { } source &&
                CapturedFile(revision, source.Path) is { } physical && physical.ContentHash == source.ContentHash
                    ? new(revision, physical) : null);
            var evaluation = new WmlStaticEvaluator().Evaluate(new(entry,
                new(targetCompatibility: "1.20"), prelude));
            var root = WmlStaticScalarProjection.Create(evaluation).Owners.FirstOrDefault(owner => owner.TagName == "scenario");
            var times = root?.DirectChildren.Where(owner => owner.TagName == "time").ToArray() ?? [];
            // Do not marry a slot to a different occurrence after uncertain expansion.
            if (times.Length != projection.Times.Count) return [];
            return times.Select((time, index) =>
            {
                var slot = projection.Times[index];
                if (WccScenarioFieldValueCodec.DecodeLiteral(time.GetKnownValue("id")) != (slot.Id ?? "") ||
                    !time.Fields.TryGetValue("image", out var image) || !image.IsKnown ||
                    WccScenarioFieldValueCodec.DecodeLiteral(image.ValueText) != slot.Image ||
                    image.WinningContribution is not { } contribution) return null;
                if (contribution.ExpansionStack.Concat(contribution.ValueExpansionStack).Any(frame =>
                    captures.GetValueOrDefault(frame.Definition.Source) is null)) return null;
                // Several nested definitions may live in the same exact physical
                // file. Authenticate every synthetic origin before deduplicating
                // its physical identity; an unmapped contributor still refuses.
                var mapped = contribution.ValueOrigins.Select(origin => captures.GetValueOrDefault(origin.Source)).ToArray();
                if (mapped.Length == 0 || mapped.Any(origin => origin is null)) return null;
                var physical = mapped.DistinctBy(origin => (origin!.Revision,
                    OperatingSystem.IsWindows() ? Path.GetFullPath(origin.Source.Path).ToUpperInvariant() : Path.GetFullPath(origin.Source.Path),
                    origin.Source.ContentHash)).ToArray();
                return physical.Length == 1 ? physical[0] : null;
            }).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return []; }
    }

    private ImageOrigin? CaptureDefinition(WmlMacroExpansionDefinition definition)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(definition.SourcePath)) return null;
        var files = snapshot.SourceFilesById.Values.Where(file => SamePath(file.StoragePath, definition.SourcePath) &&
            (definition.PackageRevisionId is { } id ? file.PackageRevisionId.ToString() == id
                : file.PackageRevisionId == scenarioRevision)).Take(2).ToArray();
        if (files.Length != 1 || CapturedFile(files[0].PackageRevisionId, definition.SourcePath) is not { } source ||
            !WmlMacroDefinitionScanner.Scan(source.Text).Any(item => item.Name == definition.Name &&
                item.RawText == definition.RawText && (definition.SourceStartLine is null || item.StartLineIndex == definition.SourceStartLine))) return null;
        return new(files[0].PackageRevisionId, source);
    }

    private WmlStaticSource? CapturedFile(WccPackageRevisionId revision, string path)
    {
        if (snapshot is null) return null;
        Lazy<WmlStaticSource?> capture;
        lock (_sourceCaptures)
        {
            var key = (revision, Path.GetFullPath(path));
            if (!_sourceCaptures.TryGetValue(key, out capture!))
                _sourceCaptures[key] = capture = new(() =>
                {
                    var files = snapshot.SourceFilesById.Values.Where(file => file.PackageRevisionId == revision &&
                        file.FileType == WccSourceFileTypes.Wml && SamePath(file.StoragePath, path)).Take(2).ToArray();
                    if (files.Length != 1) return null;
                    var source = new WmlStaticSource(path, File.ReadAllText(path));
                    return source.ContentHash == files[0].ContentHash ? source : null;
                });
        }
        return capture.Value;
    }

    private static bool SamePath(string? left, string right) => !string.IsNullOrWhiteSpace(left) &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
