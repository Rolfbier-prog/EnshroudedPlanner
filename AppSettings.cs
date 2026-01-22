namespace EnshroudedPlanner;

public sealed class AppSettings
{
    public bool CheckForUpdatesOnStart { get; set; } = true;

    // Für später (Darkmode etc.)
    // "System", "Light", "Dark"
    public string Theme { get; set; } = "System";
}
