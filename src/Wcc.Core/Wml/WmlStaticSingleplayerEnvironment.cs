using System.Globalization;
using System.Text;

namespace Wcc.Core.Wml;

public enum WmlStaticSingleplayerPlatform { Windows, Linux, MacOS }

/// <summary>
/// An explicitly constructed, fresh desktop singleplayer preprocessor environment
/// without touch emulation. It
/// does not infer hidden command-line or running-game definitions. The caller
/// still supplies its selected campaign, difficulty, and extra symbols.
/// </summary>
public sealed class WmlStaticSingleplayerEnvironment
{
    private WmlStaticSingleplayerEnvironment(string version, WmlStaticSource definitionsSource)
    {
        Version = version;
        DefinitionsSource = definitionsSource;
    }

    public string Version { get; }
    public WmlStaticSource DefinitionsSource { get; }

    /// <summary>
    /// Only exact numeric release triples on supported desktop hosts are bound.
    /// Unknown versions and special builds keep the caller's initial environment open.
    /// Builtin definitions follow tagged config_cache.cpp, including the component
    /// definitions introduced in Wesnoth 1.19.25. File/brace intrinsics are not defines.
    /// </summary>
    public static bool TryCreate(string? exactCoreVersion, out WmlStaticSingleplayerEnvironment? environment,
        WmlStaticSingleplayerPlatform? platform = null)
    {
        environment = null;
        var parts = exactCoreVersion?.Split('.');
        if (parts is not { Length: 3 })
            return false;
        var numbers = new int[3];
        for (var index = 0; index < numbers.Length; index++)
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]) ||
                numbers[index].ToString(CultureInfo.InvariantCulture) != parts[index])
                return false;

        platform ??= OperatingSystem.IsWindows() ? WmlStaticSingleplayerPlatform.Windows :
            OperatingSystem.IsLinux() ? WmlStaticSingleplayerPlatform.Linux :
            OperatingSystem.IsMacOS() ? WmlStaticSingleplayerPlatform.MacOS : null;
        if (platform is null || !Enum.IsDefined(platform.Value))
            return false;

        var text = new StringBuilder();
        Append("WESNOTH_VERSION", exactCoreVersion!);
        if (new Version(numbers[0], numbers[1], numbers[2]) >= new Version(1, 19, 25))
        {
            Append("WESNOTH_VERSION_MAJOR", parts[0]);
            Append("WESNOTH_VERSION_MINOR", parts[1]);
            Append("WESNOTH_VERSION_REVISION", parts[2]);
            Append("WESNOTH_VERSION_SPECIAL", "");
        }
        if (platform == WmlStaticSingleplayerPlatform.MacOS)
            Append("APPLE", "");

        environment = new(exactCoreVersion!, new WmlStaticSource(
            $"wcc-static://singleplayer/{exactCoreVersion}/{platform}/builtins.cfg", text.ToString(), isAuthored: false));
        return true;

        void Append(string name, string value) => text.Append("#define ").Append(name).Append('\n')
            .Append(value).Append("#enddef\n");
    }
}
