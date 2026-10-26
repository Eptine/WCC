using System.Collections.ObjectModel;

namespace Wcc.Core.Scenarios;

/// <summary>Reusable unit instance fields, without a side, map position or difficulty. Never source-write authority.</summary>
public sealed class WccScenarioUnitTemplate
{
    public WccScenarioUnitTemplate(IEnumerable<KeyValuePair<string, string>> values, bool? isLoyal = null)
    {
        var fields = WccScenarioUnitInstanceSpec.CaptureValues(values, requirePlacement: false);
        if (fields.ContainsKey("x") || fields.ContainsKey("y") || fields.ContainsKey("side"))
            throw new ArgumentException("A unit preset has no map placement; choose its side and coordinates when using it.", nameof(values));
        if (!fields.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Give this reusable character a nonempty unit ID.", nameof(values));
        Values = new ReadOnlyDictionary<string, string>(fields);
        IsLoyal = isLoyal;
    }
    public IReadOnlyDictionary<string, string> Values { get; }
    public bool? IsLoyal { get; }
    public bool AuthorizesPersistence => false;
}