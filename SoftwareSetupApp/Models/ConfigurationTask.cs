using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SoftwareSetupApp.Models;

public class ConfigurationTask : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status = "Prêt";

    public ConfigurationTask(string name, IEnumerable<string> commands)
    {
        Name = name;
        Commands = new ReadOnlyCollection<string>(commands?.ToList() ?? new List<string>());
    }

    public string Name { get; }

    public ReadOnlyCollection<string> Commands { get; }

    public string? Description { get; init; }

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

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
