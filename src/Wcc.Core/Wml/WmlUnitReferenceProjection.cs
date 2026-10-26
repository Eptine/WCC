using System.Collections.Frozen;

namespace Wcc.Core.Wml;

public enum WmlUnitReferenceKind { UnitType, Recruit, ExtraRecruit, Advancement, BaseUnit }

/// <summary>One direct evaluated attribute; its source ranges are navigation, never write authority.</summary>
public sealed record WmlUnitTypeReference(WmlStaticScalarOwner Owner, WmlStaticScalarField Field,
    WmlUnitReferenceKind Kind, bool IsList);

/// <summary>A registered type is a direct child of a proven top-level units container.</summary>
public sealed record WmlUnitTypeDeclaration(string Id, WmlStaticScalarOwner Owner,
    WmlStaticScalarField Field);

/// <summary>
/// Shared, pure observation of unit declarations and bounded unit-type references in
/// already evaluated WML. No filesystem, Core catalog activation, inheritance execution,
/// runtime Lua or edit authority is inferred here.
/// </summary>
public sealed class WmlUnitReferenceProjection
{
    private WmlUnitReferenceProjection(WmlStaticEvaluationResult evaluation, WmlStaticScalarProjection projection,
        IEnumerable<WmlUnitTypeDeclaration> declarations,
        IEnumerable<WmlUnitTypeReference> references, bool complete)
    {
        SourceEvaluation = evaluation;
        SourceProjection = projection;
        Declarations = Array.AsReadOnly(declarations.ToArray());
        References = Array.AsReadOnly(references.ToArray());
        DeclaredIds = Declarations.Select(declaration => declaration.Id).ToFrozenSet(StringComparer.Ordinal);
        HasCompleteDeclaredInventory = complete;
    }

    // Exact observation receipts for compatible reuse, never edit authority.
    internal WmlStaticEvaluationResult SourceEvaluation { get; }
    internal WmlStaticScalarProjection SourceProjection { get; }
    public IReadOnlyList<WmlUnitTypeDeclaration> Declarations { get; }
    public IReadOnlySet<string> DeclaredIds { get; }
    public IReadOnlyList<WmlUnitTypeReference> References { get; }
    /// <summary>Still requires the host's explicit engine Core-data and snapshot evidence.</summary>
    public bool HasCompleteDeclaredInventory { get; }

    public static WmlUnitReferenceProjection Create(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection? projection = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();
        projection ??= WmlStaticScalarProjection.Create(evaluation, cancellationToken);
        var roots = projection.Owners.Where(owner => owner.TagName == "units" && owner.Parent is null).ToArray();
        var rootSet = roots.ToHashSet();
        var sourceSet = evaluation.Sources.ToHashSet();
        var types = projection.Owners.Where(owner => owner.TagName == "unit_type" &&
            owner.Parent is { } parent && rootSet.Contains(parent)).ToArray();
        var declarations = new List<WmlUnitTypeDeclaration>();
        var complete = evaluation.IsComplete && projection.Diagnostics.Count == 0 && roots.Length != 0 && roots.All(HasCapturedPair);
        foreach (var type in types)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Literal token concatenation is observable without changing the
            // strict scalar resolver's edit authority. Unknown later branches
            // clear ValueText; uncertain structure clears the paired owner.
            if (HasCapturedPair(type) && HasCapturedPair(type.Parent!) &&
                type.Fields.TryGetValue("id", out var field) && HasExactDeclarationValue(field) &&
                TryLiteral(field.ValueText, out var id) && id.Length != 0 && !id.Contains(','))
                declarations.Add(new(id, type, field));
            else complete = false;
        }

        var typeSet = types.ToHashSet();
        var references = new List<WmlUnitTypeReference>();
        foreach (var owner in projection.Owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var side = owner.TagName == "side" && owner.Parent is { TagName: "scenario", Parent: null };
            var leader = owner.TagName == "leader" && owner.Parent is
                { TagName: "side", Parent: { TagName: "scenario", Parent: null } };
            var unit = owner.TagName == "unit" && WmlStaticActionBranchObservation.IsKnownActionScope(owner);
            if (side || leader || unit)
            {
                // Saved variation instances carry parent_type; it overrides type.
                Add(owner, owner.Fields.ContainsKey("parent_type") ? "parent_type" : "type", WmlUnitReferenceKind.UnitType, false);
                Add(owner, "extra_recruit", WmlUnitReferenceKind.ExtraRecruit, true);
                if (side) Add(owner, "recruit", WmlUnitReferenceKind.Recruit, true);
            }
            if (typeSet.Contains(owner) || IsVariant(owner, typeSet))
                Add(owner, "advances_to", WmlUnitReferenceKind.Advancement, true);
            if (owner.TagName == "base_unit" && owner.Parent is { } baseParent && typeSet.Contains(baseParent))
                Add(owner, "id", WmlUnitReferenceKind.BaseUnit, false);
        }
        return new(evaluation, projection, declarations, references, complete);

        void Add(WmlStaticScalarOwner owner, string key, WmlUnitReferenceKind kind, bool isList)
        {
            if (owner.Fields.TryGetValue(key, out var field)) references.Add(new(owner, field, kind, isList));
        }

        bool HasExactDeclarationValue(WmlStaticScalarField field)
        {
            if (!Mapped(field) || field.ValueText is not { } text ||
                field.Contributions.LastOrDefault() is not { } contribution ||
                contribution.ValueOutputOffset < 0 || contribution.ValueOutputLength != text.Length ||
                contribution.ValueOutputOffset > evaluation.Text.Length - contribution.ValueOutputLength ||
                !evaluation.Text.AsSpan(contribution.ValueOutputOffset, contribution.ValueOutputLength).SequenceEqual(text))
                return false;
            // An amendment's last assignment can lie beyond the original closing
            // offset. Its exact evaluated contribution is observation only,
            // never an insertion boundary or a route for rewriting the ID.
            return contribution.ValueOrigins.Count != 0 && contribution.ValueOrigins.All(IsCaptured);
        }

        bool IsCaptured(WmlStaticSourceRange? origin) => origin is not null && sourceSet.Contains(origin.Source) &&
            origin.StartOffset >= 0 && origin.Length >= 0 && origin.StartOffset <= origin.Source.Text.Length - origin.Length;

        bool HasCapturedPair(WmlStaticScalarOwner owner) => Paired(owner) &&
            HasExactBoundary(owner.OpeningOutputOffset, owner.OpeningOrigin) &&
            HasExactBoundary(owner.ClosingOutputOffset, owner.ClosingOrigin);

        bool HasExactBoundary(int? outputOffset, WmlStaticSourceRange? origin) =>
            outputOffset is { } offset && offset >= 0 && origin is { Length: > 0 } && IsCaptured(origin) &&
            offset <= evaluation.Text.Length - origin.Length &&
            evaluation.Text.AsSpan(offset, origin.Length).SequenceEqual(
                origin.Source.Text.AsSpan(origin.StartOffset, origin.Length));
    }

    internal static bool TryLiteral(string? raw, out string value)
    {
        value = "";
        if (!WmlResourceValueTextDecoder.TryDecode(raw, out var decoded, out _) ||
            !IsStaticName(decoded)) return false;
        value = decoded.Trim();
        return true;
    }

    internal static bool IsStaticName(string value) =>
        value.IndexOfAny(['$', '{', '}', '(', ')', '[', ']', '<', '>']) < 0 && !value.Any(char.IsControl);

    internal static bool Mapped(WmlStaticScalarField field) => field.Contributions.LastOrDefault() is
        { ValueMappingIsExact: true, CompositeAssignmentKeys.Count: 0 };

    private static bool Paired(WmlStaticScalarOwner owner) => owner.OpeningOutputOffset.HasValue && owner.ClosingOutputOffset.HasValue;

    private static bool IsVariant(WmlStaticScalarOwner owner, HashSet<WmlStaticScalarOwner> types)
    {
        if (owner.TagName is not ("male" or "female" or "variation")) return false;
        for (var parent = owner.Parent; parent is not null; parent = parent.Parent)
        {
            if (types.Contains(parent)) return true;
            if (parent.TagName is not ("male" or "female" or "variation")) return false;
        }
        return false;
    }

}
