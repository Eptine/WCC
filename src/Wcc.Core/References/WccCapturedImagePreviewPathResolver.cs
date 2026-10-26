using Wcc.Core.Library;
using Wcc.Core.Wml;

namespace Wcc.Core.References;

/// <summary>A display route over an exact captured source, with no source mutation authority.</summary>
public static class WccCapturedImagePreviewPathResolver
{
    public static WccUnitPreviewResolution Resolve(WccCapturedImageResourceContext context, string? rawExpression)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TryNormalize(rawExpression, out var normalized) ? context.Resolve(normalized) : new();
    }

    public static WccUnitPreviewResolution Resolve(WccLibrarySnapshot snapshot, WccPackageRevisionId rootRevisionId,
        WmlStaticSource capturedSource, string? rawExpression, WccAssetReferenceKind kind)
    {
        // Some display callers already decoded a simple scalar; concatenated
        // captured WML still starts with its opening quote. Decode that bounded
        // literal before splitting IPFs, without interpreting modifier syntax or
        // changing the author's value/source or the strict editor scalar codec.
        if (!TryNormalize(rawExpression, out var normalized)) return new();
        var expression = WccImageExpressionParser.TryParse(normalized);
        if (expression is null) return new();
        var reference = new WccAssetReference
        { Kind = kind, OwnerBlockKind = "campaign", OwnerInternalId = "", SourceFieldKey = "image",
            RawValue = expression.NormalizedExpression, LookupPath = expression.BaseReference };
        var resolution = WccAssetPathResolver.ResolveCapturedPackageReference(snapshot, rootRevisionId, capturedSource, reference);
        return new() { HasReference = true, RawExpression = expression.NormalizedExpression,
            SourceLabel = "Captured package image", ResolvedAbsolutePath = resolution.ResolvedAbsolutePath };
    }

    private static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = value;
        if (value?.TrimStart().StartsWith('"') != true) return true;
        if (!WmlResourceValueTextDecoder.TryDecode(value, out var decoded, out _)) return false;
        normalized = decoded;
        return true;
    }
}
