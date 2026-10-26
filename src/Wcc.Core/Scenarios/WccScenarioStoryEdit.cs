using System.Collections.ObjectModel;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public sealed record WccScenarioStoryContext(string Define, string SemanticKey, string DisplayName);

/// <summary>Detached UI intent; CreateEdit freezes every nested collection before review.</summary>
public sealed record WccScenarioStoryFieldSpec
{
    public bool IsTouched { get; init; }
    public WccScenarioDifficultyValueMode Mode { get; init; } = WccScenarioDifficultyValueMode.Shared;
    public string? SharedValue { get; init; }
    public IReadOnlyDictionary<string, string?> ContextValues { get; init; } = new Dictionary<string, string?>();
}

public sealed record WccScenarioStoryChildSpec
{
    /// <summary>A supported Core travel invocation is retained as a macro, rather than flattened images.</summary>
    public string? TravelMacroName { get; init; }
    public string? OriginalChildId { get; init; }
    public string? NewChildId { get; init; }
    public IReadOnlyList<string>? AvailableDefines { get; init; }
    public IReadOnlyDictionary<string, int>? OrderByDefine { get; init; }
    public IReadOnlyDictionary<string, WccScenarioStoryFieldSpec> Fields { get; init; } = new Dictionary<string, WccScenarioStoryFieldSpec>();
}

public sealed record WccScenarioStoryPartSpec
{
    public string? OriginalPartId { get; init; }
    public string? NewPartId { get; init; }
    public IReadOnlyList<string>? AvailableDefines { get; init; }
    public IReadOnlyDictionary<string, int>? OrderByDefine { get; init; }
    public IReadOnlyDictionary<string, WccScenarioStoryFieldSpec> Fields { get; init; } = new Dictionary<string, WccScenarioStoryFieldSpec>();
    public IReadOnlyList<WccScenarioStoryChildSpec> Images { get; init; } = [];
    public IReadOnlyList<WccScenarioStoryChildSpec> BackgroundLayers { get; init; } = [];
}

public sealed class WccScenarioStoryFieldSnapshot
{
    internal WccScenarioStoryFieldSnapshot(string key, IReadOnlyDictionary<string, string?> raw, bool canEdit, string? diagnostic,
        IReadOnlyDictionary<string, string?>? decodedValues = null)
    {
        Key = key; RawValues = Freeze(raw); CanEdit = canEdit; Diagnostic = diagnostic;
        Values = Freeze(decodedValues ?? raw.ToDictionary(item => item.Key, item => item.Value is null ? null : key is "story" or "title" && WccWmlPlainTextCodec.TryReadLiteral(item.Value, out var text, out _)
            ? text : WccScenarioFieldValueCodec.DecodeLiteral(item.Value), StringComparer.Ordinal));
    }
    public string Key { get; }
    public IReadOnlyDictionary<string, string?> Values { get; }
    public IReadOnlyDictionary<string, string?> RawValues { get; }
    public bool CanEdit { get; }
    public string? Diagnostic { get; }
    public string? OriginLabel { get; internal init; }
    internal static IReadOnlyDictionary<string, T> Freeze<T>(IReadOnlyDictionary<string, T> values)
        => new ReadOnlyDictionary<string, T>(values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
}

public sealed class WccScenarioStoryChildSnapshot
{
    internal WccScenarioStoryChildSnapshot(string id, string tagName, string raw,
        IEnumerable<string> available, IReadOnlyDictionary<string, int> orders, IReadOnlyDictionary<string, WccScenarioStoryFieldSnapshot> fields,
        IReadOnlyDictionary<string, WccStoryLiteralNode> nodes, bool canEdit, string? diagnostic)
    { Id = id; TagName = tagName; RawText = raw; AvailableDefines = Array.AsReadOnly(available.ToArray());
        OrderByDefine = WccScenarioStoryFieldSnapshot.Freeze(orders); Fields = WccScenarioStoryFieldSnapshot.Freeze(fields); Nodes = WccScenarioStoryFieldSnapshot.Freeze(nodes); CanEdit = canEdit; Diagnostic = diagnostic; }
    public string Id { get; }
    public string TagName { get; }
    public IReadOnlyDictionary<string, int> OrderByDefine { get; }
    /// <summary>Logical authored order, separately from a macro's native preview image order.</summary>
    public IReadOnlyDictionary<string, int>? AuthoringOrderByDefine { get; internal init; }
    public string RawText { get; }
    public IReadOnlyList<string> AvailableDefines { get; }
    public IReadOnlyDictionary<string, WccScenarioStoryFieldSnapshot> Fields { get; }
    public bool CanEdit { get; }
    public string? Diagnostic { get; }
    public bool IsExpandedPreview { get; internal init; }
    public string? OriginLabel { get; internal init; }
    public string? MacroName { get; internal init; }
    public string? MacroInvocationText { get; internal init; }
    internal IReadOnlyDictionary<string, WccStoryLiteralNode> Nodes { get; }
}

public sealed class WccScenarioStoryPartSnapshot
{
    internal WccScenarioStoryPartSnapshot(string id, string raw, IEnumerable<string> available,
        IReadOnlyDictionary<string, int> orders, IReadOnlyDictionary<string, WccScenarioStoryFieldSnapshot> fields,
        IEnumerable<WccScenarioStoryChildSnapshot> images, IEnumerable<WccScenarioStoryChildSnapshot> layers,
        IReadOnlyDictionary<string, WccStoryLiteralNode> nodes, bool canEdit, string? diagnostic)
    { Id = id; RawText = raw; AvailableDefines = Array.AsReadOnly(available.ToArray()); OrderByDefine = WccScenarioStoryFieldSnapshot.Freeze(orders);
        Fields = WccScenarioStoryFieldSnapshot.Freeze(fields); Images = Array.AsReadOnly(images.ToArray()); BackgroundLayers = Array.AsReadOnly(layers.ToArray());
        Nodes = WccScenarioStoryFieldSnapshot.Freeze(nodes); CanEdit = canEdit; Diagnostic = diagnostic; }
    public string Id { get; }
    public string RawText { get; }
    public IReadOnlyList<string> AvailableDefines { get; }
    public IReadOnlyDictionary<string, int> OrderByDefine { get; }
    public IReadOnlyDictionary<string, WccScenarioStoryFieldSnapshot> Fields { get; }
    public IReadOnlyDictionary<string, int>? AuthoringOrderByDefine { get; internal init; }
    public IReadOnlyList<WccScenarioStoryChildSnapshot> Images { get; }
    public IReadOnlyList<WccScenarioStoryChildSnapshot> BackgroundLayers { get; }
    public bool CanEdit { get; }
    public string? Diagnostic { get; }
    public bool IsExpandedPreview { get; internal init; }
    public string? OriginLabel { get; internal init; }
    internal IReadOnlyDictionary<string, WccStoryLiteralNode> Nodes { get; }
}

public sealed class WccScenarioStorySnapshot
{
    internal WccScenarioStorySnapshot(WmlStaticSource source, WmlStaticSourceRange scenario, WmlStaticSourceRange? story,
        WmlCampaignEvaluationPlan? plan, string? destinationDomain, IEnumerable<WccScenarioStoryContext> contexts,
        IEnumerable<WccScenarioStoryPartSnapshot> parts, IReadOnlyDictionary<string, WccStoryLiteralNode>? stories,
        bool canEdit, string diagnostic)
    { Source = source; ScenarioRange = scenario; StoryRange = story; Plan = plan; DestinationTextDomain = destinationDomain;
        Contexts = Array.AsReadOnly(contexts.ToArray()); Parts = Array.AsReadOnly(parts.ToArray());
        Stories = stories is null ? new ReadOnlyDictionary<string, WccStoryLiteralNode>(new Dictionary<string, WccStoryLiteralNode>())
            : WccScenarioStoryFieldSnapshot.Freeze(stories); CanEdit = canEdit; Diagnostic = diagnostic; }
    public WmlStaticSource Source { get; }
    public WmlStaticSourceRange ScenarioRange { get; }
    public WmlStaticSourceRange? StoryRange { get; }
    public string? DestinationTextDomain { get; }
    public IReadOnlyList<WccScenarioStoryContext> Contexts { get; }
    public IReadOnlyList<WccScenarioStoryPartSnapshot> Parts { get; }
    public bool CanEdit { get; }
    public string Diagnostic { get; }
    public bool AuthorizesPersistence => false;
    /// <summary>Captured expanded display values. They never replace the exact authored recipe.</summary>
    public IReadOnlyList<WccScenarioStoryPartSnapshot> DisplayParts => CapturedPreviewParts ?? Parts;
    public bool SupportsTopologyEditing { get; internal init; } = true;
    public bool CanEditTravelMarkers => CanEdit && (SupportsTopologyEditing || Parts.Count != 0);
    /// <summary>Append a local literal layer while keeping inherited macro layers and calls intact.</summary>
    public bool CanAddLocalBackgroundLayers => CanEdit && (SupportsTopologyEditing || Parts.Count != 0);
    public string? PreviewDiagnostic { get; internal init; }
    public string? DefinitionMacroName => DefinitionBinding?.MacroName;
    public string? DefinitionSourcePath => DefinitionBinding?.Definition.Source.Path;
    public string? DefinitionSourceText => DefinitionBinding?.Definition.Source.Text;
    public string? SharedUseNotice => DefinitionBinding is null ? null : "Edits update the authored " + DefinitionBinding.MacroName
        + " definition in " + DefinitionBinding.Definition.Source.Path + ". Its captured caller is this scenario.";
    internal WccStoryDefinitionBinding? DefinitionBinding { get; init; }
    internal IReadOnlyList<WccScenarioStoryPartSnapshot>? CapturedPreviewParts { get; init; }
    public IReadOnlyDictionary<string, string?> ScenarioTitles { get; internal init; } = new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>());
    internal WmlCampaignEvaluationPlan? Plan { get; }
    internal IReadOnlyDictionary<string, WccStoryLiteralNode> Stories { get; }
}

/// <summary>Immutable exact-story proposal. Only the ordinary checked collected save grants write authority.</summary>
public sealed class WccScenarioStoryEdit
{
    internal WccScenarioStoryEdit(WccScenarioStorySnapshot snapshot, IReadOnlyList<WccScenarioStoryPartSpec> parts,
        IReadOnlyList<WccScenarioFieldSourcePatch> patches, string identity, IReadOnlyList<WccScenarioStoryUploadReceipt> uploads)
    { Snapshot = snapshot; Parts = parts; Patches = patches; Identity = identity; Uploads = uploads; }
    public WccScenarioStorySnapshot Snapshot { get; }
    public WmlStaticSource Source => Snapshot.Source;
    public IReadOnlyList<WccScenarioStoryPartSpec> Parts { get; }
    public string Identity { get; }
    public IReadOnlyList<WccScenarioStoryUploadReceipt> Uploads { get; }
    public bool HasChanges => Patches.Count != 0 || DefinitionCandidate is not null;
    internal WccScenarioFieldBatchCandidate? DefinitionCandidate { get; init; }
    public bool AuthorizesPersistence => false;
    internal IReadOnlyList<WccScenarioFieldSourcePatch> Patches { get; }
}

internal sealed record WccStoryDefinitionBinding(string MacroName, WmlStaticSourceRange Definition,
    WmlStaticSourceRange Invocation, WccScenarioStorySnapshot LiteralStory, string ProjectRootPath);

internal sealed record WccStoryLiteralField(string Key, WmlStaticSourceRange Assignment, WmlStaticSourceRange Value, string? Domain);
internal sealed record WccStoryLiteralNode(string Id, string TagName, WmlStaticSourceRange Range,
    IReadOnlyDictionary<string, WccStoryLiteralField> Fields, IReadOnlyList<WccStoryLiteralNode> Children,
    string? OpeningDomain, string? ClosingDomain)
{
    internal string EffectiveText { get; init; } = Range.Text;
    internal string? TravelMacroName { get; init; }
    internal WmlStaticSourceRange? TravelInvocation { get; init; }
}
