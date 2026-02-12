using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AIWatcher;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly IEnumerable<IAIProvider> _providers;
    private IDispatcherTimer? _timer;
    private DateTime _lastUpdated;

    public ObservableCollection<AIInstance> Instances { get; } = [];

    public DateTime LastUpdated
    {
        get => _lastUpdated;
        private set
        {
            if (_lastUpdated == value) return;
            _lastUpdated = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastUpdatedText));
        }
    }

    public string LastUpdatedText => LastUpdated == default
        ? ""
        : $"Last updated: {LastUpdated:HH:mm:ss}";

    public ICommand ActivateCommand { get; }

    public MainViewModel(IEnumerable<IAIProvider> providers)
    {
        _providers = providers;
        ActivateCommand = new Command<AIInstance>(OnActivate);
    }

    private static void OnActivate(AIInstance? instance)
    {
        if (instance == null) return;
#if WINDOWS
        WindowActivator.ActivateSession(instance.Id, instance.Workspace, instance.ProviderName);
#endif
    }

    public void StartPolling(IDispatcher dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();

        // also poll immediately
        _ = PollAsync();
    }

    public void StopPolling()
    {
        _timer?.Stop();
        _timer = null;
    }

    private async Task PollAsync()
    {
        var all = new List<AIInstance>();
        foreach (var provider in _providers)
        {
            try
            {
                var snapshot = await provider.GetInstancesAsync();
                all.AddRange(snapshot);
            }
            catch
            {
                // provider failed this cycle, skip
            }
        }

        SyncCollection(all);
        LastUpdated = DateTime.Now;
    }

    private void SyncCollection(List<AIInstance> latest)
    {
        // deduplicate by ID — a session can appear from multiple discovery paths
        // (e.g. debug-log + VS Code fallback reading the same JSONL)
        var latestById = new Dictionary<string, AIInstance>(latest.Count);
        for (var i = latest.Count - 1; i >= 0; i--)
        {
            // first-added wins (earlier discovery paths have better status info)
            latestById.TryAdd(latest[i].Id, latest[i]);
        }

        // remove instances that are no longer present
        for (var i = Instances.Count - 1; i >= 0; i--)
        {
            if (!latestById.ContainsKey(Instances[i].Id))
            {
                Instances[i].StopFlash();
                Instances.RemoveAt(i);
            }
        }

        // update existing or add new
        foreach (var instance in latestById.Values)
        {
            var existing = FindById(instance.Id);
            if (existing != null)
            {
                // update in-place to avoid flicker
                existing.Status = instance.Status;
                existing.LastActivity = instance.LastActivity;
            }
            else
            {
                Instances.Add(instance);
            }
        }

        // refresh time-dependent text (e.g. "Waiting for Input (3m)")
        foreach (var instance in Instances)
            instance.RefreshTimedProperties();
    }

    private AIInstance? FindById(string id)
    {
        foreach (var instance in Instances)
        {
            if (instance.Id == id)
                return instance;
        }
        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
