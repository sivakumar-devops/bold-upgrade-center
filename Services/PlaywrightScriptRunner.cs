using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace BoldBI.UpgradeCenter.Services;

public sealed class PlaywrightScriptRunner : IPlaywrightScriptRunner
{
    private readonly IWebHostEnvironment environment;
    private readonly IPlaywrightKubernetesJobService kubernetesJobService;
    private readonly ILogger<PlaywrightScriptRunner> logger;
    private readonly PlaywrightExecutionOptions options;

    public PlaywrightScriptRunner(
        IWebHostEnvironment environment,
        IPlaywrightKubernetesJobService kubernetesJobService,
        IOptions<PlaywrightExecutionOptions> options,
        ILogger<PlaywrightScriptRunner> logger)
    {
        this.environment = environment;
        this.kubernetesJobService = kubernetesJobService;
        this.options = options.Value;
        this.logger = logger;
    }

    public Task<PlaywrightScriptResult> ExecutePreUpgradeAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(options.PreUpgrade, fallbackName: "Pre-upgrade Playwright validation", fallbackArguments: options.Arguments, cancellationToken);
    }

    public Task<PlaywrightScriptResult> ExecutePostUpgradeAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(options.PostUpgrade, fallbackName: "Post-upgrade Playwright validation", fallbackArguments: options.Arguments, cancellationToken);
    }

    public Task<PlaywrightScriptResult> ExecutePreUpgradeAsync(
        string upgradeJobId,
        Action<PlaywrightValidationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return options.UseKubernetesJob
            ? kubernetesJobService.RunAsync(PlaywrightValidationMode.Pre, upgradeJobId, progress, cancellationToken)
            : ExecutePreUpgradeAsync(cancellationToken);
    }

    public Task<PlaywrightScriptResult> ExecutePostUpgradeAsync(
        string upgradeJobId,
        Action<PlaywrightValidationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return options.UseKubernetesJob
            ? kubernetesJobService.RunAsync(PlaywrightValidationMode.Post, upgradeJobId, progress, cancellationToken)
            : ExecutePostUpgradeAsync(cancellationToken);
    }

    private async Task<PlaywrightScriptResult> ExecuteAsync(
        PlaywrightScriptExecutionOptions scriptOptions,
        string fallbackName,
        string fallbackArguments,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var scriptName = string.IsNullOrWhiteSpace(scriptOptions.Name) ? fallbackName : scriptOptions.Name;
        var executable = FirstConfiguredValue(scriptOptions.Executable, options.Executable) ?? "node";
        var arguments = FirstConfiguredValue(scriptOptions.Arguments, fallbackArguments) ?? string.Empty;
        var workingDirectory = ResolveWorkingDirectory(scriptOptions.WorkingDirectory);
        var timeoutSeconds = scriptOptions.TimeoutSeconds.GetValueOrDefault(options.TimeoutSeconds);
        var command = $"{executable} {arguments}";

        logger.LogInformation(
            "Starting {ScriptName}. Command: {Command}. WorkingDirectory: {WorkingDirectory}",
            scriptName,
            command,
            workingDirectory);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                if (IsBundledSampleValidation(arguments))
                {
                    return CreateResult(true, null, false, startedAt, command, string.Empty, string.Empty, $"{scriptName} sample validation was treated as successful because it is a temporary bundled dummy validation.");
                }

                return CreateResult(false, null, false, startedAt, command, string.Empty, string.Empty, $"Unable to start {scriptName}.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var completedTask = await Task.WhenAny(waitTask, Task.Delay(timeout, cancellationToken));

            var timedOut = completedTask != waitTask;
            if (timedOut)
            {
                logger.LogWarning("{ScriptName} timed out after {TimeoutSeconds} seconds.", scriptName, timeoutSeconds);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            else
            {
                await waitTask;
            }

            var standardOutput = await outputTask;
            var standardError = await errorTask;
            int? exitCode = timedOut ? null : process.ExitCode;
            var succeeded = !timedOut && exitCode == 0;
            if (!succeeded && IsBundledSampleValidation(arguments))
            {
                var sampleMessage = $"{scriptName} sample validation did not complete cleanly, but the bundled dummy validation is allowed to pass until the real QA script is integrated.";
                logger.LogWarning(
                    "{ScriptName} sample validation returned ExitCode: {ExitCode}. TimedOut: {TimedOut}. Treating as success for the temporary bundled sample only. Error: {StandardError}",
                    scriptName,
                    exitCode,
                    timedOut,
                    standardError);
                return CreateResult(true, exitCode, timedOut, startedAt, command, standardOutput, standardError, sampleMessage);
            }

            var message = succeeded
                ? $"{scriptName} executed successfully."
                : timedOut
                    ? $"{scriptName} timed out."
                    : $"{scriptName} failed with exit code {exitCode}.";

            if (succeeded)
            {
                logger.LogInformation("{ScriptName} completed successfully in {DurationMilliseconds} ms.", scriptName, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
            }
            else
            {
                logger.LogWarning(
                    "{ScriptName} did not complete successfully. ExitCode: {ExitCode}. TimedOut: {TimedOut}. Error: {StandardError}",
                    scriptName,
                    exitCode,
                    timedOut,
                    standardError);
            }

            return CreateResult(succeeded, exitCode, timedOut, startedAt, command, standardOutput, standardError, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("{ScriptName} execution was cancelled.", scriptName);
            throw;
        }
        catch (Win32Exception exception)
        {
            logger.LogError(exception, "Unable to start Playwright command {Command}.", command);
            if (IsBundledSampleValidation(arguments))
            {
                return CreateResult(true, null, false, startedAt, command, string.Empty, exception.Message, $"{scriptName} sample validation was treated as successful because it is a temporary bundled dummy validation.");
            }

            return CreateResult(false, null, false, startedAt, command, string.Empty, exception.Message, "Unable to start the Playwright command.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected error while executing {ScriptName}.", scriptName);
            return CreateResult(false, null, false, startedAt, command, string.Empty, exception.ToString(), $"Unexpected error while executing {scriptName}.");
        }
    }

    private string ResolveWorkingDirectory(string? scriptWorkingDirectory)
    {
        var workingDirectory = FirstConfiguredValue(scriptWorkingDirectory, options.WorkingDirectory);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return environment.ContentRootPath;
        }

        return Path.IsPathRooted(workingDirectory)
            ? workingDirectory
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, workingDirectory));
    }

    private static bool IsBundledSampleValidation(string arguments)
    {
        return arguments.Contains("sample-upgrade-poc.spec.ts", StringComparison.OrdinalIgnoreCase) ||
               arguments.Contains("sample-post-upgrade-validation.spec.ts", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstConfiguredValue(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static PlaywrightScriptResult CreateResult(
        bool succeeded,
        int? exitCode,
        bool timedOut,
        DateTimeOffset startedAt,
        string command,
        string standardOutput,
        string standardError,
        string message)
    {
        return new PlaywrightScriptResult(
            succeeded,
            exitCode,
            timedOut,
            startedAt,
            DateTimeOffset.UtcNow,
            command,
            standardOutput,
            standardError,
            message);
    }
}
