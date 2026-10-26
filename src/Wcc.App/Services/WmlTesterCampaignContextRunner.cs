using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Wcc.Core.Compatibility;
using Wcc.Core.Library;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;
using Wcc.Core.Wml.Checking;

namespace Wcc.App.Services;

/// <summary>
/// Fresh, read-only preprocessing from one explicit entry. Existing capture, evaluator
/// and scalar projection own include order and menu metadata; this report is never a save proof.
/// </summary>
public sealed class WmlTesterCampaignContextRunner(string? installPath, string installationLabel,
    WmlTesterCampaignOptions options, string? exactVersion = null, WmlTesterCampaignSourceCapture? capture = null)
{
    private readonly WmlTesterCampaignSourceCapture _capture = capture ?? new();

    public WmlCheckReport Check(IReadOnlyList<WmlCheckSource> sources,
        IReadOnlyList<WmlCheckModuleId> modules, CancellationToken cancellationToken)
        => CheckCaptured(sources, modules, cancellationToken).Report;

    public WmlTesterRunResult CheckCaptured(IReadOnlyList<WmlCheckSource> sources,
        IReadOnlyList<WmlCheckModuleId> modules, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.CheckWholeCampaign && !options.IsEnabled)
            return new(new WmlCheckService().Check([], modules, cancellationToken) with
            {
                Diagnostics = [new("campaign.scope-unavailable", WmlCheckModuleId.Structure,
                    WmlCheckSeverity.Error, WmlCheckConfidence.NeedsContext, new(options.EntryPath, 1, 1, 0, 0),
                    "Whole-campaign checks require a campaign main file.", "Select the campaign main .cfg before running this scope.")]
            });
        if (!options.IsEnabled || !options.CheckWholeCampaign && !modules.Contains(WmlCheckModuleId.References) && !modules.Contains(WmlCheckModuleId.Resources))
            return new(new WmlTesterCoreContextRunner(installPath, installationLabel).Check(sources, modules, cancellationToken));

        var version = exactVersion;
        var problems = new List<string>();
        if (version is null)
        {
            var detection = WesnothInstallationIdentityDetector.Detect(installPath);
            version = detection.Identity?.ExactVersion;
            if (version is null) problems.Add(detection.Error ?? "The exact installation version is unavailable.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        WccStaticSourceContext sourceContext;
        try
        {
            var roots = options.DependencyRoots.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .Select(path => new WccStaticDependencyRoot(Path.GetFileName(path), path)).ToArray();
            sourceContext = new(options.AddonsNamespacePath, roots);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            sourceContext = new(options.AddonsNamespacePath);
            problems.Add("The selected dependency source roots are invalid: " + failure.Message);
        }
        var captured = _capture.CaptureAsync(options.EntryPath, installPath, version, sources,
            sourceContext, cancellationToken, includeCoreUnits: modules.Contains(WmlCheckModuleId.References)).GetAwaiter().GetResult();
        problems.AddRange(captured.Problems);
        var sourceCapture = captured.Capture;
        var support = (sourceCapture?.Sources ?? []).Where(source => source.IsAuthored)
            .Select(source => new WmlCheckSource(source.Path, source.Text)).ToArray();
        var evaluations = new List<WmlCheckCampaignEvaluation>();
        var entryPath = sourceCapture?.EntrySource?.Path ?? options.EntryPath;
        if (sourceCapture?.EntrySource is { } entry && (modules.Contains(WmlCheckModuleId.Structure) ||
            modules.Contains(WmlCheckModuleId.References) || modules.Contains(WmlCheckModuleId.Resources)))
        {
            if (!WmlStaticSingleplayerEnvironment.TryCreate(version, out var environment))
                problems.Add("The exact installed version has no supported desktop singleplayer environment; campaign branches were not evaluated.");
            else
            {
                var preludes = new[] { environment!.DefinitionsSource }.Concat(sourceCapture.PreludeSources).ToArray();
                var menuContext = new WmlEvaluationContext(targetCompatibility: version);
                var menu = Evaluate(menuContext, ["EDITOR", "MULTIPLAYER"]);
                evaluations.Add(new("Menu", menuContext, menu));
                var plan = DiscoverContexts(menu, problems, cancellationToken);
                if (plan is not null)
                {
                    var selected = options.DifficultyDefine.Trim();
                    if (selected.Length != 0 && !plan.Difficulties.Contains(selected, StringComparer.Ordinal))
                        problems.Add($"Difficulty '{selected}' is not declared by the captured campaign menu; no gameplay context was invented.");
                    else
                    {
                        var difficulties = selected.Length != 0 ? new[] { selected }
                            : plan.Difficulties.Count == 0 ? new string?[] { null } : plan.Difficulties.Cast<string?>().ToArray();
                        foreach (var difficulty in difficulties)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var context = new WmlEvaluationContext(plan.CampaignDefine, difficulty,
                                plan.ExtraDefines, version);
                            var undefined = plan.Difficulties.Concat(["EDITOR", "MULTIPLAYER"])
                                .Where(symbol => !context.IsDefined(symbol)).Distinct(StringComparer.Ordinal).ToArray();
                            var evaluated = Evaluate(context, undefined, includeCoreUnits: true);
                            var coreUnitsReached = modules.Contains(WmlCheckModuleId.References)
                                && sourceCapture.HasCompleteCoreUnitPrelude
                                && evaluated.Includes.Any(include => include.IncludePath == "core/units.cfg"
                                    && include.Resolution.Exists == WmlStaticTruth.True
                                    && include.Resolution.Sources.Count == 1
                                    && include.InvocationOrigins.Any(origin => sourceCapture.CoreUnitPreludeSources.Contains(origin.Source))
                                    && evaluated.Sources.Contains(include.Resolution.Sources[0]));
                            if (modules.Contains(WmlCheckModuleId.References) && sourceCapture.HasCompleteCoreUnitPrelude && !coreUnitsReached)
                                problems.Add("The requested captured Core unit include was not reached in gameplay context '"
                                    + (difficulty ?? "Campaign default") + "'; unit-reference absence checks remain unavailable.");
                            evaluations.Add(new(difficulty ?? "Campaign default", context, evaluated)
                            {
                                IncludesEngineCoreUnitData = coreUnitsReached
                            });
                        }
                    }
                }

                WmlStaticEvaluationResult Evaluate(WmlEvaluationContext context, IReadOnlyList<string> undefined,
                    bool includeCoreUnits = false) =>
                    new WmlStaticEvaluator().Evaluate(new(entry, context,
                        includeCoreUnits ? preludes.Concat(sourceCapture.CoreUnitPreludeSources).ToArray() : preludes, undefined,
                        IsInitialEnvironmentComplete: true, IncludeResolver: sourceCapture.IncludeResolver,
                        Limits: WmlStaticEvaluationLimits.Campaign), cancellationToken);
            }
        }
        else if (sourceCapture?.EntrySource is null && problems.Count == 0) problems.Add("The campaign entry source is unavailable.");

        cancellationToken.ThrowIfCancellationRequested();
        var campaign = new WmlCheckCampaignContext(entryPath, support, evaluations, problems, sourceContext,
            sourceCapture?.IncludeResolver as IWmlStaticResourceResolver);
        var lexicalSupport = support.Where(source => string.Equals(Path.GetExtension(source.Path), ".cfg", StringComparison.OrdinalIgnoreCase)).ToArray();
        var contextSources = new WmlCheckReferenceContext(installationLabel + " — campaign, Core and explicit dependencies",
            sourceCapture?.EntrySource is { } capturedEntry ? Path.GetDirectoryName(capturedEntry.Path) : null,
            lexicalSupport, campaignContext: campaign);
        IReadOnlyList<WmlCheckSource> targets = options.CheckWholeCampaign
            ? Array.AsReadOnly(captured.CampaignSources.OrderBy(source => source.Path, StringComparer.Ordinal).ToArray())
            : sources;
        var report = new WmlCheckService(referenceContext: contextSources).Check(targets, modules, cancellationToken);
        if (options.CheckWholeCampaign)
        {
            var root = captured.CampaignRootPath ?? Path.GetDirectoryName(Path.GetFullPath(options.EntryPath))!;
            var visited = evaluations.SelectMany(item => item.Evaluation.Sources).Select(source => source.Path);
            report = report with { ReferenceContext = contextSources, TargetScope = new(root, targets.Select(source => source.Path), visited,
                captured.IsCampaignInventoryComplete) };
            if (!captured.IsCampaignInventoryComplete || sourceCapture is null || targets.Count == 0 || captured.CampaignSnapshots.Count != targets.Count)
                report = report with { Diagnostics = Array.AsReadOnly(report.Diagnostics.Concat([new WmlCheckDiagnostic(
                    "campaign.scope-unavailable", WmlCheckModuleId.Structure, WmlCheckSeverity.Error,
                    WmlCheckConfidence.NeedsContext, new(entryPath, 1, 1, 0, 0),
                    "The whole-campaign source inventory could not be captured completely.",
                    string.Join("\n", problems))]).ToArray()) };
        }
        var targetPaths = targets.Select(source => source.Path).ToHashSet(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return new(report, captured.CampaignSnapshots.Where(file => targetPaths.Contains(file.Path)));
    }

    private static ContextPlan? DiscoverContexts(WmlStaticEvaluationResult menu,
        ICollection<string> problems, CancellationToken cancellationToken)
    {
        if (!menu.IsComplete)
        {
            problems.Add("Campaign menu preprocessing is incomplete; declared gameplay contexts could not be established.");
            return null;
        }
        var projection = WmlStaticScalarProjection.Create(menu, cancellationToken);
        var owners = projection.Owners.Where(owner => owner.TagName == "campaign" && owner.Parent is null).ToArray();
        if (projection.Diagnostics.Count != 0 || owners.Length == 0
            || owners.Any(owner => owner.OpeningOutputOffset is null))
        {
            problems.Add("The evaluated entry has no unique complete campaign menu; no gameplay context was guessed.");
            return null;
        }
        ContextPlan? sharedPlan = null;
        foreach (var owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = ReadPlan(owner);
            if (plan is null) return null;
            if (sharedPlan is not null && (plan.CampaignDefine != sharedPlan.CampaignDefine
                || !plan.ExtraDefines.ToHashSet(StringComparer.Ordinal).SetEquals(sharedPlan.ExtraDefines)
                || !plan.Difficulties.ToHashSet(StringComparer.Ordinal).SetEquals(sharedPlan.Difficulties)))
            {
                problems.Add("The campaign menus have no unique preprocessing environment: their defines or difficulties differ. No gameplay context was guessed.");
                return null;
            }
            sharedPlan ??= plan;
        }
        return sharedPlan;

        ContextPlan? ReadPlan(WmlStaticScalarOwner owner)
        {
            var type = owner.ResolveField("type");
            if (type.State == WmlStaticScalarResolutionState.Unresolved || type.State == WmlStaticScalarResolutionState.Known
                && WccScenarioFieldValueCodec.DecodeLiteral(type.ValueText).Trim() != "sp")
            {
                problems.Add("Only a captured singleplayer campaign menu is supported by this context check.");
                return null;
            }
            var campaignDefine = ReadSymbols(owner, "define", required: true);
            var extras = ReadSymbols(owner, "extra_defines", required: false);
            if (campaignDefine is not { Length: 1 } || extras is null)
            {
                problems.Add("Campaign define or extra defines are ambiguous or unresolved.");
                return null;
            }
            if (!WmlCampaignDifficultyObserver.TryObserve(owner, campaignDefine[0], extras,
                (sourceOwner, key) => ReadSymbols(sourceOwner, key, required: true),
                out var difficulties, out var difficultyProblem))
            {
                problems.Add(difficultyProblem!);
                return null;
            }
            var allSymbols = campaignDefine.Concat(extras).Concat(difficulties);
            if (allSymbols.Any(symbol => symbol is "MULTIPLAYER" or "EDITOR" or "APPLE"
                || symbol.StartsWith("WESNOTH_VERSION", StringComparison.Ordinal)))
            { problems.Add("Campaign symbols conflict with the selected desktop singleplayer environment."); return null; }
            return new(campaignDefine[0], Array.AsReadOnly(extras), difficulties);
        }

        static string[]? ReadSymbols(WmlStaticScalarOwner sourceOwner, string key, bool required)
        {
            var field = sourceOwner.ResolveField(key);
            if (field.State == WmlStaticScalarResolutionState.ProvenAbsent) return required ? null : [];
            if (field.State != WmlStaticScalarResolutionState.Known) return null;
            var literal = WccScenarioFieldValueCodec.DecodeLiteral(field.ValueText).Trim();
            if (literal.Length == 0) return required ? null : [];
            var values = literal.Split(',').Select(value => value.Trim()).ToArray();
            return values.All(value => WmlPreprocessorToken.IsLiteralName(value) && !value.Contains('$')) ? values : null;
        }
    }

    private sealed record ContextPlan(string CampaignDefine, IReadOnlyList<string> ExtraDefines,
        IReadOnlyList<string> Difficulties);
}
