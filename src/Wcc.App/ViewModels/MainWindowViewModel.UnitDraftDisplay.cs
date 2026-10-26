using System;
using System.Collections.Generic;
using System.Linq;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Library;
using Wcc.Core.Units;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

internal sealed record UnitDraftDisplayCandidate(
    string Text, int SelectedUnitOrdinal, UnitTypeDocument SelectedDocument);

public partial class MainWindowViewModel
{
    private UnitDraftDisplayCandidate? _unitEffectiveDraftCandidate;
    private UnitTypeDocument? _unitEffectiveDraftSourceDocument;
    private string? _unitEffectiveDraftContentHash;
    private bool _unitEffectiveChildStructureDirty;

    // Rendering for evaluation must not advance the authoritative editable blocks or save baseline.
    internal UnitDraftDisplayCandidate? CaptureUnitDraftDisplayCandidate()
    {
        if (SelectedUnit is null) return null;
        var ordinal = Units.IndexOf(SelectedUnit);
        if (ordinal < 0) return null;
        PushUiEditsIntoModel();
        try
        {
            var text = UnitTypeFileRenderer.RenderPatchedFile(
                Units.Select(unit => CloneUnitDraftDocument(unit.Document)).ToArray());
            var documents = UnitTypeImporter.ImportAllFromText(text);
            return ordinal < documents.Count
                ? new UnitDraftDisplayCandidate(text, ordinal, documents[ordinal]) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    // The host calls this only after the matching captured-scope/draft/selection fences pass.
    internal void SetUnitEffectiveDraftCandidate(UnitDraftDisplayCandidate? candidate)
    {
        _unitEffectiveDraftCandidate = candidate;
        _unitEffectiveDraftSourceDocument = candidate is null ? null : SelectedUnit?.Document;
        _unitEffectiveDraftContentHash = candidate is null ? null : WccContentHash.ComputeText(candidate.Text);
    }

    private static UnitTypeDocument CloneUnitDraftDocument(UnitTypeDocument document)
    {
        var clone = new UnitTypeDocument
        {
            InternalId = document.InternalId,
            OriginalText = document.OriginalText,
            BlockSpan = document.BlockSpan,
            EditableBlock = CloneUnitDraftBlock(document.EditableBlock),
            Unit = document.Unit
        };
        // Patchers read these scalar models synchronously and write only the cloned blocks.
        foreach (var attack in document.AttackDocuments)
            clone.AttackDocuments.Add(new AttackDocument
            {
                InternalId = attack.InternalId, Span = attack.Span,
                EditableBlock = CloneUnitDraftBlock(attack.EditableBlock), Attack = attack.Attack
            });
        clone.AnimationDocuments.AddRange(document.AnimationDocuments.Select(CloneUnitDraftAnimation));
        foreach (var variant in document.GenderVariantDocuments)
        {
            var copy = new UnitGenderVariantDocument
            {
                InternalId = variant.InternalId, Kind = variant.Kind, Span = variant.Span,
                EditableBlock = CloneUnitDraftBlock(variant.EditableBlock), Variant = variant.Variant
            };
            copy.AnimationDocuments.AddRange(variant.AnimationDocuments.Select(CloneUnitDraftAnimation));
            clone.GenderVariantDocuments.Add(copy);
        }
        return clone;
    }

    private static UnitAnimationDocument CloneUnitDraftAnimation(UnitAnimationDocument document) => new()
    {
        InternalId = document.InternalId, TagName = document.TagName, Span = document.Span,
        EditableBlock = CloneUnitDraftBlock(document.EditableBlock),
        ApplyTo = document.ApplyTo, Flag = document.Flag
    };

    private static WmlEditableBlock CloneUnitDraftBlock(WmlEditableBlock block)
    {
        var clone = new WmlEditableBlock
        {
            TagName = block.TagName, Lines = block.Lines.ToList(),
            OpeningTagLineIndex = block.OpeningTagLineIndex,
            ClosingTagLineIndex = block.ClosingTagLineIndex,
            NewLine = block.NewLine, DefaultNewKeyIndent = block.DefaultNewKeyIndent
        };
        foreach (var entry in block.Known) clone.Known.Add(entry.Key, entry.Value);
        return clone;
    }

    private UnitTypeDocument? LocalEffectiveMappingDocument(WmlStaticScalarOwner? owner)
    {
        if (SelectedUnit is not { } selected || owner?.OpeningOrigin is not { } origin ||
            !string.Equals(origin.Source.Path, _currentSourcePath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        if (_unitEffectiveDraftCandidate is { } candidate &&
            ReferenceEquals(_unitEffectiveDraftSourceDocument, selected.Document) &&
            candidate.SelectedUnitOrdinal == Units.IndexOf(selected) &&
            origin.Source.ContentHash == _unitEffectiveDraftContentHash &&
            origin.StartOffset >= candidate.SelectedDocument.BlockSpan.StartIndex &&
            origin.StartOffset < candidate.SelectedDocument.BlockSpan.EndIndexExclusive)
            return candidate.SelectedDocument;
        var document = selected.Document;
        return !_unitEffectiveChildStructureDirty &&
            origin.Source.ContentHash == WccContentHash.ComputeText(document.OriginalText) &&
            document.EditableBlock.ToRawText() == document.BlockSpan.RawText &&
            origin.StartOffset >= document.BlockSpan.StartIndex &&
            origin.StartOffset < document.BlockSpan.EndIndexExclusive ? document : null;
    }

    private static int LocalChildOpeningOffset(UnitTypeDocument document, WmlLineBlockSpan span)
    {
        if (span.StartLineIndex < 0 || span.StartLineIndex >= document.EditableBlock.Lines.Count) return -1;
        var bracket = document.EditableBlock.Lines[span.StartLineIndex].IndexOf('[');
        if (bracket < 0) return -1;
        return document.BlockSpan.StartIndex +
            string.Join(document.EditableBlock.NewLine, document.EditableBlock.Lines.Take(span.StartLineIndex)).Length +
            (span.StartLineIndex == 0 ? 0 : document.EditableBlock.NewLine.Length) + bracket;
    }

    private void InvalidateEffectiveChildStructure()
    {
        InvalidateCapturedImageResourceContext();
        SetUnitEffectiveDraftCandidate(null);
        _unitEffectiveChildStructureDirty = true;
    }

    private (WccUnitTypeEffectiveNode Node, AttackDocument Preview)? NextProvenInheritedAttack()
    {
        if (SelectedUnit is null || _unitEffectiveDisplay is null) return null;
        var ordinal = SelectedUnit.Document.AttackDocuments.Count;
        var nodes = _unitEffectiveDisplay.Root.Children.Where(child => child.TagName == "attack").ToArray();
        if (ordinal >= nodes.Length || nodes[ordinal] is not { IsStructureKnown: true, LocalOwner: null } node) return null;
        // A RAW removal or macro-expanded child can shift the merged display list.
        // Carry only when every preceding authored overlay still has its exact slot.
        if (Enumerable.Range(0, ordinal).Any(index => Attacks.ElementAtOrDefault(index) is not { IsInherited: false } row ||
            !ReferenceEquals(row.Attack, SelectedUnit.Document.AttackDocuments[index].Attack) ||
            !ReferenceEquals(_effectiveAttackNodes.GetValueOrDefault(row), nodes[index]))) return null;
        var row = Attacks.FirstOrDefault(attack => attack.IsInherited &&
            _effectiveAttackNodes.GetValueOrDefault(attack) == node);
        return row is not null && _effectiveAttackDocuments.TryGetValue(row, out var preview)
            ? (node, preview) : null;
    }

    private void CarryProvenAttackOverlay(AttackVm local,
        (WccUnitTypeEffectiveNode Node, AttackDocument Preview)? inherited)
    {
        if (inherited is not { } overlay) return;
        var previous = Attacks.FirstOrDefault(row => row.IsInherited &&
            _effectiveAttackNodes.GetValueOrDefault(row) == overlay.Node);
        if (previous is not null)
        {
            Attacks.Remove(previous);
            _effectiveAttackNodes.Remove(previous);
            _effectiveAttackDocuments.Remove(previous);
            previous.Dispose();
        }
        _effectiveAttackNodes[local] = overlay.Node;
        _effectiveAttackDocuments[local] = overlay.Preview;
    }
}
