using Wcc.Core.Projects;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.References;

public static class WccUnitPreviewPathResolver
{
    public static WccUnitPreviewResolution ResolveRawImageExpression(
        string? sourcePath,
        WccSourceOrigin origin,
        string? rawExpression,
        WccAssetReferenceKind kind,
        string? wesnothInstallPath = null,
        IReadOnlyList<WmlMacroDefinition>? macroDefinitions = null,
        string? sourceLabel = null,
        bool preferTransparentPortrait = true)
    {
        if (kind is not (WccAssetReferenceKind.Image or WccAssetReferenceKind.Portrait))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only unit images and portraits are supported.");

        var label = sourceLabel ?? (kind == WccAssetReferenceKind.Image ? "image" : "portrait");
        var expression = WccImageExpressionParser.TryParse(rawExpression);
        if (expression is null)
            return new WccUnitPreviewResolution { SourceLabel = label };

        var hasTemplate = WmlTemplatedPathParser.TryParsePrefixMacroPath(
            expression.BaseReference,
            out var template);
        var reference = new WccAssetReference
        {
            Kind = kind,
            OwnerBlockKind = "unit_type",
            OwnerInternalId = string.Empty,
            SourceFieldKey = kind == WccAssetReferenceKind.Image ? "image" : "profile",
            RawValue = expression.NormalizedExpression,
            LookupPath = expression.BaseReference,
            IsTemplatedPath = hasTemplate || expression.BaseReference.Contains('{'),
            TemplatedMacroName = template?.MacroName,
            TemplatedMacroRawInvocation = template?.MacroRawInvocation,
            TemplatedPathSuffix = template?.Suffix
        };
        var expanded = WccTemplatedAssetReferenceExpander.TryExpand(reference, macroDefinitions);
        if (expanded is null)
        {
            return new WccUnitPreviewResolution
            {
                RawExpression = expression.NormalizedExpression,
                SourceLabel = label,
                HasReference = true,
                IsDynamicReference = true,
                UnresolvedMacroName = reference.TemplatedMacroName
            };
        }

        var resolution = WccAssetPathResolver.ResolveReference(
            sourcePath,
            origin,
            expanded,
            wesnothInstallPath,
            preferTransparentPortrait);
        return new WccUnitPreviewResolution
        {
            ResolvedAbsolutePath = resolution.ResolvedAbsolutePath,
            RawExpression = expression.NormalizedExpression,
            SourceLabel = label,
            HasReference = true
        };
    }

    public static WccUnitPreviewResolution Resolve(
        UnitFileAsset sourceFile,
        UnitTypeDocument document,
        string? wesnothInstallPath = null)
    {
        if (sourceFile is null) throw new ArgumentNullException(nameof(sourceFile));
        if (document is null) throw new ArgumentNullException(nameof(document));

        var image = ResolveKind(
            sourceFile,
            document,
            WccAssetReferenceKind.Image,
            wesnothInstallPath);
        if (image.IsResolved)
            return image;

        var portrait = ResolveKind(
            sourceFile,
            document,
            WccAssetReferenceKind.Portrait,
            wesnothInstallPath);
        if (portrait.IsResolved)
            return portrait;

        return image.HasReference ? image : portrait;
    }

    /// <summary>
    /// Resolves the best compact unit thumbnail. Unit images are preferred and portraits are
    /// retained as a fallback for sidebars and pickers.
    /// </summary>
    public static WccUnitPreviewResolution Resolve(
        string? sourcePath,
        WccSourceOrigin origin,
        UnitTypeDocument document,
        string? wesnothInstallPath = null)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        var image = ResolveKind(
            sourcePath,
            origin,
            document,
            WccAssetReferenceKind.Image,
            wesnothInstallPath);
        if (image.IsResolved)
            return image;

        var portrait = ResolveKind(
            sourcePath,
            origin,
            document,
            WccAssetReferenceKind.Portrait,
            wesnothInstallPath);
        if (portrait.IsResolved)
            return portrait;

        return image.HasReference ? image : portrait;
    }

    /// <summary>
    /// Resolves one exact unit visual field. Unlike <see cref="Resolve(UnitFileAsset, UnitTypeDocument, string?)"/>,
    /// this never substitutes a portrait for a missing unit image or vice versa.
    /// </summary>
    public static WccUnitPreviewResolution ResolveKind(
        UnitFileAsset sourceFile,
        UnitTypeDocument document,
        WccAssetReferenceKind kind,
        string? wesnothInstallPath = null)
    {
        if (sourceFile is null) throw new ArgumentNullException(nameof(sourceFile));

        return ResolveKindCore(
            sourceFile.SourcePath,
            sourceFile.Origin,
            document,
            kind,
            wesnothInstallPath,
            sourceFile.MacroDefinitions);
    }

    public static WccUnitPreviewResolution ResolveKind(
        string? sourcePath,
        WccSourceOrigin origin,
        UnitTypeDocument document,
        WccAssetReferenceKind kind,
        string? wesnothInstallPath = null)
    {
        return ResolveKindCore(
            sourcePath,
            origin,
            document,
            kind,
            wesnothInstallPath,
            macroDefinitions: null);
    }

    public static WccUnitPreviewResolution ResolveGenderVariantKind(
        UnitFileAsset sourceFile,
        UnitTypeDocument document,
        UnitGenderVariantDocument variantDocument,
        WccAssetReferenceKind kind,
        string? wesnothInstallPath = null)
    {
        if (sourceFile is null) throw new ArgumentNullException(nameof(sourceFile));

        return ResolveGenderVariantKindCore(
            sourceFile.SourcePath,
            sourceFile.Origin,
            document,
            variantDocument,
            kind,
            wesnothInstallPath,
            sourceFile.MacroDefinitions);
    }

    public static WccUnitPreviewResolution ResolveGenderVariantKind(
        string? sourcePath,
        WccSourceOrigin origin,
        UnitTypeDocument document,
        UnitGenderVariantDocument variantDocument,
        WccAssetReferenceKind kind,
        string? wesnothInstallPath = null,
        IReadOnlyList<WmlMacroDefinition>? macroDefinitions = null)
    {
        return ResolveGenderVariantKindCore(
            sourcePath,
            origin,
            document,
            variantDocument,
            kind,
            wesnothInstallPath,
            macroDefinitions);
    }

    /// <summary>
    /// Resolves the effective small portrait using Wesnoth's documented rules:
    /// a missing small_profile falls back to profile, while small_profile is
    /// ignored when profile itself is absent.
    /// </summary>
    public static WccUnitPreviewResolution ResolveSmallProfile(
        UnitFileAsset sourceFile,
        UnitTypeDocument document,
        string? wesnothInstallPath = null)
    {
        if (sourceFile is null) throw new ArgumentNullException(nameof(sourceFile));

        return ResolveSmallProfileCore(
            sourceFile.SourcePath,
            sourceFile.Origin,
            document,
            wesnothInstallPath,
            sourceFile.MacroDefinitions);
    }

    public static WccUnitPreviewResolution ResolveSmallProfile(
        string? sourcePath,
        WccSourceOrigin origin,
        UnitTypeDocument document,
        string? wesnothInstallPath = null) =>
        ResolveSmallProfileCore(
            sourcePath,
            origin,
            document,
            wesnothInstallPath,
            macroDefinitions: null);

    public static WccUnitPreviewResolution ResolveGenderVariantSmallProfile(
        UnitFileAsset sourceFile,
        UnitTypeDocument document,
        UnitGenderVariantDocument variantDocument,
        string? wesnothInstallPath = null)
    {
        if (sourceFile is null) throw new ArgumentNullException(nameof(sourceFile));

        return ResolveGenderVariantSmallProfileCore(
            sourceFile.SourcePath,
            sourceFile.Origin,
            document,
            variantDocument,
            wesnothInstallPath,
            sourceFile.MacroDefinitions);
    }

    public static WccUnitPreviewResolution ResolveGenderVariantSmallProfile(
        string? sourcePath,
        WccSourceOrigin origin,
        UnitTypeDocument document,
        UnitGenderVariantDocument variantDocument,
        string? wesnothInstallPath = null) =>
        ResolveGenderVariantSmallProfileCore(
            sourcePath,
            origin,
            document,
            variantDocument,
            wesnothInstallPath,
            macroDefinitions: null);

    private static WccUnitPreviewResolution ResolveGenderVariantKindCore(
        string? sourcePath,
        WccSourceOrigin origin,
        UnitTypeDocument document,
        UnitGenderVariantDocument variantDocument,
        WccAssetReferenceKind kind,
        string? wesnothInstallPath,
        IReadOnlyList<Wml.WmlMacroDefinition>? macroDefinitions)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (variantDocument is null) throw new ArgumentNullException(nameof(variantDocument));
        if (kind is not (WccAssetReferenceKind.Image or WccAssetReferenceKind.Portrait))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A unit preview can resolve only image or portrait references.");

        var fieldKey = kind == WccAssetReferenceKind.Image ? "image" : "profile";
        var directValue = kind == WccAssetReferenceKind.Image
            ? variantDocument.Variant.Image
            : variantDocument.Variant.Profile;

        if (variantDocument.Variant.FieldStates.TryGetValue(fieldKey, out var fieldState) &&
            !fieldState.IsEditable)
        {
            return new WccUnitPreviewResolution
            {
                SourceLabel = $"{variantDocument.Kind.ToWmlTagName()} {fieldKey} (conditional)"
            };
        }

        // A missing direct override inherits the root value; it must not be copied into
        // the variant model merely to make a preview work.
        if (string.IsNullOrWhiteSpace(directValue))
        {
            return ResolveKindCore(
                sourcePath,
                origin,
                document,
                kind,
                wesnothInstallPath,
                macroDefinitions);
        }

        if (kind == WccAssetReferenceKind.Portrait)
        {
            var profileValue = directValue.Trim();
            if (string.Equals(profileValue, "none", StringComparison.OrdinalIgnoreCase))
            {
                return new WccUnitPreviewResolution
                {
                    SourceLabel = $"{variantDocument.Kind.ToWmlTagName()} portrait",
                    HasReference = true,
                    IsSuppressed = true
                };
            }

            if (string.Equals(profileValue, "unit_image", StringComparison.OrdinalIgnoreCase))
            {
                var unitImage = ResolveGenderVariantKindCore(
                    sourcePath,
                    origin,
                    document,
                    variantDocument,
                    WccAssetReferenceKind.Image,
                    wesnothInstallPath,
                    macroDefinitions);
                return new WccUnitPreviewResolution
                {
                    ResolvedAbsolutePath = unitImage.ResolvedAbsolutePath,
                    RawExpression = unitImage.RawExpression,
                    SourceLabel = $"{variantDocument.Kind.ToWmlTagName()} unit image (profile=unit_image)",
                    HasReference = true,
                    IsDynamicReference = unitImage.IsDynamicReference,
                    UnresolvedMacroName = unitImage.UnresolvedMacroName
                };
            }
        }

        return ResolveOwnedFieldCore(
            sourcePath,
            origin,
            document,
            variantDocument.InternalId,
            kind,
            fieldKey,
            $"{variantDocument.Kind.ToWmlTagName()} {fieldKey}",
            wesnothInstallPath,
            macroDefinitions,
            preferTransparentPortrait:
                kind != WccAssetReferenceKind.Portrait ||
                (string.IsNullOrWhiteSpace(variantDocument.Variant.SmallProfile) &&
                 string.IsNullOrWhiteSpace(document.Unit.SmallProfile)));
    }

    private static WccUnitPreviewResolution ResolveGenderVariantSmallProfileCore(
        string? sourcePath,
        WccSourceOrigin origin,
        UnitTypeDocument document,
        UnitGenderVariantDocument variantDocument,
        string? wesnothInstallPath,
        IReadOnlyList<Wml.WmlMacroDefinition>? macroDefinitions)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (variantDocument is null) throw new ArgumentNullException(nameof(variantDocument));

        var variant = variantDocument.Variant;
        if (variant.FieldStates.TryGetValue("profile", out var variantProfileState) &&
            !variantProfileState.IsEditable)
        {
            return ConditionalResolution($"{variantDocument.Kind.ToWmlTagName()} small portrait (profile conditional)");
        }

        var effectiveProfile = string.IsNullOrWhiteSpace(variant.Profile)
            ? document.Unit.Profile
            : variant.Profile;
        if (string.IsNullOrWhiteSpace(effectiveProfile))
        {
            return new WccUnitPreviewResolution
            {
                SourceLabel = $"{variantDocument.Kind.ToWmlTagName()} small portrait (ignored without profile)"
            };
        }

        if (string.Equals(effectiveProfile.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return new WccUnitPreviewResolution
            {
                SourceLabel = $"{variantDocument.Kind.ToWmlTagName()} small portrait",
                HasReference = true,
                IsSuppressed = true
            };
        }

        if (variant.FieldStates.TryGetValue("small_profile", out var variantSmallState) &&
            !variantSmallState.IsEditable)
        {
            return ConditionalResolution($"{variantDocument.Kind.ToWmlTagName()} small portrait (conditional)");
        }

        if (!string.IsNullOrWhiteSpace(variant.SmallProfile))
        {
            return ResolveOwnedFieldCore(
                sourcePath,
                origin,
                document,
                variantDocument.InternalId,
                WccAssetReferenceKind.Portrait,
                "small_profile",
                $"{variantDocument.Kind.ToWmlTagName()} small portrait",
                wesnothInstallPath,
                macroDefinitions,
                preferTransparentPortrait: false);
        }

        if (document.Unit.FieldStates.TryGetValue("small_profile", out var rootSmallState) &&
            !rootSmallState.IsEditable)
        {
            return ConditionalResolution("base small portrait inherited by variant (conditional)");
        }

        if (!string.IsNullOrWhiteSpace(document.Unit.SmallProfile))
        {
            return ResolveOwnedFieldCore(
                sourcePath,
                origin,
                document,
                document.InternalId,
                WccAssetReferenceKind.Portrait,
                "small_profile",
                "base small portrait inherited by variant",
                wesnothInstallPath,
                macroDefinitions,
                preferTransparentPortrait: false);
        }

        var profileFallback = ResolveGenderVariantKindCore(
            sourcePath,
            origin,
            document,
            variantDocument,
            WccAssetReferenceKind.Portrait,
            wesnothInstallPath,
            macroDefinitions);
        return WithSourceLabel(
            profileFallback,
            $"{variantDocument.Kind.ToWmlTagName()} profile fallback for small portrait");
    }

    private static WccUnitPreviewResolution ResolveSmallProfileCore(
        string? sourcePath,
        WccSourceOrigin origin,
        UnitTypeDocument document,
        string? wesnothInstallPath,
        IReadOnlyList<Wml.WmlMacroDefinition>? macroDefinitions)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        if (document.Unit.FieldStates.TryGetValue("profile", out var profileState) &&
            !profileState.IsEditable)
        {
            return ConditionalResolution("small portrait (profile conditional)");
        }

        var profile = document.Unit.Profile?.Trim();
        if (string.IsNullOrWhiteSpace(profile))
        {
            return new WccUnitPreviewResolution
            {
                SourceLabel = "small portrait (ignored without profile)"
            };
        }

        if (string.Equals(profile, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new WccUnitPreviewResolution
            {
                SourceLabel = "small portrait",
                HasReference = true,
                IsSuppressed = true
            };
        }

        if (document.Unit.FieldStates.TryGetValue("small_profile", out var smallState) &&
            !smallState.IsEditable)
        {
            return ConditionalResolution("small portrait (conditional)");
        }

        if (string.IsNullOrWhiteSpace(document.Unit.SmallProfile))
        {
            return WithSourceLabel(
                ResolveKindCore(
                    sourcePath,
                    origin,
                    document,
                    WccAssetReferenceKind.Portrait,
                    wesnothInstallPath,
                    macroDefinitions),
                "profile fallback for small portrait");
        }

        return ResolveOwnedFieldCore(
            sourcePath,
            origin,
            document,
            document.InternalId,
            WccAssetReferenceKind.Portrait,
            "small_profile",
            "small portrait",
            wesnothInstallPath,
            macroDefinitions,
            preferTransparentPortrait: false);
    }

    private static WccUnitPreviewResolution ResolveOwnedFieldCore(
        string? sourcePath,
        WccSourceOrigin origin,
        UnitTypeDocument document,
        string ownerInternalId,
        WccAssetReferenceKind kind,
        string sourceFieldKey,
        string sourceLabel,
        string? wesnothInstallPath,
        IReadOnlyList<Wml.WmlMacroDefinition>? macroDefinitions,
        bool preferTransparentPortrait = true)
    {
        var references = document.AssetReferences
            .Where(r =>
                string.Equals(r.OwnerInternalId, ownerInternalId, StringComparison.Ordinal) &&
                r.Kind == kind &&
                string.Equals(r.SourceFieldKey, sourceFieldKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        string? unresolvedMacroName = null;
        var unresolvedExpressions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var assetRef in references)
        {
            var expandedReference = WccTemplatedAssetReferenceExpander.TryExpand(assetRef, macroDefinitions);
            unresolvedExpressions.Add(expandedReference?.RawValue ?? assetRef.RawValue);
            if (expandedReference is null)
            {
                unresolvedMacroName ??= assetRef.TemplatedMacroName;
                continue;
            }

            var resolution = WccAssetPathResolver.ResolveReference(
                sourcePath,
                origin,
                expandedReference,
                wesnothInstallPath,
                preferTransparentPortrait);
            if (resolution.IsResolved)
            {
                return new WccUnitPreviewResolution
                {
                    ResolvedAbsolutePath = resolution.ResolvedAbsolutePath,
                    RawExpression = expandedReference.RawValue,
                    SourceLabel = sourceLabel,
                    HasReference = true
                };
            }
        }

        return new WccUnitPreviewResolution
        {
            // A missing owner/Core file does not erase the image expression:
            // an actual captured campaign may resolve it in another active
            // binary path. This metadata grants no fallback or file authority.
            RawExpression = unresolvedExpressions.Count == 1 ? unresolvedExpressions.Single() : null,
            SourceLabel = sourceLabel,
            HasReference = references.Count > 0,
            IsDynamicReference = !string.IsNullOrWhiteSpace(unresolvedMacroName),
            UnresolvedMacroName = unresolvedMacroName
        };
    }

    private static WccUnitPreviewResolution ResolveKindCore(
        string? sourcePath,
        WccSourceOrigin origin,
        UnitTypeDocument document,
        WccAssetReferenceKind kind,
        string? wesnothInstallPath,
        IReadOnlyList<Wml.WmlMacroDefinition>? macroDefinitions)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (kind is not (WccAssetReferenceKind.Image or WccAssetReferenceKind.Portrait))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A unit preview can resolve only image or portrait references.");

        var sourceLabel = kind == WccAssetReferenceKind.Image ? "image" : "portrait";
        var fieldKey = kind == WccAssetReferenceKind.Image ? "image" : "profile";

        if (document.Unit.FieldStates.TryGetValue(fieldKey, out var fieldState) &&
            !fieldState.IsEditable)
        {
            return ConditionalResolution($"{sourceLabel} (conditional)");
        }

        if (kind == WccAssetReferenceKind.Portrait)
        {
            var profileValue = document.Unit.Profile?.Trim();
            if (string.Equals(profileValue, "none", StringComparison.OrdinalIgnoreCase))
            {
                return new WccUnitPreviewResolution
                {
                    SourceLabel = sourceLabel,
                    HasReference = true,
                    IsSuppressed = true
                };
            }

            if (string.Equals(profileValue, "unit_image", StringComparison.OrdinalIgnoreCase))
            {
                var unitImage = ResolveKindCore(
                    sourcePath,
                    origin,
                    document,
                    WccAssetReferenceKind.Image,
                    wesnothInstallPath,
                    macroDefinitions);
                return new WccUnitPreviewResolution
                {
                    ResolvedAbsolutePath = unitImage.ResolvedAbsolutePath,
                    RawExpression = unitImage.RawExpression,
                    SourceLabel = "unit image (profile=unit_image)",
                    HasReference = true,
                    IsDynamicReference = unitImage.IsDynamicReference,
                    UnresolvedMacroName = unitImage.UnresolvedMacroName
                };
            }
        }

        return ResolveOwnedFieldCore(
            sourcePath,
            origin,
            document,
            document.InternalId,
            kind,
            fieldKey,
            sourceLabel,
            wesnothInstallPath,
            macroDefinitions,
            preferTransparentPortrait:
                kind != WccAssetReferenceKind.Portrait ||
                string.IsNullOrWhiteSpace(document.Unit.SmallProfile));
    }

    private static WccUnitPreviewResolution ConditionalResolution(string sourceLabel) =>
        new()
        {
            SourceLabel = sourceLabel,
            HasReference = true,
            UnavailableReason = "The effective image expression is conditional WML and cannot be previewed safely yet."
        };

    private static WccUnitPreviewResolution WithSourceLabel(
        WccUnitPreviewResolution source,
        string sourceLabel) =>
        new()
        {
            ResolvedAbsolutePath = source.ResolvedAbsolutePath,
            RawExpression = source.RawExpression,
            SourceLabel = sourceLabel,
            HasReference = source.HasReference,
            IsSuppressed = source.IsSuppressed,
            IsDynamicReference = source.IsDynamicReference,
            UnresolvedMacroName = source.UnresolvedMacroName,
            UnavailableReason = source.UnavailableReason
        };
}
