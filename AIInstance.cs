using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIWatcher;

public enum AIStatus
{
    Working,
    WaitingForInput,
    WaitingForPermission,
    Active,
    Stopped
}

public class AIInstance : INotifyPropertyChanged
{
    private AIStatus _status;
    private DateTime _lastActivity;

    public required string Id { get; init; }
    public required string ProviderName { get; init; }
    public required string Workspace { get; init; }

    public AIStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
            UpdateFlashLoop();
        }
    }

    public DateTime LastActivity
    {
        get => _lastActivity;
        set
        {
            if (_lastActivity == value) return;
            _lastActivity = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get
        {
            var label = Status switch
            {
                AIStatus.Working => "Working",
                AIStatus.WaitingForInput => "Waiting for Input",
                AIStatus.WaitingForPermission => "Waiting for Permission",
                AIStatus.Active => "Active",
                AIStatus.Stopped => "Stopped",
                _ => "Unknown"
            };

            // show elapsed time for waiting states
            if (Status is AIStatus.WaitingForInput or AIStatus.WaitingForPermission
                && LastActivity != default)
            {
                var elapsed = DateTime.UtcNow - LastActivity;
                if (elapsed.TotalMinutes >= 1)
                    label += $" ({(int)elapsed.TotalMinutes}m)";
                else
                    label += $" ({(int)elapsed.TotalSeconds}s)";
            }

            return label;
        }
    }

    private bool _isFlashing;
    private CancellationTokenSource? _flashCts;

    /// <summary>
    /// Background color for the row — flashes for WaitingForPermission to draw attention.
    /// </summary>
    public Color RowBackground
    {
        get
        {
            var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;

            if (_isFlashing)
                return Color.FromArgb(isDark ? "#4D1A1A" : "#FFE0E0");

            return Color.FromArgb(isDark ? "#2A2A2A" : "#F5F5F5");
        }
    }

    private void UpdateFlashLoop()
    {
        _flashCts?.Cancel();
        _flashCts?.Dispose();
        _flashCts = null;

        if (_status == AIStatus.WaitingForPermission)
        {
            _flashCts = new CancellationTokenSource();
            _ = FlashLoop(_flashCts.Token);
        }
        else
        {
            _isFlashing = false;
            OnPropertyChanged(nameof(RowBackground));
        }
    }

    private async Task FlashLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 3 quick pulses (0.5s each = 1.5s total)
                for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
                {
                    _isFlashing = true;
                    OnPropertyChanged(nameof(RowBackground));
                    await Task.Delay(250, ct);

                    _isFlashing = false;
                    OnPropertyChanged(nameof(RowBackground));
                    await Task.Delay(250, ct);
                }

                // wait 10 seconds before next burst
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Stops the flash loop. Call before removing from the collection.
    /// </summary>
    public void StopFlash()
    {
        _flashCts?.Cancel();
        _flashCts?.Dispose();
        _flashCts = null;
    }

    /// <summary>
    /// Notifies the UI that time-dependent text (like wait duration) may have changed.
    /// Called each poll cycle.
    /// </summary>
    public void RefreshTimedProperties()
    {
        OnPropertyChanged(nameof(StatusText));
    }

    public Brush StatusColor => new SolidColorBrush(Status switch
    {
        AIStatus.Working => Colors.DodgerBlue,
        AIStatus.WaitingForInput => Colors.Orange,
        AIStatus.WaitingForPermission => Colors.OrangeRed,
        AIStatus.Active => Colors.MediumPurple,
        AIStatus.Stopped => Colors.Gray,
        _ => Colors.Gray
    });

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
