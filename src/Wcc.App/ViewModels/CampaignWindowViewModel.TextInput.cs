using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;

namespace Wcc.App.ViewModels;

public partial class CampaignWindowViewModel
{
    // One UI buffer for scalar fields, difficulty cards and path rows. Apply still
    // uses their existing typed draft APIs and the common checked Save pipeline.
    private sealed record CampaignTextInput(CampaignDraftBinding Binding, string? Text,
        Func<bool> IsCurrent, Func<string?> Read, Action<string?> Apply, Action Refresh, long Version);
    private readonly Dictionary<(object Owner, string Slot), CampaignTextInput> _campaignTextInputs = new();
    private long _campaignTextInputVersion;
    public bool HasCampaignBufferedText => _campaignTextInputs.Count != 0;

    internal bool TryGetCampaignTextInput(WccCampaignFieldEditorVm field, bool booleanOverride, out string? text) =>
        TryGetCampaignTextInput(field, booleanOverride ? "boolean" : "value", out text);
    internal bool TryGetCampaignTextInput(object owner, string slot, out string? text)
    {
        lock (_campaignFieldGate)
        {
            if (_campaignTextInputs.TryGetValue((owner, slot), out var input))
            { text = input.Text; return true; }
            text = null; return false;
        }
    }
    internal bool BufferCampaignTextInput(WccCampaignFieldEditorVm field, string? text, bool booleanOverride) =>
        BufferCampaignTextInput(field, booleanOverride ? "boolean" : "value", booleanOverride ? text ?? "" : text,
            () => _campaignFields.Contains(field) && field.CanEdit,
            () => booleanOverride ? field.BooleanOverrideText : field.Value,
            value => { if (booleanOverride) field.BooleanOverrideText = value; else field.Value = value; },
            field.RefreshBufferedText);

    internal bool BufferCampaignTextInput(object owner, string slot, string? text, Func<bool> isCurrent,
        Func<string?> read, Action<string?> apply, Action refresh)
    {
        lock (_campaignFieldGate)
        {
            if (IsCampaignFieldSaveBusy || _campaignFieldSession.IsBusy) return false;
            var key = (owner, slot);
            if (_campaignTextInputs.TryGetValue(key, out var existing))
            {
                if (existing.Text == text) return false;
                _campaignTextInputs[key] = existing with { Text = text, Version = ++_campaignTextInputVersion };
                return true;
            }
            if (_campaignProjectionBinding is not { } binding || !IsCurrentCampaignBinding(binding) || !isCurrent()) return false;
            _campaignDraftBinding ??= binding;
            _campaignTextInputs.Add(key, new(binding, text, isCurrent, read, apply, refresh, ++_campaignTextInputVersion));
            // Dirty-state changes once. Keystrokes do not rebuild pending rows, routes,
            // chapter/dependency controls or all other field bindings.
            RaiseCampaignTextInputState();
            return true;
        }
    }

    public bool CommitCampaignTextInputs(out string? problem) => CommitCampaignTextInputs(null, out problem);
    internal bool CommitCampaignTextInputs(object? onlyOwner, out string? problem)
    {
        lock (_campaignFieldGate)
        {
            problem = null;
            var inputs = _campaignTextInputs.Where(pair => onlyOwner is null || ReferenceEquals(pair.Key.Owner, onlyOwner)).ToArray();
            foreach (var pair in inputs)
            {
                var key = pair.Key; var input = pair.Value;
                if (!_campaignTextInputs.TryGetValue(key, out var current) || !ReferenceEquals(current, input))
                { problem = "The text changed while it was being committed. It remains open."; return false; }
                if (!IsCurrentCampaignBinding(input.Binding) || !input.IsCurrent())
                { problem = "The campaign source or editing context changed. Typed text remains open; discard it or restore its context."; return false; }
                _campaignTextInputs.Remove(key);
                _campaignTextInputVersion++;
                try { input.Apply(input.Text); }
                catch
                {
                    // A failed draft callback must not consume the user's input.
                    if (!_campaignTextInputs.ContainsKey(key)) _campaignTextInputs.Add(key, input);
                    RaiseCampaignTextInputState();
                    throw;
                }
                if (!string.Equals(input.Read(), input.Text, StringComparison.Ordinal) && !_campaignTextInputs.ContainsKey(key))
                {
                    _campaignTextInputs.Add(key, input);
                    problem = "The text could not be staged in its original field. It remains open.";
                    RaiseCampaignTextInputState(); return false;
                }
                input.Refresh();
            }
            if (!HasCampaignSourceDrafts) _campaignDraftBinding = null;
            if (inputs.Length != 0) RaiseCampaignTextInputState();
            if (_campaignTextInputs.Any(pair => onlyOwner is null || ReferenceEquals(pair.Key.Owner, onlyOwner)))
            { problem = "Newer typed text remains open. Save again to include it."; return false; }
            return true;
        }
    }

    internal void ClearCampaignTextInput(WccCampaignFieldEditorVm field)
    {
        lock (_campaignFieldGate)
        {
            var keys = _campaignTextInputs.Keys.Where(key => ReferenceEquals(key.Owner, field)).ToArray();
            foreach (var key in keys) _campaignTextInputs.Remove(key);
            if (keys.Length == 0) return;
            _campaignTextInputVersion++;
            field.RefreshBufferedText(); RaiseCampaignTextInputState();
        }
    }
    private void DiscardCampaignTextInputs()
    {
        var inputs = _campaignTextInputs.Values.ToArray();
        _campaignTextInputs.Clear(); _campaignTextInputVersion++;
        foreach (var input in inputs) input.Refresh();
        if (inputs.Length != 0) RaiseCampaignTextInputState();
    }
    private void RaiseCampaignTextInputState()
    {
        OnPropertyChanged(nameof(HasCampaignBufferedText));
        OnPropertyChanged(nameof(HasCampaignSourceDrafts));
        OnPropertyChanged(nameof(CanDiscardCampaignFieldDrafts));
    }
}
