using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>A cancellable display preview. It carries no source-edit authority.</summary>
public sealed class WccFieldImagePreviewVm : ViewModelBase, IDisposable
{
    private CancellationTokenSource? _cancellation;
    private long _version;
    private Bitmap? _bitmap;
    private string? _resolvedPath;
    private string? _expression;
    private string? _inheritedValue;
    private string _status = "No image is available.";
    private bool _isInherited;
    private bool _isLoading;
    internal object? RequestIdentity { get; private set; }
    internal Task LoadingTask { get; private set; } = Task.CompletedTask;

    public Bitmap? Bitmap => _bitmap;
    public bool HasBitmap => Bitmap is not null;
    public bool HasNoBitmap => !HasBitmap;
    public string? ResolvedPath => _resolvedPath;
    public string? Expression => _expression;
    public string? InheritedValue => _inheritedValue;
    public string Status => _status;
    public bool IsInherited => _isInherited;
    public bool IsLoading => _isLoading;
    public string SourceLabel => IsInherited ? "Unit type preview" : "Local preview";

    internal (long Version, CancellationToken Token, TaskCompletionSource Completion) Begin(object identity)
    {
        Cancel();
        RequestIdentity = identity;
        var cancellation = _cancellation = new();
        var version = _version;
        var token = cancellation.Token;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LoadingTask = completion.Task;
        _bitmap?.Dispose();
        _bitmap = null;
        _resolvedPath = null;
        _expression = null;
        _inheritedValue = null;
        _isInherited = false;
        _isLoading = true;
        _status = "Loading image preview…";
        RaiseChanged();
        return (version, token, completion);
    }

    internal bool Publish(long version, Bitmap? bitmap, string? path, string? expression,
        string status, bool inherited, string? inheritedValue = null)
    {
        if (version != _version || _cancellation?.IsCancellationRequested != false)
        {
            bitmap?.Dispose();
            return false;
        }
        var previous = _bitmap;
        _bitmap = bitmap;
        _resolvedPath = path;
        _expression = expression;
        _inheritedValue = inheritedValue;
        _status = status;
        _isInherited = inherited;
        _isLoading = false;
        if (!ReferenceEquals(previous, bitmap)) previous?.Dispose();
        RaiseChanged();
        return true;
    }

    internal void Cancel()
    {
        _version++;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        RequestIdentity = null;
        _isLoading = false;
    }

    public void Dispose()
    {
        Cancel();
        _bitmap?.Dispose();
        _bitmap = null;
        _resolvedPath = null;
        _expression = null;
        _inheritedValue = null;
        _isInherited = false;
        _status = "No image is available.";
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        foreach (var name in new[] { nameof(Bitmap), nameof(HasBitmap), nameof(HasNoBitmap),
                     nameof(ResolvedPath), nameof(Expression), nameof(InheritedValue), nameof(Status), nameof(IsInherited),
                     nameof(IsLoading), nameof(SourceLabel) }) OnPropertyChanged(name);
    }
}
