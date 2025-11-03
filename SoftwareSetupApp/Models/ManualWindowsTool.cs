using System.ComponentModel;

namespace SoftwareSetupApp.Models;

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
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
