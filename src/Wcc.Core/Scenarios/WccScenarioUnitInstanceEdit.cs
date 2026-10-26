using System.Collections.ObjectModel;
using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public enum WccScenarioUnitInstanceEditKind { Add, Replace, Remove }

/// <summary>Explicit initial [unit] literals. Its parent owns side membership; null difficulty means every declared difficulty.</summary>
public sealed class WccScenarioUnitInstanceSpec
{
    public WccScenarioUnitInstanceSpec(IEnumerable<KeyValuePair<string, string>> values, string? difficultyDefine = null)
    {
        var literals = CaptureValues(values, requirePlacement: true);
        if (difficultyDefine is not null && (difficultyDefine.Length == 0 || difficultyDefine.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')))
            throw new ArgumentException("Select all difficulties or one exact declared difficulty token.", nameof(difficultyDefine));
        Values = new ReadOnlyDictionary<string, string>(literals);
        DifficultyDefine = difficultyDefine;
    }
    internal static Dictionary<string, string> CaptureValues(IEnumerable<KeyValuePair<string, string>> values, bool requirePlacement)
    {
        ArgumentNullException.ThrowIfNull(values);
        var captured = values.Take(129).ToArray();
        if (captured.Length > 128 || captured.Any(pair => pair.Key is null || pair.Value is null) ||
            captured.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count() != captured.Length)
            throw new ArgumentException("Initial unit fields must have unique supported literal keys.", nameof(values));
        var literals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in captured)
        {
            if (!WccScenarioUnitInstancePlanner.IsEditableKey(pair.Key) ||
                !WccScenarioFieldValueCodec.TryGetDefinition("leader", pair.Key, out var definition) ||
                pair.Value.Length > 128 * 1024 || pair.Value.Contains('\0') ||
                WccScenarioFieldValueCodec.InferInputKind(pair.Value) != WccScenarioFieldValueInputKind.Literal)
                throw new ArgumentException("This initial unit field needs supported literal input: " + pair.Key, nameof(values));
            if (WccScenarioFieldValueCodec.ValidateInput(definition, pair.Value) is { } problem) throw new ArgumentException(problem, nameof(values));
            if (pair.Key == "id" && pair.Value.Length == 0) continue; // Absence requests the engine's generated identity.
            literals.Add(pair.Key, pair.Value);
        }
        foreach (var key in requirePlacement ? new[] { "type", "x", "y" } : new[] { "type" })
            if (!literals.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value) || value != value.Trim())
                throw new ArgumentException("An initial unit needs a nonempty literal type and complete numeric x/y; its ID is optional.", nameof(values));
        if (literals.TryGetValue("id", out var id) && (id != id.Trim() || id.Any(char.IsControl)))
            throw new ArgumentException("Enter one literal unit ID without surrounding whitespace or control characters, or leave it empty.", nameof(values));
        if (literals["type"] == "null" || literals["type"].Any(char.IsControl) || requirePlacement && (
            !int.TryParse(literals["x"], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x) || x <= 0 ||
            !int.TryParse(literals["y"], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var y) || y <= 0))
            throw new ArgumentException("Select one unit type and a positive numeric map coordinate pair. An optional unit ID must be unique.", nameof(values));
        return literals;
    }
    public IReadOnlyDictionary<string, string> Values { get; }
    public string? DifficultyDefine { get; }
    public WccCampaignLocalPresetEntry? Preset { get; init; }
    /// <summary>Null retains an existing trait; true/false explicitly adds/removes only the bounded local loyal trait.</summary>
    public bool? IsLoyal { get; init; }
    public bool AuthorizesPersistence => false;
}

public sealed class WccScenarioUnitInstanceEntry
{
    internal WccScenarioUnitInstanceEntry(WmlStaticSourceRange range, WmlStaticSourceRange unitRange,
        WmlStaticScalarOwner? owner, IReadOnlyDictionary<string, string> values, string? define,
        bool canEdit, string diagnostic, WccUnitInstanceLiteral? literal)
    { Range = range; UnitRange = unitRange; Owner = owner; Values = values; DifficultyDefine = define; CanEdit = canEdit; Diagnostic = diagnostic; Literal = literal; }
    public WmlStaticSourceRange Range { get; }
    public WmlStaticSourceRange UnitRange { get; }
    public WmlStaticScalarOwner? Owner { get; }
    public IReadOnlyDictionary<string, string> Values { get; }
    public string? DifficultyDefine { get; }
    /// <summary>True for the supported local trait, false for absence, null for an unproved/custom loyal trait.</summary>
    public bool? IsLoyal => Preset?.Payload?.UnitTemplate is { } template ? template.IsLoyal : Literal?.Loyalty.IsLoyal;
    public WccCampaignLocalPresetEntry? Preset { get; internal init; }
    public bool CanEdit { get; }
    public bool CanRemove => CanEdit;
    public string Diagnostic { get; }
    public bool AuthorizesPersistence => false;
    internal WccUnitInstanceLiteral? Literal { get; }
}

public sealed class WccScenarioUnitInstanceSnapshot
{
    internal WccScenarioUnitInstanceSnapshot(WccScenarioLeaderAdditionSnapshot side,
        IEnumerable<WccScenarioUnitInstanceEntry> entries, WmlCampaignEvaluationPlan? plan)
    { Source = side.Source; SideOwner = side.SideOwner; SideRange = side.SideRange; CanAdd = side.CanAdd; Diagnostic = side.Diagnostic;
      Entries = Array.AsReadOnly(entries.ToArray()); Plan = plan; InsertionOffset = side.InsertionOffset; Indent = side.Indent; }
    public WmlStaticSource Source { get; }
    public WmlStaticScalarOwner SideOwner { get; }
    public WmlStaticSourceRange? SideRange { get; }
    public IReadOnlyList<WccScenarioUnitInstanceEntry> Entries { get; }
    public bool CanAdd { get; }
    public string Diagnostic { get; }
    public bool AuthorizesPersistence => false;
    public bool SameOwner(WmlStaticScalarOwner side) => WccScenarioSideTopologyPlanner.SameOwner(SideOwner, side);
    internal WmlCampaignEvaluationPlan? Plan { get; }
    internal IReadOnlyList<WccCampaignLocalPresetEntry> Presets { get; init; } = [];
    internal int InsertionOffset { get; }
    internal string Indent { get; }
}

public sealed class WccScenarioUnitInstanceEdit
{
    internal WccScenarioUnitInstanceEdit(WccScenarioUnitInstanceEditKind kind, WccScenarioUnitInstanceSnapshot snapshot,
        WccScenarioUnitInstanceEntry? entry, WccScenarioUnitInstanceSpec? spec)
    { Kind = kind; Snapshot = snapshot; Entry = entry; Spec = spec; }
    public WccScenarioUnitInstanceEditKind Kind { get; }
    public WccScenarioUnitInstanceSnapshot Snapshot { get; }
    public WccScenarioUnitInstanceEntry? Entry { get; }
    public WccScenarioUnitInstanceSpec? Spec { get; }
    public WmlStaticSource Source => Snapshot.Source;
    public bool AuthorizesPersistence => false;
}

internal sealed record WccUnitInstanceField(string Key, string RawValue, int AssignmentStart, int AssignmentLength, int ValueStart, int ValueLength);
internal sealed record WccUnitInstanceLiteral(WmlStaticScalarOwner Owner, IReadOnlyDictionary<string, WccUnitInstanceField> Fields, int ClosingLineStart, string Indent)
{
    public WccUnitInstanceLoyalty Loyalty { get; init; } = new(false, null, null, "", false, null);
}

public static partial class WccScenarioUnitInstancePlanner
{
    private static readonly HashSet<string> EditableKeys = new(["id", "type", "name", "x", "y", "hitpoints", "max_hitpoints", "experience",
        "max_experience", "moves", "max_moves", "attacks_left", "max_attacks", "upkeep", "unrenamable", "random_traits", "random_gender",
        "generate_name", "gender", "variation", "role", "facing", "canrecruit"], StringComparer.Ordinal);
    public static bool IsEditableKey(string key) => EditableKeys.Contains(key);

    public static WccScenarioUnitInstanceSnapshot Read(WmlStaticSource source, WmlStaticScalarOwner side,
        WmlCampaignEvaluationPlan? plan, WmlStaticScalarProjection? capturedProjection = null) =>
        Read(source, side, plan, capturedProjection, null);

    public static WccScenarioUnitInstanceSnapshot Read(WmlStaticSource source, WmlStaticScalarOwner side,
        WmlCampaignEvaluationPlan? plan, WmlStaticScalarProjection? capturedProjection,
        IReadOnlyList<WccCampaignLocalPresetEntry>? presets)
    {
        var presetInventory = presets is null ? Array.Empty<WccCampaignLocalPresetEntry>() : presets.ToArray();
        var anchor = WccScenarioLeaderAdditionPlanner.Read(source, side, plan, capturedProjection);
        var entries = new List<WccScenarioUnitInstanceEntry>();
        // The strict side anchor supplies edit authority. A separate lexical range
        // can still inventory unsupported literal units for RAW navigation/display.
        // It never upgrades the anchor's CanAdd/SideRange or a row's edit capability.
        var displayRange = anchor.SideRange ?? FindDisplaySideRange(source, side);
        if (displayRange is { } sideRange)
        {
            var structure = WmlAuthoredStructureScanner.Scan(sideRange.Text);
            var root = WmlProjectionTreeParser.Parse(structure.MaskedText).Roots.Single();
            foreach (var child in root.Children.Where(child => child.TagName == "unit").Take(4097))
            {
                var first = structure.Lines[child.StartLine]; var last = structure.Lines[child.EndLineExclusive - 1];
                var unit = new WmlStaticSourceRange(source, sideRange.StartOffset + first.StartOffset, last.ContentEndOffset - first.StartOffset);
                var owner = side.DirectChildren.SingleOrDefault(owner => owner.TagName == "unit" && owner.OpeningOrigin is { } opening
                    && WccScenarioFieldRouteAnalyzer.SameSource(opening.Source, source) && opening.StartOffset >= unit.StartOffset && opening.StartOffset < unit.StartOffset + unit.Length);
                var scopeProblem = CaptureScope(source, sideRange, unit, plan, out var target, out var define);
                var problem = CaptureUnit(unit.Text, out var literal, out var values);
                WccCampaignLocalPresetEntry? preset = null;
                if (problem is not null && TryCapturePresetUnit(unit, owner, plan, capturedProjection, presetInventory,
                    out var boundLiteral, out var boundValues, out var boundPreset))
                { literal = boundLiteral; values = boundValues; preset = boundPreset; problem = null; }
                if (values.Count == 0) values = ReadUnitHints(unit.Text, owner);
                if (owner?.IsAmended == true || owner?.HasUnresolvedContributions == true)
                    problem = "An amended or unresolved effective initial unit retains RAW editing.";
                if (problem is null) problem = scopeProblem;
                var canEdit = anchor.CanAdd && problem is null;
                entries.Add(new(target, unit, owner, values, define, canEdit,
                    problem ?? (!anchor.CanAdd ? anchor.Diagnostic : "Edit this exact initial unit; unrelated fields and children are retained."), literal) { Preset = preset });
            }
            foreach (var owner in side.DirectChildren.Where(owner => owner.TagName == "unit" && !entries.Any(entry => ReferenceEquals(entry.Owner, owner))).Take(Math.Max(0, 4097 - entries.Count)))
                if (owner.OpeningOrigin is { } opening && owner.ClosingOrigin is { } closing &&
                    WccScenarioFieldRouteAnalyzer.SameSource(opening.Source, closing.Source) && closing.StartOffset >= opening.StartOffset)
                {
                    var range = new WmlStaticSourceRange(opening.Source, opening.StartOffset, closing.StartOffset + closing.Length - opening.StartOffset);
                    var values = new ReadOnlyDictionary<string, string>(owner.Fields.Where(pair => IsEditableKey(pair.Key) && pair.Value.IsKnown)
                        .ToDictionary(pair => pair.Key, pair => WccScenarioFieldValueCodec.DecodeLiteral(pair.Value.ValueText), StringComparer.Ordinal));
                    entries.Add(new(range, range, owner, values, null, false, "Generated, shared or amended unit source retains RAW editing.", null));
                }
        }
        return new(anchor, entries, plan) { Presets = Array.AsReadOnly(presetInventory) };
    }

    public static WccScenarioUnitInstanceEdit CreateAdd(WccScenarioUnitInstanceSnapshot snapshot, WccScenarioUnitInstanceSpec spec) => Create(WccScenarioUnitInstanceEditKind.Add, snapshot, null, spec);
    public static WccScenarioUnitInstanceEdit CreateReplace(WccScenarioUnitInstanceSnapshot snapshot, WccScenarioUnitInstanceEntry entry, WccScenarioUnitInstanceSpec spec) => Create(WccScenarioUnitInstanceEditKind.Replace, snapshot, entry, spec);
    public static WccScenarioUnitInstanceEdit CreateRemove(WccScenarioUnitInstanceSnapshot snapshot, WccScenarioUnitInstanceEntry entry) => Create(WccScenarioUnitInstanceEditKind.Remove, snapshot, entry, null);

    private static WccScenarioUnitInstanceEdit Create(WccScenarioUnitInstanceEditKind kind, WccScenarioUnitInstanceSnapshot snapshot,
        WccScenarioUnitInstanceEntry? entry, WccScenarioUnitInstanceSpec? spec)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.CanAdd || snapshot.SideRange is null || snapshot.Entries.Count > 4096) throw new InvalidOperationException(snapshot.Diagnostic);
        if (kind != WccScenarioUnitInstanceEditKind.Add && (entry is null || !entry.CanEdit || !snapshot.Entries.Any(item => ReferenceEquals(item, entry))))
            throw new InvalidOperationException("Choose one exact editable captured initial unit.");
        if (kind != WccScenarioUnitInstanceEditKind.Remove)
        {
            ArgumentNullException.ThrowIfNull(spec);
            ValidateScope(snapshot.Plan, spec.DifficultyDefine);
            if (spec.Preset is { } preset)
            {
                if (!preset.IsSupported || preset.Role != WccCampaignLocalPresetRole.Unit || !preset.IsAvailableIn(snapshot.Plan)
                    || preset.Payload?.UnitTemplate is not { } template || kind == WccScenarioUnitInstanceEditKind.Replace &&
                    (entry?.Preset is null || !SamePreset(entry.Preset, preset)))
                    throw new InvalidOperationException("Choose one saved unit preset available in this campaign; an existing instance keeps that exact preset or uses Custom.");
                if (template.Values.Any(pair => !spec.Values.ContainsKey(pair.Key)) || spec.IsLoyal != template.IsLoyal)
                    throw new InvalidOperationException("Keep the preset fields and loyal trait, or choose Custom to materialize independent unit settings.");
                if (spec.Values.GetValueOrDefault("id") != template.Values["id"])
                    throw new InvalidOperationException("A preset character keeps its saved unit ID. Choose Custom for a separate character.");
            }
            if (entry is not null && entry.Preset is null) ValidateLoyaltyChange(entry.Literal!, spec);
        }
        return new(kind, snapshot, entry, spec);
    }
}
