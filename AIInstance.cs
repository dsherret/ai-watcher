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
