using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wcc.App.Services;
using Wcc.Core.Wml.Checking;

namespace Wcc.App.ViewModels;

public sealed record WmlTesterFile(string Path)
{
    public string DisplayName => System.IO.Path.GetFileName(Path);
}

public enum WmlTesterSeverityFilter { All, Error, Warning, Information }
public enum WmlTesterModuleFilter { All, Structure, References, Formatting, Resources }
public enum WmlTesterConfidenceFilter { All, Proven, NeedsContext }

/// <summary>
/// An independent checker and explicit RAW-fix host. Each run reads fresh files
/// and retains its own buffers; reports do not authorize automatic campaign saving.
/// Commands and notifications belong to the UI thread.
/// </summary>
public sealed class WmlTesterViewModel : ViewModelBase, IDisposable
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly StandaloneWmlFileStore _store;
    private readonly Func<IReadOnlyList<WmlCheckSource>, IReadOnlyList<WmlCheckModuleId>,
        CancellationToken, WmlCheckReport> _runner;
    private readonly Func<Func<IReadOnlyList<WmlCheckSource>, IReadOnlyList<WmlCheckModuleId>,
        CancellationToken, WmlCheckReport>>? _captureRunner;
    private readonly Func<WmlTesterCampaignOptions, Func<IReadOnlyList<WmlCheckSource>, IReadOnlyList<WmlCheckModuleId>,
        CancellationToken, WmlCheckReport>>? _captureCampaignRunner;
    private readonly Func<WmlTesterCampaignOptions, WmlTesterCapturedCheck>? _captureCampaignRun;
    private WmlTesterCampaignOptions _campaignOptions = new();
    private WmlTesterCampaignOptions _reportCampaignOptions = new();
    private RunState? _run;
    private CancellationTokenSource? _sourceSave;
    private IReadOnlyDictionary<string, StandaloneWmlFileSnapshot> _reportFiles =
        new Dictionary<string, StandaloneWmlFileSnapshot>(PathComparer);
    private IReadOnlyList<string> _reportPaths = [];
    private long _generation;
    private bool _disposed;
    private bool _checkStructure = true;
    private bool _checkReferences = true;
    private bool _checkFormatting = true;
    private bool _checkResources;
    private string _status = "Add .cfg files and select check modules.";
    private string? _errorMessage;
    private WmlCheckReport? _report;
    private ObservableCollection<WmlCheckDiagnostic> _diagnostics = [];
    private ObservableCollection<WmlCheckDiagnostic> _visibleDiagnostics = [];
    private WmlCheckDiagnostic? _selectedDiagnostic;
    private long _filterRevision;
    private string _searchText = string.Empty;
    private WmlTesterSeverityFilter _severityFilter;
    private WmlTesterModuleFilter _moduleFilter;
    private WmlTesterConfidenceFilter _confidenceFilter;

    public WmlTesterViewModel(StandaloneWmlFileStore? store = null,
        Func<IReadOnlyList<WmlCheckSource>, IReadOnlyList<WmlCheckModuleId>,
            CancellationToken, WmlCheckReport>? runner = null,
        Func<Func<IReadOnlyList<WmlCheckSource>, IReadOnlyList<WmlCheckModuleId>,
            CancellationToken, WmlCheckReport>>? captureRunner = null,
        Func<WmlTesterCampaignOptions, Func<IReadOnlyList<WmlCheckSource>, IReadOnlyList<WmlCheckModuleId>,
            CancellationToken, WmlCheckReport>>? captureCampaignRunner = null,
        Func<WmlTesterCampaignOptions, WmlTesterCapturedCheck>? captureCampaignRun = null)
    {
        _store = store ?? new StandaloneWmlFileStore();
        _runner = runner ?? ((sources, modules, cancellationToken) =>
            new WmlCheckService().Check(sources, modules, cancellationToken));
        _captureRunner = captureRunner;
        if (captureRunner is not null && captureCampaignRunner is not null)
            throw new ArgumentException("Supply one run-context factory.", nameof(captureCampaignRunner));
        if (captureCampaignRun is not null && (captureRunner is not null || captureCampaignRunner is not null))
            throw new ArgumentException("Supply one run-context factory.", nameof(captureCampaignRun));
        _captureCampaignRunner = captureCampaignRunner;
        _captureCampaignRun = captureCampaignRun ?? (runner is null && captureRunner is null && captureCampaignRunner is null
            ? options => new WmlTesterCampaignContextRunner(null, "No selected installation", options).CheckCaptured : null);
        Files.CollectionChanged += FilesChanged;
    }

    public ObservableCollection<WmlTesterFile> Files { get; } = [];
    public bool CheckStructure
    {
        get => _checkStructure;
        set => SetModule(ref _checkStructure, value, nameof(CheckStructure));
    }
    public bool CheckReferences
    {
        get => _checkReferences;
        set => SetModule(ref _checkReferences, value, nameof(CheckReferences));
    }
    public bool CheckFormatting
    {
        get => _checkFormatting;
        set => SetModule(ref _checkFormatting, value, nameof(CheckFormatting));
    }
    public bool CheckResources
    {
        get => _checkResources;
        set => SetModule(ref _checkResources, value, nameof(CheckResources));
    }
    public bool IsBusy => _run is not null || _sourceSave is not null;
    public bool CanRun => !_disposed && !IsBusy && (CheckWholeCampaign ? _campaignOptions.IsEnabled : Files.Count != 0) &&
        (CheckStructure || CheckReferences || CheckFormatting || CheckResources);
    public bool CanSelectFiles => !_disposed && !IsBusy && !CheckWholeCampaign;
    public IReadOnlyList<WmlTesterFile> DisplayFiles => CheckWholeCampaign
        ? _reportCampaignOptions == _campaignOptions && _report?.TargetScope is not null
            ? Array.AsReadOnly(_report.Sources.Select(source => new WmlTesterFile(source.Path)).ToArray()) : []
        : Files;
    public bool CheckWholeCampaign
    {
        get => _campaignOptions.CheckWholeCampaign;
        set => SetCampaignOptions(_campaignOptions with { CheckWholeCampaign = value }, nameof(CheckWholeCampaign));
    }
    public bool CanCancel => !_disposed && _run is { } run && !run.Cancellation.IsCancellationRequested;
    public bool HasReport => _report is not null;
    public string CampaignEntryPath
    {
        get => _campaignOptions.EntryPath;
        set => SetCampaignOptions(_campaignOptions with { EntryPath = value ?? string.Empty }, nameof(CampaignEntryPath));
    }
    public string DifficultyDefine
    {
        get => _campaignOptions.DifficultyDefine;
        set => SetCampaignOptions(_campaignOptions with { DifficultyDefine = value ?? string.Empty }, nameof(DifficultyDefine));
    }
    public string AddonsNamespacePath
    {
        get => _campaignOptions.AddonsNamespacePath;
        set => SetCampaignOptions(_campaignOptions with { AddonsNamespacePath = value ?? string.Empty }, nameof(AddonsNamespacePath));
    }
    public string DependencyRoots
    {
        get => _campaignOptions.DependencyRoots;
        set => SetCampaignOptions(_campaignOptions with { DependencyRoots = value ?? string.Empty }, nameof(DependencyRoots));
    }

    public void ClearCampaignContext() => SetCampaignOptions(
        new() { AddonsNamespacePath = _campaignOptions.AddonsNamespacePath },
        nameof(CampaignEntryPath), nameof(DifficultyDefine), nameof(DependencyRoots), nameof(CheckWholeCampaign));

    public string CoverageText => _report?.CoverageText ?? WmlCheckReport.Coverage;
    public string SourceActionText => CanEditSelectedSource ? "Edit checked WML" : "View context WML";
    public string Status => _status;
    public string? ErrorMessage => _errorMessage;
    public WmlCheckReport? Report => _report;
    public string ContextText => _report?.ReferenceContext?.Summary ?? "Core context: not supplied.";
    public string ContextDetails => _report?.ReferenceContext is not { } context
        ? "No support sources were captured."
        : context.Summary + "\nRoot: " + (context.RootPath ?? "not supplied") +
          (context.Problems.Count == 0 ? string.Empty : "\n\nContext problems:\n" + string.Join("\n", context.Problems)) +
          (context.CampaignContext is null ? string.Empty : "\n\n" + context.CampaignContext.Details);
    public ObservableCollection<WmlCheckDiagnostic> Diagnostics => _diagnostics;
    public ObservableCollection<WmlCheckDiagnostic> VisibleDiagnostics => _visibleDiagnostics;
    public int MatchingDiagnosticCount => _visibleDiagnostics.Count;
    public string FilterSummary => $"{MatchingDiagnosticCount} of {_report?.Diagnostics.Count ?? 0} diagnostics";
    public IReadOnlyList<WmlTesterSeverityFilter> SeverityFilters { get; } =
        Array.AsReadOnly(Enum.GetValues<WmlTesterSeverityFilter>());
    public IReadOnlyList<WmlTesterModuleFilter> ModuleFilters { get; } =
        Array.AsReadOnly(Enum.GetValues<WmlTesterModuleFilter>());
    public IReadOnlyList<WmlTesterConfidenceFilter> ConfidenceFilters { get; } =
        Array.AsReadOnly(Enum.GetValues<WmlTesterConfidenceFilter>());
    public string SearchText
    {
        get => _searchText;
        set => SetFilter(ref _searchText, value ?? string.Empty, nameof(SearchText));
    }
    public WmlTesterSeverityFilter SeverityFilter
    {
        get => _severityFilter;
        set => SetEnumFilter(ref _severityFilter, value, nameof(SeverityFilter));
    }
    public WmlTesterModuleFilter ModuleFilter
    {
        get => _moduleFilter;
        set => SetEnumFilter(ref _moduleFilter, value, nameof(ModuleFilter));
    }
    public WmlTesterConfidenceFilter ConfidenceFilter
    {
        get => _confidenceFilter;
        set => SetEnumFilter(ref _confidenceFilter, value, nameof(ConfidenceFilter));
    }
    public TimeSpan ReadDuration { get; private set; }
    public TimeSpan CheckDuration { get; private set; }
    public TimeSpan TotalDuration { get; private set; }

    public WmlCheckDiagnostic? SelectedDiagnostic
    {
        get => _selectedDiagnostic;
        set
        {
            if (_disposed || ReferenceEquals(_selectedDiagnostic, value) ||
                (value is not null && !_visibleDiagnostics.Any(item => ReferenceEquals(item, value))))
                return;
            _selectedDiagnostic = value;
            NotifyPresentation(_generation, _filterRevision, _report, nameof(SelectedDiagnostic),
                nameof(SelectedDiagnosticSource), nameof(HasSelectedSource), nameof(CanEditSelectedSource), nameof(CanOpenSelectedSource), nameof(SourceActionText), nameof(DetailsText));
        }
    }

    public WmlCheckSource? SelectedDiagnosticSource
    {
        get
        {
            if (_report is null || _selectedDiagnostic is null) return null;
            // A diagnostic must refer to one exact captured buffer, not a live
            // path lookup or an inferred filename match.
            var matches = _report.Sources.Where(source => string.Equals(source.Path,
                _selectedDiagnostic.Location.Path, StringComparison.Ordinal)).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
    }
    public bool HasSelectedSource => SelectedDiagnosticSource is not null;
    public bool CanEditSelectedSource => !_disposed && !IsBusy && HasSelectedSource &&
        _reportFiles.ContainsKey(SelectedDiagnosticSource!.Path) && _reportCampaignOptions == _campaignOptions &&
        _reportPaths.SequenceEqual(Files.Select(file => file.Path), PathComparer) && CurrentModulesMatch(_report!);
    public bool CanOpenSelectedSource => !_disposed && !IsBusy && (CanEditSelectedSource || SelectedDiagnosticContextSource is not null);
    public WmlCheckSource? SelectedDiagnosticContextSource
    {
        get
        {
            if (_selectedDiagnostic is null || _report?.CampaignContext is not { } context) return null;
            var matches = context.Sources.Where(source => source.Path == _selectedDiagnostic.Location.Path).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
    }

    public WmlTesterSourceEdit? CreateSelectedSourceEdit()
    {
        if (!CanEditSelectedSource || SelectedDiagnosticSource is not { } source ||
            SelectedDiagnostic is not { } finding || !_reportFiles.TryGetValue(source.Path, out var snapshot)) return null;
        return new(_report!, snapshot, finding.Location, _reportPaths, _reportCampaignOptions, ApplySourceEditAsync);
    }

    private IReadOnlyList<WmlCheckModuleId> SelectedModules() => Array.AsReadOnly(
        new[] { (WmlCheckModuleId.Structure, CheckStructure), (WmlCheckModuleId.References, CheckReferences),
            (WmlCheckModuleId.Formatting, CheckFormatting), (WmlCheckModuleId.Resources, CheckResources) }
            .Where(item => item.Item2).Select(item => item.Item1).ToArray());

    private bool CurrentModulesMatch(WmlCheckReport report) => report.Modules.SequenceEqual(SelectedModules());

    private bool EditScopeMatches(WmlTesterSourceEdit edit) => !_disposed && ReferenceEquals(_report, edit.Report) &&
        edit.CampaignOptions == _campaignOptions && edit.Paths.SequenceEqual(Files.Select(file => file.Path), PathComparer) && CurrentModulesMatch(edit.Report);

    private async Task ApplySourceEditAsync(WmlTesterSourceEdit edit, string text)
    {
        if (IsBusy || !EditScopeMatches(edit))
            throw new InvalidOperationException("The tester scope or report changed. Rerun checks and reopen this finding before applying.");
        using var cancellation = new CancellationTokenSource();
        _sourceSave = cancellation;
        var generation = ++_generation;
        var saved = false;
        _status = "Saving checked WML…";
        _errorMessage = null;
        try
        {
            Notify(generation, nameof(IsBusy), nameof(CanRun), nameof(CanSelectFiles), nameof(CanCancel), nameof(CanEditSelectedSource),
                nameof(Status), nameof(ErrorMessage));
            cancellation.Token.ThrowIfCancellationRequested();
            if (edit.CampaignOptions.CheckWholeCampaign)
                _store.ValidateOwnedSourcePath(edit.Report.TargetScope!.CampaignRootPath, edit.Path);
            var current = await _store.OpenAsync(edit.Path, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!EditScopeMatches(edit) || current is null ||
                !PathComparer.Equals(StandaloneWmlFileStore.NormalizePath(current.Path), edit.Path))
                throw new InvalidOperationException("The tester scope or file identity changed. Rerun checks and reopen the finding.");
            if (!string.Equals(current.Text, edit.Snapshot.Text, StringComparison.Ordinal) || current.Encoding != edit.Snapshot.Encoding)
                throw new InvalidOperationException("The file changed since it was checked. Rerun checks and reopen it to keep the newer changes.");
            if (edit.CampaignOptions.CheckWholeCampaign)
                _store.ValidateOwnedSourcePath(edit.Report.TargetScope!.CampaignRootPath, edit.Path);
            if (!string.Equals(text, edit.Snapshot.Text, StringComparison.Ordinal))
                await _store.SaveAsync(edit.Snapshot, text, cancellation.Token);
            // This exact buffer has been written even if the owner closes or a
            // later check fails. It must not be presented as an unsaved edit.
            edit.Snapshot = edit.Snapshot with { Text = text };
            saved = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException("The tester closed or its file selection changed before the RAW save completed.");
        }
        finally
        {
            if (ReferenceEquals(_sourceSave, cancellation)) _sourceSave = null;
            if (!saved && !_disposed && _generation == generation)
            {
                _status = "RAW save was not completed." + PreviousReportSuffix();
                Notify(generation, nameof(IsBusy), nameof(CanRun), nameof(CanSelectFiles), nameof(CanCancel), nameof(CanEditSelectedSource), nameof(Status));
            }
        }
        var checkGeneration = generation + 1;
        try
        {
            if (_disposed) return;
            if (!EditScopeMatches(edit))
            {
                _status = "File saved. The tester scope changed; run checks again.";
                Notify(generation, nameof(IsBusy), nameof(CanRun), nameof(CanSelectFiles), nameof(CanEditSelectedSource), nameof(Status));
                return;
            }
            var checking = RunAsync();
            checkGeneration = _generation;
            var checkedSuccessfully = await checking;
            if (_disposed || _generation != checkGeneration) return;
            edit.Report = _report!;
            _status = "File saved. " + _status;
            if (!checkedSuccessfully) _status += " Run checks again when ready.";
            Notify(checkGeneration, nameof(Status));
        }
        catch (Exception failure)
        {
            // The write was accepted above. A failed recheck or observer must
            // not make the shared editor present that written buffer as unsaved.
            if (_disposed || _generation != generation && _generation != checkGeneration) return;
            _status = "File saved. Rechecking failed; run checks again when ready.";
            _errorMessage = failure.Message;
            try { Notify(_generation, nameof(Status), nameof(ErrorMessage), nameof(IsBusy), nameof(CanEditSelectedSource)); }
            catch (Exception) { /* Retain the accepted write even if an observer fails again. */ }
        }
    }

    public string DetailsText
    {
        get
        {
            if (_selectedDiagnostic is not { } diagnostic) return string.Empty;
            var location = diagnostic.Location;
            var related = diagnostic.RelatedLocations.Count == 0 ? string.Empty :
                "\n\nRelated locations:\n" + string.Join("\n", diagnostic.RelatedLocations.Select(item =>
                    $"{item.Path}:{item.Line}:{item.Column}"));
            return $"{diagnostic.Code} · {diagnostic.Module} · {diagnostic.Severity} · {diagnostic.Confidence}\n" +
                $"{location.Path}:{location.Line}:{location.Column}\n\n{diagnostic.Message}\n\n{diagnostic.Details}" + related;
        }
    }

    public string ReportSummary => _report is not { } report ? "No report yet." :
        $"{report.Sources.Count} captured file(s) · {report.ErrorCount} error(s) · " +
        $"{report.WarningCount} warning(s) · {report.ContextCount} need context · " +
        $"{report.MacroResolutions.Count} macro match(es) · " +
        $"read {ReadDuration.TotalMilliseconds:0} ms · check {CheckDuration.TotalMilliseconds:0} ms · " +
        $"total {TotalDuration.TotalMilliseconds:0} ms";

    public bool AddFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (_disposed || IsBusy) return false;
        var added = false;
        var errors = new List<string>();
        foreach (var path in paths)
        {
            if (_disposed || IsBusy) break;
            try
            {
                var normalized = StandaloneWmlFileStore.NormalizePath(path);
                if (Files.Any(file => PathComparer.Equals(file.Path, normalized))) continue;
                Files.Add(new WmlTesterFile(normalized));
                added = true;
            }
            catch (Exception failure) when (failure is ArgumentException or IOException or
                UnauthorizedAccessException or NotSupportedException)
            {
                errors.Add(failure.Message);
            }
        }
        if (!_disposed && !IsBusy && errors.Count != 0)
        {
            _errorMessage = string.Join("\n", errors);
            _status = "Some files could not be added." + PreviousReportSuffix();
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(Status));
        }
        return added;
    }

    public bool RemoveFile(WmlTesterFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return !_disposed && !IsBusy && Files.Contains(file) && Files.Remove(file);
    }

    public void ClearFiles()
    {
        if (!_disposed && !IsBusy) Files.Clear();
    }

    public Task<bool> RunAsync()
    {
        if (_disposed) return Task.FromResult(false);
        if (_run is { } running) return running.Completion.Task;
        if (_sourceSave is not null) return Task.FromResult(false);
        if (!CanRun)
        {
            _status = CheckWholeCampaign && !_campaignOptions.IsEnabled ? "Select the campaign main .cfg."
                : !CheckWholeCampaign && Files.Count == 0 ? "Add at least one .cfg file." : "Select at least one check module.";
            _status += PreviousReportSuffix();
            OnPropertyChanged(nameof(Status));
            return Task.FromResult(false);
        }

        var run = new RunState(++_generation, Array.AsReadOnly(Files.Select(file => file.Path).ToArray()),
            SelectedModules(), _campaignOptions, _runner);
        // Register before any synchronous notification can cancel, dispose or
        // request another run. Execute starts after the notification boundary.
        _run = run;
        _errorMessage = null;
        _status = (CheckWholeCampaign ? "Capturing campaign files…" : "Reading files…") + PreviousReportSuffix();
        try
        {
            if (_captureRunner is not null)
                run.Runner = _captureRunner() ?? throw new InvalidOperationException("The check context did not supply a runner.");
            else if (_captureCampaignRunner is not null)
                run.Runner = _captureCampaignRunner(run.CampaignOptions) ?? throw new InvalidOperationException("The campaign context did not supply a runner.");
            else if (_captureCampaignRun is not null)
                run.CapturedRunner = _captureCampaignRun(run.CampaignOptions) ?? throw new InvalidOperationException("The campaign context did not supply a runner.");
        }
        catch (Exception failure)
        {
            try { FinishFailure(run, "Checks failed." + PreviousReportSuffix(), failure.Message); }
            finally { run.Completion.TrySetResult(false); run.Cancellation.Dispose(); }
            return run.Completion.Task;
        }
        try
        {
            Notify(run.Generation, nameof(IsBusy), nameof(CanRun), nameof(CanSelectFiles), nameof(CanCancel),
                nameof(CanEditSelectedSource), nameof(ErrorMessage), nameof(Status));
        }
        finally
        {
            _ = ExecuteAsync(run);
        }
        return run.Completion.Task;
    }

    public void Cancel()
    {
        if (_disposed || _run is not { } run || run.Cancellation.IsCancellationRequested) return;
        run.Cancellation.Cancel();
        _status = "Cancelling…" + PreviousReportSuffix();
        Notify(run.Generation, nameof(CanCancel), nameof(Status));
    }

    private async Task ExecuteAsync(RunState run)
    {
        var completed = false;
        try
        {
            await Task.Yield();
            var token = run.Cancellation.Token;
            token.ThrowIfCancellationRequested();
            var sources = new List<WmlCheckSource>();
            var fileSnapshots = new Dictionary<string, StandaloneWmlFileSnapshot>(PathComparer);
            var readErrors = new List<WmlCheckDiagnostic>();
            var readTimer = Stopwatch.StartNew();
            foreach (var path in run.CampaignOptions.CheckWholeCampaign ? Array.Empty<string>() : run.Paths)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var source = await _store.OpenAsync(path, token);
                    token.ThrowIfCancellationRequested();
                    if (source is null || source.Text is null ||
                        !PathComparer.Equals(StandaloneWmlFileStore.NormalizePath(source.Path), path))
                        throw new InvalidDataException("The file reader returned a different source path or no source text.");
                    sources.Add(new WmlCheckSource(path, source.Text));
                    fileSnapshots.Add(path, source with { Path = path });
                }
                catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException or
                    ArgumentException or NotSupportedException)
                {
                    token.ThrowIfCancellationRequested();
                    readErrors.Add(new WmlCheckDiagnostic("source.read-error", WmlCheckModuleId.Structure,
                        WmlCheckSeverity.Error, WmlCheckConfidence.Proven,
                        new WmlCheckLocation(path, 1, 1, 0, 0), "The source file could not be read.", failure.Message));
                }
            }
            readTimer.Stop();
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(run)) return;
            _status = "Checking captured source…" + PreviousReportSuffix();
            Notify(run.Generation, nameof(Status));
            token.ThrowIfCancellationRequested();
            var captured = Array.AsReadOnly(sources.ToArray());
            var checkTimer = Stopwatch.StartNew();
            var result = await Task.Run(() => run.CapturedRunner is { } capturedRunner
                ? capturedRunner(captured, run.Modules, token) : new WmlTesterRunResult(run.Runner(captured, run.Modules, token)), token);
            var checkedReport = result.Report;
            checkTimer.Stop();
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(run)) return;
            ArgumentNullException.ThrowIfNull(checkedReport);
            var reportSources = run.CampaignOptions.CheckWholeCampaign ? checkedReport.Sources : captured;
            if (run.CampaignOptions.CheckWholeCampaign)
            {
                var targets = reportSources.GroupBy(source => source.Path, PathComparer).ToDictionary(group => group.Key, group => group.ToArray(), PathComparer);
                foreach (var group in result.Files.GroupBy(file => file.Path, PathComparer))
                {
                    token.ThrowIfCancellationRequested();
                    if (group.Count() != 1) throw new InvalidDataException("The campaign capture supplied ambiguous file snapshots.");
                    var file = group.Single();
                    if (targets.TryGetValue(file.Path, out var matches) && matches.Length == 1 && matches[0].Text == file.Text &&
                        checkedReport.TargetScope?.TargetPaths.Contains(file.Path, PathComparer) == true)
                        fileSnapshots.Add(file.Path, file);
                }
            }
            var diagnostics = WmlCheckDiagnosticOrdering.Order(checkedReport.Diagnostics.Concat(readErrors))
                .Select(item => item with { RelatedLocations = Array.AsReadOnly(item.RelatedLocations.ToArray()) })
                .ToArray();
            var report = new WmlCheckReport(Array.AsReadOnly(reportSources.ToArray()), run.Modules, Array.AsReadOnly(diagnostics), checkedReport.Duration)
            {
                ReferenceContext = checkedReport.ReferenceContext,
                TargetScope = checkedReport.TargetScope,
                MacroResolutions = Array.AsReadOnly(checkedReport.MacroResolutions.Select(item => item with
                    { ContextLabels = Array.AsReadOnly(item.ContextLabels.ToArray()) }).ToArray())
            };
            run.Timer.Stop();
            // Install a complete new snapshot before notification. A reference
            // tab can retain a prior buffer without any later file reread.
            _report = report;
            _reportFiles = fileSnapshots;
            _reportPaths = run.Paths;
            _reportCampaignOptions = run.CampaignOptions;
            _diagnostics = new ObservableCollection<WmlCheckDiagnostic>(diagnostics);
            RefreshVisibleDiagnostics(selectFirst: true);
            ReadDuration = readTimer.Elapsed;
            CheckDuration = checkTimer.Elapsed;
            TotalDuration = run.Timer.Elapsed;
            _status = $"Checks completed: {report.ErrorCount} error(s), {report.WarningCount} warning(s), " +
                $"{report.ContextCount} need context.";
            _errorMessage = null;
            _run = null;
            completed = true;
            Notify(run.Generation, nameof(Report), nameof(Diagnostics), nameof(HasReport), nameof(ContextText), nameof(ContextDetails),
                nameof(VisibleDiagnostics), nameof(MatchingDiagnosticCount), nameof(FilterSummary),
                nameof(SelectedDiagnostic), nameof(SelectedDiagnosticSource), nameof(HasSelectedSource), nameof(CanEditSelectedSource),
                nameof(DetailsText), nameof(ReadDuration), nameof(CheckDuration), nameof(TotalDuration),
                nameof(ReportSummary), nameof(Status), nameof(ErrorMessage), nameof(IsBusy),
                nameof(CanRun), nameof(CanSelectFiles), nameof(DisplayFiles), nameof(CanCancel), nameof(CoverageText));
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            FinishFailure(run, "Checks cancelled." + PreviousReportSuffix(), null);
        }
        catch (Exception failure)
        {
            FinishFailure(run, "Checks failed." + PreviousReportSuffix(), failure.Message);
        }
        finally
        {
            // Dispose or a reentrant new run must never be overwritten by this
            // older task's completion.
            run.Completion.TrySetResult(completed);
            run.Cancellation.Dispose();
        }
    }

    private bool IsCurrent(RunState run) => !_disposed && ReferenceEquals(_run, run) &&
        _generation == run.Generation;

    private void FinishFailure(RunState run, string status, string? error)
    {
        if (!IsCurrent(run)) return;
        _run = null;
        _status = status;
        _errorMessage = error;
        Notify(run.Generation, nameof(Status), nameof(ErrorMessage), nameof(IsBusy), nameof(CanRun), nameof(CanSelectFiles), nameof(CanCancel), nameof(CanEditSelectedSource));
    }

    private void Notify(long generation, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (_disposed || _generation != generation) break;
            OnPropertyChanged(property);
            if (property == nameof(CanEditSelectedSource) && !_disposed && _generation == generation)
            {
                OnPropertyChanged(nameof(CanOpenSelectedSource));
                if (!_disposed && _generation == generation) OnPropertyChanged(nameof(SourceActionText));
            }
        }
    }

    public void ResetFilters()
    {
        if (_disposed || (_searchText.Length == 0 && _severityFilter == WmlTesterSeverityFilter.All &&
            _moduleFilter == WmlTesterModuleFilter.All && _confidenceFilter == WmlTesterConfidenceFilter.All)) return;
        _searchText = string.Empty;
        _severityFilter = WmlTesterSeverityFilter.All;
        _moduleFilter = WmlTesterModuleFilter.All;
        _confidenceFilter = WmlTesterConfidenceFilter.All;
        RefreshVisibleDiagnostics();
        NotifyPresentation(_generation, _filterRevision, _report, nameof(SearchText), nameof(SeverityFilter),
            nameof(ModuleFilter), nameof(ConfidenceFilter), nameof(VisibleDiagnostics),
            nameof(MatchingDiagnosticCount), nameof(FilterSummary), nameof(SelectedDiagnostic),
            nameof(SelectedDiagnosticSource), nameof(HasSelectedSource), nameof(CanEditSelectedSource), nameof(DetailsText));
    }

    private void SetEnumFilter<T>(ref T field, T value, string propertyName) where T : struct, Enum
    {
        if (_disposed) return;
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        SetFilter(ref field, value, propertyName);
    }

    private void SetFilter<T>(ref T field, T value, string propertyName)
    {
        if (_disposed || EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        RefreshVisibleDiagnostics();
        NotifyPresentation(_generation, _filterRevision, _report, propertyName, nameof(VisibleDiagnostics),
            nameof(MatchingDiagnosticCount), nameof(FilterSummary), nameof(SelectedDiagnostic),
            nameof(SelectedDiagnosticSource), nameof(HasSelectedSource), nameof(CanEditSelectedSource), nameof(DetailsText));
    }

    private void RefreshVisibleDiagnostics(bool selectFirst = false)
    {
        ++_filterRevision;
        // Filtering uses the completed immutable report, never the live files
        // or checker. Full diagnostics remain available for report exports.
        var visible = _report?.Diagnostics.Where(MatchesFilters).ToArray() ?? [];
        _visibleDiagnostics = new ObservableCollection<WmlCheckDiagnostic>(visible);
        if (selectFirst) _selectedDiagnostic = visible.FirstOrDefault();
        else if (_selectedDiagnostic is not null && !visible.Any(item => ReferenceEquals(item, _selectedDiagnostic)))
            _selectedDiagnostic = null;
    }

    private bool MatchesFilters(WmlCheckDiagnostic diagnostic)
    {
        var severityMatches = _severityFilter switch
        {
            WmlTesterSeverityFilter.Error => diagnostic.Severity == WmlCheckSeverity.Error,
            WmlTesterSeverityFilter.Warning => diagnostic.Severity == WmlCheckSeverity.Warning,
            WmlTesterSeverityFilter.Information => diagnostic.Severity == WmlCheckSeverity.Information,
            _ => true
        };
        var moduleMatches = _moduleFilter switch
        {
            WmlTesterModuleFilter.Structure => diagnostic.Module == WmlCheckModuleId.Structure,
            WmlTesterModuleFilter.References => diagnostic.Module == WmlCheckModuleId.References,
            WmlTesterModuleFilter.Formatting => diagnostic.Module == WmlCheckModuleId.Formatting,
            WmlTesterModuleFilter.Resources => diagnostic.Module == WmlCheckModuleId.Resources,
            _ => true
        };
        var confidenceMatches = _confidenceFilter switch
        {
            WmlTesterConfidenceFilter.Proven => diagnostic.Confidence == WmlCheckConfidence.Proven,
            WmlTesterConfidenceFilter.NeedsContext => diagnostic.Confidence == WmlCheckConfidence.NeedsContext,
            _ => true
        };
        if (!severityMatches || !moduleMatches || !confidenceMatches) return false;
        var query = _searchText.Trim();
        return query.Length == 0 || diagnostic.Code.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Message.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Location.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Details.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            diagnostic.RelatedLocations.Any(location => location.Path.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void NotifyPresentation(long generation, long filterRevision, WmlCheckReport? report, params string[] properties)
    {
        foreach (var property in properties)
        {
            if (_disposed || _generation != generation || _filterRevision != filterRevision ||
                !ReferenceEquals(_report, report)) break;
            OnPropertyChanged(property);
            if (property == nameof(CanEditSelectedSource) && !_disposed && _generation == generation &&
                _filterRevision == filterRevision && ReferenceEquals(_report, report))
            {
                OnPropertyChanged(nameof(CanOpenSelectedSource));
                if (!_disposed && _generation == generation && _filterRevision == filterRevision && ReferenceEquals(_report, report))
                    OnPropertyChanged(nameof(SourceActionText));
            }
        }
    }

    private void SetModule(ref bool field, bool value, string propertyName)
    {
        if (_disposed || IsBusy || field == value) return;
        field = value;
        _status = "Check selection changed." + PreviousReportSuffix();
        _errorMessage = null;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanEditSelectedSource));
        OnPropertyChanged(nameof(CanOpenSelectedSource));
        OnPropertyChanged(nameof(SourceActionText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ErrorMessage));
    }

    private void SetCampaignOptions(WmlTesterCampaignOptions value, params string[] propertyNames)
    {
        if (_disposed || IsBusy || value == _campaignOptions) return;
        _campaignOptions = value;
        _status = "Campaign context changed." + PreviousReportSuffix();
        _errorMessage = null;
        Notify(_generation, propertyNames.Concat(new[] { nameof(CanEditSelectedSource), nameof(CanOpenSelectedSource), nameof(SourceActionText), nameof(CanRun), nameof(CanSelectFiles), nameof(DisplayFiles), nameof(Status), nameof(ErrorMessage) }).ToArray());
    }

    private void FilesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_disposed) return;
        if (IsBusy)
        {
            // Public collections can be mutated by a binding. Such a change
            // cannot alter the immutable run scope or publish a stale result.
            Cancel();
            _sourceSave?.Cancel();
            return;
        }
        _status = "File selection changed." + PreviousReportSuffix();
        _errorMessage = null;
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanSelectFiles));
        OnPropertyChanged(nameof(DisplayFiles));
        OnPropertyChanged(nameof(CanEditSelectedSource));
        OnPropertyChanged(nameof(CanOpenSelectedSource));
        OnPropertyChanged(nameof(SourceActionText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ErrorMessage));
    }

    private string PreviousReportSuffix() => HasReport ? " Showing the previous report." : string.Empty;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_generation;
        var run = _run;
        _run = null;
        Files.CollectionChanged -= FilesChanged;
        run?.Cancellation.Cancel();
        _sourceSave?.Cancel();
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanSelectFiles));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanEditSelectedSource));
        OnPropertyChanged(nameof(CanOpenSelectedSource));
    }

    private sealed class RunState(long generation, IReadOnlyList<string> paths,
        IReadOnlyList<WmlCheckModuleId> modules,
        WmlTesterCampaignOptions campaignOptions,
        Func<IReadOnlyList<WmlCheckSource>, IReadOnlyList<WmlCheckModuleId>, CancellationToken, WmlCheckReport> runner)
    {
        public long Generation { get; } = generation;
        public IReadOnlyList<string> Paths { get; } = paths;
        public IReadOnlyList<WmlCheckModuleId> Modules { get; } = modules;
        public WmlTesterCampaignOptions CampaignOptions { get; } = campaignOptions;
        public Func<IReadOnlyList<WmlCheckSource>, IReadOnlyList<WmlCheckModuleId>, CancellationToken, WmlCheckReport> Runner { get; set; } = runner;
        public WmlTesterCapturedCheck? CapturedRunner { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stopwatch Timer { get; } = Stopwatch.StartNew();
    }
}
