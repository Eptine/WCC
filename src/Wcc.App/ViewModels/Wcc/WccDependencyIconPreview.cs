using System;
using System.IO;
using System.Linq;
using Wcc.Core.Library;
using Wcc.Core.References;

namespace Wcc.App.ViewModels.Wcc;

/// <summary>Display-only package identity art from an exact captured provider.
/// It never activates binary paths, loads WML or grants mutation authority.</summary>
internal static class WccDependencyIconPreview
{
    internal const string CoreIconAsset = "avares://Wcc.App/Assets/Wesnoth/UI/wesnoth-icon.png";
    internal sealed record Selection(WccUnitPreviewResolution Resolution, string? Asset, string Status);

    internal static Selection Resolve(WccStaticSourceCaptureResult? capture, WccPackageRevisionId? provider,
        string addonId, string? declaredIcon, bool core)
    {
        if (core) return new(new() { HasReference = true, RawExpression = CoreIconAsset,
            SourceLabel = "Bundled Wesnoth icon", ResolvedAbsolutePath = CoreIconAsset }, CoreIconAsset,
            "Bundled Battle for Wesnoth icon.");
        if (capture is null || provider is not { } revision)
            return Missing(declaredIcon, "No exact captured dependency source supplies an icon.");

        var expression = WccImageExpressionParser.TryParse(declaredIcon);
        if (expression is not null)
        {
            var reference = expression.BaseReference;
            WccCapturedResourceMember? member = null;
            var ownPrefix = "data/add-ons/" + addonId + "/";
            var ownMount = "~add-ons/" + addonId + "/";
            if (reference.StartsWith(ownPrefix, StringComparison.Ordinal))
                member = WccStaticSourceCapture.FindCapturedResourceMember(capture, revision, reference[ownPrefix.Length..]);
            else if (reference.StartsWith(ownMount, StringComparison.Ordinal))
                member = WccStaticSourceCapture.FindCapturedResourceMember(capture, revision, reference[ownMount.Length..]);
            else if (reference.StartsWith("data/core/images/", StringComparison.Ordinal))
                member = WccStaticSourceCapture.FindCapturedResourceByVirtualPath(capture, "core/images/" + reference[17..]);
            else if (!reference.StartsWith("data/", StringComparison.Ordinal) && !reference.StartsWith("~", StringComparison.Ordinal))
            {
                var relative = reference.StartsWith("images/", StringComparison.Ordinal) ? reference : "images/" + reference;
                member = WccStaticSourceCapture.FindCapturedResourceMember(capture, revision, relative)
                    ?? WccStaticSourceCapture.FindCapturedResourceByVirtualPath(capture, "core/" + relative);
            }
            if (member is not null)
                return new(Resolved(member, expression.NormalizedExpression), null,
                    "Declared dependency icon from its captured package or Core.");
        }

        // The frozen resource inventory is enumerated only in the existing image
        // worker, once per request. Package IDs, not names/global search order,
        // choose the provider. Ordinal ordering is stable on Windows and Linux.
        var image = WccStaticSourceCapture.EnumerateCapturedResourceMembers(capture, revision)
            .Where(member => member.RelativePath.StartsWith("images/", StringComparison.Ordinal) &&
                IsImage(member.RelativePath))
            .OrderBy(member => Rank(member.RelativePath))
            .ThenBy(member => member.RelativePath, StringComparer.Ordinal)
            .FirstOrDefault();
        return image is null ? Missing(declaredIcon, "This dependency has no captured image for an icon preview.")
            : new(Resolved(image, image.RelativePath[7..]), null,
                string.IsNullOrWhiteSpace(declaredIcon)
                    ? "Representative image from this dependency; no package icon is declared."
                    : "Representative image from this dependency; its declared icon is unavailable.");
    }

    private static WccUnitPreviewResolution Resolved(WccCapturedResourceMember member, string expression) => new()
    { HasReference = true, RawExpression = expression, SourceLabel = "Exact dependency image", ResolvedAbsolutePath = member.PhysicalPath };
    private static Selection Missing(string? expression, string reason) => new(new()
    { HasReference = !string.IsNullOrWhiteSpace(expression), RawExpression = expression, SourceLabel = "Dependency icon", UnavailableReason = reason }, null, reason);
    private static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".webp" or ".jpg" or ".jpeg";
    private static int Rank(string path) => path switch
    {
        _ when path.StartsWith("images/units/", StringComparison.Ordinal) => 0,
        _ when path.StartsWith("images/attacks/", StringComparison.Ordinal) => 1,
        _ when path.StartsWith("images/icons/", StringComparison.Ordinal) => 2,
        _ when path.StartsWith("images/items/", StringComparison.Ordinal) => 3,
        _ when path.StartsWith("images/misc/", StringComparison.Ordinal) => 4,
        _ when path.StartsWith("images/portraits/", StringComparison.Ordinal) => 5,
        _ => 6
    };
}