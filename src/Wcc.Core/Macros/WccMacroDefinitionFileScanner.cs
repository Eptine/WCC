using Wcc.Core.Wml;

namespace Wcc.Core.Macros;

public static class WccMacroDefinitionFileScanner
{
    public static IEnumerable<(string FilePath, IReadOnlyList<WmlMacroDefinition> Definitions)> ScanDirectory(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            yield break;

        if (!Directory.Exists(rootDirectory))
            yield break;

        foreach (var file in Directory.EnumerateFiles(rootDirectory, "*.cfg", SearchOption.AllDirectories))
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            var defs = WmlMacroDefinitionScanner.Scan(text);
            if (defs.Count == 0)
                continue;

            yield return (file, defs);
        }
    }
}