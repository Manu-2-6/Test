using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SoftwareSetupApp.Models;

public class ChromeCustomizationOptions : INotifyPropertyChanged
{
    private bool _pinToTaskbar = true;
    private bool _configureHomepage = true;
    private bool _showBookmarksBar = true;
    private bool _addGoogleBookmark = true;

    public bool PinToTaskbar
    {
        get => _pinToTaskbar;
        set
        {
            if (_pinToTaskbar != value)
            {
                _pinToTaskbar = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAnySelection));
            }
        }
    }

    public bool ConfigureHomepage
    {
        get => _configureHomepage;
        set
        {
            if (_configureHomepage != value)
            {
                _configureHomepage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAnySelection));
                OnPropertyChanged(nameof(RequiresPolicyUpdate));
            }
        }
    }

    public bool ShowBookmarksBar
    {
        get => _showBookmarksBar;
        set
        {
            if (_showBookmarksBar != value)
            {
                _showBookmarksBar = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAnySelection));
                OnPropertyChanged(nameof(RequiresPolicyUpdate));
            }
        }
    }

    public bool AddGoogleBookmark
    {
        get => _addGoogleBookmark;
        set
        {
            if (_addGoogleBookmark != value)
            {
                _addGoogleBookmark = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAnySelection));
                OnPropertyChanged(nameof(RequiresPolicyUpdate));
            }
        }
    }

    public bool HasAnySelection => PinToTaskbar || ConfigureHomepage || ShowBookmarksBar || AddGoogleBookmark;

    public bool RequiresPolicyUpdate => ConfigureHomepage || ShowBookmarksBar || AddGoogleBookmark;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
