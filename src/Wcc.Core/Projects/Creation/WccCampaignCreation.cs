using Wcc.Core.Campaigns;

namespace Wcc.Core.Projects.Creation;

public enum WccCampaignCreationMode { Empty, Guided }

public sealed record WccCampaignDifficultySpec(string Label, string Description, string Image, bool IsDefault)
{
    public string? ImageFilePath { get; init; }
}

public sealed record WccCampaignImageSpec(string Reference, string? FilePath = null);

public sealed record WccCampaignCreationRequest
{
    public required string ParentDirectory { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string TextDomain { get; init; }
    public WccCampaignCreationMode Mode { get; init; }
    public string Description { get; init; } = "";
    public WccCampaignImageSpec? Icon { get; init; }
    public WccCampaignImageSpec? Image { get; init; }
    public WccCampaignImageSpec? Background { get; init; }
    public IReadOnlyList<WccCampaignDifficultySpec> Difficulties { get; init; } = [];
    public bool CreateStarterScenario { get; init; } = true;
    /// <summary>Additional selectable campaign entries; this request remains the original campaign.</summary>
    public IReadOnlyList<WccCampaignChapterSpec> Chapters { get; init; } = [];
    /// <summary>Portable dependency loading recipes shared with the campaign authoring wizard.</summary>
    public IReadOnlyList<WccCampaignDependencySpec> Dependencies { get; init; } = [];
    public WccProjectCoreRevisionPin? CoreRevision { get; init; }
}

public sealed record WccCreationSourceFile(string RelativePath, string Text);
public sealed record WccCreationAssetCopy(string SourcePath, string RelativePath);

/// <summary>A rendered creation recipe. Planning never writes or replaces project files.</summary>
public sealed class WccCampaignCreationPlan
{
    internal WccCampaignCreationPlan(string rootPath, IEnumerable<string> directories,
        IEnumerable<WccCreationSourceFile> files, IEnumerable<WccCreationAssetCopy> assets,
        WccProjectMetadata metadata)
    {
        RootPath = rootPath;
        MainFilePath = Path.Combine(rootPath, "_main.cfg");
        DirectoryPaths = Array.AsReadOnly(directories.Select(relative => Path.Combine(rootPath,
            relative.Replace('/', Path.DirectorySeparatorChar))).ToArray());
        Files = Array.AsReadOnly(files.ToArray());
        Assets = Array.AsReadOnly(assets.ToArray());
        Metadata = metadata;
    }

    public string RootPath { get; }
    public string MainFilePath { get; }
    public IReadOnlyList<string> DirectoryPaths { get; }
    public IReadOnlyList<WccCreationSourceFile> Files { get; }
    public IReadOnlyList<WccCreationAssetCopy> Assets { get; }
    public WccProjectMetadata Metadata { get; }
}

public sealed record WccCampaignCreationResult(string RootPath, string MainFilePath, WccProjectMetadata Metadata);
