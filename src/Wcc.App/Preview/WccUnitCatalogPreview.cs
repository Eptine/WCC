using System;
using System.IO;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.References;
using Wcc.Core.Wml;

namespace Wcc.App.Preview;

/// <summary>Lazy display-only thumbnails shared by unit sidebars and unit catalog pickers.</summary>
public static class WccUnitCatalogPreview
{
    public static Func<string?> CreateResolver(WccEntityRecord entity, WccSourceFileRecord sourceFile,
        WccPackageRecord? package, string? wesnothInstallPath)
    {
        var definitions = new Lazy<System.Collections.Generic.IReadOnlyList<WmlMacroDefinition>>(() =>
        {
            try { return File.Exists(sourceFile.StoragePath)
                ? WmlMacroDefinitionScanner.Scan(File.ReadAllText(sourceFile.StoragePath!))
                : Array.Empty<WmlMacroDefinition>(); }
            catch (IOException) { return Array.Empty<WmlMacroDefinition>(); }
            catch (UnauthorizedAccessException) { return Array.Empty<WmlMacroDefinition>(); }
        });
        return () =>
        {
            var origin = package?.SourceKind == WccPackageSourceKinds.WesnothCore ||
                         package?.SourceKind == WccPackageSourceKinds.WesnothMainline
                ? WccSourceOrigin.CoreInstall
                : package?.SourceKind == WccPackageSourceKinds.WccManagedLibrary
                    ? WccSourceOrigin.WccGenerated : WccSourceOrigin.ImportedExternal;
            foreach (var candidate in new[] { (Key: "image", Kind: WccAssetReferenceKind.Image),
                         (Key: "profile", Kind: WccAssetReferenceKind.Portrait) })
            {
                if (!entity.Summary.TryGetValue(candidate.Key, out var raw)) continue;
                var expression = WccImageExpressionParser.TryParse(raw);
                if (expression is null) continue;
                var templated = expression.BaseReference.Contains('{') || expression.BaseReference.Contains('}');
                var resolution = WccUnitPreviewPathResolver.ResolveRawImageExpression(sourceFile.StoragePath,
                    origin, raw, candidate.Kind, wesnothInstallPath, templated ? definitions.Value : null);
                if (resolution.IsResolved) return resolution.ResolvedAbsolutePath;
            }
            return null;
        };
    }
}
