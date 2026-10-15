using System.Text.Json;

namespace Reminders.Windows.Services;

internal static class UiPreferences
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemindersForWindows");
#if DEBUG
    static UiPreferences()
    {
        if (Environment.GetEnvironmentVariable("REMINDERS_DATA_DIR") is { Length: > 0 } developmentData)
            DirectoryPath = Path.Combine(Path.GetFullPath(developmentData), "ui");
    }
#endif
    private static string FilePath(bool localDemo) => Path.Combine(DirectoryPath, localDemo ? "demo-ui-settings.json" : "ui-settings.json");

    public static bool LoadNavigationPaneOpen(bool localDemo = false)
    {
        var filePath = FilePath(localDemo);
        try
        {
            lock (Gate)
            {
                if (!File.Exists(filePath)) return true;
                using var document = JsonDocument.Parse(File.ReadAllText(filePath));
                return !document.RootElement.TryGetProperty("navigation_pane_open", out var value) || value.GetBoolean();
            }
        }
        catch (Exception error)
        {
            AppLog.Error("Could not load UI preferences", error);
            return true;
        }
    }

    public static void SaveNavigationPaneOpen(bool isOpen, bool localDemo = false)
    {
        var filePath = FilePath(localDemo);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var temporary = filePath + ".tmp";
                var settings = Load(localDemo);
                settings.NavigationPaneOpen = isOpen;
                File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
                File.Move(temporary, filePath, true);
            }
        }
        catch (Exception error) { AppLog.Error("Could not save UI preferences", error); }
    }

    public static UiPreferenceData Load(bool localDemo = false)
    {
        var filePath = FilePath(localDemo);
        try
        {
            lock (Gate)
                return File.Exists(filePath) ? JsonSerializer.Deserialize<UiPreferenceData>(File.ReadAllText(filePath)) ?? new() : new();
        }
        catch (Exception error) { AppLog.Error("Could not load UI preferences", error); return new(); }
    }

    public static void Save(UiPreferenceData settings, bool localDemo = false)
    {
        var filePath = FilePath(localDemo);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var temporary = filePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
                File.Move(temporary, filePath, true);
            }
        }
        catch (Exception error) { AppLog.Error("Could not save UI preferences", error); }
    }
}

internal sealed class UiPreferenceData
{
    [System.Text.Json.Serialization.JsonPropertyName("navigation_pane_open")]
    public bool NavigationPaneOpen { get; set; } = true;
    public List<string> ListOrder { get; set; } = [];
    public Dictionary<string, ListAppearance> Lists { get; set; } = [];
    public double? DetailPaneFraction { get; set; }
    public bool CompletionAnimation { get; set; } = true;
    public bool ICloudSetupCompleted { get; set; }
}

internal sealed class ListAppearance
{
    public string Glyph { get; set; } = "\uE8FD";
    public string? Color { get; set; }
}
