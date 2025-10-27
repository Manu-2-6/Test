using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SoftwareSetupApp.Models
{
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
        private bool _isSelected = true;
        private bool _isCompleted;
        private bool _isApplicable = true;
        private bool _autoDeselected;

        public SetupTask(
            string title,
            string details,
            DeviceTypeScope deviceScope,
            UserProfileScope profileScope,
            Func<SetupAutomationContext, CancellationToken, Task<AutomationResult>>? automationAction = null)
        {
            Title = title;
            Details = details;
            DeviceScope = deviceScope;
            ProfileScope = profileScope;
            AutomationAction = automationAction;
        }

        public string Title { get; }

        public string Details { get; }

        public DeviceTypeScope DeviceScope { get; }

        public UserProfileScope ProfileScope { get; }

        public Func<SetupAutomationContext, CancellationToken, Task<AutomationResult>>? AutomationAction { get; }

        public bool HasAutomation => AutomationAction != null;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    _autoDeselected = false;
                    OnPropertyChanged();

                    if (!value)
                    {
                        IsCompleted = false;
                    }
                }
            }
        }

        public bool IsCompleted
        {
            get => _isCompleted;
            set
            {
                if (_isCompleted != value)
                {
                    _isCompleted = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public bool IsApplicable
        {
            get => _isApplicable;
            private set
            {
                if (_isApplicable != value)
                {
                    _isApplicable = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public string StatusText
        {
            get
            {
                if (!IsApplicable)
                {
                    return "Non applicable";
                }

                return IsCompleted ? "Terminé" : "À exécuter";
            }
        }

        public bool AppliesTo(DeviceType deviceType, UserProfile profile)
        {
            var deviceFlag = deviceType == DeviceType.Desktop ? DeviceTypeScope.Desktop : DeviceTypeScope.Laptop;
            var profileFlag = profile == UserProfile.Medic ? UserProfileScope.Medic : UserProfileScope.Standard;
            return DeviceScope.HasFlag(deviceFlag) && ProfileScope.HasFlag(profileFlag);
        }

        public void UpdateApplicability(DeviceType deviceType, UserProfile profile)
        {
            var applicable = AppliesTo(deviceType, profile);

            if (!applicable)
            {
                if (_isSelected)
                {
                    _isSelected = false;
                    _autoDeselected = true;
                    OnPropertyChanged(nameof(IsSelected));
                }

                if (_isCompleted)
                {
                    _isCompleted = false;
                    OnPropertyChanged(nameof(IsCompleted));
                }
            }
            else if (_autoDeselected && !_isSelected)
            {
                _autoDeselected = false;
                _isSelected = true;
                OnPropertyChanged(nameof(IsSelected));
            }

            IsApplicable = applicable;
        }

        public Task<AutomationResult> ExecuteAsync(SetupAutomationContext context, CancellationToken cancellationToken)
        {
            if (AutomationAction == null)
            {
                return Task.FromResult(AutomationResult.Success());
            }

            return AutomationAction(context, cancellationToken);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
