using System.Text;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Projects.Creation;

/// <summary>One portable source renderer for campaign starters and newly created scenarios; no disk writes.</summary>
public static class WccScenarioCreationWriter
{
    public const string DefaultMap = "Gg,Gg,Gg,Gg,Gg,Gg,Gg,Gg\nGg,1 Kh,Ch,Gg,Gg,Gg,Gg,Gg\nGg,Ch,Ch,Gg,Gg,Gg,Gg,Gg\nGg,Gg,Gg,Gg,Gg,Gg,Gg,Gg\nGg,Gg,Gg,Gg,Gg,Ch,Ch,Gg\nGg,Gg,Gg,Gg,Gg,Ch,2 Kh,Gg\nGg,Gg,Gg,Gg,Gg,Gg,Gg,Gg\n";

    public static string RenderEmpty(string textDomain, string scenarioId) =>
        "#textdomain " + textDomain + "\n\n[scenario]\n    id=" + scenarioId + "\n[/scenario]\n";

    public static string RenderGuided(string textDomain, string scenarioId, string name, string mapReference,
        IReadOnlyList<string> defines, int turns, string playerId, string enemyId)
    {
        var builder = new StringBuilder("#textdomain " + textDomain + "\n\n[scenario]\n    id=" + scenarioId + "\n    name=" + Q(name, true) +
            "\n    next_scenario=null\n    map_file=" + Q(mapReference) + "\n");
        builder.Append("    turns=").Append(turns.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("    victory_when_enemies_defeated=yes\n    {DEFAULT_SCHEDULE}\n\n    [side]\n        side=1\n        controller=human\n        team_name=player\n        user_team_name=")
            .Append(Q("Player", true)).Append("\n        recruit=Spearman,Bowman\n");
        builder.Append("        gold=100\n");
        builder.Append("        [leader]\n            id=").Append(playerId).Append("\n            type=Lieutenant\n            name=")
            .Append(Q("Leader", true)).Append("\n            canrecruit=yes\n        [/leader]\n    [/side]\n\n    [side]\n        side=2\n        controller=ai\n        team_name=enemy\n        user_team_name=")
            .Append(Q("Enemy", true)).Append("\n        recruit=Orcish Grunt,Orcish Archer\n");
        builder.Append("        gold=75\n");
        return builder.Append("        [leader]\n            id=").Append(enemyId).Append("\n            type=Orcish Warrior\n            canrecruit=yes\n        [/leader]\n    [/side]\n[/scenario]\n").ToString();
    }

    /// <summary>The shared map parser supports multi-digit side starts. The wizard bounds its own side count.</summary>
    public static string RenderDefaultMap(int sides)
    {
        if (sides is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(sides));
        if (sides == 2) return DefaultMap;
        const int columns = 4;
        var rows = (sides + columns - 1) / columns;
        var width = Math.Min(sides, columns) * 4 + 2;
        var height = rows * 4 + 2;
        var cells = Enumerable.Range(0, height).Select(_ => Enumerable.Repeat("Gg", width).ToArray()).ToArray();
        for (var number = 1; number <= sides; number++)
        {
            var x = 1 + (number - 1) % columns * 4;
            var y = 1 + (number - 1) / columns * 4;
            cells[y][x] = number.ToString(System.Globalization.CultureInfo.InvariantCulture) + " Kh";
            cells[y][x + 1] = "Ch"; cells[y + 1][x] = "Ch"; cells[y + 1][x + 1] = "Ch";
        }
        return string.Join("\n", cells.Select(row => string.Join(',', row))) + "\n";
    }

    public static string RenderGuided(string textDomain, string scenarioId, string name, string mapReference,
        WmlCampaignEvaluationPlan plan, int turns, WccScenarioGuidedCreationOptions options)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(options);
        // Existing composers edit one block. Keep the file-level domain outside
        // that snippet and supply it explicitly for schedule materialization.
        var definitions = (options.Schedule?.Definitions ?? []).Concat(options.Music?.Definitions ?? [])
            .DistinctBy(definition => (definition.Name, definition.SourcePath, definition.SourceStartLine, definition.RawText, definition.TextDomain)).ToArray();
        var builder = new StringBuilder("[scenario]\n    id=" + scenarioId + "\n    name=" + Q(name, true)
            + "\n    next_scenario=null\n    map_file=" + Q(mapReference) + "\n");
        var turnsSlots = WccScenarioCreationPlanner.CaptureTurnsDifficultyValues(options.TurnsDifficultyValues, plan);
        if (turnsSlots is null)
            builder.Append("    turns=").Append(turns.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        else
            foreach (var slot in turnsSlots)
                builder.Append("#ifdef ").Append(slot.Define).Append("\n    turns=").Append(slot.ValueText).Append("\n#endif\n");
        var scaffold = builder.Append("    victory_when_enemies_defeated=yes\n[/scenario]\n").ToString();
        if (options.Schedule is { } schedule)
        {
            var projection = ScenarioInitialScheduleEditor.Project(scaffold, definitions, textDomain);
            var composed = ScenarioInitialScheduleEditor.Compose(projection, schedule.Edit);
            if (!composed.IsSuccess || composed.CandidateSource is null)
                throw new ArgumentException(composed.Diagnostic ?? "The selected initial schedule has no complete pure footprint.");
            scaffold = composed.CandidateSource;
        }
        if (options.Music is { } music)
        {
            var projection = ScenarioInitialMusicEditor.Project(scaffold, definitions);
            var composed = ScenarioInitialMusicEditor.Compose(projection, music.Tracks, music.MakeFirstIntro, music.LetIntroFinish);
            if (!composed.IsSuccess || composed.CandidateSource is null)
                throw new ArgumentException(composed.Problem ?? "The selected playlist has no complete supported footprint.");
            scaffold = composed.CandidateSource;
        }
        builder = new StringBuilder();
        for (var index = 0; index < options.Sides.Count; index++)
        {
            var side = options.Sides[index];
            builder.Append("\n    [side]\n        side=").Append(index + 1).Append('\n');
            if (side.Preset is { } preset) builder.Append("        {").Append(preset.Name).Append("}\n");
            else AppendPayload(builder, side.Payload!, plan, "        ");
            if (side.LocalValues.Count + side.LocalDifficultyValues.Count != 0)
                AppendPayload(builder, new(WccCampaignLocalPresetRole.Side, side.LocalValues, side.LocalDifficultyValues), plan, "        ");
            if (side.SaveIdOverride is { } save)
            {
                if (!ScenarioInitialFieldCatalog.TryGet("side", "save_id", out var saveField))
                    throw new InvalidOperationException("The shared Side field catalog has no carryover identity.");
                builder.Append("        save_id=").Append(WccScenarioFieldValueCodec.EncodeLiteral(saveField!, save)).Append('\n');
            }
            if (side.PresetLeaderCoordinates is { } coordinates && coordinates.Values.Count + coordinates.DifficultyValues.Count != 0)
            {
                builder.Append("        [+leader]\n");
                AppendPayload(builder, new(WccCampaignLocalPresetRole.Leader, coordinates.Values, coordinates.DifficultyValues), plan, "            ");
                builder.Append("        [/leader]\n");
            }
            foreach (var leader in side.Leaders ?? [])
            {
                builder.Append("        [leader]\n");
                if (leader.Preset is { } selected) builder.Append("            {").Append(selected.Name).Append("}\n");
                AppendPayload(builder, new(WccCampaignLocalPresetRole.Leader, leader.Values, leader.DifficultyValues), plan, "            ");
                builder.Append("        [/leader]\n");
            }
            builder.Append("    [/side]\n");
        }
        return "#textdomain " + textDomain + "\n\n" + scaffold.Insert(scaffold.LastIndexOf("[/scenario]", StringComparison.Ordinal), builder.ToString());
    }

    internal static string RenderPayload(WccCampaignLocalPresetPayload payload, WmlCampaignEvaluationPlan plan, string indent)
    {
        var builder = new StringBuilder(); AppendPayload(builder, payload, plan, indent); return builder.ToString();
    }

    private static void AppendPayload(StringBuilder builder, WccCampaignLocalPresetPayload payload, WmlCampaignEvaluationPlan plan, string indent)
    {
        // Reuse the preset field/branch/leader renderer instead of maintaining a second field writer.
        var definition = WccCampaignLocalPresetCatalog.RenderDefinition(Guid.Parse("11111111111111111111111111111111"),
            "WCC_CREATION_LITERAL", payload, plan);
        var lines = definition.Split('\n');
        foreach (var line in lines.Skip(2).Take(lines.Length - 4))
        {
            if (line.StartsWith('#')) builder.Append(line);
            else builder.Append(indent).Append(line.StartsWith("    ", StringComparison.Ordinal) ? line[4..] : line);
            builder.Append('\n');
        }
    }

    private static string Q(string value, bool translated = false) => WccCreationSourceWriter.EncodeText(value, translated);
}
