namespace web.Components.Devices.FwDevice;

/// <summary>
/// A place in the Configuration tab: a section, and in its grid the row of block Number (1 based, 0 = the section).
/// Sections are the cfg-* classes on the Configuration tab's panels.
/// </summary>
public readonly record struct ConfigTarget(string Section, int Number = 0);

internal static class ConfigJumpInterop
{
    // Version query busts the browser cache when the app is updated
    public static readonly string ModulePath =
        $"./js/config-jump.js?v={typeof(ConfigJumpInterop).Assembly.GetName().Version}";
}
