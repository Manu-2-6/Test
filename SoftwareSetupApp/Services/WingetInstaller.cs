using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SoftwareSetupApp.Models;

namespace SoftwareSetupApp.Services;

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

    private static readonly SemaphoreSlim SourceInitializationLock = new(1, 1);
    private static readonly HashSet<string> InitializedSources = new(StringComparer.OrdinalIgnoreCase);

    public async Task<InstallationResult> InstallAsync(
        SoftwarePackage package,
        IProgress<string> progress,
        IProgress<int>? percentProgress,
        CancellationToken cancellationToken)
    {
        var attempts = new List<(string Arguments, string Description)>
        {
            (BuildArguments("--id", package.PackageId, includeExact: false), $"l'identifiant \"{package.PackageId}\"")
        };

        if (!string.IsNullOrWhiteSpace(package.WingetSearchQuery))
        {
            attempts.Add((BuildArguments("--name", package.WingetSearchQuery, includeExact: true), $"le nom \"{package.WingetSearchQuery}\""));
        }

        var failureMessages = new List<string>();

        if (!string.IsNullOrWhiteSpace(package.Source))
        {
            await EnsureSourceAvailableAsync(package.Source).ConfigureAwait(false);
        }

        foreach (var (arguments, description) in attempts)
        {
            if (failureMessages.Count > 0)
            {
                progress.Report($"[{package.Name}] Nouvelle tentative via {description}.");
            }

            var result = await RunWingetInstallAsync(arguments).ConfigureAwait(false);

            if (ShouldStopRetrying(result))
            {
                return result;
            }

            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                failureMessages.Add(result.Message);
            }
        }

        var resolvedIdentifier = await TryResolveIdentifierAsync().ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(resolvedIdentifier) &&
            !string.Equals(resolvedIdentifier, package.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            progress.Report($"[{package.Name}] Nouvelle tentative via l'identifiant résolu \"{resolvedIdentifier}\".");

            var resolvedArguments = BuildArguments("--id", resolvedIdentifier, includeExact: false);
            var resolvedResult = await RunWingetInstallAsync(resolvedArguments).ConfigureAwait(false);

            if (ShouldStopRetrying(resolvedResult))
            {
                return resolvedResult;
            }

            if (!string.IsNullOrWhiteSpace(resolvedResult.Message))
            {
                failureMessages.Add(resolvedResult.Message);
            }
        }

        var aggregatedMessage = failureMessages.Count > 0
            ? string.Join(Environment.NewLine + Environment.NewLine, failureMessages.Distinct(StringComparer.Ordinal))
            : $"[{package.Name}] L'installation a échoué.";

        return new InstallationResult(false, false, aggregatedMessage);

        string BuildArguments(string option, string identifier, bool includeExact)
        {
            var builder = new StringBuilder();
            builder.Append("install ")
                .Append(option)
                .Append(' ')
                .Append('"')
                .Append(identifier)
                .Append('"');

            if (includeExact)
            {
                builder.Append(" --exact");
            }

            builder.Append(" --silent --accept-package-agreements --accept-source-agreements");

            if (!string.IsNullOrWhiteSpace(package.Source))
            {
                builder.Append(" --source \"")
                    .Append(package.Source)
                    .Append('"');
            }

            return builder.ToString();
        }

        string NormalizeCommandMessage(WingetCommandResult result)
        {
            var message = !string.IsNullOrWhiteSpace(result.Error) ? result.Error : result.Output;
            return NormalizeMessage(message);
        }

        async Task EnsureSourceAvailableAsync(string source)
        {
            await SourceInitializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (InitializedSources.Contains(source))
                {
                    return;
                }

                progress.Report($"[{package.Name}] Vérification de la source winget \"{source}\"...");

                var updateArgs = new StringBuilder()
                    .Append("source update --name \"")
                    .Append(source)
                    .Append("\" --accept-source-agreements --disable-interactivity")
                    .ToString();

                var updateResult = await ExecuteWingetCommandAsync(updateArgs, forwardOutput: false).ConfigureAwait(false);

                if (updateResult.IsCanceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (updateResult.IsSuccess)
                {
                    InitializedSources.Add(source);
                    return;
                }

                var updateMessage = NormalizeCommandMessage(updateResult);
                if (!string.IsNullOrWhiteSpace(updateMessage))
                {
                    progress.Report(updateMessage);
                }

                progress.Report($"[{package.Name}] Activation de la source winget \"{source}\"...");

                var enableArgs = new StringBuilder()
                    .Append("source enable --name \"")
                    .Append(source)
                    .Append("\" --accept-source-agreements --disable-interactivity")
                    .ToString();

                var enableResult = await ExecuteWingetCommandAsync(enableArgs, forwardOutput: false).ConfigureAwait(false);

                if (enableResult.IsCanceled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (enableResult.IsSuccess)
                {
                    InitializedSources.Add(source);
                    return;
                }

                var enableMessage = NormalizeCommandMessage(enableResult);
                if (!string.IsNullOrWhiteSpace(enableMessage))
                {
                    progress.Report(enableMessage);
                    failureMessages.Add(enableMessage);
                }
                else
                {
                    var fallback = $"[{package.Name}] Impossible de préparer la source winget \"{source}\".";
                    progress.Report(fallback);
                    failureMessages.Add(fallback);
                }
            }
            finally
            {
                SourceInitializationLock.Release();
            }
        }

        async Task<InstallationResult> RunWingetInstallAsync(string arguments)
        {
            var commandResult = await ExecuteWingetCommandAsync(arguments, forwardOutput: true).ConfigureAwait(false);

            if (commandResult.IsCanceled)
            {
                return new InstallationResult(false, true, $"[{package.Name}] Installation annulée.");
            }

            if (commandResult.IsSuccess)
            {
                return new InstallationResult(true, false, string.Empty);
            }

            var message = NormalizeCommandMessage(commandResult);

            return new InstallationResult(false, false, message);
        }

        async Task<string?> TryResolveIdentifierAsync()
        {
            if (string.IsNullOrWhiteSpace(package.WingetSearchQuery))
            {
                return null;
            }

            progress.Report($"[{package.Name}] Recherche d'un identifiant winget correspondant...");

            var argumentsBuilder = new StringBuilder();
            argumentsBuilder.Append("search --name \"")
                .Append(package.WingetSearchQuery)
                .Append("\"")
                .Append(" --accept-source-agreements --disable-interactivity --output json");

            if (!string.IsNullOrWhiteSpace(package.Source))
            {
                argumentsBuilder.Append(" --source \"")
                    .Append(package.Source)
                    .Append('"');
            }

            var searchResult = await ExecuteWingetCommandAsync(argumentsBuilder.ToString(), forwardOutput: false).ConfigureAwait(false);

            if (searchResult.IsCanceled)
            {
                return null;
            }

            if (!searchResult.IsSuccess)
            {
                var message = NormalizeCommandMessage(searchResult);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    progress.Report(message);
                    failureMessages.Add(message);
                }

                return null;
            }

            var identifier = ParseIdentifierFromSearchOutput(searchResult.Output);

            if (!string.IsNullOrWhiteSpace(identifier))
            {
                progress.Report($"[{package.Name}] Identifiant winget trouvé : \"{identifier}\".");
            }
            else
            {
                var message = $"[{package.Name}] Aucun paquet correspondant n'a été trouvé via la recherche winget.";
                progress.Report(message);
                failureMessages.Add(message);
            }

            return identifier;
        }

        string? ParseIdentifierFromSearchOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(output);

                if (!document.RootElement.TryGetProperty("Data", out var dataElement) ||
                    dataElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var sourcePreference = package.Source;
                var searchQuery = package.WingetSearchQuery;

                string? bestIdentifier = null;
                var bestScore = double.MinValue;

                foreach (var entry in dataElement.EnumerateArray())
                {
                    if (!TryGetString(entry, "PackageIdentifier", out var identifier) || string.IsNullOrWhiteSpace(identifier))
                    {
                        continue;
                    }

                    TryGetString(entry, "PackageName", out var name);
                    string? entrySource = null;

                    if (!TryGetString(entry, "SourceId", out entrySource) &&
                        !TryGetString(entry, "SourceIdentifier", out entrySource) &&
                        !TryGetString(entry, "PackageSource", out entrySource))
                    {
                        entrySource = null;
                    }

                    var score = 0d;

                    if (!string.IsNullOrWhiteSpace(sourcePreference) &&
                        string.Equals(entrySource, sourcePreference, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 2;
                    }

                    if (!string.IsNullOrWhiteSpace(searchQuery) && !string.IsNullOrWhiteSpace(name))
                    {
                        if (string.Equals(name, searchQuery, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 3;
                        }
                        else if (name.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            score += 1;
                        }
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIdentifier = identifier;
                    }
                }

                return bestIdentifier;
            }
            catch (JsonException)
            {
                var message = $"[{package.Name}] Analyse JSON invalide lors de la recherche winget.";
                progress.Report(message);
                failureMessages.Add(message);
                return null;
            }
        }

        static bool TryGetString(JsonElement element, string propertyName, out string? value)
        {
            if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return true;
            }

            value = null;
            return false;
        }

        string NormalizeMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            var trimmed = message.Trim();
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            return trimmed.StartsWith("[", StringComparison.Ordinal)
                ? trimmed
                : $"[{package.Name}] {trimmed}";
        }

        async Task<WingetCommandResult> ExecuteWingetCommandAsync(string arguments, bool forwardOutput)
        {
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

                if (forwardOutput)
                {
                    percentProgress?.Report(0);
                }

                var lastReportedPercent = 0;

                process.OutputDataReceived += (_, args) =>
                {
                    if (args.Data == null)
                    {
                        outputCompletion.TrySetResult(null);
                        return;
                    }

                    outputBuilder.AppendLine(args.Data);
                    if (forwardOutput)
                    {
                        progress.Report($"[{package.Name}] {args.Data}");
                        TryReportPercentage(args.Data, percentProgress, ref lastReportedPercent);
                        TryReportDownloadSize(args.Data, percentProgress, ref lastReportedPercent);
                        TryReportStage(args.Data, percentProgress, ref lastReportedPercent);
                    }
                };

                process.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data == null)
                    {
                        errorCompletion.TrySetResult(null);
                        return;
                    }

                    errorBuilder.AppendLine(args.Data);
                    if (forwardOutput)
                    {
                        progress.Report($"[{package.Name}] {args.Data}");
                        TryReportPercentage(args.Data, percentProgress, ref lastReportedPercent);
                        TryReportDownloadSize(args.Data, percentProgress, ref lastReportedPercent);
                        TryReportStage(args.Data, percentProgress, ref lastReportedPercent);
                    }
                };

                if (!process.Start())
                {
                    return new WingetCommandResult(false, false, string.Empty, $"[{package.Name}] Impossible de démarrer winget.");
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
                    return new WingetCommandResult(false, true, string.Empty, string.Empty);
                }

                var output = outputBuilder.ToString().Trim();
                var error = errorBuilder.ToString().Trim();
                var isSuccess = process.ExitCode == 0;

                return new WingetCommandResult(isSuccess, false, output, error);
            }
            catch (Win32Exception)
            {
                return new WingetCommandResult(false, false, string.Empty, "winget est introuvable sur ce poste.");
            }
        }
    }

    private static bool ShouldStopRetrying(InstallationResult result)
    {
        if (result.IsSuccess || result.IsCanceled)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(result.Message))
        {
            return false;
        }

        return result.Message.Contains("Impossible de démarrer winget", StringComparison.OrdinalIgnoreCase)
            || result.Message.Contains("winget est introuvable", StringComparison.OrdinalIgnoreCase);
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

    private sealed record WingetCommandResult(bool IsSuccess, bool IsCanceled, string Output, string Error);
}
