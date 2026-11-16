using System.Text.Json;

namespace Tessellation.Configuration;

public sealed class PreferenceStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tessellation",
        "settings.json");

    private static readonly string[] Layouts =
        ["dwindle", "master", "columns", "grid", "monocle", "scrolling"];

    private static readonly string[] Modifiers = ["Alt", "Win", "Ctrl+Win", "Caps Lock"];

    public async Task<TessellationPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            return new TessellationPreferences();
        }

        await using var stream = File.OpenRead(SettingsPath);
        var preferences = await JsonSerializer.DeserializeAsync<TessellationPreferences>(
            stream,
            PreferenceJsonContext.Default.TessellationPreferences,
            cancellationToken: cancellationToken);

        if (preferences is null)
        {
            throw new InvalidDataException("The preferences file is empty or invalid.");
        }

        Validate(preferences);
        return preferences;
    }

    public async Task SaveAsync(
        TessellationPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        Validate(preferences);

        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("The preferences directory could not be determined.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{SettingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    preferences,
                    PreferenceJsonContext.Default.TessellationPreferences,
                    cancellationToken);
            }

            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static void Validate(TessellationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        if (!Layouts.Contains(preferences.Layout, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Unknown layout: {preferences.Layout}");
        }

        if (!Modifiers.Contains(preferences.MainModifier, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Unknown main modifier: {preferences.MainModifier}");
        }

        if (preferences.WorkspaceCount is < 1 or > 20)
        {
            throw new InvalidDataException("Workspace count must be between 1 and 20.");
        }

        if (preferences.WorkspaceNames is null ||
            preferences.WorkspaceNames.Count > 20 ||
            preferences.WorkspaceNames.Any(name => name is null || name.Length > 24))
        {
            throw new InvalidDataException("Workspace names must contain at most 20 labels, each no longer than 24 characters.");
        }

        if (preferences.OuterGap is < 0 or > 64 || preferences.InnerGap is < 0 or > 64)
        {
            throw new InvalidDataException("Window gaps must be between 0 and 64 pixels.");
        }

        if (!double.IsFinite(preferences.MasterRatio) || preferences.MasterRatio is < 0.25 or > 0.75)
        {
            throw new InvalidDataException("The master pane ratio must be between 0.25 and 0.75.");
        }

        if (!double.IsFinite(preferences.ScrollColumnWidth) || preferences.ScrollColumnWidth is < 0.1 or > 1.0)
        {
            throw new InvalidDataException("The scrolling column width must be between 0.1 and 1.0.");
        }

        if (preferences.StatusBarPosition is not ("top" or "bottom"))
        {
            throw new InvalidDataException("The status bar position must be top or bottom.");
        }

        if (preferences.StatusBarHeight is < 24 or > 64)
        {
            throw new InvalidDataException("The status bar height must be between 24 and 64 pixels.");
        }

        if (preferences.AnimationDurationMs is < 0 or > 250)
        {
            throw new InvalidDataException("Animation duration must be between 0 and 250 milliseconds.");
        }
    }
}
