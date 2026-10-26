using System.Collections.Generic;

namespace Wcc.App.ViewModels;

// Closing the dialog without choosing an action must keep the source open.
public enum PendingNavigationDecision { Cancel, Save, Discard }

public sealed record PendingEditorChange(string Scope, string Label, string Detail)
{
    public string Summary => string.IsNullOrWhiteSpace(Detail) ? Label : Label + ": " + Detail;
}

public sealed record PendingNavigationRequest(IReadOnlyList<PendingEditorChange> Changes)
{
    public string Description => "Save the pending changes before leaving this source, discard them, or keep editing.";
}
