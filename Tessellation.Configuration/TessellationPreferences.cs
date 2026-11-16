namespace Tessellation.Configuration;

public sealed class TessellationPreferences
{
    public string Layout { get; set; } = "dwindle";

    public string MainModifier { get; set; } = "Alt";

    public int WorkspaceCount { get; set; } = 9;

    public List<string> WorkspaceNames { get; set; } = [];

    public int OuterGap { get; set; } = 8;

    public int InnerGap { get; set; } = 8;

    public double MasterRatio { get; set; } = 0.55;

    public double ScrollColumnWidth { get; set; } = 0.5;

    public string StatusBarPosition { get; set; } = "top";

    public int StatusBarHeight { get; set; } = 40;

    public bool StatusBarWheelWorkspace { get; set; } = true;

    public int AnimationDurationMs { get; set; } = 140;

    public bool FocusFollowsMouse { get; set; }

    public bool AltDrag { get; set; } = true;

    public bool StartWithWindows { get; set; }
}
