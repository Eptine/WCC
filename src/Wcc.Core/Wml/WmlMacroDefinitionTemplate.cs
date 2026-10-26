namespace Wcc.Core.Wml;

/// <summary>
/// Shared parser/substituter for one indexed WML macro definition. Both block
/// expansion and value expansion use this so argument semantics cannot drift.
/// </summary>
internal sealed record WmlMacroDefinitionTemplate(
    IReadOnlyList<string> Parameters,
    IReadOnlyList<WmlMacroOptionalArgument> OptionalParameters,
    IReadOnlyList<string> BodyLines)
{
    public static bool TryParse(
        WmlMacroExpansionDefinition definition,
        out WmlMacroDefinitionTemplate? template,
        out string? problem)
    {
        template = null;
        problem = null;

        var lines = definition.RawText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var headerIndex = Array.FindIndex(lines, x =>
            x.TrimStart().StartsWith("#define", StringComparison.OrdinalIgnoreCase));
        if (headerIndex < 0)
        {
            problem = "the indexed definition has no #define header";
            return false;
        }

        var header = lines[headerIndex].Trim();
        var headerTokens = header["#define".Length..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (headerTokens.Length == 0 ||
            !string.Equals(headerTokens[0], definition.Name, StringComparison.OrdinalIgnoreCase))
        {
            problem = "the #define header does not match the indexed macro name";
            return false;
        }

        var body = new List<string>();
        var foundEnd = false;
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var endIndex = lines[i].IndexOf("#enddef", StringComparison.OrdinalIgnoreCase);
            if (endIndex >= 0)
            {
                if (endIndex > 0)
                    body.Add(lines[i][..endIndex]);
                foundEnd = true;
                break;
            }

            body.Add(lines[i]);
        }

        if (!foundEnd)
        {
            problem = "the indexed definition has no #enddef";
            return false;
        }

        if (!TryExtractOptionalArguments(
                body,
                headerTokens.Skip(1).ToArray(),
                out var optionalParameters,
                out var bodyWithoutOptionalDeclarations,
                out problem))
        {
            return false;
        }

        template = new WmlMacroDefinitionTemplate(
            headerTokens.Skip(1).ToArray(),
            optionalParameters,
            bodyWithoutOptionalDeclarations);
        return true;
    }

    public bool TryResolveArguments(
        IReadOnlyList<string> invocationArguments,
        out IReadOnlyList<string> resolvedArguments,
        out string? problem)
    {
        resolvedArguments = Array.Empty<string>();
        problem = null;
        if (invocationArguments.Count < Parameters.Count)
        {
            problem = $"expects {Parameters.Count} required argument(s), but the invocation supplies {invocationArguments.Count}";
            return false;
        }

        var values = invocationArguments.Take(Parameters.Count).ToList();
        var optionalValues = OptionalParameters.ToDictionary(
            x => x.Name,
            x => x.DefaultValue,
            StringComparer.Ordinal);
        var suppliedOptionalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var argument in invocationArguments.Skip(Parameters.Count))
        {
            if (!TryReadNamedOptionalArgument(argument, out var name, out var value))
            {
                problem = $"expects {Parameters.Count} required argument(s) followed by named optional arguments";
                return false;
            }

            if (!optionalValues.ContainsKey(name!))
            {
                problem = $"does not declare an optional argument named '{name}'";
                return false;
            }

            if (!suppliedOptionalNames.Add(name!))
            {
                problem = $"receives optional argument '{name}' more than once";
                return false;
            }

            optionalValues[name!] = value!;
        }

        values.AddRange(OptionalParameters.Select(x => optionalValues[x.Name]));
        resolvedArguments = values;
        return true;
    }

    public string Substitute(string text, IReadOnlyList<string> resolvedArguments)
    {
        var result = text;
        var parameterNames = Parameters.Concat(OptionalParameters.Select(x => x.Name)).ToArray();
        for (var i = 0; i < parameterNames.Length; i++)
        {
            result = result.Replace(
                "{" + parameterNames[i] + "}",
                NormalizeArgumentForSubstitution(resolvedArguments[i]),
                StringComparison.Ordinal);
        }

        return result;
    }

    public IReadOnlyList<string> SubstituteBody(IReadOnlyList<string> resolvedArguments)
        => BodyLines.Select(line => Substitute(line, resolvedArguments)).ToArray();

    public static bool IsPreprocessorControlLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("#if", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("#else", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("#endif", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("#undef", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("#define", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractOptionalArguments(
        IReadOnlyList<string> body,
        IReadOnlyList<string> requiredParameters,
        out IReadOnlyList<WmlMacroOptionalArgument> optionalParameters,
        out IReadOnlyList<string> bodyWithoutDeclarations,
        out string? problem)
    {
        var optional = new List<WmlMacroOptionalArgument>();
        var remaining = new List<string>();
        problem = null;
        for (var lineIndex = 0; lineIndex < body.Count; lineIndex++)
        {
            var trimmed = body[lineIndex].Trim();
            if (!trimmed.StartsWith("#arg", StringComparison.OrdinalIgnoreCase) ||
                (trimmed.Length > "#arg".Length &&
                 !char.IsWhiteSpace(trimmed["#arg".Length])))
            {
                if (trimmed.StartsWith("#endarg", StringComparison.OrdinalIgnoreCase))
                {
                    optionalParameters = Array.Empty<WmlMacroOptionalArgument>();
                    bodyWithoutDeclarations = Array.Empty<string>();
                    problem = "contains #endarg without a matching #arg";
                    return false;
                }

                remaining.Add(body[lineIndex]);
                continue;
            }

            var nameTokens = trimmed["#arg".Length..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (nameTokens.Length != 1)
            {
                optionalParameters = Array.Empty<WmlMacroOptionalArgument>();
                bodyWithoutDeclarations = Array.Empty<string>();
                problem = "contains a malformed #arg declaration";
                return false;
            }

            var name = nameTokens[0];
            if (requiredParameters.Contains(name, StringComparer.Ordinal) ||
                optional.Any(x => string.Equals(x.Name, name, StringComparison.Ordinal)))
            {
                optionalParameters = Array.Empty<WmlMacroOptionalArgument>();
                bodyWithoutDeclarations = Array.Empty<string>();
                problem = $"declares argument '{name}' more than once";
                return false;
            }

            var defaultLines = new List<string>();
            var foundEndArg = false;
            while (++lineIndex < body.Count)
            {
                var endArgIndex = body[lineIndex].IndexOf(
                    "#endarg",
                    StringComparison.OrdinalIgnoreCase);
                if (endArgIndex < 0)
                {
                    defaultLines.Add(body[lineIndex]);
                    continue;
                }

                var beforeEnd = body[lineIndex][..endArgIndex];
                if (beforeEnd.Length > 0)
                    defaultLines.Add(beforeEnd);
                var afterEnd = body[lineIndex][(endArgIndex + "#endarg".Length)..].Trim();
                if (afterEnd.Length > 0 && !afterEnd.StartsWith('#'))
                {
                    optionalParameters = Array.Empty<WmlMacroOptionalArgument>();
                    bodyWithoutDeclarations = Array.Empty<string>();
                    problem = $"contains unsupported text after #endarg for '{name}'";
                    return false;
                }

                foundEndArg = true;
                break;
            }

            if (!foundEndArg)
            {
                optionalParameters = Array.Empty<WmlMacroOptionalArgument>();
                bodyWithoutDeclarations = Array.Empty<string>();
                problem = $"contains #arg '{name}' without #endarg";
                return false;
            }

            optional.Add(new WmlMacroOptionalArgument(name, string.Join("\n", defaultLines).Trim()));
        }

        optionalParameters = optional;
        bodyWithoutDeclarations = remaining;
        return true;
    }

    private static bool TryReadNamedOptionalArgument(
        string rawArgument,
        out string? name,
        out string? value)
    {
        name = null;
        value = null;
        var normalized = rawArgument.Trim();
        if (normalized.Length >= 2 && normalized[0] == '(' && normalized[^1] == ')')
            normalized = normalized[1..^1].Trim();

        var equalsIndex = normalized.IndexOf('=');
        if (equalsIndex <= 0)
            return false;
        var candidateName = normalized[..equalsIndex].Trim();
        if (candidateName.Length == 0 || candidateName.Any(char.IsWhiteSpace))
            return false;

        name = candidateName;
        value = normalized[(equalsIndex + 1)..].Trim();
        return true;
    }

    private static string NormalizeArgumentForSubstitution(string argument)
    {
        var value = argument.Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')) &&
            !value[1..^1].Any(char.IsWhiteSpace))
        {
            return value[1..^1];
        }

        return value;
    }
}

internal sealed record WmlMacroOptionalArgument(string Name, string DefaultValue);
