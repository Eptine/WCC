namespace Wcc.Core.Wml;

public enum WmlTopLevelCodeKind
{
    None,

    ConditionalIfStart,
    ConditionalElse,
    ConditionalEndIf,

    MacroDefinitionStart,
    MacroDefinitionEnd,

    MacroInvocation,
    BangCommand,

    OtherHashDirective
}