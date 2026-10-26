using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.ViewModels;

public partial class AppShellViewModel
{
    private WccSavePerformanceCollector? _savePerformanceCollector;
    public WccSavePerformanceReport? LastSavePerformance { get; private set; }

    private void MeasureSavePhase(string name, Action action)
    {
        var collector = _savePerformanceCollector;
        var started = Stopwatch.GetTimestamp();
        try { action(); }
        finally { collector?.AddPhase(name, started); }
    }
    private T MeasureSavePhase<T>(string name, Func<T> action)
    {
        var collector = _savePerformanceCollector;
        var started = Stopwatch.GetTimestamp();
        try { return action(); }
        finally { collector?.AddPhase(name, started); }
    }
    private async Task<T> MeasureSavePhaseAsync<T>(string name, Func<Task<T>> action)
    {
        var collector = _savePerformanceCollector;
        var started = Stopwatch.GetTimestamp();
        try { return await action(); }
        finally { collector?.AddPhase(name, started); }
    }
    private async Task MeasureSavePhaseAsync(string name, Func<Task> action)
    {
        var collector = _savePerformanceCollector;
        var started = Stopwatch.GetTimestamp();
        try { await action(); }
        finally { collector?.AddPhase(name, started); }
    }
}
