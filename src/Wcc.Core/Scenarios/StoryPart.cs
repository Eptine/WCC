namespace Wcc.Core.Scenarios;

/// <summary>
/// Represents one [part] page inside a [story] sequence.
/// Keep values as raw WML-ish strings for v0.1 safety.
/// </summary>
public sealed class StoryPart
{
    // Main text keys
    public string? StoryText { get; set; }
    public string? TextAlignment { get; set; }
    public string? TextLayout { get; set; }

    // Title keys
    public string? ShowTitle { get; set; }
    public string? Title { get; set; }
    public string? TitleAlignment { get; set; }
    public string? TitlePosition { get; set; }

    // Image / sound keys
    public string? Background { get; set; }
    public string? ScaleBackground { get; set; }
    public string? Music { get; set; }
    public string? Sound { get; set; }
    public string? Voice { get; set; }
}