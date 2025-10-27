using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SoftwareSetupApp.Models;

namespace SoftwareSetupApp.Services;

public static class DefaultAppService
{
    private const string ChromeRegisteredName = "Google Chrome";

    /// <summary>
    /// Opens the Windows default apps settings page so the user can confirm the default application change.
    /// </summary>
    /// <remarks>
    /// Windows 10/11 limitent la possibilité de modifier les associations par défaut sans action de l'utilisateur.
    /// Cette méthode tente d'abord l'API <see cref="IApplicationAssociationRegistration"/> puis ouvre la page Paramètres si une
    /// confirmation est nécessaire. Pour les environnements gérés, envisagez plutôt un objet de stratégie de groupe ou un
    /// fichier d'association XML déployé via DISM/Intune.
    /// </remarks>
    public static Task RequestDefaultApplicationAsync(SoftwarePackage package, IProgress<string> progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (package.IsChrome && TrySetChromeAsDefault(progress))
                {
                    return;
                }

                progress.Report($"[{package.Name}] Ouverture des paramètres Windows pour définir l'application par défaut.");
                LaunchWindowsDefaultAppsPage();
            }
            catch (Exception ex)
            {
                progress.Report($"[{package.Name}] Impossible d'ouvrir les paramètres des applications par défaut : {ex.Message}");
            }
        }, cancellationToken);
    }

    private static bool TrySetChromeAsDefault(IProgress<string> progress)
    {
        try
        {
            var registration = (IApplicationAssociationRegistration)new ApplicationAssociationRegistration();

            var hr = registration.SetAppAsDefaultAll(ChromeRegisteredName);
            if (hr < 0)
            {
                progress.Report("[Google Chrome] L'API système a refusé le changement d'application par défaut.");
                return false;
            }

            // Explicitly assign key protocols and file types commonly associated with the default browser role.
            registration.SetAppAsDefault(ChromeRegisteredName, "http", AssociationType.UrlProtocol);
            registration.SetAppAsDefault(ChromeRegisteredName, "https", AssociationType.UrlProtocol);
            registration.SetAppAsDefault(ChromeRegisteredName, ".htm", AssociationType.FileExtension);
            registration.SetAppAsDefault(ChromeRegisteredName, ".html", AssociationType.FileExtension);

            progress.Report("[Google Chrome] Navigateur défini comme application par défaut via l'API système.");
            return true;
        }
        catch (COMException comEx)
        {
            progress.Report($"[Google Chrome] Échec de la définition automatique de l'application par défaut (COM : 0x{comEx.ErrorCode:X}).");
        }
        catch (Exception ex)
        {
            progress.Report($"[Google Chrome] Échec de la définition automatique de l'application par défaut : {ex.Message}");
        }

        progress.Report("[Google Chrome] Ouvrez la page Paramètres > Applications par défaut pour confirmer manuellement.");
        return false;
    }

    private static void LaunchWindowsDefaultAppsPage()
    {
        var startInfo = new ProcessStartInfo("ms-settings:defaultapps")
        {
            UseShellExecute = true
        };

        Process.Start(startInfo);
    }

    [ComImport]
    [Guid("1968106D-F3B5-44CF-890E-116FCB9ECEF1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationAssociationRegistration
    {
        [PreserveSig]
        int QueryCurrentDefault([MarshalAs(UnmanagedType.LPWStr)] string pszQuery,
            AssociationType atQueryType,
            AssociationLevel alQueryLevel,
            [MarshalAs(UnmanagedType.LPWStr)] out string? ppszAssociation);

        [PreserveSig]
        int QueryAppIsDefault([MarshalAs(UnmanagedType.LPWStr)] string pszQuery,
            AssociationType atQueryType,
            AssociationLevel alQueryLevel,
            [MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName,
            out bool pfDefault);

        [PreserveSig]
        int QueryAppIsDefaultAll(AssociationLevel alQueryLevel,
            [MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName,
            out bool pfDefault);

        [PreserveSig]
        int SetAppAsDefault([MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName,
            [MarshalAs(UnmanagedType.LPWStr)] string pszSet,
            AssociationType atSetType);

        [PreserveSig]
        int SetAppAsDefaultAll([MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName);

        [PreserveSig]
        int ClearUserAssociations();
    }

    [ComImport]
    [Guid("591209C7-767B-42B2-9FBA-44EE4615F2C7")]
    private class ApplicationAssociationRegistration
    {
    }

    private enum AssociationType
    {
        FileExtension = 0,
        UrlProtocol = 1,
        MimeType = 2,
        Scheme = 3,
        Invalid = 4
    }

    private enum AssociationLevel
    {
        Machine = 0,
        Effective = 1,
        User = 2
    }
}
