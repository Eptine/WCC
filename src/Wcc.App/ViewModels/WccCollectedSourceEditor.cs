using System;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels;

// UI preparation only. The Core coordinator owns the fresh review and opaque write ticket.
internal sealed record WccCollectedSourceEditor(
    WccScenarioFieldEditSessionState State,
    WccProjectSourceEditRequest Request,
    Func<bool> IsCurrent,
    Action RevokeCompetingAuthority,
    Action<string> Begin,
    Func<string, bool> Publish,
    Action<WccProjectSourceEditCandidate> RememberSelection,
    Action End);
