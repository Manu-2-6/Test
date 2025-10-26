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
            cancellationToken.ThrowIfCancellationRequested();

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            var outputCompletion = new TaskCompletionSource<object?>();
            var errorCompletion = new TaskCompletionSource<object?>();

            percentProgress?.Report(0);
            var lastReportedPercent = 0;

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    outputCompletion.TrySetResult(null);
                    return;
                }

                outputBuilder.AppendLine(args.Data);
                progress.Report($"[{package.Name}] {args.Data}");
                TryReportPercentage(args.Data, percentProgress, ref lastReportedPercent);
            };

            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    errorCompletion.TrySetResult(null);
                    return;
                }

                errorBuilder.AppendLine(args.Data);
                progress.Report($"[{package.Name}] {args.Data}");
                TryReportPercentage(args.Data, percentProgress, ref lastReportedPercent);
            };

            if (!process.Start())
            {
                return new InstallationResult(false, false, $"[{package.Name}] Impossible de démarrer winget.");
            }

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch
                {
                    // Ignored
                }
            });

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(outputCompletion.Task, errorCompletion.Task).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new InstallationResult(false, true, $"[{package.Name}] Installation annulée.");
            }

            var output = outputBuilder.ToString().Trim();
            var error = errorBuilder.ToString().Trim();
            var message = string.IsNullOrWhiteSpace(error) ? output : error;

            var isSuccess = process.ExitCode == 0;

            if (isSuccess)
            {
                percentProgress?.Report(100);
            }

            return new InstallationResult(isSuccess, false, isSuccess ? string.Empty : message);
        }
        catch (Win32Exception)
        {
            return new InstallationResult(false, false, "winget est introuvable sur ce poste.");
        }
    }

    private static void TryReportPercentage(string data, IProgress<int>? percentProgress, ref int lastReportedPercent)
    {
        if (percentProgress == null || string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        var match = PercentageRegex.Match(data);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var value))
        {
            return;
        }

        var clamped = Math.Max(0, Math.Min(100, value));
        if (clamped >= 100)
        {
            clamped = 99;
        }

        if (clamped <= lastReportedPercent)
        {
            return;
        }

        lastReportedPercent = clamped;
        percentProgress.Report(clamped);
    }
}
