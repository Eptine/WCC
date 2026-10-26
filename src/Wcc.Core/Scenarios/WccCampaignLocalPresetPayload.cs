using System.Collections.ObjectModel;
using System.Text;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public enum WccCampaignLocalPresetRole { Leader, Side, Unit, Event }

/// <summary>Bounded reusable Side/Leader/Unit/Event intent; never source-write authority.</summary>
public sealed class WccCampaignLocalPresetPayload
{
    public WccCampaignLocalPresetPayload(WccCampaignLocalPresetRole role,
        IEnumerable<KeyValuePair<string, string>> values,
        IEnumerable<KeyValuePair<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>>? difficultyValues = null,
        IEnumerable<WccScenarioLeaderAddition>? leaders = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (role is not (WccCampaignLocalPresetRole.Leader or WccCampaignLocalPresetRole.Side)) throw new ArgumentException("Use FromUnit or FromEvent for unit and event presets.", nameof(role));
        Role = role;
        var shared = values.ToArray(); var separate = (difficultyValues ?? []).ToArray(); var children = (leaders ?? []).ToArray();
        if (shared.Length + separate.Length > 256 || children.Length > 256
            || shared.Any(pair => pair.Key is null || pair.Value is null)
            || separate.Any(pair => pair.Key is null || pair.Value is null || pair.Value.Count is < 1 or > 16
                || pair.Value.Any(slot => slot is null || slot.ValueText is null))
            || shared.Select(pair => pair.Key).Concat(separate.Select(pair => pair.Key)).Distinct(StringComparer.Ordinal).Count() != shared.Length + separate.Length
            || children.Any(child => child is null || child.Preset is not null) || role == WccCampaignLocalPresetRole.Leader && children.Length != 0)
            throw new ArgumentException("Use unique supported literal fields and only nested leaders in a Side preset.");
        var encoded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in shared) encoded.Add(pair.Key, Encode(role, pair.Key, pair.Value));
        var capturedSlots = new Dictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(StringComparer.Ordinal);
        var encodedSlots = new Dictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(StringComparer.Ordinal);
        foreach (var pair in separate)
        {
            if (pair.Key is "id" or "side" or "save_id") throw new ArgumentException("Preset identity fields cannot vary by difficulty.");
            capturedSlots.Add(pair.Key, Array.AsReadOnly(pair.Value.ToArray()));
            encodedSlots.Add(pair.Key, Array.AsReadOnly(pair.Value.Select(slot => slot with { ValueText = Encode(role, pair.Key, slot.ValueText!) }).ToArray()));
        }
        if (children.Any(child => child.DifficultyValues.Keys.Any(key => key is "id" or "side" or "save_id")))
            throw new ArgumentException("Nested leader identity fields cannot vary by difficulty.");
        Values = new ReadOnlyDictionary<string, string>(shared.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        DifficultyValues = new ReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(capturedSlots);
        EncodedValues = new ReadOnlyDictionary<string, string>(encoded);
        EncodedDifficultyValues = new ReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(encodedSlots);
        Leaders = Array.AsReadOnly(children);
        if (EncodedValues.Sum(pair => (long)pair.Value.Length) + EncodedDifficultyValues.Values.Sum(slots => slots.Sum(slot => (long)slot.ValueText!.Length))
            + Leaders.Sum(leader => leader.EncodedSize) > 2 * 1024 * 1024)
            throw new ArgumentException("The preset literal payload exceeds its bounded size.");
    }

    private WccCampaignLocalPresetPayload(WccScenarioUnitTemplate? unit, string? eventText, WccScenarioEventGraphSeed? eventSeed)
    {
        Role = unit is null ? WccCampaignLocalPresetRole.Event : WccCampaignLocalPresetRole.Unit;
        UnitTemplate = unit; EventText = eventText; EventSeed = eventSeed;
        var fields = unit?.Values ?? (eventSeed?.Graph.EventId is { } id
            ? new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = id } : new Dictionary<string, string>(StringComparer.Ordinal));
        Values = new ReadOnlyDictionary<string, string>(fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        EncodedValues = new ReadOnlyDictionary<string, string>(unit is null ? new Dictionary<string, string>(StringComparer.Ordinal)
            : fields.ToDictionary(pair => pair.Key, pair => Encode(Role, pair.Key, pair.Value), StringComparer.Ordinal));
        if (EncodedValues.Values.Sum(value => (long)value.Length) > 2 * 1024 * 1024) throw new ArgumentException("The unit template exceeds its bounded payload size.");
        DifficultyValues = new ReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>(new Dictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>>());
        EncodedDifficultyValues = DifficultyValues; Leaders = [];
    }
    public static WccCampaignLocalPresetPayload FromUnit(WccScenarioUnitTemplate template) =>
        new(template ?? throw new ArgumentNullException(nameof(template)), null, null);
    public static WccCampaignLocalPresetPayload FromEvent(string eventWml) => FromEvent(eventWml, []);
    public static WccCampaignLocalPresetPayload FromEvent(string eventWml, IReadOnlyList<string> declaredDifficultyDefines) =>
        FromEvent(eventWml, declaredDifficultyDefines, null);
    public static WccCampaignLocalPresetPayload FromEvent(string eventWml, IReadOnlyList<string> declaredDifficultyDefines,
        IReadOnlyList<WccCampaignLocalPresetEntry>? unitPresets)
    {
        ArgumentNullException.ThrowIfNull(declaredDifficultyDefines);
        var captured = WccScenarioEventGraphEditor.Capture(eventWml, declaredDifficultyDefines, unitPresets);
        if (!captured.CanEdit) throw new ArgumentException(captured.Diagnostic, nameof(eventWml));
        return new(null, eventWml, captured.Seed!);
    }
    public WccScenarioUnitTemplate? UnitTemplate { get; }
    public string? EventText { get; }
    public WccScenarioEventGraphSeed? EventSeed { get; }
    internal IReadOnlyList<WccCampaignLocalPresetEntry> EventUnitPresets => EventSeed is null ? [] :
        Array.AsReadOnly(EventSeed.Graph.Actions.Where(action => action.Unit?.Preset is not null)
            .Select(action => action.Unit!.Preset!).Distinct().ToArray());
    public WccCampaignLocalPresetRole Role { get; }
    public IReadOnlyDictionary<string, string> Values { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>> DifficultyValues { get; }
    public IReadOnlyList<WccScenarioLeaderAddition> Leaders { get; }
    public bool AuthorizesPersistence => false;
    internal IReadOnlyDictionary<string, string> EncodedValues { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>> EncodedDifficultyValues { get; }

    internal static bool TryField(WccCampaignLocalPresetRole role, string key, out ScenarioInitialFieldDefinition? field) =>
        ScenarioInitialFieldCatalog.TryGet(role is WccCampaignLocalPresetRole.Leader or WccCampaignLocalPresetRole.Unit ? "leader" : "side", key, out field)
        && field.IsEditable && !(role == WccCampaignLocalPresetRole.Side && key == "side")
        && (role != WccCampaignLocalPresetRole.Unit || WccScenarioUnitInstancePlanner.IsEditableKey(key) && key is not ("x" or "y"));

    private static string Encode(WccCampaignLocalPresetRole role, string key, string value)
    {
        if (!TryField(role, key, out var field)) throw new ArgumentException("This preset field is unsupported: " + key);
        if (value.Length > 128 * 1024 || WccScenarioFieldValueCodec.InferInputKind(value) != WccScenarioFieldValueInputKind.Literal
            || WccScenarioFieldValueCodec.ValidateInput(field!, value) is { })
            throw new ArgumentException("Preset fields currently require supported literal values: " + key);
        return WccScenarioFieldValueCodec.EncodeLiteral(field!, value);
    }
}

public static partial class WccCampaignLocalPresetCatalog
{
    public static string Marker(WccCampaignLocalPresetRole role, Guid id)
    {
        if (!Enum.IsDefined(role) || id == Guid.Empty) throw new ArgumentException("The preset needs a supported role and nonempty durable ID.");
        return "# wcc-preset: v1 role=" + Kind(role) + " id=" + id.ToString("N");
    }

    /// <summary>Ordinary portable WML text only. The existing checked transaction must prove its location and activation.</summary>
    public static string RenderDefinition(Guid id, string name, WccCampaignLocalPresetPayload payload, WmlCampaignEvaluationPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!ValidName(name)) throw new ArgumentException("Use a portable macro name beginning with an ASCII letter or underscore.", nameof(name));
        ValidateSlots(payload.DifficultyValues.Values.Concat(payload.Leaders.SelectMany(leader => leader.DifficultyValues.Values)), plan);
        ValidateEventDifficultyRegions(payload, plan);
        var text = new StringBuilder("#define " + name + "\n    " + Marker(payload.Role, id) + "\n");
        if (payload.EventText is { } eventText) return text.Append(eventText).Append(eventText.EndsWith('\n') ? "" : "\n").Append("#enddef\n").ToString();
        AppendFields(text, "    ", payload.EncodedValues, payload.EncodedDifficultyValues, plan);
        if (payload.UnitTemplate?.IsLoyal == true) text.Append(WccScenarioUnitInstancePlanner.RenderPresetLoyalty("    ", "\n"));
        foreach (var leader in payload.Leaders)
        {
            text.Append("    [leader]\n");
            AppendFields(text, "        ", leader.EncodedValues, leader.EncodedDifficultyValues, plan);
            text.Append("    [/leader]\n");
        }
        return text.Append("#enddef\n").ToString();
    }

    internal static string Kind(WccCampaignLocalPresetRole role) => role switch
    {
        WccCampaignLocalPresetRole.Leader => "leader", WccCampaignLocalPresetRole.Side => "side",
        WccCampaignLocalPresetRole.Unit => "unit", WccCampaignLocalPresetRole.Event => "event",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
    internal static bool TryRole(string kind, out WccCampaignLocalPresetRole role)
    {
        role = kind switch { "leader" => WccCampaignLocalPresetRole.Leader, "side" => WccCampaignLocalPresetRole.Side,
            "unit" => WccCampaignLocalPresetRole.Unit, "event" => WccCampaignLocalPresetRole.Event, _ => (WccCampaignLocalPresetRole)(-1) };
        return Enum.IsDefined(role);
    }
    private static void AppendFields(StringBuilder text, string indent, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, IReadOnlyList<WccScenarioDifficultyFieldValue>> separate, WmlCampaignEvaluationPlan? plan)
    {
        foreach (var pair in values) text.Append(indent).Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
        if (separate.Count == 0) return;
        foreach (var context in plan!.Contexts)
        {
            text.Append("#ifdef ").Append(context.DifficultyDefine).Append('\n');
            foreach (var pair in separate) text.Append(indent).Append(pair.Key).Append('=')
                .Append(pair.Value.Single(slot => slot.ContextSemanticKey == context.SemanticKey).ValueText).Append('\n');
            text.Append("#endif\n");
        }
    }

    internal static bool ValidName(string? name) => name is { Length: > 0 and <= 128 } && (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static void ValidateEventDifficultyRegions(WccCampaignLocalPresetPayload payload, WmlCampaignEvaluationPlan? plan)
    {
        if (payload.EventSeed?.Graph is not { } graph) return;
        if (graph.DifficultyRegions.Count > 0) RequireFixedPlan(plan);
        var rendered = WccScenarioEventGraphEditor.Render(graph,
            declaredDifficultyDefines: plan?.Difficulties.Select(row => row.Define).ToArray() ?? [], unitPresets: payload.EventUnitPresets);
        if (!rendered.IsSuccess) throw new InvalidOperationException(rendered.Diagnostic);
    }

    private static void ValidateSlots(IEnumerable<IReadOnlyList<WccScenarioDifficultyFieldValue>> slots, WmlCampaignEvaluationPlan? plan)
    {
        var fields = slots.ToArray(); if (fields.Length == 0) return;
        RequireFixedPlan(plan);
        foreach (var field in fields)
            if (field.Count != plan!.Contexts.Count || field.Select(value => value.Define).Distinct(StringComparer.Ordinal).Count() != field.Count
                || field.Select(value => value.ContextSemanticKey).Distinct(StringComparer.Ordinal).Count() != field.Count
                || !plan.Contexts.All(context => field.Any(value => value.Define == context.DifficultyDefine && value.ContextSemanticKey == context.SemanticKey)))
                throw new InvalidOperationException("Preset difficulty fields need one exact literal in every fixed declared context.");
    }

    private static void RequireFixedPlan(WmlCampaignEvaluationPlan? plan)
    {
        if (plan is null || string.IsNullOrWhiteSpace(plan.CampaignDefine) || plan.Contexts.Count is < 1 or > 16
            || plan.Contexts.Count != plan.Difficulties.Count
            || plan.Difficulties.Any(row => !ValidName(row.Define))
            || plan.Difficulties.Select(row => row.Define).Distinct(StringComparer.Ordinal).Count() != plan.Contexts.Count
            || plan.Contexts.Select(context => context.SemanticKey).Distinct(StringComparer.Ordinal).Count() != plan.Contexts.Count
            || plan.Contexts.Where((context, index) => context.CampaignDefine != plan.CampaignDefine || context.DifficultyDefine != plan.Difficulties[index].Define).Any())
            throw new InvalidOperationException("Preset branches require the complete unique fixed campaign difficulty plan.");
    }
}
