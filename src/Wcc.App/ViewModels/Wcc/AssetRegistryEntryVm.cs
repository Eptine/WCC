using System;
using System.Linq;
using Wcc.Core.References;
using Wcc.Core.Projects;

namespace Wcc.App.ViewModels.Wcc;

public sealed class AssetRegistryEntryVm : ViewModelBase
{
    public AssetRegistryEntryVm(WccAssetRegistryEntry entry)
    {
        Entry = entry;
    }

    public WccAssetRegistryEntry Entry { get; }

    public string DisplayName => $"{Entry.DisplayName} [{Entry.KindLabel}] x{Entry.UsageCount}";

    public string LookupPath => Entry.LookupPath;
    public string KindLabel => Entry.KindLabel;
    public int UsageCount => Entry.UsageCount;

    public string OriginsLabel
    {
        get
        {
            var labels = Entry.Origins
                .Select(o => o switch
                {
                    WccSourceOrigin.CoreInstall => "core",
                    WccSourceOrigin.WccGenerated => "wcc",
                    _ => "imported"
                })
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

            return string.Join(", ", labels);
        }
    }
}