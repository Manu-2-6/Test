using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SoftwareSetupApp.Models;

public class ManualTask : INotifyPropertyChanged
{
    private bool _isCompleted;

    public ManualTask(string name, string? associatedToolKey = null, string? associatedToolDisplayName = null)
    {
        Name = name;
        AssociatedToolKey = string.IsNullOrWhiteSpace(associatedToolKey) ? null : associatedToolKey;
        AssociatedToolDisplayName = associatedToolDisplayName
            ?? associatedToolKey
            ?? string.Empty;
    }

    public string Name { get; }

    public string? AssociatedToolKey { get; }

    public string AssociatedToolDisplayName { get; }

    public bool HasAssociatedTool => !string.IsNullOrWhiteSpace(AssociatedToolKey);

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
