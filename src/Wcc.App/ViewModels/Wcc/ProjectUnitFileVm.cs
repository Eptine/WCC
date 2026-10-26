using System.Linq;
using Wcc.App.ViewModels;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels.Wcc;

public sealed class ProjectUnitFileVm : ViewModelBase
{
    public ProjectUnitFileVm(UnitFileAsset asset)
    {
        Asset = asset;
    }

    public UnitFileAsset Asset { get; }

    public string InternalId => Asset.InternalId;

    public string DisplayName
    {
        get
        {
            var firstUnitId = Asset.Documents.FirstOrDefault()?.Unit.Id;
            if (!string.IsNullOrWhiteSpace(firstUnitId))
                return $"{firstUnitId}  ({Asset.FileName})";

            return Asset.FileName;
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(DisplayName));
    }
}