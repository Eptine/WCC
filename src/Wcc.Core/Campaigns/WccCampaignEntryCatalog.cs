using System.Text;
using Wcc.Core.Library;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>One reached menu occurrence, independently identified even when two entries share defines.</summary>
public sealed class WccCampaignEntry
{
    internal WccCampaignEntry(WccStaticSourceCaptureResult capture, string identity, string? campaignId, string name, string? firstScenarioId,
        WmlStaticScalarOwner owner, WmlStaticSource? ownerSource, WmlBlockSpan? span,
        WmlCampaignEvaluationPlan? plan, IEnumerable<string> diagnostics)
    {
        SourceCapture = capture; Identity = identity; CampaignId = campaignId; Name = name; FirstScenarioId = firstScenarioId;
        Owner = owner; OwnerSource = ownerSource; AuthoredCampaignSpan = span; Plan = plan;
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }
    public string Identity { get; }
    internal WccStaticSourceCaptureResult SourceCapture { get; }
    public string? CampaignId { get; }
    public string Name { get; }
    public string? FirstScenarioId { get; }
    public WmlStaticScalarOwner Owner { get; }
    public WmlStaticSource? OwnerSource { get; }
    public WmlBlockSpan? AuthoredCampaignSpan { get; }
    /// <summary>The package main remains the preprocessing entrypoint, including for a menu in another file.</summary>
    public WmlCampaignEvaluationPlan? Plan { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public bool IsContextComplete => Plan is { IsContextDiscoveryComplete: true } && Diagnostics.Count == 0;
    public bool AuthorizesPersistence => false;
}

/// <summary>Immutable ordinary-menu evidence. It does not turn a selected owner into a writable campaign.</summary>
public sealed class WccCampaignEntryCatalogProjection
{
    internal WccCampaignEntryCatalogProjection(WccStaticSourceCaptureResult capture, CampaignInitialFieldProjection menu,
        IEnumerable<WccCampaignEntry> entries, IEnumerable<string> diagnostics)
    {
        Capture = capture; Menu = menu; Entries = Array.AsReadOnly(entries.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }
    public WccStaticSourceCaptureResult Capture { get; }
    public CampaignInitialFieldProjection Menu { get; }
    public IReadOnlyList<WccCampaignEntry> Entries { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public bool IsComplete => Menu.IsComplete && Diagnostics.Count == 0 && Entries.All(entry => entry.IsContextComplete);
    public bool AuthorizesPersistence => false;
}

/// <summary>
/// Discovers campaign entries from the existing captured ordinary-menu evaluation. Raw tags in inactive
/// branches (including unit-tree tooling menus) are not entries. Exact source anchors are navigation
/// evidence; existing collected writers still have to establish their own whole-package review scope.
/// </summary>
public static class WccCampaignEntryCatalog
{
    private const int MaximumEntries = 64;

    /// <summary>Retains the real menu evaluation while presenting the selected physical menu source.</summary>
    public static CampaignInitialFieldProjection CreateSelectedMenuProjection(WccCampaignEntryCatalogProjection catalog,
        WccCampaignEntry entry)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(entry);
        if (!catalog.Entries.Any(candidate => ReferenceEquals(candidate, entry)))
            throw new ArgumentException("The selected campaign entry belongs to another captured menu.", nameof(entry));
        var menu = catalog.Menu;
        return new(entry.OwnerSource ?? menu.Source, menu.Context, menu.Projection, menu.Diagnostics, entry.Plan)
        { SelectedCampaignOwner = entry.Owner, EvaluationEntrySource = menu.EvaluationEntrySource ?? menu.Source };
    }

    public static WccCampaignEntryCatalogProjection Project(WccStaticSourceCaptureResult capture,
        CancellationToken cancellationToken = default)
    {
        return Project(capture, null, cancellationToken);
    }

    /// <summary>Retains the host's compatibility target without selecting one menu owner as the package.</summary>
    public static WccCampaignEntryCatalogProjection Project(WccStaticSourceCaptureResult capture,
        WmlCampaignEvaluationPlan? suppliedPlan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var menu = CampaignInitialFieldProjector.Project(capture, suppliedPlan, cancellationToken);
        var diagnostics = menu.Diagnostics.ToList(); var entries = new List<WccCampaignEntry>();
        if (menu.Source is not { } root || menu.Projection?.SourceEvaluation is not { } evaluation)
            return new(capture, menu, entries, diagnostics);
        if (menu.Campaigns.Count > MaximumEntries)
            diagnostics.Add("The ordinary menu exceeds the bounded campaign-entry catalog limit.");
        for (var index = 0; index < Math.Min(menu.Campaigns.Count, MaximumEntries); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = menu.Campaigns[index]; var problems = new List<string>();
            var id = Read(owner, "id");
            var nameField = owner.ResolveField("name");
            var name = nameField.State == WmlStaticScalarResolutionState.Known && nameField.ValueText is { } nameWml &&
                WccScenarioFieldValueCodec.IsSupportedLiteralWml(nameWml)
                    ? WccScenarioFieldValueCodec.DecodeLiteral(nameWml) : Read(owner, "name") ?? id ?? "Campaign " + (index + 1);
            var first = Read(owner, "first_scenario");
            var span = WccCampaignCapturedOwner.FindAuthoredSpan(owner, evaluation);
            WmlCampaignEvaluationPlan? plan = null;
            if (!menu.IsComplete) problems.Add("The ordinary campaign menu is incomplete; context activation remains unproven.");
            else if (!WmlCapturedCampaignEvaluationPlan.TryObserveMetadata(owner, out var metadata, out var problem))
                problems.Add(problem ?? "The selected campaign context declarations are unresolved.");
            else if (owner.ResolveField("id").State == WmlStaticScalarResolutionState.Unresolved)
                problems.Add("The selected campaign ID is unresolved.");
            else
            {
                var contexts = metadata!.Difficulties.Count == 0
                    ? new[] { new WmlEvaluationContext(metadata.CampaignDefine, additionalDefines: metadata.ExtraDefines,
                        targetCompatibility: menu.Context.TargetCompatibility) }
                    : metadata.Difficulties.Select(difficulty => new WmlEvaluationContext(metadata.CampaignDefine,
                        difficulty.Define, metadata.ExtraDefines, menu.Context.TargetCompatibility)).ToArray();
                plan = new(id, metadata.CampaignDefine, metadata.Difficulties, contexts, [], root.Path, root.Text,
                    isContextDiscoveryComplete: true)
                {
                    // Preserve existing root-owner compatibility. Included owners deliberately cannot
                    // satisfy old root-span writer guards by borrowing another file's coordinates.
                    AuthoredCampaignSpan = owner.OpeningOrigin?.Source == root ? span : null
                };
            }
            var identity = WccContentHash.ComputeText(root.Path + "\u001f" + root.ContentHash + "\u001f"
                + WccCampaignCapturedOwner.Key(owner) + "\u001f" + index);
            entries.Add(new(capture, identity, id, name, first, owner, owner.OpeningOrigin?.Source, span, plan, problems));
        }
        if (menu.IsComplete && entries.Count == 0) diagnostics.Add("No singleplayer campaign entry was reached in the ordinary menu.");
        return new(capture, menu, entries, diagnostics);
    }

    internal static string? Read(WmlStaticScalarOwner owner, string key)
    {
        var field = owner.ResolveField(key);
        return field.State == WmlStaticScalarResolutionState.Known
            && WmlResourceValueTextDecoder.TryDecode(field.ValueText, out var value, out _) ? value : null;
    }
}

internal static class WccCampaignCapturedOwner
{
    // Navigation can identify a physical opening even when a helper closes and
    // amends that owner. This intentionally grants none of FindAuthoredSpan's
    // editable-boundary guarantees.
    internal static WmlBlockSpan? FindDisplaySpan(WmlStaticScalarOwner owner, WmlStaticEvaluationResult evaluation)
    {
        if (owner.OpeningOrigin is not { Source.IsAuthored: true } opening || !evaluation.Sources.Contains(opening.Source)
            || opening.Text != "[" + owner.TagName + "]" || owner.OpeningOutputOffset is not { } offset
            || offset < 0 || offset > evaluation.Text.Length - opening.Length
            || !evaluation.Text.AsSpan(offset, opening.Length).SequenceEqual(opening.Text.AsSpan())
            || owner.OpeningExpansionStack.Any(frame => frame.Definition.Source.Path == opening.Source.Path
                && frame.Definition.Source.ContentHash == opening.Source.ContentHash
                && opening.StartOffset < frame.Definition.StartOffset + frame.Definition.Length
                && frame.Definition.StartOffset < opening.StartOffset + opening.Length)) return null;
        var matches = WmlBlockScanner.FindTopLevelBlockSpans(opening.Source.Text).Where(span => span.TagName == owner.TagName
            && span.StartIndex >= 0 && span.EndIndexExclusive <= opening.Source.Text.Length
            && opening.StartOffset == span.StartIndex + span.RawText.Length - span.RawText.TrimStart().Length).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static WmlBlockSpan? FindAuthoredSpan(WmlStaticScalarOwner owner, WmlStaticEvaluationResult evaluation)
    {
        if (owner.IsAmended || owner.OpeningExpansionStack.Count != 0 || owner.ClosingExpansionStack.Count != 0
            || owner.OpeningOrigin is not { Source.IsAuthored: true } opening || owner.ClosingOrigin is not { } closing
            || !ReferenceEquals(opening.Source, closing.Source) || !evaluation.Sources.Contains(opening.Source)
            || opening.Text != "[" + owner.TagName + "]" || closing.Text != "[/" + owner.TagName + "]") return null;
        var matches = WmlBlockScanner.FindTopLevelBlockSpans(opening.Source.Text).Where(span => span.TagName == owner.TagName
            && span.StartIndex >= 0 && span.EndIndexExclusive <= opening.Source.Text.Length
            && opening.StartOffset == span.StartIndex + span.RawText.Length - span.RawText.TrimStart().Length
            && closing.StartOffset >= opening.StartOffset && closing.StartOffset + closing.Length <= span.EndIndexExclusive
            && Boundary(opening, owner.OpeningOutputOffset) && Boundary(closing, owner.ClosingOutputOffset)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
        bool Boundary(WmlStaticSourceRange range, int? outputOffset) => range.StartOffset >= 0 && range.Length >= 0
            && range.StartOffset <= range.Source.Text.Length - range.Length && outputOffset is { } offset && offset >= 0
            && offset <= evaluation.Text.Length - range.Length
            && evaluation.Text.AsSpan(offset, range.Length).SequenceEqual(range.Text.AsSpan());
    }

    internal static string Key(WmlStaticScalarOwner owner)
    {
        var builder = new StringBuilder();
        Append(owner.OpeningOrigin); Append(owner.ClosingOrigin);
        foreach (var frame in owner.OpeningExpansionStack)
        {
            builder.Append(frame.MacroName).Append('\u001f'); Append(frame.Definition);
            foreach (var origin in frame.InvocationOrigins) Append(origin);
        }
        return builder.ToString();
        void Append(WmlStaticSourceRange? range)
        {
            if (range is null) { builder.Append("unknown\u001f"); return; }
            builder.Append(range.Source.Path).Append('\u001f').Append(range.Source.ContentHash).Append('\u001f')
                .Append(range.StartOffset).Append('\u001f').Append(range.Length).Append('\u001f');
        }
    }
}
