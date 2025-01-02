using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutoQAC.Core.Models;
using AutoQAC.Core.Progress;
using AutoQAC.Core.Settings;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace AutoQAC.Core.Services;

/// <summary>
/// Handles logging operations for PACT
/// </summary>
public class LoggingService
{
    private readonly string _journalPath;
    private const string JournalFileName = "PACT Journal.log";

    public LoggingService()
    {
        _journalPath = Path.Combine(AppContext.BaseDirectory, JournalFileName);
    }

    public async Task LogUpdateAsync(string message)
    {
        await File.AppendAllTextAsync(_journalPath, message);
    }

    public void ExpireJournal(int expirationDays)
    {
        if (File.Exists(_journalPath))
        {
            var lastWrite = File.GetLastWriteTime(_journalPath);
            var age = DateTime.Now - lastWrite;
            if (age.Days > expirationDays)
            {
                File.Delete(_journalPath);
            }
        }
    }
}

/// <summary>
/// Handles XEdit operations with single instance enforcement
/// </summary>
public class XEditService : IAsyncDisposable
{
    private readonly PactInfo _info;
    private readonly IProgressReporter _progressReporter;
    private static readonly SemaphoreSlim XEditLock = new(1, 1);
    private static readonly object ProcessLock = new();
    private Process? _currentXEditProcess;

    public XEditService(PactInfo info, IProgressReporter progressReporter)
    {
        _info = info;
        _progressReporter = progressReporter;
    }

    public bool IsXEditRunning()
    {
        lock (ProcessLock)
        {
            if (_currentXEditProcess != null && !_currentXEditProcess.HasExited)
                return true;

            var xEditProcesses = Process.GetProcesses()
                .Where(p => _info.IsXEdit(p.ProcessName.ToLower()))
                .ToList();

            return xEditProcesses.Any();
        }
    }

    public async Task EnsureNoXEditRunning()
    {
        var processes = Process.GetProcesses()
            .Where(p => _info.IsXEdit(p.ProcessName.ToLower()));

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    await Task.Delay(1000); // Give process time to shut down
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not terminate xEdit process: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public Task ClearXEditLogs()
    {
        try
        {
            if (File.Exists(_info.XEditLogTxt))
                File.Delete(_info.XEditLogTxt);
            if (File.Exists(_info.XEditExcLog))
                File.Delete(_info.XEditExcLog);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Console.WriteLine("❌ ERROR : CANNOT CLEAR XEDIT LOGS. Try running PACT in Admin Mode.");
            Console.WriteLine("   If problems continue, please report this to the PACT GitHub repository.");
            throw new InvalidOperationException("Failed to clear XEdit logs", ex);
        }
    }

    public string CreateXEditCommand(string pluginName, bool universal = false, string? gameMode = null)
    {
        var command = string.Empty;

        if (!universal)
        {
            command = $"\"{_info.XEditPath}\" -QAC -autoexit -autoload \"{pluginName}\"";
        }
        else if (gameMode != null)
        {
            command = $"\"{_info.XEditPath}\" -{gameMode} -QAC -autoexit -autoload \"{pluginName}\"";
        }

        if (string.IsNullOrEmpty(command))
        {
            throw new InvalidOperationException("Unable to create XEdit command");
        }

        return command;
    }

    public async Task<Process> StartXEditProcess(string command, CancellationToken cancellationToken)
    {
        await XEditLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureNoXEditRunning();

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C {command}",
                UseShellExecute = true,
                CreateNoWindow = false,
            };

            lock (ProcessLock)
            {
                _currentXEditProcess = Process.Start(startInfo);
                if (_currentXEditProcess == null)
                    throw new InvalidOperationException("Failed to start XEdit process");
                return _currentXEditProcess;
            }
        }
        catch
        {
            XEditLock.Release();
            throw;
        }
    }

    public async Task MonitorXEditProcess(Process process, string pluginName, CancellationToken cancellationToken)
    {
        try
        {
            while (!process.HasExited && !cancellationToken.IsCancellationRequested)
            {
                if (await CheckLowCpuUsage(process))
                {
                    await HandleError(process, pluginName, "❌ ERROR : PLUGIN IS DISABLED OR HAS MISSING REQUIREMENTS!");
                    break;
                }

                if (await CheckTimeout(process))
                {
                    await HandleError(process, pluginName, "❌ ERROR : XEDIT TIMED OUT!", false);
                    break;
                }

                if (await CheckExceptions())
                {
                    await HandleError(process, pluginName, "❌ ERROR : PLUGIN IS EMPTY OR HAS MISSING REQUIREMENTS!");
                    break;
                }

                await Task.Delay(3000, cancellationToken);
            }
        }
        finally
        {
            XEditLock.Release();
            lock (ProcessLock)
            {
                _currentXEditProcess = null;
            }
        }
    }

    private async Task<bool> CheckLowCpuUsage(Process process)
    {
        try
        {
            await Task.Delay(5000);
            return await Task.Run(() => process.TotalProcessorTime.TotalMilliseconds < 10);
        }
        catch
        {
            return false;
        }
    }

    private Task<bool> CheckTimeout(Process process)
    {
        try
        {
            var runTime = DateTime.Now - process.StartTime;
            return Task.FromResult(runTime.TotalSeconds > _info.CleaningTimeout);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private async Task<bool> CheckExceptions()
    {
        if (File.Exists(_info.XEditExcLog))
        {
            var content = await File.ReadAllTextAsync(_info.XEditExcLog);
            return content.Contains("which can not be found") || content.Contains("which it does not have");
        }
        return false;
    }

    private async Task HandleError(Process process, string pluginName, string errorMessage, bool addToIgnore = true)
    {
        try
        {
            process.Kill();
            await Task.Delay(1000);
            await ClearXEditLogs();
            _info.PluginsProcessed--;
            _info.CleanFailedList.Add(pluginName);
            Console.WriteLine(errorMessage);

            if (addToIgnore)
            {
                // Add to ignore list logic here
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling XEdit process error: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await EnsureNoXEditRunning();
        XEditLock.Dispose();
    }
}

/// <summary>
/// Main cleaning service that orchestrates the plugin cleaning process
/// </summary>
public class CleaningService : IAsyncDisposable
{
    private readonly PactInfo _info;
    private readonly IProgressReporter _progressReporter;
    private readonly XEditService _xEditService;
    private readonly LoggingService _loggingService;
    private static readonly Regex PluginsPattern = new(@"(?:.+?)(?:\.(?:esl|esm|esp)+)$", RegexOptions.IgnoreCase);

    public CleaningService(
        PactInfo info,
        IProgressReporter progressReporter,
        XEditService xEditService,
        LoggingService loggingService)
    {
        _info = info;
        _progressReporter = progressReporter;
        _xEditService = xEditService;
        _loggingService = loggingService;
    }

    public async Task CleanPluginsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // First check if xEdit is already running
            if (_xEditService.IsXEditRunning())
            {
                Console.WriteLine("❌ ERROR: xEdit is already running. Please close all instances of xEdit before starting the cleaning process.");
                return;
            }

            using var scope = new ProgressScope(_progressReporter);

            var pluginList = await GetPluginListAsync();
            var skipList = await GetSkipListAsync();
            var pluginsToClean = pluginList.Except(skipList).ToList();

            _progressReporter.ReportMaxValue(pluginsToClean.Count);
            _progressReporter.SetVisible(true);

            Console.WriteLine($"✔️ CLEANING STARTED... ( PLUGINS TO CLEAN: {pluginsToClean.Count} )");
            var startTime = DateTime.Now;
            await LogStartCleaningAsync(startTime);

            var cleanedCount = 0;
            foreach (var plugin in pluginsToClean)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("\n❌ Cleaning process cancelled by user.");
                    break;
                }

                await CleanPluginAsync(plugin, cancellationToken);
                cleanedCount++;
                _progressReporter.ReportProgress(cleanedCount);
                Console.WriteLine($"Progress: {cleanedCount}/{pluginsToClean.Count} plugins processed");
            }

            await LogCompletionAsync(startTime);
            await OutputCleaningResultsAsync();
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n❌ Cleaning process cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during cleaning process: {ex.Message}");
            throw;
        }
    }

    private async Task CleanPluginAsync(string plugin, CancellationToken cancellationToken)
    {
        try
        {
            _progressReporter.ReportPlugin(plugin);
            Console.WriteLine($"\nCURRENTLY CLEANING : {plugin}");

            var command = _xEditService.CreateXEditCommand(plugin);
            await _xEditService.ClearXEditLogs();

            // Start xEdit process with exclusive access
            using var process = await _xEditService.StartXEditProcess(command, cancellationToken);

            // Monitor the process until completion
            await _xEditService.MonitorXEditProcess(process, plugin, cancellationToken);

            // Wait a moment for logs to be written
            await Task.Delay(1000, cancellationToken);

            // Check cleaning results and update statistics
            await CheckCleaningResultsAsync(plugin);

            _info.PluginsProcessed++;
            Console.WriteLine($"Finished cleaning: {plugin}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error cleaning plugin {plugin}: {ex.Message}");
            _info.CleanFailedList.Add(plugin);
            await _loggingService.LogUpdateAsync($"\n{plugin} -> Cleaning failed: {ex.Message}");
        }
    }

    private async Task<List<string>> GetPluginListAsync()
    {
        var content = await File.ReadAllLinesAsync(_info.LoadOrderPath);
        return content.Skip(1) // Skip first line
                     .Where(line => !line.Contains(".ghost") && !string.IsNullOrWhiteSpace(line))
                     .Select(line => line.Replace("*", "").Trim())
                     .ToList();
    }

    private Task<HashSet<string>> GetSkipListAsync()
    {
        var skipList = new HashSet<string>(_info.VipSkipList);
        skipList.UnionWith(_info.LclSkipList);
        return Task.FromResult(skipList);
    }

    private async Task CheckCleaningResultsAsync(string plugin)
    {
        if (!File.Exists(_info.XEditLogTxt))
            return;

        var cleanedSomething = false;
        var logLines = await File.ReadAllLinesAsync(_info.XEditLogTxt);

        foreach (var line in logLines)
        {
            if (line.Contains("Undeleting:"))
            {
                await _loggingService.LogUpdateAsync($"\n{plugin} -> Cleaned UDRs");
                _info.CleanResultsUDR.Add(plugin);
                cleanedSomething = true;
            }
            else if (line.Contains("Removing:"))
            {
                await _loggingService.LogUpdateAsync($"\n{plugin} -> Cleaned ITMs");
                _info.CleanResultsITM.Add(plugin);
                cleanedSomething = true;
            }
            else if (line.Contains("Skipping:"))
            {
                await _loggingService.LogUpdateAsync($"\n{plugin} -> Found Deleted Navmeshes");
                _info.CleanResultsNVM.Add(plugin);
                cleanedSomething = true;
            }
            else if (line.Contains("Making Partial Form:"))
            {
                await _loggingService.LogUpdateAsync($"\n{plugin} -> Created Partial Forms");
                _info.CleanResultsPartialForms.Add(plugin);
                cleanedSomething = true;
            }
        }

        if (cleanedSomething)
        {
            _info.PluginsCleaned++;
        }
        else
        {
            await _loggingService.LogUpdateAsync($"\n{plugin} -> NOTHING TO CLEAN");
            _info.LclSkipList.Add(plugin);
            Console.WriteLine("NOTHING TO CLEAN! Adding plugin to PACT Ignore file...");
        }
    }

    private async Task LogStartCleaningAsync(DateTime startTime)
    {
        await Task.Run(() => _loggingService.ExpireJournal(_info.JournalExpiration));
        await _loggingService.LogUpdateAsync($"\nSTARTED CLEANING PROCESS AT : {startTime}");
    }

    private async Task LogCompletionAsync(DateTime startTime)
    {
        var duration = DateTime.Now - startTime;
        var message = $"\n✔️ CLEANING COMPLETE! {_info.XEditExecutable} processed all available plugins in {duration.TotalSeconds:F1} seconds." +
                     $"\n   Processed {_info.PluginsProcessed} plugins and cleaned {_info.PluginsCleaned} of them.";

        await _loggingService.LogUpdateAsync(message);
        Console.WriteLine(message);
    }

    private async Task OutputCleaningResultsAsync()
    {
        var results = new[]
        {
            (_info.CleanFailedList, "❌ {0} WAS UNABLE TO CLEAN THESE PLUGINS:"),
            (_info.CleanResultsUDR, "✔️ The following plugins had Undisabled Records and {0} properly disabled them:"),
            (_info.CleanResultsITM, "✔️ The following plugins had Identical To Master Records and {0} successfully cleaned them:"),
            (_info.CleanResultsNVM, "❌ CAUTION: The following plugins contain Deleted Navmeshes!\n   Such plugins may cause navmesh related problems or crashes."),
            (_info.CleanResultsPartialForms, "✔️ The following plugins had ITMs converted to Partial Forms {0}:")
        };

        foreach (var (resultSet, messageTemplate) in results)
        {
            if (resultSet.Count > 0)
            {
                var message = string.Format(messageTemplate, _info.XEditExecutable);
                Console.WriteLine($"\n{message}");
                foreach (var plugin in resultSet)
                {
                    Console.WriteLine(plugin);
                    await _loggingService.LogUpdateAsync($"\n{plugin}");
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _xEditService.DisposeAsync();
    }
}
/// <summary>
/// Service responsible for checking updates from GitHub releases
/// </summary>
public class UpdateService : IAsyncDisposable
{
    private readonly SettingsManager _settingsManager;
    private readonly HttpClient _httpClient;
    private const string GithubApiUrl = "https://api.github.com/repos/evildarkarchon/XEdit-PACT/releases/latest";

    public UpdateService(SettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "PACT-Update-Checker");
    }

    /// <summary>
    /// Checks for updates against the GitHub repository
    /// </summary>
    /// <returns>True if up to date, false otherwise</returns>
    public async Task<bool> CheckForUpdatesAsync()
    {
        var updateCheckEnabled = _settingsManager.GetSetting<bool>("Update Check");
        if (!updateCheckEnabled)
        {
            Console.WriteLine("\n❌ NOTICE: UPDATE CHECK IS DISABLED IN PACT SETTINGS\n");
            Console.WriteLine("===============================================================================");
            return false;
        }

        Console.WriteLine("❓ CHECKING FOR ANY NEW PLUGIN AUTO CLEANING TOOL (PACT) UPDATES...");
        Console.WriteLine("   (You can disable this check in PACT Settings.yaml)\n");

        try
        {
            var response = await _httpClient.GetFromJsonAsync<GitHubRelease>(GithubApiUrl);
            if (response == null)
            {
                throw new InvalidOperationException("Invalid response from GitHub API");
            }

            var currentVersion = GetCurrentVersion();
            if (response.Name == currentVersion)
            {
                Console.WriteLine("\n✔️ You have the latest version of PACT!");
                return true;
            }

            var outdatedWarning = GetOutdatedWarning();
            Console.WriteLine(outdatedWarning);
            Console.WriteLine("===============================================================================");
            return false;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Failed to check for updates: {ex.Message}");
            var updateFailedWarning = GetUpdateFailedWarning();
            Console.WriteLine(updateFailedWarning);
            Console.WriteLine("===============================================================================");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error during update check: {ex.Message}");
            Console.WriteLine("===============================================================================");
            return false;
        }
    }

    private string GetCurrentVersion()
    {
        try
        {
            return _settingsManager.GetSetting<string>("PACT_Data.version")
                   ?? throw new InvalidOperationException("Current version not found in settings");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error retrieving current version: {ex.Message}");
            throw;
        }
    }

    private string GetOutdatedWarning()
    {
        return _settingsManager.GetSetting<string>("PACT_Data.Warnings.Outdated_PACT")
               ?? "Warning: Your version of PACT is outdated. Please check for updates.";
    }

    private string GetUpdateFailedWarning()
    {
        return _settingsManager.GetSetting<string>("PACT_Data.Warnings.PACT_Update_Failed")
               ?? "Warning: Failed to check for PACT updates. Please check your internet connection.";
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Represents the GitHub release response structure
/// </summary>
internal class GitHubRelease
{
    public string Name { get; set; } = string.Empty;
}