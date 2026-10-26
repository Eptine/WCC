using Wcc.Core.Projects.Creation;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Units;

/// <summary>Scopes explicitly edited local literal strings; borrowed and RAW source is never rebound.</summary>
internal static class UnitTypeStructuredTextDomainComposer
{
    internal static IReadOnlySet<string> SelectFields(UnitTypeDocument document, IReadOnlySet<string> touched)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in touched)
        {
            var attack = FindAttack(document, key);
            var editable = attack is not null
                ? WmlFieldSafetyWriteGuard.CanWrite(attack.Attack.FieldStates, "description")
                : (key is "name" or "description") && WmlFieldSafetyWriteGuard.CanWrite(document.Unit.FieldStates, key);
            if (editable && Value(document, key) is { } value && WccWmlPlainTextCodec.TryReadLiteral(value, out _, out _)) fields.Add(key);
        }
        return fields;
    }

    internal static string Compose(UnitTypeDocument document, string raw, string destination,
        IReadOnlySet<string> fields, string? renderedPrefix)
    {
        if (WccCampaignCreationTokenPolicy.ValidateTextDomain(destination) is { } problem)
            throw new ArgumentException(problem, nameof(destination));
        if (fields.Count == 0) return raw;
        var structure = WmlAuthoredStructureScanner.Scan(raw);
        if (structure.Problem is not null || !WmlSingleBlockValidator.Validate(raw, "unit_type").IsValid)
            throw new InvalidOperationException("The edited unit's literal string boundary cannot be proved. Edit its RAW source.");
        var block = WmlEditableBlockParser.Parse(new("unit_type", raw), UnitTypeSchema.KnownKeys);
        var replacements = new List<(int Start, int End, string Text)>();
        foreach (var key in fields.Order(StringComparer.Ordinal))
        {
            var requested = Value(document, key)!;
            var attack = FindAttack(document, key);
            var target = block;
            var targetKey = key;
            var lineBase = 0;
            if (attack is not null)
            {
                var slots = WmlDirectChildBlockScanner.FindSpans(block, "attack");
                var index = document.AttackDocuments.IndexOf(attack);
                if (index < 0 || slots.Count != document.AttackDocuments.Count)
                    throw new InvalidOperationException("The edited attack description has no exact local rendered slot. Edit its RAW source.");
                var slot = slots[index];
                var childRaw = string.Join(block.NewLine, block.Lines.Skip(slot.StartLineIndex).Take(slot.EndLineIndexExclusive - slot.StartLineIndex));
                target = WmlEditableBlockParser.Parse(new("attack", childRaw), AttackSchema.KnownKeys);
                targetKey = "description";
                lineBase = slot.StartLineIndex;
            }
            var state = WmlFieldSafetyAnalyzer.AnalyzeTopLevelField(target, targetKey);
            if (!state.IsEditable)
                throw new InvalidOperationException("The edited unit " + key + " needs one unconditional local assignment. Edit its RAW source.");
            int startLine, endLine;
            string indent, assignment, suffix;
            var changedString = IsTranslatedLiteral(requested);
            if (target.Known.TryGetValue(targetKey, out var entry))
            {
                if (!WmlBoundedLiteralAssignment.TryRead(target, targetKey, out var value))
                    throw new InvalidOperationException("The edited unit " + key + " has a composite, macro or unbounded value. Its translation domain must be set in RAW source.");
                startLine = value.StartLineIndex;
                endLine = value.EndLineIndexExclusive;
                if (value.Value == requested.Trim()) continue;
                if (!WccWmlPlainTextCodec.TryReadLiteral(value.Value, out _, out _))
                    throw new InvalidOperationException("The edited unit " + key + " has a composite or macro value. Its translation domain must be set in RAW source.");
                changedString = IsTranslatedLiteral(requested) && (!IsTranslatedLiteral(value.Value)
                    || !WccWmlPlainTextCodec.HasSameTranslationIdentity(value.Value, requested));
                indent = entry.LeadingWhitespace;
                assignment = indent + entry.Key + entry.PreEqualsWhitespace + "=" + entry.PostEqualsWhitespace + requested;
                suffix = value.InlineCommentSuffix;
            }
            else
            {
                startLine = endLine = target.ClosingTagLineIndex;
                indent = target.Known.Values.FirstOrDefault()?.LeadingWhitespace ?? target.DefaultNewKeyIndent;
                assignment = indent + targetKey + "=" + requested;
                suffix = "";
            }
            var offset = structure.Lines[lineBase + startLine].StartOffset;
            var replacement = assignment + suffix + block.NewLine;
            if (changedString)
            {
                var prefix = renderedPrefix ?? document.OriginalText[..document.BlockSpan.StartIndex];
                var domain = WmlTextDomainCapture.BeforeOffset(prefix + raw, prefix.Length + offset);
                if (domain is null || WccCampaignCreationTokenPolicy.ValidateTextDomain(domain) is not null)
                    throw new InvalidOperationException("The edited unit " + key + " needs a proved prior translation domain. Set it explicitly in RAW source.");
                if (domain != destination)
                    replacement = indent + "#textdomain " + destination + block.NewLine + replacement
                        + indent + "#textdomain " + domain + block.NewLine;
            }
            replacements.Add((offset, endLine == startLine ? offset : structure.Lines[lineBase + endLine - 1].EndOffsetExclusive, replacement));
        }
        foreach (var edit in replacements.OrderByDescending(edit => edit.Start))
            raw = raw[..edit.Start] + edit.Text + raw[edit.End..];
        return raw;
    }

    private static string? Value(UnitTypeDocument document, string key)
        => key == "name" ? document.Unit.NameWml : key == "description" ? document.Unit.DescriptionWml : FindAttack(document, key)?.Attack.DescriptionWml;
    private static AttackDocument? FindAttack(UnitTypeDocument document, string key)
    {
        if (!key.StartsWith("attack:", StringComparison.Ordinal)) return null;
        var matches = document.AttackDocuments.Where(attack => key == "attack:" + attack.InternalId + ":description").ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("The edited attack description has ambiguous document identity.");
        return matches.SingleOrDefault();
    }
    private static bool IsTranslatedLiteral(string? raw)
        => WccWmlPlainTextCodec.TryReadLiteral(raw, out _, out var translated) && translated;
}
