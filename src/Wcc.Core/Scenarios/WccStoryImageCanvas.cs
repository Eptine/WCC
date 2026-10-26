namespace Wcc.Core.Scenarios;

/// <summary>An ordinary visible Core background layer supplies stable reference pixels for freely placed story images.</summary>
public static class WccStoryImageCanvas
{
    public const string ImageReference = "misc/blank-hex.png~SCALE(1920,1080)";
    public static StoryPreviewSize IntrinsicSize => new(1920, 1080);
    public static bool IsReference(string? expression) => expression == ImageReference;
}
