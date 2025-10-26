using System;
using System.ComponentModel;
using System.Diagnostics;
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
                var processInfo = new ProcessStartInfo
                {
                    FileName = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty,
                    UseShellExecute = true,
                    Verb = "runas"
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
