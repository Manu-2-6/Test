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
    private const int HRESULT_S_FALSE = 1;

    private static readonly string[] ChromeCandidatePaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe")
    };

    private static readonly string[] ChromeAssociations =
    {
        ".htm",
        ".html",
        "http",
        "https"
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
            if (chromePath != null)
            {
                var script = BuildChromeTaskbarPinningScript(chromePath);
                progress.Report("[Google Chrome] Épinglage à la barre des tâches...");
                await RunPowerShellAsync(script, progress, package.Name, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                progress.Report("[Google Chrome] Impossible d'épingler Chrome : exécutable introuvable.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (options.RequiresPolicyUpdate)
        {
            var script = BuildChromePolicyScript(options);
            progress.Report("[Google Chrome] Application des paramètres de page d'accueil et de favoris...");
            await RunPowerShellAsync(script, progress, package.Name, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildChromeTaskbarPinningScript(string chromePath)
    {
        var escapedPath = EscapeForSingleQuotes(chromePath);
        var userScriptBuilder = new StringBuilder();
        userScriptBuilder.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        userScriptBuilder.AppendLine($"$chromePath = '{escapedPath}'");
        userScriptBuilder.AppendLine("if (-not (Test-Path $chromePath)) { return }");
        userScriptBuilder.AppendLine("$taskbarFolder = Join-Path $env:AppData 'Microsoft\\Internet Explorer\\Quick Launch\\User Pinned\\TaskBar'");
        userScriptBuilder.AppendLine("New-Item -ItemType Directory -Force -Path $taskbarFolder | Out-Null");
        userScriptBuilder.AppendLine("$shortcutPath = Join-Path $taskbarFolder 'Google Chrome.lnk'");
        userScriptBuilder.AppendLine("$wsh = New-Object -ComObject WScript.Shell");
        userScriptBuilder.AppendLine("$shortcut = $wsh.CreateShortcut($shortcutPath)");
        userScriptBuilder.AppendLine("$shortcut.TargetPath = $chromePath");
        userScriptBuilder.AppendLine("$shortcut.IconLocation = \"$chromePath,0\"");
        userScriptBuilder.AppendLine("$shortcut.WorkingDirectory = [System.IO.Path]::GetDirectoryName($chromePath)");
        userScriptBuilder.AppendLine("$shortcut.Save()");
        userScriptBuilder.AppendLine("$shell = New-Object -ComObject Shell.Application");
        userScriptBuilder.AppendLine("$appsFolder = $shell.Namespace('shell:Appsfolder')");
        userScriptBuilder.AppendLine("if ($appsFolder) {");
        userScriptBuilder.AppendLine("    foreach ($item in $appsFolder.Items()) {");
        userScriptBuilder.AppendLine("        if (-not $item) { continue }");
        userScriptBuilder.AppendLine("        $itemName = $item.Name");
        userScriptBuilder.AppendLine("        $itemPath = $item.Path");
        userScriptBuilder.AppendLine("        if ($itemName -match 'Chrome' -or $itemPath -like '*chrome.exe') {");
        userScriptBuilder.AppendLine("            foreach ($verb in $item.Verbs()) {");
        userScriptBuilder.AppendLine("                if (-not $verb) { continue }");
        userScriptBuilder.AppendLine("                $name = ($verb.Name -replace '&', '').ToLowerInvariant()");
        userScriptBuilder.AppendLine("                if ($verb.Verb -eq 'taskbarunpin' -or $name -like '*unpin*' -or $name -like '*désepingler*') {");
        userScriptBuilder.AppendLine("                    try { $verb.DoIt() } catch { }");
        userScriptBuilder.AppendLine("                }");
        userScriptBuilder.AppendLine("            }");
        userScriptBuilder.AppendLine("            foreach ($verb in $item.Verbs()) {");
        userScriptBuilder.AppendLine("                if (-not $verb) { continue }");
        userScriptBuilder.AppendLine("                $name = ($verb.Name -replace '&', '').ToLowerInvariant()");
        userScriptBuilder.AppendLine("                if ($verb.Verb -eq 'taskbarpin' -or $name -like '*taskbar*' -or $name -like '*barre des taches*' -or $name -like '*barre des tâches*') {");
        userScriptBuilder.AppendLine("                    try { $verb.DoIt() } catch { }");
        userScriptBuilder.AppendLine("                    break");
        userScriptBuilder.AppendLine("                }");
        userScriptBuilder.AppendLine("            }");
        userScriptBuilder.AppendLine("            break");
        userScriptBuilder.AppendLine("        }");
        userScriptBuilder.AppendLine("    }");
        userScriptBuilder.AppendLine("}");

        var userScript = userScriptBuilder.ToString();
        var encodedUserScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(userScript));

        var adminScript = new StringBuilder();
        adminScript.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        adminScript.AppendLine($"$encoded = '{encodedUserScript}'");
        adminScript.AppendLine("function Invoke-PinScript {");
        adminScript.AppendLine("    Start-Process -FilePath powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded) -WindowStyle Hidden | Out-Null");
        adminScript.AppendLine("}");
        adminScript.AppendLine("$taskName = 'SoftwareSetupApp_PinChrome'");
        adminScript.AppendLine("try {");
        adminScript.AppendLine("    Import-Module ScheduledTasks -ErrorAction Stop | Out-Null");
        adminScript.AppendLine("    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {");
        adminScript.AppendLine("        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue | Out-Null");
        adminScript.AppendLine("    }");
        adminScript.AppendLine("    $principalUser = $null");
        adminScript.AppendLine("    try {");
        adminScript.AppendLine("        $explorer = Get-CimInstance Win32_Process -Filter \"Name='explorer.exe'\" -ErrorAction Stop | Sort-Object CreationDate | Select-Object -First 1");
        adminScript.AppendLine("        if ($explorer) {");
        adminScript.AppendLine("            $owner = Invoke-CimMethod -InputObject $explorer -MethodName GetOwner -ErrorAction Stop");
        adminScript.AppendLine("            if ($owner.ReturnValue -eq 0 -and $owner.User) {");
        adminScript.AppendLine("                $principalUser = \"$($owner.Domain)\\$($owner.User)\";");
        adminScript.AppendLine("            }");
        adminScript.AppendLine("        }");
        adminScript.AppendLine("    } catch { }");
        adminScript.AppendLine("    if (-not $principalUser) {");
        adminScript.AppendLine("        $principalUser = \"$env:USERDOMAIN\\$env:USERNAME\";");
        adminScript.AppendLine("    }");
        adminScript.AppendLine("    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument \"-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded\"");
        adminScript.AppendLine("    $trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddSeconds(10))");
        adminScript.AppendLine("    $principal = New-ScheduledTaskPrincipal -UserId $principalUser -LogonType InteractiveToken");
        adminScript.AppendLine("    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -RunLevel LeastPrivilege -Force | Out-Null");
        adminScript.AppendLine("    Start-ScheduledTask -TaskName $taskName | Out-Null");
        adminScript.AppendLine("} catch {");
        adminScript.AppendLine("    Invoke-PinScript");
        adminScript.AppendLine("}");

        return adminScript.ToString();
    }

    private static string BuildChromePolicyScript(ChromeCustomizationOptions options)
    {
        var bookmarksJson = "[{\\\"t\\\":\\\"url\\\",\\\"name\\\":\\\"Google\\\",\\\"url\\\":\\\"https://www.google.com\\\"}]";
        var scriptBuilder = new StringBuilder();
        scriptBuilder.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        scriptBuilder.AppendLine($"$configureHomepage = {options.ConfigureHomepage.ToString().ToLowerInvariant()}");
        scriptBuilder.AppendLine($"$showBookmarksBar = {options.ShowBookmarksBar.ToString().ToLowerInvariant()}");
        scriptBuilder.AppendLine($"$addBookmark = {options.AddGoogleBookmark.ToString().ToLowerInvariant()}");
        scriptBuilder.AppendLine($"$bookmarkJson = '{bookmarksJson}'");
        scriptBuilder.AppendLine("function Set-ChromePolicyProperty {");
        scriptBuilder.AppendLine("    param([string]$HivePath, [string]$Name, [string]$Type, $Value)");
        scriptBuilder.AppendLine("    if (-not $HivePath) { return }");
        scriptBuilder.AppendLine("    try {");
        scriptBuilder.AppendLine("        $parent = Split-Path $HivePath");
        scriptBuilder.AppendLine("        if ($parent) { New-Item -Path $parent -Force | Out-Null }");
        scriptBuilder.AppendLine("        New-Item -Path $HivePath -Force | Out-Null");
        scriptBuilder.AppendLine("    } catch { }");
        scriptBuilder.AppendLine("    if ($null -eq $Value) {");
        scriptBuilder.AppendLine("        Remove-ItemProperty -Path $HivePath -Name $Name -ErrorAction SilentlyContinue");
        scriptBuilder.AppendLine("        return");
        scriptBuilder.AppendLine("    }");
        scriptBuilder.AppendLine("    New-ItemProperty -Path $HivePath -Name $Name -PropertyType $Type -Value $Value -Force | Out-Null");
        scriptBuilder.AppendLine("}");
        scriptBuilder.AppendLine("function Apply-ChromePolicies {");
        scriptBuilder.AppendLine("    param([string]$HivePath)");
        scriptBuilder.AppendLine("    if (-not $HivePath) { return }");
        scriptBuilder.AppendLine("    if ($configureHomepage) {");
        scriptBuilder.AppendLine("        Set-ChromePolicyProperty -HivePath $HivePath -Name 'HomepageLocation' -Type 'String' -Value 'https://www.google.com'");
        scriptBuilder.AppendLine("        Set-ChromePolicyProperty -HivePath $HivePath -Name 'HomepageIsNewTabPage' -Type 'DWord' -Value 0");
        scriptBuilder.AppendLine("        Set-ChromePolicyProperty -HivePath $HivePath -Name 'RestoreOnStartup' -Type 'DWord' -Value 4");
        scriptBuilder.AppendLine("        Set-ChromePolicyProperty -HivePath $HivePath -Name 'RestoreOnStartupURLs' -Type 'MultiString' -Value @('https://www.google.com')");
        scriptBuilder.AppendLine("        Set-ChromePolicyProperty -HivePath $HivePath -Name 'NewTabPageLocation' -Type 'String' -Value 'https://www.google.com'");
        scriptBuilder.AppendLine("    } else {");
        scriptBuilder.AppendLine("        Set-ChromePolicyProperty -HivePath $HivePath -Name 'HomepageLocation' -Type 'String' -Value $null");
        scriptBuilder.AppendLine("        Set-ChromePolicyProperty -HivePath $HivePath -Name 'HomepageIsNewTabPage' -Type 'DWord' -Value $null");
        scriptBuilder.AppendLine("        Set-ChromePolicyProperty -HivePath $HivePath -Name 'RestoreOnStartup' -Type 'DWord' -Value $null");
        scriptBuilder.AppendLine("        Set-ChromePolicyProperty -HivePath $HivePath -Name 'RestoreOnStartupURLs' -Type 'MultiString' -Value $null");
        scriptBuilder.AppendLine("        Set-ChromePolicyProperty -HivePath $HivePath -Name 'NewTabPageLocation' -Type 'String' -Value $null");
        scriptBuilder.AppendLine("    }");
        scriptBuilder.AppendLine("    if ($showBookmarksBar) {");
        scriptBuilder.AppendLine("        Set-ChromePolicyProperty -HivePath $HivePath -Name 'BookmarkBarEnabled' -Type 'DWord' -Value 1");
        scriptBuilder.AppendLine("    } else {");
        scriptBuilder.AppendLine("        Set-ChromePolicyProperty -HivePath $HivePath -Name 'BookmarkBarEnabled' -Type 'DWord' -Value $null");
        scriptBuilder.AppendLine("    }");
        scriptBuilder.AppendLine("    if ($addBookmark) {");
        scriptBuilder.AppendLine("        Set-ChromePolicyProperty -HivePath $HivePath -Name 'ManagedBookmarks' -Type 'String' -Value $bookmarkJson");
        scriptBuilder.AppendLine("    } else {");
        scriptBuilder.AppendLine("        Set-ChromePolicyProperty -HivePath $HivePath -Name 'ManagedBookmarks' -Type 'String' -Value $null");
        scriptBuilder.AppendLine("    }");
        scriptBuilder.AppendLine("}");
        scriptBuilder.AppendLine("$machinePolicy = 'HKLM:\\SOFTWARE\\Policies\\Google\\Chrome'");
        scriptBuilder.AppendLine("Apply-ChromePolicies -HivePath $machinePolicy");
        scriptBuilder.AppendLine("$appliedUsers = @()");
        scriptBuilder.AppendLine("try {");
        scriptBuilder.AppendLine("    $profiles = Get-CimInstance Win32_UserProfile -ErrorAction Stop | Where-Object { $_.SID -like 'S-1-5-21-*' -and $_.Loaded }");
        scriptBuilder.AppendLine("    foreach ($profile in $profiles) {");
        scriptBuilder.AppendLine("        $userHive = \"Registry::HKEY_USERS\\$($profile.SID)\\Software\\Policies\\Google\\Chrome\";");
        scriptBuilder.AppendLine("        Apply-ChromePolicies -HivePath $userHive");
        scriptBuilder.AppendLine("        $appliedUsers += $profile.SID");
        scriptBuilder.AppendLine("    }");
        scriptBuilder.AppendLine("} catch { }");
        scriptBuilder.AppendLine("if ($appliedUsers.Count -eq 0) {");
        scriptBuilder.AppendLine("    Apply-ChromePolicies -HivePath 'HKCU:\\Software\\Policies\\Google\\Chrome'");
        scriptBuilder.AppendLine("}");
        scriptBuilder.AppendLine("try { gpupdate.exe /target:user /force | Out-Null } catch { }");

        return scriptBuilder.ToString();
    }

    private static string EscapeForSingleQuotes(string value) => value.Replace("'", "''");

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

            const string chromeRegisteredName = "ChromeHTML";
            try
            {
                if (registration.QueryAppIsDefaultAll(chromeRegisteredName, ASSOCIATIONLEVEL.AL_EFFECTIVE, out var isDefault) == 0 && isDefault)
                {
                    progress.Report("[Google Chrome] Navigateur par défaut déjà défini.");
                    return true;
                }

                // Windows 11 peut refuser SetAppAsDefaultAll. On force alors chaque association prise en charge.
                var hr = registration.SetAppAsDefaultAll(chromeRegisteredName);
                if (hr == HRESULT_S_FALSE)
                {
                    hr = 0;
                }

                if (hr != 0)
                {
                    foreach (var association in ChromeAssociations)
                    {
                        var associationType = association.StartsWith('.', StringComparison.Ordinal)
                            ? ASSOCIATIONTYPE.AT_FILEEXTENSION
                            : ASSOCIATIONTYPE.AT_URLPROTOCOL;
                        var setHr = registration.SetAppAsDefault(chromeRegisteredName, association, associationType);

                        if (setHr != 0)
                        {
                            Marshal.ThrowExceptionForHR(setHr);
                        }
                    }

                    progress.Report("[Google Chrome] Associations individuelles configurées via l'API Windows.");
                    return true;
                }

                progress.Report("[Google Chrome] Navigateur par défaut défini via l'API Windows.");
                return true;
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
