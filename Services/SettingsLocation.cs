namespace HisaabKitaab.Services;

/// <summary>
/// Remembers where settings.json lives when the user moves it. The app has to
/// know this before it can read any settings, so it's kept in a small
/// "pointer" file in the default settings folder, holding the chosen folder's path.
/// </summary>
public static class SettingsLocation
{
    public const string PointerFileName = "settings-location.txt";

    /// <summary>
    /// %APPDATA%\HisaabKitaab\settings-location.txt (~/.config/HisaabKitaab/ on Linux).
    /// </summary>
    public static string DefaultPointerPath =>
        Path.Combine(Path.GetDirectoryName(SettingsService.DefaultFilePath)!, PointerFileName);

    /// <summary>
    /// The settings file to use: the one in the folder the pointer names, or
    /// <paramref name="defaultFilePath"/> if there's no pointer. If the folder
    /// can't be found (e.g. a drive that isn't plugged in), falls back to the
    /// default and explains in <paramref name="warning"/>; the pointer is kept
    /// so the folder is used again once it's back.
    /// </summary>
    public static string Resolve(string pointerPath, string defaultFilePath, out string? warning)
    {
        warning = null;
        if (!File.Exists(pointerPath))
            return defaultFilePath;

        string folder;
        try
        {
            folder = File.ReadAllText(pointerPath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = $"The note of where your settings are kept couldn't be read ({ex.Message}), so the default settings file was used.";
            return defaultFilePath;
        }

        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
            return defaultFilePath;

        if (!Directory.Exists(folder))
        {
            warning = $"Your settings folder ({folder}) couldn't be found, so the settings in {Path.GetDirectoryName(defaultFilePath)} were used. " +
                      "If it's on a drive that isn't connected, connect it and restart Hisaab Kitaab.";
            return defaultFilePath;
        }

        return Path.Combine(folder, "settings.json");
    }

    /// <summary>
    /// Points at <paramref name="folder"/>, or removes the pointer (back to the default) if null.
    /// </summary>
    public static void Write(string pointerPath, string? folder)
    {
        if (folder is null)
        {
            if (File.Exists(pointerPath))
                File.Delete(pointerPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(pointerPath)!);
        var tempPath = pointerPath + ".tmp";
        File.WriteAllText(tempPath, folder);
        File.Move(tempPath, pointerPath, overwrite: true);
    }
}
