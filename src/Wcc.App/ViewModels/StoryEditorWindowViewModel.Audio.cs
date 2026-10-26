using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public sealed record StoryAudioEntries(string Key, IReadOnlyList<string> Entries, bool IsPlain, bool CanEdit, string? Problem);

public sealed partial class StoryEditorWindowViewModel
{
    public StoryAudioEntries ReadAudioEntries(StoryPartVm part, string key)
    {
        if (key is not ("music" or "sound" or "voice")) throw new ArgumentException("Choose a Story audio field.", nameof(key));
        var field = part.Field(key);
        var define = Scope ?? PreviewDefine;
        var plain = WccScenarioStoryAudioValues.TryReadPlainEntries(field.Raw(define), key, out var entries);
        var problem = !plain ? "This audio expression is preserved as raw WML. Use the raw field to edit it." :
            !part.AvailableDefines.Contains(define) ? "This slide is hidden in the selected difficulty." :
            !CanEdit || !part.CanEdit || !field.CanEdit || !Parts.Contains(part) || !ReferenceEquals(part, SelectedPart)
                ? field.Diagnostic ?? part.Diagnostic ?? "This audio value is inherited or read-only." : null;
        if (problem is null && Scope is null && part.AvailableDefines.Any(context =>
            !WccScenarioStoryAudioValues.TryReadPlainEntries(field.Raw(context), key, out var other) || !entries.SequenceEqual(other, StringComparer.Ordinal)))
            problem = "These audio entries differ by difficulty. Select one difficulty to edit its rows.";
        return new(key, entries, plain, problem is null, problem);
    }

    public bool SetAudioEntry(StoryPartVm part, string key, int? index, string reference)
    {
        var state = ReadAudioEntries(part, key);
        if (!state.CanEdit) { Status = state.Problem; return false; }
        try
        {
            var entries = state.Entries.ToList();
            if (index is { } position)
            {
                if (position < 0 || position >= entries.Count) return false;
                entries[position] = reference;
            }
            else if (key == "music") { entries.Clear(); entries.Add(reference); }
            else entries.Add(reference);
            part.Field(key).Text = WccScenarioStoryAudioValues.EncodePlainEntries(entries, key) ?? "";
            return true;
        }
        catch (ArgumentException error) { Status = error.Message; return false; }
    }

    public bool RemoveAudioEntry(StoryPartVm part, string key, int index)
    {
        var state = ReadAudioEntries(part, key);
        if (!state.CanEdit) { Status = state.Problem; return false; }
        if (index < 0 || index >= state.Entries.Count) return false;
        var entries = state.Entries.ToList(); entries.RemoveAt(index);
        part.Field(key).Text = WccScenarioStoryAudioValues.EncodePlainEntries(entries, key) ?? "";
        return true;
    }

    private MediaTarget? CaptureAudioEntryTarget(StoryPartVm part, string key, int? index)
    {
        var state = ReadAudioEntries(part, key);
        if (!state.CanEdit || index is { } position && (position < 0 || position >= state.Entries.Count))
        { Status = state.Problem ?? "The selected audio row changed. Choose it again."; return null; }
        return CaptureMediaTarget(part, null, part.Field(key));
    }

    public async Task<bool> PickAudioEntryAsync(StoryPartVm part, string key, int? index,
        Func<CatalogPickerWindowViewModel, Task<CatalogPickerSelection?>> choose, Func<bool>? hostIsCurrent = null)
    {
        if (Session.AudioCatalog is not { } catalog || CaptureAudioEntryTarget(part, key, index) is not { } target) return false;
        using var lifetime = target;
        try
        {
            var define = Scope ?? PreviewDefine;
            var choices = await Task.Run(() => catalog.Build(key, define));
            if (!IsCurrentMediaTarget(target, hostIsCurrent)) return false;
            var selected = index is { } position ? ReadAudioEntries(part, key).Entries[position] : null;
            var picker = new CatalogPickerWindowViewModel(choices.Select(choice => choice.Option), selected, false,
                "Choose Story " + key, key == "music" ? "One music track per slide." : "One alternative is chosen at random when the slide is shown.");
            var selection = await choose(picker);
            if (selection is null || !IsCurrentMediaTarget(target, hostIsCurrent)) return false;
            var choice = choices.SingleOrDefault(choice => choice.Option.ToSelection() == selection)
                ?? throw new InvalidOperationException("The audio selection does not belong to this captured resource picker.");
            var revision = choice.Capture();
            SetMediaFile(target, choice.SourcePath, choice.IsDirectReference, revision, choice.Option.ValueId,
                key == "music" ? null : Guid.NewGuid().ToString("N"), reference =>
                { if (!SetAudioEntry(part, key, index, reference)) throw new InvalidOperationException(Status); });
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { Status = error.Message; return false; }
    }

    public async Task<bool> PickAudioEntryFileAsync(StoryPartVm part, string key, int? index,
        Func<Task<string?>> choose, Func<bool>? hostIsCurrent = null)
    {
        if (CaptureAudioEntryTarget(part, key, index) is not { } target) return false;
        using var lifetime = target;
        try
        {
            var path = await choose();
            if (path is null || !IsCurrentMediaTarget(target, hostIsCurrent)) return false;
            SetMediaFile(target, path, true, audioEntryId: key == "music" ? null : Guid.NewGuid().ToString("N"), assignReference: reference =>
            { if (!SetAudioEntry(part, key, index, reference)) throw new InvalidOperationException(Status); });
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { Status = error.Message; return false; }
    }
}
