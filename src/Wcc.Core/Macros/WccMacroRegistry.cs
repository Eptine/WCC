namespace Wcc.Core.Macros;

public sealed class WccMacroRegistry
{
    private readonly Dictionary<string, WccMacroRegistryEntry> _byKey =
        new(StringComparer.OrdinalIgnoreCase);

    public List<WccMacroRegistryEntry> Entries { get; } = new();

    public void Clear()
    {
        _byKey.Clear();
        Entries.Clear();
    }

    public WccMacroRegistryEntry GetOrCreate(string name, WccMacroScopeInfo scope)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Macro name must be set.", nameof(name));

        var key = BuildKey(name, scope);

        if (_byKey.TryGetValue(key, out var existing))
            return existing;

        var created = new WccMacroRegistryEntry
        {
            Name = name,
            ScopeKind = scope.ScopeKind,
            ScopeName = scope.ScopeName
        };

        _byKey.Add(key, created);
        Entries.Add(created);
        return created;
    }

    public IEnumerable<WccMacroRegistryEntry> FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Enumerable.Empty<WccMacroRegistryEntry>();

        return Entries.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildKey(string name, WccMacroScopeInfo scope)
        => $"{name}|{scope.ScopeKind}|{scope.ScopeName}";
}