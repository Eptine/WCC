using Wcc.Core.Projects;
using Wcc.Core.Wml;

namespace Wcc.Core.Library;

public static partial class WccStaticSourceCapture
{
    internal static string CapturedSourceRoot(WccStaticSourceCaptureResult capture, WmlStaticSource source)
    {
        if (!capture.IsCaptureComplete || capture.IncludeResolver is not CapturedResolver resolver)
            throw new InvalidOperationException("The physical source capture is incomplete.");
        var roots = resolver.CapturedRoots.Where(root => root.IsComplete && !root.IsAmbiguous && Contains(root.Path, source.Path)).ToArray();
        if (roots.Length != 1) throw new InvalidOperationException("The physical source belongs to an ambiguous package root.");
        return roots[0].Path;
    }
    internal static string CapturedInventoryIdentity(WccStaticSourceCaptureResult capture, WccPackageRevisionId revision)
    {
        if (!capture.IsCaptureComplete || capture.IncludeResolver is not CapturedResolver resolver)
            throw new InvalidOperationException("The package inventory is incomplete.");
        var roots = resolver.CapturedRoots.Where(root => root.Revision == revision && root.IsComplete && !root.IsAmbiguous).ToArray();
        if (roots.Length != 1) throw new InvalidOperationException("The package inventory is ambiguous.");
        return WccContentHash.ComputeText(string.Join("\n", roots[0].Inventory));
    }

    internal static string CapturedMacroClosureIdentity(WccStaticSourceCaptureResult capture)
    {
        if (!capture.IsCaptureComplete || capture.IncludeResolver is not CapturedResolver resolver)
            throw new InvalidOperationException("The macro's complete captured package closure is unavailable.");
        return WccContentHash.ComputeText(string.Join("\n", resolver.CapturedRoots.OrderBy(root => root.Path, StringComparer.Ordinal)
            .Select(root => root.Revision + "|" + root.Path + "|" + root.Mount + "|" + root.IsCore + "\n" + string.Join("\n", root.Inventory))));
    }

    internal static string? VerifyCapturedMacroClosure(WccStaticSourceCaptureResult capture)
    {
        if (!capture.IsCaptureComplete || capture.IncludeResolver is not CapturedResolver resolver)
            return "The macro's captured package closure is incomplete.";
        foreach (var root in resolver.CapturedRoots)
            if (VerifyCapturedRootInventory(capture, root.Revision) is { } error) return error;
        return null;
    }
    // Only the typed managed-macro coordinator can opt into this scope. Public
    // overlays and public candidate capture retain their ordinary working-root rule.
    internal static WccStaticSourceCaptureResult CreateManagedMacroCandidateCapture(
        WccLibrarySnapshot snapshot, WccManagedMacroCopyPlan plan, WccStaticSourceCaptureResult basis,
        IReadOnlyList<WmlStaticSource> additions, IReadOnlyList<WccCapturedBinaryCandidate> binaries,
        IReadOnlyList<WccCapturedDirectoryCandidate> directories)
    {
        WccManagedMacroCopyPlanner.ValidateDestination(snapshot, plan);
        if (basis.IncludeResolver is not CapturedResolver resolver || resolver.CapturedRoots.Count(root =>
                root.Revision == plan.DestinationRevisionId && root.IsManagedLibrary && root.IsComplete
                && !root.IsAmbiguous && DiskComparer.Equals(root.Path, plan.DestinationRoot)) != 1)
            throw new InvalidOperationException("The macro candidate has no exact managed Library capture.");
        if (additions.Any(source => !plan.Sources.Any(expected => expected.Path == source.Path
                && expected.ContentHash == source.ContentHash && expected.Text == source.Text))
            || binaries.Any(binary => !plan.Binaries.Any(expected => expected.DestinationPath == binary.DestinationPath
                && expected.SourceRevision.IsSameRevisionAs(binary.SourceRevision))))
            throw new InvalidOperationException("The candidate files differ from the frozen macro copy recipe.");
        return CreateCandidateCaptureCore(basis, plan.DestinationRevisionId, [], additions, binaries, directories, true, null);
    }

    internal static Task<WccStaticSourceCaptureResult> CaptureForStagedManagedMacroCopyAsync(
        WccLibrarySnapshot snapshot, WccManagedMacroCopyPlan plan, string entry,
        IReadOnlyList<WccTextDiskRevision> ownedStages, IReadOnlyList<WccBinaryDiskRevision> ownedBinaryStages,
        CancellationToken cancellationToken)
    {
        WccManagedMacroCopyPlanner.ValidateDestination(snapshot, plan);
        var stages = ownedStages.ToArray(); var binaries = ownedBinaryStages.ToArray();
        if (stages.Any(stage => !DiskComparer.Equals(stage.ProjectRootPath, plan.DestinationRoot))
            || binaries.Any(stage => !DiskComparer.Equals(stage.ProjectRootPath, plan.DestinationRoot)))
            throw new InvalidOperationException("A macro writer stage belongs to another root.");
        var entryPath = Path.GetFullPath(entry);
        return Task.Run(() => Capture(snapshot, plan.DestinationRevisionId, entryPath, [], null,
            cancellationToken, stages, true, binaries, allowManagedMacroStages: true), cancellationToken);
    }
}
