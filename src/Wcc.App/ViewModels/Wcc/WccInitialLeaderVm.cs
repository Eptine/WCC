using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>An exact evaluated leader owner for display; never an authored document.</summary>
public sealed class WccInitialLeaderVm : ViewModelBase
{
    private string _displayName;
    public WccInitialLeaderVm(WmlStaticScalarOwner owner, bool isLegacyInline,
        string displayName, string representationNotice)
    {
        Owner = owner;
        IsLegacyInline = isLegacyInline;
        _displayName = displayName;
        RepresentationNotice = representationNotice;
    }

    public WmlStaticScalarOwner Owner { get; }
    public bool IsLegacyInline { get; }
    public string DisplayName => _displayName;
    public string RepresentationNotice { get; }
    public string SourceLabel => RepresentationNotice;
    public string FormatBadge => IsLegacyInline ? "L" : "N";
    public WccFieldImagePreviewVm? Thumbnail { get; private set; }

    internal void RefreshPreview(string displayName, WccFieldImagePreviewVm thumbnail)
    {
        if (_displayName != displayName)
        {
            _displayName = displayName;
            OnPropertyChanged(nameof(DisplayName));
        }
        if (!ReferenceEquals(Thumbnail, thumbnail))
        {
            Thumbnail = thumbnail;
            OnPropertyChanged(nameof(Thumbnail));
        }
    }
}
