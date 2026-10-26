using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>One reached project-contained source and its unselected external location, for display only.</summary>
public sealed record WccCampaignContainedSourceObservation(string Name, string ProjectRootPath,
    WccPackageRevisionId ProjectRevisionId, string OwningAddonId, string RootPath, string LogicalMount,
    string ExternalAddonId, string ExternalPath, string Scope, IReadOnlyList<WmlStaticSourceRange> Origins)
{
    public bool AuthorizesPersistence => false;
}

internal sealed record WccCampaignCapturedBinaryRoot(string Path, IReadOnlyList<WmlStaticSourceRange> Origins,
    WmlStaticEvaluationResult Evaluation, string Scope);

internal static class WccCampaignContainedSourceObserver
{
    internal static IReadOnlyList<WccCampaignContainedSourceObservation> Observe(WccStaticSourceCaptureResult capture,
        IReadOnlyList<(WmlStaticEvaluationResult Evaluation, string Scope)> evaluations,
        IReadOnlyList<WccCampaignCapturedBinaryRoot> binaryRoots)
    {
        if (!capture.IsCaptureComplete || capture.EntrySource is not { } main) return [];
        var projectRoot = Path.GetDirectoryName(main.Path)!;
        var result = new List<WccCampaignContainedSourceObservation>();
        var analyses = new Dictionary<WmlStaticSource, WmlEditorAnalysis>();
        foreach (var (evaluation, scope) in evaluations.Where(value => value.Evaluation.IsComplete))
        foreach (var probe in evaluation.Includes.Where(trace => trace.Resolution.Exists == WmlStaticTruth.False && trace.InvocationOrigins.Count == 1))
        {
            var origin = probe.InvocationOrigins[0];
            if (!origin.Source.IsAuthored || !Under(projectRoot, origin.Source.Path)) continue;
            if (!analyses.TryGetValue(origin.Source, out var analysis))
                analyses[origin.Source] = analysis = WmlEditorAnalysis.Build(origin.Source.Text);
            var fold = analysis.Folds.SingleOrDefault(fold => fold.Label.StartsWith("#ifhave ", StringComparison.Ordinal) &&
                fold.StartOffset >= origin.StartOffset && fold.StartOffset < origin.StartOffset + origin.Length);
            if (fold is null) continue;
            var references = WccAddonSourceReferenceDiscovery.Discover([origin.Source]).References;
            var external = references.FirstOrDefault(reference => reference.Role == WccAddonSourceReferenceRole.ExistenceCondition &&
                reference.Origin.StartOffset >= origin.StartOffset && reference.Origin.StartOffset < origin.StartOffset + origin.Length);
            if (external is null) continue;
            // Pairing comes from the shared lexer's validated fold. Only its own
            // direct #else is considered; nested conditions/definitions retain
            // their separate folds and cannot borrow this false probe receipt.
            var branches = analysis.Tokens.Where(token => token.Kind == WmlEditorTokenKind.Directive &&
                token.StartOffset > fold.StartOffset && token.StartOffset < fold.EndOffsetExclusive &&
                WmlInlineCommentSplitter.Split(origin.Source.Text.Substring(token.StartOffset + 1, token.Length - 1)).Value.Trim() == "else" &&
                !analysis.Folds.Any(nested => nested.StartOffset > fold.StartOffset && nested.EndOffsetExclusive < fold.EndOffsetExclusive &&
                    token.StartOffset > nested.StartOffset && token.StartOffset < nested.EndOffsetExclusive)).ToArray();
            if (branches.Length != 1 || !analysis.References.Any(reference => reference.Kind == WmlEditorReferenceKind.Include &&
                reference.Name == probe.IncludePath && reference.StartOffset > fold.StartOffset && reference.StartOffset < branches[0].StartOffset)) continue;
            foreach (var include in evaluation.Includes.Where(trace => trace.Resolution.Exists == WmlStaticTruth.True &&
                trace.Resolution.Sources.Count == 1 && trace.InvocationOrigins.Count == 1 &&
                Same(trace.InvocationOrigins[0].Source, origin.Source) && trace.InvocationOrigins[0].StartOffset > branches[0].StartOffset &&
                trace.InvocationOrigins[0].StartOffset < fold.EndOffsetExclusive &&
                evaluation.Invocations.Any(invocation => invocation.Outcome == WmlStaticInvocationOutcome.Include &&
                    invocation.Name == trace.IncludePath && invocation.InvocationOrigins.SequenceEqual(trace.InvocationOrigins))))
            {
                var local = include.Resolution.Sources[0];
                if (!local.IsAuthored || !Under(projectRoot, local.Path)) continue;
                foreach (var binary in binaryRoots.Where(root => ReferenceEquals(root.Evaluation, evaluation)))
                {
                    if (!WmlResourceLookupPaths.TryMapDataPath(binary.Path, out var mount)) continue;
                    var member = WccStaticSourceCapture.FindCapturedResourceByVirtualPath(capture, mount + "/" + Path.GetFileName(local.Path));
                    if (member is null || !SamePath(member.RootPath, projectRoot) || !SamePath(member.PhysicalPath, local.Path)) continue;
                    var relative = Path.GetDirectoryName(member.RelativePath)?.Replace('\\', '/');
                    if (string.IsNullOrEmpty(relative) || !mount.EndsWith("/" + relative, StringComparison.Ordinal)) continue;
                    var ownMount = mount[..^(relative.Length + 1)];
                    if (!ownMount.StartsWith("~add-ons/", StringComparison.Ordinal) || ownMount[9..].Contains('/')) continue;
                    var rootPath = Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!binary.Origins.Any(value => Under(rootPath, value.Source.Path))) continue;
                    var evidence = Array.AsReadOnly(new[] { origin, include.InvocationOrigins[0] }.Concat(binary.Origins).Distinct().ToArray());
                    result.Add(new(relative, projectRoot, member.RevisionId, ownMount[9..], rootPath, mount,
                        external.AddonId, probe.IncludePath, scope, evidence));
                }
            }
        }
        return Array.AsReadOnly(result.DistinctBy(value => (value.ProjectRevisionId, value.RootPath, value.ExternalPath, value.Scope)).ToArray());
    }

    private static bool Same(WmlStaticSource left, WmlStaticSource right) => SamePath(left.Path, right.Path) && left.ContentHash == right.ContentHash;
    private static bool SamePath(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Under(string root, string path) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
