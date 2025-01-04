using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PACT.Core;

namespace PACT;

public partial class MainWindow
{
    private readonly PactInfo _info;
    private readonly SettingsManager _settings;
    private readonly CleaningService _cleaningService;
    private readonly ProgressReporter _progressReporter;
    private bool _isConfigured;
    private CancellationTokenSource? _cleaningCancellationSource;

    public MainWindow()
    {
        InitializeComponent();

        // Initialize services
        _info = new PactInfo();
        _settings = new SettingsManager();
        _progressReporter = new ProgressReporter();

        var xEditService = new XEditService(_info);
        var loggingService = new LoggingService();
        _cleaningService = new CleaningService(_info, _progressReporter, xEditService, loggingService);

        // Load initial settings
        LoadSettings();

        // Setup progress reporting
        SetupProgressReporting();
    }

    private async void LoadSettings()
    {
        try
        {
            await _settings.InitializeInfoAsync(_info);

            // Update UI with loaded settings on the UI thread
            await Dispatcher.InvokeAsync(() =>
            {
                CleaningTimeoutInput.Text = _info.CleaningTimeout.ToString();
                JournalExpirationInput.Text = _info.JournalExpiration.ToString();
                UpdateConfigurationState();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show($"Error loading settings: {ex.Message}", "Error",
                              MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }
    }

    private void SetupProgressReporting()
    {
        _progressReporter.ProgressChanged += (_, progress) =>
        {
            Dispatcher.Invoke(() =>
            {
                CleaningProgress.Value = progress;
                UpdateStatistics();
            });
        };

        _progressReporter.MaxValueChanged += (_, maxValue) =>
        {
            Dispatcher.Invoke(() => CleaningProgress.Maximum = maxValue);
        };

        _progressReporter.VisibilityChanged += (_, visible) =>
        {
            Dispatcher.Invoke(() => CleaningProgress.Visibility = visible ? Visibility.Visible : Visibility.Collapsed);
        };

        _progressReporter.PluginChanged += (_, pluginName) =>
        {
            Dispatcher.Invoke(() => CleaningProgress.ToolTip = pluginName);
        };
    }

    private void UpdateStatistics()
    {
        ItmCount.Text = _info.CleanResultsItm.Count.ToString();
        UdrCount.Text = _info.CleanResultsUdr.Count.ToString();
        NavmeshCount.Text = _info.CleanResultsNvm.Count.ToString();
        PartialFormsCount.Text = _info.CleanResultsPartialForms.Count.ToString();
    }

    private void UpdateConfigurationState()
    {
        var loadOrderValid = !string.IsNullOrEmpty(_info.LoadOrderTxt) &&
                           File.Exists(_info.LoadOrderPath);
        var xEditValid = !string.IsNullOrEmpty(_info.XEditExecutable) &&
                        File.Exists(_info.XEditPath);

        // Update Load Order button state
        if (loadOrderValid)
        {
            SetLoadOrderButton.Content = "✓ LOAD ORDER FILE SET";
            SetLoadOrderButton.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(144, 238, 144)); // Light green
        }
        else
        {
            SetLoadOrderButton.Content = "❓ LOAD ORDER FILE NOT FOUND";
            SetLoadOrderButton.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(255, 255, 224)); // Light yellow
        }

        // Update XEdit button state
        if (xEditValid)
        {
            SetXEditButton.Content = "✓ XEDIT EXECUTABLE SET";
            SetXEditButton.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(144, 238, 144));
        }
        else
        {
            SetXEditButton.Content = "❓ XEDIT EXECUTABLE NOT FOUND";
            SetXEditButton.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(255, 255, 224));
        }

        _isConfigured = loadOrderValid && xEditValid;
        CleanPluginsButton.IsEnabled = _isConfigured;
    }

    private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
    {
        var regex = new Regex("[^0-9]+");
        e.Handled = regex.IsMatch(e.Text);
    }

    private void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        // Implementation for update checking
        MessageBox.Show("Updates will be implemented in a future version.",
                      "Not Implemented", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnUpdateSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var timeout = int.Parse(CleaningTimeoutInput.Text);
            var expiration = int.Parse(JournalExpirationInput.Text);

            if (timeout < 30)
            {
                MessageBox.Show("Cleaning timeout must be at least 30 seconds.",
                              "Invalid Setting", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (expiration < 1)
            {
                MessageBox.Show("Journal expiration must be at least 1 day.",
                              "Invalid Setting", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await _settings.UpdateSettingAsync("Cleaning Timeout", timeout);
            await _settings.UpdateSettingAsync("Journal Expiration", expiration);

            _info.CleaningTimeout = timeout;
            _info.JournalExpiration = expiration;

            MessageBox.Show("Settings updated successfully!", "Settings Updated",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (FormatException)
        {
            MessageBox.Show("Please enter valid numbers for timeout and expiration.",
                          "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error updating settings: {ex.Message}",
                          "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSetLoadOrderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Load Order Files|loadorder.txt;plugins.txt|Text Files|*.txt",
            Title = "Select Load Order File"
        };

        if (dialog.ShowDialog() == true)
        {
            var filename = Path.GetFileName(dialog.FileName).ToLower();
            if (filename is "loadorder.txt" or "plugins.txt")
            {
                _info.UpdateLoadOrderPath(dialog.FileName);
                UpdateConfigurationState();
            }
            else
            {
                MessageBox.Show("Please select a valid load order file (loadorder.txt or plugins.txt).",
                              "Invalid File", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OnSetXEditClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "xEdit Executables|*.exe",
            Title = "Select xEdit Executable"
        };

        if (dialog.ShowDialog() == true)
        {
            if (_info.IsXEdit(Path.GetFileName(dialog.FileName)))
            {
                _info.UpdateXEditPaths(dialog.FileName);
                UpdateConfigurationState();
            }
            else
            {
                MessageBox.Show("Please select a valid xEdit executable.",
                              "Invalid File", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OnStartCleaningClick(object sender, RoutedEventArgs e)
    {
        // Using a wrapper to call the actual async method without risking unhandled exceptions
        _ = HandleCleaningAsync();
    }

    private async Task HandleCleaningAsync()
    {
        if (!CheckConfiguration()) return;

        if (IsCleaningInProgress())
        {
            CancelCleaning();
            return;
        }

        try
        {
            PrepareCleaningUi();
            ResetCleaningStatistics();

            _cleaningCancellationSource = new CancellationTokenSource();

            await _cleaningService.CleanPluginsAsync(_cleaningCancellationSource.Token);

            if (!_cleaningCancellationSource.Token.IsCancellationRequested)
            {
                MessageBox.Show("Cleaning process completed successfully!",
                    "Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("Cleaning process was cancelled.",
                "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error during cleaning process: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ResetUiAfterCleaning();
        }
    }

    private bool CheckConfiguration()
    {
        if (!_isConfigured)
        {
            MessageBox.Show("Please configure both the load order file and xEdit executable before cleaning.",
                "Configuration Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private bool IsCleaningInProgress() =>
        CleanPluginsButton.Content.ToString() == "STOP CLEANING";

    private void CancelCleaning()
    {
        _cleaningCancellationSource?.Cancel();
    }

    private void PrepareCleaningUi()
    {
        CleanPluginsButton.Content = "STOP CLEANING";
        CleanPluginsButton.Background = new System.Windows.Media.SolidColorBrush(LightRedColor);

        SetButtonsEnabled(false);
    }

    private void ResetUiAfterCleaning()
    {
        CleanPluginsButton.Content = "START CLEANING";
        // ReSharper disable once AssignNullToNotNullAttribute
        CleanPluginsButton.Background = (System.Windows.Media.Brush)Resources["SystemChromeLowColor"];

        SetButtonsEnabled(true);

        _cleaningCancellationSource?.Dispose();
        _cleaningCancellationSource = null;

        _progressReporter.Reset();
        CleaningProgress.Value = 0;
        CleaningProgress.Visibility = Visibility.Collapsed;
    }

    private void SetButtonsEnabled(bool isEnabled)
    {
        SetLoadOrderButton.IsEnabled = isEnabled;
        SetXEditButton.IsEnabled = isEnabled;
        CleaningTimeoutInput.IsEnabled = isEnabled;
        JournalExpirationInput.IsEnabled = isEnabled;
        BackupPluginsButton.IsEnabled = false; // Keep disabled until implemented
        RestoreBackupButton.IsEnabled = false; // Keep disabled until implemented
    }

    private void ResetCleaningStatistics()
    {
        _info.CleanResultsItm.Clear();
        _info.CleanResultsUdr.Clear();
        _info.CleanResultsNvm.Clear();
        _info.CleanResultsPartialForms.Clear();
        UpdateStatistics();
    }

    private static readonly System.Windows.Media.Color LightRedColor =
        System.Windows.Media.Color.FromRgb(255, 192, 192);

    private void OnBackupPluginsClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Backup functionality will be implemented in a future update.",
                      "Not Implemented", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnRestoreBackupClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Restore functionality will be implemented in a future update.",
                      "Not Implemented", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "If you have trouble running this program or wish to submit your PACT logs for additional help, " +
            "join the Collective Modding Discord server.\n\n" +
            "Please READ the #👋-welcome2 channel, react with the '2' emoji on the bot message there " +
            "and leave your feedback in #💡-poet-guides-mods channel.\n\n" +
            "Would you like to open the Discord server link?",
            "Need Help?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        );

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                var discordUrl = "https://discord.com/invite/7ZZbrsGQh4";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = discordUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening link: {ex.Message}",
                              "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _cleaningCancellationSource?.Cancel();
        base.OnClosing(e);
    }
}