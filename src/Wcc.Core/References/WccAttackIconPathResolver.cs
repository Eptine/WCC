using Wcc.Core.Projects;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.Core.References;

public static class WccAttackIconPathResolver
{
    public static WccAttackIconResolution Resolve(
        UnitFileAsset sourceFile,
        UnitTypeDocument unitDocument,
        AttackDocument attackDocument,
        string? wesnothInstallPath = null)
    {
        if (sourceFile is null) throw new ArgumentNullException(nameof(sourceFile));

        return Resolve(
            sourceFile.SourcePath,
            sourceFile.Origin,
            unitDocument,
            attackDocument,
            wesnothInstallPath);
    }

    public static WccAttackIconResolution Resolve(
        string? sourcePath,
        WccSourceOrigin origin,
        UnitTypeDocument unitDocument,
        AttackDocument attackDocument,
        string? wesnothInstallPath = null)
    {
        if (unitDocument is null) throw new ArgumentNullException(nameof(unitDocument));
        if (attackDocument is null) throw new ArgumentNullException(nameof(attackDocument));

        return ResolveFields(sourcePath, origin, attackDocument.Attack.Icon, attackDocument.Attack.Name, wesnothInstallPath);
    }

    /// <summary>Resolves current detached field values; cached asset references cannot replace newer edit intent.</summary>
    public static WccAttackIconResolution ResolveFields(string? sourcePath, WccSourceOrigin origin,
        string? icon, string? name, string? wesnothInstallPath = null)
    {
        if (!TryLiteral(icon, out var explicitIcon)) return new();
        var inferredDefault = explicitIcon.Length == 0;
        string expression;
        if (inferredDefault)
        {
            if (!TryLiteral(name, out var attackName)) return new();
            expression = attackName.Length == 0 ? "attacks/blank-attack.png" : "attacks/" + attackName + ".png";
        }
        else expression = explicitIcon;
        // Retain literal '#' characters when handing decoded text to the WML-aware image parser.
        var image = WccImageExpressionParser.TryParse("\"" + expression.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
        if (image is null) return new();
        var reference = new WccAssetReference
        {
            Kind = WccAssetReferenceKind.Icon, OwnerBlockKind = "attack", OwnerInternalId = string.Empty,
            SourceFieldKey = "icon", RawValue = expression, LookupPath = image.BaseReference
        };

        var resolution = WccAssetPathResolver.ResolveReference(
            sourcePath,
            origin,
            reference,
            wesnothInstallPath);

        return new WccAttackIconResolution
        {
            ResolvedAbsolutePath = resolution.ResolvedAbsolutePath,
            LookupPath = reference.LookupPath,
            IsInferredDefault = inferredDefault
        };
    }

    private static bool TryLiteral(string? raw, out string value)
    {
        value = "";
        return raw is null || WmlResourceValueTextDecoder.TryDecode(raw, out value, out _)
            && value.IndexOfAny(['{', '}', '$']) < 0;
    }
}
