namespace Wcc.App.ViewModels;

public sealed record CurrentSourceWmlView(
    string Title,
    string RawText,
    string? SourcePath,
    string DescriptionText);
