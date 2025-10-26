using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
            if (!TrySetChromeAsDefaultBrowser(progress))
            {
                progress.Report("[Google Chrome] Tentative via Chrome (--make-default-browser)...");
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
        }
        else if (package.IsDefaultAppSelected)
        {
            progress.Report("[Google Chrome] Chrome introuvable après l'installation, impossible de le définir par défaut.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (options.PinToTaskbar)
        {
            var scriptBuilder = new StringBuilder();
            scriptBuilder.AppendLine("$chromePath = [System.IO.Path]::Combine($env:ProgramFiles, 'Google', 'Chrome', 'Application', 'chrome.exe')");
            scriptBuilder.AppendLine("if (-not (Test-Path $chromePath)) { $chromePath = [System.IO.Path]::Combine(${env:ProgramFiles(x86)}, 'Google', 'Chrome', 'Application', 'chrome.exe') }");
            scriptBuilder.AppendLine("if (Test-Path $chromePath) {");
            scriptBuilder.AppendLine("    $taskbarFolder = Join-Path $env:AppData 'Microsoft\\Internet Explorer\\Quick Launch\\User Pinned\\TaskBar'");
            scriptBuilder.AppendLine("    New-Item -ItemType Directory -Force -Path $taskbarFolder | Out-Null");
            scriptBuilder.AppendLine("    $shortcutPath = Join-Path $taskbarFolder 'Google Chrome.lnk'");
            scriptBuilder.AppendLine("    $wsh = New-Object -ComObject WScript.Shell");
            scriptBuilder.AppendLine("    $shortcut = $wsh.CreateShortcut($shortcutPath)");
            scriptBuilder.AppendLine("    $shortcut.TargetPath = $chromePath");
            scriptBuilder.AppendLine("    $shortcut.IconLocation = \"$chromePath,0\"");
            scriptBuilder.AppendLine("    $shortcut.WorkingDirectory = [System.IO.Path]::GetDirectoryName($chromePath)");
            scriptBuilder.AppendLine("    $shortcut.Save()");
            scriptBuilder.AppendLine("    $shell = New-Object -ComObject Shell.Application");
            scriptBuilder.AppendLine("    $appsFolder = $shell.Namespace('shell:Appsfolder')");
            scriptBuilder.AppendLine("    if ($appsFolder) {");
            scriptBuilder.AppendLine("        $chromeItems = @()");
            scriptBuilder.AppendLine("        foreach ($item in $appsFolder.Items()) {");
            scriptBuilder.AppendLine("            if ($item.Name -match 'Chrome' -or $item.Path -like '*chrome.exe') {");
            scriptBuilder.AppendLine("                $chromeItems += $item");
            scriptBuilder.AppendLine("            }");
            scriptBuilder.AppendLine("        }");
            scriptBuilder.AppendLine("        foreach ($chromeApp in $chromeItems) {");
            scriptBuilder.AppendLine("            foreach ($verb in $chromeApp.Verbs()) {");
            scriptBuilder.AppendLine("                $name = $verb.Name.Replace('&', '')");
            scriptBuilder.AppendLine("                if ($verb.Verb -eq 'taskbarpin' -or $name -match 'taskbar' -or $name -match 'barre des taches' -or $name -match 'barre des tâches') {");
            scriptBuilder.AppendLine("                    try { $verb.DoIt() } catch {}");
            scriptBuilder.AppendLine("                }");
            scriptBuilder.AppendLine("            }");
            scriptBuilder.AppendLine("        }");
            scriptBuilder.AppendLine("    }");
            scriptBuilder.AppendLine("}");
            var script = scriptBuilder.ToString();
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
                scriptBuilder.AppendLine("New-ItemProperty -Path $chromePolicy -Name 'HomepageLocation' -PropertyType String -Value 'https://www.google.com' -Force | Out-Null");
                scriptBuilder.AppendLine("New-ItemProperty -Path $chromePolicy -Name 'HomepageIsNewTabPage' -PropertyType DWord -Value 0 -Force | Out-Null");
                scriptBuilder.AppendLine("New-ItemProperty -Path $chromePolicy -Name 'RestoreOnStartup' -PropertyType DWord -Value 4 -Force | Out-Null");
                scriptBuilder.AppendLine("New-ItemProperty -Path $chromePolicy -Name 'RestoreOnStartupURLs' -PropertyType MultiString -Value @('https://www.google.com') -Force | Out-Null");
                scriptBuilder.AppendLine("New-ItemProperty -Path $chromePolicy -Name 'NewTabPageLocation' -PropertyType String -Value 'https://www.google.com' -Force | Out-Null");
            }

            if (options.ShowBookmarksBar)
            {
                scriptBuilder.AppendLine("New-ItemProperty -Path $chromePolicy -Name 'BookmarkBarEnabled' -PropertyType DWord -Value 1 -Force | Out-Null");
            }

            if (options.AddGoogleBookmark)
            {
                const string bookmarksJson = "[{\"t\":\"url\",\"name\":\"Google\",\"url\":\"https://www.google.com\"}]";
                scriptBuilder.AppendLine($"New-ItemProperty -Path $chromePolicy -Name 'ManagedBookmarks' -PropertyType String -Value \"{bookmarksJson}\" -Force | Out-Null");
            }

            scriptBuilder.AppendLine("gpupdate.exe /target:user /force | Out-Null");
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

    private static bool TrySetChromeAsDefaultBrowser(IProgress<string> progress)
    {
        try
        {
            var registrationType = Type.GetTypeFromCLSID(ApplicationAssociationRegistrationClsid);
            if (registrationType == null)
            {
                return false;
            }

            if (Activator.CreateInstance(registrationType) is not IApplicationAssociationRegistration registration)
            {
                return false;
            }

            try
            {
                const string chromeRegisteredName = "Google Chrome";
                if (registration.QueryAppIsDefaultAll(chromeRegisteredName, ASSOCIATIONLEVEL.AL_EFFECTIVE, out var isDefault) == 0 && isDefault)
                {
                    progress.Report("[Google Chrome] Navigateur par défaut déjà défini.");
                    return true;
                }

                var hr = registration.SetAppAsDefaultAll(chromeRegisteredName);
                if (hr == 0)
                {
                    progress.Report("[Google Chrome] Navigateur par défaut défini via l'API Windows.");
                    return true;
                }

                Marshal.ThrowExceptionForHR(hr);
            }
            finally
            {
                Marshal.ReleaseComObject(registration);
            }
        }
        catch (Exception ex)
        {
            progress.Report($"[Google Chrome] Impossible d'utiliser l'API Windows : {ex.Message}.");
        }

        return false;
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

    private static readonly Guid ApplicationAssociationRegistrationClsid = new("591209C7-767B-42B2-9FBA-44EE4615F2C7");

    [ComImport]
    [Guid("1F76A169-F994-40AC-8FC8-0959E8874710")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationAssociationRegistration
    {
        int QueryCurrentDefault([MarshalAs(UnmanagedType.LPWStr)] string pszQuery, ASSOCIATIONTYPE at, ASSOCIATIONLEVEL al, [MarshalAs(UnmanagedType.LPWStr)] out string ppszAssociation);
        int QueryAppIsDefault([MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName, ASSOCIATIONTYPE at, ASSOCIATIONLEVEL al, [MarshalAs(UnmanagedType.Bool)] out bool pfDefault);
        int QueryAppIsDefaultAll([MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName, ASSOCIATIONLEVEL al, [MarshalAs(UnmanagedType.Bool)] out bool pfDefault);
        int SetAppAsDefault([MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName, [MarshalAs(UnmanagedType.LPWStr)] string pszSet, ASSOCIATIONTYPE at);
        int SetAppAsDefaultAll([MarshalAs(UnmanagedType.LPWStr)] string pszAppRegistryName);
        int ClearUserAssociations();
    }

    private enum ASSOCIATIONTYPE
    {
        AT_FILEEXTENSION,
        AT_URLPROTOCOL,
        AT_STARTMENUCLIENT,
        AT_MIMETYPE
    }

    private enum ASSOCIATIONLEVEL
    {
        AL_MACHINE,
        AL_EFFECTIVE,
        AL_USER
    }
}
