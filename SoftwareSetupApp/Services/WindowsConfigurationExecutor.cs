using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SoftwareSetupApp.Models;

namespace SoftwareSetupApp.Services;

public class WindowsConfigurationExecutor
{
    public async Task<InstallationResult> ExecuteAsync(
        ConfigurationTask task,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var combinedCommands = string.Join(";", task.Commands);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(combinedCommands));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new InstallationResult(false, false, "Impossible de démarrer PowerShell en mode administrateur.");
            }

            progress.Report($"[Tâche] PowerShell démarré pour \"{task.Name}\".");

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch
                {
                    // Ignored
                }
            });

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new InstallationResult(false, true, "Tâche annulée.");
            }

            var success = process.ExitCode == 0;
            if (success)
            {
                progress.Report($"[Tâche] PowerShell a terminé \"{task.Name}\".");
                return new InstallationResult(true, false, string.Empty);
            }

            progress.Report($"[Tâche] PowerShell a retourné le code {process.ExitCode} pour \"{task.Name}\".");
            return new InstallationResult(false, false, "PowerShell a renvoyé un code d'erreur.");
        }
        catch (OperationCanceledException)
        {
            return new InstallationResult(false, true, "Tâche annulée.");
        }
        catch (Win32Exception ex)
        {
            var message = ex.NativeErrorCode == 1223
                ? "Élévation refusée par l'utilisateur."
                : ex.Message;
            return new InstallationResult(false, false, message);
        }
        catch (Exception ex)
        {
            return new InstallationResult(false, false, ex.Message);
        }
    }
}
