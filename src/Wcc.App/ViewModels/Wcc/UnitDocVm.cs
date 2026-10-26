using Wcc.Core.Units;
using Wcc.Core.Scenarios;

namespace Wcc.App.ViewModels.Wcc;

public sealed class UnitDocVm
{
    public UnitDocVm(UnitTypeDocument doc) => Document = doc;

    public UnitTypeDocument Document { get; }

    public string DisplayName
        => WccWmlPlainTextCodec.TryReadLiteral(Document.Unit.Id, out var id, out var translated) && !translated
            ? id : Document.Unit.Id ?? "(unit_type without id)";
}