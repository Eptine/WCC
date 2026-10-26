namespace Wcc.Core.Scenarios;

public static class StoryPartSchema
{
    public static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "story",
        "text_alignment",
        "text_layout",
        "show_title",
        "title",
        "title_alignment",
        "title_position",
        "background",
        "scale_background",
        "music",
        "sound",
        "voice"
    };
}