using System.Globalization;
using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

public static class CampaignImporter
{
    public static CampaignDocument ImportFromText(string text, string? mainCfgPath = null)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var spans = WmlBlockScanner.FindTopLevelBlockSpans(text);

        var campaignSpan = spans.FirstOrDefault(b => string.Equals(b.TagName, "campaign", StringComparison.OrdinalIgnoreCase));
        if (campaignSpan is null)
            throw new InvalidOperationException("No top-level [campaign] block found.");

        var campaignBlock = new WmlBlock(campaignSpan.TagName, campaignSpan.RawText);
        var campaignEditable = WmlEditableBlockParser.Parse(campaignBlock, CampaignSchema.KnownKeys);

        var idState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(campaignEditable, "id");
        var nameState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(campaignEditable, "name");
        var abbrevState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(campaignEditable, "abbrev");
        var defineState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(campaignEditable, "define");
        var firstScenarioState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(campaignEditable, "first_scenario");
        var iconState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(campaignEditable, "icon");
        var imageState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(campaignEditable, "image");
        var backgroundState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(campaignEditable, "background");
        var descriptionState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(campaignEditable, "description");
        var rankState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(campaignEditable, "rank");
        var yearState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(campaignEditable, "year");

        var campaign = new CampaignDefinition
        {
            Id = idState.Kind == WmlFieldSafetyKind.PlainEditable ? idState.PlainValue : null,
            NameWml = nameState.Kind == WmlFieldSafetyKind.PlainEditable ? nameState.PlainValue : null,
            AbbrevWml = abbrevState.Kind == WmlFieldSafetyKind.PlainEditable ? abbrevState.PlainValue : null,
            Define = defineState.Kind == WmlFieldSafetyKind.PlainEditable ? defineState.PlainValue : null,
            FirstScenario = firstScenarioState.Kind == WmlFieldSafetyKind.PlainEditable ? firstScenarioState.PlainValue : null,
            Icon = iconState.Kind == WmlFieldSafetyKind.PlainEditable ? iconState.PlainValue : null,
            Image = imageState.Kind == WmlFieldSafetyKind.PlainEditable ? imageState.PlainValue : null,
            Background = backgroundState.Kind == WmlFieldSafetyKind.PlainEditable ? backgroundState.PlainValue : null,
            DescriptionWml = descriptionState.Kind == WmlFieldSafetyKind.PlainEditable ? descriptionState.PlainValue : null,
            Rank = rankState.Kind == WmlFieldSafetyKind.PlainEditable ? ParseInt(rankState.PlainValue) : null,
            Year = yearState.Kind == WmlFieldSafetyKind.PlainEditable ? yearState.PlainValue : null
        };

        campaign.FieldStates["id"] = idState;
        campaign.FieldStates["name"] = nameState;
        campaign.FieldStates["abbrev"] = abbrevState;
        campaign.FieldStates["define"] = defineState;
        campaign.FieldStates["first_scenario"] = firstScenarioState;
        campaign.FieldStates["icon"] = iconState;
        campaign.FieldStates["image"] = imageState;
        campaign.FieldStates["background"] = backgroundState;
        campaign.FieldStates["description"] = descriptionState;
        campaign.FieldStates["rank"] = rankState;
        campaign.FieldStates["year"] = yearState;

        var textDomainSpan = spans.FirstOrDefault(b => string.Equals(b.TagName, "textdomain", StringComparison.OrdinalIgnoreCase));
        WmlEditableBlock? textDomainEditable = null;
        CampaignTextDomain? textDomain = null;

        if (textDomainSpan is not null)
        {
            var tdBlock = new WmlBlock(textDomainSpan.TagName, textDomainSpan.RawText);
            textDomainEditable = WmlEditableBlockParser.Parse(tdBlock, CampaignTextDomainSchema.KnownKeys);

            var tdNameState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(textDomainEditable, "name");
            var tdPathState = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(textDomainEditable, "path");

            textDomain = new CampaignTextDomain
            {
                Name = tdNameState.Kind == WmlFieldSafetyKind.PlainEditable ? tdNameState.PlainValue : null,
                Path = tdPathState.Kind == WmlFieldSafetyKind.PlainEditable ? tdPathState.PlainValue : null
            };

            textDomain.FieldStates["name"] = tdNameState;
            textDomain.FieldStates["path"] = tdPathState;
        }

        var doc = new CampaignDocument
        {
            OriginalText = text,
            CampaignBlockSpan = campaignSpan,
            CampaignEditableBlock = campaignEditable,
            Campaign = campaign,
            TextDomainBlockSpan = textDomainSpan,
            TextDomainEditableBlock = textDomainEditable,
            TextDomain = textDomain
        };

        doc.AssetReferences.AddRange(WccAssetReferenceCollector.Collect(doc));

        if (!string.IsNullOrWhiteSpace(mainCfgPath))
        {
            doc.IncludeReferences.AddRange(
                CampaignMainIncludeScanner.ScanFromMainCfgText(text, mainCfgPath));
        }

        return doc;
    }

    private static int? ParseInt(string? s)
    {
        if (s is null) return null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}