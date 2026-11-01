using System.Globalization;
using System.Text;
using System.Text.Json;
using Wcc.Core.Wml;

namespace Wcc.Core.Library;

/// <summary>An exact physical unit occurrence; navigation evidence, never an edit route.</summary>
public sealed record WccUnitSidebarLookup(string Path, string ContentHash, int Start, int Length);

/// <summary>Detached derived display data. Unavailable preserves an attempted null summary.</summary>
public sealed record WccUnitSidebarCachedSummary(WccUnitSidebarLookup Lookup, bool Available,
    string? EffectiveRace, string? PreviewPath, bool RequiresCapturedImages);

/// <summary>A transient fresh-capture request. It never authorizes source or Library writes.</summary>
public sealed class WccUnitSidebarCacheRequest
{
    internal WccUnitSidebarCacheRequest(WccStaticSourceCaptureResult capture, string key,
        WccUnitSidebarLookup[] lookups)
    { Capture = capture; Key = key; Lookups = Array.AsReadOnly(lookups); }
    internal WccStaticSourceCaptureResult Capture { get; }
    public string Key { get; }
    public IReadOnlyList<WccUnitSidebarLookup> Lookups { get; }
}

/// <summary>
/// Disposable derived sidebar cache, separate from durable Library content. Every
/// read requires fresh immutable capture evidence; all invalid inputs are cache misses.
/// Cached values never enter editor models, evaluators, source writers or save proof.
/// </summary>
public sealed class WccUnitSidebarSummaryCache
{
    public const int FormatVersion = 1;
    public const int MaximumEntries = 20_000;
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false };
    private readonly string _root;

    public WccUnitSidebarSummaryCache(string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _root = Path.GetFullPath(cacheRoot);
    }

    public static WccUnitSidebarCacheRequest? CreateRequest(WccStaticSourceCaptureResult capture,
        IReadOnlyList<WccUnitSidebarLookup> lookups, string targetCompatibility, string profileIdentity,
        string contextIdentity, string codeIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(lookups);
        cancellationToken.ThrowIfCancellationRequested();
        if (!capture.HasCompleteCoreUnitPrelude || WccStaticSourceCapture.GetCapturedContentIdentity(capture) is not { } identity ||
            lookups.Count is < 1 or > MaximumEntries || new[] { targetCompatibility, profileIdentity, contextIdentity, codeIdentity }
                .Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 64 * 1024)) return null;
        var sources = capture.Sources.ToLookup(source => source.Path, Paths);
        if (lookups.Any(lookup => !ValidLookup(lookup, sources))) return null;
        var ordered = lookups.OrderBy(lookup => lookup.Path, Paths).ThenBy(lookup => lookup.ContentHash, StringComparer.Ordinal)
            .ThenBy(lookup => lookup.Start).ThenBy(lookup => lookup.Length).ToArray();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var text = new StringBuilder("wcc-unit-sidebar-v1;");
        foreach (var value in new[] { identity, capture.CoreVersion ?? "", targetCompatibility, profileIdentity, contextIdentity, codeIdentity })
            Append(value);
        AppendSources(capture.PreludeSources);
        AppendSources(capture.CoreUnitPreludeSources);
        foreach (var lookup in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidLookup(lookup, sources) || !distinct.Add(LookupIdentity(lookup))) return null;
            Append(LookupIdentity(lookup));
        }
        return new(capture, WccContentHash.ComputeText(text.ToString()), ordered);

        void Append(string value) => text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        void AppendSources(IReadOnlyList<WmlStaticSource> values)
        {
            Append(values.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var source in values) { Append(source.Path); Append(source.ContentHash); Append(source.IsAuthored.ToString()); }
        }
    }

    public async Task<IReadOnlyList<WccUnitSidebarCachedSummary>?> TryLoadAsync(WccUnitSidebarCacheRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var path = Path.Combine(_root, request.Key + ".json");
            if (!System.IO.File.Exists(path) || new FileInfo(path).Length is < 1 or > MaximumFileBytes) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            if (stream.Length is < 1 or > MaximumFileBytes) return null;
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (stream.ReadByte() != -1) return null;
            var file = JsonSerializer.Deserialize<CacheFile>(bytes, Json);
            if (file?.Version != FormatVersion || file.Key != request.Key || file.Entries is null ||
                !Validate(request, file.Entries, cancellationToken)) return null;
            return Array.AsReadOnly(file.Entries);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return null; }
    }

    public async Task<bool> TrySaveAsync(WccUnitSidebarCacheRequest request,
        IReadOnlyList<WccUnitSidebarCachedSummary> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(entries);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Validate(request, entries, cancellationToken)) return false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new CacheFile(FormatVersion, request.Key, entries.ToArray()), Json);
        if (bytes.Length > MaximumFileBytes) return false;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, request.Key + ".json");
            temporary = Path.Combine(_root, request.Key + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await System.IO.File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            System.IO.File.Move(temporary, path, overwrite: true);
            temporary = null;
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
        finally
        {
            if (temporary is not null)
                try { System.IO.File.Delete(temporary); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool Validate(WccUnitSidebarCacheRequest request, IReadOnlyList<WccUnitSidebarCachedSummary> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count != request.Lookups.Count || entries.Count > MaximumEntries) return false;
        var expected = request.Lookups.Select(LookupIdentity).ToHashSet(StringComparer.Ordinal);
        // JSON may escape each UTF-16 code unit as six ASCII bytes. Budget the
        // worst case before serialization, including property/numeric overhead.
        long remainingBytes = MaximumFileBytes - 256 - 6L * request.Key.Length;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry?.Lookup is null || string.IsNullOrWhiteSpace(entry.Lookup.Path) ||
                entry.Lookup.Path.Length > 32 * 1024 || string.IsNullOrWhiteSpace(entry.Lookup.ContentHash) ||
                entry.Lookup.ContentHash.Length > 128 ||
                entry.EffectiveRace?.Length > 64 * 1024 || entry.PreviewPath?.Length > 32 * 1024) return false;
            remainingBytes -= 256 + 6L * (entry.Lookup.Path.Length + entry.Lookup.ContentHash.Length +
                (entry.EffectiveRace?.Length ?? 0) + (entry.PreviewPath?.Length ?? 0));
            if (remainingBytes < 0 || !expected.Remove(LookupIdentity(entry.Lookup))) return false;
            if (!entry.Available)
            {
                if (entry.EffectiveRace is not null || entry.PreviewPath is not null || entry.RequiresCapturedImages) return false;
            }
            // A legacy document fallback cannot be reconstructed from a race/path
            // pair. Keep those cases as misses rather than change their route.
            else if (!entry.RequiresCapturedImages || entry.PreviewPath is { } path &&
                     !WccStaticSourceCapture.IsCapturedResourcePath(request.Capture, path)) return false;
        }
        return expected.Count == 0;
    }

    private static bool ValidLookup(WccUnitSidebarLookup? lookup, ILookup<string, WmlStaticSource> sources)
    {
        if (lookup is null || string.IsNullOrWhiteSpace(lookup.Path) || !Path.IsPathFullyQualified(lookup.Path) ||
            string.IsNullOrWhiteSpace(lookup.ContentHash) || lookup.Start < 0 || lookup.Length <= 0) return false;
        try
        {
            if (!Paths.Equals(lookup.Path, Path.GetFullPath(lookup.Path))) return false;
            var matches = sources[lookup.Path].Take(2).ToArray();
            return matches.Length == 1 && matches[0].IsAuthored && matches[0].ContentHash == lookup.ContentHash &&
                lookup.Start <= matches[0].Text.Length - lookup.Length;
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

    private static string LookupIdentity(WccUnitSidebarLookup lookup) =>
        JsonSerializer.Serialize(new[] { OperatingSystem.IsWindows() ? lookup.Path.ToUpperInvariant() : lookup.Path,
            lookup.ContentHash, lookup.Start.ToString(CultureInfo.InvariantCulture), lookup.Length.ToString(CultureInfo.InvariantCulture) });
    private sealed record CacheFile(int Version, string Key, WccUnitSidebarCachedSummary[] Entries);
}
