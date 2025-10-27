using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SoftwareSetupApp.Models;

[Flags]
public enum DeviceTypeScope
{
    Desktop = 1,
    Laptop = 2,
    All = Desktop | Laptop
}

[Flags]
public enum UserProfileScope
{
    Standard = 1,
    Medic = 2,
    All = Standard | Medic
}

public enum DeviceType
{
    Desktop,
    Laptop
}

public enum UserProfile
{
    Standard,
    Medic
}

public class SetupTask : INotifyPropertyChanged
{
    private bool _isCompleted;

    public SetupTask(
        string title,
        string details,
        DeviceTypeScope deviceScope,
        UserProfileScope profileScope)
    {
        Title = title;
        Details = details;
        DeviceScope = deviceScope;
        ProfileScope = profileScope;
    }

    public string Title { get; }

    public string Details { get; }

    public DeviceTypeScope DeviceScope { get; }

    public UserProfileScope ProfileScope { get; }

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

    public bool AppliesTo(DeviceType deviceType, UserProfile profile)
    {
        var deviceFlag = deviceType == DeviceType.Desktop ? DeviceTypeScope.Desktop : DeviceTypeScope.Laptop;
        var profileFlag = profile == UserProfile.Medic ? UserProfileScope.Medic : UserProfileScope.Standard;
        return DeviceScope.HasFlag(deviceFlag) && ProfileScope.HasFlag(profileFlag);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
