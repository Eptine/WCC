using Wcc.Core.Campaigns;
using Wcc.Core.Projects;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public enum WccScenarioCreationMode { Empty, Guided }
public sealed record WccScenarioCreationSpec(WccScenarioCreationMode Mode, string Id, string Name,
    string? FileNameStem = null, int Turns = 20)
{
    /// <summary>Optional richer guided intent. Null retains the original two-side starter shape.</summary>
    public WccScenarioGuidedCreationOptions? Guided { get; init; }
}

public sealed record WccScenarioCreationMap(string FileName, string Text, bool RenameToScenario = false);
public sealed record WccScenarioCreationMusic(IReadOnlyList<ScenarioInitialMusicTrack> Tracks,
    bool MakeFirstIntro = false, bool LetIntroFinish = false, IReadOnlyList<WmlMacroExpansionDefinition>? Definitions = null);
public sealed record WccScenarioCreationSchedule(ScenarioInitialScheduleEdit Edit,
    IReadOnlyList<WmlMacroExpansionDefinition> Definitions);
public sealed record WccScenarioCreationSide(WccCampaignLocalPresetPayload? Payload = null,
    WccCampaignLocalPresetEntry? Preset = null, string? SaveIdOverride = null,
    IReadOnlyList<WccScenarioLeaderAddition>? Leaders = null)
{
    private IReadOnlyDictionary<string, string> _localValues = new Dictionary<string, string>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>> _localDifficultyValues =
        new Dictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> LocalValues
    {
        get => _localValues;
        init => _localValues = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            (value ?? throw new ArgumentNullException(nameof(value))).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }
    public IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>> LocalDifficultyValues
    {
        get => _localDifficultyValues;
        init => _localDifficultyValues = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(
            (value ?? throw new ArgumentNullException(nameof(value))).ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<WccScenarioDifficultyFieldValue>)Array.AsReadOnly(pair.Value.ToArray()), StringComparer.Ordinal));
    }
    public WccScenarioLeaderAddition? PresetLeaderCoordinates { get; init; }
}
public sealed record WccScenarioGuidedCreationOptions(IReadOnlyList<WccScenarioCreationSide> Sides,
    WccScenarioCreationMap? Map = null, WccScenarioCreationMusic? Music = null, WccScenarioCreationSchedule? Schedule = null)
{
    /// <summary>Explicit turns opt-in, with one literal value per fixed context. Null or empty keeps Turns shared.</summary>
    public IReadOnlyList<WccScenarioDifficultyFieldValue>? TurnsDifficultyValues { get; init; }
}

/// <summary>Canonical owned campaign source evidence for a new scenario, never filesystem authority.</summary>
public sealed class WccScenarioCreationSnapshot
{
    internal WccScenarioCreationSnapshot(WccCampaignStructureSnapshot structure, WmlCampaignEvaluationPlan? plan,
        bool canCreate, string diagnostic, string textDomain)
    { Structure = structure; Plan = plan; CanCreate = canCreate; Diagnostic = diagnostic; TextDomain = textDomain; }
    public WmlStaticSource Source => Structure.Source;
    public bool CanCreate { get; }
    public string Diagnostic { get; }
    public bool AuthorizesPersistence => false;
    internal WccCampaignStructureSnapshot Structure { get; }
    internal WmlCampaignEvaluationPlan? Plan { get; }
    internal string TextDomain { get; }
    internal IReadOnlyList<WmlStaticSource> ProjectSources { get; init; } = [];
}

public sealed class WccScenarioCreationEdit
{
    internal WccScenarioCreationEdit(WccScenarioCreationSnapshot snapshot, WccScenarioCreationSpec spec)
    { Snapshot = snapshot; Spec = spec with { }; }
    public WccScenarioCreationSnapshot Snapshot { get; }
    public WccScenarioCreationSpec Spec { get; }
    public WmlStaticSource Source => Snapshot.Source;
    public string ScenarioRelativePath => (WccCampaignChapterAuthoringPlanner.ScenarioDirectoryFor(Source, Snapshot.Plan!.CampaignDefine!, Snapshot.Structure.Authoring, Snapshot.ProjectSources) ?? "scenarios") + "/" + (Spec.FileNameStem ?? Spec.Id) + ".cfg";
    public string? MapRelativePath => Spec.Mode == WccScenarioCreationMode.Guided ? "maps/" +
        (Spec.Guided?.Map is { } map ? map.RenameToScenario
            ? WccScenarioCreationPlanner.MapFileNameForScenarioName(Spec.Name, Spec.FileNameStem ?? Spec.Id) : map.FileName
            : (Spec.FileNameStem ?? Spec.Id) + ".map") : null;
    public bool AuthorizesPersistence => false;
}

public static class WccScenarioCreationPlanner
{
    public static WccScenarioCreationSnapshot Read(WmlStaticSource mainSource,
        WccCampaignAuthoringMetadata? authoring, WmlCampaignEvaluationPlan? plan,
        IReadOnlyList<WmlStaticSource>? projectSources)
    {
        ArgumentNullException.ThrowIfNull(mainSource);
        var structure = WccCampaignStructurePlanner.Read(mainSource, authoring);
        if (!structure.CanEdit) return new(structure, plan, false, structure.Diagnostic, "");
        if (plan is null || plan.CampaignMainTextHash != mainSource.ContentHash || !string.Equals(plan.CampaignMainSourcePath,
                mainSource.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || plan.CampaignDefine != authoring!.CampaignDefine || plan.Contexts.Count is < 1 or > 16
            || plan.Contexts.Count != plan.Difficulties.Count || plan.Contexts.Count != structure.DifficultyRows.Count
            || !plan.Difficulties.Select(row => row.Define).SequenceEqual(authoring.FixedDifficultyDefines, StringComparer.Ordinal)
            || plan.Contexts.Select(context => context.SemanticKey).Distinct(StringComparer.Ordinal).Count() != plan.Contexts.Count
            || plan.Contexts.Where((context, index) => context.CampaignDefine != plan.CampaignDefine
                || context.DifficultyDefine != plan.Difficulties[index].Define).Any()
            || structure.PathRows.Count(row => row.Kind == WccCampaignPathKind.CfgInclude && row.Path == (WccCampaignChapterAuthoringPlanner.ScenarioDirectoryFor(mainSource, authoring!.CampaignDefine, authoring, projectSources) ?? "scenarios")) != 1)
            return new(structure, plan, false, "Scenario creation needs this exact canonical main, fixed contexts and one scenarios directory include.", "");
        var textDomain = WmlTextDomainCapture.BeforeOffset(mainSource.Text, structure.Layout!.Campaign.Start);
        if (WccCampaignCreationTokenPolicy.ValidateTextDomain(textDomain) is { } domainIssue)
            return new(structure, plan, false, domainIssue, "");
        return new(structure, plan, true, "Creates a new scenario through the campaign's checked Save. Empty is an incomplete scaffold.", textDomain!)
        { ProjectSources = Array.AsReadOnly((projectSources ?? new[] { mainSource }).ToArray()) };
    }

    public static WccScenarioCreationEdit CreateEdit(WccScenarioCreationSnapshot snapshot, WccScenarioCreationSpec spec)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(spec);
        var fresh = Read(snapshot.Source, snapshot.Structure.Authoring, snapshot.Plan, snapshot.ProjectSources);
        if (!snapshot.CanCreate || !fresh.CanCreate || fresh.TextDomain != snapshot.TextDomain)
            throw new InvalidOperationException(fresh.Diagnostic);
        if (!Enum.IsDefined(spec.Mode) || !Token(spec.Id) || !Token(spec.FileNameStem ?? spec.Id)
            || new[] { "_main", "_initial", "_final" }.Contains(spec.FileNameStem ?? spec.Id, StringComparer.OrdinalIgnoreCase)
            || WccCreationSourceWriter.ValidateText(spec.Name) is { } || spec.Name.Length > 4096
            || spec.Mode == WccScenarioCreationMode.Guided && string.IsNullOrWhiteSpace(spec.Name)
            || spec.Turns is < -1 or > 1_000_000)
            throw new ArgumentException("Use a portable unique scenario ID and filename, a valid name, and turns from -1 to 1000000.", nameof(spec));
        if (spec.Mode == WccScenarioCreationMode.Guided && snapshot.Structure.Authoring!.AddonId + "_Leader" == spec.Id + "_Enemy_Leader")
            throw new ArgumentException("Choose a scenario ID whose generated enemy leader differs from the campaign hero identity.", nameof(spec));
        if (spec.Guided is { } guided)
        {
            if (spec.Mode != WccScenarioCreationMode.Guided)
                throw new ArgumentException("Richer guided choices belong to Guided scenarios only.", nameof(spec));
            spec = spec with { Guided = CaptureGuided(guided, snapshot.Plan!) };
        }
        return new(snapshot, spec);
    }

    public static IReadOnlyList<WmlStaticSource> PlanSources(WccScenarioCreationEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var valid = CreateEdit(edit.Snapshot, edit.Spec); var root = Path.GetDirectoryName(valid.Source.Path)!;
        var authoring = valid.Snapshot.Structure.Authoring!;
        var text = valid.Spec.Mode == WccScenarioCreationMode.Empty ? WccScenarioCreationWriter.RenderEmpty(valid.Snapshot.TextDomain, valid.Spec.Id)
            : valid.Spec.Guided is { } guided ? WccScenarioCreationWriter.RenderGuided(valid.Snapshot.TextDomain, valid.Spec.Id, valid.Spec.Name,
                "~add-ons/" + authoring.AddonId + "/" + valid.MapRelativePath, valid.Snapshot.Plan!, valid.Spec.Turns, guided)
            : WccScenarioCreationWriter.RenderGuided(valid.Snapshot.TextDomain, valid.Spec.Id, valid.Spec.Name,
                "~add-ons/" + authoring.AddonId + "/" + valid.MapRelativePath, valid.Snapshot.Plan!.Difficulties.Select(row => row.Define).ToArray(),
                valid.Spec.Turns, authoring.AddonId + "_Leader", valid.Spec.Id + "_Enemy_Leader");
        var files = new List<WmlStaticSource> { new(Path.Combine(root, valid.ScenarioRelativePath.Replace('/', Path.DirectorySeparatorChar)), text) };
        if (valid.MapRelativePath is { } map) files.Add(new(Path.Combine(root, map.Replace('/', Path.DirectorySeparatorChar)),
            valid.Spec.Guided?.Map?.Text ?? (valid.Spec.Guided is { } choices ? WccScenarioCreationWriter.RenderDefaultMap(choices.Sides.Count) : WccScenarioCreationWriter.DefaultMap)));
        return Array.AsReadOnly(files.ToArray());
    }

    /// <summary>New scenarios extend the same captured chapter ownership ledger as package creation.</summary>
    public static WccCampaignPackageSourceReplacement? PlanOwnershipCandidate(WccScenarioCreationEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var authoring = edit.Snapshot.Structure.Authoring!;
        var snapshot = WccPackageAuthoringManifest.Read(edit.Source, authoring.AddonId, edit.Snapshot.ProjectSources);
        if (snapshot.Source is null) return null; // Older source-only scaffolds retain ordinary scenario creation.
        var chapter = snapshot.Document.Chapters.SingleOrDefault(item => item.CampaignDefine == edit.Snapshot.Plan!.CampaignDefine);
        if (chapter is null) throw new InvalidOperationException("The selected chapter has no captured portable ownership receipt.");
        var root = Path.GetDirectoryName(edit.Source.Path)!;
        var additions = PlanSources(edit).Select(source => WccCampaignChapterAuthoringPlanner.OwnedText(root, chapter.Id, source)).ToArray();
        if (additions.Any(file => snapshot.Document.OwnedFiles.Any(existing => string.Equals(existing.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("The new scenario overlaps an existing authored file receipt.");
        return new(snapshot.Source, WccPackageAuthoringManifest.Serialize(snapshot.Document with { OwnedFiles = snapshot.Document.OwnedFiles.Concat(additions).ToArray() }));
    }

    private static WccScenarioGuidedCreationOptions CaptureGuided(WccScenarioGuidedCreationOptions options, WmlCampaignEvaluationPlan plan)
    {
        if (options.Sides is null || options.Sides.Count is < 1 or > 32 || options.Sides.Any(side => side is null))
            throw new ArgumentException("The guided wizard supports one to 32 sides.");
        var sides = options.Sides.Select((side, index) => CaptureSide(side, index + 1, plan)).ToArray();
        if (options.Map is { } map)
        {
            if (!ValidMapFileName(map.FileName) || map.Text is null || map.Text.Length > 8 * 1024 * 1024)
                throw new ArgumentException("Select one bounded plain .map file with a portable basename.");
            ValidateMap(map.Text, sides.Length, sides, plan);
        }
        var music = options.Music is { } playlist ? playlist with
        {
            Tracks = Array.AsReadOnly((playlist.Tracks ?? throw new ArgumentException("Capture the playlist rows.")).ToArray()),
            Definitions = Array.AsReadOnly((playlist.Definitions ?? []).ToArray())
        } : null;
        if (music?.Tracks.Count > 256 || music?.Tracks.Any(track => track is null || track.SourceId is not null) == true)
            throw new ArgumentException("Use at most 256 new literal playlist rows.");
        if (options.Schedule is { } selectedCycle && (selectedCycle.Edit is null || selectedCycle.Definitions is null))
            throw new ArgumentException("Capture one complete schedule choice and its definition evidence.");
        var schedule = options.Schedule is { } cycle ? cycle with
        {
            Edit = cycle.Edit with { TimeBlocks = cycle.Edit.TimeBlocks is { } blocks ? Array.AsReadOnly(blocks.ToArray()) : null,
                Start = cycle.Edit.Start is { } start ? start with { RandomSlotIndices = start.RandomSlotIndices is { } indices ? Array.AsReadOnly(indices.ToArray()) : null } : null },
            Definitions = Array.AsReadOnly(cycle.Definitions.ToArray())
        } : null;
        if (schedule?.Edit.Kind == ScenarioInitialScheduleEditKind.Preserve || schedule?.Edit.TimeBlocks?.Count > 256)
            throw new ArgumentException("Choose a captured schedule preset, explicit custom times, or no schedule.");
        if (schedule?.Edit.Kind == ScenarioInitialScheduleEditKind.Custom && schedule.Edit.TimeBlocks is { } custom
            && custom.Any(block => block is null || block.Length > 256 * 1024 || !WmlSingleBlockValidator.ValidateLiteral(block, "time").IsValid))
            throw new ArgumentException("A custom creation cycle needs literal [time] blocks; macro and conditional time code belongs to a captured preset.");
        foreach (var definitions in new[] { music?.Definitions, schedule?.Definitions })
            if (definitions is not null && (definitions.Count > 4096 || definitions.Any(definition => definition is null)
                || definitions.Sum(definition => (long)definition.RawText.Length) > 8 * 1024 * 1024))
                throw new ArgumentException("The captured creation definition set exceeds its bounded size.");
        return options with { Sides = Array.AsReadOnly(sides), Music = music, Schedule = schedule,
            TurnsDifficultyValues = CaptureTurnsDifficultyValues(options.TurnsDifficultyValues, plan) };
    }

    /// <summary>Checks one detached wizard page through the same fixed-context checks used by CreateEdit.</summary>
    public static void ValidateSide(WccScenarioCreationSide side, int number, WmlCampaignEvaluationPlan plan, string mapText)
    {
        ArgumentNullException.ThrowIfNull(side); ArgumentNullException.ThrowIfNull(plan);
        if (number is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(number));
        var captured = CaptureSide(side, number, plan);
        var document = ReadPlayableMap(mapText);
        ValidateSidePlacement(document, captured, number, plan);
    }

    private static WccScenarioCreationSide CaptureSide(WccScenarioCreationSide side, int number, WmlCampaignEvaluationPlan plan)
    {
        try
        {
            if ((side.Payload is null) == (side.Preset is null) || side.Payload is { Role: not WccCampaignLocalPresetRole.Side }
                || side.Preset is { IsSupported: false } || side.Preset is { Role: not WccCampaignLocalPresetRole.Side }
                || side.Leaders is { Count: > 32 } || side.Leaders?.Any(leader => leader is null) == true)
                throw new ArgumentException("Each guided side needs literal Side fields or one supported saved Side preset.");
            if (side.SaveIdOverride is { } save && (string.IsNullOrWhiteSpace(save) || save.Length > 128 * 1024
                || WccScenarioFieldValueCodec.InferInputKind(save) != WccScenarioFieldValueInputKind.Literal))
                throw new ArgumentException("Use a nonempty literal carryover identity override.");
            var payload = side.Payload ?? side.Preset!.Payload!;
            if (side.LocalValues.Keys.Concat(side.LocalDifficultyValues.Keys).Any(key => key != "gold")
                || side.Preset is null && side.LocalValues.Count + side.LocalDifficultyValues.Count != 0)
                throw new ArgumentException("Only a selected Side preset's Gold can be overridden locally in this wizard.");
            var local = new WccCampaignLocalPresetPayload(WccCampaignLocalPresetRole.Side, side.LocalValues, side.LocalDifficultyValues);
            _ = WccCampaignLocalPresetCatalog.RenderDefinition(Guid.Parse("11111111111111111111111111111111"), "WCC_CREATION_LOCAL_SIDE", local, plan);
            if (side.PresetLeaderCoordinates is { } coordinates)
            {
                if (side.Preset is null || payload.Leaders.Count != 1 || coordinates.Preset is not null ||
                    coordinates.Values.Keys.Concat(coordinates.DifficultyValues.Keys).Any(key => key is not ("x" or "y")))
                    throw new ArgumentException("Local Side-preset coordinates require exactly one captured nested leader and only X and Y fields.");
                _ = WccCampaignLocalPresetCatalog.RenderDefinition(Guid.Parse("11111111111111111111111111111111"), "WCC_CREATION_LOCAL_LEADER",
                    new(WccCampaignLocalPresetRole.Leader, coordinates.Values, coordinates.DifficultyValues), plan);
            }
            // The shared renderer validates all fixed-context slots, including nested leaders.
            _ = WccCampaignLocalPresetCatalog.RenderDefinition(Guid.Parse("11111111111111111111111111111111"), "WCC_CREATION_LITERAL", payload, plan);
            var leaders = (side.Leaders ?? []).ToArray();
            foreach (var leader in leaders)
            {
                if (leader.DifficultyValues.Keys.Any(key => key is "id" or "side" or "save_id"))
                    throw new ArgumentException("A guided leader identity must remain shared.");
                _ = WccCampaignLocalPresetCatalog.RenderDefinition(Guid.Parse("11111111111111111111111111111111"), "WCC_CREATION_LITERAL",
                    new(WccCampaignLocalPresetRole.Leader, leader.Values, leader.DifficultyValues), plan);
            }
            foreach (var (leader, index) in payload.Leaders.Concat(leaders).Select((leader, index) => (leader, index)))
                ValidateLeaderType(leader, index + 1, plan);
            return side with { Leaders = Array.AsReadOnly(leaders), LocalValues = local.Values, LocalDifficultyValues = local.DifficultyValues };
        }
        catch (Exception refusal) when (refusal is ArgumentException or InvalidOperationException)
        { throw new ArgumentException("Side " + number + ": " + refusal.Message, refusal); }
    }

    internal static IReadOnlyList<WccScenarioDifficultyFieldValue>? CaptureTurnsDifficultyValues(
        IReadOnlyList<WccScenarioDifficultyFieldValue>? values, WmlCampaignEvaluationPlan plan)
    {
        if (values is null || values.Count == 0) return null;
        if (values.Count != plan.Contexts.Count || values.Any(value => value is null)
            || values.Select(value => value.ContextSemanticKey).Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ArgumentException("Difficulty-dependent turns need exactly one literal value for every fixed context.");
        var captured = plan.Contexts.Select(context =>
        {
            var matches = values.Where(value => value.ContextSemanticKey == context.SemanticKey
                && value.Define == context.DifficultyDefine).ToArray();
            if (matches.Length != 1 || !int.TryParse(matches[0].ValueText,
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var turns)
                || turns is < -1 or > 1_000_000)
                throw new ArgumentException("Difficulty-dependent turns need their exact fixed define and an integer from -1 to 1000000.");
            return matches[0] with { ValueText = turns.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }).ToArray();
        return Array.AsReadOnly(captured);
    }

    private static void ValidateLeaderType(WccScenarioLeaderAddition leader, int number, WmlCampaignEvaluationPlan plan)
    {
        if (leader.Preset?.Payload is { } preset)
            _ = WccCampaignLocalPresetCatalog.RenderDefinition(Guid.Parse("11111111111111111111111111111111"), "WCC_CREATION_LITERAL", preset, plan);
        foreach (var context in plan.Contexts)
        {
            var type = Value(leader.Values, leader.DifficultyValues, context) ?? (leader.Preset?.Payload is { } selected
                ? Value(selected.Values, selected.DifficultyValues, context) : null);
            if (string.IsNullOrWhiteSpace(type) || WccScenarioFieldValueCodec.InferInputKind(type) != WccScenarioFieldValueInputKind.Literal
                || WccScenarioFieldValueCodec.DecodeLiteral(type).Trim() is "" or "null")
                throw new ArgumentException("Nested leader " + number + " needs a nonempty literal unit type in every difficulty (missing in "
                    + context.DifficultyDefine + "). Choose a unit or a Leader preset that supplies it, or remove the leader.");
            var placement = Value(leader.Values, leader.DifficultyValues, context, "placement") ?? (leader.Preset?.Payload is { } selectedPlacement
                ? Value(selectedPlacement.Values, selectedPlacement.DifficultyValues, context, "placement") : null);
            if (!string.IsNullOrWhiteSpace(placement) && (WccScenarioFieldValueCodec.InferInputKind(placement) != WccScenarioFieldValueInputKind.Literal
                || !WccScenarioFieldValueCodec.DecodeLiteral(placement).Split(',').Select(value => value.Trim()).SequenceEqual(["map", "leader"], StringComparer.Ordinal)))
                throw new ArgumentException("Nested leader " + number + " (" + context.DifficultyDefine
                    + ") uses a custom Placement that this wizard cannot check against map starts and coordinates. Clear Placement or use map,leader; configure other placement after creation.");
        }
        static string? Value(IReadOnlyDictionary<string, string> shared,
            IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>> separate, WmlEvaluationContext context, string key = "type") =>
            separate.TryGetValue(key, out var slots) ? slots.Single(slot => slot.ContextSemanticKey == context.SemanticKey).ValueText
                : shared.GetValueOrDefault(key);
    }

    public static string MapFileNameForScenarioName(string name, string fallbackStem)
    {
        var words = (name ?? "").Select(character => char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_').ToArray();
        var stem = string.Join('_', new string(words).Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (stem.Length > 96) stem = stem[..96];
        return (Token(stem) ? stem : fallbackStem) + ".map";
    }

    private static bool ValidMapFileName(string name) => name is { Length: > 4 and <= 120 }
        && name.EndsWith(".map", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(" .map", StringComparison.Ordinal)
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ' ')
        && !name.StartsWith(' ') && !name.StartsWith('.') && Token(name.Split('.')[0].Replace('-', '_').Replace(' ', '_'));

    internal static void ValidateMap(string text, int sideCount,
        IReadOnlyList<WccScenarioCreationSide>? sides = null, WmlCampaignEvaluationPlan? plan = null)
    {
        var document = ReadPlayableMap(text);
        for (var number = 1; number <= sideCount; number++)
        {
            if (sides is not null && plan is not null) ValidateSidePlacement(document, sides[number - 1], number, plan);
            else if (!document.Rows.SelectMany(row => row).Any(cell => cell.StartingPosition == number))
                throw new ArgumentException("Side " + number + " needs a playable map start or complete on-map leader coordinates.");
        }
    }

    private static Maps.WesnothMapDocument ReadPlayableMap(string text)
    {
        if (text is null || text.Length > 8 * 1024 * 1024)
            throw new ArgumentException("Select one bounded plain .map file.");
        var document = Maps.WesnothMapParser.Parse(text, new Maps.WesnothMapParserOptions
            { DefaultBorderSize = 1, IgnoreLegacyBorderSize = true });
        if (document.Diagnostics.Count != 0 || document.Width <= 2 || document.Height <= 2
            || document.Rows.SelectMany(row => row).Any(cell => cell.TerrainCode.IndexOfAny(['{', '}', '$', '"', '<', '>', '=', '[', ']', '#']) >= 0))
            throw new ArgumentException("This wizard needs a rectangular plain map supported by the shared map parser; use the map editor for richer map syntax.");
        var starts = document.Rows.SelectMany(row => row).Where(cell => cell.StartingPosition is not null).ToArray();
        // Modern Wesnoth uses one border hex even without the deprecated map headers.
        if (starts.Any(cell => cell.StartingPosition <= 0 || cell.ColumnIndex <= 0 || cell.RowIndex <= 0
                || cell.ColumnIndex >= document.Width - 1 || cell.RowIndex >= document.Height - 1)
            || starts.Select(cell => cell.StartingPosition).Distinct().Count() != starts.Length)
            throw new ArgumentException("Map starting positions must have distinct positive side numbers on playable map hexes.");
        return document;
    }

    private static void ValidateSidePlacement(Maps.WesnothMapDocument document, WccScenarioCreationSide side,
        int number, WmlCampaignEvaluationPlan plan)
    {
        if (document.Rows.SelectMany(row => row).Any(cell => cell.StartingPosition == number)) return;
        var payload = side.Payload ?? side.Preset!.Payload!;
        var leaders = payload.Leaders.Concat(side.Leaders ?? []).ToArray();
        foreach (var context in plan.Contexts)
        {
            for (var index = 0; index < leaders.Length; index++)
            {
                var leader = leaders[index]; var preset = leader.Preset?.Payload;
                var fields = preset is null ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : Values(preset.Values, preset.DifficultyValues, context);
                foreach (var pair in Values(leader.Values, leader.DifficultyValues, context)) fields[pair.Key] = pair.Value;
                if (side.PresetLeaderCoordinates is { } coordinates && index == payload.Leaders.Count - 1)
                    foreach (var pair in Values(coordinates.Values, coordinates.DifficultyValues, context)) fields[pair.Key] = pair.Value;
                if (!int.TryParse(fields.GetValueOrDefault("x"), out var x) || !int.TryParse(fields.GetValueOrDefault("y"), out var y)
                    || x < 1 || y < 1 || x > document.Width - 2 || y > document.Height - 2)
                    throw new ArgumentException("Side " + number + ", nested leader " + (index + 1) + " (" + context.DifficultyDefine
                        + ") needs map start " + number + " or complete on-map X and Y coordinates. Set both coordinates with the map picker, choose a preset that supplies them, or remove the leader.");
            }
        }
        static Dictionary<string, string> Values(IReadOnlyDictionary<string, string> shared,
            IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>> separate, WmlEvaluationContext context) =>
            shared.Concat(separate.Select(pair => new KeyValuePair<string, string>(pair.Key,
                    pair.Value.Single(slot => slot.ContextSemanticKey == context.SemanticKey).ValueText!)))
                .ToDictionary(pair => pair.Key, pair => WccScenarioFieldValueCodec.DecodeLiteral(pair.Value), StringComparer.Ordinal);
    }

    private static bool Token(string? value) => value is { Length: > 0 and <= 96 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
        && !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(value.ToUpperInvariant(), StringComparer.Ordinal);
}
