using System.Collections.Frozen;
using System.Globalization;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>
/// Source-bound composition for an explicitly owned initial playlist. Trusted
/// Core templates are recognized by full definition structure, never their name
/// alone. Unrelated event music and source are preserved verbatim.
/// </summary>
public static class ScenarioInitialMusicEditor
{
    public static IReadOnlySet<string> RecognizedMacroNames { get; } =
        new[] { "SCENARIO_MUSIC", "INTRO_AND_SCENARIO_MUSIC", "EXTRA_SCENARIO_MUSIC", "LET_INTRO_MUSIC_FINISH" }
            .ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> LiteralKeys =
        new[] { "name", "title", "append", "play_once", "immediate", "shuffle", "ms_before", "ms_after" }
            .ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> OtherMusicMacroNames =
        new[] { "DEFAULT_MUSIC_PLAYLIST", "DEFAULT_MUSIC_PLAYLIST_NO_EVENT", "FULL_MUSIC_PLAYLIST",
            "FULL_MUSIC_PLAYLIST_NO_EVENT", "INCIDENTAL_MUSIC", "APPEND_MUSIC", "REPLACE_SCENARIO_MUSIC" }
            .ToFrozenSet(StringComparer.Ordinal);
    private static readonly ScenarioInitialFieldDefinition NameDefinition = new("name", "Music file", "Music",
        ScenarioInitialFieldKind.Text, new(ScenarioInitialFieldDefaultKind.None, null, "No music file."), []);

    public static ScenarioInitialMusicProjection Project(string scenarioRaw,
        IEnumerable<WmlMacroExpansionDefinition>? definitions = null)
        => ProjectCore(scenarioRaw, definitions, null);

    public static ScenarioInitialMusicProjection Project(WmlStaticSourceRange range,
        WmlStaticScalarProjection capturedProjection, IEnumerable<WmlMacroExpansionDefinition>? definitions = null)
    {
        var context = ScenarioInitialSettingsCapturedContext.Create(range, capturedProjection);
        return ProjectCore(range.Text, definitions, context,
            context is null ? "The initial playlist needs a complete exact captured scenario evaluation." : null);
    }

    private static ScenarioInitialMusicProjection ProjectCore(string scenarioRaw,
        IEnumerable<WmlMacroExpansionDefinition>? definitions, ScenarioInitialSettingsCapturedContext? capturedContext,
        string? capturedProblem = null)
    {
        ArgumentNullException.ThrowIfNull(scenarioRaw);
        var supplied = (definitions ?? []).ToArray();
        var trusted = RecognizedMacroNames.Select(name => (Name: name, Definition: GetTrusted(name, supplied)))
            .Where(item => item.Definition is not null)
            .ToDictionary(item => item.Name, item => item.Definition!, StringComparer.Ordinal);
        var tracks = new List<ScenarioInitialMusicTrack>();
        var trackSources = new Dictionary<string, ScenarioInitialMusicTrackSource>(StringComparer.Ordinal);
        var eventSources = new List<ScenarioInitialMusicEventSource>();
        var entries = ScenarioContentSourceInventory.Build(scenarioRaw);
        var preserved = new List<ScenarioContentSourceEntry>();
        string Role(ScenarioContentSourceEntry entry)
        {
            if (RecognizedMacroNames.Contains(entry.Name)) return entry.Name;
            if (capturedContext?.Capture(entry)?.Invocation?.Definition is not { } active) return entry.Name;
            var matching = ScenarioInitialSettingsCapturedContext.FindActiveDefinition(supplied, entry.Name, active);
            if (matching is null || !MatchesTemplate(matching, "LET_INTRO_MUSIC_FINISH")) return entry.Name;
            preserved.Add(entry);
            return "LET_INTRO_MUSIC_FINISH";
        }
        var roles = entries.Where(entry => entry.Kind == ScenarioContentSourceKind.MacroInvocation)
            .ToDictionary(entry => entry.StartOffset, Role);
        string EntryRole(ScenarioContentSourceEntry entry) => roles.GetValueOrDefault(entry.StartOffset, entry.Name);
        var music = entries.Where(entry => IsPlaylistSource(entry) ||
            entry.Kind == ScenarioContentSourceKind.MacroInvocation && RecognizedMacroNames.Contains(EntryRole(entry))).ToArray();
        var structure = WmlAuthoredStructureScanner.Scan(scenarioRaw);
        var root = WmlProjectionTreeParser.Parse(structure.MaskedText).Roots.FirstOrDefault(node => node.TagName == "scenario");
        var insertion = root is not null ? structure.Lines[root.StartLine].EndOffsetExclusive : 0;
        var newline = scenarioRaw.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : scenarioRaw.Contains('\r') ? "\r" : "\n";
        var indent = music.Length != 0 ? LeadingIndent(scenarioRaw, music[0].StartOffset)
            : root is not null ? LeadingIndent(scenarioRaw, structure.Lines[root.StartLine].StartOffset) + "    " : "    ";
        string? problem = capturedProblem ?? WmlSingleBlockValidator.Validate(scenarioRaw, "scenario").Problem;
        var capturedUnrepresentedMusic = false;
        var intro = false;
        var finish = false;
        bool? shuffle = music.Length == 0 ? null : true;
        string? defaultIntro = null;
        var representation = music.Length == 0 ? ScenarioInitialMusicRepresentation.None :
            music.All(entry => entry.Kind == ScenarioContentSourceKind.ChildTag && entry.Name == "music")
                ? ScenarioInitialMusicRepresentation.Literal :
            music.All(entry => entry.Kind == ScenarioContentSourceKind.MacroInvocation && RecognizedMacroNames.Contains(EntryRole(entry)))
                ? ScenarioInitialMusicRepresentation.CoreMacros : ScenarioInitialMusicRepresentation.Unsupported;

        if (music.Any(entry => entry.IsConditional))
            problem ??= "The initial music source is conditional. Use its authored RAW source.";
        if (representation == ScenarioInitialMusicRepresentation.Unsupported)
            problem ??= "Mixed, amended or unsupported initial music source cannot be represented by one playlist. Use RAW source.";
        for (var index = 1; capturedContext is null && index < music.Length; index++)
            if (!OnlyWhitespaceAndComments(scenarioRaw[music[index - 1].EndOffsetExclusive..music[index].StartOffset]))
                problem ??= "The initial playlist is interleaved with other source. Use RAW to preserve its ordering and effects.";

        foreach (var entry in entries.Where(entry => entry.Kind == ScenarioContentSourceKind.MacroInvocation &&
                                                    !RecognizedMacroNames.Contains(EntryRole(entry))))
        {
            var capturedFragment = capturedContext?.Capture(entry);
            var hasCapturedMusic = capturedContext is not null && capturedFragment is not null &&
                TouchesCapturedInitialMusic(capturedContext, capturedFragment);
            capturedUnrepresentedMusic |= hasCapturedMusic;
            if (capturedContext is not null ? capturedFragment is null || hasCapturedMusic
                : OtherMusicMacroNames.Contains(entry.Name) || HasInitialMusicFootprint(entry.Name, supplied, new(StringComparer.Ordinal), 0))
                problem ??= "Another root macro supplies initial music or mixed prestart effects. Use its complete RAW source.";
        }

        var previousEventOwnedByMusic = false;
        foreach (var entry in entries)
        {
            if (entry.Kind == ScenarioContentSourceKind.MacroInvocation && RecognizedMacroNames.Contains(EntryRole(entry)))
                previousEventOwnedByMusic = true;
            else if (entry.Kind == ScenarioContentSourceKind.ChildTag && entry.Name == "event")
                previousEventOwnedByMusic = false;
            else if (previousEventOwnedByMusic && entry.Kind == ScenarioContentSourceKind.ChildTag && (entry.Name is "+event" or "-event"))
                problem ??= "An authored event amendment changes the music-generated handler. Preserve the complete source through RAW.";
        }

        if (representation == ScenarioInitialMusicRepresentation.CoreMacros)
        {
            var firstSeen = false;
            var extrasSeen = false;
            foreach (var entry in music)
            {
                var role = EntryRole(entry);
                if (!WmlMacroInvocationSyntaxScanner.TryParseSingle(entry.Text, out var call) || call is null)
                { problem ??= "A music invocation cannot be read exactly. Use RAW source."; continue; }
                WmlMacroExpansionDefinition? definition;
                if (capturedContext?.Capture(entry)?.Invocation?.Definition is { } active)
                {
                    var match = ScenarioInitialSettingsCapturedContext.FindActiveDefinition(supplied, entry.Name, active);
                    definition = match is not null && MatchesTemplate(match, role) ? match : null;
                    if (definition is not null && entry.Name == role) trusted[role] = definition;
                }
                else trusted.TryGetValue(entry.Name, out definition);
                if (definition is null)
                    problem ??= "A music macro has no single captured definition matching the exact Core template. Use RAW source.";
                if (capturedContext is not null && !CapturedMusicFragmentIsPure(capturedContext, entry, role, music))
                    problem ??= "A playlist invocation has mixed output, state changes or another captured event target. Use its complete RAW source.";
                var expected = role == "INTRO_AND_SCENARIO_MUSIC" ? 2 : role == "LET_INTRO_MUSIC_FINISH" ? 0 : 1;
                if (call.Arguments.Count != expected)
                { problem ??= "A music macro has unsupported arguments. Use RAW source."; continue; }
                if (role is "SCENARIO_MUSIC" or "INTRO_AND_SCENARIO_MUSIC")
                {
                    if (firstSeen || tracks.Count != 0) problem ??= "More than one initial music group is present. Use RAW source.";
                    firstSeen = true;
                    intro = role == "INTRO_AND_SCENARIO_MUSIC";
                    defaultIntro = intro ? null : "revelation.ogg";
                }
                else if (role == "LET_INTRO_MUSIC_FINISH")
                {
                    if (!firstSeen || extrasSeen || finish)
                        problem ??= "LET_INTRO_MUSIC_FINISH must immediately follow the main music invocation before extra tracks. Use RAW source.";
                    finish = true;
                }
                else
                {
                    if (!firstSeen) problem ??= "Extra music has no exact initial music owner. Use RAW source.";
                    extrasSeen = true;
                }
                for (var argument = 0; argument < call.Arguments.Count; argument++)
                {
                    var name = ReadTrackName(call.Arguments[argument].RawText);
                    if (name is null)
                    { problem ??= "A track name is dynamic or not a supported literal. Use RAW source."; continue; }
                    var id = $"music:{entry.StartOffset}:{argument}";
                    tracks.Add(new(name, id));
                    trackSources.Add(id, new(name, null, false, true));
                }
                if (definition is not null)
                    eventSources.Add(new(entry.Name, entry.Text, entry.Line - 1, definition));
            }
            if (!firstSeen) problem ??= "The playlist has no exact initial music owner. Use RAW source.";
        }
        else if (representation == ScenarioInitialMusicRepresentation.Literal)
        {
            foreach (var entry in music)
            {
                if (!TryReadLiteral(entry, out var values, out var rawProblem))
                { problem ??= rawProblem; continue; }
                var name = ReadTrackName(values["name"]);
                if (name is null)
                { problem ??= "A literal music filename is dynamic or unavailable. Use RAW source."; continue; }
                if (ReadBoolean(values.GetValueOrDefault("play_once"), false) != false)
                    problem ??= "Play-once music is not an initial playlist entry. Use RAW source.";
                var append = ReadBoolean(values.GetValueOrDefault("append"), false);
                if (append != (tracks.Count != 0))
                    problem ??= "Literal music resets or inherits another playlist; one ordered list cannot represent it safely. Use RAW source.";
                var trackShuffle = ReadBoolean(values.GetValueOrDefault("shuffle"), true)!.Value;
                var id = $"music:{entry.StartOffset}:0";
                tracks.Add(new(name, id));
                trackSources.Add(id, new(name, entry.Text,
                    values.Keys.Any(key => key is not ("name" or "append")), trackShuffle));
            }
            var flags = trackSources.Values.Select(track => track.Shuffle).Distinct().ToArray();
            shuffle = flags.Length == 1 ? flags[0] : null;
        }
        if (HasDuplicatePlaylistTracks(tracks, intro))
            problem ??= "The authored playlist contains duplicate track names. Use RAW source to preserve the engine's duplicate handling.";

        // A local shadow with a different body revokes trusted-name recognition.
        foreach (var local in WmlMacroDefinitionScanner.Scan(scenarioRaw))
            if (RecognizedMacroNames.Contains(local.Name) &&
                structure.Directives.Any(directive => directive.Name == "define" && directive.Line == local.StartLineIndex) &&
                !IsTrustedDefinition(new(local.Name, local.RawText)))
                problem ??= "A local music macro changes the Core template. Use RAW source.";

        return new(scenarioRaw, tracks, intro, finish, problem is null ? shuffle : null, defaultIntro, representation, problem,
            music, insertion, indent, newline, trackSources, trusted, problem is null ? eventSources : [])
        { CapturedContext = capturedContext, PreservedMusicEntries = Array.AsReadOnly(preserved.ToArray()),
            HasCapturedUnrepresentedMusic = capturedUnrepresentedMusic };
    }

    public static ScenarioInitialMusicComposeResult Compose(ScenarioInitialMusicProjection projection,
        IReadOnlyList<ScenarioInitialMusicTrack> tracks, bool makeFirstIntro, bool letFinish, bool? shuffle = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(tracks);
        if (!projection.CanEdit) return new(null, projection.Problem);
        if (projection.PreservedMusicEntries.Count != 0 && letFinish != projection.LetIntroFinish)
            return new(null, "The captured finish helper is preserved unchanged. Edit its authored source to change this playback plumbing.");
        if (shuffle is not null && shuffle != projection.Shuffle)
            return new(null, "Changing per-track shuffle behavior is not supported by this editor. Existing playback settings are preserved.");
        if (tracks.Any(track => track is null || ValidateTrackName(track.Name) is not null))
            return new(null, "Each track needs one literal music filename without variables, macro expressions or line breaks.");
        if (tracks.Count == 0 && (makeFirstIntro || letFinish) || makeFirstIntro && tracks.Count < 2)
            return new(null, "An intro needs a following scenario track, and finish timing needs an initial music track.");
        if (HasDuplicatePlaylistTracks(tracks, makeFirstIntro))
            return new(null, "The scenario playlist cannot contain the same track more than once.");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in tracks)
            if (track.SourceId is { } id && (!projection.TrackSources.ContainsKey(id) || !used.Add(id)))
                return new(null, "A playlist row no longer belongs to this exact captured source, or is used more than once.");

        if (tracks.Count == projection.Tracks.Count && makeFirstIntro == projection.HasIntro &&
            letFinish == projection.LetIntroFinish && tracks.SequenceEqual(projection.Tracks))
            return new(projection.SourceText, null);
        var useLiterals = (projection.Representation is ScenarioInitialMusicRepresentation.Literal or ScenarioInitialMusicRepresentation.None)
                          && !makeFirstIntro && !letFinish;
        string replacement;
        if (useLiterals)
            replacement = RenderLiterals(projection, tracks);
        else
        {
            if (projection.TrackSources.Values.Any(track => track.HasAncillaryAttributes))
                return new(null, "Literal track attributes cannot be transferred faithfully to intro macros. Keep them as literal music or use RAW source.");
            List<string> names = tracks.Count == 0 ? [] :
                [makeFirstIntro ? "INTRO_AND_SCENARIO_MUSIC" : "SCENARIO_MUSIC"];
            if (tracks.Count > (makeFirstIntro ? 2 : 1)) names.Add("EXTRA_SCENARIO_MUSIC");
            if (letFinish && projection.PreservedMusicEntries.Count == 0) names.Add("LET_INTRO_MUSIC_FINISH");
            if (names.Any(name => !projection.TrustedDefinitions.ContainsKey(name)))
                return new(null, "The required music macros do not have exact captured Core definitions. Use literal music or RAW source.");
            replacement = RenderMacros(projection, tracks, makeFirstIntro, letFinish);
        }

        if (projection.CapturedContext is not null)
            return ComposeCaptured(projection, tracks, makeFirstIntro, letFinish, useLiterals);
        var candidate = ReplaceOwnedSource(projection, replacement);
        var validation = WmlSingleBlockValidator.Validate(candidate, "scenario");
        if (!validation.IsValid) return new(null, validation.Problem);
        var patches = projection.SourceEntries.Count == 0
            ? replacement.Length == 0 ? Array.Empty<WccScenarioFieldSourcePatch>()
                : [new WccScenarioFieldSourcePatch(projection.InsertionOffset, 0, replacement + projection.Newline)]
            : projection.SourceEntries.Select((entry, index) => new WccScenarioFieldSourcePatch(
                entry.StartOffset, entry.EndOffsetExclusive - entry.StartOffset,
                index != 0 ? "" : replacement.Length != 0 && entry.Kind == ScenarioContentSourceKind.MacroInvocation
                    ? replacement[projection.Indent.Length..] : replacement)).ToArray();
        return new(candidate, null) { SourcePatches = Array.AsReadOnly(patches) };
    }

    private static bool IsPlaylistSource(ScenarioContentSourceEntry entry)
        => entry.Kind == ScenarioContentSourceKind.ChildTag && (entry.Name is "music" or "+music" or "-music") ||
           entry.Kind == ScenarioContentSourceKind.MacroInvocation && RecognizedMacroNames.Contains(entry.Name);

    private static bool TouchesCapturedInitialMusic(ScenarioInitialSettingsCapturedContext context,
        ScenarioInitialSettingsCapturedContext.CapturedFragment fragment) =>
        context.Scenario.DirectChildren.Any(owner => owner.TagName == "music" && fragment.Touches(owner) ||
            owner.TagName == "event" && owner.DirectChildren.Any(child => child.TagName == "music") &&
            (owner.ResolveField("name") is not { State: WmlStaticScalarResolutionState.Known, ValueText: { } name } ||
                !WccScenarioFieldValueCodec.IsSupportedLiteralWml(name) ||
                WccScenarioFieldValueCodec.DecodeLiteral(name).Split(',', StringSplitOptions.TrimEntries)
                    .Contains("prestart", StringComparer.Ordinal)) &&
            (owner.DirectChildren.Where(child => child.TagName == "music").Any(fragment.Touches) ||
                owner.Fields.Values.SelectMany(field => field.Contributions).Any(fragment.Touches)));

    private static bool CapturedMusicFragmentIsPure(ScenarioInitialSettingsCapturedContext context,
        ScenarioContentSourceEntry entry, string role, IReadOnlyList<ScenarioContentSourceEntry> entries)
    {
        if (context.Capture(entry) is not { HasStatefulEffects: false } fragment || entry.IsConditional) return false;
        var mainEntry = entries.FirstOrDefault(candidate => candidate.Name is "SCENARIO_MUSIC" or "INTRO_AND_SCENARIO_MUSIC");
        if (mainEntry is null || context.Capture(mainEntry) is not { } main) return false;
        var events = context.Scenario.DirectChildren.Where(owner => owner.TagName == "event" &&
            owner.OpeningOutputOffset is { } offset && main.Contains(offset)).Take(2).ToArray();
        if (events.Length != 1 || WccScenarioFieldValueCodec.DecodeLiteral(events[0].GetKnownValue("name") ?? "") != "prestart") return false;
        var initialEvent = events[0];
        if (context.Scenario.Fields.Values.SelectMany(field => field.Contributions).Any(fragment.Touches)) return false;
        foreach (var owner in context.Projection.Owners.Where(fragment.Touches))
        {
            if (ReferenceEquals(owner, context.Scenario)) continue;
            var top = owner;
            while (top.Parent is not null && !ReferenceEquals(top.Parent, context.Scenario)) top = top.Parent;
            if (!ReferenceEquals(top.Parent, context.Scenario) ||
                !(top.TagName == "music" && ReferenceEquals(top, owner) || ReferenceEquals(top, initialEvent)) ||
                owner.TagName is not ("music" or "event") ||
                owner.TagName == "event" && owner.Fields.Keys.Any(key => key != "name") ||
                owner.TagName == "music" && owner.Fields.Keys.Any(key => !LiteralKeys.Contains(key))) return false;
        }
        if (role == "LET_INTRO_MUSIC_FINISH")
        {
            var first = initialEvent.DirectChildren.FirstOrDefault(owner => owner.TagName == "music");
            return first is not null && first.OpeningOutputOffset is { } opening && main.Contains(opening) &&
                WccScenarioFieldValueCodec.DecodeLiteral(first.GetKnownValue("immediate") ?? "") == "no" &&
                context.Projection.Owners.SelectMany(owner => owner.Fields.SelectMany(pair => pair.Value.Contributions
                    .Where(fragment.Touches).Select(contribution => (Owner: owner, Key: pair.Key))))
                    .SequenceEqual(new[] { (Owner: first, Key: "immediate") });
        }
        return role != "EXTRA_SCENARIO_MUSIC" || initialEvent.DirectChildren.Count(owner =>
            owner.TagName == "music" && owner.OpeningOutputOffset is { } opening && fragment.Contains(opening)) == 1;
    }

    /// <summary>Paired review may normalize this unchanged plumbing only after
    /// checking the fresh before/after projection, exact call and definition.</summary>
    internal static bool HasVerifiedPreservedMusicEntries(ScenarioInitialMusicProjection projection) =>
        projection.CanEdit && projection.CapturedContext is { } context && projection.PreservedMusicEntries.All(entry =>
            CapturedMusicFragmentIsPure(context, entry, "LET_INTRO_MUSIC_FINISH", projection.SourceEntries));

    private static ScenarioInitialMusicComposeResult ComposeCaptured(ScenarioInitialMusicProjection projection,
        IReadOnlyList<ScenarioInitialMusicTrack> tracks, bool intro, bool finish, bool literals)
    {
        var pieces = new List<(string Role, string Text)>();
        string File(int index) => WccScenarioFieldValueCodec.EncodeLiteral(NameDefinition, tracks[index].Name);
        if (literals) pieces.AddRange(RenderLiteralBlocks(projection, tracks).Select(text => ("literal", text)));
        else if (tracks.Count != 0)
        {
            pieces.Add(("main", projection.Indent + (intro ? "{INTRO_AND_SCENARIO_MUSIC " + File(0) + " " + File(1) + "}"
                : "{SCENARIO_MUSIC " + File(0) + "}")));
            if (finish && projection.PreservedMusicEntries.Count == 0) pieces.Add(("finish", projection.Indent + "{LET_INTRO_MUSIC_FINISH}"));
            for (var index = intro ? 2 : 1; index < tracks.Count; index++)
                pieces.Add(("extra", projection.Indent + "{EXTRA_SCENARIO_MUSIC " + File(index) + "}"));
        }
        if (tracks.Count == 0 && projection.PreservedMusicEntries.Count != 0)
            return new(null, "The playlist owns unchanged finish plumbing. Keep its main track or edit the complete source in RAW.");
        var patches = new List<WccScenarioFieldSourcePatch>();
        var mainAnchor = projection.SourceEntries.FirstOrDefault(entry => entry.Name is "SCENARIO_MUSIC" or "INTRO_AND_SCENARIO_MUSIC");
        foreach (var entry in projection.SourceEntries)
        {
            if (projection.PreservedMusicEntries.Contains(entry)) continue;
            var role = entry.Kind == ScenarioContentSourceKind.ChildTag ? "literal" : entry.Name switch
            { "SCENARIO_MUSIC" or "INTRO_AND_SCENARIO_MUSIC" => "main", "LET_INTRO_MUSIC_FINISH" => "finish", _ => "extra" };
            var position = pieces.FindIndex(piece => piece.Role == role);
            var text = position < 0 ? "" : pieces[position].Text;
            if (position >= 0) pieces.RemoveAt(position);
            if (text.Length != 0 && entry.Kind == ScenarioContentSourceKind.MacroInvocation) text = text[projection.Indent.Length..];
            patches.Add(new(entry.StartOffset, entry.EndOffsetExclusive - entry.StartOffset, text));
        }
        foreach (var piece in pieces)
        {
            var anchor = piece.Role == "finish" ? mainAnchor : projection.SourceEntries.LastOrDefault();
            if (anchor is null)
                patches.Add(new(projection.InsertionOffset, 0, piece.Text + projection.Newline));
            else patches.Add(new(anchor.EndOffsetExclusive, 0, projection.Newline + piece.Text));
        }
        var ordered = patches.GroupBy(patch => (patch.Start, patch.Length)).OrderBy(group => group.Key.Start)
            .ThenBy(group => group.Key.Length).Select(group => new WccScenarioFieldSourcePatch(group.Key.Start,
                group.Key.Length, string.Concat(group.Select(patch => patch.ReplacementText)))).ToArray();
        var builder = new System.Text.StringBuilder();
        var cursor = 0;
        foreach (var patch in ordered)
        {
            if (patch.Start < cursor) return new(null, "The playlist source anchors overlap; preserve the complete source in RAW.");
            builder.Append(projection.SourceText, cursor, patch.Start - cursor).Append(patch.ReplacementText);
            cursor = patch.Start + patch.Length;
        }
        builder.Append(projection.SourceText, cursor, projection.SourceText.Length - cursor);
        var candidate = builder.ToString();
        var validation = WmlSingleBlockValidator.Validate(candidate, "scenario");
        return validation.IsValid ? new(candidate, null) { SourcePatches = Array.AsReadOnly(ordered) }
            : new(null, validation.Problem);
    }

    private static WmlMacroExpansionDefinition? GetTrusted(string name, IReadOnlyList<WmlMacroExpansionDefinition> definitions)
    {
        var matches = definitions.Where(definition => definition.Name == name).ToArray();
        return matches.Length == 1 && IsTrustedDefinition(matches[0]) ? matches[0] : null;
    }

    private static bool IsTrustedDefinition(WmlMacroExpansionDefinition definition)
        => MatchesTemplate(definition, definition.Name);

    private static bool MatchesTemplate(WmlMacroExpansionDefinition definition, string templateName)
    {
        var lines = definition.RawText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var header = Array.FindIndex(lines, line => line.TrimStart().StartsWith("#define", StringComparison.Ordinal));
        var end = Array.FindLastIndex(lines, line => line.Trim() == "#enddef");
        if (header < 0 || end <= header || !OnlyWhitespaceAndComments(string.Join("\n", lines.Take(header))) ||
            !OnlyWhitespaceAndComments(string.Join("\n", lines.Skip(end + 1)))) return false;
        var headerTokens = lines[header].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (headerTokens.Length < 2 || headerTokens[0] != "#define" || headerTokens[1] != definition.Name) return false;
        if (!WmlMacroDefinitionTemplate.TryParse(definition, out var template, out _) || template is null ||
            template.OptionalParameters.Count != 0) return false;
        var expected = ExpectedTemplate(templateName);
        return expected is not null && template.Parameters.SequenceEqual(expected.Value.Parameters) &&
               NormalizeCode(template.BodyLines).SequenceEqual(NormalizeCode(expected.Value.Body.Split('\n')));
    }

    private static (string[] Parameters, string Body)? ExpectedTemplate(string name) => name switch
    {
        "SCENARIO_MUSIC" => (["MUSIC"], "[music]\nname=\"revelation.ogg\"\nms_after=2000\n[/music]\n[event]\nname=prestart\n[music]\nname={MUSIC}\nimmediate=yes\n[/music]\n[/event]"),
        "INTRO_AND_SCENARIO_MUSIC" => (["INTRO_MUSIC", "SCENARIO_MUSIC"], "[music]\nname={INTRO_MUSIC}\nimmediate=yes\nms_after=2000\n[/music]\n[event]\nname=prestart\n[music]\nname={SCENARIO_MUSIC}\nimmediate=yes\nappend=no\n[/music]\n[/event]"),
        "EXTRA_SCENARIO_MUSIC" => (["MUSIC"], "[+event]\n[music]\nname={MUSIC}\nappend=yes\n[/music]\n[/event]"),
        "LET_INTRO_MUSIC_FINISH" => ([], "[+event]\n[+music]\nimmediate=no\n[/music]\n[/event]"),
        _ => null
    };

    private static IEnumerable<string> NormalizeCode(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var kind = WmlTopLevelCodeScanner.ClassifyTrimmedLine(line.Trim());
            if (kind is not (WmlTopLevelCodeKind.None or WmlTopLevelCodeKind.OtherHashDirective) || IsPreprocessorDirective(line))
            { yield return "!directive:" + line.Trim(); continue; }
            var code = WmlInlineCommentSplitter.Split(line).Value.Trim();
            if (code.Length == 0) continue;
            var equals = code.IndexOf('=');
            yield return equals > 0 ? code[..equals].Trim() + "=" + code[(equals + 1)..].Trim() : code;
        }
    }

    private static bool HasInitialMusicFootprint(string name, IReadOnlyList<WmlMacroExpansionDefinition> definitions,
        HashSet<string> visited, int depth)
    {
        if (depth > 12 || !visited.Add(name)) return false;
        foreach (var definition in definitions.Where(definition => definition.Name == name))
        {
            if (!WmlMacroDefinitionTemplate.TryParse(definition, out var template, out _) || template is null) continue;
            var body = string.Join("\n", template.BodyLines);
            var tree = WmlProjectionTreeParser.Parse(WmlAuthoredStructureScanner.Scan(body).MaskedText);
            if (tree.Roots.Any(root => root.TagName == "music" || (root.SourceLines[root.StartLine].Trim() is "[+event]" or "[-event]") ||
                root.TagName == "event" && MayBePrestart(root, body) && ContainsImmediateMusic(root))) return true;
            // Only standalone root calls supply this initial source. Calls inside
            // ordinary event handlers remain owned by those handlers.
            foreach (var invocation in ScenarioContentSourceInventory.Build("[scenario]\n" + body + "\n[/scenario]")
                         .Where(entry => entry.Kind == ScenarioContentSourceKind.MacroInvocation))
                if (RecognizedMacroNames.Contains(invocation.Name) ||
                    HasInitialMusicFootprint(invocation.Name, definitions, visited, depth + 1)) return true;
        }
        return false;
    }

    private static bool MayBePrestart(WmlProjectionNode root, string body)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var raw = string.Join("\n", lines.Skip(root.StartLine).Take(root.EndLineExclusive - root.StartLine));
        var block = WmlEditableBlockParser.Parse(new("event", raw), new HashSet<string>(["name"], StringComparer.Ordinal));
        if (!block.Known.TryGetValue("name", out var name) || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(name.Value)) return true;
        return WccScenarioFieldValueCodec.DecodeLiteral(name.Value).Split(',').Any(trigger => trigger.Trim() == "prestart");
    }

    private static bool ContainsImmediateMusic(WmlProjectionNode node)
        => node.Children.Any(child => child.TagName == "music" || child.TagName != "event" && ContainsImmediateMusic(child));

    private static bool TryReadLiteral(ScenarioContentSourceEntry entry, out Dictionary<string, string> values, out string? problem)
    {
        values = new(StringComparer.Ordinal);
        problem = null;
        var structure = WmlAuthoredStructureScanner.Scan(entry.Text);
        var tree = WmlProjectionTreeParser.Parse(structure.MaskedText);
        if (structure.Problem is not null || structure.Directives.Count != 0 || structure.Macros.Count != 0 ||
            tree.Roots.Count != 1 || tree.Roots[0].Children.Count != 0)
        { problem = "Literal music contains nested or unresolved source. Use RAW source."; return false; }
        var lines = entry.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 1; index < lines.Length - 1; index++)
        {
            var code = WmlInlineCommentSplitter.Split(lines[index]).Value.Trim();
            if (code.Length == 0) continue;
            var equals = code.IndexOf('=');
            if (equals <= 0 || !LiteralKeys.Contains(code[..equals].Trim()) ||
                !values.TryAdd(code[..equals].Trim(), code[(equals + 1)..].Trim()) ||
                code.IndexOfAny(['{', '}', '$']) >= 0)
            { problem = "Literal music has unsupported, repeated or dynamic attributes. Use RAW source."; return false; }
        }
        var capturedValues = values;
        if (!capturedValues.ContainsKey("name") ||
            new[] { "append", "play_once", "immediate", "shuffle" }.Any(key => capturedValues.TryGetValue(key, out var value) && ReadBoolean(value, false) is null) ||
            new[] { "ms_before", "ms_after" }.Any(key => capturedValues.TryGetValue(key, out var value) &&
                (!WccScenarioFieldValueCodec.IsSupportedLiteralWml(value) || IsTranslated(value) ||
                 !int.TryParse(WccScenarioFieldValueCodec.DecodeLiteral(value), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))))
        { problem = "Literal music attributes cannot be resolved as supported scalar values. Use RAW source."; return false; }
        return true;
    }

    private static bool? ReadBoolean(string? raw, bool absent)
        => raw is null ? absent : !WccScenarioFieldValueCodec.IsSupportedLiteralWml(raw) || IsTranslated(raw) ? null : WccScenarioFieldValueCodec.DecodeLiteral(raw).Trim() switch
        { "yes" or "true" => true, "no" or "false" or "" => false, _ => null };

    private static string? ReadTrackName(string? raw)
    {
        if (raw is null) return null;
        raw = raw.Trim();
        if (raw.StartsWith('(') && raw.EndsWith(')')) raw = raw[1..^1].Trim();
        if (IsTranslated(raw) || !WccScenarioFieldValueCodec.IsSupportedLiteralWml(raw)) return null;
        var name = WccScenarioFieldValueCodec.DecodeLiteral(raw);
        return name is not null && ValidateTrackName(name) is null ? name : null;
    }

    private static bool IsTranslated(string raw)
        => raw.TrimStart().StartsWith('_') && raw.TrimStart()[1..].TrimStart().StartsWith('"');

    private static string? ValidateTrackName(string name)
        => string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['{', '}', '$', '\r', '\n']) >= 0
            ? "A literal music filename is required." : WccScenarioFieldValueCodec.ValidateInput(NameDefinition, name);

    private static bool HasDuplicatePlaylistTracks(IEnumerable<ScenarioInitialMusicTrack> tracks, bool intro)
    {
        var names = tracks.Skip(intro ? 1 : 0).Select(track => track.Name).ToArray();
        return names.Distinct(StringComparer.Ordinal).Count() != names.Length;
    }

    private static string RenderMacros(ScenarioInitialMusicProjection projection, IReadOnlyList<ScenarioInitialMusicTrack> tracks,
        bool intro, bool finish)
    {
        if (tracks.Count == 0) return "";
        var lines = new List<string>();
        string File(int index) => WccScenarioFieldValueCodec.EncodeLiteral(NameDefinition, tracks[index].Name);
        lines.Add(projection.Indent + (intro ? "{INTRO_AND_SCENARIO_MUSIC " + File(0) + " " + File(1) + "}"
            : "{SCENARIO_MUSIC " + File(0) + "}"));
        if (finish) lines.Add(projection.Indent + "{LET_INTRO_MUSIC_FINISH}");
        for (var index = intro ? 2 : 1; index < tracks.Count; index++)
            lines.Add(projection.Indent + "{EXTRA_SCENARIO_MUSIC " + File(index) + "}");
        return string.Join(projection.Newline, lines);
    }

    private static string RenderLiterals(ScenarioInitialMusicProjection projection, IReadOnlyList<ScenarioInitialMusicTrack> tracks)
        => string.Join(projection.Newline, RenderLiteralBlocks(projection, tracks));

    private static IReadOnlyList<string> RenderLiteralBlocks(ScenarioInitialMusicProjection projection, IReadOnlyList<ScenarioInitialMusicTrack> tracks)
    {
        var blocks = new List<string>();
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            var raw = track.SourceId is { } id ? projection.TrackSources[id].LiteralRaw : null;
            raw ??= projection.Indent + "[music]" + projection.Newline + projection.Indent + "    name=" +
                    WccScenarioFieldValueCodec.EncodeLiteral(NameDefinition, track.Name) + projection.Newline + projection.Indent + "[/music]";
            if (ReadTrackName(FindLiteralValue(raw, "name")) != track.Name)
                raw = SetLiteralValue(raw, "name", WccScenarioFieldValueCodec.EncodeLiteral(NameDefinition, track.Name), projection.Newline);
            if (FindLiteralValue(raw, "append") is { } append)
            {
                if (ReadBoolean(append, false) != (index != 0)) raw = SetLiteralValue(raw, "append", index != 0 ? "yes" : "no", projection.Newline);
            }
            else if (index != 0) raw = SetLiteralValue(raw, "append", "yes", projection.Newline);
            blocks.Add(raw);
        }
        return blocks;
    }

    private static string? FindLiteralValue(string raw, string key)
    {
        foreach (var line in WmlAuthoredStructureScanner.Scan(raw).Lines)
        {
            var text = raw[line.StartOffset..line.ContentEndOffset];
            var equals = text.IndexOf('=');
            if (equals > 0 && text[..equals].Trim() == key)
                return WmlInlineCommentSplitter.Split(text[(equals + 1)..]).Value;
        }
        return null;
    }

    private static string SetLiteralValue(string raw, string key, string value, string newline)
    {
        var lines = WmlAuthoredStructureScanner.Scan(raw).Lines;
        foreach (var line in lines)
        {
            var text = raw[line.StartOffset..line.ContentEndOffset];
            var equals = text.IndexOf('=');
            if (equals <= 0 || text[..equals].Trim() != key) continue;
            var afterEquals = text[(equals + 1)..];
            var valueStart = line.StartOffset + equals + 1 + afterEquals.Length - afterEquals.TrimStart().Length;
            var oldValue = WmlInlineCommentSplitter.Split(afterEquals).Value;
            return raw[..valueStart] + value + raw[(valueStart + oldValue.Length)..];
        }
        var closing = lines.Last(line => raw[line.StartOffset..line.ContentEndOffset].TrimStart().StartsWith("[/music]", StringComparison.Ordinal));
        var firstField = lines.FirstOrDefault(line => raw[line.StartOffset..line.ContentEndOffset].Contains('='));
        var indent = firstField.ContentEndOffset > firstField.StartOffset ? LeadingIndent(raw, firstField.StartOffset)
            : LeadingIndent(raw, closing.StartOffset) + "    ";
        return raw[..closing.StartOffset] + indent + key + "=" + value + newline + raw[closing.StartOffset..];
    }

    private static string ReplaceOwnedSource(ScenarioInitialMusicProjection projection, string replacement)
    {
        var source = projection.SourceText;
        if (projection.SourceEntries.Count == 0)
            return replacement.Length == 0 ? source : source[..projection.InsertionOffset] + replacement + projection.Newline + source[projection.InsertionOffset..];
        for (var index = projection.SourceEntries.Count - 1; index >= 0; index--)
        {
            var entry = projection.SourceEntries[index];
            var text = index == 0 ? replacement : "";
            if (index == 0 && text.Length != 0 && entry.Kind == ScenarioContentSourceKind.MacroInvocation)
                text = text[projection.Indent.Length..]; // Original leading indentation is outside the brace span.
            source = source[..entry.StartOffset] + text + source[entry.EndOffsetExclusive..];
        }
        return source;
    }

    private static bool OnlyWhitespaceAndComments(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').All(line =>
            string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#') &&
            !IsPreprocessorDirective(line));

    private static bool IsPreprocessorDirective(string line)
    {
        var text = line.AsSpan().TrimStart();
        if (text.IsEmpty || text[0] != '#') return false;
        var end = 1;
        while (end < text.Length && char.IsLetter(text[end])) end++;
        if (end < text.Length && !char.IsWhiteSpace(text[end])) return false;
        var name = text[1..end].ToString().ToLowerInvariant();
        return name is "define" or "enddef" or "arg" or "endarg" or "undef" or "textdomain" or
            "if" or "ifdef" or "ifndef" or "ifhave" or "ifnhave" or "ifver" or "ifnver" or
            "else" or "endif" or "error" or "warning" or "deprecated";
    }

    private static string LeadingIndent(string source, int offset)
    {
        var start = offset;
        while (start > 0 && source[start - 1] is not ('\r' or '\n')) start--;
        var end = start;
        while (end < source.Length && source[end] is ' ' or '\t') end++;
        return source[start..end];
    }
}
