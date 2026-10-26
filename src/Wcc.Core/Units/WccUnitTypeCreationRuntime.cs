using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

/// <summary>Checks actual captured Core registration, not mere catalog visibility. Never write authority.</summary>
public static class WccUnitTypeCreationRuntime
{
    public static void Validate(WccUnitTypeCreationSpec spec, WccStaticSourceCaptureResult capture,
        WmlEvaluationContext context, WmlCampaignEvaluationPlan? plan = null, bool expectCreatedType = false,
        WmlStaticSource? entry = null, CancellationToken cancellationToken = default, WmlStaticSource? creationSource = null)
    {
        WccUnitTypeCreationPlanner.ValidateSpec(spec);
        if (!capture.IsCaptureComplete || !capture.HasCompleteCoreUnitPrelude ||
            !WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
            throw new InvalidOperationException("Unit type creation needs complete captured data from the exact pinned Core.");
        var undefined = (plan?.Difficulties.Select(row => row.Define) ?? [])
            .Concat(["EDITOR", "MULTIPLAYER"]).Where(symbol => !context.DefinedSymbols.Contains(symbol))
            .Distinct(StringComparer.Ordinal).ToArray();
        var evaluated = new WmlStaticEvaluator().Evaluate(new(entry ?? capture.EntrySource!, context,
            new[] { environment!.DefinitionsSource }.Concat(capture.PreludeSources).Concat(capture.CoreUnitPreludeSources).ToArray(),
            undefined, true, capture.IncludeResolver,
            Limits: WmlStaticEvaluationLimits.Campaign), cancellationToken);
        var scalar = WmlStaticScalarProjection.Create(evaluated, cancellationToken);
        var coreEntries = capture.IncludeResolver.Resolve("core/units.cfg", capture.EntrySource!).Sources;
        if (coreEntries.Count != 1) throw new InvalidOperationException("The pinned Core unit-data root is ambiguous.");
        var coreRoot = Path.GetDirectoryName(coreEntries[0].Path)!;
        var registry = WmlUnitReferenceProjection.Create(evaluated, scalar, cancellationToken);
        if (!registry.HasCompleteDeclaredInventory)
            throw new InvalidOperationException("The actual Core and campaign unit registry is incomplete in " + context.SemanticKey + ". No type was created.");
        var count = registry.Declarations.Count(declaration => declaration.Id == spec.Id);
        if (count != (expectCreatedType ? 1 : 0))
            throw new InvalidOperationException("Unit type ID '" + spec.Id + "' already exists or is ambiguous. Choose a new ID.");
        if (spec.Base is { } basis)
        {
            var matches = registry.Declarations.Where(declaration => declaration.Id == basis.Id).ToArray();
            if (matches.Length != 1 || matches[0].Owner.IsAmended ||
                matches[0].Owner.OpeningOrigin is not { } opening || matches[0].Owner.ClosingOrigin is not { } closing ||
                !SameSource(opening.Source, basis.Source) || opening.StartOffset < basis.StartOffset ||
                opening.StartOffset >= basis.StartOffset + basis.Length || !SameSource(closing.Source, basis.Source) ||
                closing.StartOffset < basis.StartOffset || closing.StartOffset + closing.Length > basis.StartOffset + basis.Length)
                throw new InvalidOperationException("The chosen Core base type is not its unique active captured definition in this context.");
        }
        else if (spec.Copy is { } copy)
        {
            var dependencies = spec.Materialization is { } materialization
                ? WccUnitTypeDependencyPlanner.Plan(copy, capture, context, materialization,
                    creationSource ?? new WmlStaticSource(Path.Combine(copy.RootPath, "__wcc_materialized_dependency_preview.cfg"),
                        WccUnitTypeMaterializationPlanner.Render(spec, copy.TextDomain)), cancellationToken)
                : WccUnitTypeDependencyPlanner.Plan(copy, capture, context, cancellationToken: cancellationToken);
            if (!dependencies.IsComplete)
                throw new InvalidOperationException("The selected authored unit needs additional resources in " + context.SemanticKey + ": " +
                    string.Join("; ", dependencies.Issues.Select(issue => issue.Diagnostic)));
        }
        else
        {
            RequireRegistered("race", "id", spec.Race);
            RequireRegistered("movetype", "name", spec.MovementType);
        }

        void RequireRegistered(string tag, string key, string requested)
        {
            var matches = scalar.Owners.Where(owner => owner.TagName == tag && owner.Parent is { TagName: "units", Parent: null }
                && owner.Fields.TryGetValue(key, out var field) && field.IsKnown
                && WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var decoded, out _) && decoded == requested).ToArray();
            // Registration needs a unique known identity. Core name-generator grammars
            // can contain braces and remain opaque scalar payload without obscuring that ID.
            if (matches.Length != 1 || matches[0].IsAmended ||
                matches[0].OpeningOrigin is not { } origin || !origin.Source.IsAuthored ||
                Path.GetRelativePath(coreRoot, origin.Source.Path) is var relative &&
                (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new InvalidOperationException("Choose an available Core " + (tag == "race" ? "race" : "movement type") + "; '" + requested + "' is missing or ambiguous.");
        }
    }

    private static bool SameSource(WmlStaticSource actual, WmlStaticSource expected) => actual.IsAuthored && expected.IsAuthored
        && string.Equals(actual.Path, expected.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && actual.ContentHash == expected.ContentHash && actual.Text == expected.Text;
}
