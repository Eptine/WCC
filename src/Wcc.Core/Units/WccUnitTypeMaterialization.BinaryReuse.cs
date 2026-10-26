using Wcc.Core.Library;
using Wcc.Core.Projects;

namespace Wcc.Core.Units;

/// <summary>Exact shared helper bytes already owned by the campaign; never overwrite authority.</summary>
public sealed class WccContentBinaryReuseReceipt
{
    internal WccContentBinaryReuseReceipt(string helperName, string relativePath,
        WccBinaryDiskRevision sourceRevision, WccBinaryDiskRevision destinationRevision)
    { HelperName = helperName; RelativePath = relativePath; SourceRevision = sourceRevision; DestinationRevision = destinationRevision; }
    public string HelperName { get; }
    public string RelativePath { get; }
    public string DestinationPath => DestinationRevision.SourcePath;
    public WccBinaryDiskRevision SourceRevision { get; }
    public WccBinaryDiskRevision DestinationRevision { get; }
    public bool AuthorizesPersistence => false;
    internal bool IsSameReceiptAs(WccContentBinaryReuseReceipt other) => HelperName == other.HelperName
        && RelativePath == other.RelativePath && SourceRevision.IsSameRevisionAs(other.SourceRevision)
        && DestinationRevision.IsSameRevisionAs(other.DestinationRevision);
}

public sealed class WccContentBinaryDestinationPlan
{
    internal WccContentBinaryDestinationPlan(IEnumerable<WccContentBinaryCreation> creates,
        IEnumerable<WccContentBinaryReuseReceipt> reuses)
    { Creates = Array.AsReadOnly(creates.ToArray()); Reuses = Array.AsReadOnly(reuses.ToArray()); }
    public IReadOnlyList<WccContentBinaryCreation> Creates { get; }
    public IReadOnlyList<WccContentBinaryReuseReceipt> Reuses { get; }
    public bool AuthorizesPersistence => false;
}

public static partial class WccUnitTypeMaterializationPlanner
{
    /// <summary>Plans exclusive creations or exact captured shared-helper reuse without writing files.</summary>
    public static WccContentBinaryDestinationPlan PlanBinaryDestinations(WccUnitTypeCreationSpec spec, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Materialization is null)
        { WccUnitTypeCreationPlanner.ValidateSpec(spec); return new([], []); }
        ValidateSpec(spec);
        return PlanBinaryDestinationsCore(projectRoot, spec.Materialization.ResourceTransfers.Select(transfer =>
        {
            var helper = spec.Materialization.Helpers.SingleOrDefault(item => SamePiece(item.Definition, transfer.Piece));
            return (Helper: helper?.Name, Owner: helper is null ? "unit '" + spec.Id + "'" : "helper {" + helper.Name + "}",
                RelativePath: GetResourceRelativePath(spec, transfer), Source: transfer.SourceRevision);
        }));
    }

    internal static WccContentBinaryDestinationPlan PlanBinaryDestinationsCore(string projectRoot,
        IEnumerable<(string? Helper, string Owner, string RelativePath, WccBinaryDiskRevision Source)> resources)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var creates = new List<WccContentBinaryCreation>();
        var reuses = new List<WccContentBinaryReuseReceipt>();
        foreach (var group in resources.GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var transfer = group.First();
            if (group.Select(item => (item.Helper, item.Owner)).Distinct().Count() != 1
                || group.Any(item => item.RelativePath != group.Key || item.Source.RawByteLength != transfer.Source.RawByteLength
                    || item.Source.RawByteSha256 != transfer.Source.RawByteSha256
                    || !item.Source.CopyBytes().AsSpan().SequenceEqual(transfer.Source.CopyBytes())))
                throw new InvalidOperationException("Helper resource destinations collide at '" + group.Key + "' with different ownership, spelling or bytes.");
            var destination = Path.Combine(root, group.Key.Replace('/', Path.DirectorySeparatorChar));
            // Unit art retains exclusive creation, even when an existing file has identical bytes.
            if (transfer.Helper is null) { creates.Add(new(destination, transfer.Source)); continue; }
            var absence = WccTextDiskRevisionCapture.CaptureCreationAbsence(root, destination);
            if (absence.IsCaptured) { creates.Add(new(destination, transfer.Source)); continue; }
            if (absence.Status != WccTextDiskFileAbsenceCaptureStatus.DestinationExists)
                throw BinaryReuseConflict(transfer.Helper, destination, absence.ErrorMessage ?? "The destination route could not be captured.");
            RequireExactBinaryRoute(root, destination, transfer.Helper);
            var captured = WccTextDiskRevisionCapture.CaptureBinary(root, destination);
            RequireExactBinaryRoute(root, destination, transfer.Helper);
            if (!captured.IsCaptured || captured.Revision is null)
                throw BinaryReuseConflict(transfer.Helper, destination, captured.ErrorMessage ?? "The existing file could not be captured.");
            if (transfer.Source.RawByteLength != captured.Revision.RawByteLength
                || transfer.Source.RawByteSha256 != captured.Revision.RawByteSha256
                || !transfer.Source.CopyBytes().AsSpan().SequenceEqual(captured.Revision.CopyBytes()))
                throw BinaryReuseConflict(transfer.Helper, destination, "The campaign file has different bytes. Keep the campaign resource or choose a distinct Library helper name.");
            reuses.Add(new(transfer.Helper, group.Key, transfer.Source, captured.Revision));
        }
        return new(creates, reuses);
    }

    internal static string? VerifyBinaryReuses(IEnumerable<WccContentBinaryReuseReceipt> receipts)
    {
        foreach (var receipt in receipts)
        {
            try
            {
                RequireExactBinaryRoute(receipt.DestinationRevision.ProjectRootPath, receipt.DestinationPath, receipt.HelperName);
                var source = WccTextDiskRevisionCapture.CaptureBinary(receipt.SourceRevision.ProjectRootPath, receipt.SourceRevision.SourcePath);
                var destination = WccTextDiskRevisionCapture.CaptureBinary(receipt.DestinationRevision.ProjectRootPath, receipt.DestinationPath);
                RequireExactBinaryRoute(receipt.DestinationRevision.ProjectRootPath, receipt.DestinationPath, receipt.HelperName);
                if (!source.IsCaptured || source.Revision is null || !receipt.SourceRevision.IsSameRevisionAs(source.Revision))
                    return "Shared helper {" + receipt.HelperName + "} resource source changed or cannot be verified: " + receipt.SourceRevision.SourcePath;
                if (!destination.IsCaptured || destination.Revision is null || !receipt.DestinationRevision.IsSameRevisionAs(destination.Revision))
                    return "Shared helper {" + receipt.HelperName + "} resource destination bytes, file identity or route changed: " + receipt.DestinationPath;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
            { return "Shared helper {" + receipt.HelperName + "} resource reuse cannot be verified: " + receipt.DestinationPath + ": " + error.Message; }
        }
        return null;
    }

    internal static void ValidateBinaryReuseMembership(IEnumerable<WccContentBinaryReuseReceipt> receipts,
        WccStaticSourceCaptureResult capture, WccPackageRevisionId revision)
    {
        foreach (var receipt in receipts)
        {
            var member = WccStaticSourceCapture.FindCapturedResourceMember(capture, revision, receipt.RelativePath);
            if (member is null || member.IsCore
                || !string.Equals(member.PhysicalPath, receipt.DestinationPath, StringComparison.Ordinal)
                || !string.Equals(Path.TrimEndingDirectorySeparator(member.RootPath), receipt.DestinationRevision.ProjectRootPath, StringComparison.Ordinal))
                throw BinaryReuseConflict(receipt.HelperName, receipt.DestinationPath, "The file is not an exact current campaign resource member.");
        }
    }

    private static void ValidateBinaryDestinationGroups(WccUnitTypeCreationSpec spec)
    {
        var plan = spec.Materialization!;
        foreach (var group in plan.ResourceTransfers.GroupBy(item => GetResourceRelativePath(spec, item), StringComparer.OrdinalIgnoreCase))
        {
            string Owner(WccContentResourceTransferReceipt item) => plan.Helpers.FirstOrDefault(helper => SamePiece(helper.Definition, item.Piece)) is { } helper
                ? "helper {" + helper.Name + "}" : "unit '" + spec.Id + "'";
            var owners = group.Select(Owner).Distinct(StringComparer.Ordinal).ToArray();
            var first = group.First().SourceRevision;
            var bytes = first.CopyBytes();
            if (owners.Length != 1 || group.Any(item => GetResourceRelativePath(spec, item) != group.Key
                || item.SourceRevision.RawByteLength != first.RawByteLength || item.SourceRevision.RawByteSha256 != first.RawByteSha256
                || !item.SourceRevision.CopyBytes().AsSpan().SequenceEqual(bytes)))
                throw new ArgumentException("Resources for " + string.Join(", ", owners)
                    + " collide at '" + group.Key + "' with different ownership, spelling or bytes. Choose distinct helper names or a distinct unit ID.", nameof(spec));
        }
    }

    private static void RequireExactBinaryRoute(string root, string destination, string helperName)
    {
        var relative = Path.GetRelativePath(root, destination);
        if (relative == "." || Path.IsPathFullyQualified(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw BinaryReuseConflict(helperName, destination, "The resource is outside the campaign root.");
        var current = root;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            var matches = Directory.EnumerateFileSystemEntries(current).Where(path =>
                string.Equals(Path.GetFileName(path), component, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || Path.GetFileName(matches[0]) != component)
                throw BinaryReuseConflict(helperName, destination, "The existing file or directory has ambiguous or different casing.");
            current = matches[0];
        }
    }

    private static InvalidOperationException BinaryReuseConflict(string helperName, string destination, string reason) =>
        new("Shared helper {" + helperName + "} resource '" + destination + "' cannot be reused: " + reason);
}
