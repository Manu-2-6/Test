using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SoftwareSetupApp.Models;

namespace SoftwareSetupApp.Services;

public static class DefaultAppService
{
    /// <summary>
    /// Opens the Windows default apps settings page so the user can confirm the default application change.
    /// </summary>
    /// <remarks>
    /// Windows 10/11 do not allow applications to silently change default handlers. For managed environments,
    /// consider using a Group Policy Object or an XML association file deployed via DISM or Intune.
    /// </remarks>
    public static Task RequestDefaultApplicationAsync(SoftwarePackage package, IProgress<string> progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                progress.Report($"[{package.Name}] Ouverture des paramètres Windows pour définir l'application par défaut.");
                var startInfo = new ProcessStartInfo("ms-settings:defaultapps")
                {
                    UseShellExecute = true
                };

                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                progress.Report($"[{package.Name}] Impossible d'ouvrir les paramètres des applications par défaut : {ex.Message}");
            }
        }, cancellationToken);
    }
}
