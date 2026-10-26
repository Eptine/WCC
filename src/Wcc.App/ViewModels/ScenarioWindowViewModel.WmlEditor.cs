using System;
using System.Collections.Generic;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    private sealed record RawEditorReferenceSnapshot(WmlStaticSource Source,
        WmlEditorReferenceEvidence Evidence, long Generation, string ContextKey);
    private RawEditorReferenceSnapshot? _rawEditorReferenceSnapshot;

    internal Func<bool> CaptureRawEditorEnvironmentGuard()
    {
        var profile = _wesnothSourceProfileIdentity;
        var install = _wesnothSourceInstallPath;
        var addons = _wesnothSourceAddonsPath;
        var readOnly = IsReadOnly;
        return () => profile == _wesnothSourceProfileIdentity &&
            install == _wesnothSourceInstallPath && addons == _wesnothSourceAddonsPath && readOnly == IsReadOnly;
    }

    internal WmlEditorReferenceContext CreateRawEditorReferenceContext(string text, string? sourcePath,
        IEnumerable<WmlEditorDefinitionCandidate> candidates, WmlStaticSourceRange? requestedOrigin = null)
    {
        lock (_wccInitialFieldRefreshGate)
        {
            var capture = _rawEditorReferenceSnapshot;
            bool IsCurrent() => ReferenceEquals(capture, _rawEditorReferenceSnapshot) &&
                capture is not null && capture.Generation == _staticInitialValuesGeneration &&
                !_hasAuthoredSourceChanges && !_externalSourceChangesPending &&
                capture.ContextKey == _evaluationContext.SemanticKey &&
                PathsEqual(capture.Source.Path, _currentSourcePath ?? LoadedPath) &&
                capture.Source.Text == _pristineLoadedSourceText;
            if (!IsCurrent()) return new(text, candidates: candidates);
            var origin = requestedOrigin;
            if (origin is null && PathsEqual(sourcePath, capture!.Source.Path))
            {
                var first = capture.Source.Text.IndexOf(text, StringComparison.Ordinal);
                if (text.Length > 0 && first >= 0 &&
                    capture.Source.Text.IndexOf(text, first + 1, StringComparison.Ordinal) < 0)
                    origin = new(capture.Source, first, text.Length);
            }
            return new(text, origin, capture!.Evidence, candidates, IsCurrent);
        }
    }
}
