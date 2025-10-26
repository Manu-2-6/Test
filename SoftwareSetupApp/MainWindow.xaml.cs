using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SoftwareSetupApp.Models;
using SoftwareSetupApp.Services;

namespace SoftwareSetupApp;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly WingetInstaller _installer = new();
    private bool _isInstalling;

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

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        Packages.Add(new SoftwarePackage("VLC", "VideoLAN.VLC"));
        Packages.Add(new SoftwarePackage("Google Chrome", "Google.Chrome"));
        Packages.Add(new SoftwarePackage("Adobe Acrobat Reader", "Adobe.Acrobat.Reader.64-bit"));

        foreach (var package in Packages)
        {
            package.PropertyChanged += PackageOnPropertyChanged;
        }
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

        IsInstalling = true;
        InstallButton.IsEnabled = false;
        SelectAllCheckBox.IsEnabled = false;
        PackagesList.IsEnabled = false;
        Logs.Clear();

        IProgress<string> progress = new Progress<string>(message =>
        {
            Dispatcher.Invoke(() =>
            {
                Logs.Add(message);
                LogListBox.ScrollIntoView(message);
            });
        });

        foreach (var package in selectedPackages)
        {
            package.Status = "Installation en cours...";
            await InstallPackageAsync(package, progress);
        }

        progress.Report("Installation terminée.");

        IsInstalling = false;
        InstallButton.IsEnabled = true;
        SelectAllCheckBox.IsEnabled = true;
        PackagesList.IsEnabled = true;
        UpdateSelectAllState();
    }

    private async Task InstallPackageAsync(SoftwarePackage package, IProgress<string> progress)
    {
        try
        {
            var result = await _installer.InstallAsync(package, progress, CancellationToken.None);
            package.Status = result.IsSuccess
                ? "Installé"
                : "Échec";

            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                progress.Report(result.Message);
            }
        }
        catch (Exception ex)
        {
            package.Status = "Erreur";
            progress.Report($"[{package.Name}] {ex.Message}");
        }
    }

    private void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        foreach (var package in Packages)
        {
            package.IsSelected = true;
        }
    }

    private void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        foreach (var package in Packages)
        {
            package.IsSelected = false;
        }
    }

    private void SelectAllCheckBox_Indeterminate(object sender, RoutedEventArgs e)
    {
        // No direct action required when indeterminate, state is controlled by individual selections.
    }

    private void SelectAllCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox checkBox)
        {
            return;
        }

        var selectedCount = Packages.Count(p => p.IsSelected);
        if (selectedCount > 0 && selectedCount < Packages.Count && checkBox.IsChecked == null)
        {
            checkBox.IsChecked = false;
            e.Handled = true;
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
