using System.Globalization;
using System.Text;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

public enum UnitAnimationPreviewFidelityKind
{
    Approximation,
    Ignored,
    Unresolved
}

public sealed record UnitAnimationPreviewFidelityIssue(
    UnitAnimationPreviewFidelityKind Kind,
    string Message);

public enum UnitAnimationPreviewHitOutcome
{
    Hit,
    Miss
}

public sealed record UnitAnimationPreviewContext(
    string Direction = "se",
    UnitAnimationPreviewHitOutcome HitOutcome = UnitAnimationPreviewHitOutcome.Hit,
    IReadOnlyDictionary<string, string>? Conditions = null,
    bool IncludeNeutralStart = false,
    bool IncludeNeutralEnd = false,
    int NeutralPoseDurationMs = 150);

public sealed record UnitAnimationPreviewConditionDescriptor(
    string Key,
    IReadOnlyList<string> Values);

public enum UnitAnimationPreviewVisualKind
{
    Image,
    Halo
}

public sealed record UnitAnimationPreviewFrame(
    int SourceFrameOrdinal,
    int SequenceOrdinal,
    int StartTimeMs,
    int DurationMs,
    string? ImageExpression,
    bool UsesUnitDefaultImage)
{
    public string TrackTagName { get; init; } = "frame";
    public int Layer { get; init; } = 40;
    public int SourceFrameStartTimeMs { get; init; }
    public int SourceFrameDurationMs { get; init; }
    public UnitAnimationPreviewProgressiveBinding? Offset { get; init; }
    public UnitAnimationPreviewProgressiveBinding? X { get; init; }
    public UnitAnimationPreviewProgressiveBinding? Y { get; init; }
    public UnitAnimationPreviewProgressiveBinding? DirectionalX { get; init; }
    public UnitAnimationPreviewProgressiveBinding? DirectionalY { get; init; }
    public UnitAnimationPreviewProgressiveBinding? HaloX { get; init; }
    public UnitAnimationPreviewProgressiveBinding? HaloY { get; init; }
    public UnitAnimationPreviewVisualKind VisualKind { get; init; } = UnitAnimationPreviewVisualKind.Image;
    public bool AutoHorizontalFlip { get; init; } = true;
    public bool AutoVerticalFlip { get; init; }
    public WmlMacroExpansionProvenance? MacroProvenance { get; init; }
    public int EndTimeMs => StartTimeMs + DurationMs;

    public UnitAnimationPreviewSpatialState EvaluateSpatial(
        double clockTimeMs,
        UnitAnimationPreviewTrack track)
        => new(
            Evaluate(Offset, clockTimeMs, track),
            Evaluate(X, clockTimeMs, track),
            Evaluate(Y, clockTimeMs, track),
            Evaluate(DirectionalX, clockTimeMs, track),
            Evaluate(DirectionalY, clockTimeMs, track));

    public UnitAnimationPreviewHaloState EvaluateHalo(
        double clockTimeMs,
        UnitAnimationPreviewTrack track)
        => new(
            Evaluate(HaloX, clockTimeMs, track),
            Evaluate(HaloY, clockTimeMs, track));

    public UnitAnimationPreviewDirectionalScale EvaluateDirectionalScale(string direction)
    {
        // Wesnoth's halo manager always mirrors west-facing halos. Ordinary
        // images instead honor their auto_hflip setting.
        var horizontalFlip = VisualKind == UnitAnimationPreviewVisualKind.Halo
            ? direction is "sw" or "nw"
            : AutoHorizontalFlip && direction is "sw" or "nw";
        var verticalFlip = AutoVerticalFlip && direction is "s" or "se" or "sw";
        return new UnitAnimationPreviewDirectionalScale(
            horizontalFlip ? -1 : 1,
            verticalFlip ? -1 : 1);
    }

    private double Evaluate(
        UnitAnimationPreviewProgressiveBinding? binding,
        double clockTimeMs,
        UnitAnimationPreviewTrack track)
    {
        if (binding is null)
            return 0;

        var start = binding.UsesTrackClock ? track.ParameterStartTimeMs : SourceFrameStartTimeMs;
        var duration = binding.UsesTrackClock
            ? Math.Max(1, track.ParameterEndTimeMs - track.ParameterStartTimeMs)
            : Math.Max(1, SourceFrameDurationMs);
        return binding.Value.Evaluate(clockTimeMs - start, duration);
    }
}

public sealed record UnitAnimationPreviewProgressiveBinding(
    UnitAnimationPreviewProgressiveNumber Value,
    bool UsesTrackClock);

public sealed record UnitAnimationPreviewSpatialState(
    double Offset,
    double X,
    double Y,
    double DirectionalX,
    double DirectionalY);

public sealed record UnitAnimationPreviewDirectionalScale(double X, double Y);

public sealed record UnitAnimationPreviewHaloState(double X, double Y);

public sealed class UnitAnimationPreviewTrack
{
    public required string TagName { get; init; }
    public required string Prefix { get; init; }
    public required int SourceOrder { get; init; }
    public required int StartTimeMs { get; init; }
    public int ParameterStartTimeMs { get; internal set; }
    public int ParameterEndTimeMs { get; internal set; }
    public UnitAnimationPreviewVisualKind VisualKind { get; init; } = UnitAnimationPreviewVisualKind.Image;
    public bool IsPrimary { get; internal set; }
    public bool IsSyntheticUnitDefaultPose { get; init; }
    public bool HoldsLastFrameAfterEnd { get; init; }
    public int? HoldUntilTimeMs { get; internal set; }
    public List<UnitAnimationPreviewFrame> Frames { get; } = new();
    public IReadOnlyList<WmlMacroExpansionProvenance> MacroProvenances => Frames
        .Select(x => x.MacroProvenance)
        .Where(x => x is not null)
        .Cast<WmlMacroExpansionProvenance>()
        .DistinctBy(x => x.Id)
        .ToArray();
    public int EndTimeMs => Frames.Count == 0 ? StartTimeMs : Frames[^1].EndTimeMs;

    public int FindFrameIndex(double clockTimeMs)
    {
        if (Frames.Count == 0 || clockTimeMs < StartTimeMs)
            return -1;

        if (clockTimeMs >= EndTimeMs)
        {
            if (HoldsLastFrameAfterEnd ||
                (IsPrimary &&
                 (HoldUntilTimeMs is null || clockTimeMs < HoldUntilTimeMs.Value)))
            {
                return Frames.Count - 1;
            }

            return -1;
        }

        for (var i = Frames.Count - 1; i >= 0; i--)
        {
            if (clockTimeMs >= Frames[i].StartTimeMs &&
                clockTimeMs < Frames[i].EndTimeMs)
            {
                return i;
            }
        }

        return -1;
    }
}

public sealed class UnitAnimationPreviewTimeline
{
    public string PrimaryTrackTagName { get; internal set; } = "frame";
    public int StartTimeMs { get; internal set; }
    public int EndTimeMs { get; internal set; }
    public bool Cycles { get; init; }
    public List<UnitAnimationPreviewTrack> Tracks { get; } = new();
    public List<UnitAnimationPreviewFidelityIssue> FidelityIssues { get; } = new();
    public List<WmlMacroExpansionProvenance> MacroExpansions { get; } = new();

    public UnitAnimationPreviewTrack? PrimaryTrack => Tracks.FirstOrDefault(x => x.IsPrimary);
    public IReadOnlyList<UnitAnimationPreviewFrame> Frames =>
        PrimaryTrack is { } primary
            ? primary.Frames
            : Array.Empty<UnitAnimationPreviewFrame>();

    public int DurationMs => Math.Max(0, EndTimeMs - StartTimeMs);

    public int FindFrameIndex(double clockTimeMs)
    {
        return PrimaryTrack?.FindFrameIndex(clockTimeMs) ?? -1;
    }
}

/// <summary>
/// Builds a read-only preview projection of explicit image and halo frame tracks.
/// Source WML remains authoritative and is never rewritten from this projection.
/// </summary>
public static class UnitAnimationPreviewTimelineBuilder
{
    private const int DefaultUnknownDurationMs = 100;
    private const int MaxExpandedFrames = 512;
    // `halo=` is rendered by Wesnoth's halo manager after the completed map
    // and animation buffer. It therefore sits above even WML layer 100; layer
    // 10 is only for ordinary image frames authored as the back of an effect.
    private const int HaloOverlayLayer = 101;

    private static readonly HashSet<string> AnimationKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "start_time",
        "cycles",
        "image_mod",
        "offset",
        "x",
        "y",
        "directional_x",
        "directional_y",
        "halo",
        "halo_x",
        "halo_y",
        "halo_mod"
    };

    private static readonly HashSet<string> FrameKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "duration",
        "begin",
        "end",
        "image",
        "image_diagonal",
        "image_mod",
        "offset",
        "alpha",
        "blend_color",
        "blend_ratio",
        "halo",
        "halo_x",
        "halo_y",
        "halo_mod",
        "submerge",
        "x",
        "y",
        "directional_x",
        "directional_y",
        "layer",
        "sound",
        "text",
        "primary",
        "auto_hflip",
        "auto_vflip"
    };

    private static readonly HashSet<string> UnsupportedVisualKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "alpha",
        "blend_color",
        "blend_ratio",
        "submerge",
        "sound",
        "text"
    };

    private static readonly string[] SpatialKeys =
        ["offset", "x", "y", "directional_x", "directional_y"];

    public static UnitAnimationPreviewTimeline Build(
        UnitAnimationDocument document,
        UnitAnimationPreviewContext? context = null,
        WmlMacroExpansionResult? macroExpansion = null)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        context ??= new UnitAnimationPreviewContext();

        var animationBlock = macroExpansion is null
            ? document.EditableBlock
            : UnitTypeImporter.ImportSingleAnimationFromRawText(macroExpansion.ExpandedText).EditableBlock;
        var trackTagNames = FindFrameTrackTagNames(animationBlock);
        var primaryTrackTagName = FindExplicitPrimaryFrameTag(animationBlock) ?? "frame";
        var animationKeys = new HashSet<string>(AnimationKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var tagName in trackTagNames)
        {
            var prefix = ReadFramePrefix(tagName);
            animationKeys.Add(ReadTrackKey(prefix, "start_time"));
            animationKeys.Add(ReadTrackKey(prefix, "image_mod"));
            animationKeys.Add(ReadTrackKey(prefix, "layer"));
            animationKeys.Add(ReadTrackKey(prefix, "halo"));
            animationKeys.Add(ReadTrackKey(prefix, "halo_x"));
            animationKeys.Add(ReadTrackKey(prefix, "halo_y"));
            animationKeys.Add(ReadTrackKey(prefix, "halo_mod"));
            foreach (var spatialKey in SpatialKeys)
                animationKeys.Add(ReadTrackKey(prefix, spatialKey));
        }
        var parsedAnimation = WmlEditableBlockParser.Parse(
            new WmlBlock(document.TagName, animationBlock.ToRawText()),
            animationKeys);
        var issues = new List<UnitAnimationPreviewFidelityIssue>();
        var cycles = ParseBoolean(parsedAnimation.Known.GetValueOrDefault("cycles")?.Value);

        var result = new UnitAnimationPreviewTimeline
        {
            PrimaryTrackTagName = primaryTrackTagName,
            Cycles = cycles
        };
        if (macroExpansion is not null)
        {
            result.MacroExpansions.AddRange(macroExpansion.Expansions);
            foreach (var issue in macroExpansion.Issues)
            {
                AddIssueOnce(
                    issues,
                    issue.Kind == WmlMacroExpansionIssueKind.Limit
                        ? UnitAnimationPreviewFidelityKind.Ignored
                        : UnitAnimationPreviewFidelityKind.Unresolved,
                    issue.Message);
            }
        }

        for (var trackOrdinal = 0; trackOrdinal < trackTagNames.Count; trackOrdinal++)
        {
            var tagName = trackTagNames[trackOrdinal];
            var frameBlocks = CollectSelectedFrameBlocks(
                parsedAnimation,
                tagName,
                context,
                issues);
            if (frameBlocks.Count == 0)
                continue;

            var prefix = ReadFramePrefix(tagName);
            var startTimeKey = ReadTrackKey(prefix, "start_time");
            var imageModKey = ReadTrackKey(prefix, "image_mod");
            var layerKey = ReadTrackKey(prefix, "layer");
            var startTime = ParseAnimationStartTime(
                parsedAnimation,
                startTimeKey,
                frameBlocks,
                issues);
            var animationImageMod = NormalizeWmlValue(
                parsedAnimation.Known.GetValueOrDefault(imageModKey)?.Value);
            var defaultLayer = ParseStaticLayer(
                parsedAnimation.Known.GetValueOrDefault(layerKey)?.Value,
                DefaultLayerForPrefix(prefix),
                $"animation key {layerKey}",
                issues);
            var spatialDefaults = ReadAnimationSpatialDefaults(
                document.TagName,
                prefix,
                trackTagNames,
                parsedAnimation,
                issues);
            var track = new UnitAnimationPreviewTrack
            {
                TagName = tagName,
                Prefix = prefix,
                SourceOrder = trackOrdinal,
                StartTimeMs = startTime,
                ParameterStartTimeMs = startTime,
                IsPrimary = string.Equals(tagName, primaryTrackTagName, StringComparison.OrdinalIgnoreCase)
            };
            BuildTrackFrames(
                track,
                frameBlocks,
                animationImageMod,
                defaultLayer,
                spatialDefaults,
                context,
                issues,
                macroExpansion);
            track.ParameterEndTimeMs = track.EndTimeMs;
            var hasRenderableImage = track.Frames.Any(
                x => x.ImageExpression is not null || x.UsesUnitDefaultImage);
            if (hasRenderableImage)
                result.Tracks.Add(track);
            else
                AddIssueOnce(issues, UnitAnimationPreviewFidelityKind.Ignored,
                    $"[{tagName}] contains no directly renderable unit/image values.");

            var haloTrack = BuildHaloTrack(
                track,
                frameBlocks,
                parsedAnimation,
                issues);
            if (haloTrack is not null)
                result.Tracks.Add(haloTrack);
        }

        AddImplicitUnitDefaultTrack(result);

        if (result.PrimaryTrack is null)
        {
            AddIssueOnce(
                issues,
                UnitAnimationPreviewFidelityKind.Unresolved,
                macroExpansion is null
                    ? $"No primary [{primaryTrackTagName}] track matched the current preview context. Macro-generated frames were not supplied to the timeline builder."
                    : $"No primary [{primaryTrackTagName}] track matched the current preview context after bounded macro expansion.");
        }

        AddUnitDefaultPreStartTrack(result);
        AddRequestedNeutralPoseTracks(result, context);

        AnalyzeAnimationStructure(parsedAnimation, result, issues);
        result.StartTimeMs = result.Tracks.Count == 0 ? 0 : result.Tracks.Min(x => x.StartTimeMs);
        result.EndTimeMs = result.Tracks.Count == 0 ? result.StartTimeMs : result.Tracks.Max(x => x.EndTimeMs);
        result.FidelityIssues.AddRange(issues);
        return result;
    }

    public static IReadOnlyList<UnitAnimationPreviewConditionDescriptor> DiscoverSelectableConditions(
        UnitAnimationDocument document,
        WmlMacroExpansionResult? macroExpansion = null)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var animation = macroExpansion is null
            ? document.EditableBlock
            : UnitTypeImporter.ImportSingleAnimationFromRawText(macroExpansion.ExpandedText).EditableBlock;
        var valuesByKey = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in ReadDirectChildBlocks(animation))
        {
            if (!string.Equals(child.TagName, "if", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(child.TagName, "else", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var conditions = ReadBranchConditions(animation, child);
            foreach (var (key, acceptedValues) in conditions.SelectableConditions)
            {
                if (!valuesByKey.TryGetValue(key, out var values))
                {
                    values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    valuesByKey[key] = values;
                }

                foreach (var value in SplitConditionValues(acceptedValues))
                    values.Add(value);
            }
        }

        return valuesByKey
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new UnitAnimationPreviewConditionDescriptor(
                x.Key,
                x.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
    }

    private static void BuildTrackFrames(
        UnitAnimationPreviewTrack track,
        IReadOnlyList<string> frameBlocks,
        string? animationImageMod,
        int defaultLayer,
        UnitAnimationPreviewSpatialDefaults spatialDefaults,
        UnitAnimationPreviewContext context,
        List<UnitAnimationPreviewFidelityIssue> issues,
        WmlMacroExpansionResult? macroExpansion)
    {
        var currentTime = track.StartTimeMs;
        for (var sourceOrdinal = 0; sourceOrdinal < frameBlocks.Count; sourceOrdinal++)
        {
            if (track.Frames.Count >= MaxExpandedFrames)
            {
                AddIssueOnce(
                    issues,
                    UnitAnimationPreviewFidelityKind.Ignored,
                    $"[{track.TagName}] expands beyond {MaxExpandedFrames} frames; the preview track was truncated.");
                break;
            }

            var frameBlock = WmlEditableBlockParser.Parse(
                new WmlBlock(track.TagName, frameBlocks[sourceOrdinal]),
                FrameKeys);
            var macroProvenance = macroExpansion?.FindProvenance(
                WmlMacroExpansionService.ReadProvenanceMarker(frameBlock.Lines));
            AnalyzeFrameFeatures(frameBlock, sourceOrdinal, issues);

            var frameImageMod = NormalizeWmlValue(
                frameBlock.Known.GetValueOrDefault("image_mod")?.Value);
            var effectiveImageMod = string.IsNullOrWhiteSpace(frameImageMod)
                ? animationImageMod
                : frameImageMod;
            var imageKey = IsDiagonalDirection(context.Direction) &&
                           frameBlock.Known.ContainsKey("image_diagonal")
                ? "image_diagonal"
                : "image";
            var imageValue = NormalizeWmlValue(frameBlock.Known.GetValueOrDefault(imageKey)?.Value);
            var explicitDuration = ParseFrameDuration(frameBlock, sourceOrdinal, issues) ??
                                   InferFrameDurationFromProgressiveValues(frameBlock, issues);
            var sequence = UnitAnimationImageSequenceParser.Parse(imageValue, issues);
            var layer = ParseStaticLayer(
                frameBlock.Known.GetValueOrDefault("layer")?.Value,
                defaultLayer,
                $"[{track.TagName}] source frame {sourceOrdinal + 1}",
                issues);
            var spatial = ReadFrameSpatialBindings(
                frameBlock,
                spatialDefaults,
                $"[{track.TagName}] source frame {sourceOrdinal + 1}",
                issues);
            var autoHorizontalFlip = ParseOptionalBoolean(
                                         frameBlock.Known.GetValueOrDefault("auto_hflip")?.Value) ??
                                     true;
            var autoVerticalFlip = ParseOptionalBoolean(
                                       frameBlock.Known.GetValueOrDefault("auto_vflip")?.Value) ??
                                   !string.IsNullOrWhiteSpace(track.Prefix);

            if (sequence.Count == 0)
            {
                var duration = explicitDuration ?? DefaultUnknownDurationMs;
                if (explicitDuration is null)
                {
                    AddIssueOnce(
                        issues,
                        UnitAnimationPreviewFidelityKind.Approximation,
                        $"[{track.TagName}] source frame {sourceOrdinal + 1} has no inferable duration; WCC uses {DefaultUnknownDurationMs} ms.");
                }

                var usesUnitDefault = string.Equals(
                    track.TagName,
                    "frame",
                    StringComparison.OrdinalIgnoreCase);
                var effectiveDuration = Math.Max(1, duration);
                track.Frames.Add(new UnitAnimationPreviewFrame(
                    sourceOrdinal,
                    0,
                    currentTime,
                    effectiveDuration,
                    usesUnitDefault ? AppendImageMod(null, effectiveImageMod) : null,
                    usesUnitDefault)
                {
                    TrackTagName = track.TagName,
                    Layer = layer,
                    SourceFrameStartTimeMs = currentTime,
                    SourceFrameDurationMs = effectiveDuration,
                    Offset = spatial.Offset,
                    X = spatial.X,
                    Y = spatial.Y,
                    DirectionalX = spatial.DirectionalX,
                    DirectionalY = spatial.DirectionalY,
                    AutoHorizontalFlip = autoHorizontalFlip,
                    AutoVerticalFlip = autoVerticalFlip,
                    MacroProvenance = macroProvenance
                });
                currentTime += effectiveDuration;
                continue;
            }

            var durations = AllocateDurations(
                sequence,
                explicitDuration,
                $"Source frame {sourceOrdinal + 1}",
                issues);
            var sourceFrameStartTime = currentTime;
            var sourceFrameDuration = durations.Sum();
            for (var sequenceOrdinal = 0;
                 sequenceOrdinal < sequence.Count && track.Frames.Count < MaxExpandedFrames;
                 sequenceOrdinal++)
            {
                var duration = durations[sequenceOrdinal];
                track.Frames.Add(new UnitAnimationPreviewFrame(
                    sourceOrdinal,
                    sequenceOrdinal,
                    currentTime,
                    duration,
                    AppendImageMod(sequence[sequenceOrdinal].ImageExpression, effectiveImageMod),
                    UsesUnitDefaultImage: false)
                {
                    TrackTagName = track.TagName,
                    Layer = layer,
                    SourceFrameStartTimeMs = sourceFrameStartTime,
                    SourceFrameDurationMs = sourceFrameDuration,
                    Offset = spatial.Offset,
                    X = spatial.X,
                    Y = spatial.Y,
                    DirectionalX = spatial.DirectionalX,
                    DirectionalY = spatial.DirectionalY,
                    AutoHorizontalFlip = autoHorizontalFlip,
                    AutoVerticalFlip = autoVerticalFlip,
                    MacroProvenance = macroProvenance
                });
                currentTime += duration;
            }
        }
    }

    private static UnitAnimationPreviewTrack? BuildHaloTrack(
        UnitAnimationPreviewTrack sourceTrack,
        IReadOnlyList<string> frameBlocks,
        WmlEditableBlock animation,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        if (sourceTrack.Frames.Count == 0)
            return null;

        var haloKey = ReadTrackKey(sourceTrack.Prefix, "halo");
        var haloXKey = ReadTrackKey(sourceTrack.Prefix, "halo_x");
        var haloYKey = ReadTrackKey(sourceTrack.Prefix, "halo_y");
        var haloModKey = ReadTrackKey(sourceTrack.Prefix, "halo_mod");
        var animationHaloRaw = NormalizeWmlValue(
            animation.Known.GetValueOrDefault(haloKey)?.Value);
        var animationHaloMod = NormalizeWmlValue(
            animation.Known.GetValueOrDefault(haloModKey)?.Value);
        var animationHaloPosition = ReadPairedHaloValues(
            animation,
            haloXKey,
            haloYKey,
            $"animation keys {haloXKey},{haloYKey}",
            issues);
        var animationHaloX = ParseProgressiveBinding(
            animationHaloPosition.X,
            true,
            $"animation key {haloXKey}",
            issues);
        var animationHaloY = ParseProgressiveBinding(
            animationHaloPosition.Y,
            true,
            $"animation key {haloYKey}",
            issues);

        var trackDuration = Math.Max(1, sourceTrack.EndTimeMs - sourceTrack.StartTimeMs);
        var animationHaloSteps = BuildTimedHaloSteps(
            animationHaloRaw,
            sourceTrack.StartTimeMs,
            trackDuration,
            $"Animation key {haloKey}",
            issues);
        var haloFrames = new List<UnitAnimationPreviewFrame>();

        for (var sourceOrdinal = 0; sourceOrdinal < frameBlocks.Count; sourceOrdinal++)
        {
            var sourceFrame = sourceTrack.Frames.FirstOrDefault(
                x => x.SourceFrameOrdinal == sourceOrdinal);
            if (sourceFrame is null)
                continue;

            var frameBlock = WmlEditableBlockParser.Parse(
                new WmlBlock(sourceTrack.TagName, frameBlocks[sourceOrdinal]),
                FrameKeys);
            var directHaloRaw = NormalizeWmlValue(
                frameBlock.Known.GetValueOrDefault("halo")?.Value);
            var frameHaloMod = NormalizeWmlValue(
                frameBlock.Known.GetValueOrDefault("halo_mod")?.Value);
            var frameHaloPosition = ReadPairedHaloValues(
                frameBlock,
                "halo_x",
                "halo_y",
                $"[{sourceTrack.TagName}] source frame {sourceOrdinal + 1} halo_x,halo_y",
                issues);
            var haloX = ReadFrameHaloBinding(
                frameHaloPosition.X,
                animationHaloX,
                $"[{sourceTrack.TagName}] source frame {sourceOrdinal + 1}",
                issues);
            var haloY = ReadFrameHaloBinding(
                frameHaloPosition.Y,
                animationHaloY,
                $"[{sourceTrack.TagName}] source frame {sourceOrdinal + 1}",
                issues);
            var effectiveHaloMod = CombineImageMods(frameHaloMod, animationHaloMod);
            var sourceStart = sourceFrame.SourceFrameStartTimeMs;
            var sourceDuration = Math.Max(1, sourceFrame.SourceFrameDurationMs);
            var sourceEnd = sourceStart + sourceDuration;

            if (!string.IsNullOrWhiteSpace(directHaloRaw))
            {
                var directSteps = BuildTimedHaloSteps(
                    directHaloRaw,
                    sourceStart,
                    sourceDuration,
                    $"[{sourceTrack.TagName}] source frame {sourceOrdinal + 1} halo",
                    issues);
                foreach (var step in directSteps)
                {
                    AddHaloFrame(
                        haloFrames,
                        sourceTrack,
                        sourceFrame,
                        sourceOrdinal,
                        step.SequenceOrdinal,
                        step.StartTimeMs,
                        step.DurationMs,
                        AppendImageMod(step.ImageExpression, effectiveHaloMod) ?? step.ImageExpression,
                        haloX,
                        haloY);
                }

                continue;
            }

            foreach (var step in animationHaloSteps)
            {
                var clippedStart = Math.Max(sourceStart, step.StartTimeMs);
                var clippedEnd = Math.Min(sourceEnd, step.EndTimeMs);
                if (clippedEnd <= clippedStart)
                    continue;

                AddHaloFrame(
                    haloFrames,
                    sourceTrack,
                    sourceFrame,
                    sourceOrdinal,
                    step.SequenceOrdinal,
                    clippedStart,
                    clippedEnd - clippedStart,
                    AppendImageMod(step.ImageExpression, effectiveHaloMod) ?? step.ImageExpression,
                    haloX,
                    haloY);
            }
        }

        if (haloFrames.Count == 0)
            return null;

        var haloTrack = new UnitAnimationPreviewTrack
        {
            TagName = $"{sourceTrack.TagName} halo",
            Prefix = sourceTrack.Prefix,
            SourceOrder = sourceTrack.SourceOrder,
            StartTimeMs = sourceTrack.StartTimeMs,
            ParameterStartTimeMs = sourceTrack.StartTimeMs,
            ParameterEndTimeMs = sourceTrack.EndTimeMs,
            VisualKind = UnitAnimationPreviewVisualKind.Halo,
            IsPrimary = false
        };
        haloTrack.Frames.AddRange(haloFrames.OrderBy(x => x.StartTimeMs));
        return haloTrack;
    }

    private static IReadOnlyList<TimedHaloStep> BuildTimedHaloSteps(
        string? haloRaw,
        int startTimeMs,
        int durationMs,
        string sourceLabel,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        var sequence = UnitAnimationImageSequenceParser.Parse(haloRaw, issues);
        if (sequence.Count == 0)
            return Array.Empty<TimedHaloStep>();

        var durations = AllocateDurations(sequence, durationMs, sourceLabel, issues);
        var currentTime = startTimeMs;
        var result = new List<TimedHaloStep>(sequence.Count);
        for (var i = 0; i < sequence.Count; i++)
        {
            var stepDuration = Math.Max(1, durations[i]);
            result.Add(new TimedHaloStep(
                i,
                currentTime,
                stepDuration,
                sequence[i].ImageExpression));
            currentTime += stepDuration;
        }

        return result;
    }

    private static void AddHaloFrame(
        List<UnitAnimationPreviewFrame> target,
        UnitAnimationPreviewTrack sourceTrack,
        UnitAnimationPreviewFrame sourceFrame,
        int sourceOrdinal,
        int sequenceOrdinal,
        int startTimeMs,
        int durationMs,
        string imageExpression,
        UnitAnimationPreviewProgressiveBinding? haloX,
        UnitAnimationPreviewProgressiveBinding? haloY)
    {
        target.Add(new UnitAnimationPreviewFrame(
            sourceOrdinal,
            sequenceOrdinal,
            startTimeMs,
            Math.Max(1, durationMs),
            imageExpression,
            UsesUnitDefaultImage: false)
        {
            TrackTagName = $"{sourceTrack.TagName} halo",
            Layer = HaloOverlayLayer,
            SourceFrameStartTimeMs = sourceFrame.SourceFrameStartTimeMs,
            SourceFrameDurationMs = sourceFrame.SourceFrameDurationMs,
            Offset = sourceFrame.Offset,
            HaloX = haloX,
            HaloY = haloY,
            VisualKind = UnitAnimationPreviewVisualKind.Halo,
            AutoHorizontalFlip = true,
            AutoVerticalFlip = sourceFrame.AutoVerticalFlip,
            MacroProvenance = sourceFrame.MacroProvenance
        });
    }

    private static UnitAnimationPreviewProgressiveBinding? ReadFrameHaloBinding(
        string? rawValue,
        UnitAnimationPreviewProgressiveBinding? fallback,
        string sourceLabel,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return fallback;

        return ParseProgressiveBinding(
            rawValue,
            false,
            $"{sourceLabel} halo position",
            issues) ?? fallback;
    }

    private static (string? X, string? Y) ReadPairedHaloValues(
        WmlEditableBlock block,
        string xKey,
        string yKey,
        string sourceLabel,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        var x = NormalizeWmlValue(block.Known.GetValueOrDefault(xKey)?.Value);
        var y = NormalizeWmlValue(block.Known.GetValueOrDefault(yKey)?.Value);
        if (!string.IsNullOrWhiteSpace(x) && !string.IsNullOrWhiteSpace(y))
            return (x, y);

        var depth = 0;
        var preprocessorDepth = 0;
        var macroDefinitionDepth = 0;
        for (var lineIndex = block.OpeningTagLineIndex + 1;
             lineIndex < block.ClosingTagLineIndex;
             lineIndex++)
        {
            var trimmed = block.Lines[lineIndex].Trim();
            var codeKind = WmlTopLevelCodeScanner.ClassifyTrimmedLine(trimmed);
            if (codeKind != WmlTopLevelCodeKind.None)
            {
                switch (codeKind)
                {
                    case WmlTopLevelCodeKind.ConditionalIfStart:
                        preprocessorDepth++;
                        break;
                    case WmlTopLevelCodeKind.ConditionalEndIf:
                        preprocessorDepth = Math.Max(0, preprocessorDepth - 1);
                        break;
                    case WmlTopLevelCodeKind.MacroDefinitionStart:
                        macroDefinitionDepth++;
                        break;
                    case WmlTopLevelCodeKind.MacroDefinitionEnd:
                        macroDefinitionDepth = Math.Max(0, macroDefinitionDepth - 1);
                        break;
                }

                continue;
            }

            if (TryReadOpeningTag(trimmed, out _))
            {
                depth++;
                continue;
            }

            if (TryReadClosingTag(trimmed))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth > 0 || preprocessorDepth > 0 || macroDefinitionDepth > 0)
                continue;

            var equals = trimmed.IndexOf('=');
            if (equals <= 0)
                continue;
            var keys = trimmed[..equals]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (keys.Length != 2 ||
                !keys.Contains(xKey, StringComparer.OrdinalIgnoreCase) ||
                !keys.Contains(yKey, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalized = NormalizeWmlValue(trimmed[(equals + 1)..]);
            var values = normalized?.Split(',', StringSplitOptions.TrimEntries);
            if (values is not { Length: 2 })
            {
                AddIssueOnce(
                    issues,
                    UnitAnimationPreviewFidelityKind.Unresolved,
                    $"The paired halo position on {sourceLabel} is dynamic or ambiguous: '{normalized}'.");
                return (x, y);
            }

            for (var keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                if (string.Equals(keys[keyIndex], xKey, StringComparison.OrdinalIgnoreCase))
                    x ??= values[keyIndex];
                else if (string.Equals(keys[keyIndex], yKey, StringComparison.OrdinalIgnoreCase))
                    y ??= values[keyIndex];
            }

            break;
        }

        return (x, y);
    }

    private static UnitAnimationPreviewSpatialDefaults ReadAnimationSpatialDefaults(
        string animationTagName,
        string prefix,
        IReadOnlyList<string> trackTagNames,
        WmlEditableBlock animation,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        var offsetKey = ReadTrackKey(prefix, "offset");
        var offsetRaw = NormalizeWmlValue(animation.Known.GetValueOrDefault(offsetKey)?.Value);
        if (string.IsNullOrWhiteSpace(offsetRaw))
        {
            var hasExplicitMissileTrack = trackTagNames.Contains(
                "missile_frame",
                StringComparer.OrdinalIgnoreCase);
            var hasMacroMissileTrack = animation.Lines.Any(x =>
                x.TrimStart().StartsWith("{MISSILE_FRAME", StringComparison.OrdinalIgnoreCase));

            if (string.Equals(animationTagName, "attack_anim", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(prefix, "missile", StringComparison.OrdinalIgnoreCase))
            {
                // AnimationWML's [attack_anim] shorthand supplies this when a
                // missile frame is present and no explicit value overrides it.
                offsetRaw = "0~0.8";
            }
            else if (string.Equals(animationTagName, "attack_anim", StringComparison.OrdinalIgnoreCase) &&
                     string.IsNullOrWhiteSpace(prefix) &&
                     !hasExplicitMissileTrack &&
                     !hasMacroMissileTrack)
            {
                // The shorthand's no-missile default is the ordinary melee lunge.
                offsetRaw = "0~0.6,0.6~0";
            }
            else if (string.Equals(animationTagName, "movement_anim", StringComparison.OrdinalIgnoreCase) &&
                     string.IsNullOrWhiteSpace(prefix))
            {
                // Movement animations are reevaluated for each entered hex. A
                // preview cycle therefore represents one 0 -> 1 transition.
                offsetRaw = "0~1";
            }
        }

        return new UnitAnimationPreviewSpatialDefaults(
            ParseProgressiveBinding(offsetRaw, true, $"animation key {offsetKey}", issues),
            ReadAnimationSpatialBinding(animation, prefix, "x", issues),
            ReadAnimationSpatialBinding(animation, prefix, "y", issues),
            ReadAnimationSpatialBinding(animation, prefix, "directional_x", issues),
            ReadAnimationSpatialBinding(animation, prefix, "directional_y", issues));
    }

    private static UnitAnimationPreviewProgressiveBinding? ReadAnimationSpatialBinding(
        WmlEditableBlock animation,
        string prefix,
        string key,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        var trackKey = ReadTrackKey(prefix, key);
        return ParseProgressiveBinding(
            NormalizeWmlValue(animation.Known.GetValueOrDefault(trackKey)?.Value),
            true,
            $"animation key {trackKey}",
            issues);
    }

    private static UnitAnimationPreviewSpatialDefaults ReadFrameSpatialBindings(
        WmlEditableBlock frame,
        UnitAnimationPreviewSpatialDefaults defaults,
        string sourceLabel,
        List<UnitAnimationPreviewFidelityIssue> issues)
        => new(
            ReadFrameSpatialBinding(frame, "offset", defaults.Offset, sourceLabel, issues),
            ReadFrameSpatialBinding(frame, "x", defaults.X, sourceLabel, issues),
            ReadFrameSpatialBinding(frame, "y", defaults.Y, sourceLabel, issues),
            ReadFrameSpatialBinding(frame, "directional_x", defaults.DirectionalX, sourceLabel, issues),
            ReadFrameSpatialBinding(frame, "directional_y", defaults.DirectionalY, sourceLabel, issues));

    private static UnitAnimationPreviewProgressiveBinding? ReadFrameSpatialBinding(
        WmlEditableBlock frame,
        string key,
        UnitAnimationPreviewProgressiveBinding? fallback,
        string sourceLabel,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        if (!frame.Known.TryGetValue(key, out var field))
            return fallback;

        return ParseProgressiveBinding(
            NormalizeWmlValue(field.Value),
            false,
            $"{sourceLabel} key {key}",
            issues);
    }

    private static UnitAnimationPreviewProgressiveBinding? ParseProgressiveBinding(
        string? rawValue,
        bool usesTrackClock,
        string sourceLabel,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;
        if (UnitAnimationPreviewProgressiveNumber.TryParse(rawValue, out var value) && value is not null)
            return new UnitAnimationPreviewProgressiveBinding(value, usesTrackClock);

        AddIssueOnce(
            issues,
            UnitAnimationPreviewFidelityKind.Unresolved,
            $"The progressive position on {sourceLabel} is dynamic or invalid: '{rawValue}'.");
        return null;
    }

    private static int ParseStaticLayer(
        string? rawValue,
        int fallback,
        string sourceLabel,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        var normalized = NormalizeWmlValue(rawValue);
        if (string.IsNullOrWhiteSpace(normalized))
            return fallback;
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layer))
            return layer;

        AddIssueOnce(
            issues,
            UnitAnimationPreviewFidelityKind.Approximation,
            $"The progressive/dynamic layer on {sourceLabel} is not evaluated; WCC uses layer {fallback}.");
        return fallback;
    }

    private static int DefaultLayerForPrefix(string prefix)
        => string.Equals(prefix, "missile", StringComparison.OrdinalIgnoreCase) ? 90 : 40;

    private static bool IsDiagonalDirection(string direction)
        => direction is "ne" or "se" or "sw" or "nw";

    private static string ReadTrackKey(string prefix, string key)
        => string.IsNullOrWhiteSpace(prefix) ? key : $"{prefix}_{key}";

    private static int ParseAnimationStartTime(
        WmlEditableBlock animation,
        string startTimeKey,
        IReadOnlyList<string> frameBlocks,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        var raw = NormalizeWmlValue(animation.Known.GetValueOrDefault(startTimeKey)?.Value);
        if (string.IsNullOrWhiteSpace(raw))
        {
            var firstFrame = frameBlocks.FirstOrDefault();
            if (firstFrame is not null)
            {
                var parsedFrame = WmlEditableBlockParser.Parse(
                    new WmlBlock("frame", firstFrame),
                    FrameKeys);
                var legacyBegin = NormalizeWmlValue(
                    parsedFrame.Known.GetValueOrDefault("begin")?.Value);
                if (int.TryParse(
                        legacyBegin,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var legacyStart))
                {
                    AddIssueOnce(
                        issues,
                        UnitAnimationPreviewFidelityKind.Approximation,
                        $"Legacy frame begin/end timing supplies the {startTimeKey} track start.");
                    return legacyStart;
                }
            }

            return 0;
        }
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;

        AddIssueOnce(
            issues,
            UnitAnimationPreviewFidelityKind.Unresolved,
            $"The primary {startTimeKey} '{raw}' is dynamic; WCC previews the track from 0 ms.");
        return 0;
    }

    private static void AddUnitDefaultPreStartTrack(UnitAnimationPreviewTimeline timeline)
    {
        var primary = timeline.PrimaryTrack;
        if (primary is null || timeline.Tracks.Count == 0)
            return;

        var earliestTrackStart = timeline.Tracks.Min(x => x.StartTimeMs);
        if (earliestTrackStart >= primary.StartTimeMs)
            return;

        var duration = primary.StartTimeMs - earliestTrackStart;
        var layer = primary.Frames.FirstOrDefault()?.Layer ?? 40;
        var fallback = new UnitAnimationPreviewTrack
        {
            TagName = "unit default pre-start",
            Prefix = string.Empty,
            SourceOrder = int.MinValue,
            StartTimeMs = earliestTrackStart,
            ParameterStartTimeMs = earliestTrackStart,
            ParameterEndTimeMs = primary.StartTimeMs,
            VisualKind = UnitAnimationPreviewVisualKind.Image,
            IsPrimary = false,
            IsSyntheticUnitDefaultPose = true
        };
        fallback.Frames.Add(new UnitAnimationPreviewFrame(
            SourceFrameOrdinal: -1,
            SequenceOrdinal: 0,
            StartTimeMs: earliestTrackStart,
            DurationMs: duration,
            ImageExpression: null,
            UsesUnitDefaultImage: true)
        {
            TrackTagName = fallback.TagName,
            Layer = layer,
            SourceFrameStartTimeMs = earliestTrackStart,
            SourceFrameDurationMs = duration
        });
        timeline.Tracks.Add(fallback);
    }

    private static void AddImplicitUnitDefaultTrack(UnitAnimationPreviewTimeline timeline)
    {
        if (timeline.PrimaryTrack is not null || timeline.Tracks.Count == 0)
            return;

        var startTime = timeline.Tracks.Min(x => x.StartTimeMs);
        var endTime = timeline.Tracks.Max(x => x.EndTimeMs);
        var duration = Math.Max(1, endTime - startTime);
        var track = new UnitAnimationPreviewTrack
        {
            TagName = "unit default pose",
            Prefix = string.Empty,
            SourceOrder = int.MinValue,
            StartTimeMs = startTime,
            ParameterStartTimeMs = startTime,
            ParameterEndTimeMs = startTime + duration,
            VisualKind = UnitAnimationPreviewVisualKind.Image,
            IsPrimary = true,
            IsSyntheticUnitDefaultPose = true
        };
        track.Frames.Add(new UnitAnimationPreviewFrame(
            SourceFrameOrdinal: -1,
            SequenceOrdinal: 0,
            StartTimeMs: startTime,
            DurationMs: duration,
            ImageExpression: null,
            UsesUnitDefaultImage: true)
        {
            TrackTagName = track.TagName,
            Layer = 40,
            SourceFrameStartTimeMs = startTime,
            SourceFrameDurationMs = duration
        });
        timeline.Tracks.Add(track);
        timeline.PrimaryTrackTagName = track.TagName;
    }

    private static void AddRequestedNeutralPoseTracks(
        UnitAnimationPreviewTimeline timeline,
        UnitAnimationPreviewContext context)
    {
        var authoredTracks = timeline.Tracks
            .Where(x => !x.IsSyntheticUnitDefaultPose)
            .ToArray();
        var primary = timeline.PrimaryTrack;
        if (primary is null || authoredTracks.Length == 0)
            return;

        var duration = Math.Max(1, context.NeutralPoseDurationMs);
        var authoredStart = authoredTracks.Min(x => x.StartTimeMs);
        var authoredEnd = authoredTracks.Max(x => x.EndTimeMs);
        var layer = primary.Frames.FirstOrDefault()?.Layer ?? 40;

        if (context.IncludeNeutralStart)
        {
            timeline.Tracks.Add(CreateNeutralPoseTrack(
                "unit neutral start",
                authoredStart - duration,
                duration,
                layer,
                int.MinValue + 1,
                holdsLastFrameAfterEnd: false));
        }

        if (context.IncludeNeutralEnd)
        {
            primary.HoldUntilTimeMs = authoredEnd;
            timeline.Tracks.Add(CreateNeutralPoseTrack(
                "unit neutral end",
                authoredEnd,
                duration,
                layer,
                int.MaxValue,
                holdsLastFrameAfterEnd: true));
        }
    }

    private static UnitAnimationPreviewTrack CreateNeutralPoseTrack(
        string tagName,
        int startTimeMs,
        int durationMs,
        int layer,
        int sourceOrder,
        bool holdsLastFrameAfterEnd)
    {
        var track = new UnitAnimationPreviewTrack
        {
            TagName = tagName,
            Prefix = string.Empty,
            SourceOrder = sourceOrder,
            StartTimeMs = startTimeMs,
            ParameterStartTimeMs = startTimeMs,
            ParameterEndTimeMs = startTimeMs + durationMs,
            VisualKind = UnitAnimationPreviewVisualKind.Image,
            IsPrimary = false,
            IsSyntheticUnitDefaultPose = true,
            HoldsLastFrameAfterEnd = holdsLastFrameAfterEnd
        };
        track.Frames.Add(new UnitAnimationPreviewFrame(
            SourceFrameOrdinal: -1,
            SequenceOrdinal: 0,
            StartTimeMs: startTimeMs,
            DurationMs: durationMs,
            ImageExpression: null,
            UsesUnitDefaultImage: true)
        {
            TrackTagName = tagName,
            Layer = layer,
            SourceFrameStartTimeMs = startTimeMs,
            SourceFrameDurationMs = durationMs
        });
        return track;
    }

    private static int? ParseFrameDuration(
        WmlEditableBlock frame,
        int sourceOrdinal,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        var durationRaw = NormalizeWmlValue(frame.Known.GetValueOrDefault("duration")?.Value);
        if (int.TryParse(
                durationRaw,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var duration) && duration > 0)
        {
            return duration;
        }

        var beginRaw = NormalizeWmlValue(frame.Known.GetValueOrDefault("begin")?.Value);
        var endRaw = NormalizeWmlValue(frame.Known.GetValueOrDefault("end")?.Value);
        if (int.TryParse(beginRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var begin) &&
            int.TryParse(endRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var end) &&
            end > begin)
        {
            AddIssueOnce(
                issues,
                UnitAnimationPreviewFidelityKind.Approximation,
                "Legacy frame begin/end timing is treated as duration=end-begin.");
            return end - begin;
        }

        if (!string.IsNullOrWhiteSpace(durationRaw))
        {
            AddIssueOnce(
                issues,
                UnitAnimationPreviewFidelityKind.Unresolved,
                $"Source frame {sourceOrdinal + 1} has dynamic duration '{durationRaw}'.");
        }

        return null;
    }

    private static int? InferFrameDurationFromProgressiveValues(
        WmlEditableBlock frame,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        var candidates = new List<int>();
        var haloRaw = NormalizeWmlValue(frame.Known.GetValueOrDefault("halo")?.Value);
        if (!string.IsNullOrWhiteSpace(haloRaw))
        {
            var haloSequence = UnitAnimationImageSequenceParser.Parse(haloRaw, issues);
            if (haloSequence.Count > 0 && haloSequence.All(x => x.DurationMs.HasValue))
                candidates.Add(haloSequence.Sum(x => x.DurationMs!.Value));
        }

        foreach (var key in new[]
                 {
                     "offset", "alpha", "blend_ratio", "submerge", "x", "y",
                     "directional_x", "directional_y", "halo_x", "halo_y", "layer"
                 })
        {
            var raw = NormalizeWmlValue(frame.Known.GetValueOrDefault(key)?.Value);
            if (UnitAnimationPreviewProgressiveNumber.TryParse(raw, out var progressive) &&
                progressive?.ExplicitDurationMs is { } duration)
            {
                candidates.Add(duration);
            }
        }

        return candidates.Count == 0 ? null : candidates.Max();
    }

    private static IReadOnlyList<int> AllocateDurations(
        IReadOnlyList<UnitAnimationImageSequenceEntry> sequence,
        int? explicitFrameDuration,
        string sourceLabel,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        var explicitImageDurations = sequence
            .Select(x => x.DurationMs)
            .ToArray();
        var knownSum = explicitImageDurations.Where(x => x.HasValue).Sum(x => x!.Value);
        var unknownCount = explicitImageDurations.Count(x => !x.HasValue);
        var result = new int[sequence.Count];

        if (explicitFrameDuration is { } frameDuration)
        {
            if (unknownCount == sequence.Count)
            {
                FillEvenly(result, frameDuration);
                return result;
            }

            if (unknownCount == 0 && knownSum == frameDuration)
                return explicitImageDurations.Select(x => Math.Max(1, x!.Value)).ToArray();

            var remaining = Math.Max(unknownCount, frameDuration - knownSum);
            var fallback = unknownCount > 0
                ? Math.Max(1, remaining / unknownCount)
                : 0;
            for (var i = 0; i < result.Length; i++)
                result[i] = Math.Max(1, explicitImageDurations[i] ?? fallback);

            var difference = frameDuration - result.Sum();
            result[^1] = Math.Max(1, result[^1] + difference);
            AddIssueOnce(
                issues,
                UnitAnimationPreviewFidelityKind.Approximation,
                $"{sourceLabel} mixes a declared duration and image timings; WCC fits the image steps to the declared duration.");
            return result;
        }

        if (unknownCount == 0)
            return explicitImageDurations.Select(x => Math.Max(1, x!.Value)).ToArray();

        for (var i = 0; i < result.Length; i++)
            result[i] = Math.Max(1, explicitImageDurations[i] ?? DefaultUnknownDurationMs);
        AddIssueOnce(
            issues,
            UnitAnimationPreviewFidelityKind.Approximation,
            $"{sourceLabel} omits some image timings; WCC uses {DefaultUnknownDurationMs} ms for those steps.");
        return result;
    }

    private static void FillEvenly(int[] target, int totalDuration)
    {
        var count = Math.Max(1, target.Length);
        var baseDuration = Math.Max(1, totalDuration / count);
        var remainder = Math.Max(0, totalDuration - baseDuration * count);
        for (var i = 0; i < target.Length; i++)
            target[i] = baseDuration + (i < remainder ? 1 : 0);
    }

    private static string? AppendImageMod(string? imageExpression, string? imageMod)
    {
        if (string.IsNullOrWhiteSpace(imageMod))
            return imageExpression;

        var suffix = imageMod.Trim();
        if (!suffix.StartsWith('~'))
            suffix = "~" + suffix;
        return (imageExpression ?? string.Empty) + suffix;
    }

    private static string? CombineImageMods(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return second;
        if (string.IsNullOrWhiteSpace(second))
            return first;

        var firstNormalized = first.Trim();
        var secondNormalized = second.Trim();
        if (!firstNormalized.StartsWith('~'))
            firstNormalized = "~" + firstNormalized;
        if (!secondNormalized.StartsWith('~'))
            secondNormalized = "~" + secondNormalized;
        return firstNormalized + secondNormalized;
    }

    private static void AnalyzeAnimationStructure(
        WmlEditableBlock animation,
        UnitAnimationPreviewTimeline timeline,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        var authoredTracks = timeline.Tracks
            .Where(x => !x.IsSyntheticUnitDefaultPose)
            .ToArray();
        if (authoredTracks.Length > 1)
        {
            AddIssueOnce(
                issues,
                UnitAnimationPreviewFidelityKind.Approximation,
                $"WCC composites {authoredTracks.Length} explicit visual tracks using their clocks, static layer values, and progressive positioning: {string.Join(", ", authoredTracks.Select(x => $"[{x.TagName}]"))}. Opacity, blend, and submerge are not applied yet.");
        }

        if (timeline.Tracks.Any(x => x.VisualKind == UnitAnimationPreviewVisualKind.Halo))
        {
            AddIssueOnce(
                issues,
                UnitAnimationPreviewFidelityKind.Approximation,
                "Resolved halo streams are composited as post-map foreground overlays, matching Wesnoth's halo manager, with independent image timing, halo_x/halo_y, halo_mod, offset anchoring, and directional mirroring. Terrain-height and live team-color context are unavailable in this preview.");
        }

        if (timeline.PrimaryTrack is { IsSyntheticUnitDefaultPose: true })
        {
            AddIssueOnce(
                issues,
                UnitAnimationPreviewFidelityKind.Approximation,
                "The animation has no selected unit/body [frame], so the default unit image remains visible behind its effect tracks.");
        }
        else if (!string.Equals(timeline.PrimaryTrackTagName, "frame", StringComparison.OrdinalIgnoreCase))
        {
            AddIssueOnce(
                issues,
                UnitAnimationPreviewFidelityKind.Approximation,
                $"[{timeline.PrimaryTrackTagName}] declares primary=yes and is used for preview controls and unit-image fallback semantics.");
        }

        if (animation.Lines.Any(x => x.TrimStart().StartsWith('{')))
        {
            AddIssueOnce(
                issues,
                UnitAnimationPreviewFidelityKind.Unresolved,
                "Animation macro invocations are preserved but not expanded into preview frames.");
        }

        if (animation.Lines.Any(x =>
                x.TrimStart().StartsWith("#if", StringComparison.OrdinalIgnoreCase) ||
                x.TrimStart().StartsWith("#else", StringComparison.OrdinalIgnoreCase)))
        {
            AddIssueOnce(
                issues,
                UnitAnimationPreviewFidelityKind.Approximation,
                "Preprocessor branches are not evaluated; direct frames are shown in source order.");
        }
    }

    private static IReadOnlyList<string> CollectSelectedFrameBlocks(
        WmlEditableBlock animation,
        string primaryTrackTagName,
        UnitAnimationPreviewContext context,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        var result = new List<string>();
        var children = ReadDirectChildBlocks(animation);
        var usedConditionalBranch = false;

        for (var childIndex = 0; childIndex < children.Count; childIndex++)
        {
            var child = children[childIndex];
            if (string.Equals(child.TagName, primaryTrackTagName, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(ReadRawBlock(animation, child));
                continue;
            }

            if (!string.Equals(child.TagName, "if", StringComparison.OrdinalIgnoreCase))
                continue;

            var branches = new List<DirectChildBlock> { child };
            while (childIndex + 1 < children.Count &&
                   string.Equals(children[childIndex + 1].TagName, "else", StringComparison.OrdinalIgnoreCase))
            {
                branches.Add(children[++childIndex]);
            }

            var selected = SelectConditionalBranch(animation, branches, context, issues);
            if (selected is null)
                continue;

            usedConditionalBranch = true;
            var branchRaw = ReadRawBlock(animation, selected);
            var branch = WmlEditableBlockParser.Parse(
                new WmlBlock(selected.TagName, branchRaw),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            foreach (var frameSpan in WmlDirectChildBlockScanner.FindSpans(branch, primaryTrackTagName))
            {
                result.Add(string.Join(
                    branch.NewLine,
                    branch.Lines
                        .Skip(frameSpan.StartLineIndex)
                        .Take(frameSpan.EndLineIndexExclusive - frameSpan.StartLineIndex)));
            }
        }

        if (usedConditionalBranch)
        {
            AddIssueOnce(
                issues,
                UnitAnimationPreviewFidelityKind.Approximation,
                $"Conditional frame tracks use preview context direction={context.Direction}, outcome={context.HitOutcome.ToString().ToLowerInvariant()}" +
                (context.Conditions is { Count: > 0 }
                    ? $", filters={string.Join(",", context.Conditions.Select(x => $"{x.Key}={x.Value}"))}."
                    : ".") +
                " Simple selectable [filter] values are supported; complex filters are not simulated.");
        }

        return result;
    }

    private static IReadOnlyList<string> FindFrameTrackTagNames(WmlEditableBlock animation)
    {
        var result = new List<string>();
        foreach (var line in animation.Lines)
        {
            var tagName = TryReadFrameTagName(line.Trim());
            if (tagName is null || result.Contains(tagName, StringComparer.OrdinalIgnoreCase))
                continue;
            result.Add(tagName);
        }

        return result;
    }

    private static string? FindExplicitPrimaryFrameTag(WmlEditableBlock animation)
    {
        var candidates = animation.Lines
            .Select((line, index) => (TagName: TryReadFrameTagName(line.Trim()), Index: index))
            .Where(x => x.TagName is not null &&
                        !string.Equals(x.TagName, "frame", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.TagName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(tagName => WmlLineBlockScanner.FindSpans(animation.Lines, tagName)
                .Select(span => (TagName: tagName, Span: span)))
            .OrderBy(x => x.Span.StartLineIndex);

        foreach (var candidate in candidates)
        {
            var raw = string.Join(
                animation.NewLine,
                animation.Lines
                    .Skip(candidate.Span.StartLineIndex)
                    .Take(candidate.Span.EndLineIndexExclusive - candidate.Span.StartLineIndex));
            var frame = WmlEditableBlockParser.Parse(
                new WmlBlock(candidate.TagName, raw),
                new HashSet<string>(["primary"], StringComparer.OrdinalIgnoreCase));
            if (ParseBoolean(frame.Known.GetValueOrDefault("primary")?.Value))
                return candidate.TagName;
        }

        return null;
    }

    private static string? TryReadFrameTagName(string line)
    {
        if (!TryReadOpeningTag(line, out var tagName) ||
            string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        return string.Equals(tagName, "frame", StringComparison.OrdinalIgnoreCase) ||
               tagName.EndsWith("_frame", StringComparison.OrdinalIgnoreCase)
            ? tagName
            : null;
    }

    private static string ReadFramePrefix(string frameTagName)
    {
        const string suffix = "_frame";
        return frameTagName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? frameTagName[..^suffix.Length]
            : string.Empty;
    }

    private static DirectChildBlock? SelectConditionalBranch(
        WmlEditableBlock animation,
        IReadOnlyList<DirectChildBlock> branches,
        UnitAnimationPreviewContext context,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        DirectChildBlock? fallback = null;
        foreach (var branchSpan in branches)
        {
            var conditions = ReadBranchConditions(animation, branchSpan);
            var hasKnownCondition = !string.IsNullOrWhiteSpace(conditions.Direction) ||
                                    !string.IsNullOrWhiteSpace(conditions.Hits) ||
                                    conditions.SelectableConditions.Count > 0 ||
                                    conditions.HasUnsupportedConditions;
            if (!hasKnownCondition)
            {
                fallback ??= branchSpan;
                continue;
            }

            if (conditions.HasUnsupportedConditions)
            {
                AddIssueOnce(
                    issues,
                    UnitAnimationPreviewFidelityKind.Unresolved,
                    "A conditional animation branch contains a complex [filter]; the preview does not guess whether it matches.");
                continue;
            }

            if (MatchesDirection(conditions.Direction, context.Direction) &&
                MatchesHitOutcome(conditions.Hits, context.HitOutcome) &&
                MatchesSelectableConditions(conditions.SelectableConditions, context.Conditions))
            {
                return branchSpan;
            }
        }

        if (fallback is not null)
            return fallback;

        AddIssueOnce(
            issues,
            UnitAnimationPreviewFidelityKind.Unresolved,
            $"No conditional branch matched direction={context.Direction}, outcome={context.HitOutcome.ToString().ToLowerInvariant()}.");
        return null;
    }

    private static BranchConditions ReadBranchConditions(
        WmlEditableBlock animation,
        DirectChildBlock branchSpan)
    {
        var raw = ReadRawBlock(animation, branchSpan);
        var branch = WmlEditableBlockParser.Parse(
            new WmlBlock(branchSpan.TagName, raw),
            new HashSet<string>(["direction", "hits"], StringComparer.OrdinalIgnoreCase));
        var direction = NormalizeWmlValue(branch.Known.GetValueOrDefault("direction")?.Value);
        var hits = NormalizeWmlValue(branch.Known.GetValueOrDefault("hits")?.Value);
        var selectable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unsupported = false;

        foreach (var filterSpan in ReadDirectChildBlocks(branch)
                     .Where(x => string.Equals(x.TagName, "filter", StringComparison.OrdinalIgnoreCase)))
        {
            var filterRaw = ReadRawBlock(branch, filterSpan);
            var filter = WmlEditableBlockParser.Parse(
                new WmlBlock("filter", filterRaw),
                ReadDirectScalarKeys(filterRaw, "filter"));
            if (ReadDirectChildBlocks(filter).Count > 0 ||
                filter.Lines.Any(line =>
                    line.TrimStart().StartsWith('{') ||
                    line.TrimStart().StartsWith("[filter_", StringComparison.OrdinalIgnoreCase) ||
                    line.TrimStart().StartsWith("[not]", StringComparison.OrdinalIgnoreCase) ||
                    line.TrimStart().StartsWith("[and]", StringComparison.OrdinalIgnoreCase) ||
                    line.TrimStart().StartsWith("[or]", StringComparison.OrdinalIgnoreCase)))
            {
                unsupported = true;
                continue;
            }

            foreach (var field in filter.Known.Values)
            {
                var value = NormalizeWmlValue(field.Value);
                if (string.IsNullOrWhiteSpace(value) ||
                    string.Equals(field.Key, "formula", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains('$') || value.Contains('{'))
                {
                    unsupported = true;
                    continue;
                }

                if (selectable.TryGetValue(field.Key, out var previous) &&
                    !string.Equals(previous, value, StringComparison.OrdinalIgnoreCase))
                {
                    unsupported = true;
                    continue;
                }

                selectable[field.Key] = value;
            }
        }

        return new BranchConditions(direction, hits, selectable, unsupported);
    }

    private static HashSet<string> ReadDirectScalarKeys(string rawText, string tagName)
    {
        var extracted = WmlKeyValueExtractor.Extract(
            new WmlBlock(tagName, rawText),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in extracted.UnknownLines)
        {
            var line = WmlInlineCommentSplitter.Split(rawLine).Value.Trim();
            var equals = line.IndexOf('=');
            if (equals <= 0 || line.StartsWith('[') || line.StartsWith('{') || line.StartsWith('#'))
                continue;
            var key = line[..equals].Trim();
            if (key.Length > 0)
                keys.Add(key);
        }

        return keys;
    }

    private static bool MatchesSelectableConditions(
        IReadOnlyDictionary<string, string> required,
        IReadOnlyDictionary<string, string>? selected)
    {
        foreach (var (key, acceptedValues) in required)
        {
            if (selected is null ||
                !selected.TryGetValue(key, out var selectedValue) ||
                string.IsNullOrWhiteSpace(selectedValue) ||
                !SplitConditionValues(acceptedValues).Contains(
                    selectedValue,
                    StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<string> SplitConditionValues(string rawValue)
        => rawValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .ToArray();

    private static bool MatchesDirection(string? rawDirections, string previewDirection)
    {
        if (string.IsNullOrWhiteSpace(rawDirections))
            return true;

        return rawDirections
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => string.Equals(x, previewDirection, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesHitOutcome(string? rawHits, UnitAnimationPreviewHitOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(rawHits))
            return true;

        var accepted = outcome == UnitAnimationPreviewHitOutcome.Hit
            ? new HashSet<string>(["yes", "hit", "kill"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(["no", "miss"], StringComparer.OrdinalIgnoreCase);
        return rawHits
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(accepted.Contains);
    }

    private static void AnalyzeFrameFeatures(
        WmlEditableBlock frame,
        int sourceOrdinal,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        var unsupported = UnsupportedVisualKeys
            .Where(frame.Known.ContainsKey)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unsupported.Length > 0)
        {
            AddIssueOnce(
                issues,
                UnitAnimationPreviewFidelityKind.Ignored,
                $"Frame drawing/audio properties are not composited yet: {string.Join(", ", unsupported)}.");
        }

        if (frame.Lines.Any(x => x.TrimStart().StartsWith('{')))
        {
            AddIssueOnce(
                issues,
                UnitAnimationPreviewFidelityKind.Unresolved,
                $"Source frame {sourceOrdinal + 1} contains a macro invocation that is not evaluated by the preview.");
        }
    }

    private static IReadOnlyList<string> ReadDirectChildTagNames(WmlEditableBlock block)
        => ReadDirectChildBlocks(block).Select(x => x.TagName).ToArray();

    private static IReadOnlyList<DirectChildBlock> ReadDirectChildBlocks(WmlEditableBlock block)
    {
        var result = new List<DirectChildBlock>();
        var depth = 0;
        var childStart = -1;
        string? childTag = null;
        for (var i = block.OpeningTagLineIndex + 1; i < block.ClosingTagLineIndex; i++)
        {
            var trimmed = block.Lines[i].Trim();
            if (TryReadOpeningTag(trimmed, out var opening))
            {
                if (depth == 0)
                {
                    childStart = i;
                    childTag = opening;
                }
                depth++;
            }
            else if (TryReadClosingTag(trimmed))
            {
                depth = Math.Max(0, depth - 1);
                if (depth == 0 && childStart >= 0 && childTag is not null)
                {
                    result.Add(new DirectChildBlock(childTag, childStart, i + 1));
                    childStart = -1;
                    childTag = null;
                }
            }
        }

        return result;
    }

    private static string ReadRawBlock(WmlEditableBlock owner, DirectChildBlock child)
        => string.Join(
            owner.NewLine,
            owner.Lines
                .Skip(child.StartLineIndex)
                .Take(child.EndLineIndexExclusive - child.StartLineIndex));

    private sealed record DirectChildBlock(
        string TagName,
        int StartLineIndex,
        int EndLineIndexExclusive);

    private sealed record BranchConditions(
        string? Direction,
        string? Hits,
        IReadOnlyDictionary<string, string> SelectableConditions,
        bool HasUnsupportedConditions);

    private sealed record UnitAnimationPreviewSpatialDefaults(
        UnitAnimationPreviewProgressiveBinding? Offset,
        UnitAnimationPreviewProgressiveBinding? X,
        UnitAnimationPreviewProgressiveBinding? Y,
        UnitAnimationPreviewProgressiveBinding? DirectionalX,
        UnitAnimationPreviewProgressiveBinding? DirectionalY);

    private sealed record TimedHaloStep(
        int SequenceOrdinal,
        int StartTimeMs,
        int DurationMs,
        string ImageExpression)
    {
        public int EndTimeMs => StartTimeMs + DurationMs;
    }

    private static bool TryReadOpeningTag(string line, out string? tagName)
    {
        tagName = null;
        if (!line.StartsWith('[') || !line.EndsWith(']'))
            return false;
        var inner = line[1..^1].Trim();
        if (inner.Length == 0 || inner.StartsWith('/'))
            return false;
        var stop = inner.IndexOfAny([' ', '\t']);
        tagName = (stop < 0 ? inner : inner[..stop]).Trim();
        return tagName.Length > 0;
    }

    private static bool TryReadClosingTag(string line)
    {
        if (!line.StartsWith('[') || !line.EndsWith(']'))
            return false;
        return line[1..^1].TrimStart().StartsWith('/');
    }

    private static bool ParseBoolean(string? value)
    {
        var normalized = NormalizeWmlValue(value);
        return string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "1", StringComparison.Ordinal);
    }

    private static bool? ParseOptionalBoolean(string? value)
    {
        var normalized = NormalizeWmlValue(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;
        if (string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "1", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(normalized, "no", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "0", StringComparison.Ordinal))
        {
            return false;
        }

        return null;
    }

    internal static string? NormalizeWmlValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = WmlInlineCommentSplitter.Split(value.Trim()).Value.Trim();
        if (normalized.Length >= 2 &&
            ((normalized[0] == '"' && normalized[^1] == '"') ||
             (normalized[0] == '\'' && normalized[^1] == '\'')))
        {
            normalized = normalized[1..^1];
        }

        return normalized.Trim();
    }

    internal static void AddIssueOnce(
        List<UnitAnimationPreviewFidelityIssue> issues,
        UnitAnimationPreviewFidelityKind kind,
        string message)
    {
        if (issues.Any(x => x.Kind == kind && string.Equals(x.Message, message, StringComparison.Ordinal)))
            return;
        issues.Add(new UnitAnimationPreviewFidelityIssue(kind, message));
    }
}

internal sealed record UnitAnimationImageSequenceEntry(
    string ImageExpression,
    int? DurationMs);

internal static class UnitAnimationImageSequenceParser
{
    public static IReadOnlyList<UnitAnimationImageSequenceEntry> Parse(
        string? rawValue,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return Array.Empty<UnitAnimationImageSequenceEntry>();

        var result = new List<UnitAnimationImageSequenceEntry>();
        foreach (var rawEntry in SplitTopLevel(rawValue!, ','))
        {
            var entry = rawEntry.Trim();
            if (entry.Length == 0)
                continue;

            SplitTiming(entry, out var imagePattern, out var timingPattern);
            var images = ExpandPattern(imagePattern, issues);
            var timings = ParseTimings(timingPattern, issues);
            for (var i = 0; i < images.Count; i++)
            {
                int? duration = null;
                if (timings.Count == 1)
                    duration = timings[0];
                else if (timings.Count > 1)
                    duration = timings[Math.Min(i, timings.Count - 1)];
                result.Add(new UnitAnimationImageSequenceEntry(images[i], duration));
            }

            if (timings.Count > 1 && timings.Count != images.Count)
            {
                UnitAnimationPreviewTimelineBuilder.AddIssueOnce(
                    issues,
                    UnitAnimationPreviewFidelityKind.Approximation,
                    "An image sequence has a different number of images and timings; WCC repeats the last available timing.");
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ExpandPattern(
        string pattern,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        var groups = new List<(int Start, int Length, IReadOnlyList<string> Values)>();
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '[')
                continue;
            var end = FindMatchingBracket(pattern, i);
            if (end < 0)
                break;
            groups.Add((i, end - i + 1, ExpandList(pattern[(i + 1)..end])));
            i = end;
        }

        if (groups.Count == 0)
            return [pattern.Trim()];

        var count = groups.Max(x => Math.Max(1, x.Values.Count));
        if (groups.Any(x => x.Values.Count != 1 && x.Values.Count != count))
        {
            UnitAnimationPreviewTimelineBuilder.AddIssueOnce(
                issues,
                UnitAnimationPreviewFidelityKind.Approximation,
                "Parallel square-bracket expansions have different lengths; WCC repeats each group's last value.");
        }

        var expanded = new List<string>(count);
        for (var itemIndex = 0; itemIndex < count; itemIndex++)
        {
            var builder = new StringBuilder();
            var cursor = 0;
            foreach (var group in groups)
            {
                builder.Append(pattern, cursor, group.Start - cursor);
                var values = group.Values.Count == 0 ? [string.Empty] : group.Values;
                builder.Append(values[Math.Min(itemIndex, values.Count - 1)]);
                cursor = group.Start + group.Length;
            }
            builder.Append(pattern, cursor, pattern.Length - cursor);
            expanded.Add(builder.ToString().Trim());
        }

        return expanded;
    }

    private static IReadOnlyList<int> ParseTimings(
        string? timingPattern,
        List<UnitAnimationPreviewFidelityIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(timingPattern))
            return Array.Empty<int>();

        var timing = timingPattern.Trim();
        IReadOnlyList<string> values = timing.StartsWith('[') && timing.EndsWith(']')
            ? ExpandList(timing[1..^1])
            : [timing];
        var parsed = new List<int>();
        foreach (var value in values)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration) &&
                duration > 0)
            {
                parsed.Add(duration);
            }
            else
            {
                UnitAnimationPreviewTimelineBuilder.AddIssueOnce(
                    issues,
                    UnitAnimationPreviewFidelityKind.Unresolved,
                    $"Image timing '{value}' is dynamic or invalid and could not be applied.");
            }
        }

        return parsed;
    }

    private static IReadOnlyList<string> ExpandList(string value)
    {
        var result = new List<string>();
        foreach (var rawPart in value.Split(','))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
                continue;

            var repetitionIndex = part.LastIndexOf('*');
            if (repetitionIndex > 0 &&
                int.TryParse(
                    part[(repetitionIndex + 1)..],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var repetitions) && repetitions > 0)
            {
                var repeatedValue = part[..repetitionIndex];
                for (var i = 0; i < repetitions; i++)
                    result.Add(repeatedValue);
                continue;
            }

            var rangeIndex = part.IndexOf('~', 1);
            if (rangeIndex > 0 &&
                int.TryParse(part[..rangeIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var from) &&
                int.TryParse(part[(rangeIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var to))
            {
                var fromText = part[..rangeIndex];
                var toText = part[(rangeIndex + 1)..];
                var width = HasExplicitLeadingZero(fromText) || HasExplicitLeadingZero(toText)
                    ? Math.Max(CountDigits(fromText), CountDigits(toText))
                    : 0;
                var step = from <= to ? 1 : -1;
                for (var current = from;; current += step)
                {
                    result.Add(FormatRangeValue(current, width));
                    if (current == to)
                        break;
                }
                continue;
            }

            result.Add(part);
        }

        return result;
    }

    private static int CountDigits(string value) => value.TrimStart('-', '+').Length;

    private static bool HasExplicitLeadingZero(string value)
    {
        var digits = value.Trim().TrimStart('-', '+');
        return digits.Length > 1 && digits[0] == '0';
    }

    private static string FormatRangeValue(int value, int width)
    {
        if (width <= 0)
            return value.ToString(CultureInfo.InvariantCulture);
        if (value < 0)
            return "-" + Math.Abs(value).ToString($"D{width}", CultureInfo.InvariantCulture);
        return value.ToString($"D{width}", CultureInfo.InvariantCulture);
    }

    private static void SplitTiming(
        string entry,
        out string imagePattern,
        out string? timingPattern)
    {
        var squareDepth = 0;
        var parenthesisDepth = 0;
        var braceDepth = 0;
        for (var i = entry.Length - 1; i >= 0; i--)
        {
            switch (entry[i])
            {
                case ']': squareDepth++; break;
                case '[': squareDepth = Math.Max(0, squareDepth - 1); break;
                case ')': parenthesisDepth++; break;
                case '(': parenthesisDepth = Math.Max(0, parenthesisDepth - 1); break;
                case '}': braceDepth++; break;
                case '{': braceDepth = Math.Max(0, braceDepth - 1); break;
                case ':' when squareDepth == 0 && parenthesisDepth == 0 && braceDepth == 0:
                    imagePattern = entry[..i].Trim();
                    timingPattern = entry[(i + 1)..].Trim();
                    return;
            }
        }

        imagePattern = entry.Trim();
        timingPattern = null;
    }

    private static IReadOnlyList<string> SplitTopLevel(string value, char separator)
    {
        var result = new List<string>();
        var start = 0;
        var squareDepth = 0;
        var parenthesisDepth = 0;
        var braceDepth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '[': squareDepth++; break;
                case ']': squareDepth = Math.Max(0, squareDepth - 1); break;
                case '(': parenthesisDepth++; break;
                case ')': parenthesisDepth = Math.Max(0, parenthesisDepth - 1); break;
                case '{': braceDepth++; break;
                case '}': braceDepth = Math.Max(0, braceDepth - 1); break;
                default:
                    if (value[i] == separator && squareDepth == 0 && parenthesisDepth == 0 && braceDepth == 0)
                    {
                        result.Add(value[start..i]);
                        start = i + 1;
                    }
                    break;
            }
        }
        result.Add(value[start..]);
        return result;
    }

    private static int FindMatchingBracket(string value, int start)
    {
        var depth = 0;
        for (var i = start; i < value.Length; i++)
        {
            if (value[i] == '[')
                depth++;
            else if (value[i] == ']' && --depth == 0)
                return i;
        }
        return -1;
    }
}
