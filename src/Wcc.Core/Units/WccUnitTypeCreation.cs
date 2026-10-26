using System.Globalization;
using System.Text;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

public enum WccUnitTypeCreationMode { Scratch, BasedOnCore, CopyAuthored, MaterializeLibrary }

/// <summary>Detached creation intent. BasedOnCore writes identity only and inherits the other fields.</summary>
public sealed record WccUnitTypeCreationSpec(WccUnitTypeCreationMode Mode, string Id, string Name,
    string? FileNameStem = null)
{
    public string Race { get; init; } = "human";
    public string MovementType { get; init; } = "smallfoot";
    public int Hitpoints { get; init; } = 32;
    public int Movement { get; init; } = 5;
    public int Level { get; init; } = 1;
    public int Cost { get; init; } = 14;
    public int Experience { get; init; } = 40;
    public string Alignment { get; init; } = "neutral";
    public WccUnitTypeCreationBase? Base { get; init; }
    public WccUnitTypeAuthoredCopySource? Copy { get; init; }
    public WccUnitTypeMaterializationPlan? Materialization { get; init; }
}

/// <summary>Exact indexed Core source identity; registration in the actual gameplay context still needs proof.</summary>
public sealed class WccUnitTypeCreationBase
{
    internal WccUnitTypeCreationBase(string id, WmlStaticSource source, WccEntityId entityId,
        WccSourceFileId sourceFileId, WccPackageRevisionId coreRevisionId, WccPackageId corePackageId,
        int startOffset, int length, string contentFingerprint)
    {
        Id = id; Source = source; EntityId = entityId; SourceFileId = sourceFileId;
        CoreRevisionId = coreRevisionId; CorePackageId = corePackageId;
        StartOffset = startOffset; Length = length; ContentFingerprint = contentFingerprint;
    }
    public string Id { get; }
    public WmlStaticSource Source { get; }
    public WccEntityId EntityId { get; }
    public WccSourceFileId SourceFileId { get; }
    public WccPackageRevisionId CoreRevisionId { get; }
    public WccPackageId CorePackageId { get; }
    public int StartOffset { get; }
    public int Length { get; }
    public string ContentFingerprint { get; }
    public bool AuthorizesPersistence => false;
}

/// <summary>Canonical owned main and fixed-context evidence; not filesystem write authority.</summary>
public sealed class WccUnitTypeCreationSnapshot
{
    internal WccUnitTypeCreationSnapshot(WccScenarioCreationSnapshot campaign, bool canCreate, string diagnostic)
    { Campaign = campaign; CanCreate = canCreate; Diagnostic = diagnostic; }
    public WmlStaticSource Source => Campaign.Source;
    public bool CanCreate { get; }
    public string Diagnostic { get; }
    public bool AuthorizesPersistence => false;
    internal WccScenarioCreationSnapshot Campaign { get; }
    internal WmlCampaignEvaluationPlan? Plan => Campaign.Plan;
    internal string TextDomain => Campaign.TextDomain;
}

/// <summary>One immutable new-file recipe, committed only through the existing checked project Save.</summary>
public sealed class WccUnitTypeCreationEdit
{
    internal WccUnitTypeCreationEdit(WccUnitTypeCreationSnapshot snapshot, WccUnitTypeCreationSpec spec)
    { Snapshot = snapshot; Spec = spec with { }; }
    public WccUnitTypeCreationSnapshot Snapshot { get; }
    public WccUnitTypeCreationSpec Spec { get; }
    public WmlStaticSource Source => Snapshot.Source;
    public string UnitRelativePath => "units/" + (Spec.FileNameStem ?? Spec.Id) + ".cfg";
    public bool AuthorizesPersistence => false;
}

/// <summary>Pure bounded unit-type planning and serialization. No loading, writes, or implicit dependencies.</summary>
public static class WccUnitTypeCreationPlanner
{
    public static WccUnitTypeCreationSnapshot Read(WmlStaticSource mainSource,
        WccCampaignAuthoringMetadata? authoring, WmlCampaignEvaluationPlan? plan,
        IReadOnlyList<WmlStaticSource>? projectSources)
    {
        var campaign = WccScenarioCreationPlanner.Read(mainSource, authoring, plan, projectSources);
        if (!campaign.CanCreate) return new(campaign, false, campaign.Diagnostic);
        if (campaign.Structure.PathRows.Count(row => row.Kind == WccCampaignPathKind.UnitsInclude && row.Path == "units") != 1)
            return new(campaign, false, "Unit type creation needs one exact units directory include inside the canonical units container.");
        return new(campaign, true, "Creates a new unit type through the campaign's checked Save.");
    }

    /// <summary>Captures indexed physical Core identity only; the caller proves active registration separately.</summary>
    public static WccUnitTypeCreationBase CaptureCoreBase(WccLibrarySnapshot snapshot,
        WccPackageRevisionId coreRevisionId, WccEntityId entityId, WmlStaticSource capturedSource)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(capturedSource);
        if (!snapshot.EntitiesById.TryGetValue(entityId, out var entity) || entity.EntityType != WccEntityTypes.UnitType
            || entity.ParentEntityId is not null || !snapshot.SourceFilesById.TryGetValue(entity.SourceFileId, out var file)
            || file.PackageRevisionId != coreRevisionId || !snapshot.RevisionsById.TryGetValue(coreRevisionId, out var revision)
            || !snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) || !package.IsAvailable
            || !IsCorePackage(package) || package.StorageMode != WccPackageStorageMode.ExternalReadOnly
            || !capturedSource.IsAuthored || string.IsNullOrWhiteSpace(file.StoragePath)
            || !SamePath(file.StoragePath, capturedSource.Path)
            || !string.Equals(file.ContentHash, capturedSource.ContentHash, StringComparison.OrdinalIgnoreCase)
            || entity.SourceSpan?.StartOffset is not int start || entity.SourceSpan.EndOffsetExclusive is not int end
            || start < 0 || end <= start || end > capturedSource.Text.Length)
            throw new ArgumentException("Choose one exact indexed physical unit type from the selected Core revision.", nameof(entityId));

        var block = WmlBlockScanner.FindTopLevelBlockSpans(capturedSource.Text).SingleOrDefault(span =>
            span.TagName == "unit_type" && span.StartIndex == start && span.EndIndexExclusive == end);
        if (block is null || !WmlSingleBlockValidator.Validate(block.RawText, "unit_type").IsValid
            || string.IsNullOrWhiteSpace(entity.ContentFingerprint)
            || !string.Equals(entity.ContentFingerprint, WccContentHash.ComputeText(block.RawText), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The indexed Core unit block no longer matches its exact captured source span and fingerprint.", nameof(entityId));
        var id = ReadLiteralId(block.RawText);
        if (id is null || entity.ExportSymbol != id
            || snapshot.FindDefinitions(coreRevisionId, WccEntityTypes.UnitType, id).Count != 1)
            throw new ArgumentException("The selected Core unit needs one unambiguous literal root ID matching its indexed identity.", nameof(entityId));
        return new(id, capturedSource, entityId, file.Id, coreRevisionId, revision.PackageId, start, end - start,
            WccContentHash.ComputeText(block.RawText));
    }

    public static WccUnitTypeCreationEdit CreateEdit(WccUnitTypeCreationSnapshot snapshot, WccUnitTypeCreationSpec spec)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(spec);
        var fresh = Read(snapshot.Source, snapshot.Campaign.Structure.Authoring, snapshot.Plan, snapshot.Campaign.ProjectSources);
        if (!snapshot.CanCreate || !fresh.CanCreate || fresh.TextDomain != snapshot.TextDomain)
            throw new InvalidOperationException(fresh.Diagnostic);
        ValidateSpec(spec);
        return new(snapshot, spec);
    }

    /// <summary>
    /// Rechecks the captured indexed identity against the coordinator's frozen file manifest.
    /// The coordinator intentionally does not retain entity analyses. The immutable basis
    /// retains that earlier indexed proof; actual active registration is checked separately.
    /// </summary>
    internal static void ValidateCoreBaseEvidence(WccLibrarySnapshot snapshot, WccUnitTypeCreationBase basis)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(basis);
        if (!snapshot.SourceFilesById.TryGetValue(basis.SourceFileId, out var file)
            || file.PackageRevisionId != basis.CoreRevisionId
            || !snapshot.RevisionsById.TryGetValue(basis.CoreRevisionId, out var revision)
            || revision.PackageId != basis.CorePackageId
            || !snapshot.PackagesById.TryGetValue(basis.CorePackageId, out var package)
            || !package.IsAvailable || !IsCorePackage(package) || package.StorageMode != WccPackageStorageMode.ExternalReadOnly
            || string.IsNullOrWhiteSpace(file.StoragePath) || !SamePath(file.StoragePath, basis.Source.Path)
            || !string.Equals(file.ContentHash, basis.Source.ContentHash, StringComparison.OrdinalIgnoreCase)
            || !ValidBase(basis))
            throw new InvalidOperationException("The captured Core unit identity no longer matches the exact pinned source manifest. Reopen the wizard.");
        var blocks = WmlBlockScanner.FindTopLevelBlockSpans(basis.Source.Text).Where(block =>
            block.TagName == "unit_type" && block.StartIndex == basis.StartOffset
            && block.EndIndexExclusive == basis.StartOffset + basis.Length).ToArray();
        if (blocks.Length != 1 || ReadLiteralId(blocks[0].RawText) != basis.Id
            || !string.Equals(WccContentHash.ComputeText(blocks[0].RawText), basis.ContentFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The captured Core base no longer names its sole exact literal unit root.");
    }

    public static IReadOnlyList<WmlStaticSource> PlanSources(WccUnitTypeCreationEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var valid = CreateEdit(edit.Snapshot, edit.Spec);
        var path = Path.Combine(Path.GetDirectoryName(valid.Source.Path)!, valid.UnitRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return Array.AsReadOnly(new[] { new WmlStaticSource(path, Render(valid.Spec, valid.Snapshot.TextDomain)) });
    }

    /// <summary>Literal syntax and authoring bounds only. The host also proves collisions and runtime references.</summary>
    public static void ValidateSpec(WccUnitTypeCreationSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var stem = spec.FileNameStem ?? spec.Id;
        if (!Enum.IsDefined(spec.Mode) || WccCampaignCreationTokenPolicy.ValidateAddonId(spec.Id) is not null
            || WccCampaignCreationTokenPolicy.ValidateAddonId(stem) is not null
            || new[] { "_main", "_initial", "_final" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Use a portable unit type ID and filename of 1 to 64 ASCII letters, digits or underscores, starting with a letter or underscore; reserved filenames are unavailable.", nameof(spec));
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Length > 4096 || WccCreationSourceWriter.ValidateText(spec.Name) is not null)
            throw new ArgumentException("Enter a nonempty single-line literal unit name of at most 4096 characters.", nameof(spec));
        if (spec.Mode is WccUnitTypeCreationMode.CopyAuthored or WccUnitTypeCreationMode.MaterializeLibrary)
        {
            if (spec.Copy is not { } copy || spec.Base is not null)
                throw new ArgumentException("Choose one exact captured authored unit source for this copy.", nameof(spec));
            if (spec.Mode == WccUnitTypeCreationMode.MaterializeLibrary &&
                (copy.IsCore || spec.Id != copy.Id || spec.Name != copy.Name))
                throw new ArgumentException("Using a Library definition keeps its original ID and name. Choose editable copy to create a variation.", nameof(spec));
            if (spec.Mode == WccUnitTypeCreationMode.CopyAuthored && spec.Id == copy.Id)
                throw new ArgumentException("An editable variation needs an ID different from its source definition.", nameof(spec));
            _ = WccUnitTypeAuthoredCopyPlanner.RenderCopy(copy, spec.Id, spec.Name);
            if (spec.Materialization is not null) WccUnitTypeMaterializationPlanner.ValidateSpec(spec);
            return;
        }
        if (spec.Copy is not null)
            throw new ArgumentException("A scratch or Core-based recipe cannot also copy an authored definition.", nameof(spec));
        if (spec.Materialization is not null)
            throw new ArgumentException("Materialization belongs to an authored copy recipe.", nameof(spec));
        if (spec.Mode == WccUnitTypeCreationMode.BasedOnCore)
        {
            if (spec.Base is null || !ValidBase(spec.Base) || spec.Base.Id == spec.Id)
                throw new ArgumentException("Choose one captured Core base with an ID different from the new unit type.", nameof(spec));
            return;
        }
        if (spec.Base is not null) throw new ArgumentException("A scratch unit type cannot also inherit a Core base.", nameof(spec));
        if (!LiteralReference(spec.Race) || !LiteralReference(spec.MovementType))
            throw new ArgumentException("Choose a nonempty literal race and movement type from the active Core catalog.", nameof(spec));
        if (spec.Hitpoints is < 1 or > 1_000_000 || spec.Movement is < 0 or > 1_000_000
            || spec.Level is < 0 or > 1_000_000 || spec.Cost is < 0 or > 1_000_000 || spec.Experience is < 1 or > 1_000_000)
            throw new ArgumentException("Hitpoints and experience must be 1 to 1000000; movement, level and cost must be 0 to 1000000.", nameof(spec));
        if (spec.Alignment is not ("lawful" or "neutral" or "chaotic" or "liminal"))
            throw new ArgumentException("Choose lawful, neutral, chaotic or liminal alignment.", nameof(spec));
    }

    /// <summary>Shared campaign/library payload. This does not authorize creating or activating a file.</summary>
    public static string Render(WccUnitTypeCreationSpec spec, string textDomain)
    {
        ValidateSpec(spec);
        if (WccCampaignCreationTokenPolicy.ValidateTextDomain(textDomain) is { } issue)
            throw new ArgumentException(issue, nameof(textDomain));
        if (spec.Materialization is not null) return WccUnitTypeMaterializationPlanner.Render(spec, textDomain);
        if (spec.Copy is { } copy)
            return WccUnitTypeAuthoredCopyPlanner.RenderCopy(copy, spec.Id, spec.Name, textDomain);
        var builder = new StringBuilder("#textdomain ").Append(textDomain).Append("\n[unit_type]\n");
        Field("id", spec.Id);
        Field("name", WccCreationSourceWriter.EncodeText(spec.Name, translated: true));
        if (spec.Mode == WccUnitTypeCreationMode.BasedOnCore)
            builder.Append("    [base_unit]\n        id=").Append(EncodeReference(spec.Base!.Id))
                .Append("\n    [/base_unit]\n");
        else
        {
            Field("race", EncodeReference(spec.Race));
            Field("movement_type", EncodeReference(spec.MovementType));
            Number("hitpoints", spec.Hitpoints); Number("movement", spec.Movement); Number("level", spec.Level);
            Number("cost", spec.Cost); Number("experience", spec.Experience);
            Field("alignment", EncodeReference(spec.Alignment)); Field("advances_to", "null");
        }
        return builder.Append("[/unit_type]\n").ToString();

        void Field(string key, string value) => builder.Append("    ").Append(key).Append('=').Append(value).Append('\n');
        void Number(string key, int value) => Field(key, value.ToString(CultureInfo.InvariantCulture));
    }

    private static bool ValidBase(WccUnitTypeCreationBase basis) => basis.Source.IsAuthored
        && basis.StartOffset >= 0 && basis.Length > 0 && basis.StartOffset <= basis.Source.Text.Length - basis.Length
        && string.Equals(basis.ContentFingerprint,
            WccContentHash.ComputeText(basis.Source.Text.Substring(basis.StartOffset, basis.Length)), StringComparison.OrdinalIgnoreCase)
        && ReadLiteralId(basis.Source.Text.Substring(basis.StartOffset, basis.Length)) == basis.Id;

    private static string? ReadLiteralId(string raw)
    {
        if (!WmlSingleBlockValidator.Validate(raw, "unit_type").IsValid) return null;
        var editable = WmlEditableBlockParser.Parse(new WmlBlock("unit_type", raw), UnitTypeSchema.KnownKeys);
        var field = WmlFieldSafetyAnalyzer.AnalyzeLiteralTopLevelField(editable, "id");
        return field.Kind == WmlFieldSafetyKind.PlainEditable
            && WmlUnitReferenceProjection.TryLiteral(field.PlainValue, out var id) && LiteralReference(id) ? id : null;
    }

    private static bool LiteralReference(string? value) => value is { Length: > 0 and <= 4096 }
        && value == value.Trim() && !value.Contains(',') && WmlUnitReferenceProjection.IsStaticName(value)
        && WccCreationSourceWriter.ValidateText(value) is null;

    private static string EncodeReference(string value) => value == value.Trim()
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '_' or '-')
        ? value : WccCreationSourceWriter.EncodeText(value);

    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsCorePackage(WccPackageRecord package) => package.ParentPackageId is null
        && (package.SourceKind == WccPackageSourceKinds.WesnothCore
                && (package.ContentKind == WccPackageContentKinds.Core || package.ContentKind == WccPackageContentKinds.ResourcePack)
            || package.SourceKind == WccPackageSourceKinds.WesnothMainline
                && package.ContentKind == WccPackageContentKinds.ResourcePack);
}
