using System;
using System.Linq;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels.Wcc;

public sealed class ResolvedWmlValueVm
{
    public ResolvedWmlValueVm(
        string scope,
        string fieldKey,
        string fieldName,
        WmlResolvedValueResult result)
    {
        Scope = scope;
        FieldKey = fieldKey;
        FieldName = fieldName;
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public string Scope { get; }
    public string FieldKey { get; }
    public string FieldName { get; }
    public WmlResolvedValueResult Result { get; }
    public string RawValue => Result.RawValue;
    public string EffectiveValue => Result.EffectiveValue;
    public string State => Result.State switch
    {
        WmlResolvedValueState.Literal => "Literal",
        WmlResolvedValueState.Resolved => "Resolved",
        WmlResolvedValueState.Unresolved => "Unresolved",
        WmlResolvedValueState.Ambiguous => "Ambiguous",
        WmlResolvedValueState.Unsupported => "Unsupported",
        WmlResolvedValueState.Limit => "Limit reached",
        _ => Result.State.ToString()
    };

    public string SourceDefinitions
    {
        get
        {
            var uses = Result.MacroUses
                .GroupBy(x => x.MacroName, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var sources = group
                        .SelectMany(x => x.CandidateDefinitions)
                        .Select(x => x.SourceLabel)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    return sources.Length == 0
                        ? $"{{{group.Key}}}: no visible definition"
                        : $"{{{group.Key}}}: {string.Join(" / ", sources)}";
                })
                .ToArray();
            return string.Join("; ", uses);
        }
    }

    public string Diagnostics => string.Join(
        " ",
        Result.Issues.Select(x => x.Message).Distinct(StringComparer.Ordinal));
}
