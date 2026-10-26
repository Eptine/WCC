using Wcc.Core.Projects;

namespace Wcc.Core.References;

public sealed class WccAssetRegistry
{
    private readonly Dictionary<string, WccAssetRegistryEntry> _byKey =
        new(StringComparer.OrdinalIgnoreCase);

    public List<WccAssetRegistryEntry> Entries { get; } = new();

    public void Clear()
    {
        _byKey.Clear();
        Entries.Clear();
    }

    public void AddReference(WccAssetReference reference, WccSourceOrigin origin)
    {
        if (reference is null) throw new ArgumentNullException(nameof(reference));
        if (string.IsNullOrWhiteSpace(reference.LookupPath))
            return;

        var key = BuildKey(reference.Kind, reference.LookupPath);

        if (!_byKey.TryGetValue(key, out var entry))
        {
            entry = new WccAssetRegistryEntry
            {
                Kind = reference.Kind,
                LookupPath = reference.LookupPath!
            };

            _byKey.Add(key, entry);
            Entries.Add(entry);
        }

        entry.Origins.Add(origin);
        entry.References.Add(reference);
    }

    public IEnumerable<WccAssetRegistryEntry> FindByLookupPath(string lookupPath)
    {
        if (string.IsNullOrWhiteSpace(lookupPath))
            return Enumerable.Empty<WccAssetRegistryEntry>();

        return Entries.Where(e => string.Equals(e.LookupPath, lookupPath, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildKey(WccAssetReferenceKind kind, string lookupPath)
        => $"{(int)kind}|{lookupPath}";
}