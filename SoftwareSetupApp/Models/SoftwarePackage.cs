using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SoftwareSetupApp.Models;

public class SoftwarePackage : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status = "Prêt";
    private string? _logoPath;
    private int _progress;
    private bool _isProgressVisible;
    private readonly List<DefaultAssociation> _defaultAssociations;
    private bool _isDefaultAppSelected = true;

    public SoftwarePackage(
        string name,
        string packageId,
        IEnumerable<DefaultAssociation>? defaultAssociations = null,
        ChromeCustomizationOptions? chromeOptions = null)
    {
        Name = name;
        PackageId = packageId;
        _defaultAssociations = defaultAssociations?.ToList() ?? new List<DefaultAssociation>();
        ChromeOptions = chromeOptions;
        if (!SupportsDefaultApp)
        {
            _isDefaultAppSelected = false;
        }
    }

    public string Name { get; }

    public string PackageId { get; }

    public bool SupportsDefaultApp => _defaultAssociations.Count > 0;

    public bool HasCustomOptions => SupportsDefaultApp || ChromeOptions != null;

    public bool HasChromeOptions => ChromeOptions != null;

    public IReadOnlyList<DefaultAssociation> DefaultAssociations => _defaultAssociations;

    public ChromeCustomizationOptions? ChromeOptions { get; }

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

    public bool IsDefaultAppSelected
    {
        get => _isDefaultAppSelected;
        set
        {
            if (_isDefaultAppSelected != value)
            {
                _isDefaultAppSelected = value;
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
