using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SoftwareSetupApp.Models;

public class ConfigurationTask : INotifyPropertyChanged
{
    private readonly List<string> _defaultCommands;
    private readonly List<string> _currentCommands;
    private readonly List<string>? _professionalCommands;
    private bool _isSelected;
    private string _status = "Prêt";

    public ConfigurationTask(string name, IEnumerable<string> commands, IEnumerable<string>? professionalCommands = null)
    {
        Name = name;
        _defaultCommands = commands?.ToList() ?? new List<string>();
        _currentCommands = new List<string>(_defaultCommands);

        if (professionalCommands != null)
        {
            _professionalCommands = professionalCommands.ToList();
        }

        Commands = new ReadOnlyCollection<string>(_currentCommands);
    }

    public string Name { get; }

    public ReadOnlyCollection<string> Commands { get; }

    public bool HasProfessionalVariant => _professionalCommands != null;

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

    public void ApplyProfessionalMode(bool isProfessional)
    {
        var source = isProfessional && _professionalCommands != null
            ? _professionalCommands
            : _defaultCommands;

        UpdateCommands(source);
    }

    private void UpdateCommands(IEnumerable<string> commands)
    {
        _currentCommands.Clear();

        foreach (var command in commands)
        {
            _currentCommands.Add(command);
        }

        OnPropertyChanged(nameof(Commands));
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
