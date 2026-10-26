using System;
using System.Collections.Generic;
using System.IO;
using Wcc.Core.Campaigns;

namespace Wcc.App.ViewModels;

internal sealed record CampaignPresentationSource(CampaignFieldSourceLocation? Location,
    CampaignSourceEntry? RawEntry, string Description);

/// <summary>Path display delegates all mutations to the existing exact editor-row guard.</summary>
public sealed class CampaignPathDisplayRow : ViewModelBase
{
    private readonly CampaignWindowViewModel _editor;
    private readonly CampaignPresentationSource? _source;
    internal CampaignPathDisplayRow(CampaignWindowViewModel editor, CampaignPathEditorRow row,
        CampaignPresentationSource? source)
    { _editor = editor; EditorRow = row; _source = source; }

    internal CampaignPathEditorRow EditorRow { get; }
    public string Path { get => EditorRow.Path; set => EditorRow.Path = value; }
    public string TextPath
    {
        get => _editor.TryGetCampaignTextInput(EditorRow, "path", out var text) ? text ?? "" : Path;
        set
        {
            if (TextPath == (value ?? "")) return;
            if (_editor.BufferCampaignTextInput(EditorRow, "path", value ?? "",
                () => _editor.ContainsCampaignPathRow(EditorRow) && EditorRow.CanEdit,
                () => Path, text => Path = text ?? "", () => OnPropertyChanged(nameof(TextPath))))
                OnPropertyChanged(nameof(TextPath));
        }
    }
    public bool CommitTextInput()
    {
        var committed = _editor.CommitCampaignTextInputs(EditorRow, out var problem);
        if (!committed) _editor.Status = problem ?? "The typed path remains open.";
        return committed;
    }
    public string Addon => _editor.CampaignPathAddon(this);
    public string Purpose => SelectedKindChoice.Label;
    public string Information => Addon + " · " + Purpose + Environment.NewLine + Path + Environment.NewLine + SourceDescription;
    public IReadOnlyList<CampaignPathKindChoice> KindChoices => EditorRow.KindChoices;
    public CampaignPathKindChoice SelectedKindChoice
    { get => EditorRow.SelectedKindChoice; set => EditorRow.SelectedKindChoice = value; }
    public bool CanEdit => EditorRow.CanEdit;
    public bool IsReadOnly => !CanEdit;
    public CampaignFieldSourceLocation? SourceLocation => _source?.Location;
    public CampaignSourceEntry? RawEntry => _source?.RawEntry;
    public bool HasSource => SourceLocation is not null;
    public bool CanEditRaw => _editor.IsCurrentCampaignPathDisplayRow(this) &&
        _editor.CanEditCampaignMainRaw && SourceLocation is { } source &&
        string.Equals(source.SourcePath, _editor.LoadedPath, StringComparison.Ordinal) &&
        string.Equals(source.SourceText, _editor.GetCurrentMainSourceRawWml(), StringComparison.Ordinal);
    public string RawEditGuidance
    {
        get
        {
            if (CanEditRaw) return "Edit this exact path in its containing campaign WML. Save checks the proposed source.";
            if (!_editor.IsCurrentCampaignPathDisplayRow(this)) return "This path belongs to an older source view. Reload its current campaign source.";
            if (_editor.HasCampaignSourceDrafts || _editor.HasCampaignSourceChanges)
                return "Save or discard pending campaign changes before editing RAW source.";
            if (_editor.IsCampaignFieldSaveBusy || _editor.IsCampaignUiLoading || _editor.IsCampaignSourceLoading)
                return "Wait for the campaign to finish loading or saving before editing its source.";
            if (_editor.IsReadOnly) return "This campaign is open read-only. Its path source remains available for inspection.";
            if (SourceLocation is not { } source) return "No exact captured source range is available for this reference. Inspect the campaign main source.";
            if (_editor.CurrentProject?.SourcePath is { } main)
            {
                var root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(main))!;
                if (!System.IO.Path.GetFullPath(source.SourcePath).StartsWith(System.IO.Path.TrimEndingDirectorySeparator(root) + System.IO.Path.DirectorySeparatorChar,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return "This reference comes from Core or a dependency source. It is view-only in the current campaign; edit the provider separately.";
            }
            if (_editor.RequiresSelectedCampaignAdapter)
                return "This file belongs to a campaign package. Use WML Editor for complete-file RAW changes; shared paths may affect other chapters.";
            if (!string.Equals(source.SourcePath, _editor.LoadedPath, StringComparison.Ordinal))
                return "This reference is defined in another project file. Use WML Editor to edit that file's RAW source.";
            return "The exact source or writable campaign context is unavailable. Reload the campaign before editing RAW source.";
        }
    }
    public string SourceDescription => _source?.Description ??
        (CanEdit ? "New campaign reference; Save checks the folder and source context." :
            "No exact source range is available. Inspect the campaign main source.");

    internal void Refresh()
    {
        foreach (var property in new[] { nameof(Path), nameof(TextPath), nameof(SelectedKindChoice), nameof(CanEdit), nameof(IsReadOnly),
                     nameof(HasSource), nameof(CanEditRaw), nameof(RawEditGuidance), nameof(SourceDescription), nameof(Addon), nameof(Purpose), nameof(Information) }) OnPropertyChanged(property);
    }
}
