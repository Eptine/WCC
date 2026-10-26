using System.Collections.Frozen;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>A track name and optional identity of an exact captured source row.</summary>
public sealed record ScenarioInitialMusicTrack(string Name, string? SourceId = null);

public enum ScenarioInitialMusicRepresentation { None, CoreMacros, Literal, Unsupported }

/// <summary>
/// Exact local initial-playlist source, not a complete simulation of music from
/// story, arbitrary event handlers, includes or runtime scripts.
/// Only this editor can construct the private source composition state.
/// </summary>
public sealed class ScenarioInitialMusicProjection
{
    internal ScenarioInitialMusicProjection(string source, IReadOnlyList<ScenarioInitialMusicTrack> tracks,
        bool intro, bool finish, bool? shuffle, string? defaultIntro,
        ScenarioInitialMusicRepresentation representation, string? problem,
        IReadOnlyList<ScenarioContentSourceEntry> entries, int insertionOffset, string indent, string newline,
        IReadOnlyDictionary<string, ScenarioInitialMusicTrackSource> trackSources,
        IReadOnlyDictionary<string, WmlMacroExpansionDefinition> trustedDefinitions,
        IReadOnlyList<ScenarioInitialMusicEventSource> eventSources)
    {
        SourceText = source;
        Tracks = Array.AsReadOnly(tracks.ToArray());
        HasIntro = intro;
        LetIntroFinish = finish;
        Shuffle = shuffle;
        DefaultIntroTrackName = defaultIntro;
        Representation = representation;
        Problem = problem;
        SourceEntries = Array.AsReadOnly(entries.ToArray());
        InsertionOffset = insertionOffset;
        Indent = indent;
        Newline = newline;
        TrackSources = trackSources.ToFrozenDictionary(StringComparer.Ordinal);
        TrustedDefinitions = trustedDefinitions.ToFrozenDictionary(StringComparer.Ordinal);
        EventSources = Array.AsReadOnly(eventSources.ToArray());
        DedicatedEventInvocationLines = eventSources.Select(entry => entry.InvocationLineIndex).ToFrozenSet();
    }

    public string SourceText { get; }
    public IReadOnlyList<ScenarioInitialMusicTrack> Tracks { get; }
    public bool HasIntro { get; }
    public bool LetIntroFinish { get; }
    public bool? Shuffle { get; }
    public string? DefaultIntroTrackName { get; }
    public ScenarioInitialMusicRepresentation Representation { get; }
    public bool CanEdit => Problem is null;
    public string? Problem { get; }
    /// <summary>A unique invocation in the complete captured evaluation was
    /// observed to supply initial music outside this local playlist. This is a
    /// read-only conflict, never inferred from a missing footprint or permission.</summary>
    public bool HasCapturedUnrepresentedMusic { get; internal init; }
    public IReadOnlyList<ScenarioContentSourceEntry> SourceEntries { get; }
    /// <summary>Zero-based original lines; use the exact event matcher, not this set alone.</summary>
    public IReadOnlySet<int> DedicatedEventInvocationLines { get; }

    internal int InsertionOffset { get; }
    internal string Indent { get; }
    internal string Newline { get; }
    internal IReadOnlyDictionary<string, ScenarioInitialMusicTrackSource> TrackSources { get; }
    internal IReadOnlyDictionary<string, WmlMacroExpansionDefinition> TrustedDefinitions { get; }
    internal IReadOnlyList<ScenarioInitialMusicEventSource> EventSources { get; }
    internal ScenarioInitialSettingsCapturedContext? CapturedContext { get; init; }
    /// <summary>Exact unchanged plumbing, never an edited or implicitly authorized region.</summary>
    internal IReadOnlyList<ScenarioContentSourceEntry> PreservedMusicEntries { get; init; } = [];

    /// <summary>Display filtering only: an exact verified dedicated macro invocation, never an arbitrary music event.</summary>
    public bool IsDedicatedGeneratedEvent(ScenarioEventProjectionEntry entry)
    {
        if (entry.SourceKind != ScenarioEventSourceKind.MacroGenerated || entry.IsNested ||
            entry.MacroProvenance is not { } provenance || provenance.Depth != 0 ||
            provenance.ParentExpansionId is not null) return false;
        return EventSources.Any(source => source.Matches(provenance));
    }
}

public sealed record ScenarioInitialMusicComposeResult(string? CandidateSource, string? Problem)
{
    public bool IsSuccess => CandidateSource is not null && Problem is null;
    /// <summary>Original-coordinate diagnostics produced by this composer, never write authority.</summary>
    public IReadOnlyList<WccScenarioFieldSourcePatch> SourcePatches { get; internal init; } = [];
}

internal sealed record ScenarioInitialMusicTrackSource(string Name, string? LiteralRaw,
    bool HasAncillaryAttributes, bool Shuffle);

internal sealed record ScenarioInitialMusicEventSource(string Name, string Invocation, int InvocationLineIndex,
    WmlMacroExpansionDefinition Definition)
{
    public bool Matches(WmlMacroExpansionProvenance provenance)
        => provenance.MacroName == Name && provenance.RawInvocation.Trim() == Invocation.Trim() &&
           provenance.InvocationLineIndex == InvocationLineIndex && provenance.SourcePath == Definition.SourcePath &&
           provenance.PackageName == Definition.PackageName && provenance.PackageRevisionId == Definition.PackageRevisionId &&
           provenance.DefinitionStartLine == Definition.SourceStartLine;
}
