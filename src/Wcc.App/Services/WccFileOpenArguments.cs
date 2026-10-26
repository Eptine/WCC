using System;
using System.Collections.Generic;
using System.IO;

namespace Wcc.App.Services;

public sealed record WccFileOpenArgumentResult(IReadOnlyList<string> Files, IReadOnlyList<string> Diagnostics);

/// <summary>Interprets shell file-open arguments in the sender's working directory.</summary>
public static class WccFileOpenArguments
{
    public static WccFileOpenArgumentResult Parse(IReadOnlyList<string> arguments, string senderDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(senderDirectory);
        var directory = Path.GetFullPath(senderDirectory);
        var files = new List<string>();
        var diagnostics = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var literalPaths = false;
        foreach (var argument in arguments)
        {
            if (!literalPaths && argument == "--") { literalPaths = true; continue; }
            if (string.IsNullOrEmpty(argument))
            { diagnostics.Add("An empty file-open argument was ignored."); continue; }
            if (!literalPaths && argument.StartsWith('-'))
            { diagnostics.Add("Unknown WCC option: " + argument); continue; }
            try
            {
                var path = argument;
                if (argument.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Uri.TryCreate(argument, UriKind.Absolute, out var uri) || !uri.IsFile ||
                        uri.Query.Length != 0 || uri.Fragment.Length != 0)
                    { diagnostics.Add("Invalid file URI: " + argument); continue; }
                    path = uri.LocalPath;
                    if (!Path.IsPathFullyQualified(path))
                    { diagnostics.Add("The file URI does not identify an absolute local path: " + argument); continue; }
                }
                else if (argument.Contains("://", StringComparison.Ordinal))
                { diagnostics.Add("Only local files and file URIs can be opened: " + argument); continue; }
                path = Path.GetFullPath(path, directory);
                if (!IsAcceptedFile(path))
                { diagnostics.Add("WCC file-open arguments must name .cfg files: " + argument); continue; }
                if (!seen.Add(path)) continue;
                if (files.Count == WccFileOpenInstanceBroker.MaximumFiles)
                { diagnostics.Add("Too many .cfg files were requested at once: " + argument); continue; }
                files.Add(path);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
            { diagnostics.Add("The file-open path is invalid: " + argument + " (" + error.Message + ")"); }
        }
        return new(files.ToArray(), diagnostics.ToArray());
    }

    internal static bool IsAcceptedFile(string? path) => !string.IsNullOrEmpty(path) &&
        path.Length <= WccFileOpenInstanceBroker.MaximumPathCharacters && path.IndexOf('\0') < 0 &&
        Path.IsPathFullyQualified(path) && string.Equals(Path.GetExtension(path), ".cfg", StringComparison.OrdinalIgnoreCase);
}
