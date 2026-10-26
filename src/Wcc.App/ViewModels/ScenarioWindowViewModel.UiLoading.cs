using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private const int MaximumScenarioUiContexts = 16;
    private Task _scenarioContextWarmTask = Task.CompletedTask;
    private StaticEvaluationScopeKey? _scenarioContextWarmScope;
    private long _scenarioContextWarmVersion;
    private bool _scenarioUiPublicationPending;

    /// <summary>Display work only; a warmed projection never grants write authority.</summary>
    public bool IsScenarioUiLoading => _scenarioUiPublicationPending || !_scenarioContextWarmTask.IsCompleted;

    public string? ScenarioUiLoadingStatus => !_scenarioContextWarmTask.IsCompleted
        ? "Reading scenario initial values in every declared difficulty…"
        : _scenarioUiPublicationPending ? "Reading scenario initial values…" : null;

    public async Task WaitForScenarioUiLoadingAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = _staticInitialValuesTask;
            await selected.WaitAsync(cancellationToken);
            var warm = _scenarioContextWarmTask;
            try { await warm.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Completed failed preview entries remain available for their
                // own selected-context diagnostic, separate from any Save.
            }
            if (ReferenceEquals(selected, _staticInitialValuesTask) &&
                ReferenceEquals(warm, _scenarioContextWarmTask)) return;
        }
    }

    private void StartScenarioContextWarm(WmlStaticSource source, WmlEvaluationContext current,
        WmlCampaignEvaluationPlan? plan, WccLibrarySnapshot? snapshot,
        System.Collections.Generic.IReadOnlySet<WccPackageRevisionId>? visibleRevisions,
        StaticRootRevisionSelection? rootSelection, string? addonsPath, string profileIdentity,
        StaticEvaluationScopeKey scope, ScenarioInspectionBinding? inspectionBinding)
    {
        if (plan is null || snapshot is null || Equals(_scenarioContextWarmScope, scope)) return;
        var contexts = new[] { current }.Concat(plan.Contexts)
            .DistinctBy(context => context.SemanticKey).ToArray();
        // Larger or unknown declarations keep the selected-context behavior;
        // they cannot cause unbounded eager campaign evaluation.
        if (contexts.Length > MaximumScenarioUiContexts) return;
        _scenarioContextWarmScope = scope;
        var version = ++_scenarioContextWarmVersion;
        var task = Task.WhenAll(contexts.Select(context =>
        {
            var key = new StaticEvaluationKey(source.Path, source.ContentHash, context.SemanticKey,
                plan, snapshot, visibleRevisions, rootSelection, addonsPath, profileIdentity)
                { InspectionBinding = inspectionBinding, CapturedBinding = scope.CapturedBinding };
            return _staticEvaluationCache.GetAsync(scope, key,
                workToken => ReadCapturedOrEvaluateStaticValuesAsync(scope.CapturedBinding, context,
                    () => EvaluateStaticInitialValuesAsync(source, context, plan, snapshot,
                        visibleRevisions, rootSelection, addonsPath, profileIdentity, workToken, inspectionBinding), workToken),
                CancellationToken.None);
        }));
        _scenarioContextWarmTask = task;
        _ = ObserveScenarioContextWarmAsync(task, version);
        RaiseScenarioUiLoadingChanged();
    }

    private async Task ObserveScenarioContextWarmAsync(Task task, long version)
    {
        try { await task; }
        catch (Exception)
        {
            // Each context refresh owns its diagnostic when selected. Observing
            // background failures cannot publish another context's UI or values.
        }
        finally
        {
            lock (_wccInitialFieldRefreshGate)
                if (version == _scenarioContextWarmVersion) RaiseScenarioUiLoadingChanged();
        }
    }

    private void ClearScenarioContextWarm()
    {
        _scenarioContextWarmVersion++;
        _scenarioContextWarmScope = null;
        _scenarioContextWarmTask = Task.CompletedTask;
    }

    private void RaiseScenarioUiLoadingChanged()
    {
        OnPropertyChanged(nameof(IsScenarioUiLoading));
        OnPropertyChanged(nameof(ScenarioUiLoadingStatus));
        RaiseWccPresetApplicationCapabilities();
    }
}
