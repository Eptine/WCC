using System.Collections.ObjectModel;
using System.Linq;
using Wcc.Core.Campaigns;

namespace Wcc.App.ViewModels;

public sealed class CampaignCreditGroupRow : ViewModelBase
{
    private readonly CampaignWindowViewModel _editor;
    private readonly WccCampaignCreditGroup _original;
    private readonly ObservableCollection<CampaignCreditEntryRow> _entries = [];
    private string _title;

    internal CampaignCreditGroupRow(CampaignWindowViewModel editor, WccCampaignCreditGroup value)
    {
        _editor = editor; _original = value; _title = value.Title;
        Entries = new(_entries);
        foreach (var entry in value.Entries) _entries.Add(new(editor, this, entry));
    }
    internal string? Key => _original.Key;
    public string Title
    {
        get => _title;
        set
        {
            var text = value ?? "";
            if (text != _title) _editor.EditCampaignCreditsRow(this, () => _title = text,
                () => OnPropertyChanged(nameof(Title)));
        }
    }
    public ReadOnlyObservableCollection<CampaignCreditEntryRow> Entries { get; }
    public bool CanEdit => _editor.CanEditCampaignCredits && _editor.ContainsCampaignCreditGroup(this);
    public bool IsReadOnly => !CanEdit;
    internal bool Contains(CampaignCreditEntryRow row) => _entries.Contains(row);
    internal void Add(WccCampaignCreditEntry entry) => _entries.Add(new(_editor, this, entry));
    internal void Remove(CampaignCreditEntryRow entry) => _entries.Remove(entry);
    internal WccCampaignCreditGroup Capture() => _original with
    { Title = _title, Entries = _entries.Select(row => row.Capture()).ToArray() };
    internal void Refresh()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(IsReadOnly));
        foreach (var entry in _entries.ToArray()) entry.Refresh();
    }
}

public sealed class CampaignCreditEntryRow : ViewModelBase
{
    private readonly CampaignWindowViewModel _editor;
    private readonly WccCampaignCreditEntry _original;
    private string _name, _comment;
    internal CampaignCreditEntryRow(CampaignWindowViewModel editor, CampaignCreditGroupRow group,
        WccCampaignCreditEntry value)
    { _editor = editor; Group = group; _original = value; _name = value.Name; _comment = value.Comment; }
    internal CampaignCreditGroupRow Group { get; }
    internal string? Key => _original.Key;
    public string Name
    {
        get => _name;
        set
        {
            var text = value ?? "";
            if (text != _name) _editor.EditCampaignCreditsRow(this, () => _name = text,
                () => OnPropertyChanged(nameof(Name)));
        }
    }
    public string Comment
    {
        get => _comment;
        set
        {
            var text = value ?? "";
            if (text != _comment) _editor.EditCampaignCreditsRow(this, () => _comment = text,
                () => OnPropertyChanged(nameof(Comment)));
        }
    }
    public bool CanEdit => Group.CanEdit && Group.Contains(this);
    public bool IsReadOnly => !CanEdit;
    internal WccCampaignCreditEntry Capture() => _original with { Name = _name, Comment = _comment };
    internal void Refresh() { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(IsReadOnly)); }
}
