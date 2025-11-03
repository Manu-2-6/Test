using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoftwareSetupApp.Models;
using SoftwareSetupApp.Services;

namespace SoftwareSetupApp;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly Regex AnsiRegex = new("\x1B\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex BlockGlyphRegex = new("[\\u2500-\\u259F\\u25A0-\\u25FF\\u2B00-\\u2BFF]+", RegexOptions.Compiled);
    private static readonly Regex ExtraWhitespaceRegex = new("\\s{2,}", RegexOptions.Compiled);
    private static readonly Regex SimpleProgressBarRegex = new("[#=><\\-\\|]{3,}", RegexOptions.Compiled);
    private static readonly Regex BrokenUtf8GlyphRegex = new("â[\\u0080-\\u00FF]", RegexOptions.Compiled);
    private static readonly Regex UsefulContentRegex =
        new("[\\p{L}\\p{Nd}]+(?:[\\p{L}\\p{Nd}\\p{P}]*[\\p{L}\\p{Nd}]+)?", RegexOptions.Compiled);

    private readonly WingetInstaller _installer = new();
    private readonly WindowsConfigurationExecutor _configurationExecutor = new();
    private readonly List<string> _logoDirectories;
    private bool _isInstalling;
    private CancellationTokenSource? _installationCts;
    private string? _lastLogEntry;
    private ScrollViewer? _logScrollViewer;
    private bool _shouldAutoScroll = true;
    private bool _isProfessionalMode;

    private const string WindowsUpdateScript = """
# --- Ouvrir et placer la fenêtre Windows Update sur la moitié gauche de l'écran ---

# 1) Ouvre la page Paramètres → Windows Update
Start-Process "ms-settings:windowsupdate" | Out-Null

# 2) Patiente pour laisser la fenêtre s’afficher
Start-Sleep -Seconds 2

# 3) Déclare les API Win32
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Win32 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int  GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern int  GetClassName(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);

    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int  SW_RESTORE = 9;
}
"@

# 4) Recherche la fenêtre des Paramètres
$target = [IntPtr]::Zero
[Win32+EnumWindowsProc]$enum = {
    param([IntPtr]$hWnd, [IntPtr]$lParam)
    if (-not [Win32]::IsWindowVisible($hWnd)) { return $true }
    $cls = New-Object System.Text.StringBuilder 256
    [Win32]::GetClassName($hWnd, $cls, $cls.Capacity) | Out-Null
    if ($cls.ToString() -ne "ApplicationFrameWindow") { return $true }

    $txt = New-Object System.Text.StringBuilder 512
    [Win32]::GetWindowText($hWnd, $txt, $txt.Capacity) | Out-Null
    $title = $txt.ToString()
    if ($title -like "*Paramètres*" -or $title -like "*Settings*") {
        $script:target = $hWnd
        return $false
    }
    return $true
}

# Essaie plusieurs fois pour laisser le temps à la fenêtre
for ($i=0; $i -lt 15 -and $target -eq [IntPtr]::Zero; $i++) {
    [Win32]::EnumWindows($enum, [IntPtr]::Zero) | Out-Null
    if ($target -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 300 }
}

if ($target -ne [IntPtr]::Zero) {
    # 5) Calcule la moitié gauche de l’écran
    Add-Type -AssemblyName System.Windows.Forms
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea

    $left   = 0
    $top    = 0
    $width  = [int]([math]::Floor($wa.Width / 2))
    $height = $wa.Height

    # 6) Restaure et positionne la fenêtre
    [Win32]::ShowWindow($target, [Win32]::SW_RESTORE) | Out-Null
    [Win32]::SetForegroundWindow($target) | Out-Null
    [Win32]::SetWindowPos($target, [Win32]::HWND_TOP, $left, $top, $width, $height,
        [Win32]::SWP_NOZORDER -bor [Win32]::SWP_NOOWNERZORDER -bor [Win32]::SWP_SHOWWINDOW) | Out-Null
} else {
    Write-Warning "Impossible de localiser la fenêtre des Paramètres Windows Update."
}
""";

    private const string DeviceManagerScript = """
# --- Ouvrir et placer le Gestionnaire de périphériques sur la moitié gauche ---

# 1) Ouvre le Gestionnaire de périphériques et récupère le process (MMC)
$proc = Start-Process "devmgmt.msc" -PassThru

# 2) Attends que la fenêtre soit créée
$hWnd = [IntPtr]::Zero
for ($i=0; $i -lt 80 -and $hWnd -eq [IntPtr]::Zero; $i++) {
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { $hWnd = [IntPtr]$proc.MainWindowHandle; break }
    Start-Sleep -Milliseconds 200
}
if ($hWnd -eq [IntPtr]::Zero) { Write-Warning "Impossible de trouver la fenêtre du Gestionnaire de périphériques."; return }

# 3) API Win32 pour (re)positionner la fenêtre
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int  SW_RESTORE = 9;
}
"@

# 4) Utilise la zone de travail (sans barre des tâches) et place à gauche
Add-Type -AssemblyName System.Windows.Forms
$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$left   = 0
$top    = 0
$width  = [int]([math]::Floor($wa.Width / 2))
$height = $wa.Height

[Win32]::ShowWindow($hWnd, [Win32]::SW_RESTORE) | Out-Null
[Win32]::SetForegroundWindow($hWnd) | Out-Null
[Win32]::SetWindowPos($hWnd, [Win32]::HWND_TOP, $left, $top, $width, $height,
    [Win32]::SWP_NOZORDER -bor [Win32]::SWP_NOOWNERZORDER -bor [Win32]::SWP_SHOWWINDOW) | Out-Null
""";

    private const string PowerOptionsScript = """
powercfg.cpl
""";

    private const string DesktopIconsScript = """
Start-Process "rundll32.exe" "shell32.dll,Control_RunDLL desk.cpl,,0"
""";

    private const string MicrosoftStoreScript = """
# --- Ouvrir/positionner le Microsoft Store sur la moitié gauche (sans Snap Assist) ---

# 1) Ouvre le Microsoft Store si besoin
Start-Process "ms-windows-store:" | Out-Null

# 2) Laisse le temps à la fenêtre d'apparaître
Start-Sleep -Seconds 2

# 3) Déclare les API Win32 nécessaires
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class Win32 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int  GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern int  GetClassName(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);

    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int  SW_RESTORE = 9;
}
"@

# 4) Cherche la vraie fenêtre UWP (classe ApplicationFrameWindow)
$target = [IntPtr]::Zero
[Win32+EnumWindowsProc]$enum = {
    param([IntPtr]$hWnd, [IntPtr]$lParam)
    if (-not [Win32]::IsWindowVisible($hWnd)) { return $true }
    $cls = New-Object System.Text.StringBuilder 256
    [Win32]::GetClassName($hWnd, $cls, $cls.Capacity) | Out-Null
    if ($cls.ToString() -ne "ApplicationFrameWindow") { return $true }

    $txt = New-Object System.Text.StringBuilder 512
    [Win32]::GetWindowText($hWnd, $txt, $txt.Capacity) | Out-Null
    $title = $txt.ToString()
    if ($title -like "*Microsoft Store*") {
        $script:target = $hWnd
        return $false  # stop enumeration
    }
    return $true
}

# Essaie plusieurs fois au cas où le Store met un peu plus de temps
for ($i=0; $i -lt 15 -and $target -eq [IntPtr]::Zero; $i++) {
    [Win32]::EnumWindows($enum, [IntPtr]::Zero) | Out-Null
    if ($target -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 250 }
}

if ($target -ne [IntPtr]::Zero) {
    # 5) Utilise la zone de travail (sans la barre des tâches) du moniteur principal
    Add-Type -AssemblyName System.Windows.Forms
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea

    $left   = 0
    $top    = 0
    $width  = [int]([math]::Floor($wa.Width / 2))
    $height = $wa.Height

    # 6) Restaure (si maximisée) + place exactement sur la moitié gauche
    [Win32]::ShowWindow($target, [Win32]::SW_RESTORE) | Out-Null
    [Win32]::SetForegroundWindow($target) | Out-Null
    [Win32]::SetWindowPos($target, [Win32]::HWND_TOP, $left, $top, $width, $height,
        [Win32]::SWP_NOZORDER -bor [Win32]::SWP_NOOWNERZORDER -bor [Win32]::SWP_SHOWWINDOW) | Out-Null
} else {
    Write-Warning "Impossible de localiser la fenêtre du Microsoft Store."
}
""";

    private const string CleanmgrScript = """
# --- Ouvre "Nettoyage de disque" et le déplace sur la moitié gauche de l'écran ---

# 1) Lance cleanmgr
$proc = Start-Process "cleanmgr.exe" -PassThru
Start-Sleep -Seconds 2  # laisse le temps à la fenêtre d'apparaitre

# 2) API Win32 pour trouver et déplacer la fenêtre
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class NativeMove {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    public const int SW_RESTORE = 9;
}
public struct RECT {
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}
"@

# 3) Recherche la fenêtre cleanmgr
$target = [IntPtr]::Zero
$wantPid = [uint32]$proc.Id

[NativeMove+EnumWindowsProc]$enum = {
    param([IntPtr]$hWnd, [IntPtr]$lParam)
    if (-not [NativeMove]::IsWindowVisible($hWnd)) { return $true }

    [uint32]$windowPid = 0
    [NativeMove]::GetWindowThreadProcessId($hWnd, [ref]$windowPid) | Out-Null
    if ($windowPid -ne [uint32]$lParam.ToInt64()) { return $true }

    $script:target = $hWnd
    return $false
}

# Essaie plusieurs fois au cas où la fenêtre tarde
for ($i=0; $i -lt 30 -and $target -eq [IntPtr]::Zero; $i++) {
    [NativeMove]::EnumWindows($enum, [IntPtr]::new($wantPid)) | Out-Null
    if ($target -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 300 }
}

# 4) Si trouvée, déplace sans changer la taille
if ($target -ne [IntPtr]::Zero) {
    [NativeMove]::ShowWindow($target, [NativeMove]::SW_RESTORE) | Out-Null
    [NativeMove]::SetForegroundWindow($target) | Out-Null

    # Récupère la taille actuelle
    $rect = New-Object RECT
    [NativeMove]::GetWindowRect($target, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top

    # Calcule nouvelle position (gauche de l'écran)
    Add-Type -AssemblyName System.Windows.Forms
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $newX = [int]($wa.Width / 4 - $width / 4)  # centré visuellement dans la moitié gauche
    $newY = [int]($wa.Top + ($wa.Height - $height) / 2)

    [NativeMove]::MoveWindow($target, $newX, $newY, $width, $height, $true) | Out-Null
} else {
    Write-Warning "Impossible de localiser la fenêtre du Nettoyage de disque."
}
""";

    private const string OptimizeDrivesScript = """
# --- Ouvre "Optimiser les lecteurs" (dfrgui.exe) et le déplace sur la moitié gauche de l'écran ---

# 1) Lance dfrgui et récupère le processus
$proc = Start-Process "dfrgui.exe" -PassThru
Start-Sleep -Seconds 2  # laisse le temps à la fenêtre d'apparaitre

# 2) API Win32 (mêmes fonctions que pour cleanmgr)
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class NativeMove {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    public const int SW_RESTORE = 9;
}
public struct RECT {
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}
"@

# 3) Trouve la fenêtre principale de dfrgui
$target = [IntPtr]::Zero
$wantPid = [uint32]$proc.Id

[NativeMove+EnumWindowsProc]$enum = {
    param([IntPtr]$hWnd, [IntPtr]$lParam)
    if (-not [NativeMove]::IsWindowVisible($hWnd)) { return $true }

    [uint32]$windowPid = 0
    [NativeMove]::GetWindowThreadProcessId($hWnd, [ref]$windowPid) | Out-Null
    if ($windowPid -ne [uint32]$lParam.ToInt64()) { return $true }

    $script:target = $hWnd
    return $false
}

# 4) Attend que la fenêtre soit détectée
for ($i=0; $i -lt 30 -and $target -eq [IntPtr]::Zero; $i++) {
    [NativeMove]::EnumWindows($enum, [IntPtr]::new($wantPid)) | Out-Null
    if ($target -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 300 }
}

# 5) Si trouvée, déplace sans redimensionner
if ($target -ne [IntPtr]::Zero) {
    [NativeMove]::ShowWindow($target, [NativeMove]::SW_RESTORE) | Out-Null
    [NativeMove]::SetForegroundWindow($target) | Out-Null

    # Taille actuelle de la fenêtre
    $rect = New-Object RECT
    [NativeMove]::GetWindowRect($target, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top

    # Calcule la position (moitié gauche)
    Add-Type -AssemblyName System.Windows.Forms
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $newX = [int]($wa.Width / 4 - $width / 4)  # centré horizontalement dans la moitié gauche
    $newY = [int]($wa.Top + ($wa.Height - $height) / 2)

    [NativeMove]::MoveWindow($target, $newX, $newY, $width, $height, $true) | Out-Null
} else {
    Write-Warning "Impossible de localiser la fenêtre 'Optimiser les lecteurs'."
}
""";

    private static readonly (string Name, string Script)[] ManualWindowsToolDefinitions =
    {
        ("Windows Update", WindowsUpdateScript),
        ("Gestionnaire de périphériques", DeviceManagerScript),
        ("Options d'alimentation", PowerOptionsScript),
        ("Paramètres des icônes du Bureau", DesktopIconsScript),
        ("Microsoft Store", MicrosoftStoreScript),
        ("Nettoyage de disque", CleanmgrScript),
        ("Optimiser les lecteurs", OptimizeDrivesScript)
    };

    public ObservableCollection<SoftwarePackage> Packages { get; } = new();
    public ObservableCollection<ConfigurationTask> ConfigurationTasks { get; } = new();
    public ObservableCollection<ManualTask> ManualTasks { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();
    public ObservableCollection<ManualWindowsTool> ManualWindowsTools { get; } = new();

    public bool IsInstalling
    {
        get => _isInstalling;
        set
        {
            if (_isInstalling != value)
            {
                _isInstalling = value;
                OnPropertyChanged(nameof(IsInstalling));
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsInstalling || _installationCts == null)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            "Voulez-vous annuler l'installation en cours ?",
            "Confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation == MessageBoxResult.Yes)
        {
            CancelButton.IsEnabled = false;
            _installationCts.Cancel();
        }
    }

    private bool? _areAllWindowsToolsSelected = true;

    public bool? AreAllWindowsToolsSelected
    {
        get => _areAllWindowsToolsSelected;
        private set
        {
            if (_areAllWindowsToolsSelected != value)
            {
                _areAllWindowsToolsSelected = value;
                OnPropertyChanged(nameof(AreAllWindowsToolsSelected));
            }
        }
    }

    private async void OpenWindowsToolsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManualWindowsTools.Count == 0)
        {
            return;
        }

        CloseWindowsToolsPopup();

        var selectedTools = ManualWindowsTools.Where(tool => tool.IsSelected).ToList();
        if (selectedTools.Count == 0)
        {
            AppendLogMessage("[Tâches manuelles] Aucune fenêtre Windows sélectionnée.");
            MessageBox.Show(
                "Veuillez sélectionner au moins une fenêtre à ouvrir.",
                "Aucune fenêtre sélectionnée",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        OpenWindowsToolsButton.IsEnabled = false;

        try
        {
            foreach (var tool in selectedTools)
            {
                AppendLogMessage($"[Tâches manuelles] Ouverture de {tool.Name}...");

                var result = await RunPowerShellScriptAsync(tool.Script);
                if (result.ExitCode != 0)
                {
                    var errorDetails = string.IsNullOrWhiteSpace(result.StandardError)
                        ? $"Code de sortie : {result.ExitCode}"
                        : result.StandardError.Trim();
                    throw new InvalidOperationException($"{tool.Name} a échoué ({errorDetails}).");
                }

                if (!string.IsNullOrWhiteSpace(result.StandardError))
                {
                    AppendLogMessage($"[Tâches manuelles] {tool.Name} (messages PowerShell) : {result.StandardError.Trim()}");
                }

                AppendLogMessage($"[Tâches manuelles] {tool.Name} ouvert.");
            }

            AppendLogMessage("[Tâches manuelles] Fenêtres d'assistance ouvertes.");
        }
        catch (Exception ex)
        {
            AppendLogMessage($"[Tâches manuelles] Échec lors de l'ouverture des fenêtres : {ex.Message}");
            MessageBox.Show(
                $"Une erreur s'est produite lors de l'ouverture des fenêtres : {ex.Message}",
                "Erreur",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            OpenWindowsToolsButton.IsEnabled = true;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        Icon = new BitmapImage(new Uri("pack://application:,,,/SoftwareSetupApp;component/Assets/Logos/Amix.png"));

        Loaded += (_, _) => PositionWindowOnRightHalf();

        _logoDirectories = BuildLogoDirectories();

        ((INotifyCollectionChanged)Logs).CollectionChanged += LogsOnCollectionChanged;

        foreach (var directory in _logoDirectories)
        {
            Directory.CreateDirectory(directory);
        }

        Packages.Add(new SoftwarePackage("VLC", "VideoLAN.VLC"));
        Packages.Add(new SoftwarePackage("Google Chrome", "Google.Chrome"));
        Packages.Add(new SoftwarePackage("Adobe Acrobat Reader", "Adobe.Acrobat.Reader.64-bit"));
        Packages.Add(new SoftwarePackage("LibreOffice", "TheDocumentFoundation.LibreOffice"));

        foreach (var package in Packages)
        {
            package.PropertyChanged += PackageOnPropertyChanged;
        }

        ConfigurationTasks.Add(
            new ConfigurationTask(
                "Désactiver la suspension USB dans les options d’alimentation",
                new[]
                {
                    "powercfg -setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0",
                    "powercfg -setdcvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0",
                    "powercfg -S SCHEME_CURRENT"
                })
            {
                Description = "Désactive la suspension sélective USB sur secteur et batterie."
            });

        ConfigurationTasks.Add(
            new ConfigurationTask(
                "Configurer la veille et la luminosité sur secteur",
                new[]
                {
                    "powercfg /change monitor-timeout-ac 30",
                    "powercfg /change standby-timeout-ac 45",
                    "Add-Type -AssemblyName System.Windows.Forms",
                    "Add-Type -AssemblyName System.Management",
                    "$brightness = 100",
                    "(Get-WmiObject -Namespace root/WMI -Class WmiMonitorBrightnessMethods).WmiSetBrightness(1, $brightness)"
                },
                new[]
                {
                    "powercfg /change monitor-timeout-ac 30",
                    "powercfg /change standby-timeout-ac 0",
                    "Add-Type -AssemblyName System.Windows.Forms",
                    "Add-Type -AssemblyName System.Management",
                    "$brightness = 100",
                    "(Get-WmiObject -Namespace root/WMI -Class WmiMonitorBrightnessMethods).WmiSetBrightness(1, $brightness)"
                })
            {
                Description = "Met la veille écran sur 30 minutes, la veille PC sur 45 minutes et règle la luminosité à 100 % (mode Pro : veille PC désactivée)."
            });

        ConfigurationTasks.Add(
            new ConfigurationTask(
                "Configurer l'arrêt des disques",
                new[]
                {
                    "powercfg /change disk-timeout-ac 0",
                    "powercfg /change disk-timeout-dc 0"
                })
            {
                Description = "Règle l'arrêt des disques durs sur 0 minute (jamais) sur secteur et batterie."
            });

        foreach (var task in ConfigurationTasks)
        {
            task.PropertyChanged += TaskOnPropertyChanged;
        }

        foreach (var (name, script) in ManualWindowsToolDefinitions)
        {
            var tool = new ManualWindowsTool(name, script);
            tool.PropertyChanged += ManualWindowsToolOnPropertyChanged;
            ManualWindowsTools.Add(tool);
        }

        UpdateWindowsToolsSelectAllState();

        ManualTasks.Add(new ManualTask("Modifier les paramètres d’alimentation avancés : Paramètres de la carte graphique (Intel Settings ou autres) : Performance max."));
        ManualTasks.Add(new ManualTask("Win + X / gestionnaire de périphérique – Pointer les pilotes manquants."));
        ManualTasks.Add(new ManualTask("Windows Update : rechercher et lancer + options avancées / Mises à jour facultatives : les cocher et les installer, Redémarrer dès lors que tout est installé. Relancer les updates jusqu’à ce qu’il n’y en ait plus, Vérifier que les pilotes soient correctement installés si non, site constructeur (ou HP Assistant et consorts)."));
        ManualTasks.Add(new ManualTask("Microsoft Store – Téléchargement – « Obtenir les mises à jour » ou « Tout mettre à jour »."));
        ManualTasks.Add(new ManualTask("Supprimer app pub type Xbox ou Linkedin."));
        ManualTasks.Add(new ManualTask("Clic droit bureau – Personnaliser – Thèmes – Paramètres des icones du Bureau – Cocher Ordinateur + Fichiers de l’utilisateur + Corbeille."));
        ManualTasks.Add(new ManualTask("Mettre « Ce PC » en dessous « Fichiers de l’utilisateur » au nom de l’utilisateur."));
        ManualTasks.Add(new ManualTask("Installer Google Chrome et/ou Firefox + Acrobat Reader + VLC + accords client."));
        ManualTasks.Add(new ManualTask("Dès lors que Windows Update et Microsoft Store OK : win + R / cleanmgr / « Nettoyer les fichiers système » / Tout cocher sauf Corbeille, Redémarrer."));
        ManualTasks.Add(new ManualTask("Nettoyer traces des téléchargements, historiques navigation."));
        ManualTasks.Add(new ManualTask("Win + X / Terminal (ou PowerShell) en admin / chkdsk c: /F + confirmer / sfc /scannow Redémarrer."));
        ManualTasks.Add(new ManualTask("🔎dfrgui ou « Ce PC » / clic droit sur C: / Propriété / Onglet Outils / Cocher « Vue Avancé » / Lancer « Optimiser » sur chacune des partitions quand cela est possible."));
        ManualTasks.Add(new ManualTask("UNIQUEMENT POUR LES PRO :  - Désactiver la mise en veille USB dans le gestionnaire de périphériques, - Désactiver la mise en veille du réseau."));

        LoadPackageLogos();
        UpdateProgramsSelectAllState();
        UpdateTasksSelectAllState();
        ApplyProfessionalModeToTasks();
    }

    private void PositionWindowOnRightHalf()
    {
        var workArea = SystemParameters.WorkArea;

        var targetWidth = Math.Max(MinWidth, workArea.Width / 2);
        targetWidth = Math.Min(targetWidth, workArea.Width);

        var targetHeight = Math.Max(MinHeight, workArea.Height);
        targetHeight = Math.Min(targetHeight, workArea.Height);

        Width = targetWidth;
        Height = targetHeight;
        Left = workArea.Left + workArea.Width - Width;
        Top = workArea.Top;
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsInstalling)
        {
            return;
        }

        var selectedPackages = Packages.Where(p => p.IsSelected).ToList();
        var selectedTasks = ConfigurationTasks.Where(t => t.IsSelected).ToList();

        if (!selectedPackages.Any() && !selectedTasks.Any())
        {
            MessageBox.Show(
                "Sélectionnez au moins un logiciel ou une tâche à exécuter.",
                "Information",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var selectedPackageSet = selectedPackages.ToHashSet();
        var selectedTaskSet = selectedTasks.ToHashSet();

        IsInstalling = true;
        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ProgramsSelectAllCheckBox.IsEnabled = false;
        TasksSelectAllCheckBox.IsEnabled = false;
        ProModeCheckBox.IsEnabled = false;
        PackagesList.IsEnabled = false;
        TasksList.IsEnabled = false;
        Logs.Clear();
        _lastLogEntry = null;
        _shouldAutoScroll = true;

        foreach (var package in Packages)
        {
            package.IsProgressVisible = false;
            if (!selectedPackageSet.Contains(package))
            {
                continue;
            }

            package.Status = "En attente...";
            package.Progress = 0;
        }

        foreach (var task in ConfigurationTasks)
        {
            task.Status = selectedTaskSet.Contains(task) ? "En attente..." : "Prêt";
        }

        _installationCts = new CancellationTokenSource();
        var cancellationToken = _installationCts.Token;
        var wasCancelled = false;

        IProgress<string> progress = new Progress<string>(message =>
        {
            Dispatcher.Invoke(() => AppendLogMessage(message));
        });

        try
        {
            for (var i = 0; i < selectedPackages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var package = selectedPackages[i];
                package.Status = "Installation en cours...";
                package.IsProgressVisible = true;
                package.Progress = 0;

                progress.Report($"[{package.Name}] Démarrage de l'installation.");

                var result = await InstallPackageAsync(package, progress, cancellationToken);
                if (result.IsCanceled || cancellationToken.IsCancellationRequested)
                {
                    wasCancelled = true;
                    CancelButton.IsEnabled = false;
                }

                if (wasCancelled)
                {
                    for (var j = i + 1; j < selectedPackages.Count; j++)
                    {
                        var pending = selectedPackages[j];
                        pending.Status = "Annulé";
                        pending.Progress = 0;
                        pending.IsProgressVisible = false;
                    }

                    break;
                }
            }

            if (!wasCancelled && selectedTasks.Count > 0)
            {
                progress.Report("Début des tâches de paramétrage Windows.");
            }

            if (!wasCancelled)
            {
                for (var i = 0; i < selectedTasks.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var task = selectedTasks[i];
                    task.Status = "Exécution en cours...";
                    progress.Report($"[Tâche] {task.Name} - démarrage.");

                    var result = await _configurationExecutor.ExecuteAsync(task, progress, cancellationToken);
                    if (result.IsCanceled || cancellationToken.IsCancellationRequested)
                    {
                        wasCancelled = true;
                        CancelButton.IsEnabled = false;
                    }

                    if (result.IsSuccess)
                    {
                        task.Status = "Terminé";
                        progress.Report($"[Tâche] {task.Name} terminée.");
                    }
                    else if (result.IsCanceled)
                    {
                        task.Status = "Annulé";
                    }
                    else
                    {
                        task.Status = "Échec";
                        if (!string.IsNullOrWhiteSpace(result.Message))
                        {
                            progress.Report($"[Tâche] {result.Message}");
                        }
                    }

                    if (wasCancelled)
                    {
                        for (var j = i + 1; j < selectedTasks.Count; j++)
                        {
                            var pendingTask = selectedTasks[j];
                            pendingTask.Status = "Annulé";
                        }

                        break;
                    }
                }
            }

            progress.Report(wasCancelled ? "Installation annulée." : "Installation terminée.");
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;

            foreach (var package in selectedPackages)
            {
                if (string.Equals(package.Status, "Installation en cours...", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(package.Status, "En attente...", StringComparison.OrdinalIgnoreCase))
                {
                    package.Status = "Annulé";
                    package.Progress = 0;
                    package.IsProgressVisible = false;
                }
            }

            foreach (var task in selectedTasks)
            {
                if (string.Equals(task.Status, "Exécution en cours...", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(task.Status, "En attente...", StringComparison.OrdinalIgnoreCase))
                {
                    task.Status = "Annulé";
                }
            }

            progress.Report("Installation annulée.");
        }
        finally
        {
            _installationCts?.Dispose();
            _installationCts = null;

            IsInstalling = false;
            InstallButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            ProgramsSelectAllCheckBox.IsEnabled = true;
            TasksSelectAllCheckBox.IsEnabled = true;
            ProModeCheckBox.IsEnabled = true;
            PackagesList.IsEnabled = true;
            TasksList.IsEnabled = true;
            UpdateProgramsSelectAllState();
            UpdateTasksSelectAllState();
        }
    }

    public bool IsProfessionalMode
    {
        get => _isProfessionalMode;
        set
        {
            if (_isProfessionalMode != value)
            {
                _isProfessionalMode = value;
                OnPropertyChanged(nameof(IsProfessionalMode));
                ApplyProfessionalModeToTasks();
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ((INotifyCollectionChanged)Logs).CollectionChanged -= LogsOnCollectionChanged;
        if (LogListBox != null)
        {
            LogListBox_OnUnloaded(LogListBox, new RoutedEventArgs());
        }
        base.OnClosed(e);
    }

    private List<string> BuildLogoDirectories()
    {
        var directories = new List<string>();

        var baseDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Logos");
        directories.Add(baseDirectory);

        var projectDirectory = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "Logos"));
        if (!directories.Contains(projectDirectory) && Directory.Exists(projectDirectory))
        {
            directories.Add(projectDirectory);
        }

        return directories;
    }

    private void LoadPackageLogos()
    {
        foreach (var package in Packages)
        {
            package.LogoPath = FindLogoForPackage(package.Name);
        }
    }

    private string? FindLogoForPackage(string packageName)
    {
        var normalized = packageName?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        foreach (var directory in _logoDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var filePath in Directory.EnumerateFiles(directory))
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                if (fileName != null && string.Equals(fileName, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return filePath;
                }
            }
        }

        return null;
    }

    private async Task<InstallationResult> InstallPackageAsync(SoftwarePackage package, IProgress<string> progress, CancellationToken cancellationToken)
    {
        ProgressSmoother? smoother = null;
        try
        {
            smoother = new ProgressSmoother(package);
            var percentProgress = new Progress<int>(value => smoother.Report(value));
            var result = await _installer.InstallAsync(package, progress, percentProgress, cancellationToken);

            if (result.IsCanceled)
            {
                smoother.Cancel();
                package.Status = "Annulé";
                package.Progress = 0;
                package.IsProgressVisible = false;
                return result;
            }

            if (result.IsSuccess)
            {
                smoother.Complete();
                await smoother.WaitForCompletionAsync();
                package.Status = "Installé";
                progress.Report($"[{package.Name}] Installation terminée.");
                return result;
            }

            smoother.Cancel();
            package.Status = "Échec";
            package.IsProgressVisible = false;

            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                progress.Report($"[{package.Name}] {result.Message}");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            smoother?.Cancel();
            package.Status = "Annulé";
            package.Progress = 0;
            package.IsProgressVisible = false;
            progress.Report($"[{package.Name}] Installation annulée.");
            return new InstallationResult(false, true, string.Empty);
        }
        catch (Exception ex)
        {
            smoother?.Cancel();
            package.Status = "Erreur";
            package.Progress = 0;
            package.IsProgressVisible = false;
            var message = $"[{package.Name}] {ex.Message}";
            progress.Report(message);
            return new InstallationResult(false, false, ex.Message);
        }
        finally
        {
            smoother?.Dispose();
        }
    }

    private static async Task<PowerShellResult> RunPowerShellScriptAsync(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return new PowerShellResult(0, string.Empty, string.Empty);
        }

        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(process.WaitForExitAsync(), standardOutputTask, standardErrorTask);

        return new PowerShellResult(process.ExitCode, standardOutputTask.Result, standardErrorTask.Result);
    }

    private void AppendLogMessage(string message)
    {
        var sanitized = SanitizeLogMessage(message);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return;
        }

        var lines = sanitized.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (!IsUsefulLogLine(trimmed))
            {
                continue;
            }

            if (!HasMeaningfulContentAfterAppTag(trimmed))
            {
                continue;
            }

            if (string.Equals(trimmed, _lastLogEntry, StringComparison.Ordinal))
            {
                continue;
            }

            _lastLogEntry = trimmed;
            Logs.Add(trimmed);
            if (_shouldAutoScroll)
            {
                if (_logScrollViewer != null)
                {
                    _logScrollViewer.ScrollToEnd();
                }
                else
                {
                    LogListBox.ScrollIntoView(trimmed);
                }
            }
        }
    }

    private static string SanitizeLogMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var normalizedMessage = message.Normalize(NormalizationForm.FormKC);
        var withoutAnsi = AnsiRegex.Replace(normalizedMessage, string.Empty);
        var withoutBlocks = BlockGlyphRegex.Replace(withoutAnsi, string.Empty);
        var withoutBrokenGlyphs = BrokenUtf8GlyphRegex.Replace(withoutBlocks, string.Empty);
        var cleaned = withoutBrokenGlyphs.Replace("\r", string.Empty);
        var builder = new StringBuilder(cleaned.Length);

        foreach (var ch in cleaned)
        {
            if (ch == '\n' || !char.IsControl(ch))
            {
                builder.Append(ch);
            }
        }

        var withoutProgressBars = SimpleProgressBarRegex.Replace(builder.ToString(), string.Empty);
        var normalized = ExtraWhitespaceRegex.Replace(withoutProgressBars, " ");
        return normalized.Trim();
    }

    private static bool IsUsefulLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (!UsefulContentRegex.IsMatch(line))
        {
            return false;
        }

        var withoutDelimiters = line.Trim('[', ']', '|', '›', '«', '»', '·', '-', '=', ':');
        if (string.IsNullOrWhiteSpace(withoutDelimiters))
        {
            return false;
        }

        if (!line.Any(char.IsWhiteSpace) && withoutDelimiters.Length <= 4)
        {
            return false;
        }

        return true;
    }

    private static bool HasMeaningfulContentAfterAppTag(string line)
    {
        var closingBracketIndex = line.IndexOf(']');
        if (closingBracketIndex < 0)
        {
            return true;
        }

        var openingBracketIndex = line.LastIndexOf('[', closingBracketIndex);
        if (openingBracketIndex < 0)
        {
            return true;
        }

        if (closingBracketIndex >= line.Length - 1)
        {
            return false;
        }

        var afterTag = line.Substring(closingBracketIndex + 1).Trim();
        return afterTag.Length >= 2;
    }

    private void LogsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_shouldAutoScroll || e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        if (_logScrollViewer != null)
        {
            _logScrollViewer.ScrollToEnd();
        }
        else if (LogListBox.Items.Count > 0)
        {
            var lastItem = LogListBox.Items[LogListBox.Items.Count - 1];
            LogListBox.ScrollIntoView(lastItem);
        }
    }

    private void LogListBox_OnLoaded(object sender, RoutedEventArgs e)
    {
        _logScrollViewer = FindVisualChild<ScrollViewer>(LogListBox);
        if (_logScrollViewer != null)
        {
            _logScrollViewer.ScrollChanged += LogScrollViewerOnScrollChanged;
        }
    }

    private void LogListBox_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_logScrollViewer != null)
        {
            _logScrollViewer.ScrollChanged -= LogScrollViewerOnScrollChanged;
            _logScrollViewer = null;
        }
    }

    private void LogScrollViewerOnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_logScrollViewer == null)
        {
            return;
        }

        if (e.ExtentHeightChange == 0)
        {
            var atBottom = Math.Abs(_logScrollViewer.VerticalOffset - _logScrollViewer.ScrollableHeight) < 0.5;
            _shouldAutoScroll = atBottom;
        }
        else if (_shouldAutoScroll)
        {
            _logScrollViewer.ScrollToEnd();
        }
    }

    private void CopyLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Logs.Count == 0)
        {
            MessageBox.Show("Le journal est vide.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var logContent = string.Join(Environment.NewLine, Logs);

        try
        {
            Clipboard.SetText(logContent);
            MessageBox.Show("Le journal a été copié dans le presse-papiers.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible de copier le journal : {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }

    private sealed class ProgressSmoother : IDisposable
    {
        private readonly SoftwarePackage _package;
        private readonly DispatcherTimer _timer;
        private readonly TimeSpan _tickInterval = TimeSpan.FromMilliseconds(120);
        private readonly TaskCompletionSource<bool> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _target;
        private bool _isCompleting;
        private bool _isCancelled;
        private int _idleTicks;

        public ProgressSmoother(SoftwarePackage package)
        {
            _package = package;
            _target = 5;
            _timer = new DispatcherTimer { Interval = _tickInterval };
            _timer.Tick += OnTick;
            _timer.Start();
            if (_package.Progress <= 0)
            {
                _package.Progress = 1;
            }
        }

        public void Report(int value)
        {
            if (_isCancelled)
            {
                return;
            }

            var clamped = Math.Max(0, Math.Min(99, value));
            if (clamped > _target)
            {
                _target = clamped;
            }

            _idleTicks = 0;
        }

        public void Complete()
        {
            _target = 100;
            _isCompleting = true;
            _idleTicks = 0;
        }

        public void Cancel()
        {
            _isCancelled = true;
            Stop();
        }

        public Task WaitForCompletionAsync()
        {
            return _completionSource.Task;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_isCancelled)
            {
                return;
            }

            if (_isCompleting)
            {
                if (_package.Progress < 100)
                {
                    var completionStep = Math.Max(1, (100 - _package.Progress) / 4);
                    _package.Progress = Math.Min(100, _package.Progress + completionStep);
                }
                else
                {
                    Stop();
                }

                return;
            }

            if (_package.Progress < _target)
            {
                var delta = Math.Max(1, (_target - _package.Progress + 2) / 3);
                _package.Progress = Math.Min(_target, _package.Progress + delta);
                _idleTicks = 0;
                return;
            }

            _idleTicks++;
            if (_idleTicks >= 6 && _target < 94)
            {
                _target++;
                _idleTicks = 0;
            }
        }

        private void Stop()
        {
            if (_timer.IsEnabled)
            {
                _timer.Stop();
            }

            _timer.Tick -= OnTick;
            _completionSource.TrySetResult(true);
        }

        public void Dispose()
        {
            _completionSource.TrySetResult(true);
            Stop();
        }
    }

    private void ProgramsSelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var shouldSelectAll = Packages.Any(p => !p.IsSelected);
        SetAllPackagesSelection(shouldSelectAll);
    }

    private void TasksSelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var shouldSelectAll = ConfigurationTasks.Any(t => !t.IsSelected);
        SetAllTasksSelection(shouldSelectAll);
    }

    private void WindowsToolsSelectAllCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        var shouldSelectAll = ManualWindowsTools.Any(t => !t.IsSelected);
        SetAllWindowsToolsSelection(shouldSelectAll);
    }

    private void SetAllPackagesSelection(bool isSelected)
    {
        foreach (var package in Packages)
        {
            package.IsSelected = isSelected;
        }
    }

    private void SetAllTasksSelection(bool isSelected)
    {
        foreach (var task in ConfigurationTasks)
        {
            task.IsSelected = isSelected;
        }
    }

    private void SetAllWindowsToolsSelection(bool isSelected)
    {
        foreach (var tool in ManualWindowsTools)
        {
            tool.IsSelected = isSelected;
        }

        UpdateWindowsToolsSelectAllState();
    }

    private void ApplyProfessionalModeToTasks()
    {
        foreach (var task in ConfigurationTasks)
        {
            task.ApplyProfessionalMode(IsProfessionalMode);
        }
    }

    private void PackageOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoftwarePackage.IsSelected))
        {
            UpdateProgramsSelectAllState();
        }
    }

    private void TaskOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigurationTask.IsSelected))
        {
            UpdateTasksSelectAllState();
        }
    }

    private void ManualWindowsToolOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ManualWindowsTool.IsSelected))
        {
            UpdateWindowsToolsSelectAllState();
        }
    }

    private void UpdateProgramsSelectAllState()
    {
        if (ProgramsSelectAllCheckBox == null)
        {
            return;
        }

        var selectedCount = Packages.Count(p => p.IsSelected);
        if (selectedCount == 0)
        {
            ProgramsSelectAllCheckBox.IsChecked = false;
        }
        else if (selectedCount == Packages.Count)
        {
            ProgramsSelectAllCheckBox.IsChecked = true;
        }
        else
        {
            ProgramsSelectAllCheckBox.IsChecked = null;
        }
    }

    private void UpdateTasksSelectAllState()
    {
        if (TasksSelectAllCheckBox == null)
        {
            return;
        }

        var selectedCount = ConfigurationTasks.Count(t => t.IsSelected);
        if (selectedCount == 0)
        {
            TasksSelectAllCheckBox.IsChecked = false;
        }
        else if (selectedCount == ConfigurationTasks.Count)
        {
            TasksSelectAllCheckBox.IsChecked = true;
        }
        else
        {
            TasksSelectAllCheckBox.IsChecked = null;
        }
    }

    private void UpdateWindowsToolsSelectAllState()
    {
        if (ManualWindowsTools.Count == 0)
        {
            AreAllWindowsToolsSelected = false;
            return;
        }

        var selectedCount = ManualWindowsTools.Count(tool => tool.IsSelected);
        if (selectedCount == 0)
        {
            AreAllWindowsToolsSelected = false;
        }
        else if (selectedCount == ManualWindowsTools.Count)
        {
            AreAllWindowsToolsSelected = true;
        }
        else
        {
            AreAllWindowsToolsSelected = null;
        }
    }

    private void WindowsToolsToggleButton_OnChecked(object sender, RoutedEventArgs e)
    {
        if (WindowsToolsPopup != null)
        {
            WindowsToolsPopup.IsOpen = true;
        }
    }

    private void WindowsToolsToggleButton_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (WindowsToolsPopup != null)
        {
            WindowsToolsPopup.IsOpen = false;
        }
    }

    private void WindowsToolsPopup_OnClosed(object? sender, EventArgs e)
    {
        if (WindowsToolsToggleButton != null)
        {
            WindowsToolsToggleButton.IsChecked = false;
        }
    }

    private void CloseWindowsToolsPopup()
    {
        if (WindowsToolsPopup != null)
        {
            WindowsToolsPopup.IsOpen = false;
        }

        if (WindowsToolsToggleButton != null)
        {
            WindowsToolsToggleButton.IsChecked = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed record PowerShellResult(int ExitCode, string StandardOutput, string StandardError);

    public sealed class ManualWindowsTool : INotifyPropertyChanged
    {
        private bool _isSelected = true;

        public ManualWindowsTool(string name, string script)
        {
            Name = name;
            Script = script;
        }

        public string Name { get; }

        public string Script { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
