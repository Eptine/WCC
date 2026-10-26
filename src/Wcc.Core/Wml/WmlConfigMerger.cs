namespace Wcc.Core.Wml;

/// <summary>
/// Applies Wesnoth's recursive WML merge semantics to two container blocks.
/// Attributes are overridden by name, while the nth child tag in the overlay
/// merges into the nth child tag with the same name in the base container.
///
/// This is intentionally independent of unit animations so the same effective
/// configuration projection can later be reused by variations, base units,
/// events, schedules, and other editors.
/// </summary>
public static class WmlConfigMerger
{
    public static string Merge(string baseText, string overlayText)
    {
        if (baseText is null) throw new ArgumentNullException(nameof(baseText));
        if (overlayText is null) throw new ArgumentNullException(nameof(overlayText));

        var baseNode = ConfigNode.Parse(baseText);
        var overlayNode = ConfigNode.Parse(overlayText);
        if (!string.Equals(baseNode.TagName, overlayNode.TagName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot merge [{overlayNode.TagName}] into [{baseNode.TagName}].");
        }

        var newLine = baseText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return baseNode.Merge(overlayNode).Render(newLine);
    }

    private sealed class ConfigNode(string tagName)
    {
        public string TagName { get; } = tagName;
        public List<ConfigElement> Elements { get; } = new();

        public static ConfigNode Parse(string rawText)
        {
            var lines = rawText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var index = 0;
            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
                index++;

            if (index >= lines.Length || !TryReadOpeningTag(lines[index], out _))
                throw new InvalidOperationException("Expected one WML container block.");

            return ParseNode(lines, ref index);
        }

        public ConfigNode Merge(ConfigNode overlay)
        {
            var result = Clone();

            foreach (var attribute in overlay.Elements.OfType<AttributeElement>())
            {
                if (IsMergeMetadata(attribute.Key))
                    continue;

                var existing = result.Elements
                    .OfType<AttributeElement>()
                    .FirstOrDefault(x => string.Equals(x.Key, attribute.Key, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.Value = attribute.Value;
                    continue;
                }

                var insertIndex = result.Elements.FindIndex(x => x is ChildElement);
                if (insertIndex < 0)
                    insertIndex = result.Elements.Count;
                result.Elements.Insert(insertIndex, attribute.Clone());
            }

            var baseChildren = result.Elements
                .OfType<ChildElement>()
                .GroupBy(x => x.Node.TagName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
            var overlayOrdinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var overlayChild in overlay.Elements.OfType<ChildElement>())
            {
                var childName = overlayChild.Node.TagName;
                var ordinal = overlayOrdinals.GetValueOrDefault(childName);
                overlayOrdinals[childName] = ordinal + 1;

                baseChildren.TryGetValue(childName, out var matches);
                if (matches is not null && ordinal < matches.Length)
                {
                    var matched = matches[ordinal];
                    if (overlayChild.Node.ReadBooleanMetadata("__remove"))
                    {
                        result.Elements.Remove(matched);
                    }
                    else if (overlayChild.Node.ReadBooleanMetadata("__replace"))
                    {
                        matched.Node = overlayChild.Node.WithoutMergeMetadata();
                    }
                    else
                    {
                        matched.Node = matched.Node.Merge(overlayChild.Node);
                    }

                    continue;
                }

                if (!overlayChild.Node.ReadBooleanMetadata("__remove"))
                    result.Elements.Add(new ChildElement(overlayChild.Node.WithoutMergeMetadata()));
            }

            var overlayProvenance = overlay.Elements
                .OfType<RawElement>()
                .FirstOrDefault(x => x.Value.TrimStart().StartsWith(
                    WmlMacroExpansionService.ProvenanceMarkerPrefix,
                    StringComparison.Ordinal));
            if (overlayProvenance is not null)
            {
                result.Elements.RemoveAll(x =>
                    x is RawElement raw && raw.Value.TrimStart().StartsWith(
                        WmlMacroExpansionService.ProvenanceMarkerPrefix,
                        StringComparison.Ordinal));
                result.Elements.Insert(0, overlayProvenance.Clone());
            }

            // Preprocessing normally consumes these lines before a merge. If a
            // bounded preview could not resolve one, retain the overlay line so
            // downstream fidelity reporting can surface the limitation.
            foreach (var raw in overlay.Elements.OfType<RawElement>())
            {
                if (string.IsNullOrWhiteSpace(raw.Value) ||
                    raw.Value.TrimStart().StartsWith('#'))
                {
                    continue;
                }

                if (!result.Elements.OfType<RawElement>().Any(x =>
                        string.Equals(x.Value.Trim(), raw.Value.Trim(), StringComparison.Ordinal)))
                {
                    result.Elements.Add(raw.Clone());
                }
            }

            return result;
        }

        public string Render(string newLine)
        {
            var lines = new List<string>();
            RenderInto(lines, depth: 0);
            return string.Join(newLine, lines);
        }

        private void RenderInto(List<string> lines, int depth)
        {
            var indent = new string(' ', depth * 4);
            var childIndent = new string(' ', (depth + 1) * 4);
            lines.Add($"{indent}[{TagName}]");
            foreach (var element in Elements)
            {
                switch (element)
                {
                    case AttributeElement attribute when !IsMergeMetadata(attribute.Key):
                        lines.Add($"{childIndent}{attribute.Key}={attribute.Value}");
                        break;
                    case RawElement raw:
                        lines.Add(raw.Value.Length == 0 ? string.Empty : childIndent + raw.Value.TrimStart());
                        break;
                    case ChildElement child:
                        child.Node.RenderInto(lines, depth + 1);
                        break;
                }
            }
            lines.Add($"{indent}[/{TagName}]");
        }

        public ConfigNode Clone()
        {
            var clone = new ConfigNode(TagName);
            foreach (var element in Elements)
                clone.Elements.Add(element.CloneElement());
            return clone;
        }

        private ConfigNode WithoutMergeMetadata()
        {
            var clone = Clone();
            clone.Elements.RemoveAll(x =>
                x is AttributeElement attribute && IsMergeMetadata(attribute.Key));
            return clone;
        }

        private bool ReadBooleanMetadata(string key)
        {
            var raw = Elements.OfType<AttributeElement>()
                .FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))
                ?.Value;
            var normalized = raw?.Trim().Trim('"', '\'').ToLowerInvariant();
            return normalized is "yes" or "true" or "1" or "on";
        }

        private static ConfigNode ParseNode(IReadOnlyList<string> lines, ref int index)
        {
            if (!TryReadOpeningTag(lines[index], out var tagName))
                throw new InvalidOperationException("Expected a WML opening tag.");

            var node = new ConfigNode(tagName!);
            index++;
            while (index < lines.Count)
            {
                var raw = lines[index];
                var trimmed = raw.Trim();
                if (TryReadClosingTag(trimmed, node.TagName))
                {
                    index++;
                    return node;
                }

                if (TryReadOpeningTag(raw, out _))
                {
                    node.Elements.Add(new ChildElement(ParseNode(lines, ref index)));
                    continue;
                }

                if (TryReadAttributes(raw, out var attributes))
                {
                    node.Elements.AddRange(attributes);
                }
                else if (trimmed.Length > 0)
                {
                    node.Elements.Add(new RawElement(trimmed));
                }

                index++;
            }

            throw new InvalidOperationException($"Missing closing tag for [{node.TagName}].");
        }

        private static bool TryReadAttributes(string raw, out IReadOnlyList<AttributeElement> attributes)
        {
            attributes = Array.Empty<AttributeElement>();
            var code = WmlInlineCommentSplitter.Split(raw).Value.Trim();
            if (code.Length == 0 || code.StartsWith('#') || code.StartsWith('{') || code.StartsWith('['))
                return false;

            var equals = code.IndexOf('=');
            if (equals <= 0)
                return false;

            var keys = code[..equals]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (keys.Length == 0 || keys.Any(x => x.Any(char.IsWhiteSpace)))
                return false;

            var value = code[(equals + 1)..].Trim();
            if (keys.Length == 1)
            {
                attributes = [new AttributeElement(keys[0], value)];
                return true;
            }

            var values = SplitPairedValues(value, keys.Length);
            if (values.Count != keys.Length)
                return false;

            attributes = keys.Select((key, i) => new AttributeElement(key, values[i])).ToArray();
            return true;
        }

        private static IReadOnlyList<string> SplitPairedValues(string value, int expectedCount)
        {
            var result = new List<string>();
            var start = 0;
            char? quote = null;
            for (var i = 0; i < value.Length; i++)
            {
                if (quote is null && value[i] is '"' or '\'')
                    quote = value[i];
                else if (quote == value[i])
                    quote = null;
                else if (quote is null && value[i] == ',' && result.Count < expectedCount - 1)
                {
                    result.Add(value[start..i].Trim());
                    start = i + 1;
                }
            }

            result.Add(value[start..].Trim());
            return result;
        }

        private static bool TryReadOpeningTag(string raw, out string? tagName)
        {
            tagName = null;
            var trimmed = raw.Trim();
            if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']') || trimmed.StartsWith("[/", StringComparison.Ordinal))
                return false;

            var inner = trimmed[1..^1].Trim();
            if (inner.StartsWith('+'))
                inner = inner[1..].TrimStart();
            var whitespace = inner.IndexOfAny([' ', '\t']);
            tagName = (whitespace < 0 ? inner : inner[..whitespace]).Trim();
            return tagName.Length > 0;
        }

        private static bool TryReadClosingTag(string trimmed, string expectedTagName)
        {
            if (!trimmed.StartsWith("[/", StringComparison.Ordinal) || !trimmed.EndsWith(']'))
                return false;

            var name = trimmed[2..^1].Trim();
            return string.Equals(name, expectedTagName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMergeMetadata(string key)
            => string.Equals(key, "__remove", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "__replace", StringComparison.OrdinalIgnoreCase);
    }

    private abstract class ConfigElement
    {
        public abstract ConfigElement CloneElement();
    }

    private sealed class AttributeElement(string key, string value) : ConfigElement
    {
        public string Key { get; } = key;
        public string Value { get; set; } = value;
        public AttributeElement Clone() => new(Key, Value);
        public override ConfigElement CloneElement() => Clone();
    }

    private sealed class RawElement(string value) : ConfigElement
    {
        public string Value { get; } = value;
        public RawElement Clone() => new(Value);
        public override ConfigElement CloneElement() => Clone();
    }

    private sealed class ChildElement(ConfigNode node) : ConfigElement
    {
        public ConfigNode Node { get; set; } = node;
        public override ConfigElement CloneElement() => new ChildElement(Node.Clone());
    }
}
