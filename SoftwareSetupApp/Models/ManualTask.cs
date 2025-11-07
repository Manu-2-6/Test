using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SoftwareSetupApp.Models;

public class ManualTask : INotifyPropertyChanged
{
    private bool _isCompleted;
    private ManualWindowsTool? _associatedTool;

    public ManualTask(string name, ManualWindowsTool? associatedTool = null)
    {
        Name = name;
        AssociatedTool = associatedTool;
    }

    public string Name { get; }

    public ManualWindowsTool? AssociatedTool
    {
        get => _associatedTool;
        set
        {
            if (_associatedTool == value)
            {
                return;
            }

            _associatedTool = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAssociatedTool));
            OnPropertyChanged(nameof(AssociatedToolName));
        }
    }

    public bool HasAssociatedTool => AssociatedTool != null;

    public string AssociatedToolName => AssociatedTool?.Name ?? string.Empty;

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
