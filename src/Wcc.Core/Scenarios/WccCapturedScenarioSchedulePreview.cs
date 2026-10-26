using System.Text;
using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

/// <summary>Effective initial time slots from one exact evaluated scenario. Event
/// actions and Lua are not run; merged display values never supply edit ordinals.</summary>
public sealed class WccCapturedScenarioSchedulePreview
{
    private WccCapturedScenarioSchedulePreview(ScenarioInitialScheduleProjection schedule,
        IReadOnlyList<IReadOnlyList<WmlStaticSource>> imageSources)
    { Schedule = schedule; ImageSources = imageSources; }
    public ScenarioInitialScheduleProjection Schedule { get; }
    /// <summary>Exact value/definition/caller sources for each displayed image, for host freshness checks.</summary>
    public IReadOnlyList<IReadOnlyList<WmlStaticSource>> ImageSources { get; }
    public bool AuthorizesPersistence => false;

    public static WccCapturedScenarioSchedulePreview? Create(WmlStaticSourceRange scenarioRange,
        WmlStaticScalarProjection projection)
    {
        ArgumentNullException.ThrowIfNull(scenarioRange);
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.SourceEvaluation is not { IsComplete: true } evaluation ||
            evaluation.Diagnostics.Any(item => item.IsBlocking) || projection.Diagnostics.Count != 0) return null;
        return CreateReadOnly(scenarioRange, projection);
    }

    public static WccCapturedScenarioSchedulePreview? Create(WmlStaticSourceRange scenarioRange,
        WmlScenarioInspectionResult inspection)
    {
        ArgumentNullException.ThrowIfNull(scenarioRange);
        ArgumentNullException.ThrowIfNull(inspection);
        if (!inspection.IsComplete || inspection.Evaluation.IsComplete ||
            !ReferenceEquals(inspection.Projection.SourceEvaluation, inspection.Evaluation) ||
            inspection.Evaluation.Diagnostics.Any(item => item.IsBlocking) || inspection.Projection.Diagnostics.Count != 0) return null;
        return CreateReadOnly(scenarioRange, inspection.Projection);
    }

    private static WccCapturedScenarioSchedulePreview? CreateReadOnly(WmlStaticSourceRange range,
        WmlStaticScalarProjection projection)
    {
        if (WccCapturedScenarioPreview.FindLiteralOwner(range, projection) is not { StructureKnown: true } scenario) return null;
        var owners = scenario.DirectChildren.Where(owner => owner.TagName == "time").Take(4097).ToArray();
        if (owners.Length > 4096) return null;
        var times = new List<ScenarioInitialTimeSlot>();
        var imageSources = new List<IReadOnlyList<WmlStaticSource>>();
        var totalLength = 0;
        foreach (var owner in owners)
        {
            if (!owner.StructureKnown || owner.DirectChildren.Count != 0 || owner.Fields.Count > 256) return null;
            var raw = new StringBuilder("[time]\n");
            foreach (var field in owner.Fields.Values.OrderBy(field => field.Contributions.LastOrDefault()?.AssignmentOutputOffset))
            {
                if (field.ValueText is not { } value) return null;
                raw.Append("    ").Append(field.Key).Append('=').Append(value).Append('\n');
                if (raw.Length > 2 * 1024 * 1024 - totalLength) return null;
            }
            raw.Append("[/time]");
            if (raw.Length > 2 * 1024 * 1024 - totalLength) return null;
            totalLength += raw.Length;
            string? Value(string key) => owner.ResolveField(key) is { State: WmlStaticScalarResolutionState.Known, ValueText: { } value }
                ? WccScenarioFieldValueCodec.DecodeLiteral(value) : null;
            times.Add(new(times.Count, raw.ToString(), Value("id"), Value("name"), Value("image"), Value("lawful_bonus"), false)
            { Red = Value("red"), Green = Value("green"), Blue = Value("blue") });
            var image = owner.Fields.GetValueOrDefault("image")?.WinningContribution;
            imageSources.Add(image is null ? [] : Array.AsReadOnly(image.ValueOrigins.Select(origin => origin.Source)
                .Concat(image.ExpansionStack.Concat(image.ValueExpansionStack).SelectMany(frame =>
                    frame.InvocationOrigins.Select(origin => origin.Source).Prepend(frame.Definition.Source)))
                .Append(range.Source).Distinct().ToArray()));
        }
        return new(ScenarioInitialScheduleEditor.CapturedReadOnly(range, scenario, times), Array.AsReadOnly(imageSources.ToArray()));
    }
}
