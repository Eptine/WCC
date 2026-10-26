using System.Globalization;
using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

public static class ScenarioImporter
{
    private static readonly HashSet<string> NoKnownKeys = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ScenarioDocument> ImportAllFromText(string text)
    {
        var spans = WmlBlockScanner.FindTopLevelBlockSpans(text)
            .Where(b => string.Equals(b.TagName, "scenario", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var docs = new List<ScenarioDocument>();

        foreach (var span in spans)
        {
            var block = new WmlBlock(span.TagName, span.RawText);
            var editable = WmlEditableBlockParser.Parse(block, ScenarioSchema.KnownKeys);

            var scenario = new Scenario
            {
                Id = Get(editable, "id"),
                NextScenario = Get(editable, "next_scenario"),
                MapFile = Get(editable, "map_file"),
                MapData = Get(editable, "map_data"),
                Turns = GetInt(editable, "turns"),
                TurnAt = GetInt(editable, "turn_at"),
                RandomStartTime = Get(editable, "random_start_time"),
                Music = Get(editable, "music"),
                VictoryWhenEnemiesDefeated = Get(editable, "victory_when_enemies_defeated")
            };
            foreach (var pair in WmlFieldSafetyAnalyzer.AnalyzeLiteralTopLevelFields(
                         editable,
                         ScenarioSchema.KnownKeys))
            {
                scenario.FieldStates[pair.Key] = pair.Value;
            }

            var sideDocs = ImportSidesFromScenarioEditable(editable);
            foreach (var sd in sideDocs)
                scenario.Sides.Add(sd.Side);

            var storyDocs = ImportStoriesFromScenarioEditable(editable);
            foreach (var st in storyDocs)
                scenario.Stories.Add(st.Story);

            var doc = new ScenarioDocument
            {
                OriginalText = text,
                BlockSpan = span,
                EditableBlock = editable,
                Scenario = scenario
            };

            doc.SideDocuments.AddRange(sideDocs);
            doc.StoryDocuments.AddRange(storyDocs);
            doc.AssetReferences.AddRange(WccAssetReferenceCollector.Collect(doc));
            docs.Add(doc);
        }

        return docs;
    }

    private static List<ScenarioSideDocument> ImportSidesFromScenarioEditable(
        WmlEditableBlock scenarioEditable)
    {
        var spans = ScenarioSideEditor.FindLiteralSideSpans(scenarioEditable);
        var docs = new List<ScenarioSideDocument>();

        foreach (var span in spans)
        {
            var sideIsConditional = WmlPreprocessorScanner.FindEnclosingDirectChildRegion(
                scenarioEditable,
                span.StartLineIndex) is not null;
            var raw = string.Join(
                scenarioEditable.NewLine,
                scenarioEditable.Lines
                    .Skip(span.StartLineIndex)
                    .Take(span.EndLineIndexExclusive - span.StartLineIndex));
            var editable = WmlEditableBlockParser.Parse(
                new WmlBlock("side", raw),
                ScenarioSideSchema.KnownKeys);
            var states = AnalyzeFields(editable, ScenarioSideSchema.KnownKeys);
            var fieldAnalyses = WmlTopLevelFieldAnalyzer.AnalyzeMany(
                editable,
                ScenarioLeaderSchema.LegacyInlineKeys);

            var side = new ScenarioSide
            {
                SideNumber = ReadInt(states, "side"),
                Gold = ReadInt(states, "gold"),
                Income = ReadInt(states, "income"),
                VillageGold = ReadInt(states, "village_gold"),
                VillageSupport = ReadInt(states, "village_support"),
                RecallCost = ReadInt(states, "recall_cost"),
                Controller = Read(states, "controller"),
                TeamName = Read(states, "team_name"),
                UserTeamName = Read(states, "user_team_name"),
                SideNameWml = Read(states, "side_name"),
                Color = Read(states, "color"),
                Recruit = Read(states, "recruit"),
                Persistent = Read(states, "persistent"),
                SaveId = Read(states, "save_id"),
                PreviousSaveId = Read(states, "previous_save_id"),
                Fog = Read(states, "fog"),
                Shroud = Read(states, "shroud"),
                ShareVision = Read(states, "share_vision"),
                Hidden = Read(states, "hidden"),
                ScrollToLeader = Read(states, "scroll_to_leader"),
                SuppressEndTurnConfirmation = Read(states, "suppress_end_turn_confirmation"),
                DefeatCondition = Read(states, "defeat_condition"),
                NoLeader = Read(states, "no_leader"),
                RecruitmentPattern = Read(states, "recruitment_pattern")
            };
            foreach (var pair in states)
                side.FieldStates[pair.Key] = pair.Value;

            var sideDoc = new ScenarioSideDocument
            {
                Span = span,
                EditableBlock = editable,
                Side = side,
                IsConditional = sideIsConditional
            };

            if (ScenarioLeaderSchema.LegacyInlineKeys.Any(key =>
                    fieldAnalyses[key].Occurrences.Count > 0))
            {
                var legacy = ReadLeader(states);
                var storedLegacy = side.EnsureLegacyLeader();
                CopyLeaderValues(legacy, storedLegacy);
                CopyLeaderStates(states, storedLegacy);
                sideDoc.LeaderDocuments.Add(new ScenarioLeaderDocument
                {
                    SourceKind = ScenarioLeaderSourceKind.LegacyInline,
                    EditableBlock = editable,
                    Leader = storedLegacy,
                    IsConditional = sideIsConditional || ScenarioLeaderSchema.LegacyInlineKeys.Any(key =>
                        fieldAnalyses[key].Occurrences.Any(x => x.IsConditional))
                });
            }

            foreach (var leaderDoc in ImportNestedLeaders(editable, sideIsConditional))
            {
                side.NestedLeaders.Add(leaderDoc.Leader);
                sideDoc.LeaderDocuments.Add(leaderDoc);
            }

            docs.Add(sideDoc);
        }

        return docs;
    }

    private static IReadOnlyList<ScenarioLeaderDocument> ImportNestedLeaders(
        WmlEditableBlock sideEditable,
        bool containingSideIsConditional)
    {
        var result = new List<ScenarioLeaderDocument>();
        foreach (var span in ScenarioLeaderEditor.FindLiteralLeaderSpans(sideEditable))
        {
            var raw = string.Join(
                sideEditable.NewLine,
                sideEditable.Lines
                    .Skip(span.StartLineIndex)
                    .Take(span.EndLineIndexExclusive - span.StartLineIndex));
            result.Add(ImportLiteralLeader(
                raw,
                span,
                containingSideIsConditional || WmlPreprocessorScanner.FindEnclosingDirectChildRegion(
                    sideEditable,
                    span.StartLineIndex) is not null));
        }

        return result;
    }

    internal static ScenarioLeaderDocument ImportLiteralLeader(
        string raw,
        WmlLineBlockSpan span,
        bool isConditional,
        string? internalId = null)
    {
        var editable = WmlEditableBlockParser.Parse(
            new WmlBlock("leader", raw),
            ScenarioLeaderSchema.KnownKeys);
        var states = AnalyzeFields(editable, ScenarioLeaderSchema.KnownKeys);
        var leader = ReadLeader(states);
        CopyLeaderStates(states, leader);
        return new ScenarioLeaderDocument
        {
            InternalId = internalId ?? global::Wcc.Core.WccInternalId.New("leaderdoc"),
            SourceKind = ScenarioLeaderSourceKind.LiteralNested,
            Span = span,
            EditableBlock = editable,
            Leader = leader,
            IsConditional = isConditional
        };
    }

    private static Dictionary<string, WmlFieldSafetyState> AnalyzeFields(
        WmlEditableBlock editable,
        IEnumerable<string> keys)
        => WmlFieldSafetyAnalyzer.AnalyzeLiteralTopLevelFields(editable, keys)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

    private static ScenarioLeader ReadLeader(
        IReadOnlyDictionary<string, WmlFieldSafetyState> states)
    {
        var combinedState = states.GetValueOrDefault("x,y");
        var hasCombined = combinedState?.Kind is
            WmlFieldSafetyKind.PlainEditable or
            WmlFieldSafetyKind.ConditionalOrMultipleReadOnly;
        var hasSeparate = states.GetValueOrDefault("x")?.Kind is
                              WmlFieldSafetyKind.PlainEditable or
                              WmlFieldSafetyKind.ConditionalOrMultipleReadOnly ||
                          states.GetValueOrDefault("y")?.Kind is
                              WmlFieldSafetyKind.PlainEditable or
                              WmlFieldSafetyKind.ConditionalOrMultipleReadOnly;
        var (combinedX, combinedY) = ParseCoordinatePair(
            combinedState?.Kind == WmlFieldSafetyKind.PlainEditable
                ? combinedState.PlainValue
                : null);
        return new ScenarioLeader
        {
            Type = Read(states, "type"),
            Id = Read(states, "id"),
            NameWml = Read(states, "name"),
            Gender = Read(states, "gender"),
            Variation = Read(states, "variation"),
            CanRecruit = Read(states, "canrecruit"),
            X = ReadInt(states, "x") ?? combinedX,
            Y = ReadInt(states, "y") ?? combinedY,
            UsesCombinedCoordinates = hasCombined && !hasSeparate,
            CoordinatesAreAmbiguous = hasCombined && hasSeparate,
            Facing = Read(states, "facing"),
            Unrenamable = Read(states, "unrenamable"),
            RandomTraits = Read(states, "random_traits"),
            RandomGender = Read(states, "random_gender"),
            Placement = Read(states, "placement"),
            LocationId = Read(states, "location_id"),
            ExtraRecruit = Read(states, "extra_recruit")
        };
    }

    private static void CopyLeaderStates(
        IReadOnlyDictionary<string, WmlFieldSafetyState> source,
        ScenarioLeader target)
    {
        foreach (var key in ScenarioLeaderSchema.KnownKeys)
            if (source.TryGetValue(key, out var state))
                target.FieldStates[key] = state;

        if (target.CoordinatesAreAmbiguous)
        {
            target.FieldStates["x"] = CreateCoordinateConflictState("x");
            target.FieldStates["y"] = CreateCoordinateConflictState("y");
        }
        else if (target.UsesCombinedCoordinates && source.TryGetValue("x,y", out var combined))
        {
            target.FieldStates["x"] = CreateCoordinateProxyState("x", target.X, combined);
            target.FieldStates["y"] = CreateCoordinateProxyState("y", target.Y, combined);
        }
    }

    private static void CopyLeaderValues(ScenarioLeader source, ScenarioLeader target)
    {
        target.Type = source.Type;
        target.Id = source.Id;
        target.NameWml = source.NameWml;
        target.Gender = source.Gender;
        target.Variation = source.Variation;
        target.CanRecruit = source.CanRecruit;
        target.X = source.X;
        target.Y = source.Y;
        target.UsesCombinedCoordinates = source.UsesCombinedCoordinates;
        target.CoordinatesAreAmbiguous = source.CoordinatesAreAmbiguous;
        target.Facing = source.Facing;
        target.Unrenamable = source.Unrenamable;
        target.RandomTraits = source.RandomTraits;
        target.RandomGender = source.RandomGender;
        target.Placement = source.Placement;
        target.LocationId = source.LocationId;
        target.ExtraRecruit = source.ExtraRecruit;
    }

    private static string? Read(
        IReadOnlyDictionary<string, WmlFieldSafetyState> states,
        string key)
        => states.TryGetValue(key, out var state) &&
           state.Kind == WmlFieldSafetyKind.PlainEditable
            ? state.PlainValue
            : null;

    private static int? ReadInt(
        IReadOnlyDictionary<string, WmlFieldSafetyState> states,
        string key)
        => ParseInt(Read(states, key));

    private static (int? X, int? Y) ParseCoordinatePair(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, null);
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (ParseInt(parts[0]), ParseInt(parts[1]))
            : (null, null);
    }

    private static WmlFieldSafetyState CreateCoordinateProxyState(
        string key,
        int? value,
        WmlFieldSafetyState combined)
        => new()
        {
            Key = key,
            Kind = combined.Kind,
            PlainValue = value?.ToString(CultureInfo.InvariantCulture),
            Reason = combined.Reason,
            RawCodePreview = combined.RawCodePreview,
            RawEditRegion = combined.RawEditRegion
        };

    private static WmlFieldSafetyState CreateCoordinateConflictState(string key)
        => new()
        {
            Key = key,
            Kind = WmlFieldSafetyKind.ConditionalOrMultipleReadOnly,
            Reason = "Both x,y= and separate coordinate keys are present; edit the leader block as raw WML instead of guessing precedence."
        };

    private static List<ScenarioStoryDocument> ImportStoriesFromScenarioEditable(WmlEditableBlock scenarioEditable)
    {
        var spans = WmlDirectChildBlockScanner.FindSpans(scenarioEditable, "story");
        var docs = new List<ScenarioStoryDocument>();

        foreach (var sp in spans)
        {
            var raw = string.Join(
                scenarioEditable.NewLine,
                scenarioEditable.Lines.Skip(sp.StartLineIndex).Take(sp.EndLineIndexExclusive - sp.StartLineIndex));

            var block = new WmlBlock("story", raw);
            var editable = WmlEditableBlockParser.Parse(block, NoKnownKeys);

            var story = new ScenarioStory();
            var partDocs = ImportPartsFromStoryEditable(editable);
            foreach (var pd in partDocs)
                story.Parts.Add(pd.Part);

            var doc = new ScenarioStoryDocument
            {
                Span = sp,
                EditableBlock = editable,
                Story = story
            };

            doc.PartDocuments.AddRange(partDocs);
            docs.Add(doc);
        }

        return docs;
    }

    private static List<StoryPartDocument> ImportPartsFromStoryEditable(WmlEditableBlock storyEditable)
    {
        var spans = WmlDirectChildBlockScanner.FindSpans(storyEditable, "part");
        var docs = new List<StoryPartDocument>();

        foreach (var sp in spans)
        {
            var raw = string.Join(
                storyEditable.NewLine,
                storyEditable.Lines.Skip(sp.StartLineIndex).Take(sp.EndLineIndexExclusive - sp.StartLineIndex));

            var block = new WmlBlock("part", raw);
            var editable = WmlEditableBlockParser.Parse(block, StoryPartSchema.KnownKeys);

            var part = new StoryPart
            {
                StoryText = Get(editable, "story"),
                TextAlignment = Get(editable, "text_alignment"),
                TextLayout = Get(editable, "text_layout"),

                ShowTitle = Get(editable, "show_title"),
                Title = Get(editable, "title"),
                TitleAlignment = Get(editable, "title_alignment"),
                TitlePosition = Get(editable, "title_position"),

                Background = Get(editable, "background"),
                ScaleBackground = Get(editable, "scale_background"),
                Music = Get(editable, "music"),
                Sound = Get(editable, "sound"),
                Voice = Get(editable, "voice")
            };

            docs.Add(new StoryPartDocument
            {
                Span = sp,
                EditableBlock = editable,
                Part = part
            });
        }

        return docs;
    }

    private static string? Get(WmlEditableBlock b, string key)
        => b.Known.TryGetValue(key, out var e) ? e.Value : null;

    private static int? GetInt(WmlEditableBlock b, string key)
    {
        var s = Get(b, key);
        if (s is null) return null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static int? ParseInt(string? s)
    {
        if (s is null) return null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
