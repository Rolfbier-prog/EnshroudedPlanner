using System;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace EnshroudedPlanner;

public sealed class UpdateService
{
    private readonly UpdateManager _mgr;

    public UpdateService()
    {
        // Public Repo => kein Token nötig
        var source = new GithubSource(
            repoUrl: "https://github.com/Rolfbier-prog/EnshroudedPlanner",
            accessToken: null,
            prerelease: false
        );

        _mgr = new UpdateManager(source);
    }

    public async Task CheckAndPromptAsync(Window owner, bool showUpToDateMessage)
    {
        // Velopack kann Update-Checks nur in installierten Builds
        if (!_mgr.IsInstalled)
        {
            if (showUpToDateMessage)
            {
                MessageBox.Show(owner,
                    "Update-Check funktioniert nur in der installierten App (nicht aus Visual Studio).",
                    "Updates",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            return;
        }

        try
        {
            var update = await _mgr.CheckForUpdatesAsync();
            if (update == null)
            {
                if (showUpToDateMessage)
                    MessageBox.Show(owner, "Du bist aktuell.", "Updates",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var current = _mgr.CurrentVersion?.ToString() ?? "unbekannt";
            var target = update.TargetFullRelease?.Version?.ToString() ?? "unbekannt";

            var answer = MessageBox.Show(owner,
                $"Update verfügbar: {current} → {target}\n\nJetzt herunterladen und installieren?",
                "Updates",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;

            owner.IsEnabled = false;
            try
            {
                await _mgr.DownloadUpdatesAsync(update);
                _mgr.ApplyUpdatesAndRestart(update); // schließt App + installiert + startet neu
            }
            finally
            {
                // Wird in der Praxis meist nicht mehr erreicht, weil App beendet wird.
                owner.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner,
                "Update-Check fehlgeschlagen:\n" + ex.Message,
                "Updates",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
