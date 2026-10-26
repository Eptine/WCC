using Wcc.Core.Projects;
using Wcc.Core.Wml;
using Wcc.Core.Campaigns;
using Wcc.Core.Units;

namespace Wcc.Core.Scenarios;

/// <summary>Immutable local playlist intent. This is a proposal, never write authority.</summary>
public sealed class WccScenarioInitialMusicEdit
{
    public WccScenarioInitialMusicEdit(IEnumerable<ScenarioInitialMusicTrack> tracks, bool makeFirstIntro,
        bool letIntroFinish, bool? shuffle = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        Tracks = Array.AsReadOnly(tracks.ToArray());
        MakeFirstIntro = makeFirstIntro;
        LetIntroFinish = letIntroFinish;
        Shuffle = shuffle;
    }
    public IReadOnlyList<ScenarioInitialMusicTrack> Tracks { get; }
    public bool MakeFirstIntro { get; }
    public bool LetIntroFinish { get; }
    public bool? Shuffle { get; }
    public bool AuthorizesPersistence => false;
}

/// <summary>Exact source-bound initial settings recipe; shared definitions are never edited.</summary>
public sealed class WccScenarioInitialSettingsEdit
{
    public WccScenarioInitialSettingsEdit(WmlStaticSourceRange scenarioRange,
        IEnumerable<WmlMacroExpansionDefinition>? definitions = null, string? sourceTextDomain = null,
        WccScenarioInitialMusicEdit? music = null, ScenarioInitialScheduleEdit? schedule = null,
        IEnumerable<WccScenarioSideFlagPresetEdit>? sideFlags = null,
        IEnumerable<WccScenarioEventSourceEdit>? events = null)
    {
        ScenarioRange = scenarioRange ?? throw new ArgumentNullException(nameof(scenarioRange));
        Definitions = Array.AsReadOnly((definitions ?? []).ToArray());
        SourceTextDomain = sourceTextDomain;
        Music = music;
        var flagEdits = (sideFlags ?? []).ToArray();
        if (flagEdits.Any(flag => flag is null || flag.SideOwner is null || flag.Definition is null))
            throw new ArgumentException("Flag recipes need complete side and definition bindings.", nameof(sideFlags));
        SideFlags = Array.AsReadOnly(flagEdits);
        var eventEdits = (events ?? []).ToArray();
        if (eventEdits.Any(edit => edit is null)) throw new ArgumentException("Event recipes cannot be null.", nameof(events));
        Events = Array.AsReadOnly(eventEdits);
        Schedule = schedule is null ? null : schedule with
        {
            TimeBlocks = schedule.TimeBlocks is null ? null : Array.AsReadOnly(schedule.TimeBlocks.ToArray()),
            Start = schedule.Start is null ? null : schedule.Start with
            {
                RandomSlotIndices = schedule.Start.RandomSlotIndices is null ? null
                    : Array.AsReadOnly(schedule.Start.RandomSlotIndices.ToArray())
            }
        };
    }
    public WmlStaticSourceRange ScenarioRange { get; }
    public IReadOnlyList<WmlMacroExpansionDefinition> Definitions { get; }
    public string? SourceTextDomain { get; }
    public WccScenarioInitialMusicEdit? Music { get; }
    public ScenarioInitialScheduleEdit? Schedule { get; }
    public IReadOnlyList<WccScenarioSideFlagPresetEdit> SideFlags { get; }
    public IReadOnlyList<WccScenarioEventSourceEdit> Events { get; }
    public bool AuthorizesPersistence => false;
    internal ScenarioInitialSettingsCapturedContext? CapturedContext { get; private init; }

    /// <summary>Attach exact full-evaluation planning evidence. Fresh Save review
    /// reconstructs its own evidence; this method never authorizes persistence.</summary>
    public WccScenarioInitialSettingsEdit WithCapturedProjection(WmlStaticScalarProjection projection)
    {
        var context = ScenarioInitialSettingsCapturedContext.Create(ScenarioRange, projection)
            ?? throw new InvalidOperationException("Initial settings need a complete exact captured scenario evaluation.");
        return new(ScenarioRange, Definitions, SourceTextDomain, Music, Schedule, SideFlags, Events)
        { CapturedContext = context };
    }

    /// <summary>Combine independent original-coordinate recipes through the same
    /// project composer used by Save. The returned text is only a working preview.</summary>
    public WccScenarioInitialSettingsComposeResult ComposePreview() => WccProjectSourceEditComposer.ComposeInitialSettings(this);
}

public sealed record WccScenarioInitialSettingsComposeResult(bool IsSuccess, string? CandidateSource = null,
    string? Diagnostic = null)
{ public bool AuthorizesPersistence => false; }

/// <summary>Explicit authored RAW intent at original source coordinates, separate from generated output.</summary>
public sealed record WccAuthoredSourcePatch(WmlStaticSourceRange Range, string ReplacementText)
{
    public bool AuthorizesPersistence => false;
}

/// <summary>One pristine-file proposal within a combined project save.</summary>
public sealed class WccProjectSourceEditRequest
{
    public WccProjectSourceEditRequest(WmlStaticSource source,
        IEnumerable<WccScenarioFieldEditProposal>? fields = null,
        WccScenarioInitialSettingsEdit? initialSettings = null,
        IEnumerable<WccAuthoredSourcePatch>? rawPatches = null,
        WccCampaignStructureEdit? campaignStructure = null,
        WccCampaignCreditsEdit? campaignCredits = null,
        IEnumerable<WccScenarioDifficultyFieldEdit>? difficultyFields = null,
        WccScenarioSideTopologyEdit? sideTopology = null,
        IEnumerable<WccScenarioLeaderAdditionEdit>? leaderAdditions = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Fields = Array.AsReadOnly((fields ?? []).ToArray());
        InitialSettings = initialSettings;
        RawPatches = Array.AsReadOnly((rawPatches ?? []).ToArray());
        CampaignStructure = campaignStructure;
        CampaignCredits = campaignCredits;
        DifficultyFields = Array.AsReadOnly((difficultyFields ?? []).ToArray());
        SideTopology = sideTopology;
        LeaderAdditions = Array.AsReadOnly((leaderAdditions ?? []).ToArray());
    }
    public WmlStaticSource Source { get; }
    public IReadOnlyList<WccScenarioFieldEditProposal> Fields { get; }
    public WccScenarioInitialSettingsEdit? InitialSettings { get; }
    public IReadOnlyList<WccAuthoredSourcePatch> RawPatches { get; }
    public WccCampaignStructureEdit? CampaignStructure { get; }
    public WccCampaignCreditsEdit? CampaignCredits { get; }
    public IReadOnlyList<WccScenarioDifficultyFieldEdit> DifficultyFields { get; }
    public WccScenarioSideTopologyEdit? SideTopology { get; }
    public IReadOnlyList<WccScenarioLeaderAdditionEdit> LeaderAdditions { get; }
    public WccCampaignLocalPresetStorageEdit? PresetStorage { get; init; }
    public WccCampaignPackageEdit? PackageEdit { get; init; }
    public WccScenarioCreationEdit? ScenarioCreation { get; init; }
    public WccUnitTypeCreationEdit? UnitTypeCreation { get; init; }
    public WccUnitTypeUpdateEdit? UnitTypeUpdate { get; init; }
    public WccScenarioStoryEdit? StoryEdit { get; init; }
    private IReadOnlyList<WccScenarioUnitInstanceEdit> _unitInstances = Array.Empty<WccScenarioUnitInstanceEdit>();
    public IReadOnlyList<WccScenarioUnitInstanceEdit> UnitInstances
    {
        get => _unitInstances;
        init => _unitInstances = Array.AsReadOnly((value ?? []).ToArray());
    }
    public bool AuthorizesPersistence => false;
}

public enum WccProjectSourceEditStatus
{
    Prepared, Succeeded, SucceededWithCleanupWarning, InvalidRequest, SessionUnavailable,
    ReadOnly, StaleSession, DiskRevisionRefused, ReviewRefused, ReviewChanged, InvalidTicket,
    TicketAlreadyConsumed, FileInUse, SaveRefused, RecoveryRequired
}

/// <summary>Per-file persistence diagnostics, including retained recovery artifacts.</summary>
public sealed record WccProjectSourceFileOutcome(string SourcePath, WccTextAtomicReplaceResult? Save,
    bool WasReplaced, string Diagnostic)
{
    public bool WasCreated { get; init; }
    public bool WasDeleted { get; internal init; }
    public bool WasWritten => WasReplaced || WasCreated || WasDeleted;
    public bool AuthorizesPersistence => false;
}

public sealed class WccProjectSourceEditResult
{
    internal WccProjectSourceEditResult(WccProjectSourceEditStatus status, string diagnostic,
        WccProjectSourceEditTicket? ticket = null, WccProjectSourceEditCandidate? candidate = null,
        IEnumerable<WccProjectSourceFileOutcome>? files = null)
    {
        Status = status;
        Diagnostic = diagnostic;
        Ticket = ticket;
        Candidate = candidate;
        Files = Array.AsReadOnly((files ?? []).ToArray());
    }
    public WccProjectSourceEditStatus Status { get; }
    public string Diagnostic { get; }
    public WccProjectSourceEditTicket? Ticket { get; }
    public WccProjectSourceEditCandidate? Candidate { get; }
    public IReadOnlyList<WccProjectSourceFileOutcome> Files { get; }
    public IReadOnlyList<string> RemovedDirectories { get; internal init; } = [];
    public bool IsPrepared => Status == WccProjectSourceEditStatus.Prepared;
    public bool IsSuccess => Status is WccProjectSourceEditStatus.Succeeded
        or WccProjectSourceEditStatus.SucceededWithCleanupWarning;
    public bool AuthorizesPersistence => false;
}

/// <summary>Diagnostics for one composed pristine file. The coordinator retains its own proof.</summary>
public sealed class WccProjectSourceFileCandidate
{
    internal WccProjectSourceFileCandidate(WmlStaticSource source, WmlStaticSource candidateSource,
        IEnumerable<WccScenarioFieldSourcePatch> patches)
    {
        Source = source;
        CandidateSource = candidateSource;
        Patches = Array.AsReadOnly(patches.ToArray());
    }
    public WmlStaticSource Source { get; }
    public WmlStaticSource CandidateSource { get; }
    public IReadOnlyList<WccScenarioFieldSourcePatch> Patches { get; }
    public bool AuthorizesPersistence => false;
    public WmlStaticSourceRange? MapRange(WmlStaticSourceRange range) =>
        new WccScenarioFieldSourceChange(new WccScenarioFieldBatchCandidate(Source, CandidateSource, [], Patches)).MapRange(range);
}

public sealed class WccProjectSourceEditCandidate
{
    internal WccProjectSourceEditCandidate(IEnumerable<WccProjectSourceFileCandidate> files) =>
        Files = Array.AsReadOnly(files.ToArray());
    public IReadOnlyList<WccProjectSourceFileCandidate> Files { get; }
    public IReadOnlyList<WmlStaticSource> SourceDeletes { get; internal init; } = [];
    public IReadOnlyList<WccPackageBinaryFileDeletion> BinaryDeletes { get; internal init; } = [];
    public IReadOnlyList<WmlStaticSource> SourceCreates { get; internal init; } = Array.Empty<WmlStaticSource>();
    public IReadOnlyList<WccContentBinaryCreation> BinaryCreates { get; internal init; } = Array.Empty<WccContentBinaryCreation>();
    public IReadOnlyList<WccContentBinaryReuseReceipt> BinaryReuses { get; internal init; } = Array.Empty<WccContentBinaryReuseReceipt>();
    public IReadOnlyList<string> DirectoryCreates { get; internal init; } = Array.Empty<string>();
    public IReadOnlyList<string> DirectoryRemovals { get; internal init; } = [];
    public bool AuthorizesPersistence => false;
    public WmlStaticSourceRange? MapRange(WmlStaticSourceRange range)
    {
        var file = Files.SingleOrDefault(file => string.Equals(file.Source.Path, range.Source.Path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        return file is null ? range : file.MapRange(range);
    }
}

/// <summary>Opaque authority owned by one coordinator for one attempted combined save.</summary>
public sealed class WccProjectSourceEditTicket
{
    private readonly object _owner;
    private int _consumed;
    internal WccProjectSourceEditTicket(object owner, object proof) { _owner = owner; Proof = proof; }
    public bool IsConsumed => Volatile.Read(ref _consumed) != 0;
    internal object Proof { get; }
    internal bool BelongsTo(object owner) => ReferenceEquals(_owner, owner);
    internal bool TryConsume() => Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
}
