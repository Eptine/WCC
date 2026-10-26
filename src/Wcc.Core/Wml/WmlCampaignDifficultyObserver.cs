namespace Wcc.Core.Wml;

/// <summary>
/// Selects difficulty symbols from one already evaluated campaign menu. Modern
/// direct children take precedence over the deprecated difficulties= list. The
/// caller supplies its existing literal/source observation; no preprocessing,
/// context creation, filesystem access or edit authority is provided here.
/// </summary>
public static class WmlCampaignDifficultyObserver
{
    public const int MaxDifficulties = 16;

    public static bool TryObserve(WmlStaticScalarOwner campaign, string campaignDefine,
        IReadOnlyList<string> extraDefines, Func<WmlStaticScalarOwner, string, string[]?> observeSymbols,
        out IReadOnlyList<string> difficulties, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(extraDefines);
        ArgumentNullException.ThrowIfNull(observeSymbols);
        difficulties = [];
        problem = null;
        if (campaign.TagName != "campaign" || campaign.Parent is not null ||
            campaign.OpeningOutputOffset is null || campaign.ClosingOutputOffset is null)
        {
            problem = "The evaluated campaign has no complete menu owner.";
            return false;
        }

        var children = campaign.DirectChildren.Where(child => child.TagName == "difficulty").ToArray();
        var names = new List<string>();
        if (children.Length != 0)
        {
            if (children.Length > MaxDifficulties)
            {
                problem = "Difficulty defines conflict or exceed the bounded context limit (16).";
                return false;
            }
            foreach (var child in children)
            {
                var symbols = observeSymbols(child, "define");
                if (child.OpeningOutputOffset is null || child.ClosingOutputOffset is null || symbols is not { Length: 1 })
                {
                    problem = "An evaluated difficulty has no unique literal define.";
                    return false;
                }
                names.Add(symbols[0]);
            }
        }
        else
        {
            // Legacy metadata is a fallback only. An unrelated stale, different
            // or unresolved legacy list cannot invalidate real modern entries.
            var legacy = campaign.ResolveField("difficulties");
            if (legacy.State != WmlStaticScalarResolutionState.ProvenAbsent)
            {
                var symbols = observeSymbols(campaign, "difficulties");
                if (legacy.State != WmlStaticScalarResolutionState.Known || symbols is not { Length: > 0 })
                {
                    problem = "Legacy difficulty defines are not a supported literal list.";
                    return false;
                }
                names.AddRange(symbols);
            }
        }

        if (names.Count > MaxDifficulties || names.Distinct(StringComparer.Ordinal).Count() != names.Count ||
            names.Any(symbol => symbol == campaignDefine || extraDefines.Contains(symbol, StringComparer.Ordinal)))
        {
            problem = "Difficulty defines conflict or exceed the bounded context limit (16).";
            return false;
        }
        difficulties = Array.AsReadOnly(names.ToArray());
        return true;
    }
}
