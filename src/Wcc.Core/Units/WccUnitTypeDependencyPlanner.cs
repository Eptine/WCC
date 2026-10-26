using Wcc.Core.Library;
using Wcc.Core.Wml;
using Wcc.Core.Wml.Checking;

namespace Wcc.Core.Units;

public enum WccContentDependencyKind { Macro, Include, UnitType, Race, MovementType, Ability, WeaponSpecial, Resource, Advancement }
public enum WccContentDependencyAvailability { CapturedCore, NeedsMaterialization, Unknown }
public sealed record WccContentDependencyNode(WccContentDependencyKind Kind, string Identity,
    WmlStaticSourceRange? Source, WccContentDependencyAvailability Availability);
public sealed record WccContentDependencyIssue(string Code, string Reference, string Field, string Diagnostic,
    WmlStaticSourceRange? Source = null);

/// <summary>Reusable immutable closure report. Availability is never activation or persistence authority.</summary>
public sealed class WccContentDependencyPlan
{
    internal WccContentDependencyPlan(IEnumerable<WccContentDependencyNode> dependencies,
        IEnumerable<WccContentDependencyIssue> issues, WmlStaticEvaluationResult? evaluation)
    {
        Dependencies = Array.AsReadOnly(dependencies.Distinct().ToArray());
        Issues = Array.AsReadOnly(issues.Distinct().ToArray()); Evaluation = evaluation;
    }
    public IReadOnlyList<WccContentDependencyNode> Dependencies { get; }
    public IReadOnlyList<WccContentDependencyIssue> Issues { get; }
    public WmlStaticEvaluationResult? Evaluation { get; }
    public bool IsComplete => Evaluation is { IsComplete: true } && Issues.Count == 0;
    public bool AuthorizesPersistence => false;
}

/// <summary>Unit adapter over captured ordered evaluation, scalar/reference projections and resource grammar.</summary>
public static class WccUnitTypeDependencyPlanner
{
    public static WccContentDependencyPlan Plan(WccUnitTypeAuthoredCopySource source,
        WccStaticSourceCaptureResult capture, WmlEvaluationContext context,
        WmlStaticSource? candidateSource = null, CancellationToken cancellationToken = default)
        => PlanCore(source, capture, context, candidateSource, null, cancellationToken);

    public static WccContentDependencyPlan Plan(WccUnitTypeAuthoredCopySource source,
        WccStaticSourceCaptureResult capture, WmlEvaluationContext context,
        WccUnitTypeMaterializationPlan materialization, WmlStaticSource candidateSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        if (!WccUnitTypeMaterializationPlanner.SameReceipt(source, materialization.OriginalSource))
            throw new ArgumentException("The dependency plan and materialization source differ.", nameof(materialization));
        var candidate = WccUnitTypeMaterializationPlanner.ValidateCandidate(materialization, candidateSource);
        return PlanCore(source, capture, context, candidateSource, candidate, cancellationToken);
    }

    internal static WccContentDependencyPlan ProbeMaterialization(WccUnitTypeAuthoredCopySource source,
        WccStaticSourceCaptureResult capture, WmlEvaluationContext context, WccMaterializationCandidate candidate,
        CancellationToken cancellationToken = default)
        => PlanCore(source, capture, context, candidate.Source, candidate, cancellationToken);

    private static WccContentDependencyPlan PlanCore(WccUnitTypeAuthoredCopySource source,
        WccStaticSourceCaptureResult capture, WmlEvaluationContext context, WmlStaticSource? candidateSource,
        WccMaterializationCandidate? materialization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(capture); ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var nodes = new List<WccContentDependencyNode>(); var issues = new List<WccContentDependencyIssue>();
        void Issue(string code, string reference, string field, string message, WmlStaticSourceRange? origin = null)
            => issues.Add(new(code, reference, field, message, origin));
        if (!capture.IsCaptureComplete || !capture.HasCompleteCoreUnitPrelude || capture.IncludeResolver is not IWmlStaticResourceResolver resources
            || !WmlStaticSingleplayerEnvironment.TryCreate(capture.CoreVersion, out var environment))
        { Issue("dependency.core-unavailable", "Core", "source", "Copying needs complete captured data from the exact pinned Core."); return new(nodes, issues, null); }
        if (source.IsCore && capture.Sources.Count(file => SameSource(file, source.Source)) != 1)
        { Issue("dependency.core-source-stale", source.Id, "source", "The selected authored Core file does not match the exact captured runtime source. Reopen the wizard."); return new(nodes, issues, null); }
        var coreEntry = capture.IncludeResolver.Resolve("core/units.cfg", capture.EntrySource!).Sources;
        if (coreEntry.Count != 1)
        { Issue("dependency.core-ambiguous", "Core", "source", "The captured Core unit entry is ambiguous."); return new(nodes, issues, null); }
        var coreRoot = Path.GetDirectoryName(coreEntry[0].Path)!;
        var candidate = candidateSource ?? new WmlStaticSource(Path.Combine(source.RootPath, "__wcc_authored_copy_preview.cfg"),
            WccUnitTypeAuthoredCopyPlanner.RenderOriginal(source));
        if (materialization is null) WccUnitTypeAuthoredCopyPlanner.ValidateCandidate(source, candidate);
        var helperSources = materialization?.HelperSources.Where(helper => !SameSource(helper, candidate)).ToArray() ?? [];
        var resolver = new CoreOnlyResolver(capture.IncludeResolver, resources, candidate, helperSources);
        var entry = new WmlStaticSource("wcc-static://authored-copy/units.cfg",
            (helperSources.Length == 0 ? "" : "{~wcc-copy/helpers.cfg}\n") + "[units]\n{~wcc-copy/selected.cfg}\n[/units]\n", false);
        var evaluated = new WmlStaticEvaluator().Evaluate(new(entry, context,
            new[] { environment!.DefinitionsSource }.Concat(capture.PreludeSources).Concat(capture.CoreUnitPreludeSources).ToArray(),
            new[] { "EDITOR", "MULTIPLAYER" }.Where(symbol => !context.DefinedSymbols.Contains(symbol)).ToArray(), true, resolver,
            WmlStaticEvaluationLimits.Campaign), cancellationToken);
        foreach (var diagnostic in evaluated.Diagnostics.Where(item => item.IsBlocking))
        {
            Issue("dependency.evaluation", "preprocessing", "source", diagnostic.Message, diagnostic.Origin);
            if (materialization?.HelperConflict(diagnostic.Origin) is { } helperName)
                Issue("materialization.helper-conflict", helperName, "macro", "Helper {" + helperName
                    + "} conflicts with an active definition in captured Core or the destination.", diagnostic.Origin);
        }
        var scalar = WmlStaticScalarProjection.Create(evaluated, cancellationToken);
        var registry = WmlUnitReferenceProjection.Create(evaluated, scalar, cancellationToken);
        if (!registry.HasCompleteDeclaredInventory)
            Issue("dependency.unit-inventory", "unit_type", "source", "The captured unit registration inventory is incomplete; no definition was copied.");
        var selected = registry.Declarations.Where(item => SameSource(item.Owner.OpeningOrigin?.Source, candidate)).ToArray();
        if (selected.Length != 1 || selected[0].Owner.IsAmended
            || selected[0].Owner.ClosingOrigin is not { } selectedClosing || !SameSource(selectedClosing.Source, candidate)
            || selected[0].Owner.OpeningExpansionStack.Count != 0 || selected[0].Owner.ClosingExpansionStack.Count != 0)
        { Issue("dependency.selected-owner", source.Id, "unit_type", "The detached source must register one exact unamended selected unit."); return new(nodes, issues, evaluated); }
        var reached = new HashSet<WmlStaticScalarOwner>(); var pending = new Queue<WmlStaticScalarOwner>();
        var coreRegistryContainers = new Dictionary<WmlStaticScalarOwner, bool>();
        var reachableMacroDefinitions = new List<WmlStaticSourceRange>();
        var baseEdges = new Dictionary<WmlStaticScalarOwner, HashSet<WmlStaticScalarOwner>>();
        pending.Enqueue(selected[0].Owner);
        var definitions = WmlDefinitionReferenceProjection.Create(evaluated, scalar, cancellationToken);
        var resourceProjection = WmlCheckResourceProjection.Create(evaluated, scalar, _ => true, cancellationToken);
        var resourceOwners = resourceProjection.Owners.ToDictionary(item => item.OriginalOwner);
        var composites = new WmlCheckResourceCompositeAssignments(evaluated, cancellationToken);
        var amlaReferences = new List<(string Id, WmlStaticScalarOwner Unit, WmlStaticSourceRange? Origin)>();
        var unitReferences = registry.References.ToLookup(item => item.Owner);
        var definitionReferences = definitions.References.ToLookup(item => item.Owner);
        while (pending.TryDequeue(out var owner))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reached.Add(owner)) continue;
            if (reached.Count > 8192)
            { Issue("dependency.limit", source.Id, "source", "The copy dependency closure exceeds 8192 owners."); break; }
            foreach (var child in owner.DirectChildren) pending.Enqueue(child);
            foreach (var reference in unitReferences[owner])
                foreach (var id in Names(reference.Field, reference.IsList, reference.Field.Key))
                {
                    if (reference.Kind == WmlUnitReferenceKind.Advancement && id == "null"
                        && WmlResourceValueTextDecoder.TryDecode(reference.Field.ValueText, out var whole, out _) && whole.Trim() == "null") continue;
                    var matches = registry.Declarations.Where(item => item.Id == id && Core(item.Owner)).ToArray();
                    if (matches.Length != 1)
                        Issue("dependency.unit-needs-materialization", id, reference.Field.Key,
                            "Unit type '" + id + "' is not uniquely available from captured Core; its authored definition needs materialization.", Origin(reference.Field));
                    else
                    {
                        Add(WccContentDependencyKind.UnitType, id, matches[0].Owner); pending.Enqueue(matches[0].Owner);
                        if (reference.Kind == WmlUnitReferenceKind.BaseUnit)
                        {
                            var unit = UnitAncestor(owner);
                            if (!baseEdges.TryGetValue(unit, out var edges)) baseEdges.Add(unit, edges = []);
                            edges.Add(matches[0].Owner);
                        }
                    }
                }
            foreach (var reference in definitionReferences[owner])
                foreach (var id in Names(reference.Field, false, reference.Field.Key))
                {
                    var matches = definitions.Declarations.Where(item => item.Kind == reference.Kind && item.Id == id && Core(item.Owner)).ToArray();
                    if (matches.Length != 1)
                        Issue("dependency.definition-needs-materialization", id, reference.Field.Key,
                            "The " + reference.Field.Key + " '" + id + "' is not uniquely available from captured Core; its definition needs materialization.", Origin(reference.Field));
                    else { Add(reference.Kind == WmlDefinitionReferenceKind.Race ? WccContentDependencyKind.Race : WccContentDependencyKind.MovementType, id, matches[0].Owner); pending.Enqueue(matches[0].Owner); }
                }
            RegistryList(owner, "abilities_list", "abilities", WccContentDependencyKind.Ability);
            RegistryList(owner, "specials_list", "weapon_specials", WccContentDependencyKind.WeaponSpecial);
            if (UnitFilter(owner.TagName) || owner.TagName == "plague")
                if (owner.Fields.TryGetValue("type", out var type))
                    foreach (var id in Names(type, UnitFilter(owner.TagName), "type"))
                    {
                        // A filter may name the new root itself; that definition is already in this payload.
                        if (UnitFilter(owner.TagName) && id == selected[0].Id) continue;
                        var targets = registry.Declarations.Where(item => item.Id == id && Core(item.Owner)).ToArray();
                        if (targets.Length != 1) Issue("dependency.unit-needs-materialization", id, "type",
                            "Unit type '" + id + "' referenced by [" + owner.TagName + "] is not uniquely available from captured Core.", Origin(type));
                        else { Add(WccContentDependencyKind.UnitType, id, targets[0].Owner); pending.Enqueue(targets[0].Owner); }
                    }
            if (owner.TagName == "advancement" && owner.Fields.TryGetValue("require_amla", out var amla))
                foreach (var id in Names(amla, true, "require_amla"))
                    amlaReferences.Add((id, UnitAncestor(owner), Origin(amla)));
            if (owner.TagName == "lua")
                Issue("dependency.lua", "Lua payload", "code", "Lua dependency closure requires the later materialization/runtime contract; the authored payload was preserved.", owner.OpeningOrigin);
            if (owner.TagName == "effect" && owner.Fields.TryGetValue("apply_to", out var effect)
                && WmlResourceValueTextDecoder.TryDecode(effect.ValueText, out var action, out _) && action == "type")
                Issue("dependency.runtime-unit-type", "type effect", "name", "Runtime unit-type transformations require additional dependency proof.", Origin(effect));
            if (resourceOwners.TryGetValue(owner, out var resource))
                foreach (var key in owner.Fields.Keys)
                {
                    var kind = WmlResourceFieldCatalog.Find(owner, key); if (kind is null || kind == WmlResourceFieldKind.Lua) continue;
                    var field = resource.ValueOwner.Fields.GetValueOrDefault(key) ?? owner.Fields[key];
                    if (field.ValueText is null) field = composites.Observe(owner, key, cancellationToken) ?? field;
                    if (field.Contributions.LastOrDefault() is not { ValueMappingIsExact: true }
                        || !WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var value, out var reason))
                    { Issue("dependency.resource-value", field.ValueText ?? "(unknown)", key, "Resource value needs additional literal/mapping proof.", Origin(field)); continue; }
                    if (value.Length == 0 || value is "none" or "unit_image" && key is "profile" or "small_profile" or "portrait") continue;
                    var parsed = kind == WmlResourceFieldKind.Sound ? WmlResourceReferenceSyntax.ParseSound(value, cancellationToken: cancellationToken)
                        : kind is WmlResourceFieldKind.Image or WmlResourceFieldKind.AnimationImage ? WmlResourceReferenceSyntax.ParseImage(value, kind == WmlResourceFieldKind.AnimationImage, cancellationToken: cancellationToken)
                        : WmlResourceReferenceSyntax.ParseBinaryFilename(value, cancellationToken);
                    if (!parsed.IsComplete)
                        foreach (var problem in parsed.Issues) Issue("dependency.resource-syntax", value, key, problem.Reason, Origin(field));
                    var folder = kind == WmlResourceFieldKind.Sound ? "sounds" : kind == WmlResourceFieldKind.Music ? "music" : kind == WmlResourceFieldKind.Map ? "maps" : "images";
                    foreach (var filename in parsed.Filenames)
                    {
                        if (materialization?.ContainsResource(key, filename, field) == true)
                        { nodes.Add(new(WccContentDependencyKind.Resource, filename, Origin(field), WccContentDependencyAvailability.NeedsMaterialization)); continue; }
                        var paths = WmlResourceLookupPaths.Create(filename, folder, ["core"], folder == "images", cancellationToken, resolver.InstallationResourceFolders);
                        var matches = paths.Candidates.Where(path => resolver.ResolveResource(path, candidate) is
                            { Exists: WmlStaticTruth.True, MembershipIdentity.Length: > 0 }).ToArray();
                        if (matches.Length == 0)
                            Issue("dependency.resource-needs-materialization", filename, key,
                                "Resource '" + filename + "' is not proved available from captured Core paths; its bytes and activation need materialization.", Origin(field));
                        else foreach (var path in matches) nodes.Add(new(WccContentDependencyKind.Resource, path, Origin(field), WccContentDependencyAvailability.CapturedCore));
                    }
                }
        }
        var visiting = new HashSet<WmlStaticScalarOwner>(); var done = new HashSet<WmlStaticScalarOwner>();
        foreach (var unit in baseEdges.Keys)
            if (BaseCycle(unit, 0))
            { Issue("dependency.base-cycle", Literal(unit, "id") ?? source.Id, "base_unit", "The captured Core base chain is cyclic or exceeds the bounded 256-level depth.", unit.OpeningOrigin); break; }
        foreach (var (id, unit, origin) in amlaReferences)
            if (!reached.Any(owner => owner.TagName == "advancement" && UnitAncestor(owner) == unit
                && owner.Fields.TryGetValue("id", out var field) && WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var declared, out _) && declared == id))
                Issue("dependency.amla", id, "require_amla", "AMLA prerequisite '" + id + "' is not proved in its authored unit; inherited AMLA resolution needs the later closure contract.", origin);
        foreach (var invocation in evaluated.Invocations.Where(item => item.InvocationOrigins.Any(origin => SameSource(origin.Source, candidate))
            || item.InvocationOrigins.Any(origin => reached.Any(owner => Within(origin, owner))
                || coreRegistryContainers.Any(container => container.Value && Within(origin, container.Key))
                || reachableMacroDefinitions.Any(definition => SameSource(origin.Source, definition.Source)
                    && origin.StartOffset >= definition.StartOffset && origin.StartOffset + origin.Length <= definition.StartOffset + definition.Length))))
        {
            if (invocation.Outcome == WmlStaticInvocationOutcome.Expanded && invocation.Definition is { } definition)
            {
                if (materialization?.ContainsHelper(invocation.Name, definition) == true)
                {
                    nodes.Add(new(WccContentDependencyKind.Macro, invocation.Name, definition, WccContentDependencyAvailability.NeedsMaterialization));
                    if (!reachableMacroDefinitions.Contains(definition)) reachableMacroDefinitions.Add(definition);
                }
                else if (!CoreSource(definition.Source)) Issue("dependency.macro-needs-materialization", invocation.Name, "macro", "Helper macro {" + invocation.Name + "} needs authored materialization.", definition);
                else
                {
                    nodes.Add(new(WccContentDependencyKind.Macro, invocation.Name, definition, WccContentDependencyAvailability.CapturedCore));
                    if (!reachableMacroDefinitions.Contains(definition)) reachableMacroDefinitions.Add(definition);
                }
            }
            else if (invocation.Outcome is WmlStaticInvocationOutcome.Unresolved or WmlStaticInvocationOutcome.Unsupported or WmlStaticInvocationOutcome.Computed or WmlStaticInvocationOutcome.IncludeUnresolved)
                Issue("dependency.macro-unresolved", invocation.Name, "macro", "Invocation {" + invocation.Name + "} cannot be proved using captured Core; local helpers need materialization.", invocation.InvocationOrigins.FirstOrDefault());
        }
        return new(nodes, issues, evaluated);

        bool CoreSource(WmlStaticSource origin) => origin.IsAuthored && !SameSource(origin, candidate)
            && Inside(coreRoot, origin.Path) && capture.Sources.Any(captured => SameSource(captured, origin));
        bool Core(WmlStaticScalarOwner owner) => owner.OpeningOrigin is { } origin && CoreSource(origin.Source) && !owner.IsAmended;
        bool CoreRegistry(WmlStaticScalarOwner owner)
        {
            if (owner.OpeningOrigin is not { } origin || !CoreSource(origin.Source) || owner.Parent is not { } container)
                return false;
            if (coreRegistryContainers.TryGetValue(container, out var known)) return known;
            // Core healing registry macros deliberately amend their emitted [heals]
            // tag to add a special note. Amendments forbid source insertion, but do
            // not make a captured Core registry entry unavailable. Prove the whole
            // exact container, including amendment tags and otherwise silent fields.
            var complete = !container.IsAmended && container.OpeningOutputOffset.HasValue
                && container.ClosingOutputOffset.HasValue && container.ClosingOrigin is { } closing
                && container.OpeningOrigin is { } opening && CoreSource(opening.Source) && CoreSource(closing.Source);
            if (complete)
            {
                var cursor = container.OpeningOutputOffset!.Value;
                var end = container.ClosingOutputOffset!.Value + container.ClosingOrigin!.Length;
                foreach (var segment in evaluated.Segments.Where(segment => segment.OutputOffset < end
                    && segment.OutputOffset + segment.Text.Length > cursor))
                {
                    if (segment.OutputOffset > cursor || !CoreSource(segment.Origin.Source)
                        || segment.ExpansionStack.Any(frame => !CoreSource(frame.Definition.Source)
                            || frame.InvocationOrigins.Count == 0 || frame.InvocationOrigins.Any(invocation => !CoreSource(invocation.Source))))
                    { complete = false; break; }
                    cursor = Math.Min(end, segment.OutputOffset + segment.Text.Length);
                }
                complete &= cursor == end;
            }
            if (complete)
                foreach (var frame in evaluated.Segments.Where(segment => segment.OutputOffset < container.ClosingOutputOffset!.Value + container.ClosingOrigin!.Length
                    && segment.OutputOffset + segment.Text.Length > container.OpeningOutputOffset!.Value).SelectMany(segment => segment.ExpansionStack))
                    if (!reachableMacroDefinitions.Contains(frame.Definition)) reachableMacroDefinitions.Add(frame.Definition);
            coreRegistryContainers.Add(container, complete); return complete;
        }
        bool CopiedRegistry(WmlStaticScalarOwner owner, string tag, string id)
        {
            if (materialization?.ContainsRegistry(tag, id, owner) != true || owner.IsAmended
                || owner.Parent is not { } container) return false;
            if (coreRegistryContainers.TryGetValue(container, out var known)) return known;
            bool RecipeSource(WmlStaticSource capturedSource) => materialization.Registries.Any(receipt => SameSource(capturedSource, receipt.Range.Source));
            bool AllowedSource(WmlStaticSource capturedSource) => CoreSource(capturedSource) || RecipeSource(capturedSource)
                || materialization.Helpers.Any(helper => SameSource(capturedSource, helper.Range.Source));
            var complete = !container.IsAmended && container.OpeningOutputOffset.HasValue
                && container.ClosingOutputOffset.HasValue && container.OpeningOrigin is { } opening
                && container.ClosingOrigin is { } closing && RecipeSource(opening.Source) && RecipeSource(closing.Source);
            if (complete)
            {
                var cursor = container.OpeningOutputOffset!.Value;
                var end = container.ClosingOutputOffset!.Value + container.ClosingOrigin!.Length;
                foreach (var segment in evaluated.Segments.Where(segment => segment.OutputOffset < end
                    && segment.OutputOffset + segment.Text.Length > cursor))
                {
                    if (segment.OutputOffset > cursor || !AllowedSource(segment.Origin.Source)
                        || segment.ExpansionStack.Any(frame => !CoreSource(frame.Definition.Source)
                            && !materialization.ContainsHelper(frame.MacroName, frame.Definition)
                            || frame.InvocationOrigins.Count == 0 || frame.InvocationOrigins.Any(invocation => !AllowedSource(invocation.Source))))
                    { complete = false; break; }
                    cursor = Math.Min(end, segment.OutputOffset + segment.Text.Length);
                }
                complete &= cursor == end;
            }
            if (complete)
                foreach (var frame in evaluated.Segments.Where(segment => segment.OutputOffset < container.ClosingOutputOffset!.Value + container.ClosingOrigin!.Length
                    && segment.OutputOffset + segment.Text.Length > container.OpeningOutputOffset!.Value).SelectMany(segment => segment.ExpansionStack))
                    if (!reachableMacroDefinitions.Contains(frame.Definition)) reachableMacroDefinitions.Add(frame.Definition);
            coreRegistryContainers.Add(container, complete); return complete;
        }
        bool BaseCycle(WmlStaticScalarOwner unit, int depth)
        {
            if (done.Contains(unit)) return false;
            if (depth >= 256 || !visiting.Add(unit)) return true;
            foreach (var target in baseEdges.GetValueOrDefault(unit) ?? []) if (BaseCycle(target, depth + 1)) return true;
            visiting.Remove(unit); done.Add(unit); return false;
        }
        void Add(WccContentDependencyKind kind, string id, WmlStaticScalarOwner owner) => nodes.Add(new(kind, id, owner.OpeningOrigin, WccContentDependencyAvailability.CapturedCore));
        IEnumerable<string> Names(WmlStaticScalarField field, bool list, string key)
        {
            if (!WmlUnitReferenceProjection.Mapped(field) || !WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var decoded, out _)
                || !WmlUnitReferenceProjection.IsStaticName(decoded))
            { Issue("dependency.dynamic-reference", field.ValueText ?? "(unknown)", key, "The " + key + " reference is dynamic or unproved.", Origin(field)); return []; }
            return list ? decoded.Split(',').Select(id => id.Trim()).Where(id => id.Length != 0).ToArray() : decoded.Length == 0 ? [] : [decoded.Trim()];
        }
        void RegistryList(WmlStaticScalarOwner owner, string key, string tag, WccContentDependencyKind kind)
        {
            if (!owner.Fields.TryGetValue(key, out var field)) return;
            foreach (var id in Names(field, true, key))
            {
                var matches = scalar.Owners.Where(item => item.Parent is { } parent && parent.TagName == tag
                    && parent.Parent is { TagName: "units", Parent: null }
                    && (item.Fields.ContainsKey("unique_id") ? Literal(item, "unique_id") : Literal(item, "id")) == id).ToArray();
                if (matches.Length != 1 || !CoreRegistry(matches[0]) && !CopiedRegistry(matches[0], tag, id)) Issue("dependency.registry-needs-materialization", id, key,
                    "Registry entry '" + id + "' is not uniquely active in captured Core; its registry container/helper needs materialization.", Origin(field));
                else
                {
                    if (materialization?.ContainsRegistry(tag, id, matches[0]) == true)
                        nodes.Add(new(kind, id, matches[0].OpeningOrigin, WccContentDependencyAvailability.NeedsMaterialization));
                    else Add(kind, id, matches[0]);
                    pending.Enqueue(matches[0]);
                }
            }
        }
    }
    private static string? Literal(WmlStaticScalarOwner owner, string key) => owner.Fields.TryGetValue(key, out var field)
        && WmlUnitReferenceProjection.Mapped(field) && WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var value, out _) ? value : null;
    private static WmlStaticSourceRange? Origin(WmlStaticScalarField field) => field.Contributions.LastOrDefault()?.ValueOrigins.FirstOrDefault();
    private static WmlStaticScalarOwner UnitAncestor(WmlStaticScalarOwner owner)
    { while (owner.Parent is { } parent && owner.TagName != "unit_type") owner = parent; return owner; }
    private static bool Within(WmlStaticSourceRange range, WmlStaticScalarOwner owner) => owner.OpeningOrigin is { } opening && owner.ClosingOrigin is { } closing
        && SameSource(range.Source, opening.Source) && SameSource(range.Source, closing.Source) && range.StartOffset >= opening.StartOffset
        && range.StartOffset + range.Length <= closing.StartOffset + closing.Length;
    private static bool SameSource(WmlStaticSource? left, WmlStaticSource right) => left is not null && left.IsAuthored && right.IsAuthored
        && string.Equals(left.Path, right.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && left.ContentHash == right.ContentHash && left.Text == right.Text;
    private static bool Inside(string root, string path) => Path.GetRelativePath(root, path) is var relative && !Path.IsPathRooted(relative)
        && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static bool UnitFilter(string tag) => tag is "filter" or "filter_unit" or "filter_self" or "filter_attacker"
        or "filter_defender" or "filter_opponent" or "filter_adjacent" or "filter_second" or "filter_recall" or "filter_wml";

    private sealed class CoreOnlyResolver(IWmlStaticIncludeResolver includes, IWmlStaticResourceResolver resources,
        WmlStaticSource candidate, IReadOnlyList<WmlStaticSource> helpers) : IWmlStaticExistenceResolver, IWmlStaticResourceResolver
    {
        public IReadOnlyList<string> InstallationResourceFolders => resources.InstallationResourceFolders.Where(folder =>
            WmlResourceLookupPaths.SupportedInstallationResourceFolders.Contains(folder, StringComparer.Ordinal)).ToArray();
        public WmlStaticIncludeResolution Resolve(string path, WmlStaticSource caller) => path == "~wcc-copy/selected.cfg"
            ? new(WmlStaticTruth.True, [candidate], candidate.ContentHash) : path == "~wcc-copy/helpers.cfg"
                ? new(WmlStaticTruth.True, helpers, "wcc-copy-helpers:" + WccContentHash.ComputeText(string.Join("\n", helpers.Select(helper => helper.Path + "\n" + helper.ContentHash))))
                : Allowed(path) ? includes.Resolve(path, caller) : Refused(path);
        public WmlStaticIncludeResolution ResolveExistence(string path, WmlStaticSource caller) => Allowed(path)
            && includes is IWmlStaticExistenceResolver existence ? existence.ResolveExistence(path, caller) : Refused(path);
        public WmlStaticIncludeResolution ResolveResource(string path, WmlStaticSource caller) => ResourceAllowed(path)
            ? resources.ResolveResource(path, caller) : Refused(path);
        private static bool Allowed(string path) => new[] { "core", "themes", "lua", "ai", "internal" }.Any(root => path == root || path.StartsWith(root + "/", StringComparison.Ordinal));
        private static bool ResourceAllowed(string path) => path.StartsWith("core/", StringComparison.Ordinal)
            || WmlResourceLookupPaths.SupportedInstallationResourceFolders.Any(folder => path.StartsWith(WmlResourceLookupPaths.InstallationResourceMountPrefix + folder + "/", StringComparison.Ordinal));
        private static WmlStaticIncludeResolution Refused(string path) => new(WmlStaticTruth.Unknown, [], "wcc-core-only:" + path,
            "The reference '" + path + "' requires non-Core materialization or unsupported source-relative activation.");
    }
}
