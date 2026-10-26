using Wcc.Core.Library;
using Wcc.Core.Campaigns;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>Exact captured shared definition and durable role intent, never activation or editing authority.</summary>
public sealed class WccCampaignLocalPresetEntry
{
    internal WccCampaignLocalPresetEntry(Guid id, WccCampaignLocalPresetRole role, string name,
        WmlStaticSourceRange definitionRange, WmlStaticSourceRange bodyRange,
        WccCampaignLocalPresetPayload? payload, string? diagnostic)
    {
        Id = id; Role = role; Name = name; DefinitionRange = definitionRange; BodyRange = bodyRange;
        Payload = payload; Diagnostic = diagnostic;
        Definition = new(name, definitionRange.Text, definitionRange.Source.Path,
            SourceStartLine: definitionRange.Source.Text[..definitionRange.StartOffset].Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n').Count(character => character == '\n'))
        { TextDomain = WmlTextDomainCapture.BeforeOffset(definitionRange.Source.Text, definitionRange.StartOffset) };
    }
    public WccCampaignPresetScope Scope { get; internal init; } = WccCampaignPresetScope.Package;
    public bool HasContentReceipt { get; internal init; }
    public bool IsAvailableIn(WmlCampaignEvaluationPlan? plan) => Scope.Targets(plan);
    public Guid Id { get; }
    public WccCampaignLocalPresetRole Role { get; }
    public string Name { get; }
    public string SourcePath => DefinitionRange.Source.Path;
    public string SourceHash => DefinitionRange.Source.ContentHash;
    public WmlStaticSourceRange DefinitionRange { get; }
    public WmlStaticSourceRange BodyRange { get; }
    public WmlMacroExpansionDefinition Definition { get; }
    public WccCampaignLocalPresetPayload? Payload { get; }
    public bool IsSupported => Payload is not null && Diagnostic is null;
    public string? Diagnostic { get; }
    public bool AuthorizesPersistence => false;
    public bool ProvesActivation => false;
    public string DisplayName => Payload?.Values.GetValueOrDefault("name") is { Length: > 0 } name ? name
        : Payload?.Values.GetValueOrDefault("id") is { Length: > 0 } id ? id : Name;
    internal WccCampaignLocalPresetEntry Refused(string diagnostic) => new(Id, Role, Name, DefinitionRange, BodyRange, Payload, diagnostic) { Scope = Scope, HasContentReceipt = HasContentReceipt };
}

public sealed class WccCampaignLocalPresetCatalogSnapshot
{
    internal WccCampaignLocalPresetCatalogSnapshot(WmlStaticSource? main, IEnumerable<WccCampaignLocalPresetEntry> entries, IEnumerable<string> diagnostics)
    { MainSource = main; Entries = Array.AsReadOnly(entries.ToArray()); Diagnostics = Array.AsReadOnly(diagnostics.Distinct(StringComparer.Ordinal).ToArray()); }
    public WmlStaticSource? MainSource { get; }
    public IReadOnlyList<WccCampaignLocalPresetEntry> Entries { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public bool AuthorizesPersistence => false;
}

/// <summary>One campaign-local catalog over existing captured source revisions; no directory scan, global registry or resolver.</summary>
public static partial class WccCampaignLocalPresetCatalog
{
    public static WccCampaignLocalPresetCatalogSnapshot Build(WccStaticSourceCaptureResult capture, WmlCampaignEvaluationPlan? plan)
    {
        return Build(capture, plan, null);
    }

    /// <summary>Physical managed declarations only, including unsupported entries with their exact RAW ranges.</summary>
    public static IReadOnlyList<WccCampaignLocalPresetEntry> ReadDefinitions(WmlStaticSource source, WmlCampaignEvaluationPlan? plan) =>
        ReadDefinitions(source, plan, null);
    internal static IReadOnlyList<WccCampaignLocalPresetEntry> ReadDefinitions(WmlStaticSource source, WmlCampaignEvaluationPlan? plan,
        IReadOnlyList<WccCampaignLocalPresetEntry>? unitPresets)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsAuthored || source.Text.Length > 8 * 1024 * 1024) return [];
        var structure = WmlAuthoredStructureScanner.Scan(source.Text);
        var entries = new List<WccCampaignLocalPresetEntry>();
        foreach (var opening in structure.Directives.Where(directive => directive.Name == "define" && directive.DefinitionDepth == 0))
        {
            var end = structure.Directives.FirstOrDefault(directive => directive.Name == "enddef" && directive.DefinitionDepth == 1 && directive.Line > opening.Line);
            if (end.Name is null) continue;
            var first = structure.Lines[opening.Line]; var last = structure.Lines[end.Line];
            var range = new WmlStaticSourceRange(source, first.StartOffset, last.EndOffsetExclusive - first.StartOffset);
            var entry = ReadPhysical(source, range, new(source, first.EndOffsetExclusive, last.StartOffset - first.EndOffsetExclusive), plan,
                structure.Problem, null, unitPresets);
            if (entry is not null) entries.Add(entry);
        }
        if (unitPresets is null && entries.Any(entry => entry.Role == WccCampaignLocalPresetRole.Event))
        {
            var units = entries.Where(entry => entry.Role == WccCampaignLocalPresetRole.Unit && entry.IsSupported &&
                !entries.Any(other => !ReferenceEquals(other, entry) && (other.Id == entry.Id || other.Name == entry.Name))).ToArray();
            var rebound = ReadDefinitions(source, plan, units);
            for (var index = 0; index < entries.Count; index++)
                if (entries[index].Role == WccCampaignLocalPresetRole.Event)
                    entries[index] = rebound.Single(entry => entry.DefinitionRange.StartOffset == entries[index].DefinitionRange.StartOffset);
        }
        return Array.AsReadOnly(entries.ToArray());
    }

    public static WccCampaignLocalPresetEntry? ReadDefinition(WmlStaticSource source, WmlStaticSourceRange exactDefinition, WmlCampaignEvaluationPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(exactDefinition);
        if (!SameSource(source, exactDefinition.Source) || exactDefinition.StartOffset < 0 || exactDefinition.Length < 0
            || exactDefinition.StartOffset > source.Text.Length - exactDefinition.Length) return null;
        return ReadDefinitions(source, plan).SingleOrDefault(entry => entry.DefinitionRange.StartOffset == exactDefinition.StartOffset
            && entry.DefinitionRange.Length == exactDefinition.Length);
    }

    private static WccCampaignLocalPresetEntry? ReadPhysical(WmlStaticSource source, WmlStaticSourceRange range,
        WmlStaticSourceRange body, WmlCampaignEvaluationPlan? plan, string? sourceProblem,
        WccPackagePresetReceipt? receipt, IReadOnlyList<WccCampaignLocalPresetEntry>? unitPresets)
    {
        var firstBody = body.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        var hasMarker = firstBody is not null && TryMarker(firstBody.Trim(), out _, out _);
        WccCampaignLocalPresetRole role; Guid id;
        if (hasMarker) TryMarker(firstBody!.Trim(), out role, out id);
        else if (receipt is not null && TryRole(receipt.Kind, out _))
        { TryRole(receipt.Kind, out role); id = receipt.Id; }
        else return null;
        var headerEnd = source.Text.IndexOfAny(['\r', '\n'], range.StartOffset);
        if (headerEnd < 0) return null;
        var header = source.Text[range.StartOffset..headerEnd].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var name = header.Length > 1 ? header[1] : "";
        WccCampaignLocalPresetPayload? payload = null; string? problem = sourceProblem;
        try
        {
            if (problem is not null) throw new InvalidOperationException(problem);
            if (header.Length != 2 || header[0] != "#define" || !ValidName(name))
                throw new InvalidOperationException("Managed presets need one portable zero-parameter macro header.");
            if (range.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd().Split('\n').Last().Trim() != "#enddef")
                throw new InvalidOperationException("Managed presets need a separate exact closing #enddef line.");
            if (body.Length > 2 * 1024 * 1024) throw new InvalidOperationException("The managed preset body exceeds its bounded size.");
            if (receipt is not null && (receipt.Id != id || receipt.Symbol != name || receipt.Kind != Kind(role)
                || receipt.DefinitionHash != WccContentHash.ComputeText(range.Text)
                || receipt.TextDomain != WmlTextDomainCapture.BeforeOffset(source.Text, range.StartOffset)))
                throw new InvalidOperationException("The preset content receipt does not match this exact authored definition.");
            var scope = receipt?.Scope ?? WccCampaignPresetScope.Package;
            var permittedUnits = unitPresets?.Where(unit => unit.IsSupported && unit.Role == WccCampaignLocalPresetRole.Unit &&
                (unit.Scope.IsPackage || !scope.IsPackage && unit.Scope == scope)).ToArray();
            payload = ReadPayload(body, role, plan, permittedUnits);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        { problem = exception.Message; }
        return new(id, role, name, range, body, payload, problem)
        { Scope = receipt?.Scope ?? WccCampaignPresetScope.Package, HasContentReceipt = receipt is not null };
    }

    private static bool TryMarker(string text, out WccCampaignLocalPresetRole role, out Guid id)
    {
        role = default; id = default;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[0] != "#" || parts[1] != "wcc-preset:" || parts[2] != "v1"
            || !parts[3].StartsWith("role=", StringComparison.Ordinal) || !TryRole(parts[3][5..], out _) || !parts[4].StartsWith("id=", StringComparison.Ordinal)
            || !Guid.TryParseExact(parts[4][3..], "N", out id) || id == Guid.Empty) return false;
        TryRole(parts[3][5..], out role);
        return true;
    }

    private static bool SameSource(WmlStaticSource left, WmlStaticSource right) => left.IsAuthored && right.IsAuthored
        && left.ContentHash == right.ContentHash && string.Equals(left.Path, right.Path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
