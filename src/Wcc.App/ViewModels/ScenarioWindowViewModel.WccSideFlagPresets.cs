using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>A captured manual source application; it grants no scalar or persistence authority.</summary>
public sealed class WccSideFlagPresetEditDraft
{
    internal WccSideFlagPresetEditDraft(WccScenarioRawEditDraft scenario, WmlStaticScalarOwner owner,
        ScenarioSideVm side, WccLibrarySnapshot snapshot, WccPackageRevisionId revision,
        WmlMacroExpansionDefinition definition, CatalogPickerSelection selection, int start, int end,
        string prefix, string suffix)
    {
        Scenario = scenario; Owner = owner; Side = side; Snapshot = snapshot; Revision = revision;
        Definition = definition; Selection = selection; Start = start; End = end;
        Prefix = prefix; Suffix = suffix;
    }
    internal WccScenarioRawEditDraft Scenario { get; }
    internal WmlStaticScalarOwner Owner { get; }
    internal ScenarioSideVm Side { get; }
    internal WccLibrarySnapshot Snapshot { get; }
    internal WccPackageRevisionId Revision { get; }
    internal WmlMacroExpansionDefinition Definition { get; }
    internal CatalogPickerSelection Selection { get; }
    internal int Start { get; }
    internal int End { get; }
    internal string Prefix { get; }
    internal string Suffix { get; }
    public string Text => Selection.EmittedValue;
    internal string Candidate(string invocation) => Scenario.Text[..Start] + Prefix + invocation.Trim() +
        Suffix + Scenario.Text[End..];
}

public partial class ScenarioWindowViewModel
{
    private bool _sideFlagPresetNotificationsInitialized;
    private static readonly string[] FlagPresetMacroNames = ["FLAG_VARIANT", "FLAG_VARIANT6"];

    public string WccSideFlagPresetInvocation
    {
        get
        {
            InitializeSideFlagPresetNotifications();
            lock (_wccInitialFieldRefreshGate)
            {
                if (SelectedStaticSideOwner is { } selected && GetEffectiveSideFlagDraft(selected) is { } draft)
                    return draft.InvocationText;
                return SelectedStaticSideOwner is { } owner && FindCurrentFlagPreset(owner, requireEditableLocal: false) is { } current
                    ? current.Invocation.Text : string.Empty;
            }
        }
    }

    public string WccSideFlagPresetGuidance
    {
        get
        {
            InitializeSideFlagPresetNotifications();
            if (HasWccSideFlagDrafts)
                return _sideFlagSourceBinding is { } binding && GetSideFlagBindingProblem(binding) is { } problem ? problem
                    : "Flag preset changes are staged. Save checks them together with field, music, schedule and campaign drafts; saved source remains unchanged.";
            if (_hasAuthoredSourceChanges || _externalSourceChangesPending)
                return "Save or reload source changes before choosing another flag preset.";
            if (SelectedStaticSideOwner is { } owner && IsSharedFlagPreset(owner))
                return "This flag invocation comes from a shared macro and is read-only here. Its flag fields and containing RAW source remain available.";
            if (!TryGetFlagPresetSide(out _, out _, out _, out _))
                return "This side has no exact editable literal source for a flag preset. Its flag fields and RAW source remain available.";
            return WccSideFlagPresetInvocation.Length > 0
                ? "The macro sets flag animation and icon together. Choosing a preset stages its whole invocation for the collected Save."
                : CanStageWccSideFlagPreset ? "No authored flag pair. Choosing a preset stages a local macro invocation for the collected Save."
                    : "Custom or uncertain flag values remain available in the flag fields and RAW source.";
        }
    }

    public bool CanPickWccSideFlagPreset
    {
        get
        {
            InitializeSideFlagPresetNotifications();
            lock (_wccInitialFieldRefreshGate) return CanStageWccSideFlagPreset;
        }
    }

    private bool CanApplySideFlagPresetRaw => !HasWccInlineDrafts && !HasWccMusicScheduleDrafts && !HasWccSideFlagDrafts &&
        !IsWccInlineSaveBusy && TryGetFlagPresetSide(out var owner, out _, out _, out _) && !IsSharedFlagPreset(owner) &&
        BeginWccScenarioSourceRawEdit() is not null;

    public IReadOnlyList<CatalogPickerOptionVm> GetWccSideFlagPresetOptions()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (_macroLibrarySnapshot is not { } snapshot || ScenarioContentVisibleRevisions is not { } visible ||
                _staticRootRevisionSelection is not { } root) return [];
            var definitions = GetWccMusicScheduleDefinitions(FlagPresetMacroNames, fresh: true);
            var options = new List<CatalogPickerOptionVm>();
            foreach (var package in snapshot.PackagesById.Values.Where(package => package.IsAvailable &&
                         package.SourceLocation is not null && (IsInitialCorePackage(package) ||
                             snapshot.RevisionsById.TryGetValue(root.RootRevisionId, out var selected) &&
                             selected.PackageId == package.Id && PathsEqual(package.SourceLocation, _sourceHostSession.RootPath))))
            {
                var packageRevisions = snapshot.RevisionsById.Values.Where(revision => revision.PackageId == package.Id &&
                    visible.Contains(revision.Id)).Select(revision => revision.Id).ToArray();
                if (packageRevisions.Length != 1) continue;
                var directory = Path.Combine(package.SourceLocation!, "images", "flags");
                if (!Directory.Exists(directory)) continue;
                try
                {
                    foreach (var icon in Directory.EnumerateFiles(directory, "*-flag-icon.png", SearchOption.AllDirectories)
                                 .Order(StringComparer.Ordinal))
                    {
                        var relative = Path.GetRelativePath(directory, icon).Replace('\\', '/');
                        var variant = relative[..^"-flag-icon.png".Length];
                        if (!IsSafeFlagVariant(variant)) continue;
                        var basePath = icon[..^"-flag-icon.png".Length];
                        var six = Enumerable.Range(1, 6).All(index => File.Exists(basePath + "-flag-" + index + ".png"));
                        var four = Enumerable.Range(1, 4).All(index => File.Exists(basePath + "-flag-" + index + ".png"));
                        if (!four) continue;
                        var macroName = six ? "FLAG_VARIANT6" : "FLAG_VARIANT";
                        var matches = definitions.Where(definition => definition.Name == macroName).ToArray();
                        if (matches.Length != 1 || !IsSupportedFlagPresetDefinition(matches[0])) continue;
                        var definition = matches[0];
                        var entities = snapshot.FindDefinitionsAcrossPackages(WccEntityTypes.MacroDefinition, macroName)
                            .Where(entity => WccLibraryRevisionVisibility.ContainsEntity(snapshot, visible, entity) &&
                                snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file) &&
                                PathsEqual(file.StoragePath, definition.SourcePath) &&
                                entity.SourceSpan?.StartLine == definition.SourceStartLine &&
                                (definition.PackageRevisionId == file.PackageRevisionId.ToString() ||
                                 definition.PackageRevisionId is null && PathsEqual(file.StoragePath, _currentSourcePath ?? LoadedPath)))
                            .ToArray();
                        if (entities.Length != 1) continue;
                        var entity = entities[0];
                        options.Add(new CatalogPickerOptionVm
                        {
                            ValueId = "{" + macroName + " " + variant + "}",
                            DisplayName = variant + " [" + package.Name + "] — " + macroName,
                            LibraryEntityId = entity.Id,
                            LibraryPackageId = snapshot.FindPackageForEntity(entity.Id)?.Id,
                            EmissionKind = CatalogPickerEmissionKind.MacroInvocation,
                            PreviewPath = icon,
                            ResourceSourceLabel = IsInitialCorePackage(package) ? "Core" : "Current campaign",
                            ResourceSourceHint = "Available flag image family; applying keeps the macro invocation in scenario source."
                        });
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            // A campaign resource can shadow a Core resource with the same image path. Do not offer
            // two indistinguishable invocations as if their physical origin could be selected in WML.
            return options.GroupBy(option => option.ValueId, StringComparer.Ordinal)
                .Select(group => group.OrderBy(option => option.ResourceSourceLabel == "Current campaign" ? 0 : 1).First())
                .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public WccSideFlagPresetEditDraft? BeginWccSideFlagPresetEdit(CatalogPickerSelection selection)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!CanApplySideFlagPresetRaw || !TryGetFlagPresetSide(out var owner, out var side, out var opening, out var closing) ||
                BeginWccScenarioSourceRawEdit() is not { } scenario || _macroLibrarySnapshot is not { } snapshot ||
                _staticRootRevisionSelection is not { } root || !IsCurrentFlagPresetOption(selection) ||
                !WmlMacroInvocationSyntaxScanner.TryParseSingle(selection.EmittedValue, out var syntax)) return null;
            var definitions = GetWccMusicScheduleDefinitions([syntax!.Name], fresh: true)
                .Where(definition => definition.Name == syntax.Name).ToArray();
            if (definitions.Length != 1 || !IsSupportedFlagPresetDefinition(definitions[0])) return null;
            var current = FindCurrentFlagPreset(owner);
            var blockStart = scenario.Document.BlockSpan.StartIndex;
            if (current is not null)
                return new(scenario, owner, side, snapshot, root.RootRevisionId, definitions[0], selection,
                    current.Invocation.StartOffset - blockStart,
                    current.Invocation.StartOffset + current.Invocation.Length - blockStart, "", "");
            // Only append on an exact same-source, unamended literal side. Preserve manual values
            // and unrelated content; the reviewed invocation supplies the winning pair.
            var lineStart = scenario.SourceText.LastIndexOf('\n', Math.Max(0, closing.StartOffset - 1)) + 1;
            if (!scenario.SourceText.AsSpan(lineStart, closing.StartOffset - lineStart).Trim().IsEmpty) return null;
            var indentation = scenario.SourceText[lineStart..closing.StartOffset];
            var newline = scenario.SourceText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            return new(scenario, owner, side, snapshot, root.RootRevisionId, definitions[0], selection,
                lineStart - blockStart, lineStart - blockStart, indentation + "    ", newline);
        }
    }

    public string? ValidateWccSideFlagPresetEdit(WccSideFlagPresetEditDraft draft, string invocation)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (!ReferenceEquals(draft.Side, SelectedSide) || !ReferenceEquals(draft.Owner, SelectedStaticSideOwner) ||
                !ReferenceEquals(draft.Snapshot, _macroLibrarySnapshot) ||
                _staticRootRevisionSelection?.RootRevisionId != draft.Revision ||
                !CanApplySideFlagPresetRaw || !IsCurrentFlagPresetOption(draft.Selection))
                return "The side, source or preset binding changed. Reopen the flag picker.";
            if (!string.Equals(invocation.Trim(), draft.Text, StringComparison.Ordinal))
                return "Use the selected whole flag invocation. Custom source remains available in the side RAW editor.";
            var fresh = GetWccMusicScheduleDefinitions([draft.Definition.Name], fresh: true)
                .Where(definition => definition.Name == draft.Definition.Name).ToArray();
            if (fresh.Length != 1 || !PathsEqual(fresh[0].SourcePath, draft.Definition.SourcePath) ||
                fresh[0].SourceStartLine != draft.Definition.SourceStartLine ||
                !string.Equals(fresh[0].RawText, draft.Definition.RawText, StringComparison.Ordinal))
                return "The flag macro definition changed. Refresh its source and reopen the picker.";
            return ValidateWccInitialFieldRawEdit(draft.Scenario, draft.Candidate(invocation));
        }
    }

    public bool ApplyWccSideFlagPresetEdit(WccSideFlagPresetEditDraft draft, string invocation)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (ValidateWccSideFlagPresetEdit(draft, invocation) is { } problem)
            { Status = problem; return false; }
            return ApplyWccInitialFieldRawEdit(draft.Scenario, draft.Candidate(invocation));
        }
    }

    private bool IsCurrentFlagPresetOption(CatalogPickerSelection selection) =>
        selection.EmissionKind == CatalogPickerEmissionKind.MacroInvocation &&
        GetWccSideFlagPresetOptions().Any(option => option.ValueId == selection.EmittedValue &&
            option.LibraryEntityId == selection.LibraryEntityId && option.LibraryPackageId == selection.LibraryPackageId);

    private bool TryGetFlagPresetSide(out WmlStaticScalarOwner owner, out ScenarioSideVm side,
        out WmlStaticSourceRange opening, out WmlStaticSourceRange closing)
    {
        owner = null!; side = null!; opening = null!; closing = null!;
        if (SelectedSide is not { IsGenerated: false } selected || !Sides.Contains(selected) ||
            SelectedStaticSideOwner is not { IsAmended: false, OpeningOutputOffset: not null,
                OpeningOrigin: { } start, ClosingOrigin: { } end } current ||
            _pristineLoadedSourceText is not { } text || !PathsEqual(start.Source.Path, _currentSourcePath ?? LoadedPath) ||
            !PathsEqual(end.Source.Path, start.Source.Path) || start.Source.Text != text || end.Source.Text != text ||
            start.Text.Trim() != "[side]" || end.Text.Trim() != "[/side]" || end.StartOffset <= start.StartOffset ||
            current.OpeningExpansionStack.Any(frame => IsFlagRangeInside(start, frame.Definition)) ||
            current.ClosingExpansionStack.Any(frame => IsFlagRangeInside(end, frame.Definition)) ||
            current.ResolveField("flag").State == WmlStaticScalarResolutionState.Unresolved ||
            current.ResolveField("flag_icon").State == WmlStaticScalarResolutionState.Unresolved) return false;
        if (WccSideTopologyOwnerProblem(current) is not null) return false;
        owner = current; side = selected; opening = start; closing = end;
        return true;
    }

    private sealed record CapturedFlagPreset(WmlStaticExpansionFrame Frame, WmlStaticSourceRange Invocation);

    private bool IsSharedFlagPreset(WmlStaticScalarOwner owner) =>
        FindCurrentFlagPreset(owner, requireEditableLocal: false) is not null && FindCurrentFlagPreset(owner) is null;

    private CapturedFlagPreset? FindCurrentFlagPreset(WmlStaticScalarOwner owner, bool requireEditableLocal = true)
    {
        if (!IsCurrentWccInitialFieldOwner(owner) || !owner.Fields.TryGetValue("flag", out var flag) ||
            !owner.Fields.TryGetValue("flag_icon", out var icon) || flag.WinningContribution is not { } flagValue ||
            icon.WinningContribution is not { } iconValue) return null;
        foreach (var frame in flagValue.ExpansionStack.Reverse())
        {
            if (!FlagPresetMacroNames.Contains(frame.MacroName, StringComparer.Ordinal) ||
                !IsSupportedFlagPresetDefinition(new(frame.MacroName, frame.Definition.Text, frame.Definition.Source.Path)) ||
                frame.InvocationOrigins.Count != 1 || frame.InvocationOrigins[0] is not { } invocation ||
                !iconValue.ExpansionStack.Any(other => other.MacroName == frame.MacroName &&
                    other.Definition == frame.Definition && other.InvocationOrigins.SequenceEqual(frame.InvocationOrigins)) ||
                !WmlMacroInvocationSyntaxScanner.TryParseSingle(invocation.Text, out var syntax) ||
                syntax!.Name != frame.MacroName || syntax.Arguments.Count != 1) continue;
            if (requireEditableLocal && (!PathsEqual(invocation.Source.Path, _currentSourcePath ?? LoadedPath) ||
                invocation.Source.Text != _pristineLoadedSourceText ||
                owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing ||
                !PathsEqual(opening.Source.Path, invocation.Source.Path) || !PathsEqual(closing.Source.Path, invocation.Source.Path) ||
                invocation.StartOffset <= opening.StartOffset || invocation.StartOffset + invocation.Length > closing.StartOffset ||
                flagValue.ExpansionStack.Concat(iconValue.ExpansionStack).Any(other => IsFlagRangeInside(invocation, other.Definition)))) continue;
            return new(frame, invocation);
        }
        return null;
    }

    private static bool IsFlagRangeInside(WmlStaticSourceRange range, WmlStaticSourceRange container) =>
        PathsEqual(range.Source.Path, container.Source.Path) && range.Source.ContentHash == container.Source.ContentHash &&
        range.StartOffset >= container.StartOffset && range.StartOffset + range.Length <= container.StartOffset + container.Length;

    private static bool IsSafeFlagVariant(string variant) => variant.Length > 0 &&
        variant.Split('/').All(component => component.Length > 0 && component is not ("." or "..") &&
            component.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));

    private static bool IsSupportedFlagPresetDefinition(WmlMacroExpansionDefinition definition)
    {
        if (!FlagPresetMacroNames.Contains(definition.Name, StringComparer.Ordinal)) return false;
        var code = new List<string>();
        foreach (var line in definition.RawText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                var directive = trimmed[1..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (directive is "define" or "enddef") code.Add("#" + WmlInlineCommentSplitter.Split(trimmed[1..]).Value.Trim());
                else if (directive is "undef" or "ifdef" or "ifndef" or "ifhave" or "ifnhave" or "ifver" or "ifnver" or
                         "if" or "else" or "endif" or "textdomain" or "error" or "warning" or "deprecated" or "arg" or "endarg") return false;
                continue;
            }
            var value = WmlInlineCommentSplitter.Split(trimmed).Value.Trim();
            if (value.Length > 0) code.Add(value.Replace(" ", "", StringComparison.Ordinal).Replace("\t", "", StringComparison.Ordinal));
        }
        var frames = definition.Name == "FLAG_VARIANT6" ? "6" : "4";
        return code.SequenceEqual(new[] { "#define " + definition.Name + " NAME",
            "flag=flags/{NAME}-flag-[1~" + frames + "].png:150", "flag_icon=flags/{NAME}-flag-icon.png", "#enddef" });
    }

    private void InitializeSideFlagPresetNotifications()
    {
        if (_sideFlagPresetNotificationsInitialized) return;
        _sideFlagPresetNotificationsInitialized = true;
        PropertyChanged += SideFlagPresetSourcePropertyChanged;
    }

    private void SideFlagPresetSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SelectedSide) or nameof(SelectedScenario) or nameof(WccSideFieldGroups) or
            nameof(StaticInitialValuesStatus) or nameof(HasWccInlineDrafts) or nameof(HasWccMusicScheduleDrafts) or
            nameof(IsWccInlineSaveBusy) or nameof(CanEditDocument) or nameof(HasWccInitialFieldEditorsStatus))
        {
            OnPropertyChanged(nameof(WccSideFlagPresetInvocation));
            OnPropertyChanged(nameof(WccSideFlagPresetGuidance));
            OnPropertyChanged(nameof(CanPickWccSideFlagPreset));
        }
    }
}
