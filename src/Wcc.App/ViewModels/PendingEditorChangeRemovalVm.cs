using System;

namespace Wcc.App.ViewModels;

/// <summary>A short-lived discard action over an existing editor draft; never a second save queue.</summary>
public sealed class PendingEditorChangeRemovalVm
{
    private readonly Func<string?> _problem;
    private readonly Func<bool> _remove;
    internal PendingEditorChangeRemovalVm(PendingEditorChange change, Func<string?> problem, Func<bool> remove)
    { Change = change; _problem = problem; _remove = remove; }
    public PendingEditorChange Change { get; }
    public string Scope => Change.Scope;
    public string Label => Change.Label;
    public string Detail => Change.Detail;
    public string Summary => Change.Summary;
    public string? RemovalProblem => _problem();
    public bool CanRemove => RemovalProblem is null;
    public bool Remove() => _remove();
}

internal sealed record PendingChangeRemovalProposal(PendingEditorChange Change, Func<string?> Problem, Func<bool> Remove);
