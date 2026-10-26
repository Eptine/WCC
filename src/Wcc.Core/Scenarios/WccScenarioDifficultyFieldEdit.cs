using System.Globalization;
using System.Text;
using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public enum WccScenarioDifficultyValueMode { Shared, Independent }

/// <summary>A displayed or requested literal in one fixed declared gameplay context.</summary>
public sealed record WccScenarioDifficultyFieldValue(string Define, string ContextSemanticKey, string? ValueText);

/// <summary>Source topology evidence only. Fresh collected review grants the separate write authority.</summary>
public sealed class WccScenarioDifficultyFieldSnapshot
{
    internal WccScenarioDifficultyFieldSnapshot(WmlStaticSource mainSource, WmlStaticSource source,
        WmlStaticScalarOwner owner, string key, WmlCampaignEvaluationPlan? plan,
        WccCampaignAuthoringMetadata? authoring, WccScenarioDifficultyValueMode mode,
        IEnumerable<WccScenarioDifficultyFieldValue> values, bool canEdit, string diagnostic,
        WccScenarioDifficultyFieldLayout? layout, WccScenarioDifficultyFieldInsertionIntent? insertionIntent = null)
    {
        MainSource = mainSource; Source = source; Owner = owner; Key = key; Plan = plan;
        Authoring = authoring is null ? null : new()
        {
            LayoutVersion = authoring.LayoutVersion, AddonId = authoring.AddonId,
            CampaignDefine = authoring.CampaignDefine, MainRelativePath = authoring.MainRelativePath,
            FixedDifficultyDefines = authoring.FixedDifficultyDefines?.ToList() ?? [],
            UsesExplicitDifficultyValues = authoring.UsesExplicitDifficultyValues
        };
        Mode = mode; Values = Array.AsReadOnly(values.ToArray()); CanEdit = canEdit;
        Diagnostic = diagnostic; Layout = layout; InsertionIntent = insertionIntent;
        var identity = new StringBuilder();
        foreach (var part in new[] { source.Path, source.ContentHash, owner.TagName, key,
                     owner.OpeningOrigin?.StartOffset.ToString(CultureInfo.InvariantCulture) ?? "",
                     owner.ClosingOrigin?.StartOffset.ToString(CultureInfo.InvariantCulture) ?? "" })
            identity.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
        FieldIdentity = identity.ToString();
    }
    public WmlStaticSource Source { get; }
    public WmlStaticSource MainSource { get; }
    public WmlStaticScalarOwner Owner { get; }
    public string Key { get; }
    public WccScenarioDifficultyValueMode Mode { get; }
    public IReadOnlyList<WccScenarioDifficultyFieldValue> Values { get; }
    public bool CanEdit { get; }
    public string Diagnostic { get; }
    public string FieldIdentity { get; }
    public bool AuthorizesPersistence => false;
    public WccScenarioDifficultyFieldInsertionIntent? InsertionIntent { get; }
    public bool SameOwner(WmlStaticScalarOwner owner) => WccScenarioDifficultyFieldPlanner.SameOwner(Owner, owner);
    internal WmlCampaignEvaluationPlan? Plan { get; }
    internal WccCampaignAuthoringMetadata? Authoring { get; }
    internal WccScenarioDifficultyFieldLayout? Layout { get; }
}

/// <summary>Immutable difficulty topology and final literal intent, never an unrestricted source rewrite.</summary>
public sealed class WccScenarioDifficultyFieldEdit
{
    internal WccScenarioDifficultyFieldEdit(WccScenarioDifficultyFieldSnapshot snapshot,
        WccScenarioDifficultyValueMode mode, string selectedContextSemanticKey, string selectedValue,
        IEnumerable<WccScenarioDifficultyFieldValue> values, IEnumerable<string> encodedValues)
    {
        Snapshot = snapshot; Mode = mode; SelectedContextSemanticKey = selectedContextSemanticKey;
        SelectedValue = selectedValue; Values = Array.AsReadOnly(values.ToArray());
        EncodedValues = Array.AsReadOnly(encodedValues.ToArray());
    }
    public WccScenarioDifficultyFieldSnapshot Snapshot { get; }
    public WmlStaticSource Source => Snapshot.Source;
    public WccScenarioDifficultyValueMode Mode { get; }
    public string SelectedContextSemanticKey { get; }
    public string SelectedValue { get; }
    public IReadOnlyList<WccScenarioDifficultyFieldValue> Values { get; }
    public bool AuthorizesPersistence => false;
    internal IReadOnlyList<string> EncodedValues { get; }
}

internal sealed record WccScenarioDifficultyFieldAssignment(string? Define,
    WmlStaticSourceRange Assignment, WmlStaticSourceRange Value, WmlStaticSourceRange Region, string Literal);
internal sealed record WccScenarioDifficultyFieldLayout(IReadOnlyList<WccScenarioDifficultyFieldAssignment> Assignments,
    int InsertionOffset, string Indent, string Newline,
    IReadOnlyList<WccScenarioDifficultyFieldGroup> Groups,
    WccScenarioDifficultyMacroContract? MacroContract = null);

/// <summary>Bounded display contract; the fresh reviewer separately proves every real contribution.</summary>
internal sealed class WccScenarioDifficultyMacroContract
{
    internal WccScenarioDifficultyMacroContract(WmlStaticSourceRange invocation,
        WmlStaticSourceRange statementRegion, IEnumerable<WccScenarioDifficultyMacroSlot> slots,
        IEnumerable<WmlStaticSource> definitionSources)
    {
        Invocation = invocation; StatementRegion = statementRegion;
        Slots = Array.AsReadOnly(slots.Select(slot => slot with
        { ActiveDefinitions = Array.AsReadOnly(slot.ActiveDefinitions.ToArray()) }).ToArray());
        DefinitionSources = Array.AsReadOnly(definitionSources.ToArray());
    }
    internal WmlStaticSourceRange Invocation { get; }
    internal WmlStaticSourceRange StatementRegion { get; }
    internal IReadOnlyList<WccScenarioDifficultyMacroSlot> Slots { get; }
    internal IReadOnlyList<WmlStaticSource> DefinitionSources { get; }
}

internal sealed record WccScenarioDifficultyMacroSlot(string Define, string ContextSemanticKey,
    int ArgumentIndex, WmlStaticSourceRange Argument, string Literal,
    IReadOnlyList<WmlStaticSourceRange> ActiveDefinitions);

/// <summary>Exact direct sibling assignments inside one proved literal difficulty branch.</summary>
internal sealed record WccScenarioDifficultyFieldGroupMember(string Key, WmlStaticSourceRange Assignment,
    WmlStaticSourceRange Value, string Literal);
internal sealed record WccScenarioDifficultyFieldGroup(string Define, WmlStaticSourceRange Region,
    IReadOnlyList<WccScenarioDifficultyFieldGroupMember> Members);
