using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SoftwareSetupApp.Services;

public static class ChromeCustomizationService
{
    private const string GoogleUrl = "https://www.google.com";
    private const string ChromePolicyKeyPath = @"Software\\Policies\\Google\\Chrome";

    public static Task ApplyAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            ConfigurePolicies(progress);
        }, cancellationToken);
    }

    private static void ConfigurePolicies(IProgress<string> progress)
    {
        try
        {
            using var chromeKey = Registry.CurrentUser.CreateSubKey(ChromePolicyKeyPath, writable: true);
            if (chromeKey == null)
            {
                progress.Report("[Google Chrome] Impossible de créer la clé de stratégie utilisateur.");
                return;
            }

            chromeKey.SetValue("HomepageLocation", GoogleUrl, RegistryValueKind.String);
            chromeKey.SetValue("HomepageIsNewTabPage", 0, RegistryValueKind.DWord);
            chromeKey.SetValue("RestoreOnStartup", 4, RegistryValueKind.DWord);
            chromeKey.SetValue("RestoreOnStartupURLs", new[] { GoogleUrl }, RegistryValueKind.MultiString);
            progress.Report("[Google Chrome] Page d'accueil configurée sur https://www.google.com.");

            chromeKey.SetValue("BookmarkBarEnabled", 1, RegistryValueKind.DWord);
            progress.Report("[Google Chrome] Barre des favoris activée.");
        }
        catch (Exception ex)
        {
            progress.Report($"[Google Chrome] Erreur lors de la configuration des stratégies : {ex.Message}");
        }
    }
}
