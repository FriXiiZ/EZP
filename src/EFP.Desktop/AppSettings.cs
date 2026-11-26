using System.Text.Json;

namespace EFP.Desktop;

/// <summary>Réglages gardés entre deux lancements (ezp-settings.json dans le dossier de données).</summary>
public sealed class AppSettings
{
    /// <summary>"light", "dark" ou null (thème du système).</summary>
    public string? Theme { get; set; }
    /// <summary>"fr", "en" ou null (langue du système).</summary>
    public string? Language { get; set; }
    public string NameTemplate { get; set; } = "{Brand} {Model}";
    public bool DuplexByDefault { get; set; } = true;
    public bool ReplaceExisting { get; set; } = true;
    public bool DisableWindowsAutoSetup { get; set; } = true;
    public bool ScanAtStartup { get; set; } = true;

    public static string FilePath => Path.Combine(Program.DataFolder, "ezp-settings.json");
    private static string OldFilePath => Path.Combine(Program.DataFolder, "efp-settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath) && File.Exists(OldFilePath)) File.Copy(OldFilePath, FilePath);
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}
