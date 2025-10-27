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

    private readonly WingetInstaller _installer = new();
    private readonly WindowsConfigurationExecutor _configurationExecutor = new();
    private readonly List<string> _logoDirectories;
    private bool _isInstalling;
    private CancellationTokenSource? _installationCts;
    private string? _lastLogEntry;
    private ScrollViewer? _logScrollViewer;
    private bool _shouldAutoScroll = true;

    public ObservableCollection<SoftwarePackage> Packages { get; } = new();
    public ObservableCollection<ConfigurationTask> ConfigurationTasks { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

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

        foreach (var task in ConfigurationTasks)
        {
            task.PropertyChanged += TaskOnPropertyChanged;
        }

        LoadPackageLogos();
        UpdateProgramsSelectAllState();
        UpdateTasksSelectAllState();
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
            PackagesList.IsEnabled = true;
            TasksList.IsEnabled = true;
            UpdateProgramsSelectAllState();
            UpdateTasksSelectAllState();
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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
