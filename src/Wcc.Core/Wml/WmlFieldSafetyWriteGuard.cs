namespace Wcc.Core.Wml;

public static class WmlFieldSafetyWriteGuard
{
    public static bool CanWrite(IReadOnlyDictionary<string, WmlFieldSafetyState> fieldStates, string key)
    {
        if (fieldStates is null) throw new ArgumentNullException(nameof(fieldStates));
        if (key is null) throw new ArgumentNullException(nameof(key));

        if (!fieldStates.TryGetValue(key, out var state))
            return true;

        return state.IsEditable;
    }
}