using System.Text.Json;
using System.Text.Json.Nodes;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Scenarios;

namespace Wcc.Core.Campaigns;

/// <summary>Portable preset intent. An exact captured definition and the checked transaction still authorize every edit.</summary>
public sealed record WccPackagePresetReceipt(Guid Id, string Kind, string Symbol, WccCampaignPresetScope Scope,
    string SourcePath, string DefinitionHash, string TextDomain);

public sealed record WccPackageContentAuthoringSnapshot(bool IsPresent, int? Version,
    IReadOnlyList<WccPackagePresetReceipt> Presets, string? Issue)
{
    public bool CanMutate => Issue is null;
}

/// <summary>
/// Optional, independently versioned content extension of the public schema-1 companion.
/// This service only reads and patches an existing immutable document; it never captures,
/// serializes, upgrades on Open, or writes a file. Unknown JSON survives supported edits.
/// </summary>
public static class WccPackageContentAuthoringManifest
{
    public const string ExtensionName = "contentAuthoring";
    public const int CurrentVersion = 1;
    private const int MaxPresets = 10_000;

    public static WccPackageContentAuthoringSnapshot Read(WccPackageAuthoringDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        WccPackageAuthoringManifest.Validate(document);
        if (document.Extensions is null || !document.Extensions.TryGetValue(ExtensionName, out var content))
            return new(false, CurrentVersion, [], null);
        int? version = null;
        try
        {
            RequireObject(content);
            RejectDuplicateProperties(content);
            if (!content.TryGetProperty("version", out var encodedVersion) || !encodedVersion.TryGetInt32(out var value))
                throw new JsonException("The content extension needs an integer version.");
            version = value;
            if (value != CurrentVersion)
                return new(true, version, [], "This package content-authoring version is unsupported; preserve it and use RAW for presets.");
            if (!content.TryGetProperty("presets", out var records) || records.ValueKind != JsonValueKind.Array || records.GetArrayLength() > MaxPresets)
                throw new JsonException("The content extension needs a bounded preset list.");
            var presets = records.EnumerateArray().Select(ReadReceipt).ToArray();
            if (presets.Select(item => item.Id).Distinct().Count() != presets.Length)
                throw new JsonException("Preset receipt IDs must be unique throughout the package.");
            return new(true, version, Array.AsReadOnly(presets), null);
        }
        catch (Exception issue) when (issue is JsonException or ArgumentException or InvalidOperationException)
        {
            return new(true, version, [], "The package content-authoring extension is malformed; preserve it and use RAW for presets.");
        }
    }

    /// <summary>
    /// Composes content intent into the supplied exact document, including any structural
    /// mutations already composed by the caller. Source/activation/collision proof belongs
    /// to the shared preset planner and reviewer, never to these receipts alone.
    /// </summary>
    public static WccPackageAuthoringDocument Apply(WccPackageAuthoringDocument document,
        IReadOnlyList<WccPackagePresetReceipt> upserts, IReadOnlyList<Guid> removals)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(removals);
        var snapshot = Read(document);
        if (!snapshot.CanMutate) throw new InvalidOperationException(snapshot.Issue);
        if (upserts.Count == 0 && removals.Count == 0) return document;
        if (upserts.Count > MaxPresets || removals.Count > MaxPresets || upserts.Any(item => item is null) ||
            upserts.Select(item => item.Id).Distinct().Count() != upserts.Count ||
            removals.Any(id => id == Guid.Empty) || removals.Distinct().Count() != removals.Count ||
            upserts.Any(item => removals.Contains(item.Id)))
            throw new ArgumentException("Use one bounded content mutation for each nonempty preset ID.");
        foreach (var receipt in upserts)
        {
            ValidateReceipt(receipt);
            if (!SupportedKind(receipt.Kind)) throw new InvalidOperationException("This preset kind needs a compatible content editor; preserve its receipt.");
        }
        var original = snapshot.Presets.ToDictionary(item => item.Id);
        foreach (var id in removals)
        {
            if (!original.TryGetValue(id, out var receipt))
                throw new InvalidOperationException("The preset receipt to remove is not in the captured companion.");
            if (!SupportedKind(receipt.Kind))
                throw new InvalidOperationException("This preset kind needs a compatible content editor; preserve its receipt.");
        }
        foreach (var receipt in upserts)
            if (original.TryGetValue(receipt.Id, out var saved) &&
                (saved.Kind != receipt.Kind || saved.Symbol != receipt.Symbol || saved.Scope != receipt.Scope))
                throw new InvalidOperationException("Saved preset kind, symbol and scope are immutable; use an explicit checked identity operation.");
        if (snapshot.Presets.Count - removals.Count + upserts.Count(item => !original.ContainsKey(item.Id)) > MaxPresets)
            throw new InvalidOperationException("The content extension exceeds its preset receipt bound.");

        var content = snapshot.IsPresent
            ? JsonNode.Parse(document.Extensions![ExtensionName].GetRawText())!.AsObject()
            : new JsonObject { ["version"] = CurrentVersion, ["presets"] = new JsonArray() };
        var encoded = content["presets"]!.AsArray();
        var positions = snapshot.Presets.Select((item, index) => (item.Id, index)).ToDictionary(item => item.Id, item => item.index);
        foreach (var receipt in upserts)
        {
            if (positions.TryGetValue(receipt.Id, out var index)) PatchReceipt(encoded[index]!.AsObject(), receipt);
            else
            {
                var added = new JsonObject();
                PatchReceipt(added, receipt);
                encoded.Add(added);
            }
        }
        foreach (var index in removals.Select(id => positions[id]).OrderDescending()) encoded.RemoveAt(index);
        var extensions = document.Extensions?.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal)
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        extensions[ExtensionName] = JsonSerializer.SerializeToElement(content);
        return document with { Extensions = extensions };
    }

    private static WccPackagePresetReceipt ReadReceipt(JsonElement value)
    {
        RequireObject(value);
        var idText = String(value, "id");
        if (!Guid.TryParseExact(idText, "D", out var id)) throw new JsonException("A preset receipt needs a canonical GUID.");
        var scopeValue = Property(value, "scope"); RequireObject(scopeValue);
        var scope = String(scopeValue, "kind") switch
        {
            "package" => new WccCampaignPresetScope(WccCampaignPresetScopeKind.Package,
                OptionalString(scopeValue, "campaignId"), OptionalString(scopeValue, "campaignDefine"), OptionalString(scopeValue, "managedChapterId")),
            "campaign" => new WccCampaignPresetScope(WccCampaignPresetScopeKind.Campaign,
                String(scopeValue, "campaignId"), String(scopeValue, "campaignDefine"), OptionalString(scopeValue, "managedChapterId")),
            _ => throw new JsonException("The preset scope needs a compatible content reader.")
        };
        var source = Property(value, "source"); RequireObject(source);
        var result = new WccPackagePresetReceipt(id, String(value, "kind"), String(value, "symbol"), scope,
            String(source, "path"), String(source, "definitionSha256"), String(source, "textDomain"));
        ValidateReceipt(result);
        return result;
    }

    private static void ValidateReceipt(WccPackagePresetReceipt receipt)
    {
        if (receipt.Id == Guid.Empty || receipt.Kind is not { Length: > 0 and <= 64 } ||
            receipt.Kind.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            !Symbol(receipt.Symbol) || receipt.Scope is null || !Enum.IsDefined(receipt.Scope.Kind) ||
            receipt.Scope.Kind == WccCampaignPresetScopeKind.Package &&
                (receipt.Scope.CampaignId is not null || receipt.Scope.CampaignDefine is not null || receipt.Scope.ManagedChapterId is not null) ||
            receipt.Scope.Kind == WccCampaignPresetScopeKind.Campaign &&
                (!Symbol(receipt.Scope.CampaignId) || !Symbol(receipt.Scope.CampaignDefine) ||
                 receipt.Scope.ManagedChapterId is { } chapter && WccCampaignCreationTokenPolicy.ValidateAddonId(chapter) is not null) ||
            !Relative(receipt.SourcePath) || receipt.DefinitionHash is not { Length: 64 } || !receipt.DefinitionHash.All(Uri.IsHexDigit) ||
            WccCampaignCreationTokenPolicy.ValidateTextDomain(receipt.TextDomain) is not null)
            throw new ArgumentException("The preset receipt has invalid identity, scope or source fields.");
    }

    private static void PatchReceipt(JsonObject encoded, WccPackagePresetReceipt receipt)
    {
        encoded["id"] = receipt.Id.ToString("D"); encoded["kind"] = receipt.Kind; encoded["symbol"] = receipt.Symbol;
        var scope = encoded["scope"] as JsonObject ?? new JsonObject();
        if (scope.Parent is null) encoded["scope"] = scope;
        scope["kind"] = receipt.Scope.Kind == WccCampaignPresetScopeKind.Package ? "package" : "campaign";
        SetOptional(scope, "campaignId", receipt.Scope.CampaignId);
        SetOptional(scope, "campaignDefine", receipt.Scope.CampaignDefine);
        SetOptional(scope, "managedChapterId", receipt.Scope.ManagedChapterId);
        var source = encoded["source"] as JsonObject ?? new JsonObject();
        if (source.Parent is null) encoded["source"] = source;
        source["path"] = receipt.SourcePath; source["definitionSha256"] = receipt.DefinitionHash; source["textDomain"] = receipt.TextDomain;
    }
    private static void SetOptional(JsonObject value, string name, string? text)
    {
        if (text is null) value.Remove(name); else value[name] = text;
    }
    private static bool SupportedKind(string kind) => WccCampaignLocalPresetCatalog.TryRole(kind, out _);
    private static bool Symbol(string? value) => value is { Length: > 0 and <= 192 } &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    private static bool Relative(string? path) => path is { Length: > 0 and <= 1024 } && !path.StartsWith('/') && !path.Contains('\\') &&
        path.Split('/').All(part => part.Length != 0 && part is not ("." or "..") &&
            part.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.'));
    private static JsonElement Property(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        ? property : throw new JsonException("A required preset receipt field is absent.");
    private static string String(JsonElement value, string name)
    {
        var property = Property(value, name);
        return property.ValueKind == JsonValueKind.String ? property.GetString()! : throw new JsonException("A preset receipt field needs a string.");
    }
    private static string? OptionalString(JsonElement value, string name) => !value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null
        ? null : property.ValueKind == JsonValueKind.String ? property.GetString() : throw new JsonException("An optional receipt field needs a string.");
    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("The content extension needs JSON objects.");
    }
    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate content property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
