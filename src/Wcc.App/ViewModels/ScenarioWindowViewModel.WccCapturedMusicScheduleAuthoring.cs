using System.Collections.Generic;
using System.Linq;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public partial class ScenarioWindowViewModel
{
    // A complete original publication may describe an invocation in its actual
    // include/define chronology. Inspection receipts remain display-only.
    private WmlStaticScalarProjection? _musicScheduleCapturedProjection;
    private bool _musicUsesCapturedAuthoring;
    private bool _scheduleUsesCapturedAuthoring;

    private bool NeedsCapturedMusicScheduleSettings =>
        _wccMusicChanged && _musicUsesCapturedAuthoring || HasPendingWccScheduleEdit && _scheduleUsesCapturedAuthoring;

    private WmlStaticScalarProjection? CurrentMusicScheduleAuthoringProjection =>
        !IsBoundedScenarioInspection && _publishedScenarioInspection is null &&
        HasCurrentStaticScalarProjection && _staticScenarioTopologyProjection is { } projection &&
        _staticEvaluationCache.CompletedValues().Any(result => result.Value.IsComplete &&
            ReferenceEquals(result.Value.Projection, projection)) &&
        _staticScenarioOwner is not null && SelectedScenario?.Document is { } document &&
        _pristineLoadedSourceText is { } text && document.OriginalText == text &&
        _currentScenarioAsset?.RawTextSnapshot == text && !_hasAuthoredSourceChanges && !_externalSourceChangesPending
            ? projection : null;

    private WmlStaticSourceRange? MusicScheduleOriginalRange =>
        SelectedScenario?.Document is { } document && (_currentSourcePath ?? LoadedPath) is { } path &&
        _pristineLoadedSourceText is { } text && document.OriginalText == text
            ? new(new(path, text), document.BlockSpan.StartIndex,
                document.BlockSpan.EndIndexExclusive - document.BlockSpan.StartIndex) : null;

    private ScenarioInitialMusicProjection ProjectWccMusicAuthoring(string raw,
        IReadOnlyList<WmlMacroExpansionDefinition> definitions)
    {
        var ordinary = ScenarioInitialMusicEditor.Project(raw, definitions);
        var captured = CapturedMusicAuthoring(_musicScheduleCapturedProjection, raw, definitions);
        _musicUsesCapturedAuthoring = PreferCapturedMusic(ordinary, captured);
        return _musicUsesCapturedAuthoring ? captured! : ordinary;
    }

    private ScenarioInitialMusicProjection? CapturedMusicAuthoring(WmlStaticScalarProjection? projection, string? raw,
        IReadOnlyList<WmlMacroExpansionDefinition> definitions) => projection is not null &&
        MusicScheduleOriginalRange is { } range && range.Text == raw
            ? ScenarioInitialMusicEditor.Project(range, projection, definitions) : null;

    private static bool PreferCapturedMusic(ScenarioInitialMusicProjection ordinary, ScenarioInitialMusicProjection? captured) =>
        captured is not null && (!ordinary.CanEdit || captured.HasCapturedUnrepresentedMusic ||
            captured.CanEdit && (!ordinary.Tracks.Select(track => track.Name).SequenceEqual(captured.Tracks.Select(track => track.Name)) ||
                ordinary.HasIntro != captured.HasIntro || ordinary.LetIntroFinish != captured.LetIntroFinish ||
                ordinary.Shuffle != captured.Shuffle || ordinary.DefaultIntroTrackName != captured.DefaultIntroTrackName));

    private ScenarioInitialScheduleProjection ProjectWccScheduleAuthoring(string raw,
        IReadOnlyList<WmlMacroExpansionDefinition> definitions) => SelectWccScheduleAuthoring(raw, definitions).Projection;

    private (ScenarioInitialScheduleProjection Projection, bool UsesCaptured) SelectWccScheduleAuthoring(string raw,
        IReadOnlyList<WmlMacroExpansionDefinition> definitions)
    {
        var ordinary = ScenarioInitialScheduleEditor.Project(raw, definitions, GetWccScenarioTextDomain());
        var usesCaptured = (!ordinary.CanCompose ||
                CapturedScheduleDiffers(_musicScheduleCapturedProjection, raw, ordinary)) &&
            _musicScheduleCapturedProjection is not null && MusicScheduleOriginalRange is { } range && range.Text == raw;
        var projection = usesCaptured
            ? ScenarioInitialScheduleEditor.Project(MusicScheduleOriginalRange!, _musicScheduleCapturedProjection!, definitions,
                GetWccScenarioTextDomain())
            : ordinary;
        return (projection, usesCaptured);
    }

    private bool CapturedScheduleDiffers(WmlStaticScalarProjection? projection, string? raw,
        ScenarioInitialScheduleProjection? ordinary) => projection is not null && ordinary is not null &&
        MusicScheduleOriginalRange is { } range && range.Text == raw &&
        WccCapturedScenarioSchedulePreview.Create(range, projection) is { } captured &&
        !SameScheduleTimeline(captured.Schedule, ordinary);

    private WccScenarioInitialSettingsEdit CreateWccMusicScheduleSettings(WmlStaticSourceRange range)
    {
        var settings = new WccScenarioInitialSettingsEdit(range, _musicScheduleDefinitions, GetWccScenarioTextDomain(),
            _wccMusicChanged ? new WccScenarioInitialMusicEdit(_wccMusicRows.Select(row => row.Track),
                _wccMakeFirstTrackIntro, _wccLetIntroFinish) : null,
            HasPendingWccScheduleEdit ? _wccScheduleEdit : null);
        return NeedsCapturedMusicScheduleSettings && _musicScheduleCapturedProjection is { } projection
            ? settings.WithCapturedProjection(projection) : settings;
    }
}
