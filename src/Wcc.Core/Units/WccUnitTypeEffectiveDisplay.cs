using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

/// <summary>A read-only value after preprocessing and base-unit merging. Never an edit route.</summary>
public sealed record WccUnitTypeEffectiveField(string Key, string? ValueText, bool IsKnown,
    bool IsInherited, WmlStaticScalarOwner DefiningOwner, IReadOnlyList<WmlStaticSourceRange> Origins);

/// <summary>An effective child and its optional authored counterpart in the selected unit.</summary>
public sealed class WccUnitTypeEffectiveNode
{
    internal WccUnitTypeEffectiveNode(string tagName, WmlStaticScalarOwner? localOwner,
        IDictionary<string, WccUnitTypeEffectiveField> fields, IEnumerable<WccUnitTypeEffectiveNode> children,
        bool structureKnown)
    {
        TagName = tagName;
        LocalOwner = localOwner;
        Fields = new ReadOnlyDictionary<string, WccUnitTypeEffectiveField>(
            new Dictionary<string, WccUnitTypeEffectiveField>(fields, StringComparer.Ordinal));
        Children = Array.AsReadOnly(children.ToArray());
        IsStructureKnown = structureKnown;
    }
    public string TagName { get; }
    public WmlStaticScalarOwner? LocalOwner { get; }
    public IReadOnlyDictionary<string, WccUnitTypeEffectiveField> Fields { get; }
    public IReadOnlyList<WccUnitTypeEffectiveNode> Children { get; }
    public bool IsStructureKnown { get; }
    public string? GetKnownValue(string key) => Fields.TryGetValue(key, out var field) && field.IsKnown ? field.ValueText : null;
}

/// <summary>Display-only inheritance over the exact captured registry; never changes a save model.</summary>
public sealed class WccUnitTypeEffectiveDisplay
{
    private WccUnitTypeEffectiveDisplay(WmlStaticScalarOwner selectedOwner, WccUnitTypeEffectiveNode root,
        UnitTypeDocument preview, IEnumerable<string> diagnostics)
    {
        SelectedOwner = selectedOwner;
        Root = root;
        PreviewDocument = preview;
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct(StringComparer.Ordinal).ToArray());
        IsComplete = Diagnostics.Count == 0;
    }
    public WmlStaticScalarOwner SelectedOwner { get; }
    public WccUnitTypeEffectiveNode Root { get; }
    /// <summary>Detached display data. Must never be passed to a source writer.</summary>
    public UnitTypeDocument PreviewDocument { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public bool IsComplete { get; }
    /// <summary>The selected base-unit chain resolved exactly, independent of unrelated child-field diagnostics.</summary>
    public bool HasResolvedInheritance { get; private init; }
    public string? EffectiveRace => WmlResourceValueTextDecoder.TryDecode(Root.GetKnownValue("race"), out var race, out _) ? race : null;

    public static WccUnitTypeEffectiveDisplay Create(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection projection, WmlStaticScalarOwner selectedOwner,
        CancellationToken cancellationToken = default) =>
        CreateCore(evaluation, projection, selectedOwner, null, cancellationToken);

    /// <summary>Reuses an inventory from this exact projection; other captures and contexts cannot lend evidence.</summary>
    public static WccUnitTypeEffectiveDisplay Create(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection projection, WmlStaticScalarOwner selectedOwner,
        WmlUnitReferenceProjection registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return CreateCore(evaluation, projection, selectedOwner, registry, cancellationToken);
    }

    private static WccUnitTypeEffectiveDisplay CreateCore(WmlStaticEvaluationResult evaluation,
        WmlStaticScalarProjection projection, WmlStaticScalarOwner selectedOwner,
        WmlUnitReferenceProjection? registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(selectedOwner);
        if (!ReferenceEquals(projection.SourceEvaluation, evaluation) || !projection.Owners.Contains(selectedOwner) || selectedOwner.TagName != "unit_type")
            throw new ArgumentException("Select a unit type from this exact evaluation and scalar projection.", nameof(selectedOwner));
        cancellationToken.ThrowIfCancellationRequested();
        if (registry is not null && (!ReferenceEquals(registry.SourceEvaluation, evaluation) ||
            !ReferenceEquals(registry.SourceProjection, projection)))
            throw new ArgumentException("Use a unit registry from this exact scalar projection.", nameof(registry));
        var builder = new Builder(evaluation, projection, selectedOwner, registry, cancellationToken);
        var root = builder.Resolve(selectedOwner, []);
        return new(selectedOwner, root, UnitTypeImporter.ImportSingleUnitFromRawText(Render(root)), builder.Diagnostics)
            { HasResolvedInheritance = builder.HasResolvedInheritance };
    }

    /// <summary>Normalized effective WML for previews only; it has no authored source identity.</summary>
    public static string Render(WccUnitTypeEffectiveNode node)
    {
        var text = new StringBuilder();
        Append(node, 0);
        return text.ToString();
        void Append(WccUnitTypeEffectiveNode current, int depth)
        {
            var indent = new string(' ', depth * 4);
            text.Append(indent).Append('[').Append(current.TagName).AppendLine("]");
            foreach (var field in current.Fields.Values.Where(field => field.IsKnown && field.ValueText is not null)
                         .OrderBy(field => field.Key, StringComparer.Ordinal))
                text.Append(indent).Append("    ").Append(field.Key).Append('=').AppendLine(field.ValueText);
            foreach (var child in current.Children.Where(child => child.IsStructureKnown)) Append(child, depth + 1);
            text.Append(indent).Append("[/").Append(current.TagName).AppendLine("]");
        }
    }

    private sealed class Builder(WmlStaticEvaluationResult evaluation, WmlStaticScalarProjection projection,
        WmlStaticScalarOwner selected, WmlUnitReferenceProjection? registry, CancellationToken cancellationToken)
    {
        private WmlUnitReferenceProjection? _registry = registry;
        private WmlUnitReferenceProjection Registry =>
            _registry ??= WmlUnitReferenceProjection.Create(evaluation, projection, cancellationToken);
        private int _nodes;
        public List<string> Diagnostics { get; } = [];
        public bool HasResolvedInheritance { get; private set; } = true;

        public WccUnitTypeEffectiveNode Resolve(WmlStaticScalarOwner owner, HashSet<WmlStaticScalarOwner> path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.Count >= 64) throw new InvalidOperationException("The base-unit inheritance depth limit was reached.");
            var local = FromOwner(owner, 0);
            if (!path.Add(owner))
            { HasResolvedInheritance = false; Diagnostics.Add("Base-unit inheritance contains a cycle; inherited values are unavailable."); return local; }
            try
            {
                var bases = owner.DirectChildren.Where(child => child.TagName == "base_unit").ToArray();
                if (bases.Length == 0) return local;
                // Engine optional_child reads the first active base-unit tag.
                if (bases.Length > 1) Diagnostics.Add("Several [base_unit] tags are present; the engine uses the first active tag.");
                if (!Registry.HasCompleteDeclaredInventory || !WmlUnitReferenceProjection.TryLiteral(bases[0].GetKnownValue("id"), out var id) || id.Length == 0)
                { HasResolvedInheritance = false; Diagnostics.Add("The active base-unit identity or unit registry is unresolved; inherited values are unavailable."); return local; }
                var matches = Registry.Declarations.Where(entry => entry.Id == id).ToArray();
                if (matches.Length != 1)
                { HasResolvedInheritance = false; Diagnostics.Add(matches.Length == 0 ? $"Base unit '{id}' is not active in this context." : $"Base unit '{id}' has several active definitions."); return local; }
                if (path.Contains(matches[0].Owner))
                { HasResolvedInheritance = false; Diagnostics.Add("Base-unit inheritance contains a cycle; inherited values are unavailable."); return local; }
                var before = Diagnostics.Count;
                var basis = Resolve(matches[0].Owner, path);
                // A failed chain cannot donate a fabricated partial inheritance result.
                if (Diagnostics.Skip(before).Any(message => message.Contains("inheritance contains a cycle", StringComparison.Ordinal) ||
                    message.Contains("not active", StringComparison.Ordinal) || message.Contains("several active definitions", StringComparison.Ordinal) ||
                    message.Contains("registry is unresolved", StringComparison.Ordinal))) return local;
                return Merge(basis, local, 0);
            }
            finally { path.Remove(owner); }
        }

        private WccUnitTypeEffectiveNode FromOwner(WmlStaticScalarOwner owner, int depth)
        {
            CheckLimit(depth);
            var structure = owner.ResolveField("__wcc_effective_structure_probe").State != WmlStaticScalarResolutionState.Unresolved;
            if (!structure) Diagnostics.Add($"The [{owner.TagName}] structure is unresolved; inherited children cannot be fully determined.");
            var fields = new Dictionary<string, WccUnitTypeEffectiveField>(StringComparer.Ordinal);
            foreach (var field in owner.Fields.Values)
            {
                fields[field.Key] = new(field.Key, field.ValueText, structure && field.IsKnown,
                    !IsSelected(owner), owner, Array.AsReadOnly(field.Origins.ToArray()));
                if (!field.IsKnown) Diagnostics.Add($"The [{owner.TagName}] field '{field.Key}' is unresolved in this context.");
            }
            return new(owner.TagName, IsSelected(owner) ? owner : null, fields,
                owner.DirectChildren.Select(child => FromOwner(child, depth + 1)), structure);
        }

        private bool IsSelected(WmlStaticScalarOwner owner)
        {
            for (var current = owner; current is not null; current = current.Parent)
                if (ReferenceEquals(current, selected)) return true;
            return false;
        }

        private WccUnitTypeEffectiveNode Merge(WccUnitTypeEffectiveNode basis, WccUnitTypeEffectiveNode local, int depth)
        {
            CheckLimit(depth);
            var fields = basis.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            // Engine attributes are ordered by name, including special operations.
            foreach (var field in local.Fields.Values.OrderBy(field => field.Key, StringComparer.Ordinal))
            {
                var add = field.Key.StartsWith("add_to_", StringComparison.Ordinal);
                var concat = field.Key.StartsWith("concat_to_", StringComparison.Ordinal);
                if (!add && !concat) { fields[field.Key] = field; continue; }
                var key = field.Key[(add ? 7 : 10)..];
                fields.TryGetValue(key, out var old);
                string? value = null;
                var known = field.IsKnown && (old is null || old.IsKnown);
                if (known && add)
                {
                    var leftKnown = TryNumber(old?.ValueText, out var left);
                    var rightKnown = TryNumber(field.ValueText, out var right);
                    known = leftKnown && rightKnown && double.IsFinite(left + right);
                    if (known) value = (left + right).ToString("G17", CultureInfo.InvariantCulture);
                }
                else if (known)
                {
                    var leftKnown = TryText(old?.ValueText, out var left);
                    var rightKnown = TryText(field.ValueText, out var right);
                    known = leftKnown && rightKnown;
                    if (known) value = '"' + (left + right).Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
                }
                if (!known) Diagnostics.Add($"The inheritance operation '{field.Key}' needs literal values; its result is unresolved.");
                fields[key] = new(key, value, known, field.IsInherited, field.DefiningOwner,
                    Array.AsReadOnly((old?.Origins ?? []).Concat(field.Origins).ToArray()));
            }
            if (!basis.IsStructureKnown || !local.IsStructureKnown)
                return new(local.TagName, local.LocalOwner, fields.ToDictionary(pair => pair.Key,
                    pair => pair.Value with { IsKnown = false }, StringComparer.Ordinal), local.Children, false);
            var children = basis.Children.ToList();
            var groups = local.Children.GroupBy(child => child.TagName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var visits = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < children.Count; index++)
            {
                var child = children[index];
                if (!groups.TryGetValue(child.TagName, out var overlays)) continue;
                var ordinal = visits.GetValueOrDefault(child.TagName);
                if (ordinal >= overlays.Length) continue;
                visits[child.TagName] = ordinal + 1;
                var overlay = overlays[ordinal];
                if (IsTrue(overlay.GetKnownValue("__remove")))
                { children.RemoveAt(index--); continue; }
                if (overlay.Fields.TryGetValue("__remove", out var removal) && !removal.IsKnown)
                {
                    Diagnostics.Add($"Removal of an inherited [{child.TagName}] is unresolved.");
                    children[index] = new(child.TagName, overlay.LocalOwner,
                        child.Fields.ToDictionary(pair => pair.Key, pair => pair.Value with { IsKnown = false }, StringComparer.Ordinal), [], false);
                    continue;
                }
                children[index] = Merge(child, overlay, depth + 1);
            }
            // Engine child_map appends unvisited tags in tag-name order.
            foreach (var group in groups.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                children.AddRange(group.Value.Skip(visits.GetValueOrDefault(group.Key)));
            return new(local.TagName, local.LocalOwner, fields, children, true);
        }

        private void CheckLimit(int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 128 || ++_nodes > 100_000)
                throw new InvalidOperationException("The effective unit display exceeded its inheritance/tree limits.");
        }
        private static bool IsTrue(string? raw) => TryText(raw, out var text) && text.Trim() is "yes" or "true" or "on" or "1";
        private static bool TryText(string? raw, out string text)
        { if (raw is null) { text = ""; return true; } return WmlResourceValueTextDecoder.TryDecode(raw, out text, out _); }
        private static bool TryNumber(string? raw, out double number)
        {
            number = 0;
            if (!TryText(raw, out var text)) return false;
            if (string.IsNullOrWhiteSpace(text)) return true;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
        }
    }
}
