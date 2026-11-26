using System.Text.Json;

namespace FF6PakEditor.UI;

/// <summary>Per-user settings in %APPDATA%\FastShowdownVfsEditor\settings.json.</summary>
sealed class AppSettings
{
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastShowdownVfsEditor", "settings.json");

    public List<string> RecentArchives { get; set; } = new();
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool Maximized { get; set; }
    public int TreeWidth { get; set; } = 320;
    public int OutputHeight { get; set; } = 190;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void AddRecent(string path)
    {
        RecentArchives.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentArchives.Insert(0, path);
        if (RecentArchives.Count > 10) RecentArchives.RemoveRange(10, RecentArchives.Count - 10);
        Save();
    }
}
