using System.Text;
using Wcc.Core.Projects.Creation;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.Core.Campaigns;

/// <summary>Typed local credits edits, preserving every retained unknown field and child byte.</summary>
public static class WccCampaignCreditsPlanner
{
    private static readonly string[] GroupKeys = ["title", "text", "images"];
    private static readonly string[] EntryKeys = ["name", "comment", "email", "wikiuser", "ircuser", "discorduser"];

    public static WccCampaignCreditsSnapshot Read(WmlStaticSource source, WmlStaticScalarOwner? capturedCampaignOwner)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rows = new List<WccCampaignCreditGroup>(); var layouts = new List<WccCampaignCreditLayout>();
        var captured = capturedCampaignOwner;
        if (captured is not null)
            rows.AddRange(captured.DirectChildren.Where(owner => owner.TagName == "about").Take(512).Select(Display));
        try
        {
            if (captured is null || !Parent(source, captured, out var campaignRange))
                throw new InvalidOperationException("Credits require one captured literal campaign parent in this main source; included or macro credits remain available in RAW.");
            var innerStart = campaignRange.StartOffset + "[campaign]".Length;
            var innerLength = campaignRange.Length - "[campaign]".Length - "[/campaign]".Length;
            var scanParent = WmlAuthoredStructureScanner.Scan(source.Text.Substring(innerStart, innerLength));
            if (scanParent.Problem is not null) throw new InvalidOperationException(scanParent.Problem);
            var abouts = WmlBlockScanner.FindTopLevelBlockSpans(scanParent.MaskedText).Where(span => span.TagName == "about")
                .Select(span =>
                {
                    var origin = new WmlStaticSourceRange(source, innerStart + span.StartIndex, span.EndIndexExclusive - span.StartIndex);
                    var literal = new WmlStaticEvaluationResult(origin.Text, [new(0, origin.Text, origin, [])], [], [source], [], true);
                    var projection = WmlStaticScalarProjection.Create(literal);
                    if (projection.Diagnostics.Count != 0) throw new InvalidOperationException("The literal credits group is unresolved; use RAW.");
                    return projection.Owners.Single(owner => owner.TagName == "about" && owner.Parent is null);
                }).ToArray();
            var observed = captured.DirectChildren.Where(owner => owner.TagName == "about").ToArray();
            if (abouts.Length > 512 || !abouts.Select(Identity).SequenceEqual(observed.Select(Identity), StringComparer.Ordinal))
                throw new InvalidOperationException("Some credits are generated or included; they are displayed without flattening their source.");
            rows.Clear();
            foreach (var about in abouts)
            {
                if (!Local(source, about, out var range) || about.DirectChildren.Count(owner => owner.TagName == "entry") > 4096)
                    throw new InvalidOperationException("A credits group is not one complete local literal block.");
                var scan = WmlAuthoredStructureScanner.Scan(range.Text);
                if (scan.Problem is not null || scan.Macros.Count != 0 || scan.Directives.Count != 0)
                    throw new InvalidOperationException("A credits group contains preprocessing; inspect or edit that group in RAW.");
                var actual = observed[layouts.Count];
                var entries = about.DirectChildren.Where(owner => owner.TagName == "entry").ToArray();
                if (!entries.Select(Identity).SequenceEqual(actual.DirectChildren.Where(owner => owner.TagName == "entry").Select(Identity), StringComparer.Ordinal)
                    || entries.Any(entry => !Local(source, entry, out _))
                    || !ValuesAgree(about, actual, GroupKeys)
                    || entries.Where((entry, index) => !ValuesAgree(entry, actual.DirectChildren.Where(owner => owner.TagName == "entry").ElementAt(index), EntryKeys)).Any())
                    throw new InvalidOperationException("Credits values differ from their captured local source; use RAW.");
                var row = Display(about); rows.Add(row); layouts.Add(new(row, about, range, Array.AsReadOnly(entries)));
            }
            return new(source, rows, true, "Literal campaign credits; Save checks all drafts together and preserves retained unknown metadata.", campaignRange, layouts);
        }
        catch (InvalidOperationException exception)
        { return new(source, captured is not null ? captured.DirectChildren.Where(owner => owner.TagName == "about").Take(512).Select(Display) : rows,
            false, exception.Message, null); }
    }

    public static WccCampaignCreditsEdit BuildEdit(WccCampaignCreditsSnapshot snapshot, IEnumerable<WccCampaignCreditGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(groups);
        if (!snapshot.CanEdit || snapshot.CampaignRange is null) throw new InvalidOperationException(snapshot.Diagnostic);
        var rows = groups.ToArray();
        if (rows.Length > 512 || rows.Any(row => row is null || row.Entries is null || row.Entries.Count > 4096 || row.Entries.Any(entry => entry is null)))
            throw new InvalidOperationException("The credits proposal exceeds its bounded group/entry scope.");
        RequireOrder(rows.Select(row => row.Key), snapshot.GroupRows.Select(row => row.Key));
        foreach (var row in rows)
        {
            foreach (var value in GroupValues(row)) Validate(value.Value);
            var original = row.Key is null ? null : snapshot.GroupRows.Single(item => item.Key == row.Key);
            RequireOrder(row.Entries.Select(entry => entry.Key), original?.Entries.Select(entry => entry.Key) ?? []);
            foreach (var entry in row.Entries) foreach (var value in EntryValues(entry)) Validate(value.Value);
        }
        return new(snapshot, rows);
    }

    internal static IReadOnlyList<WccCampaignCreditPatch> PlanPatches(WccCampaignCreditsEdit edit)
    {
        _ = BuildEdit(edit.Snapshot, edit.GroupRows);
        var source = edit.Source; var patches = new List<WccCampaignCreditPatch>();
        foreach (var layout in edit.Snapshot.Layouts)
        {
            var row = edit.GroupRows.SingleOrDefault(item => item.Key == layout.Row.Key);
            if (row is null) { patches.Add(new(layout.Range, "")); continue; }
            if (Equal(row, layout.Row)) continue;
            var local = new List<(int Start, int Length, string Text)>();
            AddFields(layout.Owner, GroupValues(layout.Row), GroupValues(row), local);
            foreach (var entryOwner in layout.Entries)
            {
                var key = Key(entryOwner); var old = layout.Row.Entries.Single(item => item.Key == key);
                var changed = row.Entries.SingleOrDefault(item => item.Key == key);
                if (!Local(source, entryOwner, out var range)) throw new InvalidOperationException("An entry lost its exact local pair.");
                if (changed is null) local.Add((range.StartOffset, range.Length, ""));
                else AddFields(entryOwner, EntryValues(old), EntryValues(changed), local);
            }
            var added = row.Entries.Where(entry => entry.Key is null).ToArray();
            if (added.Length != 0) local.Add((Insertion(source, layout.Owner.ClosingOrigin!.StartOffset), 0,
                string.Concat(added.Select(entry => WriteEntry(entry, Indent(source, layout.Owner.ClosingOrigin!.StartOffset) + "    ", Newline(source))))));
            patches.Add(new(layout.Range, Replace(layout.Range.Text, local.Select(item => (item.Start - layout.Range.StartOffset, item.Length, item.Text)))));
        }
        var newGroups = edit.GroupRows.Where(row => row.Key is null).ToArray();
        if (newGroups.Length != 0)
        {
            var range = edit.Snapshot.CampaignRange!; var close = range.StartOffset + range.Length - "[/campaign]".Length;
            var indent = Indent(source, close); var newline = Newline(source);
            patches.Add(new(new(source, Insertion(source, close), 0), string.Concat(newGroups.Select(row => WriteGroup(row, indent + "    ", newline)))));
        }
        return Array.AsReadOnly(patches.ToArray());

        void AddFields(WmlStaticScalarOwner owner, IReadOnlyDictionary<string, string> old, IReadOnlyDictionary<string, string> proposed,
            List<(int Start, int Length, string Text)> local)
        {
            var insert = new StringBuilder();
            foreach (var (key, value) in proposed)
            {
                if (old[key] == value) continue;
                var encoded = WccCreationSourceWriter.EncodeText(value, key == "title", multiline: true);
                if (owner.Fields.TryGetValue(key, out var field))
                {
                    var contribution = field.Contributions.Single(); var origin = contribution.ValueOrigins.Single();
                    var definition = new ScenarioInitialFieldDefinition("credit_text", "Credits", "Campaign",
                        key is "title" or "text" or "name" or "comment" ? ScenarioInitialFieldKind.TranslatableText : ScenarioInitialFieldKind.Multiline,
                        new(ScenarioInitialFieldDefaultKind.None, null, ""), []);
                    local.Add((origin.StartOffset, origin.Length, WccScenarioFieldValueCodec.EncodeLiteral(definition, value, field.ValueText)));
                }
                else insert.Append(Indent(source, owner.ClosingOrigin!.StartOffset)).Append("    ").Append(key).Append('=').Append(encoded).Append(Newline(source));
            }
            if (insert.Length != 0) local.Add((Insertion(source, owner.ClosingOrigin!.StartOffset), 0, insert.ToString()));
        }
    }

    internal static bool Equal(WccCampaignCreditGroup a, WccCampaignCreditGroup b) =>
        a.Title == b.Title && a.Text == b.Text && a.Images == b.Images && a.Entries.SequenceEqual(b.Entries);
    internal static bool EqualRows(IReadOnlyList<WccCampaignCreditGroup> a, IReadOnlyList<WccCampaignCreditGroup> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => pair.First.Key == pair.Second.Key && Equal(pair.First, pair.Second));
    internal static bool SamePresentation(IReadOnlyList<WccCampaignCreditGroup> a, IReadOnlyList<WccCampaignCreditGroup> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => pair.First.Title == pair.Second.Title && pair.First.Text == pair.Second.Text
            && pair.First.Images == pair.Second.Images && pair.First.Entries.Select(WithoutKey).SequenceEqual(pair.Second.Entries.Select(WithoutKey)));
    private static WccCampaignCreditEntry WithoutKey(WccCampaignCreditEntry entry) => entry with { Key = null };
    private static void Validate(string value)
    { if (WccCreationSourceWriter.ValidateText(value, multiline: true) is { } issue) throw new InvalidOperationException(issue); }
    private static void RequireOrder(IEnumerable<string?> proposed, IEnumerable<string?> originals)
    {
        var existing = proposed.Where(key => key is not null).Cast<string>().ToArray(); var source = originals.Where(key => key is not null).Cast<string>().ToArray();
        if (existing.Distinct(StringComparer.Ordinal).Count() != existing.Length || existing.Any(key => !source.Contains(key, StringComparer.Ordinal))
            || !source.Where(key => existing.Contains(key, StringComparer.Ordinal)).SequenceEqual(existing, StringComparer.Ordinal))
            throw new InvalidOperationException("Existing credits cannot be borrowed, duplicated or reordered; additions append after existing entries.");
        var sawNew = false;
        foreach (var key in proposed) { if (key is null) sawNew = true; else if (sawNew) throw new InvalidOperationException("New credits append after retained source rows."); }
    }
    private static WccCampaignCreditGroup Display(WmlStaticScalarOwner owner) =>
        new(Key(owner), Decode(owner, "title"), Array.AsReadOnly(owner.DirectChildren.Where(child => child.TagName == "entry").Take(4096)
            .Select(child => new WccCampaignCreditEntry(Key(child), Decode(child, "name"), Decode(child, "comment"))
            { Email = Decode(child, "email"), WikiUser = Decode(child, "wikiuser"), IrcUser = Decode(child, "ircuser"), DiscordUser = Decode(child, "discorduser") }).ToArray()))
        { Text = Decode(owner, "text"), Images = Decode(owner, "images") };
    private static string Decode(WmlStaticScalarOwner owner, string key) => owner.Fields.TryGetValue(key, out var field)
        ? WccWmlPlainTextCodec.IsTextField(owner.TagName, key, owner.Parent?.TagName) &&
            WccWmlPlainTextCodec.TryReadLiteral(field.ValueText, out var text, out _)
                ? text : WccScenarioFieldValueCodec.DecodeLiteral(field.ValueText)
        : "";
    private static string? Key(WmlStaticScalarOwner owner) => owner.OpeningOrigin is { } open && owner.ClosingOrigin is { } close
        ? owner.TagName + ":" + open.StartOffset + ":" + (close.StartOffset + close.Length - open.StartOffset) : null;
    private static string Identity(WmlStaticScalarOwner owner) => owner.OpeningOrigin is { } opening && owner.ClosingOrigin is { } closing
        ? opening.Source.Path + ":" + opening.Source.ContentHash + ":" + opening.StartOffset + ":" + closing.StartOffset : "unproven:" + owner.TagName;
    private static bool Parent(WmlStaticSource source, WmlStaticScalarOwner owner, out WmlStaticSourceRange range)
    { range = null!; return owner.TagName == "campaign" && owner.Parent is null && Local(source, owner, out range); }
    private static bool Local(WmlStaticSource source, WmlStaticScalarOwner owner, out WmlStaticSourceRange range)
    {
        range = null!;
        if (!source.IsAuthored || owner.IsAmended || owner.OpeningExpansionStack.Count != 0 || owner.ClosingExpansionStack.Count != 0
            || owner.OpeningOrigin is not { } opening || owner.ClosingOrigin is not { } closing
            || !WccScenarioFieldRouteAnalyzer.SameSource(source, opening.Source) || !WccScenarioFieldRouteAnalyzer.SameSource(source, closing.Source)
            || opening.Text != "[" + owner.TagName + "]" || closing.Text != "[/" + owner.TagName + "]"
            || closing.StartOffset < opening.StartOffset) return false;
        range = new(source, opening.StartOffset, closing.StartOffset + closing.Length - opening.StartOffset); return true;
    }
    private static bool ValuesAgree(WmlStaticScalarOwner a, WmlStaticScalarOwner b, IReadOnlyList<string> keys) =>
        keys.All(key => Literal(a, key) && Literal(b, key) && Decode(a, key) == Decode(b, key));
    private static bool Literal(WmlStaticScalarOwner owner, string key) => !owner.Fields.TryGetValue(key, out var field)
        || field.Contributions.Count == 1 && field.Contributions[0].ExpansionStack.Count == 0
            && field.Contributions[0].ValueExpansionStack.Count == 0 && field.Contributions[0].CompositeAssignmentKeys.Count <= 1
            && field.Contributions[0].ValueMappingIsExact && field.Contributions[0].ValueOrigins.Count == 1
            && field.Contributions[0].AssignmentOrigin is not null && WccCampaignStructurePlanner.Literal(field,
                WccWmlPlainTextCodec.IsTextField(owner.TagName, key, owner.Parent?.TagName));
    private static IReadOnlyDictionary<string, string> GroupValues(WccCampaignCreditGroup row) =>
        new Dictionary<string, string> { ["title"] = row.Title, ["text"] = row.Text, ["images"] = row.Images };
    private static IReadOnlyDictionary<string, string> EntryValues(WccCampaignCreditEntry row) =>
        new Dictionary<string, string> { ["name"] = row.Name, ["comment"] = row.Comment, ["email"] = row.Email,
            ["wikiuser"] = row.WikiUser, ["ircuser"] = row.IrcUser, ["discorduser"] = row.DiscordUser };
    private static string WriteEntry(WccCampaignCreditEntry row, string indent, string newline) =>
        indent + "[entry]" + newline + string.Concat(EntryValues(row).Where(pair => pair.Value.Length != 0)
            .Select(pair => indent + "    " + pair.Key + "=" + WccCreationSourceWriter.EncodeText(pair.Value, false, true) + newline)) + indent + "[/entry]" + newline;
    private static string WriteGroup(WccCampaignCreditGroup row, string indent, string newline) =>
        indent + "[about]" + newline + string.Concat(GroupValues(row).Where(pair => pair.Value.Length != 0)
            .Select(pair => indent + "    " + pair.Key + "=" + WccCreationSourceWriter.EncodeText(pair.Value, pair.Key == "title", true) + newline))
            + string.Concat(row.Entries.Select(entry => WriteEntry(entry, indent + "    ", newline))) + indent + "[/about]" + newline;
    private static string Newline(WmlStaticSource source) => source.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    private static string Indent(WmlStaticSource source, int offset)
    { var start = source.Text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1; var value = source.Text[start..offset]; return value.All(character => character is ' ' or '\t') ? value : ""; }
    private static int Insertion(WmlStaticSource source, int offset) => offset - Indent(source, offset).Length;
    private static string Replace(string source, IEnumerable<(int Start, int Length, string Text)> patches)
    {
        var builder = new StringBuilder(); var position = 0;
        foreach (var patch in patches.OrderBy(patch => patch.Start).ThenBy(patch => patch.Length))
        { if (patch.Start < position) throw new InvalidOperationException("Credits field patches overlap."); builder.Append(source, position, patch.Start - position).Append(patch.Text); position = patch.Start + patch.Length; }
        return builder.Append(source, position, source.Length - position).ToString();
    }
}
