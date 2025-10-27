using System;

namespace SoftwareSetupApp.Models
{
    public sealed class SetupAutomationContext
    {
        private readonly Action<string> _logger;

        public SetupAutomationContext(DeviceType deviceType, UserProfile userProfile, Action<string> logger)
        {
            DeviceType = deviceType;
            UserProfile = userProfile;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public DeviceType DeviceType { get; }

        public UserProfile UserProfile { get; }

        public void Report(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            _logger(message);
        }
    }
}
