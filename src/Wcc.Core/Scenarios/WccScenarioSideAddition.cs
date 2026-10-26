using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>One new literal side wrapper and optional saved shared field preset; no source-write authority.</summary>
public sealed class WccScenarioSideAddition
{
    public WccScenarioSideAddition(WccCampaignLocalPresetEntry? preset = null, string? saveIdOverride = null)
    {
        if (preset is not null && (!preset.IsSupported || preset.Role != WccCampaignLocalPresetRole.Side || preset.Payload is null))
            throw new ArgumentException("Select one supported saved campaign-local Side preset.", nameof(preset));
        if (saveIdOverride is not null)
        {
            if (preset is null || saveIdOverride.Length is 0 or > 128 * 1024 ||
                !WccScenarioFieldValueCodec.TryGetDefinition("side", "save_id", out var field) ||
                WccScenarioFieldValueCodec.InferInputKind(saveIdOverride) != WccScenarioFieldValueInputKind.Literal ||
                WccScenarioFieldValueCodec.ValidateInput(field, saveIdOverride) is { })
                throw new ArgumentException("Use one literal shared carryover save_id override on the selected Side preset.", nameof(saveIdOverride));
            EncodedSaveIdOverride = WccScenarioFieldValueCodec.EncodeLiteral(field, saveIdOverride);
        }
        Preset = preset; SaveIdOverride = saveIdOverride;
    }
    public WccCampaignLocalPresetEntry? Preset { get; }
    public string? SaveIdOverride { get; }
    public bool AuthorizesPersistence => false;
    internal string? EncodedSaveIdOverride { get; }
}
