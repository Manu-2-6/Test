using System;

namespace SoftwareSetupApp.Models
{
    public sealed class AutomationResult
    {
        private AutomationResult(bool isSuccess, string? message)
        {
            IsSuccess = isSuccess;
            Message = message;
        }

        public bool IsSuccess { get; }

        public string? Message { get; }

        public static AutomationResult Success(string? message = null) => new(true, message);

        public static AutomationResult Failure(string? message = null) => new(false, message);

        public static AutomationResult FromException(Exception exception)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            return Failure(exception.Message);
        }
    }
}
