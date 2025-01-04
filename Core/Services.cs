using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;

// ReSharper disable MergeIntoPattern

namespace PACT.Core;

/// <summary>
/// Handles logging operations for PACT
/// </summary>
public class LoggingService
{
    private readonly string _journalPath = Path.Combine(AppContext.BaseDirectory, JournalFileName);
    private const string JournalFileName = "PACT Journal.log";

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
public class XEditService(PactInfo info) : IAsyncDisposable
{
    private static readonly SemaphoreSlim XEditLock = new(1, 1);
    private static readonly object ProcessLock = new();
    private Process? _currentXEditProcess;

public bool IsXEditRunning()
{
    lock (ProcessLock)
    {
        if (IsCurrentXEditProcessRunning())
            return true;

        return IsAnyXEditProcessRunning();
    }
}

/// <summary>
/// Checks if the current XEdit process is running and active.
/// </summary>
private bool IsCurrentXEditProcessRunning()
{
    return _currentXEditProcess != null && !_currentXEditProcess.HasExited;
}

/// <summary>
/// Checks if there are any other XEdit processes running on the system.
/// </summary>
private bool IsAnyXEditProcessRunning()
{
    return Process.GetProcesses()
        .Any(process => info.IsXEdit(process.ProcessName.ToLower()));
}

private async Task EnsureNoXEditRunning()
    {
        var processes = Process.GetProcesses()
            .Where(p => info.IsXEdit(p.ProcessName.ToLower()));

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
            if (File.Exists(info.XEditLogTxt))
                File.Delete(info.XEditLogTxt);
            if (File.Exists(info.XEditExcLog))
                File.Delete(info.XEditExcLog);
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
            command = $"\"{info.XEditPath}\" -QAC -autoexit -autoload \"{pluginName}\"";
        }
        else if (gameMode != null)
        {
            command = $"\"{info.XEditPath}\" -{gameMode} -QAC -autoexit -autoload \"{pluginName}\"";
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
            return Task.FromResult(runTime.TotalSeconds > info.CleaningTimeout);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private async Task<bool> CheckExceptions()
    {
        if (File.Exists(info.XEditExcLog))
        {
            var content = await File.ReadAllTextAsync(info.XEditExcLog);
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
            info.PluginsProcessed--;
            info.CleanFailedList.Add(pluginName);
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
public class CleaningService(
    PactInfo info,
    IProgressReporter progressReporter,
    XEditService xEditService,
    LoggingService loggingService)
    : IAsyncDisposable
{
/*
    private static readonly Regex PluginsPattern = new(@"(?:.+?)(?:\.(?:esl|esm|esp)+)$", RegexOptions.IgnoreCase);
*/

    public async Task CleanPluginsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // First check if xEdit is already running
            if (xEditService.IsXEditRunning())
            {
                Console.WriteLine("❌ ERROR: xEdit is already running. Please close all instances of xEdit before starting the cleaning process.");
                return;
            }

            using var scope = new ProgressScope(progressReporter);

            var pluginList = await GetPluginListAsync();
            var skipList = await GetSkipListAsync();
            var pluginsToClean = pluginList.Except(skipList).ToList();

            progressReporter.ReportMaxValue(pluginsToClean.Count);
            progressReporter.SetVisible(true);

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
                progressReporter.ReportProgress(cleanedCount);
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
            progressReporter.ReportPlugin(plugin);
            Console.WriteLine($"\nCURRENTLY CLEANING : {plugin}");

            var command = xEditService.CreateXEditCommand(plugin);
            await xEditService.ClearXEditLogs();

            // Start xEdit process with exclusive access
            using var process = await xEditService.StartXEditProcess(command, cancellationToken);

            // Monitor the process until completion
            await xEditService.MonitorXEditProcess(process, plugin, cancellationToken);

            // Wait a moment for logs to be written
            await Task.Delay(1000, cancellationToken);

            // Check cleaning results and update statistics
            await CheckCleaningResultsAsync(plugin);

            info.PluginsProcessed++;
            Console.WriteLine($"Finished cleaning: {plugin}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error cleaning plugin {plugin}: {ex.Message}");
            info.CleanFailedList.Add(plugin);
            await loggingService.LogUpdateAsync($"\n{plugin} -> Cleaning failed: {ex.Message}");
        }
    }

    private async Task<List<string>> GetPluginListAsync()
    {
        var content = await File.ReadAllLinesAsync(info.LoadOrderPath);
        return content.Skip(1) // Skip first line
                     .Where(line => !line.Contains(".ghost") && !string.IsNullOrWhiteSpace(line))
                     .Select(line => line.Replace("*", "").Trim())
                     .ToList();
    }

    private Task<HashSet<string>> GetSkipListAsync()
    {
        var skipList = new HashSet<string>(info.GetVipSkipList());
        skipList.UnionWith(info.LclSkipList);
        return Task.FromResult(skipList);
    }

    private async Task CheckCleaningResultsAsync(string plugin)
    {
        if (!File.Exists(info.XEditLogTxt))
            return;

        var cleanedSomething = false;
        var logLines = await File.ReadAllLinesAsync(info.XEditLogTxt);

        foreach (var line in logLines)
        {
            if (line.Contains("Undeleting:"))
            {
                await loggingService.LogUpdateAsync($"\n{plugin} -> Cleaned UDRs");
                info.CleanResultsUdr.Add(plugin);
                cleanedSomething = true;
            }
            else if (line.Contains("Removing:"))
            {
                await loggingService.LogUpdateAsync($"\n{plugin} -> Cleaned ITMs");
                info.CleanResultsItm.Add(plugin);
                cleanedSomething = true;
            }
            else if (line.Contains("Skipping:"))
            {
                await loggingService.LogUpdateAsync($"\n{plugin} -> Found Deleted Navmeshes");
                info.CleanResultsNvm.Add(plugin);
                cleanedSomething = true;
            }
            else if (line.Contains("Making Partial Form:"))
            {
                await loggingService.LogUpdateAsync($"\n{plugin} -> Created Partial Forms");
                info.CleanResultsPartialForms.Add(plugin);
                cleanedSomething = true;
            }
        }

        if (cleanedSomething)
        {
            info.PluginsCleaned++;
        }
        else
        {
            await loggingService.LogUpdateAsync($"\n{plugin} -> NOTHING TO CLEAN");
            info.LclSkipList.Add(plugin);
            Console.WriteLine("NOTHING TO CLEAN! Adding plugin to PACT Ignore file...");
        }
    }

    private async Task LogStartCleaningAsync(DateTime startTime)
    {
        await Task.Run(() => loggingService.ExpireJournal(info.JournalExpiration));
        await loggingService.LogUpdateAsync($"\nSTARTED CLEANING PROCESS AT : {startTime}");
    }

    private async Task LogCompletionAsync(DateTime startTime)
    {
        var duration = DateTime.Now - startTime;
        var message = $"\n✔️ CLEANING COMPLETE! {info.XEditExecutable} processed all available plugins in {duration.TotalSeconds:F1} seconds." +
                     $"\n   Processed {info.PluginsProcessed} plugins and cleaned {info.PluginsCleaned} of them.";

        await loggingService.LogUpdateAsync(message);
        Console.WriteLine(message);
    }

    private async Task OutputCleaningResultsAsync()
    {
        var results = new[]
        {
            (info.CleanFailedList, "❌ {0} WAS UNABLE TO CLEAN THESE PLUGINS:"),
            (info.CleanResultsUdr, "✔️ The following plugins had Deleted Records and {0} properly disabled them:"),
            (info.CleanResultsItm, "✔️ The following plugins had Identical To Master Records and {0} successfully cleaned them:"),
            (info.CleanResultsNvm, "❌ CAUTION: The following plugins contain Deleted Navmeshes!\n   Such plugins may cause navmesh related problems or crashes."),
            (info.CleanResultsPartialForms, "✔️ The following plugins had ITMs converted to Partial Forms {0}:")
        };

        foreach (var (resultSet, messageTemplate) in results)
        {
            if (resultSet.Count > 0)
            {
                var message = string.Format(messageTemplate, info.XEditExecutable);
                Console.WriteLine($"\n{message}");
                foreach (var plugin in resultSet)
                {
                    Console.WriteLine(plugin);
                    await loggingService.LogUpdateAsync($"\n{plugin}");
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await xEditService.DisposeAsync();
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