using System;
using System.Threading;
using System.Threading.Tasks;
using SoftwareSetupApp.Models;

namespace SoftwareSetupApp.Services;

public static class DefaultAppService
{
    /// <summary>
    /// Informe l'utilisateur qu'il doit définir manuellement l'application par défaut.
    /// </summary>
    public static Task RequestDefaultApplicationAsync(SoftwarePackage package, IProgress<string> progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress.Report($"[{package.Name}] Veuillez définir manuellement cette application comme application par défaut via les paramètres Windows.");
        }, cancellationToken);
    }
}
