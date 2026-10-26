using Wcc.Core.Wml;

namespace Wcc.Core.Library;

public sealed record WccAddonMetadataIssue(
    string Code,
    string Message,
    WccDiagnosticSeverity Severity);

public sealed class WccAddonMetadataReadResult
{
    public required WccAddonPackageMetadata Metadata { get; init; }
    public required IReadOnlyList<WccAddonMetadataIssue> Issues { get; init; }
}

/// <summary>
/// Conservatively reads the package metadata Wesnoth places beside an add-on. Downloaded
/// add-ons use _info.cfg; a local authoring tree may only have _server.pbl. This reader does
/// not preprocess either file: conditional, repeated, or dynamic declarations stay in the
/// raw fields and are not used as dependency-graph facts.
/// </summary>
public static class WccAddonMetadataReader
{
    private const string InstalledInfoFileName = "_info.cfg";
    private const string ServerPublishFileName = "_server.pbl";

    public static WccAddonMetadataReadResult Read(string packageRoot)
    {
        if (string.IsNullOrWhiteSpace(packageRoot))
            throw new ArgumentException("Package root must be set.", nameof(packageRoot));

        var fullRoot = Path.GetFullPath(packageRoot);
        var addonId = Path.GetFileName(fullRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var issues = new List<WccAddonMetadataIssue>();

        var installedInfoPath = Path.Combine(fullRoot, InstalledInfoFileName);
        var serverPublishPath = Path.Combine(fullRoot, ServerPublishFileName);
        var metadataPath = File.Exists(installedInfoPath)
            ? installedInfoPath
            : File.Exists(serverPublishPath)
                ? serverPublishPath
                : null;

        if (metadataPath is null)
        {
            issues.Add(new WccAddonMetadataIssue(
                "addon.metadata_missing",
                $"Add-on '{addonId}' has neither _info.cfg nor _server.pbl; its folder name and default Core relationship were inferred.",
                WccDiagnosticSeverity.Information));
            return CreateResult(addonId, metadataSourceFileName: null, rawMetadataText: null, new(), issues);
        }

        string text;
        try
        {
            text = File.ReadAllText(metadataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new WccAddonMetadataIssue(
                "addon.metadata_read_failed",
                $"Could not read metadata for add-on '{addonId}': {ex.Message}",
                WccDiagnosticSeverity.Warning));
            return CreateResult(addonId, Path.GetFileName(metadataPath), rawMetadataText: null, new(), issues);
        }

        var sourceFileName = Path.GetFileName(metadataPath);
        WmlBlock? metadataBlock;
        if (string.Equals(sourceFileName, InstalledInfoFileName, StringComparison.OrdinalIgnoreCase))
        {
            var infoBlocks = WmlAllBlockScanner.FindBlocks(text, "info");
            if (infoBlocks.Count != 1)
            {
                issues.Add(new WccAddonMetadataIssue(
                    "addon.metadata_info_block_ambiguous",
                    $"Add-on '{addonId}' metadata contains {infoBlocks.Count} [info] blocks; no conditional choice was guessed.",
                    WccDiagnosticSeverity.Warning));
                return CreateResult(addonId, sourceFileName, text, new(), issues);
            }

            metadataBlock = infoBlocks[0];
        }
        else
        {
            var infoBlocks = WmlAllBlockScanner.FindBlocks(text, "info");
            metadataBlock = infoBlocks.Count switch
            {
                0 => new WmlBlock("info", $"[info]{Environment.NewLine}{text}{Environment.NewLine}[/info]"),
                1 => infoBlocks[0],
                _ => null
            };

            if (metadataBlock is null)
            {
                issues.Add(new WccAddonMetadataIssue(
                    "addon.metadata_info_block_ambiguous",
                    $"Add-on '{addonId}' metadata contains several [info] blocks; no conditional choice was guessed.",
                    WccDiagnosticSeverity.Warning));
                return CreateResult(addonId, sourceFileName, text, new(), issues);
            }
        }

        var candidateKeys = DiscoverCandidateKeys(metadataBlock.RawText);
        var editable = WmlEditableBlockParser.Parse(metadataBlock, candidateKeys);
        var rawFields = editable.Known.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Value.Trim(),
            StringComparer.OrdinalIgnoreCase);

        string? ReadStaticScalar(string key)
        {
            if (!rawFields.TryGetValue(key, out var raw))
                return null;

            if (LooksDynamic(raw))
            {
                issues.Add(new WccAddonMetadataIssue(
                    "addon.metadata_dynamic_field",
                    $"Add-on '{addonId}' declares dynamic '{key}' metadata; WCC preserved it but did not use it for package planning.",
                    WccDiagnosticSeverity.Warning));
                return null;
            }

            return DecodeScalar(raw);
        }

        var title = NullIfWhiteSpace(ReadStaticScalar("title"));
        var declaredType = NullIfWhiteSpace(ReadStaticScalar("type"))?.ToLowerInvariant();
        var version = NullIfWhiteSpace(ReadStaticScalar("version"));
        var declaredCore = ReadStaticScalar("core");
        var dependencies = ParseDependencies(ReadStaticScalar("dependencies"));

        var metadata = new WccAddonPackageMetadata
        {
            AddonId = addonId,
            Title = title,
            DeclaredType = declaredType,
            DeclaredVersion = version,
            DeclaredCore = declaredCore,
            MetadataSourceFileName = sourceFileName,
            RawMetadataText = text,
            DeclaredDependencyIds = dependencies,
            RawFields = rawFields
        };

        return new WccAddonMetadataReadResult
        {
            Metadata = metadata,
            Issues = issues
        };
    }

    public static string? GetDeclaredVersion(WccAddonPackageMetadata metadata)
    {
        if (metadata is null) throw new ArgumentNullException(nameof(metadata));
        return metadata.DeclaredVersion;
    }

    private static WccAddonMetadataReadResult CreateResult(
        string addonId,
        string? metadataSourceFileName,
        string? rawMetadataText,
        Dictionary<string, string> rawFields,
        IReadOnlyList<WccAddonMetadataIssue> issues)
        => new()
        {
            Metadata = new WccAddonPackageMetadata
            {
                AddonId = addonId,
                MetadataSourceFileName = metadataSourceFileName,
                RawMetadataText = rawMetadataText,
                RawFields = rawFields
            },
            Issues = issues
        };

    private static HashSet<string> DiscoverCandidateKeys(string rawText)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in rawText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
                continue;

            var key = line[..equalsIndex].Trim();
            if (key.Length > 0 && key.All(character =>
                    char.IsLetterOrDigit(character) || character is '_' or '-'))
            {
                result.Add(key);
            }
        }

        return result;
    }

    private static List<string> ParseDependencies(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new List<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(seen.Add)
            .ToList();
    }

    private static bool LooksDynamic(string raw)
        => raw.Contains('{') || raw.Contains('}') || raw.Contains('$');

    private static string DecodeScalar(string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith('_'))
            value = value[1..].TrimStart();

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1];

        return value
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Trim();
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
