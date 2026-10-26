using System;
using Wcc.App.ViewModels;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

public sealed class ScenarioSideVm : ViewModelBase
{
    public ScenarioSideVm(ScenarioSideDocument document) => AuthoredDocument = document;

    internal ScenarioSideVm(WmlStaticScalarOwner owner, string identity)
    {
        EffectiveOwner = owner;
        EffectiveIdentity = identity;
    }

    public ScenarioSideDocument? AuthoredDocument { get; }
    public bool IsGenerated => AuthoredDocument is null;
    public ScenarioSideDocument Document => AuthoredDocument ??
        throw new InvalidOperationException("A generated side has no authored side document.");
    public ScenarioSide Side => Document.Side;
    public WmlStaticScalarOwner? EffectiveOwner { get; internal set; }
    internal string? EffectiveIdentity { get; }

    // Effective values are display evidence only, never authored save inputs.
    public string? SideDisplayNumber { get; private set; }
    private bool _hasEffectiveNumber;

    public string? DisplayNumber => _hasEffectiveNumber ? SideDisplayNumber :
        AuthoredDocument?.Side.SideNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public void SetEffectiveNumber(string? number)
    {
        if (_hasEffectiveNumber && SideDisplayNumber == number)
            return;
        _hasEffectiveNumber = true;
        SideDisplayNumber = number;
        OnPropertyChanged(nameof(SideDisplayNumber));
        Refresh();
    }

    public void ClearEffectiveNumber()
    {
        if (!_hasEffectiveNumber)
            return;
        _hasEffectiveNumber = false;
        SideDisplayNumber = null;
        OnPropertyChanged(nameof(SideDisplayNumber));
        Refresh();
    }

    public string DisplayName
    {
        get
        {
            var number = DisplayNumber;
            var label = number is null ? "Side (number unresolved)" : $"Side {number}";
            return IsGenerated ? $"{label} · {(EffectiveOwner?.OpeningExpansionStack.Count > 0 ? "macro" : "included")}" :
                Document.IsConditional ? $"{label} · conditional" : label;
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(DisplayNumber));
        OnPropertyChanged(nameof(DisplayName));
    }
}