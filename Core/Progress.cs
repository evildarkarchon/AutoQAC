namespace AutoQAC.Core.Progress;

/// <summary>
/// Progress scope that automatically reports completion when disposed
/// </summary>
public sealed class ProgressScope : IDisposable
{
    private readonly IProgressReporter _reporter;
    private bool _disposed;

    public ProgressScope(IProgressReporter reporter)
    {
        _reporter = reporter;
        reporter.Reset();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _reporter.ReportDone();
            _disposed = true;
        }
    }
}

public class ProgressReporter : IProgressReporter
{
    public event EventHandler<int>? ProgressChanged;
    public event EventHandler<int>? MaxValueChanged;
    public event EventHandler<string>? PluginChanged;
    public event EventHandler? Done;
    public event EventHandler? Resetting; // Add this event
    public event EventHandler<bool>? VisibilityChanged;

    public bool IsDone { get; private set; }

    public void ReportMaxValue(int maxValue)
    {
        MaxValueChanged?.Invoke(this, maxValue);
    }

    public void ReportProgress(int progress)
    {
        ProgressChanged?.Invoke(this, progress);
    }

    public void ReportPlugin(string plugin)
    {
        PluginChanged?.Invoke(this, $"Cleaning {plugin} %v/%m - %p%");
    }

    public void ReportDone()
    {
        IsDone = true;
        Done?.Invoke(this, EventArgs.Empty);
    }

    public void SetVisible(bool visible)
    {
        VisibilityChanged?.Invoke(this, visible);
    }

    public void Reset()
    {
        IsDone = false;
        Resetting?.Invoke(this, EventArgs.Empty);
    }
}

public interface IProgressReporter
{
    void ReportProgress(int progress);
    void ReportMaxValue(int maxValue);
    void ReportPlugin(string plugin);
    void ReportDone();
    void SetVisible(bool visible);
    void Reset();
    bool IsDone { get; }
}