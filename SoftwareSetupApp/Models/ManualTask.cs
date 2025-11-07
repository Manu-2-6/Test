using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SoftwareSetupApp.Models;

public class ManualTask : INotifyPropertyChanged
{
    private bool _isCompleted;

    public ManualTask(string name, string? linkText = null, string? linkScript = null)
    {
        Name = name;
        LinkText = linkText?.Trim() ?? string.Empty;
        LinkScript = linkScript?.Trim() ?? string.Empty;
    }

    public string Name { get; }

    public string LinkText { get; }

    public string LinkScript { get; }

    public bool HasLink => !string.IsNullOrWhiteSpace(LinkText) && !string.IsNullOrWhiteSpace(LinkScript);

    public bool IsCompleted
    {
        get => _isCompleted;
        set
        {
            if (_isCompleted != value)
            {
                _isCompleted = value;
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
