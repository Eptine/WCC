using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private int _fieldDisplayDeferralDepth;
    private bool _fieldDisplayRefreshPending;
    private bool _fieldEditorRefreshPending;
    private bool _musicScheduleDisplayRefreshPending;
    private bool _editingStateDisplayRefreshPending;
    private bool _flushingFieldDisplay;
    private readonly Dictionary<string, WccScenarioFieldRoute> _inlineDraftDisplayRoutes = new(StringComparer.Ordinal);

    // Only display publication is deferred. Source changes still revoke checked
    // edit sessions and invalidate routes before any observable input changes.
    private IDisposable DeferFieldDisplayRefresh()
    {
        lock (_wccInitialFieldRefreshGate) _fieldDisplayDeferralDepth++;
        return new FieldDisplayRefreshScope(this);
    }

    private void RequestFieldDisplayRefresh(bool full)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            if (full) _fieldDisplayRefreshPending = true;
            else _fieldEditorRefreshPending = true;
            FlushFieldDisplayRefresh();
        }
    }

    private void RequestWccInlineDraftDisplayRefresh(WccScenarioFieldRoute route)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            _inlineDraftDisplayRoutes[WccInlineGroupIdentity(route)] = route;
            FlushFieldDisplayRefresh();
        }
    }
    private void RequestMusicScheduleDisplayRefresh()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            _musicScheduleDisplayRefreshPending = true;
            FlushFieldDisplayRefresh();
        }
    }

    private void RequestEditingStateDisplayRefresh()
    {
        lock (_wccInitialFieldRefreshGate)
        {
            _editingStateDisplayRefreshPending = true;
            FlushFieldDisplayRefresh();
        }
    }

    private void FlushFieldDisplayRefresh()
    {
        // Called under the existing collection gate, never around an arbitrary
        // RunUiSync delegate. Headless callers can synchronize on another thread.
        if (_fieldDisplayDeferralDepth != 0 || _flushingFieldDisplay) return;
        _flushingFieldDisplay = true;
        var refreshUnitRows = false;
        try
        {
            while (_fieldDisplayDeferralDepth == 0 &&
                   (_fieldDisplayRefreshPending || _fieldEditorRefreshPending || _musicScheduleDisplayRefreshPending ||
                    _editingStateDisplayRefreshPending || _inlineDraftDisplayRoutes.Count != 0))
            {
                // Session invalidation and its read-only/recovery notifications
                // already happened synchronously. Only repeat capability display
                // updates wait for the selected owner and hydration to settle.
                if (_editingStateDisplayRefreshPending)
                {
                    refreshUnitRows = true;
                    _editingStateDisplayRefreshPending = false;
                    try { RaiseEditingStateChangedCore(); }
                    catch
                    {
                        _editingStateDisplayRefreshPending = true;
                        throw;
                    }
                }
                if (_fieldDisplayDeferralDepth != 0) continue;
                // Music capability publication also requests field rows. Publish
                // it first so those requests join this turn's settled field view.
                if (_musicScheduleDisplayRefreshPending)
                {
                    refreshUnitRows = true;
                    _musicScheduleDisplayRefreshPending = false;
                    try { RefreshWccMusicScheduleEditorsNow(); }
                    catch
                    {
                        _musicScheduleDisplayRefreshPending = true;
                        throw;
                    }
                }
                if (_fieldDisplayDeferralDepth != 0) continue;
                if (!_fieldDisplayRefreshPending && !_fieldEditorRefreshPending)
                {
                    var routes = _inlineDraftDisplayRoutes.Values.ToArray();
                    _inlineDraftDisplayRoutes.Clear();
                    try { RefreshWccInlineDraftFieldEditorsNow(routes); }
                    catch
                    {
                        foreach (var route in routes) _inlineDraftDisplayRoutes[WccInlineGroupIdentity(route)] = route;
                        throw;
                    }
                    continue;
                }
                // A broader publication already includes every queued draft row.
                _inlineDraftDisplayRoutes.Clear();
                refreshUnitRows = true;
                var full = _fieldDisplayRefreshPending;
                _fieldDisplayRefreshPending = false;
                _fieldEditorRefreshPending = false;
                try
                {
                    if (full) RaiseInitialScalarFieldsChangedCore();
                    else RefreshWccInitialFieldEditorsNow();
                }
                catch
                {
                    // A callback may throw. Leave unfinished publication pending
                    // for the next request without trapping the editor in a scope.
                    if (full) _fieldDisplayRefreshPending = true;
                    else _fieldEditorRefreshPending = true;
                    throw;
                }
            }
        }
        finally
        {
            _flushingFieldDisplay = false;
            if (refreshUnitRows) RefreshWccInitialUnitRows();
            // Broader capability observers saw the temporary guard above. A
            // targeted draft did not notify them and needs no restoration pass.
            if (refreshUnitRows)
            {
                OnPropertyChanged(nameof(CanAddWccDialogueGraph));
                OnPropertyChanged(nameof(CanAddWccEventFromPreset));
            }
        }
    }

    private sealed class FieldDisplayRefreshScope(ScenarioWindowViewModel owner) : IDisposable
    {
        private ScenarioWindowViewModel? _owner = owner;

        public void Dispose()
        {
            var current = _owner;
            _owner = null;
            if (current is null) return;
            lock (current._wccInitialFieldRefreshGate)
            {
                current._fieldDisplayDeferralDepth--;
                current.FlushFieldDisplayRefresh();
            }
        }
    }
}
