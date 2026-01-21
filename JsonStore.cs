using System;
using System.IO;
using System.Text.Json;

namespace EnshroudedPlanner;

public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static T LoadFromAppFolder<T>(string relativePath) where T : class
    {
        string baseDir = AppContext.BaseDirectory;
        string fullPath = Path.Combine(baseDir, relativePath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Datei nicht gefunden: {fullPath}");

        string json = File.ReadAllText(fullPath);
        T? obj = JsonSerializer.Deserialize<T>(json, Options);

        if (obj is null)
            throw new InvalidDataException($"Konnte JSON nicht parsen: {fullPath}");

        return obj;
    }
}
