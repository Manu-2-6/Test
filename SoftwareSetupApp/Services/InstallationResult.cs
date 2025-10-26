namespace SoftwareSetupApp.Services;

public record InstallationResult(bool IsSuccess, bool IsCanceled, string Message);
