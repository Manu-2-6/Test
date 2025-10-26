using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SoftwareSetupApp.Models;

namespace SoftwareSetupApp.Services;

public class WingetInstaller
{
    private static readonly Regex PercentageRegex = new("(\\d{1,3})%", RegexOptions.Compiled);

    public async Task<InstallationResult> InstallAsync(
        SoftwarePackage package,
        IProgress<string> progress,
        IProgress<int>? percentProgress,
        CancellationToken cancellationToken)
    {
        var arguments = $"install --id \"{package.PackageId}\" --silent --accept-package-agreements --accept-source-agreements";

        var startInfo = new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            var outputCompletion = new TaskCompletionSource<object?>();
            var errorCompletion = new TaskCompletionSource<object?>();

            percentProgress?.Report(0);

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    outputCompletion.TrySetResult(null);
                }
                else
                {
                    outputBuilder.AppendLine(args.Data);
                    progress.Report($"[{package.Name}] {args.Data}");
                    TryReportPercentage(args.Data, percentProgress);
                }
            };

            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    errorCompletion.TrySetResult(null);
                }
                else
                {
                    errorBuilder.AppendLine(args.Data);
                    progress.Report($"[{package.Name}] {args.Data}");
                    TryReportPercentage(args.Data, percentProgress);
                }
            };

            if (!process.Start())
            {
                return new InstallationResult(false, $"[{package.Name}] Impossible de démarrer winget.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputCompletion.Task, errorCompletion.Task).ConfigureAwait(false);

            var output = outputBuilder.ToString().Trim();
            var error = errorBuilder.ToString().Trim();
            var message = string.IsNullOrWhiteSpace(error) ? output : error;

            var isSuccess = process.ExitCode == 0;

            if (isSuccess)
            {
                percentProgress?.Report(100);
            }

            return isSuccess
                ? new InstallationResult(true, message)
                : new InstallationResult(false, message);
        }
        catch (Win32Exception)
        {
            return new InstallationResult(false, "winget est introuvable sur ce poste.");
        }
    }

    private static void TryReportPercentage(string data, IProgress<int>? percentProgress)
    {
        if (percentProgress == null || string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        var match = PercentageRegex.Match(data);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var value))
        {
            var clamped = Math.Max(0, Math.Min(100, value));
            percentProgress.Report(clamped);
        }
        else if (data.Contains("terminé", StringComparison.OrdinalIgnoreCase) ||
                 data.Contains("installed", StringComparison.OrdinalIgnoreCase) ||
                 data.Contains("success", StringComparison.OrdinalIgnoreCase))
        {
            percentProgress.Report(100);
        }
    }
}
