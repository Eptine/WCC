using System.Text;
using System.Xml;
using Wcc.Core.Scenarios;

namespace Wcc.Core.Projects.Creation;

/// <summary>Shared literal serialization and exclusive new-file writing for creator workflows.</summary>
public static class WccCreationSourceWriter
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string? ValidateText(string? value, bool multiline = false)
    {
        if (value is null) return "A text value is required.";
        try { XmlConvert.VerifyXmlChars(value); }
        catch (XmlException) { return "The value contains an unsupported control character or incomplete Unicode character."; }
        return WccScenarioFieldValueCodec.ValidateInput(Field(multiline, false), value);
    }

    public static string EncodeText(string value, bool translated = false, bool multiline = false)
    {
        var issue = ValidateText(value, multiline);
        if (issue is not null) throw new ArgumentException(issue, nameof(value));
        // A translated field is multiline in the shared catalog. Validate the
        // requested single-line policy above before delegating its literal codec.
        return WccScenarioFieldValueCodec.EncodeLiteral(Field(multiline, translated),
            value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
    }

    public static async Task WriteNewTextAsync(string path, string text, CancellationToken cancellationToken = default)
        => await WriteNewTextAsync(path, text, cancellationToken, null).ConfigureAwait(false);

    internal static async Task WriteNewTextAsync(string path, string text, CancellationToken cancellationToken,
        Action<string>? acquired)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        acquired?.Invoke(path);
        await using var writer = new StreamWriter(stream, Utf8, 81920, leaveOpen: true);
        await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task CopyNewAssetAsync(WccCreationAssetCopy asset, string destination,
        CancellationToken cancellationToken, Action<string>? acquired = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectLinkedFile(asset.SourcePath);
        await using var source = new FileStream(asset.SourcePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length > 64L * 1024 * 1024) throw new IOException("A selected campaign image exceeds the 64 MiB creation limit.");
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous);
        acquired?.Invoke(destination);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void RejectLinkedFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("A selected image must be an ordinary file, not a linked file or directory.");
        WccCampaignCreationPlanner.ValidateDirectoryRoute(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    private static ScenarioInitialFieldDefinition Field(bool multiline, bool translated) => new(
        "creator_text", "Text", "Creation",
        translated ? ScenarioInitialFieldKind.TranslatableText : multiline ? ScenarioInitialFieldKind.Multiline : ScenarioInitialFieldKind.Text,
        new(ScenarioInitialFieldDefaultKind.None, null, ""), []);
}
