using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SoftwareSetupApp.Models;

namespace SoftwareSetupApp.Services;

public class WindowsCustomizationService
{
    private static readonly string[] ChromeCandidatePaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe")
    };

    public async Task ApplyAsync(SoftwarePackage package, IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            progress.Report($"[{package.Name}] Configuration spécifique à Windows ignorée (système non Windows).");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (package.SupportsDefaultApp && package.IsDefaultAppSelected && package.DefaultAssociations.Any())
        {
            await ApplyDefaultAssociationsAsync(package, progress, cancellationToken).ConfigureAwait(false);
        }

        if (package.ChromeOptions != null)
        {
            await ApplyChromeCustomizationsAsync(package, package.ChromeOptions, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyDefaultAssociationsAsync(SoftwarePackage package, IProgress<string> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var associations = package.DefaultAssociations;
        if (associations.Count == 0)
        {
            return;
        }

        progress.Report($"[{package.Name}] Application des associations par défaut...");

        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.AppendLine("<DefaultAssociations>");
        foreach (var association in associations)
        {
            builder.Append("  <Association Identifier=\"")
                   .Append(association.Identifier)
                   .Append("\" ProgId=\"")
                   .Append(association.ProgId)
                   .Append("\" ApplicationName=\"")
                   .Append(association.ApplicationName)
                   .AppendLine("\" />");
        }

        builder.AppendLine("</DefaultAssociations>");

        var tempFile = Path.Combine(Path.GetTempPath(), $"defaults_{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(tempFile, builder.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = "dism",
                Arguments = $"/Online /Import-DefaultAppAssociations:\"{tempFile}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var exitCode = await RunProcessAsync(startInfo, progress, package.Name, cancellationToken).ConfigureAwait(false);
            if (exitCode == 0)
            {
                progress.Report($"[{package.Name}] Associations par défaut appliquées.");
            }
            else
            {
                progress.Report($"[{package.Name}] Impossible d'appliquer les associations par défaut (code {exitCode}).");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // Ignored
            }
        }
    }

    private async Task ApplyChromeCustomizationsAsync(SoftwarePackage package, ChromeCustomizationOptions options, IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (!package.IsDefaultAppSelected && !options.HasAnySelection)
        {
            return;
        }

        var chromePath = FindChromeExecutable();
        if (package.IsDefaultAppSelected && chromePath != null)
        {
            progress.Report("[Google Chrome] Définition comme navigateur par défaut...");
            var startInfo = new ProcessStartInfo
            {
                FileName = chromePath,
                Arguments = "--make-default-browser",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var exitCode = await RunProcessAsync(startInfo, progress, package.Name, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                progress.Report("[Google Chrome] Impossible de définir le navigateur par défaut via la ligne de commande.");
            }
        }
        else if (package.IsDefaultAppSelected)
        {
            progress.Report("[Google Chrome] Chrome introuvable après l'installation, impossible de le définir par défaut.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (options.PinToTaskbar)
        {
            const string script = @"
$appsFolder = (New-Object -ComObject Shell.Application).Namespace('shell:Appsfolder')
if ($appsFolder) {
    $chromeApp = $appsFolder.ParseName('Google Chrome')
    if ($chromeApp) {
        foreach ($verb in $chromeApp.Verbs()) {
            $name = $verb.Name.Replace('&', '')
            if ($name -match 'taskbar' -or $name -match 'barre des taches' -or $name -match 'barre des tâches') {
                $verb.DoIt()
                break
            }
        }
    }
}
";
            progress.Report("[Google Chrome] Épinglage à la barre des tâches...");
            await RunPowerShellAsync(script, progress, package.Name, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (options.RequiresPolicyUpdate)
        {
            var scriptBuilder = new StringBuilder();
            scriptBuilder.AppendLine("$policyRoot = 'HKCU:\\Software\\Policies\\Google'");
            scriptBuilder.AppendLine("$chromePolicy = Join-Path $policyRoot 'Chrome'");
            scriptBuilder.AppendLine("New-Item -Path $policyRoot -Force | Out-Null");
            scriptBuilder.AppendLine("New-Item -Path $chromePolicy -Force | Out-Null");

            if (options.ConfigureHomepage)
            {
                scriptBuilder.AppendLine("Set-ItemProperty -Path $chromePolicy -Name 'HomepageLocation' -Value 'https://www.google.com'");
                scriptBuilder.AppendLine("Set-ItemProperty -Path $chromePolicy -Name 'HomepageIsNewTabPage' -Type DWord -Value 0");
                scriptBuilder.AppendLine("Set-ItemProperty -Path $chromePolicy -Name 'RestoreOnStartup' -Type DWord -Value 4");
                scriptBuilder.AppendLine("Set-ItemProperty -Path $chromePolicy -Name 'RestoreOnStartupURLs' -Type MultiString -Value @('https://www.google.com')");
                scriptBuilder.AppendLine("Set-ItemProperty -Path $chromePolicy -Name 'NewTabPageLocation' -Value 'https://www.google.com'");
            }

            if (options.ShowBookmarksBar)
            {
                scriptBuilder.AppendLine("Set-ItemProperty -Path $chromePolicy -Name 'BookmarkBarEnabled' -Type DWord -Value 1");
            }

            if (options.AddGoogleBookmark)
            {
                const string bookmarksJson = "[{\"t\":\"url\",\"name\":\"Google\",\"url\":\"https://www.google.com\"}]";
                scriptBuilder.AppendLine($"Set-ItemProperty -Path $chromePolicy -Name 'ManagedBookmarks' -Value '{bookmarksJson}'");
            }

            progress.Report("[Google Chrome] Application des paramètres de page d'accueil et de favoris...");
            await RunPowerShellAsync(scriptBuilder.ToString(), progress, package.Name, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? FindChromeExecutable()
    {
        foreach (var path in ChromeCandidatePaths)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
                // Ignored
            }
        }

        return null;
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    private async Task RunPowerShellAsync(string script, IProgress<string> progress, string packageName, CancellationToken cancellationToken)
    {
        var startInfo = CreatePowerShellStartInfo(script);
        var exitCode = await RunProcessAsync(startInfo, progress, packageName, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            progress.Report($"[{packageName}] Le script PowerShell s'est terminé avec le code {exitCode}.");
        }
    }

    private static async Task<int> RunProcessAsync(ProcessStartInfo startInfo, IProgress<string> progress, string packageName, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var outputCompletion = new TaskCompletionSource<object?>();
            var errorCompletion = new TaskCompletionSource<object?>();

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    outputCompletion.TrySetResult(null);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    progress.Report($"[{packageName}] {args.Data}");
                }
            };

            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    errorCompletion.TrySetResult(null);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    progress.Report($"[{packageName}] {args.Data}");
                }
            };

            if (!process.Start())
            {
                return -1;
            }

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

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputCompletion.Task, errorCompletion.Task).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception)
        {
            progress.Report($"[{packageName}] L'exécutable '{startInfo.FileName}' est introuvable.");
            return -1;
        }
    }
}
