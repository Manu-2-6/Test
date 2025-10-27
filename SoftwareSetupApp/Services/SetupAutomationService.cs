using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SoftwareSetupApp.Models;

namespace SoftwareSetupApp.Services;

public class SetupAutomationService
{
    private const string ChecklistPrefix = "Checklist";

    public Task<AutomationResult> ConfigureDesktopPowerAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        return ConfigurePowerAsync("Configuration de l'alimentation secteur (poste fixe)", context, cancellationToken);
    }

    public Task<AutomationResult> ConfigureLaptopPowerAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        return ConfigurePowerAsync("Configuration de l'alimentation secteur (portable)", context, cancellationToken);
    }

    public Task<AutomationResult> ConfigureAdvancedPowerAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        const string script = @"
Write-Output 'Ajustement des paramètres d''alimentation avancés.';
try {
    & powercfg /setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE 0 | Out-Null;
    Write-Output 'Arrêt des disques sur 0 minute (secteur).';
} catch {
    Write-Warning ('Impossible de modifier DISKIDLE : ' + $_.Exception.Message);
}
try {
    & powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 100 | Out-Null;
    Write-Output 'Etat minimal du processeur fixé à 100 % (secteur).';
} catch {
    Write-Warning ('Impossible de modifier PROCTHROTTLEMIN : ' + $_.Exception.Message);
}
try {
    & powercfg /setacvalueindex SCHEME_CURRENT SUB_VIDEO VIDEOPERF 0 | Out-Null;
    Write-Output 'Priorité aux performances graphiques (secteur).';
} catch {
    Write-Warning 'Paramètre graphique non disponible sur ce matériel.';
}
try {
    & powercfg /setactive SCHEME_CURRENT | Out-Null;
    Write-Output 'Plan d''alimentation actualisé.';
} catch {
    Write-Warning ('Impossible d''actualiser le plan d''alimentation : ' + $_.Exception.Message);
}
";

        return RunPowerShellScriptAsync("Paramètres d'alimentation avancés", script, context, cancellationToken);
    }

    public Task<AutomationResult> DisableUsbSelectiveSuspendAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        const string script = @"
Write-Output 'Désactivation de la suspension sélective USB sur secteur et batterie.';
try {
    & powercfg /setacvalueindex SCHEME_CURRENT SUB_USB USBSELECTIVE 0 | Out-Null;
    & powercfg /setdcvalueindex SCHEME_CURRENT SUB_USB USBSELECTIVE 0 | Out-Null;
    & powercfg /setactive SCHEME_CURRENT | Out-Null;
    Write-Output 'Suspension sélective USB désactivée.';
} catch {
    Write-Warning ('Impossible de modifier la suspension USB : ' + $_.Exception.Message);
}
";

        return RunPowerShellScriptAsync("Suspension USB", script, context, cancellationToken);
    }

    public Task<AutomationResult> DisableUsbPowerManagementAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        const string script = @"
Write-Output 'Désactivation de l''extinction automatique des concentrateurs USB.';
$hubs = Get-PnpDevice -Class 'USB' -Status OK -ErrorAction SilentlyContinue | Where-Object {
    $_.FriendlyName -like '*Hub*'
};
if (-not $hubs) {
    Write-Warning 'Aucun concentrateur USB détecté.';
} else {
    foreach ($hub in $hubs) {
        $segments = $hub.InstanceId -split '\\';
        $regPath = 'HKLM:\\SYSTEM\\CurrentControlSet\\Enum';
        foreach ($segment in $segments) {
            $regPath = Join-Path -Path $regPath -ChildPath $segment;
        }
        $regPath = Join-Path -Path $regPath -ChildPath 'Device Parameters';

        if (-not (Test-Path $regPath)) {
            Write-Warning ('Paramètres introuvables pour ' + $hub.InstanceId);
            continue;
        }

        New-ItemProperty -Path $regPath -Name 'PnPCapabilities' -Value 24 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null;
        New-ItemProperty -Path $regPath -Name 'SelectiveSuspendEnabled' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null;
        New-ItemProperty -Path $regPath -Name 'EnableSelectiveSuspend' -Value 0 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null;
        Write-Output ('Extinction automatique désactivée pour ' + $hub.FriendlyName);
    }
}
";

        return RunPowerShellScriptAsync("Gestion de l'alimentation USB", script, context, cancellationToken);
    }

    public Task<AutomationResult> DisableNetworkPowerManagementAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        const string script = @"
Write-Output 'Désactivation de l''extinction automatique des cartes réseau.';
try {
    Import-Module NetAdapter -ErrorAction SilentlyContinue;
} catch {
    Write-Warning 'Module NetAdapter indisponible.';
}
$adapters = Get-NetAdapter -Physical -ErrorAction SilentlyContinue;
if (-not $adapters) {
    Write-Warning 'Aucune carte réseau physique détectée.';
} else {
    foreach ($adapter in $adapters) {
        $segments = $adapter.PnPDeviceID -split '\\';
        $regPath = 'HKLM:\\SYSTEM\\CurrentControlSet\\Enum';
        foreach ($segment in $segments) {
            $regPath = Join-Path -Path $regPath -ChildPath $segment;
        }
        $regPath = Join-Path -Path $regPath -ChildPath 'Device Parameters';

        if (-not (Test-Path $regPath)) {
            Write-Warning ('Paramètres introuvables pour ' + $adapter.Name);
            continue;
        }

        New-ItemProperty -Path $regPath -Name 'PnPCapabilities' -Value 24 -PropertyType DWord -Force -ErrorAction SilentlyContinue | Out-Null;
        Write-Output ('Extinction automatique désactivée pour ' + $adapter.Name);
    }
}
";

        return RunPowerShellScriptAsync("Gestion de l'alimentation réseau", script, context, cancellationToken);
    }

    public Task<AutomationResult> CheckDeviceManagerAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        const string script = @"
Write-Output 'Recherche de périphériques avec pilotes manquants.';
$devices = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object {
    $_.ConfigManagerErrorCode -ne 0
};
if (-not $devices) {
    Write-Output 'Aucun périphérique en erreur n''a été détecté.';
} else {
    foreach ($device in $devices) {
        Write-Warning ('Pilote manquant : ' + $device.Name + ' (Code ' + $device.ConfigManagerErrorCode + ')');
    }
}
";

        return RunPowerShellScriptAsync("Contrôle des pilotes", script, context, cancellationToken);
    }

    public Task<AutomationResult> RunWindowsUpdateAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        const string script = @"
Write-Output 'Recherche et installation des mises à jour Windows.';
$commands = 'StartScan','StartDownload','StartInstall','RefreshSettings';
foreach ($command in $commands) {
    Write-Output ('UsoClient ' + $command);
    try {
        & UsoClient.exe $command | Out-Null;
    } catch {
        Write-Warning ('Commande UsoClient ' + $command + ' indisponible : ' + $_.Exception.Message);
    }
    Start-Sleep -Seconds 5;
}
";

        return RunPowerShellScriptAsync("Windows Update", script, context, cancellationToken);
    }

    public Task<AutomationResult> RunMicrosoftStoreUpdatesAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        const string script = @"
Write-Output 'Mise à jour des applications Microsoft Store via winget.';
try {
    & winget upgrade --source msstore --accept-package-agreements --accept-source-agreements --all;
} catch {
    Write-Warning ('winget indisponible pour les mises à jour Store : ' + $_.Exception.Message);
}
";

        return RunPowerShellScriptAsync("Microsoft Store", script, context, cancellationToken);
    }

    public Task<AutomationResult> RemoveBloatwareAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        const string script = @"
Write-Output 'Suppression des applications préinstallées (Xbox, LinkedIn).';
$patterns = 'Microsoft.XboxApp','Microsoft.XboxGamingOverlay','Microsoft.XboxGameOverlay','Microsoft.XboxIdentityProvider','Microsoft.XboxSpeechToTextOverlay','Microsoft.GamingApp','Microsoft.LinkedIn';
foreach ($pattern in $patterns) {
    $apps = Get-AppxPackage -AllUsers -Name $pattern -ErrorAction SilentlyContinue;
    if (-not $apps) {
        continue;
    }
    foreach ($app in $apps) {
        Write-Output ('Suppression de ' + $app.Name + ' pour ' + $app.PackageFullName);
        try {
            Remove-AppxPackage -Package $app.PackageFullName -AllUsers -ErrorAction SilentlyContinue;
        } catch {
            Write-Warning ('Impossible de supprimer ' + $app.PackageFullName + ' : ' + $_.Exception.Message);
        }
    }
}
";

        return RunPowerShellScriptAsync("Suppression des applications publicitaires", script, context, cancellationToken);
    }

    public Task<AutomationResult> EnableDesktopIconsAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        const string script = @"
Write-Output 'Activation des icônes système sur le bureau.';
$keys = @(
    'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\HideDesktopIcons\\NewStartPanel',
    'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\HideDesktopIcons\\ClassicStartMenu'
);
$values = @{
    '{20D04FE0-3AEA-1069-A2D8-08002B30309D}' = 0;
    '{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}' = 0;
    '{645FF040-5081-101B-9F08-00AA002F954E}' = 0;
};
foreach ($key in $keys) {
    if (-not (Test-Path $key)) {
        New-Item -Path $key -Force | Out-Null;
    }
    foreach ($entry in $values.GetEnumerator()) {
        New-ItemProperty -Path $key -Name $entry.Key -Value $entry.Value -PropertyType DWord -Force | Out-Null;
    }
}
try {
    & RUNDLL32.EXE user32.dll,UpdatePerUserSystemParameters | Out-Null;
} catch {
    Write-Warning 'Impossible de rafraîchir immédiatement les icônes du bureau.';
}
";

        return RunPowerShellScriptAsync("Icônes du bureau", script, context, cancellationToken);
    }

    public Task<AutomationResult> RunDiskCleanupAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        const string script = @"
Write-Output 'Nettoyage du disque système (cleanmgr).';
try {
    & cleanmgr.exe /d C: /verylowdisk | Out-Null;
} catch {
    Write-Warning ('Impossible d''exécuter cleanmgr : ' + $_.Exception.Message);
}
";

        return RunPowerShellScriptAsync("Nettoyage disque", script, context, cancellationToken);
    }

    public Task<AutomationResult> ClearTemporaryDataAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        const string script = @"
Write-Output 'Nettoyage des fichiers temporaires et historiques de navigation courants.';
$tempPaths = @(
    $env:TEMP,
    (Join-Path $env:LOCALAPPDATA 'Microsoft\\Windows\\INetCache'),
    (Join-Path $env:LOCALAPPDATA 'Microsoft\\Edge\\User Data\\Default\\Cache'),
    (Join-Path $env:LOCALAPPDATA 'Google\\Chrome\\User Data\\Default\\Cache')
);
foreach ($path in $tempPaths) {
    if (-not $path) { continue }
    if (-not (Test-Path $path)) { continue }
    Write-Output ('Suppression du contenu de ' + $path);
    Get-ChildItem -Path $path -Recurse -Force -ErrorAction SilentlyContinue | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue;
}
$downloadPath = Join-Path $env:USERPROFILE 'Downloads';
if (Test-Path $downloadPath) {
    Write-Output 'Suppression des fichiers temporaires (.tmp, .crdownload, .partial) du dossier Téléchargements.';
    Get-ChildItem -Path $downloadPath -Include *.tmp,*.crdownload,*.partial,*.part -Recurse -Force -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue;
}
";

        return RunPowerShellScriptAsync("Nettoyage des traces de navigation", script, context, cancellationToken);
    }

    public Task<AutomationResult> RunSystemChecksAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        const string script = @"
Write-Output 'Programmation de chkdsk /F sur C: (acceptation automatique).';
try {
    & cmd.exe /c 'echo Y|chkdsk C: /F' | Out-Null;
} catch {
    Write-Warning ('Impossible de lancer chkdsk : ' + $_.Exception.Message);
}
Write-Output 'Exécution de sfc /scannow (peut prendre plusieurs minutes).';
try {
    & sfc.exe /scannow | Out-Null;
} catch {
    Write-Warning ('Impossible d''exécuter sfc : ' + $_.Exception.Message);
}
";

        return RunPowerShellScriptAsync("chkdsk et SFC", script, context, cancellationToken);
    }

    public Task<AutomationResult> OptimizeDrivesAsync(SetupAutomationContext context, CancellationToken cancellationToken)
    {
        const string script = @"
Write-Output 'Optimisation des lecteurs (defrag /O).';
try {
    $volumes = Get-Volume -ErrorAction SilentlyContinue | Where-Object {
        $_.DriveType -eq 'Fixed' -and $_.DriveLetter
    };
} catch {
    $volumes = @();
}
if (-not $volumes) {
    Write-Warning 'Aucun lecteur fixe détecté pour l''optimisation.';
} else {
    foreach ($volume in $volumes) {
        $drive = $volume.DriveLetter + ':';
        Write-Output ('Optimisation du lecteur ' + $drive);
        try {
            & defrag.exe $drive /O | Out-Null;
        } catch {
            Write-Warning ('Impossible d''optimiser ' + $drive + ' : ' + $_.Exception.Message);
        }
    }
}
";

        return RunPowerShellScriptAsync("Optimisation des lecteurs", script, context, cancellationToken);
    }

    private Task<AutomationResult> ConfigurePowerAsync(string description, SetupAutomationContext context, CancellationToken cancellationToken)
    {
        const string script = @"
Write-Output 'Configuration des paramètres secteur (écran 30 min, PC 60 min, luminosité 100 %).';
try {
    & powercfg /setacvalueindex SCHEME_CURRENT SUB_VIDEO VIDEOIDLE 1800 | Out-Null;
    & powercfg /change monitor-timeout-ac 30 | Out-Null;
    & powercfg /setacvalueindex SCHEME_CURRENT SUB_SLEEP STANDBYIDLE 3600 | Out-Null;
    & powercfg /change standby-timeout-ac 60 | Out-Null;
    Write-Output 'Temps de veille secteur configurés.';
} catch {
    Write-Warning ('Impossible de définir les paramètres de veille : ' + $_.Exception.Message);
}
try {
    $methods = Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods -ErrorAction Stop;
    foreach ($method in $methods) {
        $null = $method.WmiSetBrightness(1, 100);
    }
    Write-Output 'Luminosité réglée à 100 %.';
} catch {
    Write-Warning ('Impossible de régler la luminosité automatiquement : ' + $_.Exception.Message);
}
";

        return RunPowerShellScriptAsync(description, script, context, cancellationToken);
    }

    private Task<AutomationResult> RunPowerShellScriptAsync(
        string description,
        string script,
        SetupAutomationContext context,
        CancellationToken cancellationToken)
    {
        context.Report($"{ChecklistPrefix} : {description}");
        var arguments = BuildEncodedPowerShellArguments(script);
        return ExecuteProcessAsync("powershell", arguments, context, cancellationToken);
    }

    private static string BuildEncodedPowerShellArguments(string script)
    {
        var normalized = script?.Replace("\r\n", "\n") ?? string.Empty;
        var bytes = Encoding.Unicode.GetBytes(normalized);
        var encoded = Convert.ToBase64String(bytes);
        return $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}";
    }

    private async Task<AutomationResult> ExecuteProcessAsync(
        string fileName,
        string arguments,
        SetupAutomationContext context,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            var outputCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errorCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    outputCompletion.TrySetResult(null);
                    return;
                }

                context.Report($"{ChecklistPrefix} ▶ {args.Data}");
            };

            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    errorCompletion.TrySetResult(null);
                    return;
                }

                context.Report($"{ChecklistPrefix} ⚠ {args.Data}");
            };

            if (!process.Start())
            {
                return AutomationResult.Failure($"Impossible de démarrer {fileName}.");
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

            if (process.ExitCode != 0)
            {
                return AutomationResult.Failure($"La commande {fileName} s'est terminée avec le code {process.ExitCode}.");
            }

            return AutomationResult.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception ex)
        {
            return AutomationResult.Failure($"Commande introuvable : {fileName} ({ex.Message}).");
        }
        catch (Exception ex)
        {
            return AutomationResult.FromException(ex);
        }
    }
}
