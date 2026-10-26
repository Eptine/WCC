using Wcc.Core.Scenarios;

namespace Wcc.Core.Wml;

/// <summary>Derives context metadata from an already evaluated, exact captured campaign menu. No persistence authority.</summary>
public static class WmlCapturedCampaignEvaluationPlan
{
    public static bool TryCreate(WmlStaticEvaluationResult evaluation, WmlStaticScalarProjection projection,
        WmlStaticSource source, WmlCampaignEvaluationPlan? suppliedPlan,
        out WmlCampaignEvaluationPlan? resolvedPlan, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(evaluation); ArgumentNullException.ThrowIfNull(projection); ArgumentNullException.ThrowIfNull(source);
        resolvedPlan = null; problem = null;
        if (!evaluation.IsComplete || evaluation.Diagnostics.Count != 0 || projection.Diagnostics.Count != 0
            || !ReferenceEquals(projection.SourceEvaluation, evaluation) || !source.IsAuthored || !evaluation.Sources.Contains(source))
            return Refuse("The captured campaign menu is incomplete or belongs to a different evaluation.", out problem);
        if (suppliedPlan is null || suppliedPlan.Contexts.Count == 0 || !SamePath(source.Path, suppliedPlan.CampaignMainSourcePath)
            || source.ContentHash != suppliedPlan.CampaignMainTextHash)
            return Refuse("The supplied campaign plan has no exact current main-source anchor.", out problem);
        var target = suppliedPlan.Contexts[0].TargetCompatibility;
        if (suppliedPlan.Contexts.Any(context => context.TargetCompatibility != target))
            return Refuse("The supplied campaign plan has conflicting target compatibility values.", out problem);
        var campaigns = projection.Owners.Where(owner => owner.TagName == "campaign" && owner.Parent is null).Take(2).ToArray();
        if (campaigns.Length != 1 || !HasAuthoredAnchor(campaigns[0], suppliedPlan, source, evaluation))
            return Refuse("The captured campaign menu has no unique exact authored owner.", out problem);
        var campaign = campaigns[0];
        if (!TryObserveMetadata(campaign, out var metadata, out problem)) return false;
        var contexts = metadata!.Difficulties.Count == 0
            ? new[] { new WmlEvaluationContext(metadata.CampaignDefine, additionalDefines: metadata.ExtraDefines, targetCompatibility: target) }
            : metadata.Difficulties.Select(difficulty => new WmlEvaluationContext(metadata.CampaignDefine,
                difficulty.Define, metadata.ExtraDefines, target)).ToArray();
        if (!ReadLiteral(campaign, "id", out var id))
            return Refuse("The captured campaign ID remains unresolved.", out problem);
        resolvedPlan = new WmlCampaignEvaluationPlan(string.IsNullOrEmpty(id) ? null : id, metadata.CampaignDefine,
            metadata.Difficulties, contexts, [], source.Path, source.Text, isContextDiscoveryComplete: true)
        { AuthoredCampaignSpan = suppliedPlan.AuthoredCampaignSpan };
        return true;
    }

    internal static bool HasAuthoredAnchor(WmlStaticScalarOwner owner, WmlCampaignEvaluationPlan plan,
        WmlStaticSource source, WmlStaticEvaluationResult evaluation)
    {
        var span = plan.AuthoredCampaignSpan;
        if (owner.IsAmended || span is null || span.TagName != "campaign" || span.StartIndex < 0
            || span.EndIndexExclusive > source.Text.Length || source.Text[span.StartIndex..span.EndIndexExclusive] != span.RawText
            || owner.OpeningExpansionStack.Count != 0 || owner.ClosingExpansionStack.Count != 0
            || owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing
            || opening.StartOffset != span.StartIndex + span.RawText.Length - span.RawText.TrimStart().Length
            || opening.Text != "[campaign]" || closing.Text != "[/campaign]"
            || closing.StartOffset < opening.StartOffset || closing.StartOffset + closing.Length > span.EndIndexExclusive)
            return false;
        return Boundary(opening, owner.OpeningOutputOffset) && Boundary(closing, owner.ClosingOutputOffset);
        bool Boundary(WmlStaticSourceRange range, int? outputOffset) => ReferenceEquals(range.Source, source)
            && range.StartOffset >= 0 && range.Length >= 0 && range.StartOffset <= source.Text.Length - range.Length
            && outputOffset is { } offset && offset >= 0 && offset <= evaluation.Text.Length - range.Length
            && evaluation.Text.AsSpan(offset, range.Length).SequenceEqual(range.Text.AsSpan());
    }

    internal static bool TryObserveMetadata(WmlStaticScalarOwner campaign,
        out WmlCapturedCampaignContextMetadata? metadata, out string? problem)
    {
        metadata = null; problem = null;
        if (campaign.TagName != "campaign" || campaign.Parent is not null || campaign.IsAmended
            || campaign.OpeningOutputOffset is null || campaign.ClosingOutputOffset is null)
            return Refuse("The evaluated campaign has no complete unamended menu owner.", out problem);
        if (!ReadLiteral(campaign, "define", out var campaignDefine) || !ReadLiteral(campaign, "type", out var type)
            || !ReadLiteral(campaign, "extra_defines", out var extraText))
            return Refuse("Campaign context declarations are unresolved or malformed.", out problem);
        if (type.Length != 0 && type != "sp")
            return Refuse("Only singleplayer campaign menu settings are supported; multiplayer and hybrid campaigns require RAW review.", out problem);
        var extras = Symbols(extraText, allowEmpty: true);
        if (campaignDefine.Length != 0 && !Symbol(campaignDefine) || extras is null
            || extras.Distinct(StringComparer.Ordinal).Count() != extras.Length
            || campaignDefine.Length != 0 && extras.Contains(campaignDefine, StringComparer.Ordinal))
            return Refuse("The campaign has malformed or conflicting context symbols.", out problem);
        if (!WmlCampaignDifficultyObserver.TryObserve(campaign, campaignDefine, extras,
                (owner, key) => ReadLiteral(owner, key, out var value) ? Symbols(value, allowEmpty: false) : null,
                out var names, out problem)) return false;
        var children = campaign.DirectChildren.Where(child => child.TagName == "difficulty").ToArray();
        var variants = new List<WmlDifficultyVariant>(names.Count);
        if (children.Length != 0)
        {
            for (var index = 0; index < children.Length; index++)
            {
                var child = children[index];
                if (!ReadLiteral(child, "default", out var defaultValue) || defaultValue is not ("" or "yes" or "true" or "no" or "false"))
                    return Refuse("An evaluated difficulty has no supported literal default flag.", out problem);
                variants.Add(new(names[index], child.GetKnownValue("image"), child.GetKnownValue("label"),
                    child.GetKnownValue("description"), defaultValue is "yes" or "true", null, null));
            }
        }
        else
        {
            if (!ReadLiteral(campaign, "default_difficulty", out var defaultName)
                || defaultName.Length != 0 && (!Symbol(defaultName) || !names.Contains(defaultName, StringComparer.Ordinal)))
                return Refuse("The legacy default difficulty does not identify a declared literal difficulty.", out problem);
            variants.AddRange(names.Select(name => new WmlDifficultyVariant(name, null, null, null, name == defaultName, null, null)));
        }
        if (variants.Count(variant => variant.IsDefault) > 1)
            return Refuse("The campaign declares more than one default difficulty.", out problem);
        metadata = new(string.IsNullOrEmpty(campaignDefine) ? null : campaignDefine,
            Array.AsReadOnly(extras), Array.AsReadOnly(variants.ToArray()));
        return true;
    }

    private static bool ReadLiteral(WmlStaticScalarOwner owner, string key, out string value)
    {
        value = ""; var field = owner.ResolveField(key);
        if (field.State == WmlStaticScalarResolutionState.ProvenAbsent) return true;
        if (field.State != WmlStaticScalarResolutionState.Known || field.ValueText is not { } raw
            || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(raw)) return false;
        var decoded = WmlResourceValueTextDecoder.Decode(raw);
        if (!decoded.IsDecoded) return false;
        value = decoded.ValueText.Trim(); return true;
    }
    private static string[]? Symbols(string value, bool allowEmpty)
    {
        if (value.Length == 0) return allowEmpty ? [] : null;
        var values = value.Split(',', StringSplitOptions.TrimEntries);
        return values.All(Symbol) ? values : null;
    }
    private static bool Symbol(string value) => value.Length != 0 && (value[0] == '_' || char.IsAsciiLetter(value[0]))
        && value.Skip(1).All(character => character == '_' || char.IsAsciiLetterOrDigit(character));
    private static bool SamePath(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Refuse(string message, out string? problem) { problem = message; return false; }
}

internal sealed record WmlCapturedCampaignContextMetadata(string? CampaignDefine,
    IReadOnlyList<string> ExtraDefines, IReadOnlyList<WmlDifficultyVariant> Difficulties);
