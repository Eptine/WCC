namespace Wcc.Core.Scenarios;

/// <summary>
/// Represents one [story] block inside a [scenario].
/// </summary>
public sealed class ScenarioStory
{
    public List<StoryPart> Parts { get; } = new();
}