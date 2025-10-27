using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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
    private static readonly HashSet<string> EssentialPackageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "VLC",
        "Google Chrome",
        "Adobe Acrobat Reader",
        "LibreOffice"
    };

    private readonly WingetInstaller _installer = new();
    private readonly SetupAutomationService _automationService = new();
    private readonly List<string> _logoDirectories;
    private bool _isInstalling;
    private CancellationTokenSource? _installationCts;
    private string? _lastLogEntry;
    private ScrollViewer? _logScrollViewer;
    private bool _shouldAutoScroll = true;

    public ObservableCollection<SoftwarePackage> Packages { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();
    public ObservableCollection<SetupTask> Tasks { get; } = new();

    public ICollectionView TasksView { get; }

    public IReadOnlyList<SelectionOption<DeviceType>> DeviceTypeOptions { get; }

    public IReadOnlyList<SelectionOption<UserProfile>> UserProfileOptions { get; }

    private DeviceType _selectedDeviceType = DeviceType.Desktop;
    private UserProfile _selectedUserProfile = UserProfile.Standard;

    public DeviceType SelectedDeviceType
    {
        get => _selectedDeviceType;
        set
        {
            if (_selectedDeviceType != value)
            {
                _selectedDeviceType = value;
                OnPropertyChanged(nameof(SelectedDeviceType));
                TasksView.Refresh();
            }
        }
    }

    public UserProfile SelectedUserProfile
    {
        get => _selectedUserProfile;
        set
        {
            if (_selectedUserProfile != value)
            {
                _selectedUserProfile = value;
                OnPropertyChanged(nameof(SelectedUserProfile));
                TasksView.Refresh();
            }
        }
    }

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

    public MainWindow()
    {
        InitializeComponent();
        DeviceTypeOptions = new List<SelectionOption<DeviceType>>
        {
            new(DeviceType.Desktop, "Ordinateur fixe"),
            new(DeviceType.Laptop, "Ordinateur portable")
        };

        UserProfileOptions = new List<SelectionOption<UserProfile>>
        {
            new(UserProfile.Standard, "Utilisateur standard"),
            new(UserProfile.Medic, "Médecin")
        };

        TasksView = CollectionViewSource.GetDefaultView(Tasks);
        TasksView.Filter = TaskFilter;

        DataContext = this;

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

        LoadPackageLogos();
        InitializeTasks();
    }

    private bool TaskFilter(object? item)
    {
        if (item is not SetupTask task)
        {
            return false;
        }

        return task.AppliesTo(SelectedDeviceType, SelectedUserProfile);
    }

    private void InitializeTasks()
    {
        Tasks.Clear();

        Tasks.Add(new SetupTask(
            "PC fixe : configurer la veille et la luminosité",
            """
            Pour un ordinateur fixe sur secteur :
            • Veille écran : 30 minutes
            • Veille PC : 1 heure (professionnels : aucun)
            • Luminosité : 100 %
            """,
            DeviceTypeScope.Desktop,
            UserProfileScope.All,
            _automationService.ConfigureDesktopPowerAsync));

        Tasks.Add(new SetupTask(
            "PC portable : configurer la veille sur secteur",
            """
            Pour un ordinateur portable branché sur secteur :
            • Veille écran : 30 minutes
            • Veille PC : 1 heure (professionnels : aucun)
            • Luminosité : 100 %
            """,
            DeviceTypeScope.Laptop,
            UserProfileScope.All,
            _automationService.ConfigureLaptopPowerAsync));

        Tasks.Add(new SetupTask(
            "Modifier les paramètres d’alimentation avancés",
            """
            - Arrêt des disques : 0 minute
            - Paramètres de la carte graphique : performances maximales
            - Gestion de l’alimentation du processeur : état minimal 100 %
            """,
            DeviceTypeScope.All,
            UserProfileScope.All,
            _automationService.ConfigureAdvancedPowerAsync));

        Tasks.Add(new SetupTask(
            "Médecins : désactiver la suspension USB dans les options d’alimentation",
            """
            1. Démarrer → Panneau de configuration → Matériel et audio → Options d’alimentation
            2. Modifier les paramètres du mode actif
            3. Ouvrir les paramètres d’alimentation avancés
            4. Paramètres USB → Paramètre de suspension sélective USB → Désactivé (secteur et batterie)
            5. Appliquer puis valider
            """,
            DeviceTypeScope.All,
            UserProfileScope.Medic,
            _automationService.DisableUsbSelectiveSuspendAsync));

        Tasks.Add(new SetupTask(
            "Médecins : empêcher la mise en veille USB dans le gestionnaire de périphériques",
            """
            1. Win + X → Gestionnaire de périphériques
            2. Déployer « Contrôleurs de bus USB »
            3. Pour chaque concentrateur USB racine → Propriétés → Onglet Gestion de l’alimentation
            4. Décocher « Autoriser l’ordinateur à éteindre ce périphérique pour économiser l’énergie »
            """,
            DeviceTypeScope.All,
            UserProfileScope.Medic,
            _automationService.DisableUsbPowerManagementAsync));

        Tasks.Add(new SetupTask(
            "Médecins : désactiver la mise en veille des cartes réseau",
            """
            1. Gestionnaire de périphériques → Cartes réseau
            2. Ouvrir la carte utilisée (Ethernet ou Wi-Fi)
            3. Onglet Gestion de l’alimentation → Décocher l’extinction automatique
            """,
            DeviceTypeScope.All,
            UserProfileScope.Medic,
            _automationService.DisableNetworkPowerManagementAsync));

        Tasks.Add(new SetupTask(
            "Gestionnaire de périphériques : contrôler les pilotes",
            "Win + X → Gestionnaire de périphériques → rechercher les pilotes manquants (icônes avec avertissement)",
            DeviceTypeScope.All,
            UserProfileScope.All,
            _automationService.CheckDeviceManagerAsync));

        Tasks.Add(new SetupTask(
            "Windows Update : appliquer toutes les mises à jour",
            """
            - Lancer la recherche des mises à jour
            - Installer également les mises à jour facultatives
            - Redémarrer puis relancer la recherche jusqu’à absence de mises à jour
            - Si besoin, compléter via le site constructeur ou les assistants OEM
            """,
            DeviceTypeScope.All,
            UserProfileScope.All,
            _automationService.RunWindowsUpdateAsync));

        Tasks.Add(new SetupTask(
            "Microsoft Store : tout mettre à jour",
            "Ouvrir l’onglet Téléchargements, cliquer sur « Obtenir les mises à jour » ou « Tout mettre à jour »",
            DeviceTypeScope.All,
            UserProfileScope.All,
            _automationService.RunMicrosoftStoreUpdatesAsync));

        Tasks.Add(new SetupTask(
            "Désinstaller les applications publicitaires",
            "Supprimer les applications préinstallées type Xbox, LinkedIn ou promotions équivalentes",
            DeviceTypeScope.All,
            UserProfileScope.All,
            _automationService.RemoveBloatwareAsync));

        Tasks.Add(new SetupTask(
            "Afficher les icônes système sur le bureau",
            "Bureau → Clic droit → Personnaliser → Thèmes → Paramètres des icônes du bureau → activer Ordinateur, Fichiers de l’utilisateur et Corbeille",
            DeviceTypeScope.All,
            UserProfileScope.All,
            _automationService.EnableDesktopIconsAsync));

        Tasks.Add(new SetupTask(
            "Réorganiser l’explorateur",
            "Dans l’explorateur, placer « Ce PC » sous « Fichiers de l’utilisateur » portant le nom de l’utilisateur",
            DeviceTypeScope.All,
            UserProfileScope.All));

        Tasks.Add(new SetupTask(
            "Installer les navigateurs et utilitaires essentiels",
            "Installer Google Chrome et/ou Firefox, Adobe Acrobat Reader, VLC et les accords client requis",
            DeviceTypeScope.All,
            UserProfileScope.All));

        Tasks.Add(new SetupTask(
            "Nettoyage disque après mises à jour",
            """
            1. Win + R → cleanmgr
            2. Cliquer sur « Nettoyer les fichiers système »
            3. Tout cocher sauf la Corbeille puis valider
            4. Redémarrer la machine
            """,
            DeviceTypeScope.All,
            UserProfileScope.All,
            _automationService.RunDiskCleanupAsync));

        Tasks.Add(new SetupTask(
            "Nettoyer les historiques et téléchargements",
            "Effacer l’historique des navigateurs et supprimer les fichiers temporaires ou téléchargements inutiles",
            DeviceTypeScope.All,
            UserProfileScope.All,
            _automationService.ClearTemporaryDataAsync));

        Tasks.Add(new SetupTask(
            "Vérifications système (chkdsk & SFC)",
            "Win + X → Terminal/PowerShell (admin) → exécuter successivement ‘chkdsk c: /F’ (confirmer) puis ‘sfc /scannow’, redémarrer ensuite",
            DeviceTypeScope.All,
            UserProfileScope.All,
            _automationService.RunSystemChecksAsync));

        Tasks.Add(new SetupTask(
            "Optimiser les lecteurs",
            "Ouvrir dfrgui ou Ce PC → clic droit sur C: → Propriétés → Outils → Optimiser chaque partition disponible",
            DeviceTypeScope.All,
            UserProfileScope.All,
            _automationService.OptimizeDrivesAsync));

        TasksView.Refresh();
    }

    private async Task<AutomationRunSummary> RunAutomationTasksAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        var applicableTasks = Tasks
            .Where(t => t.AppliesTo(SelectedDeviceType, SelectedUserProfile) && t.IsSelected)
            .ToList();
        var manualTasks = applicableTasks.Where(t => !t.HasAutomation).ToList();

        foreach (var manualTask in manualTasks)
        {
            progress.Report($"[Checklist] Action manuelle requise : {manualTask.Title}");
        }

        var automatedTasks = applicableTasks.Where(t => t.HasAutomation).ToList();
        foreach (var automatedTask in automatedTasks)
        {
            automatedTask.IsCompleted = false;
        }

        if (automatedTasks.Count == 0)
        {
            TasksView.Refresh();
            return new AutomationRunSummary(false, false);
        }

        var hadFailures = false;

        foreach (var task in automatedTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress.Report($"[Checklist] Démarrage : {task.Title}");

            var context = new SetupAutomationContext(
                SelectedDeviceType,
                SelectedUserProfile,
                message =>
                {
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        return;
                    }

                    if (message.StartsWith("[Checklist]", StringComparison.OrdinalIgnoreCase))
                    {
                        progress.Report(message);
                    }
                    else
                    {
                        progress.Report($"[Checklist] {message}");
                    }
                });

            AutomationResult result;
            try
            {
                result = await task.ExecuteAsync(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                progress.Report($"[Checklist] Automatisation annulée pendant « {task.Title} ».");
                throw;
            }

            if (result.IsSuccess)
            {
                if (!task.IsCompleted)
                {
                    task.IsCompleted = true;
                }

                progress.Report($"[Checklist] Terminé : {task.Title}");

                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    progress.Report($"[Checklist] {result.Message}");
                }
            }
            else
            {
                hadFailures = true;
                var errorMessage = string.IsNullOrWhiteSpace(result.Message)
                    ? "Une erreur est survenue."
                    : result.Message;
                progress.Report($"[Checklist] Échec : {task.Title} — {errorMessage}");
            }
        }

        TasksView.Refresh();
        return new AutomationRunSummary(automatedTasks.Any(), hadFailures);
    }

    private bool UpdateInstallTaskCompletion(bool packageFailures)
    {
        var installTask = Tasks.FirstOrDefault(t =>
            string.Equals(t.Title, "Installer les navigateurs et utilitaires essentiels", StringComparison.OrdinalIgnoreCase));

        if (installTask == null)
        {
            return false;
        }

        if (!installTask.IsSelected)
        {
            if (installTask.IsCompleted)
            {
                installTask.IsCompleted = false;
            }

            TasksView.Refresh();
            return false;
        }

        if (packageFailures)
        {
            installTask.IsCompleted = false;
            TasksView.Refresh();
            return false;
        }

        var allEssentialInstalled = Packages
            .Where(p => EssentialPackageNames.Contains(p.Name))
            .All(p => string.Equals(p.Status, "Installé", StringComparison.OrdinalIgnoreCase));

        if (allEssentialInstalled)
        {
            installTask.IsCompleted = true;
            TasksView.Refresh();
            return true;
        }

        installTask.IsCompleted = false;
        TasksView.Refresh();
        return false;
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsInstalling)
        {
            return;
        }

        var selectedPackages = Packages.Where(p => p.IsSelected).ToList();
        var applicableTasks = Tasks
            .Where(t => t.AppliesTo(SelectedDeviceType, SelectedUserProfile) && t.IsSelected)
            .ToList();
        var hasAutomatedTasks = applicableTasks.Any(t => t.HasAutomation);
        var hasManualTasks = applicableTasks.Any(t => !t.HasAutomation);

        if (!selectedPackages.Any() && !hasAutomatedTasks && !hasManualTasks)
        {
            MessageBox.Show("Aucune action n'est disponible pour ce profil.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedSet = selectedPackages.ToHashSet();

        IsInstalling = true;
        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        SelectAllCheckBox.IsEnabled = false;
        PackagesList.IsEnabled = false;
        Logs.Clear();
        _lastLogEntry = null;
        _shouldAutoScroll = true;

        foreach (var package in Packages)
        {
            package.IsProgressVisible = false;
            if (!selectedSet.Contains(package))
            {
                continue;
            }

            package.Status = "En attente...";
            package.Progress = 0;
        }

        _installationCts = new CancellationTokenSource();
        var cancellationToken = _installationCts.Token;
        var wasCancelled = false;
        var automationFailures = false;
        var packageFailures = false;

        IProgress<string> progress = new Progress<string>(message =>
        {
            Dispatcher.Invoke(() => AppendLogMessage(message));
        });

        try
        {
            AutomationRunSummary automationSummary = new(false, false);
            try
            {
                automationSummary = await RunAutomationTasksAsync(progress, cancellationToken);
                automationFailures = automationSummary.HadFailures;
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
            }

            if (wasCancelled)
            {
                progress.Report("[Checklist] Préparation annulée avant l'installation des applications.");
            }
            else if (selectedPackages.Any())
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

                    if (!result.IsSuccess)
                    {
                        packageFailures = true;
                        if (!string.IsNullOrWhiteSpace(result.Message))
                        {
                            progress.Report($"[{package.Name}] {result.Message}");
                        }
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

                progress.Report(wasCancelled ? "Installation annulée." : "Installation terminée.");
            }

            if (!wasCancelled)
            {
                var installTaskCompleted = UpdateInstallTaskCompletion(packageFailures);
                if (installTaskCompleted)
                {
                    progress.Report("[Checklist] Logiciels essentiels installés.");
                }

                if (automationFailures || packageFailures)
                {
                    progress.Report("[Checklist] Préparation terminée avec avertissements. Consultez le journal pour les détails.");
                }
                else
                {
                    progress.Report("[Checklist] Préparation terminée avec succès.");
                }
            }
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

            progress.Report("Préparation annulée.");
        }
        finally
        {
            _installationCts?.Dispose();
            _installationCts = null;

            IsInstalling = false;
            InstallButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            SelectAllCheckBox.IsEnabled = true;
            PackagesList.IsEnabled = true;
            UpdateSelectAllState();
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

    private sealed record AutomationRunSummary(bool RanAny, bool HadFailures);

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

    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var shouldSelectAll = Packages.Any(p => !p.IsSelected);
        SetAllPackagesSelection(shouldSelectAll);
    }

    private void SetAllPackagesSelection(bool isSelected)
    {
        foreach (var package in Packages)
        {
            package.IsSelected = isSelected;
        }
    }

    private void PackageOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoftwarePackage.IsSelected))
        {
            UpdateSelectAllState();
        }
    }

    private void UpdateSelectAllState()
    {
        var selectedCount = Packages.Count(p => p.IsSelected);
        if (selectedCount == 0)
        {
            SelectAllCheckBox.IsChecked = false;
        }
        else if (selectedCount == Packages.Count)
        {
            SelectAllCheckBox.IsChecked = true;
        }
        else
        {
            SelectAllCheckBox.IsChecked = null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
