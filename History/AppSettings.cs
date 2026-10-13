using System.Text;
using System.Text.Json;

namespace DocumentChecker.Engine.History;

/// <summary>
/// The reviewer's choices that are kept between sessions, in settings.json next to the history
/// (in the user's application-data folder on this computer).
/// </summary>
public sealed record AppSettings(bool CompareHeadersAndFooters = true)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The settings file. DOCHECK_SETTINGS_FILE points tests and trial runs at their own file.</summary>
    public static string FilePath => Environment.GetEnvironmentVariable("DOCHECK_SETTINGS_FILE") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "DocumentChecker", "settings.json");

    /// <summary>The saved settings, or the defaults when there are none or the file cannot be read.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>Saves the settings. A problem saving them never stops the app; the choice then lasts for this session.</summary>
    public void Save(string? path = null)
    {
        path ??= FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("The settings could not be saved: " + exception.Message);
        }
    }
}
