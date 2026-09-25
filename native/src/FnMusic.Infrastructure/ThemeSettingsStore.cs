using System.Text.Json;

namespace FnMusic.Infrastructure;

public enum ThemePreference { System, Light, Dark }

public sealed class ThemeSettingsStore(string directory)
{
    private readonly string file = Path.Combine(directory, "appearance.json");
    public ThemePreference Load()
    {
        if (!File.Exists(file)) return ThemePreference.System;
        if (new FileInfo(file).Length > 4096) throw new InvalidDataException("Invalid appearance settings.");
        var value = JsonSerializer.Deserialize<Settings>(File.ReadAllText(file));
        return value?.Theme switch
        {
            "system" => ThemePreference.System,
            "light" => ThemePreference.Light,
            "dark" => ThemePreference.Dark,
            _ => throw new InvalidDataException("Invalid appearance settings.")
        };
    }
    public void Save(ThemePreference preference)
    {
        string theme = preference switch
        {
            ThemePreference.System => "system", ThemePreference.Light => "light", ThemePreference.Dark => "dark",
            _ => throw new ArgumentOutOfRangeException(nameof(preference))
        };
        Directory.CreateDirectory(directory);
        string pending = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(pending, JsonSerializer.Serialize(new Settings(theme)));
            File.Move(pending, file, true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    private sealed record Settings(string Theme);
}
