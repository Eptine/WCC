using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Macros;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

/// <summary>Manual source fragment inside a fenced authored scenario draft.</summary>
public sealed class WccScenarioContentRawEditDraft
{
    internal WccScenarioContentRawEditDraft(WccScenarioRawEditDraft scenario, int start, int end,
        string text, IReadOnlyList<string>? placeholders = null)
    {
        Scenario = scenario;
        Start = start;
        End = end;
        Text = text;
        Placeholders = placeholders ?? [];
        RequiredMacroName = Placeholders.Count == 0 ? null
            : WmlMacroInvocationSyntaxScanner.Scan(text).FirstOrDefault()?.Name;
    }
    internal WccScenarioRawEditDraft Scenario { get; }
    internal int Start { get; }
    internal int End { get; }
    internal IReadOnlyList<string> Placeholders { get; }
    internal string? RequiredMacroName { get; }
    public string Text { get; }
    internal string Candidate(string replacement) =>
        Scenario.Text[..Start] + replacement + Scenario.Text[End..];
}

public partial class ScenarioWindowViewModel
{
    private ScenarioDocument? _contentInventoryDocument;
    private string? _contentInventoryText;
    private IReadOnlyList<ScenarioContentSourceEntry> _contentInventory = [];
    private ScenarioContentSourceEntry? _selectedScenarioContent;

    public IReadOnlyList<ScenarioContentSourceEntry> WccScenarioContentEntries
    {
        get
        {
            var document = SelectedScenario?.Document;
            var raw = GetWccScenarioSourceRawWml();
            if (!ReferenceEquals(document, _contentInventoryDocument) ||
                !string.Equals(raw, _contentInventoryText, StringComparison.Ordinal))
            {
                _contentInventoryDocument = document;
                _contentInventoryText = raw;
                _contentInventory = raw is null ? [] : ScenarioContentSourceInventory.Build(raw);
                _selectedScenarioContent = null;
            }
            return _contentInventory;
        }
    }

    public ScenarioContentSourceEntry? SelectedWccScenarioContent
    {
        get => _selectedScenarioContent;
        set => SetField(ref _selectedScenarioContent, value);
    }

    public bool CanEditWccScenarioContent => BeginWccScenarioSourceRawEdit() is not null;
    public bool HasWccScenarioSource => SelectedScenario is not null;
    public string WccScenarioContentGuidance => HasWccInlineDrafts || IsWccInlineSaveBusy || HasWccMusicScheduleDrafts || (HasWccSideFlagDrafts || HasWccEventDrafts || HasWccSideTopologyDrafts || HasWccLeaderAdditionDrafts || HasWccInitialUnitDrafts || HasWccStoryDraft)
        ? "Save or discard field, music, schedule, flag and event drafts before editing source blocks."
        : _hasAuthoredSourceChanges
            ? "Save or reload source changes before editing another source block or typed field."
            : "Music, schedules, events and custom tags keep their source order. RAW edits change the working source; Save writes the file. Macros may also add events or settings.";

    public string? GetWccScenarioSourceRawWml()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (SelectedScenario?.Document is not { } document) return null;
            // Draft fields are separate from this loaded/authored source.
            if (!_hasAuthoredSourceChanges)
            {
                var span = document.BlockSpan;
                if (span.StartIndex >= 0 && span.EndIndexExclusive <= document.OriginalText.Length)
                    return document.OriginalText[span.StartIndex..span.EndIndexExclusive];
            }
            return ScenarioFileRenderer.RenderUpdatedScenarioBlock(document);
        }
    }

    public WccScenarioContentRawEditDraft? BeginSelectedWccScenarioContentRawEdit()
    {
        var entries = WccScenarioContentEntries;
        var entry = SelectedWccScenarioContent;
        if (entry is null || !entries.Any(item => ReferenceEquals(item, entry)) ||
            BeginWccScenarioSourceRawEdit() is not { } scenario ||
            entry.StartOffset < 0 || entry.EndOffsetExclusive > scenario.Text.Length ||
            !string.Equals(scenario.Text[entry.StartOffset..entry.EndOffsetExclusive], entry.Text,
                StringComparison.Ordinal)) return null;
        return new(scenario, entry.StartOffset, entry.EndOffsetExclusive, entry.Text);
    }

    public WccScenarioContentRawEditDraft? BeginAddWccScenarioContentRawEdit(string template,
        IReadOnlyList<string>? placeholders = null)
    {
        if (BeginWccScenarioSourceRawEdit() is not { } scenario) return null;
        var position = scenario.Text.LastIndexOf("[/scenario]", StringComparison.OrdinalIgnoreCase);
        if (position < 0) return null;
        // Insert before the closing line, outside preceding conditional shells.
        var lineStart = scenario.Text.LastIndexOf('\n', position);
        position = lineStart < 0 ? position : lineStart + 1;
        var newline = scenario.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = template.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Replace("\n", newline, StringComparison.Ordinal);
        if (!text.EndsWith(newline, StringComparison.Ordinal)) text += newline;
        return new(scenario, position, position, text, placeholders);
    }

    public string? ValidateWccScenarioContentRawEdit(WccScenarioContentRawEditDraft draft, string raw)
    {
        var sourceProblem = ValidateWccInitialFieldRawEdit(draft.Scenario, draft.Candidate(raw));
        if (sourceProblem is not null) return sourceProblem;
        if (draft.Placeholders.Any(placeholder => raw.Contains(placeholder, StringComparison.Ordinal)))
            return "Fill in the macro's required arguments before applying this source change.";
        if (draft.RequiredMacroName is { } name)
        {
            var invocations = WmlMacroInvocationSyntaxScanner.Scan(raw)
                .Where(invocation => invocation.Name == name && !invocation.IsInsideMacroDefinition).ToArray();
            if (invocations.Length == 0 || invocations.Any(invocation => invocation.Arguments.Count < draft.Placeholders.Count))
                return "Fill in the selected macro's required arguments before applying this source change.";
        }
        return null;
    }

    public bool ApplyWccScenarioContentRawEdit(WccScenarioContentRawEditDraft draft, string raw)
    {
        if (ValidateWccScenarioContentRawEdit(draft, raw) is { } problem)
        {
            Status = problem;
            return false;
        }
        return ApplyWccInitialFieldRawEdit(draft.Scenario, draft.Candidate(raw));
    }

    private IReadOnlySet<WccPackageRevisionId>? ScenarioContentVisibleRevisions =>
        _macroLibrarySnapshot is { } snapshot && _staticRootRevisionSelection is { } root &&
        ReferenceEquals(root.Snapshot, snapshot)
            ? WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, root.RootRevisionId)
            : null;

    public IReadOnlyList<CatalogPickerOptionVm> GetWccScenarioMacroOptions(bool schedule)
    {
        if (_macroLibrarySnapshot is not { } snapshot || ScenarioContentVisibleRevisions is not { } visible)
            return [];
        var registry = WccLibraryMacroCatalogProjection.BuildRegistry(snapshot, visible);
        var names = schedule
            ? WccMacroCatalogBuilder.Build(registry).Where(entry => entry.PurposeKind == WccMacroPurposeKind.Schedule)
                .Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : registry.Entries.Where(entry => entry.DirectBodyTagNames.Contains("music", StringComparer.OrdinalIgnoreCase))
                .Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!schedule)
        {
            // Display suggestions only. Composite macros retain their complete invocation.
            bool added;
            do
            {
                added = false;
                foreach (var entry in registry.Entries)
                    if (entry.DirectInvokedMacroNames.Any(names.Contains)) added |= names.Add(entry.Name);
            } while (added);
        }
        IReadOnlyList<WmlMacroExpansionDefinition> scheduleDefinitions = schedule ? GetWccMusicScheduleDefinitions(names) : [];
        var scheduleNames = schedule ? WmlScheduleMacroClassifier.GetScheduleMacroNames(scheduleDefinitions) : null;
        var scheduleImages = schedule ? CreateWccScheduleImagePreviews(scheduleDefinitions) : null;
        return snapshot.FindEntities(WccEntityTypes.MacroDefinition)
            .Where(entity => entity.ExportSymbol is { } symbol && names.Contains(symbol) &&
                WccLibraryRevisionVisibility.ContainsEntity(snapshot, visible, entity) &&
                (!schedule || scheduleNames!.Contains(symbol) &&
                    snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file) &&
                    scheduleDefinitions.Any(definition => definition.Name == symbol &&
                        PathsEqual(definition.SourcePath, file.StoragePath) &&
                        (definition.PackageRevisionId == file.PackageRevisionId.ToString() ||
                         definition.PackageRevisionId is null && PathsEqual(file.StoragePath, _currentSourcePath ?? LoadedPath)) &&
                        definition.SourceStartLine == entity.SourceSpan?.StartLine)))
            .Select(entity =>
            {
                var preview = scheduleImages?.Macro(entity.ExportSymbol!);
                return new CatalogPickerOptionVm
                {
                    ValueId = entity.ExportSymbol!,
                    DisplayName = entity.ExportSymbol + " [" + snapshot.FindPackageForEntity(entity.Id)?.Name + "]",
                    LibraryEntityId = entity.Id,
                    LibraryPackageId = snapshot.FindPackageForEntity(entity.Id)?.Id,
                    PreviewPathResolver = preview?.PreviewPathResolver,
                    PreviewImageExpression = preview?.PreviewImageExpression,
                    PreviewImageExpressionResolver = preview?.PreviewImageExpressionResolver,
                    PreviewHint = preview?.PreviewHint,
                    EmissionKind = CatalogPickerEmissionKind.MacroInvocation
                };
            }).ToArray();
    }

    public async Task<WccScenarioContentRawEditDraft?> BeginWccScenarioMacroRawEditAsync(
        CatalogPickerSelection selection, bool schedule)
    {
        if (selection.EmissionKind != CatalogPickerEmissionKind.MacroInvocation ||
            _macroLibrarySnapshot is not { } snapshot || selection.LibraryEntityId is not { } id ||
            !GetWccScenarioMacroOptions(schedule).Any(option => option.LibraryEntityId == id &&
                option.LibraryPackageId == selection.LibraryPackageId &&
                option.ValueId == selection.EmittedValue) ||
            BeginAddWccScenarioContentRawEdit(string.Empty) is not { } draft) return null;
        try
        {
            var content = await WccLibraryEntityContentLoader.LoadAsync(snapshot, id);
            if (!ReferenceEquals(snapshot, _macroLibrarySnapshot) || !content.MatchesIndexedContentHash ||
                ValidateWccInitialFieldRawEdit(draft.Scenario, draft.Scenario.Text) is not null ||
                ScenarioContentVisibleRevisions is not { } visible ||
                !snapshot.EntitiesById.TryGetValue(id, out var entity) ||
                !WccLibraryRevisionVisibility.ContainsEntity(snapshot, visible, entity)) return null;
            var definitions = WmlMacroDefinitionScanner.Scan(content.EntityRawText)
                .Where(definition => definition.Name == selection.EmittedValue).ToArray();
            if (definitions.Length != 1) return null;
            var resolved = new WccLibraryMacroExpansionDefinitionResolver(snapshot, visible,
                _currentSourcePath ?? LoadedPath).FindDefinitions(selection.EmittedValue);
            if (resolved.Count != 1 ||
                !PathsEqual(resolved[0].SourcePath, content.SourceFile.StoragePath) ||
                !string.Equals(resolved[0].RawText, definitions[0].RawText, StringComparison.Ordinal) ||
                _currentSourceMacroDefinitions.Any(definition => definition.Name == selection.EmittedValue &&
                    !PathsEqual(_currentSourcePath ?? LoadedPath, content.SourceFile.StoragePath)))
            {
                Status = "This preset has a different or ambiguous definition in the current project. Use its authored RAW source.";
                return null;
            }
            var header = definitions[0].RawText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .First(line => line.TrimStart().StartsWith("#define ", StringComparison.Ordinal)).Trim();
            var parameters = WmlInlineCommentSplitter.Split(header["#define".Length..]).Value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1)
                .Select(parameter => "<" + parameter + ">").ToArray();
            var invocation = "{" + selection.EmittedValue +
                (parameters.Length == 0 ? "" : " " + string.Join(" ", parameters)) + "}";
            return BeginAddWccScenarioContentRawEdit(invocation, parameters);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = "The selected preset source is unavailable. Refresh the Library or use authored RAW source.";
            return null;
        }
    }

    public IReadOnlyList<CatalogPickerOptionVm> GetWccScenarioMusicFileOptions()
    {
        if (_macroLibrarySnapshot is not { } snapshot || ScenarioContentVisibleRevisions is not { } visible) return [];
        return snapshot.SourceFilesById.Values
            .Where(file => visible.Contains(file.PackageRevisionId) && file.FileType == WccSourceFileTypes.Audio)
            .Select(file => new { File = file, Path = file.RelativePath.Replace('\\', '/') })
            .Where(item => item.Path.StartsWith("music/", StringComparison.OrdinalIgnoreCase) ||
                item.Path.Contains("/music/", StringComparison.OrdinalIgnoreCase))
            .Select(item => new CatalogPickerOptionVm
            {
                ValueId = item.Path[(item.Path.IndexOf("music/", StringComparison.OrdinalIgnoreCase) + 6)..],
                DisplayName = item.Path,
                EmissionKind = CatalogPickerEmissionKind.Default
            }).ToArray();
    }

    public WccScenarioContentRawEditDraft? BeginWccScenarioMusicFileRawEdit(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        var definition = ScenarioInitialFieldCatalog.TryGet("scenario", "map_file", out var field) ? field : null;
        if (definition is null || WccScenarioFieldValueCodec.ValidateInput(definition, file) is not null) return null;
        var encoded = WccScenarioFieldValueCodec.EncodeLiteral(definition, file);
        return BeginAddWccScenarioContentRawEdit("[music]\n    name=" + encoded + "\n[/music]");
    }

    private void RaiseWccScenarioContentChanged()
    {
        RefreshWccMusicScheduleEditors();
        OnPropertyChanged(nameof(WccScenarioContentEntries));
        OnPropertyChanged(nameof(SelectedWccScenarioContent));
        OnPropertyChanged(nameof(CanEditWccScenarioContent));
        OnPropertyChanged(nameof(HasWccScenarioSource));
        OnPropertyChanged(nameof(WccScenarioContentGuidance));
    }
}
