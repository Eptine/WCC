namespace Wcc.Core.Wml;

public enum WmlFieldSafetyKind
{
    MissingEditable,
    PlainEditable,
    ConditionalOrMultipleReadOnly,
    UnsafeTopLevelCodeReadOnly
}