using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private readonly Dictionary<(ScenarioInitialFieldOwnerScope Scope, string Key), bool> _wccFieldVisibility = new();
    private readonly Dictionary<(ScenarioInitialFieldOwnerScope Scope, string Label), WccInitialFieldEditorGroup> _wccFieldGroups = new();
    private readonly Dictionary<WmlStaticSource, IReadOnlyList<(int Offset, string Text)>> _wccPresentationDirectives = new();
    private bool _refreshingWccFieldPresentation;
    public event EventHandler? WccFieldVisibilityChanged;
    public IReadOnlyList<WccInitialFieldEditorGroup> WccScenarioFieldGroupsLeft { get; private set; } = Array.Empty<WccInitialFieldEditorGroup>();
    public IReadOnlyList<WccInitialFieldEditorGroup> WccScenarioFieldGroupsRight { get; private set; } = Array.Empty<WccInitialFieldEditorGroup>();

    internal bool IsRequiredWccInitialField(ScenarioInitialFieldDefinition definition) => definition.OwnerScope switch
    {
        ScenarioInitialFieldOwnerScope.Scenario => definition.Key == "id",
        ScenarioInitialFieldOwnerScope.Side => definition.Key == "side",
        ScenarioInitialFieldOwnerScope.Leader => definition.Key == "type",
        _ => false
    };

    internal bool IsWccInitialFieldPreferredVisible(ScenarioInitialFieldDefinition definition) =>
        _wccFieldVisibility.TryGetValue((definition.OwnerScope, definition.Key), out var visible)
            ? visible : IsCommonWccInitialField(definition);

    private static bool IsCommonWccInitialField(ScenarioInitialFieldDefinition field) => field.OwnerScope switch
    {
        ScenarioInitialFieldOwnerScope.Scenario => field.Key is "id" or "name" or "next_scenario" or "map_data" or "map_file" or
            "turns" or "victory_when_enemies_defeated" or "carryover_percentage" or "carryover_add" or "objectives",
        ScenarioInitialFieldOwnerScope.Side => field.Key is "side" or "controller" or "side_name" or "team_name" or "user_team_name" or
            "gold" or "income" or "village_gold" or "recall_cost" or "recruit" or "no_leader" or "defeat_condition" or
            "fog" or "shroud" or "color" or "flag" or "flag_icon",
        ScenarioInitialFieldOwnerScope.Leader => field.Key is "type" or "id" or "name" or "canrecruit" or "unrenamable" or
            "x" or "y" or "facing" or "profile" or "small_profile",
        _ => false
    };

    private IEnumerable<WccInitialFieldEditorVm> CurrentWccPresentationRows =>
        WccScenarioFieldEditors.Concat(WccSideFieldEditors).Concat(WccLeaderFieldEditors);

    public void SetWccInitialFieldVisible(ScenarioInitialFieldOwnerScope scope, string key, bool visible)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            var definition = WccPresentationDefinitions(scope).FirstOrDefault(field => field.Key == key);
            if (definition is null || IsRequiredWccInitialField(definition) ||
                CurrentWccPresentationRows.Any(row => row.Definition.OwnerScope == scope && row.Key == key && !row.CanChangeVisibility)) return;
            if (IsWccInitialFieldPreferredVisible(definition) == visible) return;
            _wccFieldVisibility[(scope, key)] = visible;
            RefreshWccFieldPresentation();
        }
    }

    internal static IReadOnlyList<ScenarioInitialFieldDefinition> WccPresentationDefinitions(ScenarioInitialFieldOwnerScope scope) => scope switch
    {
        ScenarioInitialFieldOwnerScope.Scenario => ScenarioInitialFieldCatalog.ScenarioFields,
        ScenarioInitialFieldOwnerScope.Side => ScenarioInitialFieldCatalog.SideFields,
        ScenarioInitialFieldOwnerScope.Leader => ScenarioInitialFieldCatalog.LeaderFields,
        _ => Array.Empty<ScenarioInitialFieldDefinition>()
    };

    internal IReadOnlyList<WccInitialFieldEditorVm> GetWccPresentationRows(ScenarioInitialFieldOwnerScope scope, string key) =>
        CurrentWccPresentationRows.Where(row => row.Definition.OwnerScope == scope && row.Key == key).ToArray();

    private WccInitialFieldEditorGroup GetWccFieldPresentationGroup(string label, IReadOnlyList<WccInitialFieldEditorVm> rows)
    {
        var scope = rows[0].Definition.OwnerScope;
        if (!_wccFieldGroups.TryGetValue((scope, label), out var group))
            _wccFieldGroups[(scope, label)] = group = new(this, scope, label);
        group.SetFields(rows);
        return group;
    }

    private void RefreshWccFieldPresentation()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (_refreshingWccFieldPresentation) return;
            _refreshingWccFieldPresentation = true;
            try
            {
                foreach (var row in CurrentWccPresentationRows.ToArray()) row.RefreshPresentation();
                foreach (var group in WccScenarioFieldGroups.Concat(WccSideFieldGroups).Concat(WccLeaderFieldGroups).ToArray()) group.RefreshVisibility();
                UpdateWccScenarioPresentationColumns();
                WccFieldVisibilityChanged?.Invoke(this, EventArgs.Empty);
            }
            finally { _refreshingWccFieldPresentation = false; }
        }
    }

    private void UpdateWccScenarioPresentationColumns()
    {
        var left = WccScenarioFieldGroups.Where(group => group.Label is "Identity" or "Map" or "Turns").ToArray();
        var right = WccScenarioFieldGroups.Where(group => group.Label is not ("Identity" or "Map" or "Turns")).ToArray();
        if (!WccScenarioFieldGroupsLeft.SequenceEqual(left))
        { WccScenarioFieldGroupsLeft = left; OnPropertyChanged(nameof(WccScenarioFieldGroupsLeft)); }
        if (!WccScenarioFieldGroupsRight.SequenceEqual(right))
        { WccScenarioFieldGroupsRight = right; OnPropertyChanged(nameof(WccScenarioFieldGroupsRight)); }
    }

    internal bool HasAuthoredLocalWccInitialFieldValue(WmlStaticScalarOwner owner, string key)
    {
        var route = GetWccInitialFieldRoute(owner, key);
        if (route is null || !owner.Fields.TryGetValue(key, out var field)) return false;
        if (route.Target is { } target && IsLocalPresentationRange(target, route.Source) &&
            route.Kind is WccScenarioFieldRouteKind.DirectToken or WccScenarioFieldRouteKind.ScalarArgument or WccScenarioFieldRouteKind.OptionalArgument)
            return true;
        // Unsupported local scalar syntax still stays visible. A macro definition's
        // emissions are defaults, even when the definition is in the same file.
        var contributions = field.IsKnown ? field.Contributions.TakeLast(1) : field.Contributions;
        return contributions.Any(contribution => contribution.AssignmentOrigins.Append(contribution.AssignmentOrigin)
            .Any(origin => origin is not null && IsLocalPresentationRange(origin, route.Source) &&
                contribution.ExpansionStack.Concat(contribution.ValueExpansionStack).All(frame => !ContainsPresentationRange(frame.Definition, origin))));
    }

    private static bool IsLocalPresentationRange(WmlStaticSourceRange range, WmlStaticSource source) =>
        range.Source.IsAuthored && source.IsAuthored && range.Source.ContentHash == source.ContentHash && PathsEqual(range.Source.Path, source.Path);

    private static bool ContainsPresentationRange(WmlStaticSourceRange container, WmlStaticSourceRange range) =>
        IsLocalPresentationRange(range, container.Source) && range.StartOffset >= container.StartOffset &&
        range.StartOffset + range.Length <= container.StartOffset + container.Length;

    public WccFieldVisibilityDialogViewModel CreateWccFieldVisibilityDialog(ScenarioInitialFieldOwnerScope? scope = null) => new(this, scope);

    internal bool HasExistingWccDifficultyFieldSource(WmlStaticScalarOwner owner, string key)
    {
        if (!IsCurrentWccInitialFieldOwner(owner) || owner.ResolveField(key).State != WmlStaticScalarResolutionState.Known ||
            _sourceEvaluationPlan is null || !owner.Fields.TryGetValue(key, out var field)) return false;
        var difficulties = _sourceEvaluationPlan.Contexts.Select(context => context.DifficultyDefine)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToHashSet(StringComparer.Ordinal);
        if (difficulties.Count < 2) return false;
        var contribution = field.WinningContribution;
        if (contribution is null) return false;
        return contribution.ValueOrigins.Append(contribution.AssignmentOrigin).Where(range => range is not null)
            .Any(range => IsInsideCapturedDifficultyCondition(range!, difficulties)) ||
            contribution.ValueExpansionStack.Any(frame => PresentationDirectives(frame.Definition.Source)
                .Any(item => item.Offset >= frame.Definition.StartOffset && item.Offset < frame.Definition.StartOffset + frame.Definition.Length &&
                    IsDifficultyDirective(item.Text, difficulties)));
    }

    private bool IsInsideCapturedDifficultyCondition(WmlStaticSourceRange range, IReadOnlySet<string> difficulties)
    {
        var stack = new Stack<bool>();
        foreach (var directive in PresentationDirectives(range.Source).TakeWhile(item => item.Offset < range.StartOffset))
        {
            var kind = WmlPreprocessorScanner.GetDirectiveKind(directive.Text);
            if (kind == WmlPreprocessorDirectiveKind.IfStart) stack.Push(IsDifficultyDirective(directive.Text, difficulties));
            else if (kind == WmlPreprocessorDirectiveKind.EndIf && stack.Count != 0) stack.Pop();
        }
        return stack.Contains(true);
    }

    private static bool IsDifficultyDirective(string text, IReadOnlySet<string> difficulties)
    {
        var pieces = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return pieces.Length >= 2 && (pieces[0] is "#ifdef" or "#ifndef") && difficulties.Contains(pieces[1]);
    }

    private IReadOnlyList<(int Offset, string Text)> PresentationDirectives(WmlStaticSource source)
    {
        if (_wccPresentationDirectives.TryGetValue(source, out var directives)) return directives;
        if (_wccPresentationDirectives.Count >= 128) _wccPresentationDirectives.Clear();
        return _wccPresentationDirectives[source] = ReadPresentationDirectives(source.Text).ToArray();
    }

    // Lexical inspection of already captured source only. Quoted/raw text and
    // comments cannot lend a fake difficulty directive to the display indicator.
    private static IEnumerable<(int Offset, string Text)> ReadPresentationDirectives(string text)
    {
        var quoted = false;
        var raw = false;
        var lineStart = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (index == lineStart && !quoted && !raw)
            {
                var end = text.IndexOf('\n', index);
                if (end < 0) end = text.Length;
                var line = text[index..end].Trim();
                if (line.StartsWith('#'))
                {
                    yield return (index, "#" + WmlInlineCommentSplitter.Split(line[1..]).Value.Trim());
                    index = end - 1;
                    continue;
                }
            }
            if (text[index] == '\n') { lineStart = index + 1; continue; }
            if (!quoted && index + 1 < text.Length && text[index] == '<' && text[index + 1] == '<')
            { raw = true; index++; continue; }
            if (raw && index + 1 < text.Length && text[index] == '>' && text[index + 1] == '>')
            { raw = false; index++; continue; }
            if (raw) continue;
            if (quoted && text[index] == '\\') { index++; continue; }
            if (text[index] == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"') index++;
                else quoted = !quoted;
            }
            else if (!quoted && text[index] == '#')
            {
                var end = text.IndexOf('\n', index);
                index = end < 0 ? text.Length : end - 1;
            }
        }
    }
}
