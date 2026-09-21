using System.Diagnostics;

namespace HamLoggerEditor.UI;

/// <summary>
/// Progress feedback in a StatusStrip: "Loading log.adi… 42%  [=====     ]  0:03  [Cancel]".
/// One operation at a time. Create operations on the UI thread; the returned
/// <see cref="StatusOperation.Progress"/> marshals reports back to the UI thread by itself.
/// Nothing is shown for operations that finish within <see cref="ShowDelay"/>, to avoid flicker.
/// </summary>
public sealed class StatusProgress : IDisposable
{
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(150);

    private readonly ToolStripStatusLabel _captionLabel = new() { Visible = false };
    private readonly ToolStripProgressBar _progressBar = new() { Visible = false, Width = 180, Minimum = 0, Maximum = 100 };
    private readonly ToolStripStatusLabel _elapsedLabel = new() { Visible = false };
    private readonly ToolStripButton _cancelButton = new("Cancel") { Visible = false, DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly System.Windows.Forms.Timer _clockTimer = new() { Interval = 200 };
    private StatusOperation? _current;

    public StatusProgress(StatusStrip statusStrip)
    {
        statusStrip.Items.AddRange(new ToolStripItem[] { _captionLabel, _progressBar, _elapsedLabel, _cancelButton });
        _cancelButton.Click += (_, _) => _current?.Cancel();
        _clockTimer.Tick += (_, _) => Refresh();
    }

    public bool IsBusy => _current is not null;

    /// <summary>Raised on the UI thread when an operation starts (true) and when it ends (false).</summary>
    public event EventHandler<bool>? BusyChanged;

    /// <param name="caption">For example "Loading log.adi".</param>
    /// <param name="cancellable">Show a Cancel button.</param>
    public StatusOperation Begin(string caption, bool cancellable = true)
    {
        if (_current is not null) throw new InvalidOperationException($"\"{_current.Caption}\" is still running.");
        _current = new StatusOperation(this, caption);
        _cancelButton.Enabled = cancellable;
        _progressBar.Style = ProgressBarStyle.Marquee; // until the first percentage arrives
        _progressBar.Value = 0;
        _clockTimer.Start();
        BusyChanged?.Invoke(this, true);
        return _current;
    }

    internal void OnProgress(StatusOperation operation, int percent)
    {
        if (!ReferenceEquals(operation, _current)) return;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Value = Math.Clamp(percent, 0, 100);
        Refresh();
    }

    internal void OnCancelRequested(StatusOperation operation)
    {
        if (!ReferenceEquals(operation, _current)) return;
        _cancelButton.Enabled = false;
        Refresh();
    }

    internal void End(StatusOperation operation)
    {
        if (!ReferenceEquals(operation, _current)) return;
        _current = null;
        _clockTimer.Stop();
        SetVisible(false);
        BusyChanged?.Invoke(this, false);
    }

    private void Refresh()
    {
        StatusOperation? op = _current;
        if (op is null) return;
        TimeSpan elapsed = op.Elapsed;
        if (elapsed < ShowDelay) return;
        SetVisible(true);
        string percent = _progressBar.Style == ProgressBarStyle.Continuous ? $" {_progressBar.Value}%" : string.Empty;
        _captionLabel.Text = op.IsCancellationRequested ? $"Cancelling {op.Caption}…" : $"{op.Caption}…{percent}";
        _elapsedLabel.Text = FormatElapsed(elapsed);
    }

    private void SetVisible(bool visible)
    {
        if (_captionLabel.Visible == visible) return;
        _captionLabel.Visible = _progressBar.Visible = _elapsedLabel.Visible = _cancelButton.Visible = visible;
    }

    /// <summary>"0.4 s", "12.3 s", "1:05".</summary>
    public static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 60 ? $"{elapsed.TotalSeconds:0.0} s" : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";

    public void Dispose()
    {
        _clockTimer.Dispose();
        _current?.Cancel();
    }
}

/// <summary>A running operation. Dispose it (with <c>using</c>) when the work is finished, failed or cancelled.</summary>
public sealed class StatusOperation : IDisposable
{
    private readonly StatusProgress _owner;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    internal StatusOperation(StatusProgress owner, string caption)
    {
        _owner = owner;
        Caption = caption;
        Progress = new Progress<int>(percent => _owner.OnProgress(this, percent)); // captures the UI thread
    }

    public string Caption { get; }

    /// <summary>Report 0–100 from any thread.</summary>
    public IProgress<int> Progress { get; }

    public CancellationToken Token => _cancellation.Token;

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

    public void Cancel()
    {
        if (_cancellation.IsCancellationRequested) return;
        _cancellation.Cancel();
        _owner.OnCancelRequested(this);
    }

    public void Dispose()
    {
        _stopwatch.Stop();
        _owner.End(this);
        _cancellation.Dispose();
    }
}
