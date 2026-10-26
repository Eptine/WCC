using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static class StoryPartPatcher
{
    public static void Apply(StoryPart part, WmlEditableBlock block)
    {
        Set(block, "story", part.StoryText);
        Set(block, "text_alignment", part.TextAlignment);
        Set(block, "text_layout", part.TextLayout);

        Set(block, "show_title", part.ShowTitle);
        Set(block, "title", part.Title);
        Set(block, "title_alignment", part.TitleAlignment);
        Set(block, "title_position", part.TitlePosition);

        Set(block, "background", part.Background);
        Set(block, "scale_background", part.ScaleBackground);
        Set(block, "music", part.Music);
        Set(block, "sound", part.Sound);
        Set(block, "voice", part.Voice);
    }

    private static void Set(WmlEditableBlock b, string key, string? value)
    {
        if (value is null) return;
        b.SetKnownValue(key, value);
    }
}