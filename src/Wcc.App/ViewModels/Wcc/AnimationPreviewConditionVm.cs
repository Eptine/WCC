using System;
using System.Collections.Generic;
using System.Linq;

namespace Wcc.App.ViewModels.Wcc;

public sealed class AnimationPreviewConditionVm : ViewModelBase
{
    public const string UnsetValue = "(not set)";

    private string _selectedValue = UnsetValue;

    public AnimationPreviewConditionVm(
        string key,
        IEnumerable<string> values,
        string? selectedValue = null)
    {
        Key = key;
        DisplayName = Humanize(key);
        Options = new[] { UnsetValue }
            .Concat(values.Where(x => !string.IsNullOrWhiteSpace(x)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(selectedValue) &&
            Options.Contains(selectedValue, StringComparer.OrdinalIgnoreCase))
        {
            _selectedValue = Options.First(
                x => string.Equals(x, selectedValue, StringComparison.OrdinalIgnoreCase));
        }
    }

    public event EventHandler? SelectionChanged;

    public string Key { get; }
    public string DisplayName { get; }
    public IReadOnlyList<string> Options { get; }

    public string SelectedValue
    {
        get => _selectedValue;
        set
        {
            if (SetField(ref _selectedValue, value))
                SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? EffectiveValue =>
        string.Equals(SelectedValue, UnsetValue, StringComparison.Ordinal)
            ? null
            : SelectedValue;

    private static string Humanize(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "Condition";

        var words = key.Replace('_', ' ').Trim();
        return char.ToUpperInvariant(words[0]) + words[1..];
    }
}
