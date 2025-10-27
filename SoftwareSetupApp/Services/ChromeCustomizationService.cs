using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            PinToTaskbar(progress);
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

            UpdateManagedBookmarks(chromeKey, progress);
        }
        catch (Exception ex)
        {
            progress.Report($"[Google Chrome] Erreur lors de la configuration des stratégies : {ex.Message}");
        }
    }

    private static void UpdateManagedBookmarks(RegistryKey chromeKey, IProgress<string> progress)
    {
        try
        {
            var existingValue = chromeKey.GetValue("ManagedBookmarks") as string;
            var entries = DeserializeManagedBookmarks(existingValue);

            entries.RemoveAll(static e => !string.IsNullOrWhiteSpace(e.TopLevelName));
            RemoveExistingGoogleBookmark(entries);

            entries.Add(new ManagedBookmarkNode
            {
                Name = "Google",
                Url = GoogleUrl
            });

            var managedBookmarksJson = JsonSerializer.Serialize(entries, SerializerOptions);
            chromeKey.SetValue("ManagedBookmarks", managedBookmarksJson, RegistryValueKind.String);
            progress.Report("[Google Chrome] Favori 'Google' ajouté à la barre des favoris.");
        }
        catch (Exception ex)
        {
            progress.Report($"[Google Chrome] Impossible de mettre à jour les favoris gérés : {ex.Message}");
        }
    }

    private static List<ManagedBookmarkNode> DeserializeManagedBookmarks(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new List<ManagedBookmarkNode>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<ManagedBookmarkNode>>(rawValue);
            return parsed ?? new List<ManagedBookmarkNode>();
        }
        catch (JsonException)
        {
            return new List<ManagedBookmarkNode>();
        }
    }

    private static void RemoveExistingGoogleBookmark(List<ManagedBookmarkNode> entries)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (entry.Children != null && entry.Children.Count > 0)
            {
                RemoveExistingGoogleBookmark(entry.Children);
            }

            if (string.Equals(entry.Name, "Google", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Url, GoogleUrl, StringComparison.OrdinalIgnoreCase))
            {
                entries.RemoveAt(i);
            }
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class ManagedBookmarkNode
    {
        [JsonPropertyName("toplevel_name")]
        public string? TopLevelName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("children")]
        public List<ManagedBookmarkNode>? Children { get; set; }
    }

    private static void PinToTaskbar(IProgress<string> progress)
    {
        try
        {
            var chromeExecutable = LocateChromeExecutable();
            if (chromeExecutable == null)
            {
                progress.Report("[Google Chrome] chrome.exe introuvable pour l'épingler à la barre des tâches.");
                return;
            }

            if (WindowsShellService.TryPinToTaskbar(chromeExecutable, out var message))
            {
                progress.Report("[Google Chrome] Application épinglée à la barre des tâches.");
            }
            else if (!string.IsNullOrWhiteSpace(message))
            {
                progress.Report($"[Google Chrome] {message}");
            }
        }
        catch (Exception ex)
        {
            progress.Report($"[Google Chrome] Échec de l'épinglage dans la barre des tâches : {ex.Message}");
        }
    }

    private static string? LocateChromeExecutable()
    {
        static IEnumerable<string> EnumerateCandidates()
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe");
            }

            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe");
            }

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe");
            }
        }

        return EnumerateCandidates().FirstOrDefault(File.Exists);
    }
}
