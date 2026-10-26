using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>Plain audio filename observation; not evaluation, resource lookup or persistence authority.</summary>
public static class WccScenarioStoryAudioValues
{
    public static bool TryReadPlainEntries(string? raw, string key, out IReadOnlyList<string> entries)
    {
        entries = Array.Empty<string>();
        if (key is not ("music" or "sound" or "voice")) return false;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var value = raw.Trim();
        if (value.Length > 128 * 1024 || value.StartsWith('_') && value[1..].TrimStart().StartsWith('"') ||
            !WccScenarioFieldValueCodec.IsSupportedLiteralWml(value)) return false;
        var decoded = WccScenarioFieldValueCodec.DecodeLiteral(value);
        if (decoded.Length == 0) return true;
        var candidates = decoded.Split(',').Select(entry => entry.Trim()).ToArray();
        if (candidates.Length > 256 || key == "music" && candidates.Length != 1 || candidates.Any(entry =>
            entry.Length == 0 || !WmlResourceReferenceSyntax.ParseBinaryFilename(entry).IsComplete)) return false;
        // Keep duplicate entries: they contribute to the engine's random selection weighting.
        entries = Array.AsReadOnly(candidates); return true;
    }

    public static bool ReferencesUpload(string? raw, WccScenarioStoryUploadReceipt upload) =>
        upload.Slot.EntryId is null && WccScenarioFieldValueCodec.DecodeLiteral(raw) == upload.RelativeReference ||
        upload.Slot.FieldKey is "sound" or "voice" && TryReadPlainEntries(raw, upload.Slot.FieldKey, out var entries) &&
            entries.Contains(upload.RelativeReference, StringComparer.Ordinal);

    public static string? EncodePlainEntries(IEnumerable<string> entries, string key)
    {
        var values = entries.ToArray();
        if (values.Length == 0) return null;
        var raw = "\"" + string.Join(",", values).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        if (!TryReadPlainEntries(raw, key, out var read) || !read.SequenceEqual(values, StringComparer.Ordinal))
            throw new ArgumentException("Choose plain audio filenames; music supports one track and sound/voice support random alternatives.", nameof(entries));
        return raw;
    }
}
