using Wcc.Core.Scenarios;

namespace Wcc.Core.Wml.Checking;

/// <summary>
/// One Resources module for recognized literal WML file references against immutable captured routes.
/// Uses the existing ordered evaluation, scalar projection and source resolver;
/// no filesystem reads, audio decoding or game-validity proof are performed.
/// </summary>
public sealed class WmlMusicResourceCheckModule(WmlCheckReferenceContext? context = null) : IWmlCheckModule
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public WmlCheckModuleId Id => WmlCheckModuleId.Resources;

    public void Check(IReadOnlyList<WmlCheckDocument> documents, ICollection<WmlCheckDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(diagnostics);
        cancellationToken.ThrowIfCancellationRequested();
        if (documents.Count == 0) return;
        if (context?.CampaignContext is not { ResourceResolver: { } resolver } campaign)
        {
            foreach (var document in documents)
                diagnostics.Add(Finding("resources.context-unavailable", WmlCheckSeverity.Information,
                    document.Locate(0), "Resource file checks need captured campaign context.",
                    "Select a campaign main file and any required dependency folders. Resource filenames and active binary paths " +
                    "have not been evaluated. This module does not read live files or infer package roots."));
            return;
        }

        var captured = campaign.Sources.GroupBy(source => KeyPath(source.Path), PathComparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), PathComparer);
        var selected = documents.GroupBy(document => KeyPath(document.Source.Path), PathComparer)
            .Where(group => group.Count() == 1 && captured.TryGetValue(group.Key, out var matches) &&
                matches.Length == 1 && matches[0].Text == group.First().Source.Text)
            .ToDictionary(group => group.Key, group => group.First(), PathComparer);
        foreach (var document in documents.Where(document => !selected.ContainsKey(KeyPath(document.Source.Path))))
            diagnostics.Add(Finding("resources.source-unmatched", WmlCheckSeverity.Information,
                document.Locate(0), "This checked source has no unique matching campaign capture.",
                "Resource checks for this file were deferred. Its exact captured source text and ordered include context must match before resource evidence is applied."));

        var supportLocations = new Dictionary<string, WmlCheckDocument>(PathComparer);
        var findings = new Dictionary<FindingKey, FindingGroup>();
        var deferred = new Dictionary<(string Path, string Kind, string Site), DeferredGroup>();
        var visits = new HashSet<string>(PathComparer);
        var skipped = new Dictionary<string, List<string>>(PathComparer);
        var lookupCache = new Dictionary<string, WmlStaticIncludeResolution>(StringComparer.Ordinal);
        foreach (var evaluation in campaign.Evaluations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reached = evaluation.Evaluation.Sources.Where(source => Matches(source)).Select(source => KeyPath(source.Path))
                .Where(selected.ContainsKey).Distinct(PathComparer).ToArray();
            foreach (var path in reached) visits.Add(path);
            // Includes retain their ordered evaluator evidence, including a known missing
            // file which itself made an environment partial. No second include evaluation.
            foreach (var include in evaluation.Evaluation.Includes)
            {
                var kind = include.IncludePath.EndsWith(".lua", StringComparison.Ordinal) ? "lua"
                    : include.IncludePath.EndsWith(".map", StringComparison.Ordinal) || include.IncludePath.EndsWith(".mask", StringComparison.Ordinal) ? "map" : null;
                if (kind is null || !TryOriginsLocation(include.InvocationOrigins, out var includeLocation, out var includeCaller, out var includeRelated)) continue;
                CheckFilename(kind, kind == "lua" ? "Lua file" : "Map", include.IncludePath, null, [], false,
                    includeLocation!, includeCaller!, includeRelated, evaluation.Label, false);
            }
            if (!evaluation.Evaluation.IsComplete)
            {
                foreach (var path in reached)
                {
                    if (!skipped.TryGetValue(path, out var labels)) skipped.Add(path, labels = []);
                    labels.Add(evaluation.Label);
                }
                continue;
            }

            var projection = WmlStaticScalarProjection.Create(evaluation.Evaluation, cancellationToken);
            var resources = WmlCheckResourceProjection.Create(evaluation.Evaluation, projection,
                tag => tag == "binary_path" || WmlResourceFieldCatalog.IsEligibleOwner(tag), cancellationToken);
            var valueOrigins = new WmlCheckResourceValueOrigins(evaluation.Evaluation, cancellationToken);
            var composites = new Lazy<WmlCheckResourceCompositeAssignments>(() =>
                new(evaluation.Evaluation, cancellationToken));
            var binaryPaths = new List<string> { "core" };
            var unresolvedBinaryPaths = resources.HasUncertainBinaryPathInventory;
            foreach (var resource in resources.Owners.Where(resource => resource.OriginalOwner.Parent is null &&
                resource.OriginalOwner.TagName == "binary_path"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var owner = resource.ValueOwner;
                var path = owner.ResolveField("path");
                if (resource.HasProvenTopLevelExtent && path.State == WmlStaticScalarResolutionState.Known && TryLiteral(path.ValueText, out var literal) &&
                    TryBinaryMount(literal, out var mount))
                    binaryPaths.Add(mount);
                else unresolvedBinaryPaths = true;
            }
            // Deterministic enumeration for captured presence only; this does not
            // model the engine's conflict handling or establish lookup precedence.
            var routes = binaryPaths.AsEnumerable().Reverse().Distinct(StringComparer.Ordinal).ToArray();
            foreach (var resource in resources.Owners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var owner = resource.OriginalOwner;
                foreach (var key in owner.Fields.Keys)
                {
                    var fieldKind = WmlResourceFieldCatalog.Find(owner, key);
                    if (fieldKind is null) continue;
                    var field = owner.Fields[key];
                    var (kind, label, folder) = fieldKind.Value switch
                    {
                        WmlResourceFieldKind.Music => ("music", "Music", "music"),
                        WmlResourceFieldKind.Sound => ("sound", "Sound", "sounds"),
                        WmlResourceFieldKind.Map => ("map", "Map", "maps"),
                        WmlResourceFieldKind.Lua => ("lua", "Lua file", (string?)null),
                        _ => ("image", "Image", "images")
                    };
                    // Multiple assignment remains unresolved in the strict scalar
                    // projection. Its direct, paired, exactly mapped components
                    // are independently observable by this report-only adapter.
                    var composite = field.ValueText is null ? composites.Value.Observe(owner, key, cancellationToken) : null;
                    if (composite is not null) field = composite;
                    var contribution = field.Contributions.LastOrDefault();
                    if (!TrySelectedLocation(owner, field, contribution, out var location, out var caller, out var related)) continue;
                    var valueOwner = resource.ValueOwner;
                    if (composite is null && !valueOwner.Fields.TryGetValue(key, out field)) continue;
                    contribution = field.Contributions.LastOrDefault();
                    if (!TrySelectedLocation(valueOwner, field, contribution, out location, out caller, out related)) continue;
                    var value = valueOwner.ResolveField(key);
                    var boundedMappedValue = valueOwner.OpeningOutputOffset.HasValue && valueOwner.ClosingOutputOffset.HasValue &&
                        contribution is { ValueMappingIsExact: true };
                    boundedMappedValue |= resource.CanObserveLiteralField(key);
                    var rawLua = field.ValueText?.Trim() ?? "";
                    var boundedLuaOwner = valueOwner.OpeningOutputOffset.HasValue && valueOwner.ClosingOutputOffset.HasValue && !valueOwner.IsAmended;
                    var boundedLuaPayload = boundedLuaOwner && rawLua.StartsWith("<<", StringComparison.Ordinal) &&
                        rawLua.EndsWith(">>", StringComparison.Ordinal) && rawLua.IndexOf(">>", StringComparison.Ordinal) == rawLua.Length - 2;
                    var quotedLua = boundedLuaOwner && rawLua.StartsWith('"') && rawLua.EndsWith('"')
                        ? WccScenarioFieldValueCodec.DecodeLiteral(rawLua) : null;
                    if (quotedLua == rawLua) quotedLua = null;
                    // Raw Lua is intentionally not a known scalar in the shared writer projection.
                    // A complete mapped payload in a paired owner permits lexical path observation only.
                    if (fieldKind == WmlResourceFieldKind.Lua && (value.State == WmlStaticScalarResolutionState.Known || boundedLuaPayload || quotedLua is not null))
                    {
                        foreach (var lua in WmlLuaFileReferenceScanner.Find(quotedLua ?? field.ValueText ?? "", cancellationToken))
                        {
                            var luaLocation = location!;
                            if (quotedLua is null && contribution?.ValueOrigins is { Count: 1 } origins && origins[0] is { } origin && Valid(origin) &&
                                origin.Text == field.ValueText && lua.StartOffset >= 0 && lua.Length <= origin.Length - lua.StartOffset &&
                                selected.TryGetValue(KeyPath(origin.Source.Path), out var luaDocument) &&
                                origin.StartOffset + lua.StartOffset + lua.Length <= luaDocument.DiagnosticEndOffset)
                                luaLocation = luaDocument.Locate(origin.StartOffset + lua.StartOffset, lua.Length);
                            if (lua.Filename is null)
                            {
                                Add("resources.lua.dynamic", WmlCheckSeverity.Information, luaLocation,
                                    "This Lua file call has a computed or unsupported path.",
                                    "Only literal first arguments to wesnoth.require and wesnoth.dofile are checked. Lua is not executed and aliases or runtime scope are not inferred.", evaluation.Label, related);
                                continue;
                            }
                            if (lua.Filename.StartsWith("./", StringComparison.Ordinal) || lua.IsRequire && !lua.Filename.EndsWith(".lua", StringComparison.Ordinal))
                            {
                                Add("resources.lua.needs-context", WmlCheckSeverity.Information, luaLocation,
                                    $"Lua module '{lua.Filename}' needs runtime lookup context.",
                                    "Relative Lua paths use the executing script's directory, and require can use runtime modules, directory packages and filename fallbacks. Only explicit captured .lua paths are checked; no fallback or WML-caller directory was guessed.", evaluation.Label, related);
                                continue;
                            }
                            var syntax = WmlResourceReferenceSyntax.ParseFilename(lua.Filename, cancellationToken);
                            if (!syntax.IsComplete)
                            {
                                Add("resources.lua.dynamic", WmlCheckSeverity.Information, luaLocation, "This Lua file path needs additional context.",
                                    string.Join("\n", syntax.Issues.Select(issue => issue.Reason)), evaluation.Label, related);
                                continue;
                            }
                            foreach (var luaFilename in syntax.Filenames)
                            {
                                CheckFilename("lua", "Lua file", luaFilename, null, [], resources.HasGlobalUncertainty,
                                    luaLocation, caller!, related, evaluation.Label, lua.IsRequire);
                            }
                        }
                        continue;
                    }
                    // An exactly mapped literal payload may contain runtime siblings. The pure
                    // parser observes only its independent static filenames, never effective value proof.
                    var decoded = WmlResourceValueTextDecoder.Decode(field.ValueText, cancellationToken);
                    if (value.State != WmlStaticScalarResolutionState.Known && !boundedMappedValue)
                    {
                        var literalOwnerUncertainty = resource.HasUncertainEnclosingStructure &&
                            decoded.IsDecoded && SupportedFilename(decoded.ValueText);
                        if (literalOwnerUncertainty)
                            Add($"resources.{kind}.owner-uncertain", WmlCheckSeverity.Information, location!,
                                $"This literal {kind} filename has no proven local owner extent.",
                                "Resolved value: " + decoded.ValueText + "\nField: [" + owner.TagName + "]." + key +
                                "\n" + Provenance(contribution) +
                                "\nThe surrounding evaluated WML is structurally uncertain, and an exact, locally paired resource fragment could not be established. Resource checks were deferred without guessing ownership. This is a coverage notice, not a missing-file finding.",
                                evaluation.Label, related);
                        else Add($"resources.{kind}.value-unobserved", WmlCheckSeverity.Information, location!,
                            $"This {kind} value has no proven mapped resource extent.",
                            "Field: [" + owner.TagName + "]." + key + "\nCaptured value: " +
                            (field.ValueText ?? "(unavailable)") + "\n" + Provenance(contribution) +
                            "\nResource checks were deferred. Structural or value-mapping uncertainty does not establish a runtime filename or a missing file.", evaluation.Label, related);
                        continue;
                    }
                    if (!decoded.IsDecoded)
                    {
                        Add($"resources.{kind}.value-unsupported", WmlCheckSeverity.Information, location!,
                            $"This {kind} value needs additional WML text context.",
                            "Captured value: " + field.ValueText + "\n" + decoded.Issue!.Reason +
                            "\nNo runtime dependency or missing file was inferred from unsupported value encoding.", evaluation.Label, related);
                        continue;
                    }
                    var name = decoded.ValueText;
                    if (name.Length == 0) continue;
                    var valueCalls = valueOrigins.FindInvocationOrigins(contribution, field.ValueText,
                        composite is null ? resource.OutputOffset : 0, cancellationToken);
                    if (name is "none" or "unit_image" && (owner.TagName == "message" && key is "image" or "second_image" ||
                        key is "profile" or "small_profile" or "portrait")) continue;
                    if (fieldKind is WmlResourceFieldKind.TerrainImage or WmlResourceFieldKind.TerrainSymbol)
                    {
                        if (name.Contains('@'))
                        {
                            var dynamicLocation = LocateDynamicValue(contribution, valueCalls, location!, caller!);
                            Defer("image", dynamicLocation.Location, name,
                                TerrainTemplateDetails(owner), evaluation.Label, related, RuntimeSourceSite(contribution, dynamicLocation.Location));
                            continue;
                        }
                        if (fieldKind == WmlResourceFieldKind.TerrainSymbol) name += ".png";
                    }
                    var parsed = fieldKind.Value switch
                    {
                        WmlResourceFieldKind.Sound => WmlResourceReferenceSyntax.ParseSound(name, cancellationToken: cancellationToken),
                        WmlResourceFieldKind.TerrainSymbol => WmlResourceReferenceSyntax.ParseImage(name, cancellationToken: cancellationToken, baseFilenamePrefix: "terrain/"),
                        WmlResourceFieldKind.TerrainImage => WmlResourceReferenceSyntax.ParseImage(name, true, cancellationToken: cancellationToken, baseFilenamePrefix: "terrain/"),
                        WmlResourceFieldKind.Image => WmlResourceReferenceSyntax.ParseImage(name, cancellationToken: cancellationToken),
                        WmlResourceFieldKind.AnimationImage => WmlResourceReferenceSyntax.ParseImage(name, true, cancellationToken: cancellationToken),
                        _ => WmlResourceReferenceSyntax.ParseBinaryFilename(name, cancellationToken)
                    };
                    if (!parsed.IsComplete && parsed.Issues.All(issue => issue.Kind == WmlResourceSyntaxIssueKind.Dynamic))
                    {
                        var dynamicLocation = LocateDynamicValue(contribution, valueCalls, location!, caller!);
                        Defer(kind, dynamicLocation.Location, name, "Field: [" + owner.TagName + "]." + key + "\n" +
                            string.Join("\n", parsed.Issues.Select(issue => issue.Reason)) + "\n" + Provenance(contribution),
                            evaluation.Label, related, RuntimeSourceSite(contribution, dynamicLocation.Location));
                    }
                    else if (!parsed.IsComplete)
                        Add($"resources.{kind}.syntax-unsupported", WmlCheckSeverity.Information, location!,
                            $"This {kind} reference contains unsupported filename or rendering syntax.",
                            "Resolved value: " + name + "\n" + string.Join("\n", parsed.Issues.Select(issue => issue.Reason)) + "\nIndependent supported literal filenames retain their own checks.", evaluation.Label, related);
                    foreach (var filename in parsed.Filenames)
                    {
                        var filenameLocation = LocateFilename(filename, contribution, valueCalls, location!, caller!);
                        CheckFilename(kind, label, filename, folder, routes, unresolvedBinaryPaths,
                            filenameLocation.Location, filenameLocation.Caller, related, evaluation.Label, false);
                    }
                }
            }
        }

        foreach (var (path, labels) in skipped)
            diagnostics.Add(Finding("resources.evaluation-partial", WmlCheckSeverity.Information, selected[path].Locate(0),
                "Resource field checks were deferred in incomplete campaign environments.",
                "Contexts: " + string.Join(", ", labels.Distinct(StringComparer.Ordinal)) +
                ". Resolve preprocessing findings before interpreting resource fields. Reached resource includes and other complete environments retain their own checks."));
        foreach (var (path, document) in selected.Where(pair => !visits.Contains(pair.Key)))
            diagnostics.Add(Finding("resources.source-unvisited", WmlCheckSeverity.Information, document.Locate(0),
                "This checked source was not visited by the captured campaign environments.",
                "Its resource references were not checked. Source availability does not establish that the selected campaign includes the file."));
        foreach (var ((_, kind, _), group) in deferred)
        {
            var locations = group.Locations.OrderBy(item => item.StartOffset).ToArray();
            diagnostics.Add(Finding($"resources.{kind}.dynamic", WmlCheckSeverity.Information, locations[0],
                $"{locations.Length} {kind} reference(s) in this file need runtime or generated filename context.",
                "Macros were expanded in the captured campaign context. These resulting filenames share a dynamic source and still depend on game state or generated rule values; they were not reported as missing files.\n" +
                string.Join("\n", group.Examples) +
                "\nIndependent supported literal filenames retain their own checks. Related locations identify the other occurrences and definitions.\n\nContexts: " +
                string.Join(", ", group.Labels.Distinct(StringComparer.Ordinal))) with
            {
                RelatedLocations = Array.AsReadOnly(locations.Skip(1).Concat(group.Related).Distinct().ToArray())
            });
        }
        foreach (var group in findings.Values)
            diagnostics.Add(group.Diagnostic with
            {
                Details = group.Diagnostic.Details + "\n\nContexts: " + string.Join(", ", group.Labels.Distinct(StringComparer.Ordinal)),
                RelatedLocations = Array.AsReadOnly(group.Related.Distinct().ToArray())
            });

        bool Matches(WmlStaticSource source) => source.IsAuthored && captured.TryGetValue(KeyPath(source.Path), out var sources) &&
            sources.Length == 1 && sources[0].Text == source.Text;

        void CheckFilename(string kind, string label, string name, string? folder, IReadOnlyList<string> routes,
            bool uncertain, WmlCheckLocation location, WmlStaticSource caller, IReadOnlyList<WmlCheckLocation> related,
            string environment, bool luaRequire)
        {
            // This virtual namespace is internal capture metadata. Only the path
            // planner may produce it from an engine-supported resource filename.
            var guardedName = folder is not null && WmlResourceLookupPaths.TryNormalizeBinaryFilename(name, out var normalizedName)
                ? normalizedName : name;
            if (guardedName.StartsWith(WmlResourceLookupPaths.InstallationResourceMountPrefix, StringComparison.Ordinal))
            {
                Add($"resources.{kind}.needs-context", WmlCheckSeverity.Information, location,
                    $"{label} '{name}' uses a private capture path rather than a supported Wesnoth path.",
                    "Installation resource mounts are internal report evidence. An authored reference cannot use them to establish resource presence.", environment, related);
                return;
            }
            var plan = WmlResourceLookupPaths.Create(name, folder, routes, kind == "image", cancellationToken, resolver.InstallationResourceFolders);
            var checkedPaths = plan.Candidates;
            uncertain |= campaign.Problems.Count != 0;
            foreach (var candidate in checkedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = Lookup(candidate, caller);
                if (string.IsNullOrWhiteSpace(result.MembershipIdentity)) { uncertain = true; continue; }
                if (result.Exists == WmlStaticTruth.True) return;
                if (result.Exists != WmlStaticTruth.False) uncertain = true;
                if (luaRequire && resolver is IWmlStaticExistenceResolver existence && result.Exists == WmlStaticTruth.False &&
                    existence.ResolveExistence(candidate, caller).Exists == WmlStaticTruth.True) uncertain = true;
            }
            var details = "Captured search paths:\n" + string.Join("\n", checkedPaths.Select(DisplayResourcePath)) +
                (kind == "image" ? "\nRequested image filenames and same-stem WebP fallbacks are separate lookup attempts. " +
                    "'installation/' refers to captured Wesnoth installation directories, not the campaign root." : "") +
                "\nOnly recognized resource filenames and captured routes were checked, including captured installation resource folders, explicit data paths and PNG/JPG-to-WebP image fallback. Global game/user fallback paths outside those captured folders (including direct root lookup of loose files), " +
                "resource precedence, runtime binary-path changes, portrait fallbacks, Lua execution, image/audio decoding and full campaign validity remain unvalidated.";
            if (uncertain)
                details += "\nSome binary-path or captured search-path evidence is unavailable. Literal filename observation does not supply missing lookup context or establish game validity.";
            var inactive = new List<string>();
            if (folder is not null && !plan.IsExplicitDataPath)
                foreach (var dependency in campaign.SourceContext.DependencyRoots)
                {
                    foreach (var path in WmlResourceLookupPaths.Create(name, folder, ["~add-ons/" + dependency.AddonId], kind == "image", cancellationToken).Candidates)
                    {
                        if (checkedPaths.Contains(path, StringComparer.Ordinal)) continue;
                        var candidate = Lookup(path, caller);
                        if (!string.IsNullOrWhiteSpace(candidate.MembershipIdentity) && candidate.Exists == WmlStaticTruth.True) inactive.Add(path);
                    }
                }
            if (inactive.Count != 0)
            {
                Add(uncertain ? $"resources.{kind}.needs-context" : $"resources.{kind}.inactive-path",
                    uncertain ? WmlCheckSeverity.Information : WmlCheckSeverity.Warning, location,
                    uncertain ? $"{label} '{name}' was found in a supplied dependency outside the proven active paths; search-path context remains uncertain."
                        : $"{label} '{name}' was found in a supplied dependency, but its resource path is inactive.",
                    details + "\nCaptured dependency files:\n" + string.Join("\n", inactive) +
                    "\nSupplying a dependency does not activate its [binary_path]. Check the campaign's add-on ID and authored [binary_path] before changing the filename.", environment, related);
                return;
            }
            Add(uncertain ? $"resources.{kind}.needs-context" : $"resources.{kind}.not-found",
                uncertain ? WmlCheckSeverity.Information : WmlCheckSeverity.Warning, location,
                uncertain ? $"{label} '{name}' was not found in the checked captured paths; some source or search-path context remains uncertain."
                    : $"{label} '{name}' was not found in the captured search paths.", details, environment, related);
        }

        WmlStaticIncludeResolution Lookup(string path, WmlStaticSource caller)
        {
            if (path.StartsWith("./", StringComparison.Ordinal)) return resolver.ResolveResource(path, caller);
            if (!lookupCache.TryGetValue(path, out var result)) lookupCache.Add(path, result = resolver.ResolveResource(path, caller));
            return result;
        }

        bool TrySelectedLocation(WmlStaticScalarOwner owner, WmlStaticScalarField field,
            WmlStaticScalarContribution? contribution, out WmlCheckLocation? location,
            out WmlStaticSource? caller, out IReadOnlyList<WmlCheckLocation> related)
        {
            var origins = contribution?.ValueOrigins ?? field.Origins;
            var candidates = origins.Concat(contribution?.ExpansionStack.SelectMany(frame => frame.InvocationOrigins) ?? [])
                .Concat(owner.OpeningExpansionStack.SelectMany(frame => frame.InvocationOrigins));
            return TryOriginsLocation(candidates, out location, out caller, out related, origins);
        }

        (WmlCheckLocation Location, WmlStaticSource Caller) LocateFilename(string filename,
            WmlStaticScalarContribution? contribution, IReadOnlyList<WmlStaticSourceRange> valueCalls, WmlCheckLocation fallback, WmlStaticSource fallbackCaller)
        {
            // A composite value can contain several different caller arguments.
            // Prefer a captured origin that actually spells this filename, not
            // the first sibling argument used while locating the whole field.
            foreach (var origin in contribution?.ValueOrigins ?? [])
            {
                if (!Valid(origin) || !Matches(origin.Source) || !selected.TryGetValue(KeyPath(origin.Source.Path), out var document) ||
                    origin.StartOffset >= document.DiagnosticEndOffset || origin.Length > document.DiagnosticEndOffset - origin.StartOffset) continue;
                var raw = origin.Text.Trim();
                if (raw != filename && WccScenarioFieldValueCodec.DecodeLiteral(raw) != filename) continue;
                var index = origin.Text.IndexOf(filename, StringComparison.Ordinal);
                if (index < 0 || origin.StartOffset + index + filename.Length > document.DiagnosticEndOffset) continue;
                return (document.Locate(origin.StartOffset + index, filename.Length), origin.Source);
            }
            // Generated or split spellings may have no single literal source
            // span. Navigate to their invocation instead of a sibling value.
            if (TryOriginsLocation(valueCalls, out var invocation, out var caller, out _)) return (invocation!, caller!);
            return (fallback, fallbackCaller);
        }

        (WmlCheckLocation Location, WmlStaticSource Caller) LocateDynamicValue(WmlStaticScalarContribution? contribution,
            IReadOnlyList<WmlStaticSourceRange> valueCalls, WmlCheckLocation fallback, WmlStaticSource fallbackCaller)
        {
            var dynamicOrigins = (contribution?.ValueOrigins ?? []).Where(origin => Valid(origin) &&
                origin.Text.IndexOfAny(['$', '@', '{']) >= 0);
            if (TryOriginsLocation(dynamicOrigins, out var dynamicLocation, out var dynamicCaller, out _))
                return (dynamicLocation!, dynamicCaller!);
            if (TryOriginsLocation(valueCalls, out var invocation, out var caller, out _)) return (invocation!, caller!);
            if (TryOriginsLocation(contribution?.AssignmentOrigins ?? [], out var assignment, out caller, out _))
                return (assignment!, caller!);
            return (fallback, fallbackCaller);
        }

        string RuntimeSourceSite(WmlStaticScalarContribution? contribution, WmlCheckLocation fallback)
        {
            // Group actual dynamic source sites, rather than unrelated runtime filenames
            // throughout the whole file. This is display grouping, not variable evaluation.
            var sites = (contribution?.ValueOrigins ?? []).Where(origin => Valid(origin) && Matches(origin.Source) &&
                origin.Text.IndexOfAny(['$', '@', '{']) >= 0).Select(origin =>
                KeyPath(origin.Source.Path) + ":" + origin.StartOffset + ":" + origin.Length).Distinct().ToArray();
            return sites.Length == 0 ? fallback.StartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Join("|", sites);
        }

        string Provenance(WmlStaticScalarContribution? contribution)
            => "Expansion: " + string.Join(" -> ", contribution?.ExpansionStack.Select(frame => frame.MacroName) ?? []) +
               "\nValue origins: " + string.Join(", ", (contribution?.ValueOrigins ?? []).Where(origin => Valid(origin) && Matches(origin.Source))
                   .Select(LocateSupport).Select(origin => origin.Path + ":" + origin.Line).Distinct());

        string TerrainTemplateDetails(WmlStaticScalarOwner owner)
        {
            // These are terrain-builder filename templates, not necessarily game
            // variables. Their optional variants and rotated rules cannot be
            // treated as a list of individually required files.
            var image = owner.TagName == "variant" ? owner.Parent : owner;
            var terrain = image;
            while (terrain is not null && terrain.TagName != "terrain_graphics") terrain = terrain.Parent;
            var variations = image?.ResolveField("variations");
            var rotations = terrain?.ResolveField("rotations");
            return "Terrain filename template: @V uses the enclosing image's variations; @R0..@R5 use the rule's rotations. " +
                "The terrain builder discards unavailable optional image variants/frames and can discard an unavailable rotated rule. " +
                "A missing candidate is therefore not independently a required-file error. This bounded checker has not proven a usable generated rule." +
                "\nCaptured variations: " + (variations?.State == WmlStaticScalarResolutionState.Known ? variations.ValueText : "(unavailable)") +
                "\nCaptured rotations: " + (rotations?.State == WmlStaticScalarResolutionState.Known ? rotations.ValueText : "(unavailable)") +
                "\nNo runtime variable or missing file was inferred from this template alone.";
        }

        bool TryOriginsLocation(IEnumerable<WmlStaticSourceRange> candidates, out WmlCheckLocation? location,
            out WmlStaticSource? caller, out IReadOnlyList<WmlCheckLocation> related, IEnumerable<WmlStaticSourceRange>? origins = null)
        {
            location = null; caller = null; related = [];
            foreach (var origin in candidates)
            {
                if (!Valid(origin) || !Matches(origin.Source) || !selected.TryGetValue(KeyPath(origin.Source.Path), out var document) ||
                    origin.StartOffset >= document.DiagnosticEndOffset ||
                    origin.Length > document.DiagnosticEndOffset - origin.StartOffset) continue;
                location = document.Locate(origin.StartOffset, origin.Length);
                caller = origin.Source;
                related = Array.AsReadOnly((origins ?? candidates).Where(item => Valid(item) && Matches(item.Source) &&
                        !selected.ContainsKey(KeyPath(item.Source.Path))).Select(LocateSupport).Distinct().ToArray());
                return true;
            }
            return false;
        }

        WmlCheckLocation LocateSupport(WmlStaticSourceRange origin)
        {
            var path = KeyPath(origin.Source.Path);
            if (!supportLocations.TryGetValue(path, out var document))
                supportLocations.Add(path, document = new(new(origin.Source.Path, origin.Source.Text), cancellationToken));
            return document.Locate(origin.StartOffset, origin.Length);
        }

        void Add(string code, WmlCheckSeverity severity, WmlCheckLocation location, string message, string details,
            string label, IReadOnlyList<WmlCheckLocation> related)
        {
            var key = new FindingKey(code, location, message);
            if (!findings.TryGetValue(key, out var group))
                findings.Add(key, group = new(Finding(code, severity, location, message, details)));
            group.Labels.Add(label); group.Related.AddRange(related);
        }

        void Defer(string kind, WmlCheckLocation location, string? value, string reason, string label, IReadOnlyList<WmlCheckLocation> related, string site)
        {
            var key = (KeyPath(location.Path), kind, site);
            if (!deferred.TryGetValue(key, out var group)) deferred.Add(key, group = new());
            group.Locations.Add(location); group.Labels.Add(label); group.Related.UnionWith(related);
            value ??= "(unavailable)";
            if (group.Examples.Count < 12)
                group.Examples.Add("Source: " + location.Path + ":" + location.Line + "\nResolved value: " +
                    (value.Length > 512 ? value[..512] + "…" : value) + "\n" + reason);
        }
    }

    private WmlCheckDiagnostic Finding(string code, WmlCheckSeverity severity, WmlCheckLocation location,
        string message, string details) => new(code, Id, severity, WmlCheckConfidence.NeedsContext, location, message, details);

    private static bool TryLiteral(string? raw, out string value, bool allowDynamic = false)
    {
        value = "";
        if (raw is null) return false;
        var text = raw.Trim();
        var quoted = text.StartsWith('"');
        if (text.StartsWith('"'))
        {
            if (text.Length < 2 || !text.EndsWith('"')) return false;
            text = text[1..^1];
        }
        if (text.Contains('"') || text.StartsWith("_ ", StringComparison.Ordinal) || text.StartsWith("_(", StringComparison.Ordinal) ||
            text.Contains('{') || text.Contains('}') || !allowDynamic && text.Contains('$') || !quoted && text.Contains('+') || text.Contains('\n') || text.Contains('\r')) return false;
        value = text;
        return true;
    }

    private static bool SupportedFilename(string value) => !value.StartsWith('/') && !value.Contains('\\') &&
        !value.Contains(':') && !value.Contains(',') && !value.Contains('[') && !value.Contains(']') &&
        !value.Contains('(') && !value.Contains(')') && !value.Any(char.IsControl) &&
        value.Split('/').All(component => component.Length != 0 && component is not ("." or ".."));

    private static string DisplayResourcePath(string path) => path.StartsWith(WmlResourceLookupPaths.InstallationResourceMountPrefix, StringComparison.Ordinal)
        ? "installation/" + path[WmlResourceLookupPaths.InstallationResourceMountPrefix.Length..] : path;

    private static bool TryBinaryMount(string value, out string mount)
    {
        mount = "";
        value = value.TrimEnd('/');
        if (!SupportedFilename(value)) return false;
        return WmlResourceLookupPaths.TryMapDataPath(value, out mount);
    }

    private static bool Valid(WmlStaticSourceRange origin) => origin.Source.IsAuthored && origin.StartOffset >= 0 &&
        origin.Length >= 0 && origin.StartOffset <= origin.Source.Text.Length - origin.Length;
    private static string KeyPath(string path) => path.Replace('\\', '/');
    private sealed record FindingKey(string Code, WmlCheckLocation Location, string Message);
    private sealed class FindingGroup(WmlCheckDiagnostic diagnostic)
    {
        public WmlCheckDiagnostic Diagnostic { get; } = diagnostic;
        public List<string> Labels { get; } = [];
        public List<WmlCheckLocation> Related { get; } = [];
    }
    private sealed class DeferredGroup
    {
        public HashSet<WmlCheckLocation> Locations { get; } = [];
        public HashSet<WmlCheckLocation> Related { get; } = [];
        public HashSet<string> Examples { get; } = new(StringComparer.Ordinal);
        public List<string> Labels { get; } = [];
    }
}
