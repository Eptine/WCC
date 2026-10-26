namespace Wcc.App.Services;

/// <summary>Explicit run-local context. Empty entry keeps independent authored-file checks.</summary>
public sealed record WmlTesterCampaignOptions(string EntryPath = "", string DifficultyDefine = "")
{
    /// <summary>Optional add-ons namespace used to capture directory presence, without loading siblings.</summary>
    public string AddonsNamespacePath { get; init; } = "";

    /// <summary>Explicit add-on source folders, one per line. Parsed only by the run-local source capture.</summary>
    public string DependencyRoots { get; init; } = "";

    /// <summary>Check all owned campaign CFGs from one capture; dependencies remain support sources.</summary>
    public bool CheckWholeCampaign { get; init; }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(EntryPath);
}
