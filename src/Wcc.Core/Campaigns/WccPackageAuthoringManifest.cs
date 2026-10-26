using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wcc.Core.Library;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public sealed record WccPackageAuthoredDependency(WccCampaignDependencySpec Spec, string LoaderHash, string RecipeHash);
public sealed record WccPackageAuthoredChapter(string Id, string CampaignId, string CampaignDefine,
    string ScenarioDirectory, string TextDomain, IReadOnlyList<string> DifficultyDefines, bool IsPrimary = false);
/// <summary>Only exact listed files are cleanup candidates. Text hashes use decoded source text; binary hashes use raw bytes.</summary>
public sealed record WccPackageOwnedFile(string OwnerKind, string OwnerId, string RelativePath, string Role,
    string ContentHash, bool IsBinary = false);

/// <summary>Portable authoring intent. Source bytes and the checked writer, not these records alone, authorize changes.</summary>
public sealed record WccPackageAuthoringDocument
{
    public string Schema { get; init; } = WccPackageAuthoringManifest.Schema;
    public int SchemaVersion { get; init; } = WccPackageAuthoringManifest.CurrentSchemaVersion;
    public int GeneratorVersion { get; init; } = 1;
    public required string ProjectAddonId { get; init; }
    public IReadOnlyList<WccPackageAuthoredDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<WccPackageAuthoredChapter> Chapters { get; init; } = [];
    public IReadOnlyList<WccPackageOwnedFile> OwnedFiles { get; init; } = [];
    public WccCampaignDependencyMetadataReceipt? DependencyMetadata { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record WccPackageAuthoringManifestSnapshot(WmlStaticSource? Source, WccPackageAuthoringDocument Document);

/// <summary>
/// Shared captured companion format. Opening never rewrites it. Public schema 1 is permanent:
/// future incompatible schemas require an explicit checked migration; unknown optional root data
/// survives ordinary edits. No migration or direct filesystem writer lives in this service.
/// </summary>
public static class WccPackageAuthoringManifest
{
    public const string RelativePath = ".wcc/package-authoring.json";
    public const string Schema = "wcc.package-authoring";
    public const int CurrentSchemaVersion = 1;
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static WccPackageAuthoringDocument Empty(string addonId)
    {
        if (WccCampaignCreationTokenPolicy.ValidateAddonId(addonId) is { } issue) throw new ArgumentException(issue, nameof(addonId));
        return new() { ProjectAddonId = addonId };
    }
    public static WccPackageAuthoringManifestSnapshot Read(WmlStaticSource main, string addonId, IReadOnlyList<WmlStaticSource> sources)
    {
        ArgumentNullException.ThrowIfNull(main); ArgumentNullException.ThrowIfNull(sources);
        var path = Path.Combine(Path.GetDirectoryName(main.Path)!, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var matches = sources.Where(source => PathEquals(source.Path, path)).Take(2).ToArray();
        if (matches.Length == 0) return new(null, Empty(addonId));
        if (matches.Length != 1 || !matches[0].IsAuthored) throw new InvalidOperationException("The package authoring companion is ambiguous; use RAW.");
        var source = matches[0];
        if (Encoding.UTF8.GetByteCount(source.Text) > MaxBytes) throw new InvalidOperationException("The package authoring companion exceeds its captured text bound; use RAW.");
        WccPackageAuthoringDocument document;
        try
        {
            using var syntax = JsonDocument.Parse(source.Text, new JsonDocumentOptions { MaxDepth = 64 });
            RejectDuplicateProperties(syntax.RootElement);
            if (syntax.RootElement.ValueKind != JsonValueKind.Object || new[] { "schema", "schemaVersion", "generatorVersion", "projectAddonId", "dependencies", "chapters", "ownedFiles", "dependencyMetadata" }.Any(name => !syntax.RootElement.TryGetProperty(name, out _)))
                throw new JsonException("The public authoring envelope is incomplete.");
            document = JsonSerializer.Deserialize<WccPackageAuthoringDocument>(source.Text, Json) ?? throw new JsonException("Empty document.");
        }
        catch (Exception issue) when (issue is JsonException or NotSupportedException or ArgumentException)
        { throw new InvalidOperationException("The package authoring companion is malformed; preserve it and use RAW.", issue); }
        Validate(document);
        if (document.ProjectAddonId != addonId) throw new InvalidOperationException("The package authoring companion belongs to another add-on; use RAW.");
        return new(source, document);
    }
    public static string Serialize(WccPackageAuthoringDocument document)
    {
        Validate(document);
        var text = JsonSerializer.Serialize(document, Json) + "\n";
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new InvalidOperationException("The package authoring companion exceeds its captured text bound.");
        return text;
    }
    public static void Validate(WccPackageAuthoringDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Schema != Schema || document.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidOperationException("This package authoring schema needs a compatible WCC or an explicit migration. Preserve its companion and use RAW.");
        if (document.GeneratorVersion < 1 || WccCampaignCreationTokenPolicy.ValidateAddonId(document.ProjectAddonId) is not null ||
            document.Dependencies is null || document.Chapters is null || document.OwnedFiles is null ||
            document.Dependencies.Count > 64 || document.Chapters.Count > 32 || document.OwnedFiles.Count > 100_000 ||
            document.Dependencies.Any(item => item is null || item.Spec is null || !Hash(item.LoaderHash) || !Hash(item.RecipeHash)) ||
            document.Chapters.Any(item => item is null || WccCampaignCreationTokenPolicy.ValidateAddonId(item.Id) is not null ||
                !Symbol(item.CampaignId) || !Symbol(item.CampaignDefine) ||
                WccCampaignCreationTokenPolicy.ValidateTextDomain(item.TextDomain) is not null || !Relative(item.ScenarioDirectory) ||
                item.DifficultyDefines is null || item.DifficultyDefines.Count is < 1 or > 16 || item.DifficultyDefines.Any(value => !Symbol(value)) ||
                item.DifficultyDefines.Distinct(StringComparer.Ordinal).Count() != item.DifficultyDefines.Count) ||
            document.OwnedFiles.Any(item => item is null || item.OwnerKind is not ("dependency" or "chapter") ||
                string.IsNullOrEmpty(item.OwnerId) || !Relative(item.RelativePath) || string.IsNullOrEmpty(item.Role) || !Hash(item.ContentHash)) ||
            document.OwnedFiles.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.OwnedFiles.Count ||
            document.Chapters.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Chapters.Count ||
            document.Chapters.Count(item => item.IsPrimary) > 1)
            throw new InvalidOperationException("The package authoring companion has invalid identities or file receipts; use RAW.");
        if (document.Extensions?.Keys.Any(name => name is "schema" or "schemaVersion" or "generatorVersion" or "projectAddonId" or "dependencies" or "chapters" or "ownedFiles" or "dependencyMetadata") == true)
            throw new InvalidOperationException("Optional package metadata cannot replace authoring fields.");
    }
    private static bool Symbol(string? value) => value is { Length: > 0 and <= 192 } &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    private static bool Relative(string? path) => path is { Length: > 0 and <= 1024 } && !path.StartsWith('/') &&
        !path.Contains('\\') && path.Split('/').All(part => part.Length != 0 && part is not ("." or "..") &&
            part.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.'));
    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static bool PathEquals(string left, string right) => string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException("Duplicate property."); RejectDuplicateProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
