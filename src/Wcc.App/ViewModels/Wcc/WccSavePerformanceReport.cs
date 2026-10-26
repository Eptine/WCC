using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels.Wcc;

public sealed record WccSaveUiPhaseMeasurement(string Name, TimeSpan Duration);

/// <summary>Read-only measurements of one global Save, including its final preview wait.</summary>
public sealed class WccSavePerformanceReport
{
    internal WccSavePerformanceReport(TimeSpan elapsed, bool succeeded,
        IEnumerable<WccSaveUiPhaseMeasurement> phases, IEnumerable<WccSourceSaveDiagnostics> operations)
    {
        Elapsed = elapsed;
        Succeeded = succeeded;
        Phases = Array.AsReadOnly(phases.ToArray());
        SourceOperations = Array.AsReadOnly(operations.ToArray());
    }
    public TimeSpan Elapsed { get; }
    /// <summary>
    /// Whether this batch finished checking, writing and publishing before the
    /// report notifications. Later edits or navigation changes do not mutate it
    /// or grant permission to navigate. Joined source runs retain their full
    /// elapsed duration, which can begin before this global Save was requested.
    /// </summary>
    public bool Succeeded { get; }
    // Inclusive timings can overlap. They are not an additive breakdown of Elapsed.
    public IReadOnlyList<WccSaveUiPhaseMeasurement> Phases { get; }
    public IReadOnlyList<WccSourceSaveDiagnostics> SourceOperations { get; }
    public bool AuthorizesPersistence => false;
    public string Summary
    {
        get
        {
            var lines = new List<string>
            {
                $"Last Save batch: {(Succeeded ? "completed" : "incomplete")}, {Seconds(Elapsed)} s total"
            };
            if (Phases.Count != 0)
            {
                lines.Add(string.Empty);
                lines.Add("UI stages (overlapping waits):");
                lines.AddRange(Phases.Select(phase => $"{DisplayPhaseName(phase.Name)}: {Seconds(phase.Duration)} s"));
            }
            lines.Add(string.Empty);
            lines.Add("Complete Core runs:");
            if (SourceOperations.Count == 0)
                lines.Add("Source-stage measurements unavailable for this save path.");
            else
            {
                lines.Add(RunSummary("Prepare", SourceOperations.Where(item => item.OperationName.Contains("prepare", StringComparison.OrdinalIgnoreCase)).ToArray()));
                lines.Add(RunSummary("Commit", SourceOperations.Where(item => item.OperationName.Contains("commit", StringComparison.OrdinalIgnoreCase)).ToArray()));
            }
            lines.Add($"Preprocessing evaluations: {SourceOperations.Sum(item => item.Count(WccSourceSaveCount.PreprocessingEvaluations))}; " +
                $"scalar projections (including masked): {SourceOperations.Sum(item => item.Count(WccSourceSaveCount.ScalarProjections))}");
            lines.Add("Durations overlap and do not add up to the total. Core times cover complete runs; joined runs can start before Save.");
            return string.Join("\n", lines);
        }
    }
    private static string DisplayPhaseName(string name) => name switch
    {
        "Source check" => "Wait for source check",
        "Source commit" => "Wait for source write",
        "Check, write and publish" => "Check, write and publish (includes nested waits)",
        _ => name
    };
    private static string RunSummary(string name, IReadOnlyList<WccSourceSaveDiagnostics> runs) =>
        $"{name}: {Seconds(TimeSpan.FromTicks(runs.Sum(item => item.Elapsed.Ticks)))} s across {runs.Count} {(runs.Count == 1 ? "run" : "runs")}";
    private static string Seconds(TimeSpan duration) => duration.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
}

internal sealed class WccSavePerformanceCollector
{
    private readonly object _gate = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly List<WccSaveUiPhaseMeasurement> _phases = new();
    private readonly List<WccSourceSaveDiagnostics> _operations = new();
    public void AddPhase(string name, long started)
    {
        var phase = new WccSaveUiPhaseMeasurement(name, Stopwatch.GetElapsedTime(started));
        lock (_gate) _phases.Add(phase);
    }
    public void AddSourceOperation(WccSourceSaveDiagnostics? operation)
    {
        if (operation is null) return;
        lock (_gate)
            if (!_operations.Any(item => ReferenceEquals(item, operation))) _operations.Add(operation);
    }
    public WccSavePerformanceReport Complete(bool succeeded)
    {
        lock (_gate) return new(Stopwatch.GetElapsedTime(_started), succeeded, _phases, _operations);
    }
}
