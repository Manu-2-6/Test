using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace SoftwareSetupApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!IsRunningAsAdministrator())
        {
            try
            {
                var exePath = Environment.ProcessPath;
                var workingDirectory = exePath is null ? null : Path.GetDirectoryName(exePath);

                var processInfo = new ProcessStartInfo
                {
                    FileName = exePath ?? string.Empty,
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = workingDirectory
                };

                if (string.IsNullOrWhiteSpace(processInfo.FileName))
                {
                    MessageBox.Show(
                        "Impossible d'obtenir le chemin de l'application pour l'exécution en mode administrateur.",
                        "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown();
                    return;
                }

                Process.Start(processInfo);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                MessageBox.Show(
                    "Cette application doit être exécutée en tant qu'administrateur pour installer les logiciels sans interruption.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Impossible de relancer l'application en mode administrateur : {ex.Message}",
                    "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
