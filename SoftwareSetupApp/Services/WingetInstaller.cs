using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SoftwareSetupApp.Models;

namespace SoftwareSetupApp.Services
{
    public class WingetInstaller
    {
        private static readonly Regex PercentageRegex = new("(\\d{1,3})%", RegexOptions.Compiled);
        private static readonly Regex DownloadSizeRegex = new("(\\d+(?:\\.\\d+)?)\\s*(B|KB|MB|GB|TB)\\s*/\\s*(\\d+(?:\\.\\d+)?)\\s*(B|KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly (Regex Pattern, int Target)[] StageTargets =
        {
            (new Regex("\\b(acquiring|processing|resolving)\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 10),
            (new Regex("\\b(verification|hash|signature)\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 20),
            (new Regex("\\bdownload(ing|ed)?\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 45),
            (new Regex("\\binstall(ing|ed)?\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 85),
            (new Regex("\\bsuccessfully installed\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 95)
        };

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
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
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
                    TryReportDownloadSize(args.Data, percentProgress, ref lastReportedPercent);
                    TryReportStage(args.Data, percentProgress, ref lastReportedPercent);
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
                    TryReportDownloadSize(args.Data, percentProgress, ref lastReportedPercent);
                    TryReportStage(args.Data, percentProgress, ref lastReportedPercent);
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

            ReportPercent(Math.Max(0, Math.Min(100, value)), percentProgress, ref lastReportedPercent);
        }

        private static void TryReportDownloadSize(string data, IProgress<int>? percentProgress, ref int lastReportedPercent)
        {
            if (percentProgress == null || string.IsNullOrWhiteSpace(data))
            {
                return;
            }

            var match = DownloadSizeRegex.Match(data);
            if (!match.Success)
            {
                return;
            }

            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var current) ||
                !double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
            {
                return;
            }

            var currentBytes = ConvertToBytes(current, match.Groups[2].Value);
            var totalBytes = ConvertToBytes(total, match.Groups[4].Value);
            if (totalBytes <= 0)
            {
                return;
            }

            var ratio = currentBytes / totalBytes;
            if (ratio <= 0)
            {
                return;
            }

            var percent = (int)Math.Round(Math.Min(0.99, ratio) * 100);
            ReportPercent(percent, percentProgress, ref lastReportedPercent);
        }

        private static void TryReportStage(string data, IProgress<int>? percentProgress, ref int lastReportedPercent)
        {
            if (percentProgress == null || string.IsNullOrWhiteSpace(data))
            {
                return;
            }

            foreach (var (pattern, target) in StageTargets)
            {
                if (!pattern.IsMatch(data))
                {
                    continue;
                }

                ReportPercent(target, percentProgress, ref lastReportedPercent);
                break;
            }
        }

        private static void ReportPercent(int value, IProgress<int>? percentProgress, ref int lastReportedPercent)
        {
            if (percentProgress == null)
            {
                return;
            }

            var clamped = Math.Max(0, Math.Min(99, value));
            if (clamped <= lastReportedPercent)
            {
                return;
            }

            lastReportedPercent = clamped;
            percentProgress.Report(clamped);
        }

        private static double ConvertToBytes(double value, string unit)
        {
            var normalized = unit.Trim().ToUpperInvariant();
            return normalized switch
            {
                "TB" => value * Math.Pow(1024d, 4),
                "GB" => value * Math.Pow(1024d, 3),
                "MB" => value * Math.Pow(1024d, 2),
                "KB" => value * 1024d,
                _ => value
            };
        }
    }
}
