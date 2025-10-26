using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using SoftwareSetupApp.Models;
using SoftwareSetupApp.Services;

namespace SoftwareSetupApp;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly Regex AnsiRegex = new("\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    private readonly WingetInstaller _installer = new();
    private readonly List<string> _logoDirectories;
    private bool _isInstalling;
    private CancellationTokenSource? _installationCts;
    private string? _lastLogEntry;

    public ObservableCollection<SoftwarePackage> Packages { get; } = new();
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

        foreach (var directory in _logoDirectories)
        {
            Directory.CreateDirectory(directory);
        }

        Packages.Add(new SoftwarePackage("VLC", "VideoLAN.VLC"));
        Packages.Add(new SoftwarePackage("Google Chrome", "Google.Chrome"));
        Packages.Add(new SoftwarePackage("Adobe Acrobat Reader", "Adobe.Acrobat.Reader.64-bit"));

        foreach (var package in Packages)
        {
            package.PropertyChanged += PackageOnPropertyChanged;
        }

        LoadPackageLogos();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsInstalling)
        {
            return;
        }

        var selectedPackages = Packages.Where(p => p.IsSelected).ToList();
        if (!selectedPackages.Any())
        {
            MessageBox.Show("Sélectionnez au moins un logiciel à installer.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
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

            progress.Report("Installation annulée.");
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
        try
        {
            var percentProgress = new Progress<int>(value => package.Progress = value);
            var result = await _installer.InstallAsync(package, progress, percentProgress, cancellationToken);

            if (result.IsCanceled)
            {
                package.Status = "Annulé";
                package.Progress = 0;
                package.IsProgressVisible = false;
                return result;
            }

            if (result.IsSuccess)
            {
                package.Progress = 100;
                package.Status = "Installé";
                progress.Report($"[{package.Name}] Installation terminée.");
                return result;
            }

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
            package.Status = "Annulé";
            package.Progress = 0;
            package.IsProgressVisible = false;
            progress.Report($"[{package.Name}] Installation annulée.");
            return new InstallationResult(false, true, string.Empty);
        }
        catch (Exception ex)
        {
            package.Status = "Erreur";
            package.Progress = 0;
            package.IsProgressVisible = false;
            var message = $"[{package.Name}] {ex.Message}";
            progress.Report(message);
            return new InstallationResult(false, false, ex.Message);
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

            if (string.Equals(trimmed, _lastLogEntry, StringComparison.Ordinal))
            {
                continue;
            }

            _lastLogEntry = trimmed;
            Logs.Add(trimmed);
            LogListBox.ScrollIntoView(trimmed);
        }
    }

    private static string SanitizeLogMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var withoutAnsi = AnsiRegex.Replace(message, string.Empty);
        var builder = new StringBuilder(withoutAnsi.Length);

        foreach (var ch in withoutAnsi)
        {
            if (ch == '\n' || !char.IsControl(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
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
