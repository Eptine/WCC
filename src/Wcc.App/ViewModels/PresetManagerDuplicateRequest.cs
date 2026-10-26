using System;
using System.Threading.Tasks;

namespace Wcc.App.ViewModels;

/// <summary>A detached dialog draft bound to its exact original selection and copy route.</summary>
public sealed class PresetManagerDuplicateRequest
{
    private readonly Func<string, Func<bool>, Task> _save;
    internal PresetManagerDuplicateRequest(string initialText, bool copiesManagedMacro,
        Func<string, Func<bool>, Task> save)
    { InitialText = initialText; CopiesManagedMacro = copiesManagedMacro; _save = save; }
    internal PresetManagerDuplicateRequest(string initialText, Action<string> save)
    {
        InitialText = initialText; LegacySave = save;
        _save = (text, current) =>
        {
            if (!current()) throw new InvalidOperationException("The copy draft changed. Reopen Clone to WCC.");
            save(text); return Task.CompletedTask;
        };
    }
    public string InitialText { get; }
    public bool CopiesManagedMacro { get; }
    internal Action<string>? LegacySave { get; }
    public Task SaveAsync(string rawText, Func<bool>? draftIsCurrent = null) =>
        _save(rawText, draftIsCurrent ?? (() => true));
}
