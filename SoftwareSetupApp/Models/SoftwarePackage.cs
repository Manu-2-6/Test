using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SoftwareSetupApp.Models;

public class SoftwarePackage : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status = "Prêt";
    private string? _logoPath;
    private int _progress;
    private bool _isProgressVisible;

    public SoftwarePackage(string name, string packageId, string? source = null)
    {
        Name = name;
        PackageId = packageId;
        Source = string.IsNullOrWhiteSpace(source) ? null : source;
    }

    public string Name { get; }

    public string PackageId { get; }

    public string? Source { get; }

    public string? LogoPath
    {
        get => _logoPath;
        set
        {
            if (_logoPath != value)
            {
                _logoPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLogo));
            }
        }
    }

    public bool HasLogo => !string.IsNullOrWhiteSpace(_logoPath);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
            }
        }
    }

    public int Progress
    {
        get => _progress;
        set
        {
            var clamped = Math.Max(0, Math.Min(100, value));
            if (_progress != clamped)
            {
                _progress = clamped;
                OnPropertyChanged();
            }
        }
    }

    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        set
        {
            if (_isProgressVisible != value)
            {
                _isProgressVisible = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
